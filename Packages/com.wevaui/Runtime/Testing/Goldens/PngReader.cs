using System;
using System.IO;
using System.IO.Compression;

namespace Weva.Testing.Goldens {
    public sealed class PngImage {
        public byte[] Rgba { get; }
        public int Width { get; }
        public int Height { get; }

        public PngImage(byte[] rgba, int width, int height) {
            Rgba = rgba;
            Width = width;
            Height = height;
        }
    }

    public static class PngReader {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static PngImage Decode(byte[] png) {
            if (png == null) throw new ArgumentNullException(nameof(png));
            if (png.Length < Signature.Length) throw new InvalidDataException("PNG too short");
            for (int i = 0; i < Signature.Length; i++) {
                if (png[i] != Signature[i]) throw new InvalidDataException("Not a PNG (signature mismatch)");
            }

            int pos = Signature.Length;
            int width = 0, height = 0;
            byte bitDepth = 0, colorType = 0, interlace = 0;
            using var idat = new MemoryStream();
            bool sawIend = false;

            while (pos < png.Length && !sawIend) {
                if (pos + 8 > png.Length) throw new InvalidDataException("Truncated chunk header");
                int len = (int)ReadUInt32BE(png, pos); pos += 4;
                string type = ReadAscii4(png, pos); pos += 4;
                if (pos + len + 4 > png.Length) throw new InvalidDataException("Truncated chunk data");
                switch (type) {
                    case "IHDR":
                        if (len < 13) throw new InvalidDataException("IHDR too short");
                        width = (int)ReadUInt32BE(png, pos);
                        height = (int)ReadUInt32BE(png, pos + 4);
                        bitDepth = png[pos + 8];
                        colorType = png[pos + 9];
                        interlace = png[pos + 12];
                        if (bitDepth != 8) throw new NotSupportedException("Only 8-bit-depth PNG supported");
                        if (colorType != 6) throw new NotSupportedException("Only RGBA color type supported");
                        if (interlace != 0) throw new NotSupportedException("Interlaced PNG not supported");
                        break;
                    case "IDAT":
                        idat.Write(png, pos, len);
                        break;
                    case "IEND":
                        sawIend = true;
                        break;
                }
                pos += len;
                pos += 4; // CRC, skipped for the reader
            }

            if (width <= 0 || height <= 0) throw new InvalidDataException("Missing or invalid IHDR");

            byte[] zlib = idat.ToArray();
            byte[] filtered = ZlibDecompress(zlib);

            int stride = width * 4;
            int expected = height * (1 + stride);
            if (filtered.Length != expected) {
                throw new InvalidDataException($"Decompressed length {filtered.Length} != expected {expected}");
            }

            byte[] rgba = new byte[height * stride];
            byte[] prev = new byte[stride];
            byte[] curr = new byte[stride];
            for (int y = 0; y < height; y++) {
                int rowStart = y * (1 + stride);
                byte filter = filtered[rowStart];
                Buffer.BlockCopy(filtered, rowStart + 1, curr, 0, stride);
                Unfilter(filter, curr, prev, 4, stride);
                Buffer.BlockCopy(curr, 0, rgba, y * stride, stride);
                var swap = prev; prev = curr; curr = swap;
            }
            return new PngImage(rgba, width, height);
        }

        static void Unfilter(byte filter, byte[] curr, byte[] prev, int bpp, int stride) {
            switch (filter) {
                case 0:
                    break;
                case 1: // Sub
                    for (int x = bpp; x < stride; x++) curr[x] = (byte)(curr[x] + curr[x - bpp]);
                    break;
                case 2: // Up
                    for (int x = 0; x < stride; x++) curr[x] = (byte)(curr[x] + prev[x]);
                    break;
                case 3: // Average
                    for (int x = 0; x < stride; x++) {
                        int left = x >= bpp ? curr[x - bpp] : 0;
                        curr[x] = (byte)(curr[x] + ((left + prev[x]) >> 1));
                    }
                    break;
                case 4: // Paeth
                    for (int x = 0; x < stride; x++) {
                        int left = x >= bpp ? curr[x - bpp] : 0;
                        int up = prev[x];
                        int upLeft = x >= bpp ? prev[x - bpp] : 0;
                        curr[x] = (byte)(curr[x] + Paeth(left, up, upLeft));
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown PNG filter {filter}");
            }
        }

        static int Paeth(int a, int b, int c) {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        static byte[] ZlibDecompress(byte[] zlib) {
            // Strip 2-byte zlib header and trailing 4-byte Adler32; the middle is raw deflate.
            if (zlib.Length < 6) throw new InvalidDataException("zlib payload too short");
            using var src = new MemoryStream(zlib, 2, zlib.Length - 6);
            using var dst = new MemoryStream();
            using (var inflate = new DeflateStream(src, CompressionMode.Decompress)) {
                inflate.CopyTo(dst);
            }
            return dst.ToArray();
        }

        static uint ReadUInt32BE(byte[] buf, int offset) {
            return ((uint)buf[offset] << 24)
                 | ((uint)buf[offset + 1] << 16)
                 | ((uint)buf[offset + 2] << 8)
                 | buf[offset + 3];
        }

        static string ReadAscii4(byte[] buf, int offset) {
            return new string(new[] { (char)buf[offset], (char)buf[offset + 1], (char)buf[offset + 2], (char)buf[offset + 3] });
        }
    }
}
