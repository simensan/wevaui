using System;
using System.IO;
using System.IO.Compression;

namespace Weva.Testing.Goldens {
    public static class PngWriter {
        // PNG signature: \x89 P N G \r \n \x1A \n
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static byte[] Encode(byte[] rgba, int width, int height) {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            int expected = width * height * 4;
            if (rgba.Length != expected) {
                throw new ArgumentException($"rgba length {rgba.Length} does not match width*height*4 ({expected})", nameof(rgba));
            }

            using var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);

            // IHDR: width(4) + height(4) + bitDepth(1) + colorType(1) + compression(1) + filter(1) + interlace(1)
            byte[] ihdr = new byte[13];
            WriteUInt32BE(ihdr, 0, (uint)width);
            WriteUInt32BE(ihdr, 4, (uint)height);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // color type RGBA
            ihdr[10] = 0;  // compression: deflate
            ihdr[11] = 0;  // filter: standard
            ihdr[12] = 0;  // interlace: none
            WriteChunk(ms, "IHDR", ihdr, 0, ihdr.Length);

            // Filtered scanlines: each row prefixed by filter byte (0 = None) then row pixels.
            byte[] filtered = new byte[height * (1 + width * 4)];
            int stride = width * 4;
            for (int y = 0; y < height; y++) {
                int dst = y * (1 + stride);
                filtered[dst] = 0;
                Buffer.BlockCopy(rgba, y * stride, filtered, dst + 1, stride);
            }

            byte[] zlibCompressed = ZlibCompress(filtered);
            WriteChunk(ms, "IDAT", zlibCompressed, 0, zlibCompressed.Length);

            WriteChunk(ms, "IEND", Array.Empty<byte>(), 0, 0);
            return ms.ToArray();
        }

        static void WriteChunk(Stream s, string type, byte[] data, int offset, int length) {
            byte[] lenBytes = new byte[4];
            WriteUInt32BE(lenBytes, 0, (uint)length);
            s.Write(lenBytes, 0, 4);

            byte[] typeBytes = new byte[4] {
                (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]
            };
            s.Write(typeBytes, 0, 4);
            s.Write(data, offset, length);

            // CRC is over chunk type + chunk data.
            uint crc = Crc32.Compute(typeBytes, 0, 4, 0xFFFFFFFFu);
            crc = Crc32.Compute(data, offset, length, crc);
            crc ^= 0xFFFFFFFFu;

            byte[] crcBytes = new byte[4];
            WriteUInt32BE(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        static byte[] ZlibCompress(byte[] data) {
            // zlib container: 2-byte header (0x78 0x9C — deflate, default compression) +
            // raw deflate stream + 4-byte big-endian Adler32 checksum of the uncompressed bytes.
            using var ms = new MemoryStream();
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) {
                deflate.Write(data, 0, data.Length);
            }
            uint adler = Adler32.Compute(data);
            byte[] adlerBytes = new byte[4];
            WriteUInt32BE(adlerBytes, 0, adler);
            ms.Write(adlerBytes, 0, 4);
            return ms.ToArray();
        }

        static void WriteUInt32BE(byte[] buf, int offset, uint v) {
            buf[offset + 0] = (byte)((v >> 24) & 0xFF);
            buf[offset + 1] = (byte)((v >> 16) & 0xFF);
            buf[offset + 2] = (byte)((v >> 8) & 0xFF);
            buf[offset + 3] = (byte)(v & 0xFF);
        }

        static class Crc32 {
            static readonly uint[] Table = BuildTable();

            static uint[] BuildTable() {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) {
                    uint c = i;
                    for (int j = 0; j < 8; j++) {
                        c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                    }
                    t[i] = c;
                }
                return t;
            }

            public static uint Compute(byte[] data, int offset, int length, uint seed) {
                uint c = seed;
                for (int i = 0; i < length; i++) {
                    c = Table[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
                }
                return c;
            }
        }

        static class Adler32 {
            const uint Mod = 65521;

            public static uint Compute(byte[] data) {
                uint a = 1, b = 0;
                for (int i = 0; i < data.Length; i++) {
                    a = (a + data[i]) % Mod;
                    b = (b + a) % Mod;
                }
                return (b << 16) | a;
            }
        }
    }
}
