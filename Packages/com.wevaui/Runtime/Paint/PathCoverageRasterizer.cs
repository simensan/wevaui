using System;
using System.Collections.Generic;
using Weva.Paint.Images;

namespace Weva.Paint {
    // B16 — CPU coverage rasterizer for clip-path: path() GPU clipping.
    //
    // Converts a PathClipPathShape into a byte[] RGBA image (white RGB, alpha=coverage)
    // that can be consumed by either the software rasterizer (via IRawPixelImageSource)
    // or the GPU image-mask path (kind=5 mask layer bound to _WevaMaskImage).
    //
    // Algorithm: 4x4 supersampling (16 samples per pixel). Each sub-sample is tested
    // via PathClipPathShape.Contains(), which already honours the fill rule (nonzero/
    // evenodd). Coverage = count_inside / 16, stored in the alpha channel. RGB = 255
    // (white) so luminance-mode masks degenerate cleanly to alpha.
    //
    // Caching: PathCoverageCache (keyed by shape identity + integer pixel size) must
    // be maintained by the caller. This class is stateless and each call allocates a
    // fresh byte[] — cache externally to amortize cost.
    //
    // Coordinate convention: the shape's world-space coordinates already include the
    // border-box origin. The rasterizer samples from (bounds.X, bounds.Y) to
    // (bounds.Right, bounds.Bottom) at integer pixel resolution. Row 0 = top.
    public static class PathCoverageRasterizer {
        // 4x4 Halton sub-pixel offsets (base 2 / base 3) for even coverage distribution.
        // Using a regular 4x4 grid would alias with axis-aligned edges; Halton spreads
        // the samples more uniformly across the pixel, which removes the staircase
        // artifact on 45-degree diagonal edges (visible at 1-2px widths).
        // Offsets are in [0,1) relative to the pixel's bottom-left corner.
        static readonly (double dx, double dy)[] Samples = {
            (0.0625, 0.0625), (0.3125, 0.1875), (0.5625, 0.3125), (0.8125, 0.4375),
            (0.0625, 0.5625), (0.3125, 0.6875), (0.5625, 0.8125), (0.8125, 0.9375),
            (0.1875, 0.0625), (0.4375, 0.1875), (0.6875, 0.3125), (0.9375, 0.4375),
            (0.1875, 0.5625), (0.4375, 0.6875), (0.6875, 0.8125), (0.9375, 0.9375),
        };

        // Rasterize the shape to a byte[] image at the given pixel dimensions.
        // The image covers exactly (bounds.X, bounds.Y)-(bounds.Right, bounds.Bottom).
        // If the shape is null or has zero area, returns null.
        public static byte[] Rasterize(PathClipPathShape shape, int pixelW, int pixelH) {
            if (shape == null || pixelW <= 0 || pixelH <= 0) return null;

            var bounds = shape.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var pixels = new byte[pixelW * pixelH * 4];

            double ox = bounds.X;
            double oy = bounds.Y;
            double sx = bounds.Width  / pixelW;
            double sy = bounds.Height / pixelH;

            const int N = 16; // sample count (4x4 grid above)
            for (int row = 0; row < pixelH; row++) {
                double worldYBase = oy + row * sy;
                for (int col = 0; col < pixelW; col++) {
                    double worldXBase = ox + col * sx;
                    int inside = 0;
                    for (int s = 0; s < N; s++) {
                        double wx = worldXBase + Samples[s].dx * sx;
                        double wy = worldYBase + Samples[s].dy * sy;
                        if (shape.Contains(wx, wy)) inside++;
                    }
                    int i = (row * pixelW + col) * 4;
                    byte coverage = (byte)((inside * 255 + N / 2) / N);
                    pixels[i + 0] = 255; // R = white
                    pixels[i + 1] = 255; // G = white
                    pixels[i + 2] = 255; // B = white
                    pixels[i + 3] = coverage; // A = coverage
                }
            }

            return pixels;
        }

        // Compute the integer pixel dimensions for a path coverage image given the
        // shape's actual world-space bounds. Width/height are clamped to [1, maxSize].
        public static (int w, int h) ComputePixelSize(PathClipPathShape shape, int maxSize = 1024) {
            if (shape == null) return (1, 1);
            var b = shape.Bounds;
            int w = Math.Max(1, Math.Min(maxSize, (int)Math.Ceiling(b.Width)));
            int h = Math.Max(1, Math.Min(maxSize, (int)Math.Ceiling(b.Height)));
            return (w, h);
        }

        // IRawPixelImageSource wrapper around a rasterized coverage buffer.
        // White RGB, alpha = coverage.
        public sealed class PathCoverageImageSource : IRawPixelImageSource {
            public int Width  { get; }
            public int Height { get; }
            public byte[] Pixels { get; }

            public PathCoverageImageSource(int w, int h, byte[] pixels) {
                Width  = w;
                Height = h;
                Pixels = pixels;
            }
        }

        // Cache entry. Keyed externally by (shapeHashCode, w, h).
        public static PathCoverageImageSource BuildSource(PathClipPathShape shape) {
            var (w, h) = ComputePixelSize(shape);
            var pixels = Rasterize(shape, w, h);
            if (pixels == null) return null;
            return new PathCoverageImageSource(w, h, pixels);
        }
    }

    // B16 — per-converter cache for PathCoverageImageSource instances.
    //
    // Keyed by (hashCode, width, height) where hashCode is computed from the
    // shape's SubPolygons content and fill rule. The key is intentionally
    // conservative: any mutation produces a different key.
    //
    // Eviction: v1 grows unboundedly per unique (shape, size) combination.
    // In practice clip-path shapes are static (resolved once from CSS), so the
    // per-element steady-state is 1 entry. A growing-without-bound situation
    // can only arise from programmatic style mutation that produces a new path
    // per frame — that is O(animationFrames) entries and can be fixed in v2 by
    // adding a generation counter or LRU. Document the story here so v2 has
    // the context.
    //
    // Thread safety: not synchronized. Paint runs on the main thread; same
    // contract as InMemoryImageRegistry.
    public sealed class PathCoverageCache {
        readonly Dictionary<(int hash, int w, int h), PathCoverageRasterizer.PathCoverageImageSource> _map
            = new Dictionary<(int, int, int), PathCoverageRasterizer.PathCoverageImageSource>();

        // Returns a cached PathCoverageImageSource for the shape, rasterizing if needed.
        public PathCoverageRasterizer.PathCoverageImageSource GetOrCreate(PathClipPathShape shape) {
            if (shape == null) return null;
            var (w, h) = PathCoverageRasterizer.ComputePixelSize(shape);
            int hash = ComputeShapeHash(shape);
            var key = (hash, w, h);
            if (_map.TryGetValue(key, out var existing)) return existing;
            var src = PathCoverageRasterizer.BuildSource(shape);
            if (src == null) return null;
            _map[key] = src;
            return src;
        }

        // Returns true if the cache already has an entry for this shape at its current size,
        // and the handle that was registered for it.
        public bool TryGetHandle(PathClipPathShape shape, out string handle) {
            handle = null;
            if (shape == null) return false;
            var (w, h) = PathCoverageRasterizer.ComputePixelSize(shape);
            int hash = ComputeShapeHash(shape);
            var key = (hash, w, h);
            if (!_map.ContainsKey(key)) return false;
            handle = MakeHandle(hash, w, h);
            return true;
        }

        // Produces the synthetic registry handle for a shape at a given pixel size.
        // Format: "__path_clip_<hash>_<w>x<h>" — guaranteed not to collide with
        // author handles (authors write urls like "ui/icon", never with this prefix).
        public static string MakeHandle(int hash, int w, int h) {
            return $"__path_clip_{hash:X8}_{w}x{h}";
        }

        // Registers the coverage image in the given registry and returns its handle.
        // No-op if the image is already registered (same source object).
        public string EnsureRegistered(PathClipPathShape shape, InMemoryImageRegistry registry) {
            if (shape == null || registry == null) return null;
            var (w, h) = PathCoverageRasterizer.ComputePixelSize(shape);
            int hash = ComputeShapeHash(shape);
            string handle = MakeHandle(hash, w, h);
            // GetOrCreate also caches in _map so no re-rasterization occurs.
            var src = GetOrCreate(shape);
            if (src == null) return null;
            // Register always checks ReferenceEquals before bumping Version, so
            // re-registering the same instance is a no-op (cheap).
            registry.Register(handle, src);
            return handle;
        }

        // Stable hash over shape content. Not cryptographic — used only for cache keying.
        // Same fill rule + same polygon points → same hash. Collisions are benign (a cache
        // miss produces a fresh rasterization at worst).
        static int ComputeShapeHash(PathClipPathShape shape) {
            int h = (int)shape.FillRule * unchecked((int)0x9e3779b9);
            foreach (var poly in shape.SubPolygons) {
                h ^= poly.Length * 0x517cc1b7;
                for (int i = 0; i < poly.Length; i++) {
                    // Use double bits directly to avoid precision loss.
                    long bx = BitConverter.DoubleToInt64Bits(poly[i].X);
                    long by = BitConverter.DoubleToInt64Bits(poly[i].Y);
                    h ^= (int)(bx ^ (bx >> 32)) * 0x1f8ef4b5;
                    h ^= (int)(by ^ (by >> 32)) * 0x2f7d1c3a;
                    h = (h << 13) | (h >> 19); // rotate
                }
            }
            return h;
        }
    }
}
