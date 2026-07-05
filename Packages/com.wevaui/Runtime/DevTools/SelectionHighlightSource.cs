using Weva.Documents;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Rendering;

namespace Weva.DevTools {
    // Chrome DevTools box-model selection highlight — IUIPaintSource that draws
    // four semi-transparent tinted bands (margin/border/padding/content) over the
    // selected element's box, mirroring Chrome's DevTools inspect overlay.
    //
    // Registered/unregistered by ElementsWindow; never serialized into the scene.
    // EmitPaint is defensive: if the target box/state becomes stale post-rebuild,
    // it clears the target and emits nothing rather than throwing.
    //
    // Color conventions (Chrome palette, sRGB):
    //   Content:  rgba(111, 168, 220, 0.66)  — blue
    //   Padding:  rgba(147, 196, 125, 0.55)  — green
    //   Border:   rgba(255, 229, 153, 0.60)  — yellow
    //   Margin:   rgba(246, 178, 107, 0.66)  — orange
    public sealed class SelectionHighlightSource : IUIPaintSource {
        // Draws on top of all document paint (int.MaxValue - 1 to leave headroom
        // for other overlay sources at MaxValue).
        public int Order => int.MaxValue - 1;

        // Whether the target has changed since the last EmitPaint call.
        // True forces a repaint; set false after emitting.
        bool dirty;
        bool hasEmitted;

        Box targetBox;
        UIDocumentState targetState;

        // Live pick-mode hover preview (Chrome: while the picker is armed,
        // mousing over the page highlights the hovered element; the committed
        // selection's overlay is suppressed until the hover clears).
        Box hoverBox;
        UIDocumentState hoverState;

        // Colors in linear space (pre-converted from sRGB).
        // sRGB -> linear: (v / 255)^2.2 approximation for display values.
        // Using full IEC 61966-2-1 curve isn't necessary for overlay tints;
        // we use Mathf.GammaToLinearSpace equivalent values, baked as constants.
        // Alphas tuned DOWN from Chrome's nominal values (0.55-0.66): with
        // the engine's compositing the element underneath must stay clearly
        // readable through the tint (user feedback — the selection hid the
        // element entirely).
        // Content: sRGB(111,168,220) @ 0.28
        static readonly LinearColor ContentColor  = SrgbToLinear(111, 168, 220, 0.28f);
        // Padding: sRGB(147,196,125) @ 0.35
        static readonly LinearColor PaddingColor  = SrgbToLinear(147, 196, 125, 0.35f);
        // Border: sRGB(255,229,153) @ 0.40
        static readonly LinearColor BorderColor   = SrgbToLinear(255, 229, 153, 0.40f);
        // Margin: sRGB(246,178,107) @ 0.35
        static readonly LinearColor MarginColor   = SrgbToLinear(246, 178, 107, 0.35f);

        // Set the target box + document state. Pass null to clear the highlight.
        // The EditorWindow calls this when the tree selection changes.
        public void SetTarget(Box box, UIDocumentState state) {
            targetBox = box;
            targetState = state;
            dirty = true;
        }

        public void ClearTarget() {
            targetBox = null;
            targetState = null;
            dirty = true;
        }

        public bool HasTarget => targetBox != null;

        // Set the hover-preview box (pick mode mouse-over). No-ops when the
        // hover hasn't actually changed so per-pointer-move calls don't
        // defeat the renderer's idle-frame batch reuse.
        public void SetHover(Box box, UIDocumentState state) {
            if (box == hoverBox && state == hoverState) return;
            hoverBox = box;
            hoverState = state;
            dirty = true;
        }

        public void ClearHover() {
            if (hoverBox == null && hoverState == null) return;
            hoverBox = null;
            hoverState = null;
            dirty = true;
        }

        public bool HasHover => hoverBox != null;

        // IUIPaintSource.NeedsRepaint: true when target changed or first-ever emit.
        // Once emitted, false until next SetTarget/ClearTarget call.
        public bool NeedsRepaint {
            get {
                if (!hasEmitted) return true;
                return dirty;
            }
        }

        // IUIPaintSource.EmitPaint: emit four FillRect commands for the selected
        // box's margin/border/padding/content bands using the Chrome palette.
        public void EmitPaint(IRenderBackend backend) {
            dirty = false;
            hasEmitted = true;

            // Hover preview wins over the committed selection while present
            // (Chrome shows only the hovered element's overlay during pick).
            // Each candidate is validated defensively: if the document was
            // rebuilt the old box tree is orphaned — detected via a null
            // state or a box whose Element was cleared by the pool reclaim
            // path — and the stale pair is dropped rather than throwing.
            if (!TryResolveTarget(ref hoverBox, ref hoverState, out Box box) &&
                !TryResolveTarget(ref targetBox, ref targetState, out box)) {
                return;
            }

            // Compute absolute position (same convention as BoxModelNumbers).
            double absX = 0, absY = 0;
            for (var b = box; b != null; b = b.Parent) {
                absX += b.X + b.StickyOffsetX;
                absY += b.Y + b.StickyOffsetY;
                if (b.Style != null) {
                    var xf = Weva.Paint.Conversion.TransformResolver.ResolveTransform(
                        b.Style, b.Width, b.Height);
                    if (xf.Tx != 0f || xf.Ty != 0f) {
                        absX += xf.Tx;
                        absY += xf.Ty;
                    }
                }
            }

            // Margin box (outermost)
            double marginX = absX - box.MarginLeft;
            double marginY = absY - box.MarginTop;
            double marginW = box.Width  + box.MarginLeft + box.MarginRight;
            double marginH = box.Height + box.MarginTop  + box.MarginBottom;

            // Border box
            double borderX = absX;
            double borderY = absY;
            double borderW = box.Width;
            double borderH = box.Height;

            // Padding box (inside border)
            double paddingX = absX + box.BorderLeft;
            double paddingY = absY + box.BorderTop;
            double paddingW = System.Math.Max(0, borderW - box.BorderLeft - box.BorderRight);
            double paddingH = System.Math.Max(0, borderH - box.BorderTop  - box.BorderBottom);

            // Content box (inside padding)
            double contentX = paddingX + box.PaddingLeft;
            double contentY = paddingY + box.PaddingTop;
            double contentW = System.Math.Max(0, paddingW - box.PaddingLeft - box.PaddingRight);
            double contentH = System.Math.Max(0, paddingH - box.PaddingTop  - box.PaddingBottom);

            // Emit EXCLUSIVE bands (each region only painted once). The first
            // cut stacked full rects, so content pixels composited margin +
            // border + padding + content alphas — near-opaque, hiding the
            // element underneath (user report). Chrome paints non-overlapping
            // regions; mirror that with 4 edge strips per ring + the content
            // rect alone.
            EmitRing(backend, marginX, marginY, marginW, marginH,
                     borderX, borderY, borderW, borderH, MarginColor);
            EmitRing(backend, borderX, borderY, borderW, borderH,
                     paddingX, paddingY, paddingW, paddingH, BorderColor);
            EmitRing(backend, paddingX, paddingY, paddingW, paddingH,
                     contentX, contentY, contentW, contentH, PaddingColor);
            EmitFill(backend, contentX, contentY, contentW, contentH, ContentColor);
        }

        // Validates a (box, state) candidate pair, clearing it in place when
        // stale. Returns true with the box when it is paintable.
        static bool TryResolveTarget(ref Box candidate, ref UIDocumentState state, out Box box) {
            box = null;
            if (candidate == null) return false;
            if (state == null || candidate.Element == null) {
                candidate = null;
                state = null;
                return false;
            }
            box = candidate;
            return true;
        }

        // Paints the area between an outer rect and an inner rect as four
        // strips (top, bottom, left, right) — no overlap with the inner rect.
        static void EmitRing(IRenderBackend backend,
                             double ox, double oy, double ow, double oh,
                             double ix, double iy, double iw, double ih,
                             LinearColor color) {
            if (ow <= 0 || oh <= 0) return;
            if (iw <= 0 || ih <= 0) {
                // Inner collapsed — the ring IS the outer rect.
                EmitFill(backend, ox, oy, ow, oh, color);
                return;
            }
            double top = iy - oy;
            double bottom = (oy + oh) - (iy + ih);
            double left = ix - ox;
            double right = (ox + ow) - (ix + iw);
            if (top > 0)    EmitFill(backend, ox, oy, ow, top, color);
            if (bottom > 0) EmitFill(backend, ox, iy + ih, ow, bottom, color);
            if (left > 0)   EmitFill(backend, ox, iy, left, ih, color);
            if (right > 0)  EmitFill(backend, ix + iw, iy, right, ih, color);
        }

        static void EmitFill(IRenderBackend backend, double x, double y, double w, double h,
                             LinearColor color) {
            if (w <= 0 || h <= 0) return;
            var rect = new Rect(x, y, w, h);
            var brush = Brush.SolidColor(color);
            var cmd = new FillRectCommand(rect, brush, BorderRadii.Zero);
            backend.Submit(cmd);
        }

        // sRGB byte channels (0-255) + linear alpha (0-1) → LinearColor.
        // Uses the proper IEC 61966-2-1 piecewise transfer function.
        static LinearColor SrgbToLinear(byte r, byte g, byte b, float a) {
            return new LinearColor(Channel(r), Channel(g), Channel(b), a);
        }

        static float Channel(byte b) {
            float v = b / 255f;
            if (v <= 0.04045f) return v / 12.92f;
            return (float)System.Math.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
    }
}
