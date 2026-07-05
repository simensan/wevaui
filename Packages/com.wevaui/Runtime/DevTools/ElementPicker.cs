using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Paint.Conversion;

namespace Weva.DevTools {
    // DevTools W7 phase 1 — element picker.
    //
    // Resolves a (x, y) document-space point to the deepest element-owning Box
    // whose border-box contains the point, respecting CSS translate transforms
    // the same way BoxOutlineRenderer does (Tx/Ty from TransformResolver). Full
    // rotate/scale hit-testing is out of scope for v1 — we apply Tx/Ty so the
    // overlay at minimum places the highlight at the correct translation-adjusted
    // origin; skewed/rotated elements will show a best-effort axis-aligned rect.
    //
    // Rather than duplicating BoxTreeHitTester's transform-aware walk, we
    // REUSE it through the IHitTester contract for the element lookup, then do
    // a second lightweight walk on the same box tree to retrieve the Box for the
    // resolved element. The second walk is O(depth) and only fires when the
    // caller asks for the box (Dump path); hit-testing alone is a single call.
    //
    // TextRuns are skipped automatically: they share their parent Element pointer
    // and the hit-tester already excludes them from element resolution.
    public sealed class ElementPicker {
        // Last element resolved by the most recent Pick call.
        public Element LastElement { get; private set; }

        // Last box resolved. Null when Pick found no element, or when the
        // ElementToBox lookup supplied by the caller returned null.
        public Box LastBox { get; private set; }

        // Resolve a point to the deepest element-owning box. The supplied
        // hitTester (typically BoxTreeHitTester) handles transform-aware lookup;
        // elementToBox maps the resolved element back to its primary Box so the
        // box-model dump can read margin/border/padding/content geometry.
        //
        // Returns the resolved element (or null on miss). LastElement and
        // LastBox are set for the duration until the next Pick call.
        public Element Pick(IHitTester hitTester,
                            double x, double y,
                            System.Func<Element, Box> elementToBox) {
            LastElement = null;
            LastBox = null;
            if (hitTester == null) return null;
            var element = hitTester.HitTest(x, y);
            if (element == null) return null;
            LastElement = element;
            if (elementToBox != null) {
                LastBox = elementToBox(element);
            }
            return element;
        }

        // Variant: pick directly from a Box tree root without a pre-built
        // BoxTreeHitTester instance — convenience overload for tests and
        // one-shot inspection calls.
        public Element Pick(Box root, double x, double y,
                            System.Func<Element, Box> elementToBox,
                            Layout.Scrolling.ScrollContainer scrollContainer = null) {
            if (root == null) return null;
            var hitTester = new BoxTreeHitTester(root, scrollContainer);
            return Pick(hitTester, x, y, elementToBox);
        }

        // Walk a Box tree and return the deepest element-owning Box whose
        // absolute border-box contains (x, y), applying the same Tx/Ty
        // transform adjustments as BoxOutlineRenderer. Used internally and
        // exposed for tests that need to assert on Box geometry directly.
        //
        // Unlike BoxTreeHitTester this returns a Box, not an Element. It does
        // NOT honour pointer-events:none or visibility:hidden — the DevTools
        // inspector should be able to pick hidden elements when the user
        // explicitly requests them. Callers that want the event-routed element
        // should use IHitTester.HitTest instead.
        public static Box PickBox(Box root, double x, double y) {
            if (root == null) return null;
            return PickBoxRecursive(root, x, y, 0, 0);
        }

        static Box PickBoxRecursive(Box box, double x, double y,
                                    double parentAbsX, double parentAbsY) {
            if (box == null) return null;

            // Accumulate absolute position the same way BoxOutlineRenderer does.
            double absX = parentAbsX + box.X + box.StickyOffsetX;
            double absY = parentAbsY + box.Y + box.StickyOffsetY;

            // CSS Transforms §6 — apply Tx/Ty of any translate/transform on
            // this box so the DevTools highlight lands at the painted origin.
            // Rotate and scale are noted as out-of-scope for v1 (we still
            // apply their Tx/Ty translation component for partial correctness).
            if (box.Style != null) {
                var xf = TransformResolver.ResolveTransform(box.Style, box.Width, box.Height);
                if (xf.Tx != 0f || xf.Ty != 0f) {
                    absX += xf.Tx;
                    absY += xf.Ty;
                }
            }

            // Test border-box containment.
            bool contained = x >= absX && x < absX + box.Width
                          && y >= absY && y < absY + box.Height;
            if (!contained) return null;

            // Depth-first: children are painted on top and should be preferred.
            // Walk in reverse paint order (last child = topmost).
            var kids = box.Children;
            for (int i = kids.Count - 1; i >= 0; i--) {
                var hit = PickBoxRecursive(kids[i], x, y, absX, absY);
                if (hit != null) return hit;
            }

            // Skip TextRuns (they share the element pointer and have no
            // independent box-model layout). Skip anonymous boxes when no
            // element is attached — they are internal layout artefacts.
            if (box is TextRun) return null;
            if (box.Element == null) return null;
            return box;
        }
    }
}
