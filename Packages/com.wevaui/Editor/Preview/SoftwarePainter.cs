using System;
using System.Collections.Generic;
using UnityEngine;
using Weva.Paint;
using Rect = Weva.Paint.Rect;

namespace Weva.EditorTools.Preview {
    // TEMP: minimal CPU rasterizer used by the editor preview while the URP backend is
    // being brought up. Pure C# (no UnityEditor, no Graphics) — exercised by headless
    // tests in Tests/Editor/Preview/. TODO(v1.x): swap in URPRenderBackend once it is
    // valid in editor (PLAN §9 #12, end of §11 "What's left of v1").
    //
    // Implements the IRenderBackend surface defined in Runtime/Paint/IRenderBackend.cs.
    // For v1 we only paint solid rectangles, border outlines, and a placeholder rect
    // for text bounds. Shadows / filters / gradients / transforms / clips are tracked
    // as a stack (so push/pop is balanced) but their visual contribution is skipped
    // and a single TODO outline is drawn over the affected region.
    public sealed class SoftwarePainter : IRenderBackend {
        readonly Color32[] pixels;
        readonly int width;
        readonly int height;
        readonly Stack<RectI> clipStack = new Stack<RectI>();
        readonly Stack<float> opacityStack = new Stack<float>();
        int transformDepth;
        int filterDepth;

        public SoftwarePainter(int width, int height) {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            this.width = width;
            this.height = height;
            pixels = new Color32[width * height];
            clipStack.Push(new RectI(0, 0, width, height));
            opacityStack.Push(1f);
        }

        public int Width => width;
        public int Height => height;
        public Color32[] Pixels => pixels;

        public void Clear(Color32 color) {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
        }

        public void FillRect(RectI rect, Color32 color) {
            var clip = clipStack.Peek();
            int x0 = Math.Max(rect.X, clip.X);
            int y0 = Math.Max(rect.Y, clip.Y);
            int x1 = Math.Min(rect.X + rect.W, clip.X + clip.W);
            int y1 = Math.Min(rect.Y + rect.H, clip.Y + clip.H);
            if (x1 <= x0 || y1 <= y0) return;
            float opacity = opacityStack.Peek();
            for (int y = y0; y < y1; y++) {
                int rowBase = y * width;
                for (int x = x0; x < x1; x++) {
                    pixels[rowBase + x] = BlendPremultiplied(pixels[rowBase + x], color, opacity);
                }
            }
        }

        public void StrokeRect(RectI rect, int thickness, Color32 color) {
            if (thickness <= 0) return;
            int t = Math.Max(1, thickness);
            FillRect(new RectI(rect.X, rect.Y, rect.W, t), color);
            FillRect(new RectI(rect.X, rect.Y + rect.H - t, rect.W, t), color);
            FillRect(new RectI(rect.X, rect.Y, t, rect.H), color);
            FillRect(new RectI(rect.X + rect.W - t, rect.Y, t, rect.H), color);
        }

        public Texture2D ToTexture2D() {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Point;
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        public void UploadInto(Texture2D target) {
            if (target == null) return;
            if (target.width != width || target.height != height) {
                target.Reinitialize(width, height, TextureFormat.RGBA32, false);
            }
            target.SetPixels32(pixels);
            target.Apply(false, false);
        }

        public void Submit(FillRectCommand command) {
            if (command.Brush == null) return;
            Color32 c;
            if (command.Brush.Kind == BrushKind.SolidColor) {
                c = LinearToColor32(command.Brush.Color);
            } else {
                // TODO: gradients/images not supported in v1 software path. Fall back to
                // a neutral fill so authors at least see "something is here".
                c = new Color32(180, 180, 180, 255);
            }
            FillRect(ToRectI(command.Bounds), c);
        }

        public void Submit(StrokeBorderCommand command) {
            var b = command.Borders;
            if (b.IsNone) return;
            var rect = ToRectI(command.Bounds);
            // Approximate per-edge widths with the max edge width (v1 simplification —
            // the proper paint converter resolves them but the software painter doesn't
            // need pixel parity yet).
            int t = (int)Math.Round(Math.Max(Math.Max(b.Top.Width, b.Bottom.Width), Math.Max(b.Left.Width, b.Right.Width)));
            if (t <= 0) t = 1;
            // Use the top edge color as a representative; v1 doesn't paint mismatched
            // per-side colors. The cascade emits one dominant border-color for most cases.
            var color = LinearToColor32(b.Top.Color);
            if (color.a == 0) color = LinearToColor32(b.Left.Color);
            if (color.a == 0) color = new Color32(0, 0, 0, 255);
            StrokeRect(rect, t, color);
        }

        public void Submit(DrawTextCommand command) {
            // v1 placeholder: outline the text-run bounds so authors can see the layout
            // shape. Real glyph rendering requires TextCore, which the URP backend will
            // own once it's wired up.
            var rect = ToRectI(command.Bounds);
            if (rect.W <= 0 || rect.H <= 0) return;
            var color = LinearToColor32(command.Color);
            if (color.a == 0) color = new Color32(120, 120, 120, 255);
            // Tinted fill at low alpha so adjacent text runs don't merge visually.
            var fill = new Color32(color.r, color.g, color.b, (byte)Math.Min(64, (int)color.a));
            FillRect(rect, fill);
            StrokeRect(rect, 1, color);
        }

        public void Submit(DrawShadowCommand command) {
            // TODO(v1.x): real shadow blur. v1 just sketches an outline at the offset.
            var rect = ToRectI(command.Bounds);
            StrokeRect(rect, 1, new Color32(0, 0, 0, 64));
        }

        public void Submit(PushClipCommand command) {
            var rect = ToRectI(command.Bounds);
            var current = clipStack.Peek();
            int x0 = Math.Max(rect.X, current.X);
            int y0 = Math.Max(rect.Y, current.Y);
            int x1 = Math.Min(rect.X + rect.W, current.X + current.W);
            int y1 = Math.Min(rect.Y + rect.H, current.Y + current.H);
            int w = Math.Max(0, x1 - x0);
            int h = Math.Max(0, y1 - y0);
            clipStack.Push(new RectI(x0, y0, w, h));
        }

        public void Submit(PopClipCommand command) {
            if (clipStack.Count > 1) clipStack.Pop();
        }

        public void Submit(PushOpacityCommand command) {
            float current = opacityStack.Peek();
            opacityStack.Push(current * Mathf.Clamp01((float)command.Opacity));
        }

        public void Submit(PopOpacityCommand command) {
            if (opacityStack.Count > 1) opacityStack.Pop();
        }

        // Transforms / filters: tracked so push/pop is balanced, but their visual
        // effect is intentionally skipped in v1.
        public void Submit(PushTransformCommand command) { transformDepth++; }
        public void Submit(PopTransformCommand command)  { if (transformDepth > 0) transformDepth--; }
        public void Submit(PushFilterCommand command)    { filterDepth++; }
        public void Submit(PopFilterCommand command)     { if (filterDepth > 0) filterDepth--; }
        public void Submit(BeginSubtreeCaptureCommand command) { }
        public void Submit(EndSubtreeCaptureCommand command) { }
        public void Submit(ReplaySubtreeSnapshotCommand command) { }

        static RectI ToRectI(Rect r) {
            int x = (int)Math.Floor(r.X);
            int y = (int)Math.Floor(r.Y);
            int w = (int)Math.Ceiling(r.Right) - x;
            int h = (int)Math.Ceiling(r.Bottom) - y;
            if (w < 0) w = 0;
            if (h < 0) h = 0;
            return new RectI(x, y, w, h);
        }

        static Color32 LinearToColor32(LinearColor c) {
            byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToSrgb(c.R) * 255f), 0, 255);
            byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToSrgb(c.G) * 255f), 0, 255);
            byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToSrgb(c.B) * 255f), 0, 255);
            byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(c.A * 255f), 0, 255);
            return new Color32(r, g, b, a);
        }

        static float LinearToSrgb(float v) {
            if (v <= 0.0031308f) return Mathf.Max(0f, 12.92f * v);
            return 1.055f * Mathf.Pow(Mathf.Max(v, 0f), 1f / 2.4f) - 0.055f;
        }

        static Color32 BlendPremultiplied(Color32 dst, Color32 src, float opacity) {
            float aSrc = (src.a / 255f) * Mathf.Clamp01(opacity);
            if (aSrc <= 0f) return dst;
            if (aSrc >= 1f && opacity >= 1f) return src;
            float aDst = dst.a / 255f;
            float aOut = aSrc + aDst * (1f - aSrc);
            if (aOut <= 0f) return new Color32(0, 0, 0, 0);
            float r = (src.r * aSrc + dst.r * aDst * (1f - aSrc)) / aOut;
            float g = (src.g * aSrc + dst.g * aDst * (1f - aSrc)) / aOut;
            float b = (src.b * aSrc + dst.b * aDst * (1f - aSrc)) / aOut;
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(aOut * 255f), 0, 255));
        }

        public readonly struct RectI {
            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;

            public RectI(int x, int y, int w, int h) {
                X = x;
                Y = y;
                W = w;
                H = h;
            }
        }
    }
}
