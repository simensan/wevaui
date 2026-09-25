#include "weva/image_decode.h"

#include <cstring>

namespace weva {

namespace {

// ---- bit reader ---------------------------------------------------------
//
// DEFLATE reads its codes least-significant-bit first within a byte, and its
// literal lengths most-significant-byte first. Getting those two the wrong way
// round produces output that starts correct and then diverges, which is the
// worst kind of wrong, so they are separated here rather than open-coded.
class BitReader {
public:
    BitReader(const uint8_t* data, size_t size) : data_(data), size_(size) {}

    bool bit(uint32_t* out) {
        if (bits_ == 0) {
            if (at_ >= size_) return false;
            hold_ = data_[at_++];
            bits_ = 8;
        }
        *out = hold_ & 1u;
        hold_ >>= 1;
        --bits_;
        return true;
    }

    bool bits(int n, uint32_t* out) {
        uint32_t v = 0;
        for (int i = 0; i < n; ++i) {
            uint32_t b = 0;
            if (!bit(&b)) return false;
            v |= b << i;
        }
        *out = v;
        return true;
    }

    // Drops to the next byte boundary; a stored block starts there.
    void align() { bits_ = 0; hold_ = 0; }

    bool take(size_t n, const uint8_t** out) {
        if (at_ + n > size_) return false;
        *out = data_ + at_;
        at_ += n;
        return true;
    }

    size_t position() const { return at_; }

private:
    const uint8_t* data_;
    size_t size_;
    size_t at_ = 0;
    uint32_t hold_ = 0;
    int bits_ = 0;
};

// ---- canonical Huffman --------------------------------------------------
//
// Decoded by walking the code lengths, which is slower than a lookup table and
// enormously easier to be sure of. An image decodes once and is cached; the
// per-frame cost of this is zero.
struct Huffman {
    // counts[n] is how many codes have length n; symbols is every symbol in
    // canonical order.
    int counts[16] = {0};
    std::vector<int> symbols;

    bool build(const std::vector<int>& lengths) {
        for (int& c : counts) c = 0;
        for (int len : lengths) {
            if (len < 0 || len > 15) return false;
            ++counts[len];
        }
        // A length of zero means "this symbol is not used", not a code.
        counts[0] = 0;
        int left = 1;
        for (int len = 1; len < 16; ++len) {
            left <<= 1;
            left -= counts[len];
            if (left < 0) return false;   // over-subscribed
        }
        std::vector<int> offsets(16, 0);
        for (int len = 1; len < 15; ++len) offsets[len + 1] = offsets[len] + counts[len];
        symbols.assign(lengths.size(), 0);
        for (size_t sym = 0; sym < lengths.size(); ++sym) {
            if (lengths[sym] != 0) symbols[offsets[lengths[sym]]++] = static_cast<int>(sym);
        }
        return true;
    }

    bool decode(BitReader* in, int* out) const {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len < 16; ++len) {
            uint32_t b = 0;
            if (!in->bit(&b)) return false;
            code |= static_cast<int>(b);
            const int count = counts[len];
            if (code - first < count) {
                *out = symbols[static_cast<size_t>(index + (code - first))];
                return true;
            }
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        return false;
    }
};

// The length and distance tables of RFC 1951 section 3.2.5, verbatim.
constexpr int kLengthBase[29] = {3,  4,  5,  6,  7,  8,  9,  10, 11,  13,  15,  17,  19,  23, 27,
                                 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
constexpr int kLengthExtra[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2,
                                  2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
constexpr int kDistBase[30] = {1,    2,    3,    4,    5,    7,     9,     13,    17,   25,
                               33,   49,   65,   97,   129,  193,   257,   385,   513,  769,
                               1025, 1537, 2049, 3073, 4097, 6145,  8193,  12289, 16385, 24577};
constexpr int kDistExtra[30] = {0, 0, 0, 0, 1, 1, 2, 2,  3,  3,  4,  4,  5,  5,  6,
                                6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};

bool inflate_block(BitReader* in, const Huffman& lit, const Huffman& dist,
                   std::vector<uint8_t>* out) {
    for (;;) {
        int sym = 0;
        if (!lit.decode(in, &sym)) return false;
        if (sym < 256) {
            out->push_back(static_cast<uint8_t>(sym));
            continue;
        }
        if (sym == 256) return true;   // end of block
        sym -= 257;
        if (sym >= 29) return false;
        uint32_t extra = 0;
        if (!in->bits(kLengthExtra[sym], &extra)) return false;
        const size_t length = static_cast<size_t>(kLengthBase[sym]) + extra;

        int dsym = 0;
        if (!dist.decode(in, &dsym)) return false;
        if (dsym >= 30) return false;
        if (!in->bits(kDistExtra[dsym], &extra)) return false;
        const size_t distance = static_cast<size_t>(kDistBase[dsym]) + extra;
        if (distance > out->size()) return false;

        // Byte at a time, deliberately: the run may overlap its own output,
        // which is how DEFLATE expresses a repeat, and a memcpy would read
        // bytes that have not been written yet.
        const size_t from = out->size() - distance;
        for (size_t i = 0; i < length; ++i) out->push_back((*out)[from + i]);
    }
}

bool fixed_tables(Huffman* lit, Huffman* dist) {
    std::vector<int> lengths(288);
    for (int i = 0; i < 144; ++i) lengths[static_cast<size_t>(i)] = 8;
    for (int i = 144; i < 256; ++i) lengths[static_cast<size_t>(i)] = 9;
    for (int i = 256; i < 280; ++i) lengths[static_cast<size_t>(i)] = 7;
    for (int i = 280; i < 288; ++i) lengths[static_cast<size_t>(i)] = 8;
    if (!lit->build(lengths)) return false;
    return dist->build(std::vector<int>(30, 5));
}

bool dynamic_tables(BitReader* in, Huffman* lit, Huffman* dist) {
    uint32_t hlit = 0, hdist = 0, hclen = 0;
    if (!in->bits(5, &hlit) || !in->bits(5, &hdist) || !in->bits(4, &hclen)) return false;
    const size_t n_lit = hlit + 257, n_dist = hdist + 1, n_clen = hclen + 4;

    // The code-length alphabet's own lengths arrive in this permuted order.
    static constexpr int kOrder[19] = {16, 17, 18, 0, 8,  7, 9,  6, 10, 5,
                                       11, 4,  12, 3, 13, 2, 14, 1, 15};
    std::vector<int> clen(19, 0);
    for (size_t i = 0; i < n_clen; ++i) {
        uint32_t v = 0;
        if (!in->bits(3, &v)) return false;
        clen[static_cast<size_t>(kOrder[i])] = static_cast<int>(v);
    }
    Huffman code_lengths;
    if (!code_lengths.build(clen)) return false;

    std::vector<int> lengths;
    lengths.reserve(n_lit + n_dist);
    while (lengths.size() < n_lit + n_dist) {
        int sym = 0;
        if (!code_lengths.decode(in, &sym)) return false;
        if (sym < 16) {
            lengths.push_back(sym);
            continue;
        }
        int repeat = 0, value = 0;
        uint32_t extra = 0;
        if (sym == 16) {
            if (lengths.empty()) return false;
            value = lengths.back();
            if (!in->bits(2, &extra)) return false;
            repeat = 3 + static_cast<int>(extra);
        } else if (sym == 17) {
            if (!in->bits(3, &extra)) return false;
            repeat = 3 + static_cast<int>(extra);
        } else if (sym == 18) {
            if (!in->bits(7, &extra)) return false;
            repeat = 11 + static_cast<int>(extra);
        } else {
            return false;
        }
        if (lengths.size() + static_cast<size_t>(repeat) > n_lit + n_dist) return false;
        for (int i = 0; i < repeat; ++i) lengths.push_back(value);
    }
    if (!lit->build(std::vector<int>(lengths.begin(), lengths.begin() + static_cast<long>(n_lit)))) {
        return false;
    }
    return dist->build(std::vector<int>(lengths.begin() + static_cast<long>(n_lit), lengths.end()));
}

} // namespace

bool inflate_deflate(const uint8_t* data, size_t size, std::vector<uint8_t>* out) {
    if (!data || !out) return false;
    BitReader in(data, size);
    for (;;) {
        uint32_t final_block = 0, type = 0;
        if (!in.bit(&final_block) || !in.bits(2, &type)) return false;
        if (type == 0) {
            in.align();
            const uint8_t* header = nullptr;
            if (!in.take(4, &header)) return false;
            const size_t len = static_cast<size_t>(header[0]) | (static_cast<size_t>(header[1]) << 8);
            const size_t nlen =
                static_cast<size_t>(header[2]) | (static_cast<size_t>(header[3]) << 8);
            if ((len ^ 0xFFFFu) != nlen) return false;
            const uint8_t* body = nullptr;
            if (!in.take(len, &body)) return false;
            out->insert(out->end(), body, body + len);
        } else if (type == 1 || type == 2) {
            Huffman lit, dist;
            if (type == 1 ? !fixed_tables(&lit, &dist) : !dynamic_tables(&in, &lit, &dist)) {
                return false;
            }
            if (!inflate_block(&in, lit, dist, out)) return false;
        } else {
            return false;   // reserved
        }
        if (final_block) return true;
    }
}

bool inflate_zlib(const uint8_t* data, size_t size, std::vector<uint8_t>* out) {
    if (!data || size < 2) return false;
    const uint8_t cmf = data[0], flg = data[1];
    if ((cmf & 0x0F) != 8) return false;             // not DEFLATE
    if (((cmf << 8) | flg) % 31 != 0) return false;  // header check
    if (flg & 0x20) return false;                    // a preset dictionary, which PNG never uses
    return inflate_deflate(data + 2, size - 2, out);
}

namespace {

uint32_t be32(const uint8_t* p) {
    return (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) |
           (static_cast<uint32_t>(p[2]) << 8) | static_cast<uint32_t>(p[3]);
}

// PNG's five filters, reconstructing in place against the previous row.
// Straight from the spec's pseudo-code; `bpp` is the byte distance to the
// pixel on the left, which is where every one of these gets its `a`.
void unfilter_row(uint8_t type, uint8_t* row, const uint8_t* prev, size_t stride, size_t bpp) {
    const auto left = [&](size_t i) -> int { return i >= bpp ? row[i - bpp] : 0; };
    const auto up = [&](size_t i) -> int { return prev ? prev[i] : 0; };
    const auto upleft = [&](size_t i) -> int { return (prev && i >= bpp) ? prev[i - bpp] : 0; };
    switch (type) {
        case 0: break;
        case 1:
            for (size_t i = 0; i < stride; ++i) row[i] = static_cast<uint8_t>(row[i] + left(i));
            break;
        case 2:
            for (size_t i = 0; i < stride; ++i) row[i] = static_cast<uint8_t>(row[i] + up(i));
            break;
        case 3:
            for (size_t i = 0; i < stride; ++i) {
                row[i] = static_cast<uint8_t>(row[i] + ((left(i) + up(i)) >> 1));
            }
            break;
        case 4:
            for (size_t i = 0; i < stride; ++i) {
                const int a = left(i), b = up(i), c = upleft(i);
                const int p = a + b - c;
                const int pa = p > a ? p - a : a - p;
                const int pb = p > b ? p - b : b - p;
                const int pc = p > c ? p - c : c - p;
                const int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                row[i] = static_cast<uint8_t>(row[i] + pred);
            }
            break;
        default: break;
    }
}

} // namespace

DecodedImage decode_png(const uint8_t* data, size_t size) {
    DecodedImage image;
    static constexpr uint8_t kSignature[8] = {0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n'};
    if (!data || size < 8 || std::memcmp(data, kSignature, 8) != 0) return image;

    uint32_t width = 0, height = 0;
    int depth = 0, colour = 0, interlace = 0;
    std::vector<uint8_t> compressed;
    std::vector<uint8_t> palette;      // RGB triples
    std::vector<uint8_t> palette_alpha;
    bool saw_header = false;

    size_t at = 8;
    while (at + 8 <= size) {
        const uint32_t length = be32(data + at);
        const uint8_t* kind = data + at + 4;
        if (length > size || at + 12 + length > size) return DecodedImage{};
        const uint8_t* body = data + at + 8;
        at += 12 + length;   // length, type, body, CRC

        if (std::memcmp(kind, "IHDR", 4) == 0) {
            if (length < 13) return DecodedImage{};
            width = be32(body);
            height = be32(body + 4);
            depth = body[8];
            colour = body[9];
            interlace = body[12];
            saw_header = true;
        } else if (std::memcmp(kind, "PLTE", 4) == 0) {
            palette.assign(body, body + length);
        } else if (std::memcmp(kind, "tRNS", 4) == 0) {
            palette_alpha.assign(body, body + length);
        } else if (std::memcmp(kind, "IDAT", 4) == 0) {
            compressed.insert(compressed.end(), body, body + length);
        } else if (std::memcmp(kind, "IEND", 4) == 0) {
            break;
        }
    }

    // The subset UI art is stored in. Anything else is refused rather than
    // guessed at, and the caller degrades to the flat fill it already drew.
    if (!saw_header || width == 0 || height == 0 || depth != 8 || interlace != 0) return image;
    if (colour != 0 && colour != 2 && colour != 3 && colour != 4 && colour != 6) return image;
    if (colour == 3 && palette.empty()) return image;
    // A cap, so a corrupt header cannot ask for an enormous allocation.
    if (static_cast<uint64_t>(width) * height > 64ull * 1024 * 1024) return image;

    const size_t channels = colour == 0 ? 1 : colour == 2 ? 3 : colour == 3 ? 1 : colour == 4 ? 2 : 4;
    std::vector<uint8_t> raw;
    raw.reserve(static_cast<size_t>(height) * (1 + static_cast<size_t>(width) * channels));
    if (!inflate_zlib(compressed.data(), compressed.size(), &raw)) return image;

    const size_t stride = static_cast<size_t>(width) * channels;
    if (raw.size() < static_cast<size_t>(height) * (stride + 1)) return image;

    std::vector<uint8_t> rgba(static_cast<size_t>(width) * height * 4, 0);
    std::vector<uint8_t> previous(stride, 0);
    std::vector<uint8_t> row(stride, 0);
    size_t read = 0;
    for (uint32_t y = 0; y < height; ++y) {
        const uint8_t filter = raw[read++];
        std::memcpy(row.data(), raw.data() + read, stride);
        read += stride;
        unfilter_row(filter, row.data(), y == 0 ? nullptr : previous.data(), stride, channels);

        for (uint32_t x = 0; x < width; ++x) {
            const uint8_t* p = row.data() + static_cast<size_t>(x) * channels;
            uint8_t* out = rgba.data() + (static_cast<size_t>(y) * width + x) * 4;
            switch (colour) {
                case 0:   // greyscale
                    out[0] = out[1] = out[2] = p[0];
                    out[3] = 255;
                    break;
                case 2:   // truecolour
                    out[0] = p[0]; out[1] = p[1]; out[2] = p[2]; out[3] = 255;
                    break;
                case 3: {   // indexed
                    const size_t i = static_cast<size_t>(p[0]);
                    if (i * 3 + 2 >= palette.size()) return DecodedImage{};
                    out[0] = palette[i * 3 + 0];
                    out[1] = palette[i * 3 + 1];
                    out[2] = palette[i * 3 + 2];
                    out[3] = i < palette_alpha.size() ? palette_alpha[i] : 255;
                    break;
                }
                case 4:   // greyscale + alpha
                    out[0] = out[1] = out[2] = p[0];
                    out[3] = p[1];
                    break;
                default:  // truecolour + alpha
                    out[0] = p[0]; out[1] = p[1]; out[2] = p[2]; out[3] = p[3];
                    break;
            }
        }
        previous.swap(row);
    }

    image.width = static_cast<int>(width);
    image.height = static_cast<int>(height);
    image.rgba = std::move(rgba);
    return image;
}

DecodedImage decode_image(const uint8_t* data, size_t size) { return decode_png(data, size); }

} // namespace weva
