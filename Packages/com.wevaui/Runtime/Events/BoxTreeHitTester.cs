using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using Weva.Layout.Scrolling;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Profiling;

namespace Weva.Events {
    public sealed class BoxTreeHitTester : IHitTester {
        readonly Box root;
        readonly ScrollContainer scrollContainer;

        public BoxTreeHitTester(Box root) : this(root, null) { }

        public BoxTreeHitTester(Box root, ScrollContainer scrollContainer) {
            this.root = root;
            this.scrollContainer = scrollContainer;
        }

        public Element HitTest(double x, double y) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.HitTest)) {
                if (root == null) return null;
                if (!Contains(root, x, y)) return null;
                return HitTestBox(root, x, y, 0, 0);
            }
        }

        Element HitTestBox(Box box, double x, double y, double offsetX, double offsetY) {
            // Coordinate model: Box.X/Box.Y are stored LOCAL to the parent.
            // We descend by accumulating the parent chain's positions in
            // (offsetX, offsetY) and comparing the document-space test point
            // (x, y) against the child's absolute rect (offsetX + child.X,
            // offsetY + child.Y). Scroll containers add an extra shift —
            // scrollY > 0 means content moved UP by scrollY, so the hit test
            // sees descendants at (X, Y - scrollY) in document space.
            //
            // CSS Transforms: when the box has a CSS transform, the visual
            // position differs from the layout position. We inverse-transform
            // the test point into the box's local coordinate space so
            // hit-testing matches the painted location. The transform-origin
            // is the box's border-box center by default.
            ScrollState state = scrollContainer != null ? scrollContainer.Get(box) : null;

            double childOffsetX = offsetX + box.X + box.StickyOffsetX;
            double childOffsetY = offsetY + box.Y + box.StickyOffsetY;
            if (HasTransform(box)) {
                var xf = TransformResolver.ResolveTransform(box.Style, box.Width, box.Height);
                childOffsetX += xf.Tx;
                childOffsetY += xf.Ty;
            }
            if (state != null) {
                childOffsetX -= state.ScrollX;
                childOffsetY -= state.ScrollY;
            }

            // CSS Containment L2 §4.2: content-visibility:hidden contents are
            // not hit-testable — they are skipped as if they had no boxes.
            // The ELEMENT itself (box) is still hittable; only its descendants
            // are excluded.  We check before the child loop so we don't descend.
            bool cvHidden = ContainmentResolver.IsContentVisibilityHidden(box.Style);

            if (!cvHidden) {
                for (int i = box.Children.Count - 1; i >= 0; i--) {
                    var child = box.Children[i];
                    if (ContainsAbsolute(child, x, y, childOffsetX, childOffsetY)) {
                        var deeper = HitTestBox(child, x, y, childOffsetX, childOffsetY);
                        if (deeper != null) return deeper;
                        // pointer-events: none — the child box is transparent to
                        // hit-testing as a target. Children of the child were
                        // already given a chance via the recursive call above; if
                        // they declined we fall through to the next sibling rather
                        // than selecting this child itself. visibility: hidden /
                        // collapse likewise removes the box from the hit-test
                        // target set per CSS UI 4 §9 (children with `visibility:
                        // visible` remained selectable through the recursive call
                        // above). opacity:0 is intentionally NOT skipped here —
                        // CSS Pointer Events 1 keeps fully-transparent elements
                        // hittable; authors opt out via pointer-events:none.
                        if (child.Element != null
                            && !IsPointerEventsNone(child)
                            && !IsVisibilityHidden(child)) return child.Element;
                    }
                }
            }
            // Likewise, the box itself is not selectable when it opted out via
            // pointer-events: none, or when `visibility: hidden` / `collapse`
            // removes it from the target set. Returning null here lets our
            // caller (the parent's loop) try the next sibling, mirroring web
            // behavior.
            if (IsPointerEventsNone(box) || IsVisibilityHidden(box)) return null;
            return box.Element;
        }

        static bool IsPointerEventsNone(Box box) {
            if (box?.Style == null) return false;
            // Id-indexed read: avoids a string→id dictionary lookup per call.
            string v = box.Style.Get(Weva.Css.Cascade.CssProperties.PointerEventsId);
            if (string.IsNullOrEmpty(v)) return false;
            return v.Trim().Equals("none", System.StringComparison.OrdinalIgnoreCase);
        }

        // CSS UI 4 §9: `visibility: hidden` (and `collapse`) makes the box
        // transparent to hit-testing. Children whose own visibility is
        // `visible` remain hittable — the recursive walk in HitTestBox
        // already gave them a chance before this check fires for the
        // ancestor. opacity:0 is intentionally not consulted: per CSS
        // Pointer Events 1 a fully-transparent element still receives
        // events (authors opt out via pointer-events:none).
        static bool IsVisibilityHidden(Box box) {
            if (box?.Style == null) return false;
            // Id-indexed read: avoids a string→id dictionary lookup per call.
            string v = box.Style.Get(Weva.Css.Cascade.CssProperties.VisibilityId);
            if (string.IsNullOrEmpty(v)) return false;
            string trimmed = v.Trim();
            return trimmed.Equals("hidden", System.StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("collapse", System.StringComparison.OrdinalIgnoreCase);
        }

        // Box.X / Box.Y are local-to-parent. The caller passes the parent's
        // accumulated absolute origin (offsetX, offsetY) so we can compute
        // the child's absolute rect and test the document-space point
        // against it. Sticky offsets are a paint-time translation; we
        // mirror that here so hit-testing matches the visual location.
        static bool ContainsAbsolute(Box box, double x, double y, double offsetX, double offsetY) {
            double bx = offsetX + box.X + box.StickyOffsetX;
            double by = offsetY + box.Y + box.StickyOffsetY;
            if (HasTransform(box)) {
                var xf = TransformResolver.ResolveTransform(box.Style, box.Width, box.Height);
                bx += xf.Tx;
                by += xf.Ty;
            }
            return x >= bx && x < bx + box.Width
                && y >= by && y < by + box.Height;
        }

        static bool Contains(Box box, double x, double y) {
            return x >= box.X && x < box.X + box.Width
                && y >= box.Y && y < box.Y + box.Height;
        }

        static bool HasTransform(Box box) {
            if (box?.Style == null) return false;
            // Id-indexed read — the string-name overload pays a GetId
            // dictionary lookup per hit-tested node per frame (same hot-path
            // fix as IsPointerEventsNone/IsVisibilityHidden).
            string raw = box.Style.Get(Weva.Css.Cascade.CssProperties.TransformId);
            return !string.IsNullOrEmpty(raw) && raw != "none";
        }

        static void ResolveTransformOrigin(string raw, double w, double h, out double ox, out double oy) {
            ox = w * 0.5;
            oy = h * 0.5;
            if (string.IsNullOrEmpty(raw)) return;
            var parts = raw.Split(' ');
            if (parts.Length >= 1) ox = ResolveOriginPart(parts[0], w);
            if (parts.Length >= 2) oy = ResolveOriginPart(parts[1], h);
        }

        static double ResolveOriginPart(string part, double basis) {
            part = part.Trim();
            if (part == "left" || part == "top") return 0;
            if (part == "right" || part == "bottom") return basis;
            if (part == "center") return basis * 0.5;
            if (part.EndsWith("%") && double.TryParse(part.Substring(0, part.Length - 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pct))
                return basis * pct * 0.01;
            if (part.EndsWith("px") && double.TryParse(part.Substring(0, part.Length - 2),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double px))
                return px;
            return basis * 0.5;
        }
    }
}
