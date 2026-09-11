#include "weva/border_image.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <string>

namespace weva {

namespace {

bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f'; }

std::string_view trim(std::string_view s) {
    while (!s.empty() && is_space(s.front())) s.remove_prefix(1);
    while (!s.empty() && is_space(s.back())) s.remove_suffix(1);
    return s;
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

std::vector<std::string_view> split_ws(std::string_view s) {
    std::vector<std::string_view> out;
    size_t at = 0;
    while (at < s.size()) {
        while (at < s.size() && is_space(s[at])) ++at;
        const size_t start = at;
        while (at < s.size() && !is_space(s[at])) ++at;
        if (at > start) out.push_back(s.substr(start, at - start));
    }
    return out;
}

bool parse_number(std::string_view s, double* out) {
    if (s.empty()) return false;
    const std::string tmp(s);
    char* end = nullptr;
    const double v = std::strtod(tmp.c_str(), &end);
    if (end == tmp.c_str() || *end != '\0') return false;
    *out = v;
    return true;
}

// The 1-to-4 value shorthand every box property uses: top, right, bottom,
// left, with the usual mirroring.
template <typename T>
void expand_sides(const std::vector<T>& v, T* top, T* right, T* bottom, T* left) {
    if (v.empty()) return;
    *top = v[0];
    *right = v.size() > 1 ? v[1] : v[0];
    *bottom = v.size() > 2 ? v[2] : *top;
    *left = v.size() > 3 ? v[3] : *right;
}

BorderImageRepeat parse_repeat(std::string_view raw) {
    if (iequals(raw, "repeat")) return BorderImageRepeat::Repeat;
    if (iequals(raw, "round")) return BorderImageRepeat::Round;
    if (iequals(raw, "space")) return BorderImageRepeat::Space;
    return BorderImageRepeat::Stretch;
}

// One texel of the source, nearest, clamped. Matches how the rest of the
// engine samples a texture; see the note in background.cpp's sample_image.
void sample(const DecodedImage& image, double sx, double sy, uint8_t* out) {
    const int ix = std::clamp(static_cast<int>(sx), 0, image.width - 1);
    const int iy = std::clamp(static_cast<int>(sy), 0, image.height - 1);
    const uint8_t* p = image.rgba.data() + (static_cast<size_t>(iy) * image.width + ix) * 4;
    out[0] = p[0];
    out[1] = p[1];
    out[2] = p[2];
    out[3] = p[3];
}

// Maps a position along a destination run onto the source slice, according to
// how the edge repeats.
//
// `stretch` is the identity scaled; the other three tile. `round` differs from
// `repeat` only in choosing a tile size that divides the run exactly, which is
// what stops a frame's edge ending mid-motif -- the reason the value exists.
double source_along(double d, double dest_len, double src_len, BorderImageRepeat repeat) {
    if (dest_len <= 0 || src_len <= 0) return 0;
    switch (repeat) {
        case BorderImageRepeat::Stretch:
            return d / dest_len * src_len;
        case BorderImageRepeat::Round: {
            const double count = std::max(1.0, std::round(dest_len / src_len));
            const double tile = dest_len / count;
            return std::fmod(d, tile) / tile * src_len;
        }
        case BorderImageRepeat::Space: {
            const double count = std::floor(dest_len / src_len);
            if (count < 1) return d / dest_len * src_len;
            const double gap = (dest_len - count * src_len) / (count + 1);
            const double period = src_len + gap;
            const double within = std::fmod(d - gap, period);
            // Inside a gap: nothing is drawn there, which the caller sees as a
            // negative coordinate.
            if (within < 0 || within >= src_len) return -1;
            return within;
        }
        case BorderImageRepeat::Repeat:
        default: {
            // Centred, so a run shows the middle of the tile pattern rather
            // than starting flush at one end.
            const double offset = (dest_len - std::floor(dest_len / src_len) * src_len) * 0.5;
            double within = std::fmod(d - offset, src_len);
            if (within < 0) within += src_len;
            return within;
        }
    }
}

} // namespace

bool resolve_border_image(const ComputedStyle* style, const DecodedImage* image,
                          double border_top, double border_right, double border_bottom,
                          double border_left, double box_width, double box_height,
                          const LayoutContext& ctx, double font_size, BorderImage* out) {
    if (!style || !image || !image->valid() || !out) return false;
    BorderImage bi;
    bi.image = image;

    // ---- border-image-slice, in SOURCE pixels ---------------------------
    //
    // A number is source pixels already; a percentage is of the source's own
    // width or height, which is why the two axes resolve separately.
    {
        std::string_view raw = trim(style->get("border-image-slice"));
        if (raw.empty()) raw = "100%";
        std::vector<double> h, v;
        std::vector<std::string_view> toks = split_ws(raw);
        for (std::string_view t : toks) {
            if (iequals(t, "fill")) {
                bi.slice.fill = true;
                continue;
            }
            double n = 0;
            const bool percent = !t.empty() && t.back() == '%';
            if (!parse_number(percent ? t.substr(0, t.size() - 1) : t, &n)) continue;
            // Both lists are filled with the same token stream; each side then
            // takes its own axis's resolution below.
            h.push_back(percent ? n * 0.01 * image->width : n);
            v.push_back(percent ? n * 0.01 * image->height : n);
        }
        if (h.empty()) {
            bi.slice.top = bi.slice.bottom = image->height;
            bi.slice.left = bi.slice.right = image->width;
        } else {
            double t = 0, r = 0, b = 0, l = 0;
            expand_sides(v, &t, &r, &b, &l);   // top and bottom are vertical
            bi.slice.top = t;
            bi.slice.bottom = b;
            expand_sides(h, &t, &r, &b, &l);   // right and left are horizontal
            bi.slice.right = r;
            bi.slice.left = l;
        }
    }
    // A slice cannot eat more than the source has; the spec clamps opposing
    // pairs so they never overlap.
    if (bi.slice.top + bi.slice.bottom > image->height) {
        const double scale = image->height / (bi.slice.top + bi.slice.bottom);
        bi.slice.top *= scale;
        bi.slice.bottom *= scale;
    }
    if (bi.slice.left + bi.slice.right > image->width) {
        const double scale = image->width / (bi.slice.left + bi.slice.right);
        bi.slice.left *= scale;
        bi.slice.right *= scale;
    }

    // ---- border-image-width, in DESTINATION pixels ----------------------
    //
    // A bare number is a multiple of the corresponding border width, which is
    // the default (`1`) and the reason `border: 16px solid transparent` is the
    // whole idiom: the border reserves the space, and the image fills it.
    {
        std::string_view raw = trim(style->get("border-image-width"));
        if (raw.empty()) raw = "1";
        const std::vector<std::string_view> toks = split_ws(raw);
        std::vector<std::string_view> sides(4);
        expand_sides(toks, &sides[0], &sides[1], &sides[2], &sides[3]);
        const double border[4] = {border_top, border_right, border_bottom, border_left};
        const double basis[4] = {box_height, box_width, box_height, box_width};
        double used[4] = {0, 0, 0, 0};
        for (int i = 0; i < 4; ++i) {
            const std::string_view t = trim(sides[static_cast<size_t>(i)]);
            double n = 0;
            if (t.empty() || iequals(t, "auto")) {
                used[i] = border[i];
            } else if (!t.empty() && t.back() == '%' &&
                       parse_number(t.substr(0, t.size() - 1), &n)) {
                used[i] = n * 0.01 * basis[i];
            } else if (parse_number(t, &n)) {
                used[i] = n * border[i];   // a bare number multiplies the border
            } else {
                const ResolvedLength r = resolve_length(t, ctx, font_size, basis[i]);
                used[i] = r.kind == LengthKind::Length ? r.pixels : border[i];
            }
        }
        bi.width.top = std::max(0.0, used[0]);
        bi.width.right = std::max(0.0, used[1]);
        bi.width.bottom = std::max(0.0, used[2]);
        bi.width.left = std::max(0.0, used[3]);
    }
    // Opposing widths cannot exceed the box either, or the two corners would
    // draw over each other and the edges would have negative length.
    if (bi.width.left + bi.width.right > box_width && box_width > 0) {
        const double scale = box_width / (bi.width.left + bi.width.right);
        bi.width.left *= scale;
        bi.width.right *= scale;
    }
    if (bi.width.top + bi.width.bottom > box_height && box_height > 0) {
        const double scale = box_height / (bi.width.top + bi.width.bottom);
        bi.width.top *= scale;
        bi.width.bottom *= scale;
    }

    // ---- border-image-repeat --------------------------------------------
    {
        const std::vector<std::string_view> toks = split_ws(trim(style->get("border-image-repeat")));
        if (!toks.empty()) {
            bi.repeat_x = parse_repeat(toks[0]);
            bi.repeat_y = parse_repeat(toks.size() > 1 ? toks[1] : toks[0]);
        }
    }

    *out = bi;
    return true;
}

void rasterize_border_image(const BorderImage& bi, double dest_w, double dest_h, int tex_w,
                            int tex_h, std::vector<uint8_t>* out_rgba) {
    tex_w = std::max(1, tex_w);
    tex_h = std::max(1, tex_h);
    out_rgba->assign(static_cast<size_t>(tex_w) * tex_h * 4, 0);
    if (!bi.valid() || dest_w <= 0 || dest_h <= 0) return;
    const DecodedImage& img = *bi.image;

    // Clamped HERE as well as in resolve_border_image, because this is a
    // public entry point and a caller that builds a BorderImage by hand --
    // a test, a host, a future shorthand -- should not be able to ask for
    // slices that overlap. Opposing pairs that sum past the source would have
    // the two corners reading across each other, which the sampler's own
    // clamp turns into a plausible-looking wrong picture rather than a crash.
    BorderImage c = bi;
    if (c.slice.top + c.slice.bottom > img.height && c.slice.top + c.slice.bottom > 0) {
        const double k = img.height / (c.slice.top + c.slice.bottom);
        c.slice.top *= k;
        c.slice.bottom *= k;
    }
    if (c.slice.left + c.slice.right > img.width && c.slice.left + c.slice.right > 0) {
        const double k = img.width / (c.slice.left + c.slice.right);
        c.slice.left *= k;
        c.slice.right *= k;
    }
    if (c.width.left + c.width.right > dest_w && c.width.left + c.width.right > 0) {
        const double k = dest_w / (c.width.left + c.width.right);
        c.width.left *= k;
        c.width.right *= k;
    }
    if (c.width.top + c.width.bottom > dest_h && c.width.top + c.width.bottom > 0) {
        const double k = dest_h / (c.width.top + c.width.bottom);
        c.width.top *= k;
        c.width.bottom *= k;
    }

    // The three destination bands on each axis, and the three source bands
    // they come from. The middle of each is what stretches or tiles.
    const double dl = c.width.left, dr = c.width.right;
    const double dt = c.width.top, db = c.width.bottom;
    const double sl = c.slice.left, sr = c.slice.right;
    const double st = c.slice.top, sb = c.slice.bottom;
    const double dest_mid_w = dest_w - dl - dr;
    const double dest_mid_h = dest_h - dt - db;
    const double src_mid_w = img.width - sl - sr;
    const double src_mid_h = img.height - st - sb;

    const double px = dest_w / tex_w, py = dest_h / tex_h;
    for (int ty = 0; ty < tex_h; ++ty) {
        const double dy = (ty + 0.5) * py;
        for (int tx = 0; tx < tex_w; ++tx) {
            const double dx = (tx + 0.5) * px;

            // Which band, and where within it. A negative source coordinate
            // means `space` put a gap here and nothing is drawn.
            double sx = 0, sy = 0;
            if (dx < dl) {
                sx = dl > 0 ? dx / dl * sl : 0;
            } else if (dx >= dest_w - dr) {
                sx = dr > 0 ? img.width - sr + (dx - (dest_w - dr)) / dr * sr : img.width - 1;
            } else {
                if (src_mid_w <= 0 || dest_mid_w <= 0) continue;
                const double within = source_along(dx - dl, dest_mid_w, src_mid_w, c.repeat_x);
                if (within < 0) continue;
                sx = sl + within;
            }
            if (dy < dt) {
                sy = dt > 0 ? dy / dt * st : 0;
            } else if (dy >= dest_h - db) {
                sy = db > 0 ? img.height - sb + (dy - (dest_h - db)) / db * sb : img.height - 1;
            } else {
                if (src_mid_h <= 0 || dest_mid_h <= 0) continue;
                const double within = source_along(dy - dt, dest_mid_h, src_mid_h, c.repeat_y);
                if (within < 0) continue;
                sy = st + within;
            }

            // The centre piece is only painted when `fill` asked for it.
            const bool in_middle_x = dx >= dl && dx < dest_w - dr;
            const bool in_middle_y = dy >= dt && dy < dest_h - db;
            if (in_middle_x && in_middle_y && !c.slice.fill) continue;

            sample(img, sx, sy, out_rgba->data() + (static_cast<size_t>(ty) * tex_w + tx) * 4);
        }
    }
}

} // namespace weva
