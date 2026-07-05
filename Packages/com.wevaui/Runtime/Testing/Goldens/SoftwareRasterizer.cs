using System;
using System.Collections.Generic;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Paint.Images;

namespace Weva.Testing.Goldens {
    // CPU-only IRenderBackend that produces an RGBA framebuffer the golden suite
    // can hash and diff. No Unity APIs. Intentionally tuned for deterministic,
    // regression-stable output rather than fidelity:
    //   - text glyphs are flat blocks (one solid square per character via MonoFontMetrics)
    //   - gradients fall back to their first stop's solid color
    //   - image brushes paint magenta with a TODO (background path); mask url() brushes
    //     sample the source pixels via IRawPixelImageSource when a registry is supplied
    //   - rounded corners use a per-pixel SDF for soft edges
    //   - shadows are a separable box blur, repeated 3 times to approximate gaussian
    //   - filters: blur(), brightness(), contrast(), grayscale() implemented; others no-op
    //
    // Same input -> identical bytes. All math goes through doubles, then rounds to byte
    // with a consistent banker-free rule (Math.Round MidpointRounding.AwayFromZero).
    public sealed class SoftwareRasterizer : IRenderBackend {
        readonly int width;
        readonly int height;
        readonly byte[] pixels;
        readonly IFontMetrics fontMetrics;
        // Optional image registry for URL mask pixel sampling (B17). When non-null,
        // url()-sourced mask layers sample the registered image's pixels via the
        // IRawPixelImageSource contract. When null (or the source doesn't implement
        // IRawPixelImageSource), unresolved url() masks render the element fully
        // transparent — matching Chrome's behavior for failed mask-image urls.
        readonly IImageRegistry imageRegistry;

        readonly Stack<ClipRect> clipStack = new Stack<ClipRect>();
        readonly Stack<ClipPathShape> clipPathStack = new Stack<ClipPathShape>();
        readonly Stack<double> opacityStack = new Stack<double>();
        readonly Stack<Transform2D> transformStack = new Stack<Transform2D>();
        readonly Stack<FilterFrame> filterStack = new Stack<FilterFrame>();
        readonly Stack<MaskFrame> maskStack = new Stack<MaskFrame>();

        public SoftwareRasterizer(int width, int height) : this(width, height, new MonoFontMetrics()) { }

        public SoftwareRasterizer(int width, int height, IFontMetrics fontMetrics)
            : this(width, height, fontMetrics, null) { }

        public SoftwareRasterizer(int width, int height, IFontMetrics fontMetrics, IImageRegistry imageRegistry) {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            this.width = width;
            this.height = height;
            this.pixels = new byte[width * height * 4];
            this.fontMetrics = fontMetrics ?? new MonoFontMetrics();
            this.imageRegistry = imageRegistry;
            clipStack.Push(new ClipRect(0, 0, width, height));
            opacityStack.Push(1.0);
            transformStack.Push(Transform2D.Identity);
        }

        public int Width => width;
        public int Height => height;
        public byte[] Pixels => pixels;

        public void Clear(byte r, byte g, byte b, byte a) {
            for (int i = 0; i < pixels.Length; i += 4) {
                pixels[i + 0] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = a;
            }
        }

        // ===== IRenderBackend =====

        public void Submit(FillRectCommand command) {
            if (command == null || command.Brush == null) return;
            if (command.Brush.Kind == BrushKind.Gradient && command.Brush.GradientValue != null) {
                FillGradientRect(command.Bounds, command.Radii, command.Brush);
                return;
            }
            if (command.Brush.Kind == BrushKind.Image) {
                FillImageRect(command.Bounds, command.Radii, command.Brush);
                return;
            }
            (byte r, byte g, byte b, byte a) = ResolveBrush(command.Brush);
            FillRoundedRect(command.Bounds, command.Radii, r, g, b, a);
        }

        // Per-pixel gradient sampling for goldens. The URP backend has a real
        // shader; here we just evaluate each gradient analytically inside the
        // rect bounds. CSS gradient axes are expressed in the BOX's local
        // coordinate frame (not the tile/origin frame), so for the common
        // "gradient fills the whole rect" case we map pixel (px,py) into
        // [0..W]×[0..H] local coords before sampling.
        void FillGradientRect(Rect rect, BorderRadii radii, Brush brush) {
            var gradient = brush.GradientValue;
            var (x0, y0) = TransformPoint(rect.X, rect.Y);
            var (x1, y1) = TransformPoint(rect.Right, rect.Y);
            var (x2, y2) = TransformPoint(rect.X, rect.Bottom);
            var (x3, y3) = TransformPoint(rect.Right, rect.Bottom);
            double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));

            var clip = clipStack.Peek();
            int ix0 = (int)Math.Max(clip.X, Math.Floor(minX));
            int iy0 = (int)Math.Max(clip.Y, Math.Floor(minY));
            int ix1 = (int)Math.Min(clip.Right, Math.Ceiling(maxX));
            int iy1 = (int)Math.Min(clip.Bottom, Math.Ceiling(maxY));
            if (ix1 <= ix0 || iy1 <= iy0) return;

            bool hasRadii = !radii.IsZero;
            double rectMidX = (minX + maxX) * 0.5;
            double rectMidY = (minY + maxY) * 0.5;
            double halfW = (maxX - minX) * 0.5;
            double halfH = (maxY - minY) * 0.5;
            double avgRadius = 0;
            if (hasRadii) {
                avgRadius = 0.25 * (radii.TopLeft.XRadius + radii.TopRight.XRadius
                                  + radii.BottomLeft.XRadius + radii.BottomRight.XRadius);
                if (avgRadius < 0) avgRadius = 0;
                if (avgRadius > halfW) avgRadius = halfW;
                if (avgRadius > halfH) avgRadius = halfH;
            }

            // Precompute linear-gradient axis params (CSS angle convention:
            // 0deg = bottom-to-top, increases clockwise; 180deg = top-to-bottom).
            double w = rect.Width;
            double h = rect.Height;
            double lgDx = 0, lgDy = 0, lgLen = 1, lgOriginU = 0;
            LinearGradient lin = gradient as LinearGradient;
            RadialGradient rad = gradient as RadialGradient;
            ConicGradient con = gradient as ConicGradient;
            if (lin != null) {
                double angRad = lin.AngleDegrees * Math.PI / 180.0;
                double sx = Math.Sin(angRad);
                double sy = -Math.Cos(angRad);
                lgLen = Math.Abs(w * sx) + Math.Abs(h * sy);
                if (lgLen <= 0) lgLen = 1;
                lgDx = sx;
                lgDy = sy;
                // Center of box maps to t=0.5 on the gradient line.
                lgOriginU = -0.5 * lgLen;
            }

            for (int py = iy0; py < iy1; py++) {
                for (int px = ix0; px < ix1; px++) {
                    double cx = px + 0.5;
                    double cy = py + 0.5;
                    double coverage;
                    if (hasRadii) {
                        double qx = Math.Abs(cx - rectMidX) - (halfW - avgRadius);
                        double qy = Math.Abs(cy - rectMidY) - (halfH - avgRadius);
                        double dx = Math.Max(qx, 0);
                        double dy = Math.Max(qy, 0);
                        double dist = Math.Sqrt(dx * dx + dy * dy) + Math.Min(Math.Max(qx, qy), 0) - avgRadius;
                        coverage = 1.0 - Math.Max(0, Math.Min(1, dist + 0.5));
                    } else {
                        if (cx < minX || cx >= maxX || cy < minY || cy >= maxY) continue;
                        coverage = 1.0;
                    }
                    if (coverage <= 0) continue;

                    // Map pixel to box-local coords (origin at rect.X/Y).
                    double lx = cx - rect.X;
                    double ly = cy - rect.Y;
                    double t;
                    if (lin != null) {
                        // Project box-centered point onto axis.
                        double rx = lx - 0.5 * w;
                        double ry = ly - 0.5 * h;
                        double proj = rx * lgDx + ry * lgDy;
                        t = (proj - lgOriginU) / lgLen;
                        if (lin.IsRepeating) t = t - Math.Floor(t);
                        else { if (t < 0) t = 0; else if (t > 1) t = 1; }
                    } else if (rad != null) {
                        double dxr = lx - rad.CenterX;
                        double dyr = ly - rad.CenterY;
                        double rxr = rad.RadiusX > 0 ? rad.RadiusX : 1;
                        double ryr = rad.RadiusY > 0 ? rad.RadiusY : 1;
                        double nx = dxr / rxr;
                        double ny = dyr / ryr;
                        t = Math.Sqrt(nx * nx + ny * ny);
                        // G13c: repeating-radial-gradient tiles its ramp along the
                        // normalized radius. Wrap t into [0,1) instead of clamping
                        // to 1 so the gradient repeats outside the radius. Non-
                        // repeating radials keep the original clamp behavior.
                        if (rad.IsRepeating) {
                            t = t - Math.Floor(t);
                        } else if (t > 1) {
                            t = 1;
                        }
                    } else if (con != null) {
                        var c = SampleConicWithRepeating(con, lx, ly);
                        byte cr = ToByte(c.R), cg = ToByte(c.G), cb = ToByte(c.B);
                        byte ca = (byte)Math.Round(c.A * 255.0 * coverage);
                        if (ca > 0) PutPixel(px, py, cr, cg, cb, ca);
                        continue;
                    } else {
                        // Unknown gradient — fall back to first stop.
                        var c = gradient.Stops.Count > 0 ? gradient.Stops[0].Color : LinearColor.Transparent;
                        byte cr = ToByte(c.R), cg = ToByte(c.G), cb = ToByte(c.B);
                        byte ca = (byte)Math.Round(c.A * 255.0 * coverage);
                        if (ca > 0) PutPixel(px, py, cr, cg, cb, ca);
                        continue;
                    }

                    var col = gradient.Sample(t);
                    byte br = ToByte(col.R), bg = ToByte(col.G), bb = ToByte(col.B);
                    byte ba = (byte)Math.Round(col.A * 255.0 * coverage);
                    if (ba == 0) continue;
                    PutPixel(px, py, br, bg, bb, ba);
                }
            }
        }

        // Per-pixel background image fill (B17 background path). Mirrors FillGradientRect
        // but samples pixels from an IRawPixelImageSource via the imageRegistry rather than
        // evaluating a gradient function. Tile geometry is read from brush.Tile; without a
        // tile the image stretches to fill the entire box (legacy behaviour, same as GPU path).
        //
        // Chrome behavior for missing / non-raw images in a BACKGROUND context: paint nothing
        // (transparent). This differs from the MASK context where a failed url() hides the
        // element (transparent-black mask) — for backgrounds, transparent means "show through
        // to whatever is beneath" which is the same end-result (no contribution from this layer).
        void FillImageRect(Rect rect, BorderRadii radii, Brush brush) {
            // Resolve the image source. If unavailable or not IRawPixelImageSource → no-op.
            if (string.IsNullOrEmpty(brush.ImageHandle) || imageRegistry == null) return;
            if (!imageRegistry.TryResolve(brush.ImageHandle, out var source) || source == null) return;
            var raw = source as IRawPixelImageSource;
            if (raw == null || raw.Pixels == null || raw.Width <= 0 || raw.Height <= 0) return;

            // Compute transformed bounding box for pixel iteration.
            var (x0t, y0t) = TransformPoint(rect.X, rect.Y);
            var (x1t, y1t) = TransformPoint(rect.Right, rect.Y);
            var (x2t, y2t) = TransformPoint(rect.X, rect.Bottom);
            var (x3t, y3t) = TransformPoint(rect.Right, rect.Bottom);
            double minX = Math.Min(Math.Min(x0t, x1t), Math.Min(x2t, x3t));
            double minY = Math.Min(Math.Min(y0t, y1t), Math.Min(y2t, y3t));
            double maxX = Math.Max(Math.Max(x0t, x1t), Math.Max(x2t, x3t));
            double maxY = Math.Max(Math.Max(y0t, y1t), Math.Max(y2t, y3t));

            var clip = clipStack.Peek();
            int ix0 = (int)Math.Max(clip.X, Math.Floor(minX));
            int iy0 = (int)Math.Max(clip.Y, Math.Floor(minY));
            int ix1 = (int)Math.Min(clip.Right, Math.Ceiling(maxX));
            int iy1 = (int)Math.Min(clip.Bottom, Math.Ceiling(maxY));
            if (ix1 <= ix0 || iy1 <= iy0) return;

            bool hasRadii = !radii.IsZero;
            double rectMidX = (minX + maxX) * 0.5;
            double rectMidY = (minY + maxY) * 0.5;
            double halfW = (maxX - minX) * 0.5;
            double halfH = (maxY - minY) * 0.5;
            double avgRadius = 0;
            if (hasRadii) {
                avgRadius = 0.25 * (radii.TopLeft.XRadius + radii.TopRight.XRadius
                                  + radii.BottomLeft.XRadius + radii.BottomRight.XRadius);
                if (avgRadius < 0) avgRadius = 0;
                if (avgRadius > halfW) avgRadius = halfW;
                if (avgRadius > halfH) avgRadius = halfH;
            }

            // Tile geometry. When brush.Tile is null, stretch image across the full box.
            bool hasTile = brush.Tile.HasValue;
            BackgroundTile tile = hasTile ? brush.Tile.Value : default;
            double tileW = hasTile ? tile.TileWidth  : rect.Width;
            double tileH = hasTile ? tile.TileHeight : rect.Height;
            double originX = hasTile ? tile.OriginX : 0;
            double originY = hasTile ? tile.OriginY : 0;
            var repeatX = hasTile ? tile.RepeatX : BackgroundRepeatMode.NoRepeat;
            var repeatY = hasTile ? tile.RepeatY : BackgroundRepeatMode.NoRepeat;
            if (tileW <= 0 || tileH <= 0) return;

            int imgW = raw.Width;
            int imgH = raw.Height;
            byte[] imgPixels = raw.Pixels;

            for (int py = iy0; py < iy1; py++) {
                for (int px = ix0; px < ix1; px++) {
                    double cx = px + 0.5;
                    double cy = py + 0.5;

                    // Rounded-rect coverage.
                    double coverage;
                    if (hasRadii) {
                        double qx = Math.Abs(cx - rectMidX) - (halfW - avgRadius);
                        double qy = Math.Abs(cy - rectMidY) - (halfH - avgRadius);
                        double dx = Math.Max(qx, 0);
                        double dy = Math.Max(qy, 0);
                        double dist = Math.Sqrt(dx * dx + dy * dy) + Math.Min(Math.Max(qx, qy), 0) - avgRadius;
                        coverage = 1.0 - Math.Max(0, Math.Min(1, dist + 0.5));
                    } else {
                        if (cx < minX || cx >= maxX || cy < minY || cy >= maxY) continue;
                        coverage = 1.0;
                    }
                    if (coverage <= 0) continue;

                    // Tile coordinate: offset within the tiling origin, then wrap/clamp.
                    double lx = cx - rect.X - originX;
                    double ly = cy - rect.Y - originY;

                    if (repeatX == BackgroundRepeatMode.NoRepeat) {
                        if (lx < 0 || lx >= tileW) continue; // transparent outside tile
                    } else {
                        lx = lx - Math.Floor(lx / tileW) * tileW;
                    }
                    if (repeatY == BackgroundRepeatMode.NoRepeat) {
                        if (ly < 0 || ly >= tileH) continue; // transparent outside tile
                    } else {
                        ly = ly - Math.Floor(ly / tileH) * tileH;
                    }

                    // Map local tile coords to image UV → texel → bilinear sample.
                    var color = SampleRawImagePixel(imgPixels, imgW, imgH, lx / tileW, ly / tileH);
                    byte ca = (byte)Math.Round(color.A * 255.0 * coverage);
                    if (ca == 0) continue;
                    byte cr = ToByte(color.R);
                    byte cg = ToByte(color.G);
                    byte cb = ToByte(color.B);
                    PutPixel(px, py, cr, cg, cb, ca);
                }
            }
        }

        public void Submit(StrokeBorderCommand command) {
            if (command == null) return;
            var b = command.Borders;
            if (b.IsNone) return;
            var r = command.Bounds;
            // Each edge runs along a 1D segment with a fixed width perpendicular to it.
            // We dispatch on style: solid is one filled rect, dashed/dotted/double break
            // the segment up. The corner crossings between edges are slightly inaccurate
            // for non-solid styles (each edge paints into the corner square independently);
            // good enough for the deterministic CPU rasterizer used in goldens.
            DrawEdge(b.Top, new Rect(r.X, r.Y, r.Width, b.Top.Width), Orientation.Horizontal);
            DrawEdge(b.Bottom, new Rect(r.X, r.Y + r.Height - b.Bottom.Width, r.Width, b.Bottom.Width), Orientation.Horizontal);
            DrawEdge(b.Left, new Rect(r.X, r.Y, b.Left.Width, r.Height), Orientation.Vertical);
            DrawEdge(b.Right, new Rect(r.X + r.Width - b.Right.Width, r.Y, b.Right.Width, r.Height), Orientation.Vertical);
        }

        enum Orientation { Horizontal, Vertical }

        void DrawEdge(BorderEdge edge, Rect rect, Orientation o) {
            if (edge.Style == BorderStyle.None || edge.Style == BorderStyle.Hidden || edge.Width <= 0) return;
            if (rect.Width <= 0 || rect.Height <= 0) return;
            switch (edge.Style) {
                case BorderStyle.Solid:
                    FillSolidRect(rect, edge.Color);
                    break;
                case BorderStyle.Dashed:
                    DrawDashedSegment(rect, edge.Width, edge.Color, o);
                    break;
                case BorderStyle.Dotted:
                    DrawDottedSegment(rect, edge.Width, edge.Color, o);
                    break;
                case BorderStyle.Double:
                    DrawDoubleSegment(rect, edge.Width, edge.Color, o);
                    break;
            }
        }

        // Dashed pattern per CSS: stroke length = 2 * width, gap = 2 * width.
        void DrawDashedSegment(Rect rect, double width, LinearColor color, Orientation o) {
            double dash = 2.0 * width;
            double gap = 2.0 * width;
            double period = dash + gap;
            if (o == Orientation.Horizontal) {
                double x = rect.X;
                double end = rect.X + rect.Width;
                while (x < end) {
                    double w = Math.Min(dash, end - x);
                    FillSolidRect(new Rect(x, rect.Y, w, rect.Height), color);
                    x += period;
                }
            } else {
                double y = rect.Y;
                double end = rect.Y + rect.Height;
                while (y < end) {
                    double h = Math.Min(dash, end - y);
                    FillSolidRect(new Rect(rect.X, y, rect.Width, h), color);
                    y += period;
                }
            }
        }

        // Dotted pattern: filled circles of diameter = width, spaced by 2 * width on center.
        void DrawDottedSegment(Rect rect, double width, LinearColor color, Orientation o) {
            double diameter = width;
            double step = 2.0 * width;
            if (step <= 0) return;
            if (o == Orientation.Horizontal) {
                double cy = rect.Y + rect.Height * 0.5;
                double cx = rect.X + diameter * 0.5;
                double end = rect.X + rect.Width;
                while (cx <= end + 0.5) {
                    FillCircle(cx, cy, diameter * 0.5, color);
                    cx += step;
                }
            } else {
                double cx = rect.X + rect.Width * 0.5;
                double cy = rect.Y + diameter * 0.5;
                double end = rect.Y + rect.Height;
                while (cy <= end + 0.5) {
                    FillCircle(cx, cy, diameter * 0.5, color);
                    cy += step;
                }
            }
        }

        // Double border: two parallel solid strokes with a gap of width / 3 between them
        // (CSS spec: "the line, the gap, and the line have the same width").
        void DrawDoubleSegment(Rect rect, double width, LinearColor color, Orientation o) {
            double third = width / 3.0;
            if (third <= 0) {
                FillSolidRect(rect, color);
                return;
            }
            if (o == Orientation.Horizontal) {
                FillSolidRect(new Rect(rect.X, rect.Y, rect.Width, third), color);
                FillSolidRect(new Rect(rect.X, rect.Y + rect.Height - third, rect.Width, third), color);
            } else {
                FillSolidRect(new Rect(rect.X, rect.Y, third, rect.Height), color);
                FillSolidRect(new Rect(rect.X + rect.Width - third, rect.Y, third, rect.Height), color);
            }
        }

        void FillCircle(double cx, double cy, double radius, LinearColor color) {
            if (radius <= 0) return;
            byte r = ToByte(color.R);
            byte g = ToByte(color.G);
            byte bl = ToByte(color.B);
            byte a = (byte)Math.Round(color.A * 255.0);
            var (txc, tyc) = TransformPoint(cx, cy);
            var clip = clipStack.Peek();
            int ix0 = (int)Math.Max(clip.X, Math.Floor(txc - radius));
            int iy0 = (int)Math.Max(clip.Y, Math.Floor(tyc - radius));
            int ix1 = (int)Math.Min(clip.Right, Math.Ceiling(txc + radius));
            int iy1 = (int)Math.Min(clip.Bottom, Math.Ceiling(tyc + radius));
            double r2 = radius * radius;
            for (int py = iy0; py < iy1; py++) {
                for (int px = ix0; px < ix1; px++) {
                    double dx = px + 0.5 - txc;
                    double dy = py + 0.5 - tyc;
                    double d2 = dx * dx + dy * dy;
                    if (d2 > r2) continue;
                    PutPixel(px, py, r, g, bl, a);
                }
            }
        }

        public void Submit(DrawTextCommand command) {
            if (command == null || string.IsNullOrEmpty(command.Text)) return;
            double fontSize = command.Font.Size > 0 ? command.Font.Size : 16;
            double charWidth = fontMetrics.Measure("M", fontSize);
            if (charWidth <= 0) charWidth = fontSize * 0.5;
            double ascent = fontMetrics.Ascent(fontSize);
            // Draw each non-space character as a solid square the width of an em.
            // The block is centered vertically inside the run bounds and grows downward
            // from the baseline a la a real glyph would. For the regression-stable goal
            // the exact y-position only matters in being deterministic.
            double baselineY = command.Bounds.Y + ascent;
            double blockHeight = ascent * 0.7;
            for (int i = 0; i < command.Text.Length; i++) {
                char c = command.Text[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') continue;
                double x = command.Bounds.X + i * charWidth;
                double w = charWidth * 0.85;
                double y = baselineY - blockHeight;
                FillSolidRect(new Rect(x, y, w, blockHeight), command.Color);
            }
        }

        public void Submit(DrawShadowCommand command) {
            if (command == null) return;
            var s = command.Shadow;
            if (s.Inset) {
                DrawInsetShadow(command);
                return;
            }

            double spread = s.SpreadRadius;
            var b = command.Bounds;
            var shadowRect = new Rect(
                b.X + s.OffsetX - spread,
                b.Y + s.OffsetY - spread,
                b.Width + 2 * spread,
                b.Height + 2 * spread);

            // Render the shadow into a small RGBA buffer big enough to include the blur halo.
            int blurPx = (int)Math.Ceiling(s.BlurRadius);
            if (blurPx < 0) blurPx = 0;
            int padding = blurPx + 2;

            int sx = (int)Math.Floor(shadowRect.X) - padding;
            int sy = (int)Math.Floor(shadowRect.Y) - padding;
            int sw = (int)Math.Ceiling(shadowRect.Right) - sx + padding;
            int sh = (int)Math.Ceiling(shadowRect.Bottom) - sy + padding;
            if (sw <= 0 || sh <= 0) return;

            byte[] shadowBuf = new byte[sw * sh * 4];
            byte cr = ToByte(s.Color.R);
            byte cg = ToByte(s.Color.G);
            byte cb = ToByte(s.Color.B);

            // Pre-fill the whole buffer with the shadow color at alpha=0.
            // BoxBlurAlpha (below) only blurs the alpha channel, so RGB stays
            // constant per pixel — by pre-filling with the shadow color we
            // ensure the blurred alpha halo carries the correct color too.
            // Without this, pixels outside the stamped shape keep RGB=0
            // (black) and the blurred halo around a colored shadow renders
            // dark instead of taking on the shadow's tint (visible as a
            // green/black smudge around match3's gold selected-tile glow).
            for (int i = 0; i < shadowBuf.Length; i += 4) {
                shadowBuf[i + 0] = cr;
                shadowBuf[i + 1] = cg;
                shadowBuf[i + 2] = cb;
                // alpha left at 0
            }
            // Stamp the (rounded-rect) shadow shape into shadowBuf at full alpha so the
            // box-blur below will smear it deterministically.
            double shapeMinX = shadowRect.X - sx;
            double shapeMinY = shadowRect.Y - sy;
            double shapeMaxX = shadowRect.Right - sx;
            double shapeMaxY = shadowRect.Bottom - sy;
            byte stampAlpha = (byte)Math.Round(s.Color.A * 255.0);
            for (int y = 0; y < sh; y++) {
                double py = y + 0.5;
                for (int x = 0; x < sw; x++) {
                    double px = x + 0.5;
                    if (px < shapeMinX || px >= shapeMaxX || py < shapeMinY || py >= shapeMaxY) continue;
                    int o = (y * sw + x) * 4;
                    shadowBuf[o + 3] = stampAlpha;
                }
            }

            if (blurPx > 0) {
                // Three box-blur passes of radius ~ blurPx/3 approximate a gaussian.
                int radius = Math.Max(1, (int)Math.Round(blurPx / 3.0));
                BoxBlurAlpha(shadowBuf, sw, sh, radius);
                BoxBlurAlpha(shadowBuf, sw, sh, radius);
                BoxBlurAlpha(shadowBuf, sw, sh, radius);
            }

            // Composite the temp buffer into the framebuffer through the global state.
            for (int y = 0; y < sh; y++) {
                int dy = sy + y;
                for (int x = 0; x < sw; x++) {
                    int dx = sx + x;
                    int o = (y * sw + x) * 4;
                    byte alpha = shadowBuf[o + 3];
                    if (alpha == 0) continue;
                    PutPixel(dx, dy, shadowBuf[o + 0], shadowBuf[o + 1], shadowBuf[o + 2], alpha);
                }
            }
        }

        // Inset shadow: paints darkness on the *inside* of the box opposite to the
        // offset direction. The CSS spec defines the shadow shape as the box contracted
        // by `spread`, then translated by (offsetX, offsetY); pixels inside the box but
        // outside this hole receive the shadow color. With offset (+8, +8) the hole
        // shifts down-right, leaving the dark band on the top-left edge.
        void DrawInsetShadow(DrawShadowCommand command) {
            var s = command.Shadow;
            var b = command.Bounds;
            double spread = s.SpreadRadius;
            var holeRect = new Rect(
                b.X + s.OffsetX + spread,
                b.Y + s.OffsetY + spread,
                Math.Max(0, b.Width - 2 * spread),
                Math.Max(0, b.Height - 2 * spread));

            int blurPx = (int)Math.Ceiling(s.BlurRadius);
            if (blurPx < 0) blurPx = 0;
            int padding = blurPx + 2;

            int sx = (int)Math.Floor(b.X) - padding;
            int sy = (int)Math.Floor(b.Y) - padding;
            int sw = (int)Math.Ceiling(b.Right) - sx + padding;
            int sh = (int)Math.Ceiling(b.Bottom) - sy + padding;
            if (sw <= 0 || sh <= 0) return;

            byte[] buf = new byte[sw * sh * 4];
            byte cr = ToByte(s.Color.R);
            byte cg = ToByte(s.Color.G);
            byte cb = ToByte(s.Color.B);
            byte ca = (byte)Math.Round(s.Color.A * 255.0);

            double holeMinX = holeRect.X - sx;
            double holeMinY = holeRect.Y - sy;
            double holeMaxX = holeRect.Right - sx;
            double holeMaxY = holeRect.Bottom - sy;
            // Stamp: full-alpha shadow outside the hole, transparent inside.
            for (int y = 0; y < sh; y++) {
                double py = y + 0.5;
                for (int x = 0; x < sw; x++) {
                    double px = x + 0.5;
                    bool inHole = px >= holeMinX && px < holeMaxX && py >= holeMinY && py < holeMaxY;
                    if (inHole) continue;
                    int o = (y * sw + x) * 4;
                    buf[o + 0] = cr;
                    buf[o + 1] = cg;
                    buf[o + 2] = cb;
                    buf[o + 3] = ca;
                }
            }

            if (blurPx > 0) {
                int radius = Math.Max(1, (int)Math.Round(blurPx / 3.0));
                BoxBlurAlpha(buf, sw, sh, radius);
                BoxBlurAlpha(buf, sw, sh, radius);
                BoxBlurAlpha(buf, sw, sh, radius);
            }

            // Clip to the box's interior so the dark band paints only inside the box.
            int boxX0 = (int)Math.Floor(b.X);
            int boxY0 = (int)Math.Floor(b.Y);
            int boxX1 = (int)Math.Ceiling(b.Right);
            int boxY1 = (int)Math.Ceiling(b.Bottom);
            for (int y = 0; y < sh; y++) {
                int dy = sy + y;
                if (dy < boxY0 || dy >= boxY1) continue;
                for (int x = 0; x < sw; x++) {
                    int dx = sx + x;
                    if (dx < boxX0 || dx >= boxX1) continue;
                    int o = (y * sw + x) * 4;
                    byte alpha = buf[o + 3];
                    if (alpha == 0) continue;
                    PutPixel(dx, dy, buf[o + 0], buf[o + 1], buf[o + 2], alpha);
                }
            }
        }

        public void Submit(PushClipCommand command) {
            if (command == null) return;
            var topClip = clipStack.Peek();
            var (xfx0, xfy0) = TransformPoint(command.Bounds.X, command.Bounds.Y);
            var (xfx1, xfy1) = TransformPoint(command.Bounds.Right, command.Bounds.Bottom);
            int x0 = (int)Math.Max(topClip.X, Math.Floor(Math.Min(xfx0, xfx1)));
            int y0 = (int)Math.Max(topClip.Y, Math.Floor(Math.Min(xfy0, xfy1)));
            int x1 = (int)Math.Min(topClip.Right, Math.Ceiling(Math.Max(xfx0, xfx1)));
            int y1 = (int)Math.Min(topClip.Bottom, Math.Ceiling(Math.Max(xfy0, xfy1)));
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;
            clipStack.Push(new ClipRect(x0, y0, x1 - x0, y1 - y0));
        }

        public void Submit(DrawBackdropFilterCommand command) {
            if (command == null || command.Filters == null || command.Filters.IsEmpty) return;
            var (xfx0, xfy0) = TransformPoint(command.Bounds.X, command.Bounds.Y);
            var (xfx1, xfy1) = TransformPoint(command.Bounds.Right, command.Bounds.Bottom);
            int x0 = (int)Math.Max(0, Math.Floor(Math.Min(xfx0, xfx1)));
            int y0 = (int)Math.Max(0, Math.Floor(Math.Min(xfy0, xfy1)));
            int x1 = (int)Math.Min(width, Math.Ceiling(Math.Max(xfx0, xfx1)));
            int y1 = (int)Math.Min(height, Math.Ceiling(Math.Max(xfy0, xfy1)));
            if (x1 <= x0 || y1 <= y0) return;
            int w = x1 - x0;
            int h = y1 - y0;
            byte[] working = SnapshotRegion(x0, y0, w, h);
            var mask = new byte[w * h];
            for (int i = 0; i < mask.Length; i++) mask[i] = 255;
            ApplyFilterChain(working, w, h, command.Filters, mask);

            for (int yy = 0; yy < h; yy++) {
                for (int xx = 0; xx < w; xx++) {
                    int px = x0 + xx;
                    int py = y0 + yy;
                    if (!PixelPassesGlobalClip(px, py)) continue;
                    if (!PointInRoundedRect(px + 0.5, py + 0.5, command.Bounds, command.Radii)) continue;
                    int src = (yy * w + xx) * 4;
                    int dst = (py * width + px) * 4;
                    pixels[dst + 0] = working[src + 0];
                    pixels[dst + 1] = working[src + 1];
                    pixels[dst + 2] = working[src + 2];
                    pixels[dst + 3] = working[src + 3];
                }
            }
        }

        public void Submit(PopClipCommand command) {
            if (clipStack.Count > 1) clipStack.Pop();
        }

        public void Submit(PushClipPathCommand command) {
            if (command == null || command.Shape == null) return;
            clipPathStack.Push(command.Shape.Transform(transformStack.Peek()));
        }

        public void Submit(PopClipPathCommand command) {
            if (clipPathStack.Count > 0) clipPathStack.Pop();
        }

        public void Submit(PushMaskCommand command) {
            if (command == null || command.Mask == null) return;
            var (xfx0, xfy0) = TransformPoint(command.Bounds.X, command.Bounds.Y);
            var (xfx1, xfy1) = TransformPoint(command.Bounds.Right, command.Bounds.Bottom);
            int x0 = (int)Math.Max(0, Math.Floor(Math.Min(xfx0, xfx1)));
            int y0 = (int)Math.Max(0, Math.Floor(Math.Min(xfy0, xfy1)));
            int x1 = (int)Math.Min(width, Math.Ceiling(Math.Max(xfx0, xfx1)));
            int y1 = (int)Math.Min(height, Math.Ceiling(Math.Max(xfy0, xfy1)));
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;
            byte[] before = SnapshotRegion(x0, y0, x1 - x0, y1 - y0);
            maskStack.Push(new MaskFrame(command.Mask, x0, y0, x1 - x0, y1 - y0, before));
        }

        public void Submit(PopMaskCommand command) {
            if (maskStack.Count == 0) return;
            var frame = maskStack.Pop();
            if (frame.W <= 0 || frame.H <= 0) return;
            byte[] live = SnapshotRegion(frame.X, frame.Y, frame.W, frame.H);
            for (int y = 0; y < frame.H; y++) {
                for (int x = 0; x < frame.W; x++) {
                    int o = (y * frame.W + x) * 4;
                    int dx = frame.X + x;
                    int dy = frame.Y + y;
                    int p = (dy * width + dx) * 4;
                    pixels[p + 0] = frame.Before[o + 0];
                    pixels[p + 1] = frame.Before[o + 1];
                    pixels[p + 2] = frame.Before[o + 2];
                    pixels[p + 3] = frame.Before[o + 3];

                    double alpha = SampleMaskAlpha(frame.Mask, dx + 0.5, dy + 0.5);
                    if (alpha <= 0) continue;
                    byte a = (byte)Math.Round(live[o + 3] * alpha);
                    if (a == 0) continue;
                    PutPixel(dx, dy, live[o + 0], live[o + 1], live[o + 2], a);
                }
            }
        }

        public void Submit(PushOpacityCommand command) {
            if (command == null) return;
            double current = opacityStack.Peek();
            double v = command.Opacity;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            opacityStack.Push(current * v);
        }

        public void Submit(PopOpacityCommand command) {
            if (opacityStack.Count > 1) opacityStack.Pop();
        }

        public void Submit(PushTransformCommand command) {
            if (command == null) return;
            var top = transformStack.Peek();
            transformStack.Push(top.Multiply(command.Transform));
        }

        public void Submit(PopTransformCommand command) {
            if (transformStack.Count > 1) transformStack.Pop();
        }

        public void Submit(PushFilterCommand command) {
            if (command == null) return;
            var (xfx0, xfy0) = TransformPoint(command.Bounds.X, command.Bounds.Y);
            var (xfx1, xfy1) = TransformPoint(command.Bounds.Right, command.Bounds.Bottom);
            int x0 = (int)Math.Max(0, Math.Floor(Math.Min(xfx0, xfx1)));
            int y0 = (int)Math.Max(0, Math.Floor(Math.Min(xfy0, xfy1)));
            int x1 = (int)Math.Min(width, Math.Ceiling(Math.Max(xfx0, xfx1)));
            int y1 = (int)Math.Min(height, Math.Ceiling(Math.Max(xfy0, xfy1)));
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;
            // Snapshot the current contents of the sub-rect so PopFilter can operate
            // on only what was painted *during* this PushFilter scope: subtract the
            // pre-state on Pop, leave only the new content for filtering.
            byte[] before = SnapshotRegion(x0, y0, x1 - x0, y1 - y0);
            filterStack.Push(new FilterFrame(command.Filters, x0, y0, x1 - x0, y1 - y0, before));
        }

        public void Submit(PopFilterCommand command) {
            if (filterStack.Count == 0) return;
            var frame = filterStack.Pop();
            if (frame.W <= 0 || frame.H <= 0) return;

            // Read the live region. Diff against the pre-snapshot to isolate "what was painted
            // inside the filter scope". For pixels that didn't change we leave the original alone.
            byte[] live = SnapshotRegion(frame.X, frame.Y, frame.W, frame.H);
            byte[] working = (byte[])live.Clone();
            // changedMask[i] is non-zero if pixel i was painted within this filter scope.
            // Drop-shadow needs this because the rasterizer's framebuffer doesn't carry a
            // "freshly painted" alpha — both before and after live in the same byte buffer
            // and white-on-white is a legitimate no-op. Only mask-marked pixels cast a
            // shadow, and the shadow halo extends mask coverage so the diff loop below
            // writes the halo to the framebuffer too.
            byte[] mask = new byte[frame.W * frame.H];
            for (int i = 0; i < mask.Length; i++) {
                int o = i * 4;
                if (live[o + 0] != frame.Before[o + 0]
                    || live[o + 1] != frame.Before[o + 1]
                    || live[o + 2] != frame.Before[o + 2]
                    || live[o + 3] != frame.Before[o + 3]) {
                    mask[i] = 255;
                }
            }

            ApplyFilterChain(working, frame.W, frame.H, frame.Filters, mask);

            // Splat working back into the framebuffer for pixels touched within the
            // scope. A pixel is "touched" if either it differs from before (the usual
            // path) or the filter chain explicitly marked it (drop-shadow halo).
            for (int y = 0; y < frame.H; y++) {
                for (int x = 0; x < frame.W; x++) {
                    int o = (y * frame.W + x) * 4;
                    int mi = y * frame.W + x;
                    bool changed = mask[mi] != 0
                        || live[o + 0] != frame.Before[o + 0]
                        || live[o + 1] != frame.Before[o + 1]
                        || live[o + 2] != frame.Before[o + 2]
                        || live[o + 3] != frame.Before[o + 3];
                    int dx = frame.X + x;
                    int dy = frame.Y + y;
                    int p = (dy * width + dx) * 4;
                    if (changed) {
                        pixels[p + 0] = working[o + 0];
                        pixels[p + 1] = working[o + 1];
                        pixels[p + 2] = working[o + 2];
                        pixels[p + 3] = working[o + 3];
                    } else {
                        pixels[p + 0] = frame.Before[o + 0];
                        pixels[p + 1] = frame.Before[o + 1];
                        pixels[p + 2] = frame.Before[o + 2];
                        pixels[p + 3] = frame.Before[o + 3];
                    }
                }
            }
        }

        void ApplyFilterChain(byte[] working, int w, int h, FilterChain filters, byte[] changeMask = null) {
            if (working == null || filters == null || filters.Functions == null) return;
            if (changeMask == null) changeMask = new byte[w * h];
            foreach (var fn in filters.Functions) {
                switch (fn) {
                    case BlurFilter bf:
                        // CSS Filter Effects 1 §6.1: the <length> argument to
                        // blur() is the Gaussian σ directly. We approximate that
                        // Gaussian with three box-blur passes (Wells 1986). For
                        // n=3 cascaded boxes of half-width r, the resulting
                        // standard deviation satisfies σ² = n · ((2r+1)² − 1)/12,
                        // which for n=3 simplifies to r ≈ σ · √3 / 2 ≈ 0.866 σ.
                        // The earlier r = σ/3 yielded σ_approx ≈ σ/3 — the same
                        // half-spec mistake as A3 on the GPU path. Pin per-pass
                        // radius to σ × √3 / 2 instead so the golden matches the
                        // runtime blur strength.
                        int r = ComputeBoxBlurRadiusForSigma(bf.RadiusPx);
                        BoxBlurRgba(working, w, h, r);
                        BoxBlurRgba(working, w, h, r);
                        BoxBlurRgba(working, w, h, r);
                        break;
                    case BrightnessFilter br:
                        ApplyBrightness(working, br.Amount);
                        break;
                    case ContrastFilter ct:
                        ApplyContrast(working, ct.Amount);
                        break;
                    case GrayscaleFilter gs:
                        ApplyGrayscale(working, gs.Amount);
                        break;
                    case DropShadowFilter ds:
                        ApplyDropShadow(working, changeMask, w, h, ds);
                        break;
                    // TODO: opacity, saturate, hue-rotate, invert, sepia
                }
            }
        }

        public void Submit(BeginSubtreeCaptureCommand command) { }
        public void Submit(EndSubtreeCaptureCommand command) { }
        public void Submit(ReplaySubtreeSnapshotCommand command) { }

        // ===== fill helpers =====

        void FillSolidRect(Rect rect, LinearColor color) {
            byte r = ToByte(color.R);
            byte g = ToByte(color.G);
            byte b = ToByte(color.B);
            byte a = (byte)Math.Round(color.A * 255.0);
            FillRoundedRect(rect, BorderRadii.Zero, r, g, b, a);
        }

        void FillSolidRect(Rect rect, byte r, byte g, byte b, byte a) {
            FillRoundedRect(rect, BorderRadii.Zero, r, g, b, a);
        }

        void FillRoundedRect(Rect rect, BorderRadii radii, byte r, byte g, byte b, byte a) {
            // Transform the rect's four corners. For axis-aligned scales/translations the
            // bounding box fully covers the rotated rect; for rotations we paint the AABB
            // as an approximation (this is the deterministic "good enough" path).
            var (x0, y0) = TransformPoint(rect.X, rect.Y);
            var (x1, y1) = TransformPoint(rect.Right, rect.Y);
            var (x2, y2) = TransformPoint(rect.X, rect.Bottom);
            var (x3, y3) = TransformPoint(rect.Right, rect.Bottom);
            double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));

            var clip = clipStack.Peek();
            int ix0 = (int)Math.Max(clip.X, Math.Floor(minX));
            int iy0 = (int)Math.Max(clip.Y, Math.Floor(minY));
            int ix1 = (int)Math.Min(clip.Right, Math.Ceiling(maxX));
            int iy1 = (int)Math.Min(clip.Bottom, Math.Ceiling(maxY));
            if (ix1 <= ix0 || iy1 <= iy0) return;

            // Only apply the SDF when there are nonzero radii; otherwise a flat fill is
            // both faster and pixel-perfect.
            bool hasRadii = !radii.IsZero;
            double rectMidX = (minX + maxX) * 0.5;
            double rectMidY = (minY + maxY) * 0.5;
            double halfW = (maxX - minX) * 0.5;
            double halfH = (maxY - minY) * 0.5;
            // Pick a single radius for SDF rounding (simplification: average the four).
            double avgRadius = 0;
            if (hasRadii) {
                avgRadius = 0.25 * (radii.TopLeft.XRadius + radii.TopRight.XRadius
                                  + radii.BottomLeft.XRadius + radii.BottomRight.XRadius);
                if (avgRadius < 0) avgRadius = 0;
                if (avgRadius > halfW) avgRadius = halfW;
                if (avgRadius > halfH) avgRadius = halfH;
            }

            for (int py = iy0; py < iy1; py++) {
                for (int px = ix0; px < ix1; px++) {
                    double cx = px + 0.5;
                    double cy = py + 0.5;
                    double coverage;
                    if (hasRadii) {
                        // Rounded-rect SDF: distance from the box edge, with corner radius subtracted.
                        double qx = Math.Abs(cx - rectMidX) - (halfW - avgRadius);
                        double qy = Math.Abs(cy - rectMidY) - (halfH - avgRadius);
                        double dx = Math.Max(qx, 0);
                        double dy = Math.Max(qy, 0);
                        double dist = Math.Sqrt(dx * dx + dy * dy) + Math.Min(Math.Max(qx, qy), 0) - avgRadius;
                        // 1px AA band.
                        coverage = 1.0 - Math.Max(0, Math.Min(1, dist + 0.5));
                    } else {
                        if (cx < minX || cx >= maxX || cy < minY || cy >= maxY) continue;
                        coverage = 1.0;
                    }
                    if (coverage <= 0) continue;
                    byte effA = (byte)Math.Round(a * coverage);
                    if (effA == 0) continue;
                    PutPixel(px, py, r, g, b, effA);
                }
            }
        }

        void PutPixel(int px, int py, byte r, byte g, byte b, byte a) {
            if (px < 0 || px >= width || py < 0 || py >= height) return;
            if (!PixelPassesGlobalClip(px, py)) return;
            double opacity = opacityStack.Peek();
            double srcA = (a / 255.0) * opacity;
            if (srcA <= 0) return;
            int o = (py * width + px) * 4;
            double dstR = pixels[o + 0] / 255.0;
            double dstG = pixels[o + 1] / 255.0;
            double dstB = pixels[o + 2] / 255.0;
            double dstA = pixels[o + 3] / 255.0;
            double srcR = r / 255.0;
            double srcG = g / 255.0;
            double srcB = b / 255.0;
            double outA = srcA + dstA * (1.0 - srcA);
            if (outA <= 0) {
                pixels[o + 0] = pixels[o + 1] = pixels[o + 2] = pixels[o + 3] = 0;
                return;
            }
            double outR = (srcR * srcA + dstR * dstA * (1.0 - srcA)) / outA;
            double outG = (srcG * srcA + dstG * dstA * (1.0 - srcA)) / outA;
            double outB = (srcB * srcA + dstB * dstA * (1.0 - srcA)) / outA;
            pixels[o + 0] = ToByte01(outR);
            pixels[o + 1] = ToByte01(outG);
            pixels[o + 2] = ToByte01(outB);
            pixels[o + 3] = ToByte01(outA);
        }

        bool PixelPassesGlobalClip(int px, int py) {
            var clip = clipStack.Peek();
            if (px < clip.X || px >= clip.Right || py < clip.Y || py >= clip.Bottom) return false;
            if (clipPathStack.Count == 0) return true;
            double x = px + 0.5;
            double y = py + 0.5;
            foreach (var shape in clipPathStack) {
                if (shape != null && !shape.Contains(x, y)) return false;
            }
            return true;
        }

        static bool PointInRoundedRect(double x, double y, Rect rect, BorderRadii radii) {
            if (x < rect.X || x > rect.Right || y < rect.Y || y > rect.Bottom) return false;
            if (radii.IsZero) return true;
            var shape = new InsetClipPathShape(rect, radii);
            return shape.Contains(x, y);
        }

        // ===== brushes =====

        (byte r, byte g, byte b, byte a) ResolveBrush(Brush brush) {
            switch (brush.Kind) {
                case BrushKind.SolidColor:
                    return (ToByte(brush.Color.R), ToByte(brush.Color.G), ToByte(brush.Color.B),
                            (byte)Math.Round(brush.Color.A * 255.0));
                case BrushKind.Gradient:
                    var stops = brush.GradientValue?.Stops;
                    if (stops != null && stops.Count > 0) {
                        var c = stops[0].Color;
                        return (ToByte(c.R), ToByte(c.G), ToByte(c.B), (byte)Math.Round(c.A * 255.0));
                    }
                    return (128, 128, 128, 255);
                case BrushKind.Image:
                    // Image brushes are handled by FillImageRect (per-pixel path) and
                    // never reach ResolveBrush. Return transparent as a safe fallback
                    // in case a code path calls ResolveBrush directly on an Image brush.
                    return (0, 0, 0, 0);
                default:
                    return (0, 0, 0, 0);
            }
        }

        double SampleMaskAlpha(MaskDefinition mask, double x, double y) {
            if (mask == null || mask.IsEmpty) return 1.0;
            var layers = mask.Layers;
            double alpha = SampleMaskLayerAlpha(layers[layers.Count - 1], x, y);
            for (int i = layers.Count - 2; i >= 0; i--) {
                var layer = layers[i];
                double src = SampleMaskLayerAlpha(layer, x, y);
                alpha = CompositeMaskAlpha(src, alpha, layer.Composite);
            }
            if (alpha < 0) return 0;
            if (alpha > 1) return 1;
            return alpha;
        }

        static double CompositeMaskAlpha(double src, double dst, MaskComposite composite) {
            switch (composite) {
                case MaskComposite.Subtract:
                    return src * (1.0 - dst);
                case MaskComposite.Intersect:
                    return src * dst;
                case MaskComposite.Exclude:
                    return src * (1.0 - dst) + dst * (1.0 - src);
                default:
                    return src + dst * (1.0 - src);
            }
        }

        double SampleMaskLayerAlpha(MaskLayer mask, double x, double y) {
            // B16 — skip synthetic path-clip mask layers: the software path already clips
            // via PixelPassesGlobalClip / PathClipPathShape.Contains, so applying the
            // coverage mask a second time would double-darken AA edges. The GPU path
            // uses this layer normally (it does not have a Contains() short-circuit).
            if (mask != null && mask.IsSyntheticClipMask) return 1.0;
            if (mask == null || mask.Brush == null) return 0.0;
            var b = mask.Bounds;
            if (x < b.X || x >= b.Right || y < b.Y || y >= b.Bottom) return 0.0;

            double sx = x;
            double sy = y;
            Rect sampleBounds = b;
            if (mask.Tile.HasValue) {
                var tile = mask.Tile.Value;
                double tw = tile.TileWidth > 0 ? tile.TileWidth : b.Width;
                double th = tile.TileHeight > 0 ? tile.TileHeight : b.Height;
                if (tw <= 0 || th <= 0) return 0.0;
                double lx = x - b.X - tile.OriginX;
                double ly = y - b.Y - tile.OriginY;
                if (tile.RepeatX == BackgroundRepeatMode.NoRepeat) {
                    if (lx < 0 || lx >= tw) return 0.0;
                } else {
                    lx = lx - Math.Floor(lx / tw) * tw;
                }
                if (tile.RepeatY == BackgroundRepeatMode.NoRepeat) {
                    if (ly < 0 || ly >= th) return 0.0;
                } else {
                    ly = ly - Math.Floor(ly / th) * th;
                }
                sx = lx;
                sy = ly;
                sampleBounds = new Rect(0, 0, tw, th);
            }

            LinearColor color;
            switch (mask.Brush.Kind) {
                case BrushKind.SolidColor:
                    color = mask.Brush.Color;
                    break;
                case BrushKind.Gradient:
                    color = SampleGradient(mask.Brush.GradientValue, sampleBounds, sx, sy);
                    break;
                case BrushKind.Image:
                    // B17: sample the registered image's RGBA pixels via IRawPixelImageSource.
                    // Chrome behavior for a failed/unresolved mask-image url: the layer acts as
                    // a fully transparent image, hiding the element. We match that here —
                    // returning 0.0 (transparent) when the image cannot be sampled.
                    color = SampleImageMaskPixel(mask.Brush.ImageHandle, sampleBounds, sx, sy);
                    break;
                default:
                    return 0.0;
            }

            double alpha = color.A;
            if (mask.Mode == MaskMode.Luminance) {
                double luma = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
                alpha *= luma;
            }
            if (alpha < 0) return 0;
            if (alpha > 1) return 1;
            return alpha;
        }

        // G13c: per-pixel conic sample that honors ConicGradient.IsRepeating.
        // For repeating conic gradients the angular sweep tiles with period equal
        // to the largest stop position (in normalized [0,1] / turn space), so
        // angles past the last stop wrap back to the first instead of clamping.
        // Non-repeating gradients fall through to ConicGradient.SampleAtPixel.
        internal static LinearColor SampleConicWithRepeating(ConicGradient con, double px, double py) {
            if (!con.IsRepeating) return con.SampleAtPixel(px, py);
            double dx = px - con.CenterX;
            double dy = py - con.CenterY;
            double ang = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
            ang = ang - con.FromAngleDegrees;
            ang = ((ang % 360.0) + 360.0) % 360.0;
            double t = ang / 360.0;
            // Period == largest stop position. Stops with NaN/default positions
            // are ignored; if no positive position is found we keep period == 1
            // (degenerate; matches the non-repeating clamp behaviour for safety).
            double period = 0.0;
            var stops = con.Stops;
            for (int i = 0; i < stops.Count; i++) {
                double p = stops[i].Position;
                if (double.IsNaN(p)) continue;
                if (p > period) period = p;
            }
            if (period <= 1e-9) period = 1.0;
            // frac(t / period) * period — t now lives in [0, period], the same
            // numeric range as the packed stop positions.
            double scaled = t / period;
            scaled = scaled - Math.Floor(scaled);
            return con.Sample(scaled * period);
        }

        static LinearColor SampleGradient(Gradient gradient, Rect bounds, double x, double y) {
            if (gradient == null) return new LinearColor(1f, 1f, 1f, 1f);
            double lx = x - bounds.X;
            double ly = y - bounds.Y;
            double w = bounds.Width > 0 ? bounds.Width : 1.0;
            double h = bounds.Height > 0 ? bounds.Height : 1.0;

            if (gradient is LinearGradient lin) {
                double angRad = lin.AngleDegrees * Math.PI / 180.0;
                double sx = Math.Sin(angRad);
                double sy = -Math.Cos(angRad);
                double len = Math.Abs(w * sx) + Math.Abs(h * sy);
                if (len <= 0) len = 1.0;
                double rx = lx - 0.5 * w;
                double ry = ly - 0.5 * h;
                double t = (rx * sx + ry * sy + 0.5 * len) / len;
                if (lin.IsRepeating) t = t - Math.Floor(t);
                else if (t < 0) t = 0;
                else if (t > 1) t = 1;
                return gradient.Sample(t);
            }

            if (gradient is RadialGradient rad) {
                double rx = rad.RadiusX > 0 ? rad.RadiusX : w * 0.5;
                double ry = rad.RadiusY > 0 ? rad.RadiusY : h * 0.5;
                if (rx <= 0) rx = 1.0;
                if (ry <= 0) ry = 1.0;
                double nx = (lx - rad.CenterX) / rx;
                double ny = (ly - rad.CenterY) / ry;
                double t = Math.Sqrt(nx * nx + ny * ny);
                // G13c: mask-side parity with the main radial sampling branch —
                // honor repeating-radial-gradient by wrapping t instead of
                // clamping past the radius.
                if (rad.IsRepeating) t = t - Math.Floor(t);
                else if (t > 1) t = 1;
                return gradient.Sample(t);
            }

            if (gradient is ConicGradient con) {
                return SampleConicWithRepeating(con, lx, ly);
            }

            return gradient.Stops.Count > 0 ? gradient.Stops[0].Color : LinearColor.Transparent;
        }

        // B17: sample the RGBA color from a url() mask image at the given (sx, sy)
        // coordinate within sampleBounds. The coordinate is in [0, tileDim) space
        // (after tile-wrapping has already been applied by SampleMaskLayerAlpha).
        //
        // Requires the brush's handle to resolve to an IRawPixelImageSource via the
        // imageRegistry. If the registry is null, the handle is unregistered, or the
        // source doesn't implement IRawPixelImageSource, we return fully transparent —
        // matching Chrome's behavior for a failed mask-image url (the layer acts as a
        // transparent black image so the element is hidden beneath it).
        //
        // Delegates to SampleRawImagePixel for the bilinear core — shared with
        // FillImageRect (background image path) so the sampling arithmetic is DRY.
        LinearColor SampleImageMaskPixel(string handle, Rect sampleBounds, double sx, double sy) {
            if (string.IsNullOrEmpty(handle) || imageRegistry == null)
                return LinearColor.Transparent;
            if (!imageRegistry.TryResolve(handle, out var source) || source == null)
                return LinearColor.Transparent;
            var raw = source as IRawPixelImageSource;
            if (raw == null || raw.Pixels == null || raw.Width <= 0 || raw.Height <= 0)
                return LinearColor.Transparent;

            // Map (sx, sy) within sampleBounds to normalized [0, 1).
            double bw = sampleBounds.Width > 0 ? sampleBounds.Width : 1.0;
            double bh = sampleBounds.Height > 0 ? sampleBounds.Height : 1.0;
            double u = (sx - sampleBounds.X) / bw;
            double v = (sy - sampleBounds.Y) / bh;

            return SampleRawImagePixel(raw.Pixels, raw.Width, raw.Height, u, v);
        }

        // Shared bilinear sampler for both the background (FillImageRect) and mask
        // (SampleImageMaskPixel) paths. Takes a normalized (u, v) in [0, 1] space
        // and returns the bilinear-interpolated RGBA as LinearColor in [0..1] range.
        //
        // u/v are clamped to [0, 1) before computing texel coords. The caller is
        // responsible for tile-wrapping prior to calling this method.
        //
        // Pixel channel values are byte (0..255) in the source buffer; we return them
        // as LinearColor divided by 255 — no sRGB decode. Both the mask and background
        // software paths treat raw byte values as linear for sampling consistency with
        // the GPU path.
        static LinearColor SampleRawImagePixel(byte[] imgPixels, int imgW, int imgH,
                                               double u, double v) {
            // Clamp to [0, 1) — tile-wrapping already happened upstream.
            if (u < 0) u = 0; else if (u >= 1) u = 1.0 - 1e-9;
            if (v < 0) v = 0; else if (v >= 1) v = 1.0 - 1e-9;

            // Convert to texel coordinates. Bilinear: sample four corners.
            double tx = u * (imgW - 1);
            double ty = v * (imgH - 1);
            int x0 = (int)Math.Floor(tx); if (x0 < 0) x0 = 0;
            int y0 = (int)Math.Floor(ty); if (y0 < 0) y0 = 0;
            int x1 = x0 + 1; if (x1 >= imgW) x1 = imgW - 1;
            int y1 = y0 + 1; if (y1 >= imgH) y1 = imgH - 1;
            double fx = tx - x0; // fractional part
            double fy = ty - y0;

            // Fetch four surrounding texels.
            var c00 = ReadTexel(imgPixels, imgW, x0, y0);
            var c10 = ReadTexel(imgPixels, imgW, x1, y0);
            var c01 = ReadTexel(imgPixels, imgW, x0, y1);
            var c11 = ReadTexel(imgPixels, imgW, x1, y1);

            // Bilinear blend.
            double r = (c00.R * (1 - fx) + c10.R * fx) * (1 - fy)
                     + (c01.R * (1 - fx) + c11.R * fx) * fy;
            double g = (c00.G * (1 - fx) + c10.G * fx) * (1 - fy)
                     + (c01.G * (1 - fx) + c11.G * fx) * fy;
            double b = (c00.B * (1 - fx) + c10.B * fx) * (1 - fy)
                     + (c01.B * (1 - fx) + c11.B * fx) * fy;
            double a = (c00.A * (1 - fx) + c10.A * fx) * (1 - fy)
                     + (c01.A * (1 - fx) + c11.A * fx) * fy;

            return new LinearColor((float)r, (float)g, (float)b, (float)a);
        }

        static LinearColor ReadTexel(byte[] pixels, int imgW, int x, int y) {
            int o = (y * imgW + x) * 4;
            return new LinearColor(
                pixels[o + 0] / 255f,
                pixels[o + 1] / 255f,
                pixels[o + 2] / 255f,
                pixels[o + 3] / 255f);
        }

        // ===== blur =====

        static void BoxBlurAlpha(byte[] buf, int w, int h, int radius) {
            if (radius <= 0) return;
            byte[] tmp = new byte[buf.Length];
            // Horizontal pass on alpha only.
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int total = 0;
                    int count = 0;
                    int x0 = x - radius; if (x0 < 0) x0 = 0;
                    int x1 = x + radius; if (x1 >= w) x1 = w - 1;
                    for (int xi = x0; xi <= x1; xi++) {
                        total += buf[(y * w + xi) * 4 + 3];
                        count++;
                    }
                    int o = (y * w + x) * 4;
                    tmp[o + 0] = buf[o + 0];
                    tmp[o + 1] = buf[o + 1];
                    tmp[o + 2] = buf[o + 2];
                    tmp[o + 3] = (byte)(total / count);
                }
            }
            // Vertical pass.
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int total = 0;
                    int count = 0;
                    int y0 = y - radius; if (y0 < 0) y0 = 0;
                    int y1 = y + radius; if (y1 >= h) y1 = h - 1;
                    for (int yi = y0; yi <= y1; yi++) {
                        total += tmp[(yi * w + x) * 4 + 3];
                        count++;
                    }
                    int o = (y * w + x) * 4;
                    buf[o + 0] = tmp[o + 0];
                    buf[o + 1] = tmp[o + 1];
                    buf[o + 2] = tmp[o + 2];
                    buf[o + 3] = (byte)(total / count);
                }
            }
        }

        // Maps a Gaussian σ (in pixels — CSS Filter Effects 1 §6.1 makes the
        // blur() <length> the σ directly) to the per-pass half-width of a 3-pass
        // box-blur approximation. Wells (1986) gives r ≈ σ · √3 / 2 for n = 3
        // boxes; we round to the nearest integer and clamp to ≥ 1 so blur radii
        // < ~0.6 px still apply at least one pass (matches the GPU path, which
        // always emits one shader pass). Exposed as internal for the parity
        // tests in Weva.Tests.Runtime.
        internal static int ComputeBoxBlurRadiusForSigma(double sigmaPx) {
            if (sigmaPx <= 0) return 0;
            // r = σ · √3 / 2 ≈ σ · 0.8660254. This composes 3 boxes whose total
            // variance equals σ² to ≤1% across the σ ∈ [1, 64] range, vs the
            // prior `σ / 3` rule which approximated σ_approx ≈ σ / 3.
            int r = (int)Math.Round(sigmaPx * Math.Sqrt(3.0) / 2.0);
            // Sub-pixel σ rounds to 0; clamp to a minimum 1 px box (mirrors the
            // previous behaviour and keeps blur() non-zero values visible).
            if (r < 1) r = 1;
            return r;
        }

        // Inverse mapping: given a per-pass box-blur half-width r, what Gaussian
        // σ does a 3-pass cascade approximate? Tests use this to confirm
        // ComputeBoxBlurRadiusForSigma round-trips within 20% (the spec-allowed
        // tolerance for a 3-box approximation).
        internal static double EstimateSigmaForBoxBlurRadius(int r) {
            if (r <= 0) return 0;
            // n=3 cascaded boxes of half-width r: σ² = 3 · ((2r+1)² − 1) / 12
            //                                        = ((2r+1)² − 1) / 4
            double width = 2.0 * r + 1.0;
            double variance = (width * width - 1.0) / 4.0;
            return Math.Sqrt(variance);
        }

        static void BoxBlurRgba(byte[] buf, int w, int h, int radius) {
            if (radius <= 0) return;
            byte[] tmp = new byte[buf.Length];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int rs = 0, gs = 0, bs = 0, asum = 0, count = 0;
                    int x0 = x - radius; if (x0 < 0) x0 = 0;
                    int x1 = x + radius; if (x1 >= w) x1 = w - 1;
                    for (int xi = x0; xi <= x1; xi++) {
                        int o = (y * w + xi) * 4;
                        rs += buf[o]; gs += buf[o + 1]; bs += buf[o + 2]; asum += buf[o + 3];
                        count++;
                    }
                    int t = (y * w + x) * 4;
                    tmp[t + 0] = (byte)(rs / count);
                    tmp[t + 1] = (byte)(gs / count);
                    tmp[t + 2] = (byte)(bs / count);
                    tmp[t + 3] = (byte)(asum / count);
                }
            }
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int rs = 0, gs = 0, bs = 0, asum = 0, count = 0;
                    int y0 = y - radius; if (y0 < 0) y0 = 0;
                    int y1 = y + radius; if (y1 >= h) y1 = h - 1;
                    for (int yi = y0; yi <= y1; yi++) {
                        int o = (yi * w + x) * 4;
                        rs += tmp[o]; gs += tmp[o + 1]; bs += tmp[o + 2]; asum += tmp[o + 3];
                        count++;
                    }
                    int t = (y * w + x) * 4;
                    buf[t + 0] = (byte)(rs / count);
                    buf[t + 1] = (byte)(gs / count);
                    buf[t + 2] = (byte)(bs / count);
                    buf[t + 3] = (byte)(asum / count);
                }
            }
        }

        static void ApplyBrightness(byte[] buf, double amount) {
            for (int i = 0; i < buf.Length; i += 4) {
                buf[i + 0] = ClampByte((int)Math.Round(buf[i + 0] * amount));
                buf[i + 1] = ClampByte((int)Math.Round(buf[i + 1] * amount));
                buf[i + 2] = ClampByte((int)Math.Round(buf[i + 2] * amount));
            }
        }

        static void ApplyContrast(byte[] buf, double amount) {
            // out = (in - 128) * amount + 128
            for (int i = 0; i < buf.Length; i += 4) {
                buf[i + 0] = ClampByte((int)Math.Round((buf[i + 0] - 128) * amount + 128));
                buf[i + 1] = ClampByte((int)Math.Round((buf[i + 1] - 128) * amount + 128));
                buf[i + 2] = ClampByte((int)Math.Round((buf[i + 2] - 128) * amount + 128));
            }
        }

        // CSS drop-shadow(): composite a blurred, tinted, offset alpha-stamp of the
        // foreground UNDER the foreground itself. The `mask` byte-buffer marks which
        // pixels were painted within this filter scope (1 byte per pixel, 0 or 255);
        // we cast a shadow only from those pixels and grow `mask` to also cover the
        // shadow's halo so the diff loop in PopFilter writes the halo through to the
        // framebuffer.
        static void ApplyDropShadow(byte[] buf, byte[] mask, int w, int h, DropShadowFilter ds) {
            byte[] shadowAlpha = new byte[w * h];
            int offX = (int)Math.Round(ds.OffsetX);
            int offY = (int)Math.Round(ds.OffsetY);
            // Stamp the foreground silhouette (from mask) into shadowAlpha at the offset.
            for (int y = 0; y < h; y++) {
                int sy = y - offY;
                if (sy < 0 || sy >= h) continue;
                for (int x = 0; x < w; x++) {
                    int sx = x - offX;
                    if (sx < 0 || sx >= w) continue;
                    byte m = mask[sy * w + sx];
                    if (m == 0) continue;
                    shadowAlpha[y * w + x] = m;
                }
            }

            int blurPx = (int)Math.Ceiling(ds.BlurRadius);
            if (blurPx > 0) {
                int radius = Math.Max(1, (int)Math.Round(blurPx / 3.0));
                BoxBlurAlpha8(shadowAlpha, w, h, radius);
                BoxBlurAlpha8(shadowAlpha, w, h, radius);
                BoxBlurAlpha8(shadowAlpha, w, h, radius);
            }

            byte cr = ToByte(ds.Color.R);
            byte cg = ToByte(ds.Color.G);
            byte cb = ToByte(ds.Color.B);
            double colorA = ds.Color.A;

            // Composite shadow UNDER the foreground silhouette (mask). For pixels covered
            // only by shadow, draw the shadow color directly. For pixels covered by both
            // foreground and shadow, the foreground (already in `buf`) stays on top — so
            // we only write where mask == 0. We also widen `mask` so the halo writes back.
            for (int i = 0; i < shadowAlpha.Length; i++) {
                byte sh = shadowAlpha[i];
                if (sh == 0) continue;
                int o = i * 4;
                if (mask[i] == 0) {
                    double shA = (sh / 255.0) * colorA;
                    if (shA <= 0) continue;
                    double dstR = buf[o + 0] / 255.0;
                    double dstG = buf[o + 1] / 255.0;
                    double dstB = buf[o + 2] / 255.0;
                    double dstA = buf[o + 3] / 255.0;
                    double srcR = cr / 255.0;
                    double srcG = cg / 255.0;
                    double srcB = cb / 255.0;
                    double outA = shA + dstA * (1.0 - shA);
                    if (outA <= 0) continue;
                    double outR = (srcR * shA + dstR * dstA * (1.0 - shA)) / outA;
                    double outG = (srcG * shA + dstG * dstA * (1.0 - shA)) / outA;
                    double outB = (srcB * shA + dstB * dstA * (1.0 - shA)) / outA;
                    buf[o + 0] = ToByte01(outR);
                    buf[o + 1] = ToByte01(outG);
                    buf[o + 2] = ToByte01(outB);
                    buf[o + 3] = ToByte01(outA);
                    mask[i] = 255;
                }
            }
        }

        static void BoxBlurAlpha8(byte[] buf, int w, int h, int radius) {
            if (radius <= 0) return;
            byte[] tmp = new byte[buf.Length];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int total = 0, count = 0;
                    int x0 = x - radius; if (x0 < 0) x0 = 0;
                    int x1 = x + radius; if (x1 >= w) x1 = w - 1;
                    for (int xi = x0; xi <= x1; xi++) { total += buf[y * w + xi]; count++; }
                    tmp[y * w + x] = (byte)(total / count);
                }
            }
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int total = 0, count = 0;
                    int y0 = y - radius; if (y0 < 0) y0 = 0;
                    int y1 = y + radius; if (y1 >= h) y1 = h - 1;
                    for (int yi = y0; yi <= y1; yi++) { total += tmp[yi * w + x]; count++; }
                    buf[y * w + x] = (byte)(total / count);
                }
            }
        }

        static void ApplyGrayscale(byte[] buf, double amount) {
            // amount=0 -> identity; amount=1 -> full luma. CSS uses Rec. 601 weights.
            double t = amount;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            for (int i = 0; i < buf.Length; i += 4) {
                int r = buf[i + 0];
                int g = buf[i + 1];
                int b = buf[i + 2];
                double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                buf[i + 0] = ClampByte((int)Math.Round(r * (1 - t) + luma * t));
                buf[i + 1] = ClampByte((int)Math.Round(g * (1 - t) + luma * t));
                buf[i + 2] = ClampByte((int)Math.Round(b * (1 - t) + luma * t));
            }
        }

        // ===== misc =====

        byte[] SnapshotRegion(int x, int y, int w, int h) {
            byte[] buf = new byte[w * h * 4];
            for (int dy = 0; dy < h; dy++) {
                int srcRow = ((y + dy) * width + x) * 4;
                int dstRow = (dy * w) * 4;
                if (x < 0 || x + w > width || y + dy < 0 || y + dy >= height) {
                    // Out-of-bounds rows stay zero.
                    continue;
                }
                Buffer.BlockCopy(pixels, srcRow, buf, dstRow, w * 4);
            }
            return buf;
        }

        (double X, double Y) TransformPoint(double x, double y) {
            return transformStack.Peek().Apply(x, y);
        }

        static byte ToByte(float linear) {
            // Linear -> sRGB byte using IEC 61966-2-1 piecewise curve, matching LinearColor's
            // inverse so encode -> decode round-trips bit-exactly for the 256 sRGB values.
            float v = linear < 0 ? 0 : (linear > 1 ? 1 : linear);
            float srgb = v <= 0.0031308f
                ? 12.92f * v
                : 1.055f * (float)Math.Pow(v, 1.0 / 2.4) - 0.055f;
            int b = (int)Math.Round(srgb * 255.0, MidpointRounding.AwayFromZero);
            if (b < 0) b = 0; else if (b > 255) b = 255;
            return (byte)b;
        }

        static byte ToByte01(double v) {
            int b = (int)Math.Round(v * 255.0, MidpointRounding.AwayFromZero);
            if (b < 0) b = 0; else if (b > 255) b = 255;
            return (byte)b;
        }

        static byte ClampByte(int v) {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        readonly struct ClipRect {
            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;
            public ClipRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
            public int Right => X + W;
            public int Bottom => Y + H;
        }

        readonly struct FilterFrame {
            public readonly FilterChain Filters;
            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;
            public readonly byte[] Before;

            public FilterFrame(FilterChain filters, int x, int y, int w, int h, byte[] before) {
                Filters = filters;
                X = x; Y = y; W = w; H = h;
                Before = before;
            }
        }

        readonly struct MaskFrame {
            public readonly MaskDefinition Mask;
            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;
            public readonly byte[] Before;

            public MaskFrame(MaskDefinition mask, int x, int y, int w, int h, byte[] before) {
                Mask = mask;
                X = x; Y = y; W = w; H = h;
                Before = before;
            }
        }
    }
}
