using System.Collections.Generic;
using NUnit.Framework;
using Weva.DevTools;
using Weva.Documents;
using Weva.Layout.Boxes;
using Weva.Paint;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.DevTools {
    // SelectionHighlightSource — the Chrome DevTools box-model overlay.
    // Pins the selection bands, the pick-mode HOVER preview (hover wins over
    // the committed selection while present — Chrome shows only the hovered
    // element's overlay during pick), the NeedsRepaint dirty contract that
    // the renderer's idle-frame batch reuse depends on, and the defensive
    // stale-target handling.
    public class SelectionHighlightSourceTests {

        static Box FindBoxById(Box b, string id) {
            if (b == null) return null;
            if (b.Element != null && b.Element.Id == id) return b;
            foreach (var c in b.Children) {
                var r = FindBoxById(c, id);
                if (r != null) return r;
            }
            return null;
        }

        // Two sibling blocks at distinct positions/sizes so the hovered and
        // selected overlays are geometrically distinguishable.
        static (Box sel, Box hov, UIDocumentState state) BuildTwoBoxes() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { width: 100px; height: 50px; } " +
                "#b { width: 40px; height: 20px; margin-left: 200px; }");
            var sel = FindBoxById(root, "a");
            var hov = FindBoxById(root, "b");
            Assert.That(sel, Is.Not.Null, "#a box");
            Assert.That(hov, Is.Not.Null, "#b box");
            return (sel, hov, new UIDocumentState());
        }

        static List<Rect> EmitFills(SelectionHighlightSource src) {
            var backend = new RecordingBackend();
            src.EmitPaint(backend);
            var rects = new List<Rect>();
            foreach (var cmd in backend.Recorded) {
                if (cmd is FillRectCommand fill) rects.Add(fill.Bounds);
            }
            return rects;
        }

        // Union of all emitted fill rects — for a box with no transform this
        // must equal its margin box (the outermost highlight band).
        static (double x, double y, double w, double h) Union(List<Rect> rects) {
            Assert.That(rects, Is.Not.Empty, "expected highlight fills");
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var r in rects) {
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
            }
            return (minX, minY, maxX - minX, maxY - minY);
        }

        static (double x, double y, double w, double h) MarginBoxOf(Box box) {
            double absX = 0, absY = 0;
            for (var b = box; b != null; b = b.Parent) {
                absX += b.X + b.StickyOffsetX;
                absY += b.Y + b.StickyOffsetY;
            }
            return (absX - box.MarginLeft, absY - box.MarginTop,
                    box.Width + box.MarginLeft + box.MarginRight,
                    box.Height + box.MarginTop + box.MarginBottom);
        }

        static void AssertUnionIsMarginBox(List<Rect> rects, Box box, string label) {
            var u = Union(rects);
            var m = MarginBoxOf(box);
            Assert.That(u.x, Is.EqualTo(m.x).Within(0.01), label + " x");
            Assert.That(u.y, Is.EqualTo(m.y).Within(0.01), label + " y");
            Assert.That(u.w, Is.EqualTo(m.w).Within(0.01), label + " w");
            Assert.That(u.h, Is.EqualTo(m.h).Within(0.01), label + " h");
        }

        [Test]
        public void Selection_paints_bands_covering_the_margin_box() {
            var (sel, _, state) = BuildTwoBoxes();
            var src = new SelectionHighlightSource();
            src.SetTarget(sel, state);

            var rects = EmitFills(src);
            // Content fill + at least one strip per non-empty ring.
            Assert.That(rects.Count, Is.GreaterThanOrEqualTo(1));
            AssertUnionIsMarginBox(rects, sel, "selection");
        }

        [Test]
        public void Hover_preview_wins_over_the_committed_selection() {
            var (sel, hov, state) = BuildTwoBoxes();
            var src = new SelectionHighlightSource();
            src.SetTarget(sel, state);
            src.SetHover(hov, state);

            AssertUnionIsMarginBox(EmitFills(src), hov, "hover");
        }

        [Test]
        public void Clearing_hover_restores_the_selection_paint() {
            var (sel, hov, state) = BuildTwoBoxes();
            var src = new SelectionHighlightSource();
            src.SetTarget(sel, state);
            src.SetHover(hov, state);
            EmitFills(src);

            src.ClearHover();
            AssertUnionIsMarginBox(EmitFills(src), sel, "post-hover selection");
        }

        [Test]
        public void Hover_alone_paints_without_any_selection() {
            var (_, hov, state) = BuildTwoBoxes();
            var src = new SelectionHighlightSource();
            src.SetHover(hov, state);

            AssertUnionIsMarginBox(EmitFills(src), hov, "hover only");
            Assert.That(src.HasHover, Is.True);
            Assert.That(src.HasTarget, Is.False);
        }

        [Test]
        public void NeedsRepaint_contract_supports_idle_frame_reuse() {
            var (sel, hov, state) = BuildTwoBoxes();
            var src = new SelectionHighlightSource();

            // First-ever emit must always run.
            Assert.That(src.NeedsRepaint, Is.True, "before first emit");
            EmitFills(src);
            Assert.That(src.NeedsRepaint, Is.False, "idle after emit");

            src.SetTarget(sel, state);
            Assert.That(src.NeedsRepaint, Is.True, "after SetTarget");
            EmitFills(src);

            src.SetHover(hov, state);
            Assert.That(src.NeedsRepaint, Is.True, "after SetHover");
            EmitFills(src);

            // Same hover again is a no-op — per-pointer-move calls must not
            // defeat the renderer's idle-frame batch reuse.
            src.SetHover(hov, state);
            Assert.That(src.NeedsRepaint, Is.False, "unchanged hover is a no-op");

            src.ClearHover();
            Assert.That(src.NeedsRepaint, Is.True, "after ClearHover");
            EmitFills(src);

            // Clearing an already-clear hover is a no-op.
            src.ClearHover();
            Assert.That(src.NeedsRepaint, Is.False, "double ClearHover is a no-op");
        }

        [Test]
        public void Hover_with_null_state_is_dropped_and_selection_paints() {
            var (sel, hov, state) = BuildTwoBoxes();
            var src = new SelectionHighlightSource();
            src.SetTarget(sel, state);
            src.SetHover(hov, null);

            AssertUnionIsMarginBox(EmitFills(src), sel, "null-state hover ignored");
            Assert.That(src.HasHover, Is.False, "stale hover cleared during emit");
        }

        [Test]
        public void Nothing_set_emits_no_fills_and_does_not_throw() {
            var src = new SelectionHighlightSource();
            Assert.That(EmitFills(src), Is.Empty);

            src.SetHover(null, null);
            src.ClearTarget();
            Assert.That(EmitFills(src), Is.Empty);
        }
    }
}
