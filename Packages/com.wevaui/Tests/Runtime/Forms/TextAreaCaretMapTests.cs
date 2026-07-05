using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Forms {
    // Input/selection audit #6 — the multiline caret map aligns the
    // textarea's PAINTED runs (LineBox/TextRun geometry from the real inline
    // pipeline under the UA `white-space: pre-wrap`) back to model-text
    // indices. These tests drive the real cascade + layout.
    public class TextAreaCaretMapTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static (Box taBox, Element ta, System.Func<string, int, int, double> measure)
                Layout(string valueText, string css) {
            var doc = HtmlParser.Parse("<div class=\"page\"><textarea class=\"ta\"></textarea></div>");
            var ta = doc.GetElementsByTagName("textarea").First();
            ta.AppendChild(new TextNode(valueText));
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var metrics = new MonoFontMetrics();
            var ctx = new LayoutContext(metrics) {
                ViewportWidthPx = 800, ViewportHeightPx = 600,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(metrics);
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
            var taBox = FindBoxFor(root, ta);
            Assert.That(taBox, Is.Not.Null, "textarea box not laid out");
            double fs = 16;
            return (taBox, ta, (t, s, c) => metrics.Measure(t, s, c, fs));
        }

        const string Css = @"
            * { box-sizing: border-box; }
            .page { width: 600px; }
            .ta { width: 200px; height: 120px; font-size: 16px; }";

        [Test]
        public void Hard_newlines_produce_aligned_lines() {
            var (box, _, measure) = Layout("hello world\nsecond line", Css);
            var map = TextAreaCaretMap.Build(box, "hello world\nsecond line", measure);
            Assert.That(map, Is.Not.Null, "alignment must succeed on plain text");
            Assert.That(map.Lines.Count, Is.EqualTo(2));
            Assert.That(map.Lines[0].StartIndex, Is.EqualTo(0));
            Assert.That(map.Lines[1].StartIndex, Is.EqualTo(12), "second line starts after the newline");
            Assert.That(map.Lines[0].EndIndex, Is.EqualTo(12), "the newline belongs to line 0's range");
        }

        [Test]
        public void Caret_rects_round_trip_through_IndexFromPoint() {
            const string v = "hello world\nsecond line";
            var (box, _, measure) = Layout(v, Css);
            var map = TextAreaCaretMap.Build(box, v, measure);
            Assert.That(map, Is.Not.Null);
            foreach (int idx in new[] { 0, 3, 11, 12, 15, v.Length }) {
                var (x, y, h) = map.CaretRectFor(idx);
                Assert.That(h, Is.GreaterThan(0), $"line height at {idx}");
                int back = map.IndexFromPoint(x + 0.5, y + h / 2);
                Assert.That(back, Is.EqualTo(idx), $"round trip at index {idx}");
            }
        }

        [Test]
        public void Newline_index_sits_at_the_end_of_its_visual_line() {
            const string v = "hello world\nsecond line";
            var (box, _, measure) = Layout(v, Css);
            var map = TextAreaCaretMap.Build(box, v, measure);
            var endOfLine0 = map.CaretRectFor(11); // before the '\n'
            var startOfLine1 = map.CaretRectFor(12);
            Assert.That(endOfLine0.Y, Is.LessThan(startOfLine1.Y), "11 paints on line 0, 12 on line 1");
        }

        [Test]
        public void Soft_wrap_maps_the_boundary_to_the_next_line() {
            // 200px textarea, monospace metrics — force a wrap mid-text.
            const string v = "aaaa bbbb cccc dddd eeee ffff gggg";
            var (box, _, measure) = Layout(v, Css);
            var map = TextAreaCaretMap.Build(box, v, measure);
            Assert.That(map, Is.Not.Null);
            Assert.That(map.Lines.Count, Is.GreaterThan(1), "narrow box must soft-wrap");
            int wrapStart = map.Lines[1].StartIndex;
            Assert.That(wrapStart, Is.GreaterThan(0).And.LessThan(v.Length));
            var r = map.CaretRectFor(wrapStart);
            Assert.That(r.Y, Is.EqualTo(map.Lines[1].Y).Within(0.01),
                "the wrap-boundary index takes downstream affinity (next line)");
        }

        [Test]
        public void Empty_forced_line_is_addressable() {
            const string v = "a\n\nb";
            var (box, _, measure) = Layout(v, Css);
            var map = TextAreaCaretMap.Build(box, v, measure);
            Assert.That(map, Is.Not.Null);
            Assert.That(map.Lines.Count, Is.EqualTo(3));
            Assert.That(map.Lines[1].StartIndex, Is.EqualTo(2), "the empty middle line owns index 2");
            var r = map.CaretRectFor(2);
            Assert.That(r.Y, Is.EqualTo(map.Lines[1].Y).Within(0.01));
        }

        [Test]
        public void Selection_rects_cover_each_visual_line_once() {
            const string v = "hello world\nsecond line";
            var (box, _, measure) = Layout(v, Css);
            var map = TextAreaCaretMap.Build(box, v, measure);
            var rects = new List<(double X, double Y, double W, double H)>();
            map.AddSelectionRects(3, 18, rects); // spans both lines
            Assert.That(rects.Count, Is.EqualTo(2));
            Assert.That(rects[0].Y, Is.LessThan(rects[1].Y));
            Assert.That(rects[0].W, Is.GreaterThan(0));
            Assert.That(rects[1].X, Is.EqualTo(map.Lines[1].X).Within(0.01),
                "the continuation line's rect starts at the line origin");
        }

        [Test]
        public void Vertical_clamping_maps_outside_points_to_first_and_last_lines() {
            const string v = "hello world\nsecond line";
            var (box, _, measure) = Layout(v, Css);
            var map = TextAreaCaretMap.Build(box, v, measure);
            Assert.That(map.IndexFromPoint(-100, -100), Is.EqualTo(0));
            int below = map.IndexFromPoint(9999, 9999);
            Assert.That(below, Is.EqualTo(v.Length), "below/right of everything → end of text");
        }

        // ── controller-level: pointer gestures on a real laid-out textarea ──

        sealed class FixedHit : IHitTester {
            readonly Element only;
            public FixedHit(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        static (InputController ctrl, EventDispatcher d, TextAreaCaretMap map, Box box)
                WireTextArea(string valueText) {
            var (box, ta, measure) = Layout(valueText, Css);
            var d = new EventDispatcher(ta.OwnerDocument, new FixedHit(ta), new FakeUIClock());
            var ctrl = new InputController(ta, d);
            ctrl.Model.SetMeasureSubstring(measure);
            Box root = box; while (root.Parent != null) root = root.Parent;
            ctrl.ElementToBox = e => FindBoxFor(root, e);
            ctrl.Wire();
            d.Focus(ta);
            var map = TextAreaCaretMap.Build(box, valueText, measure);
            Assert.That(map, Is.Not.Null);
            return (ctrl, d, map, box);
        }

        static (double X, double Y) AbsoluteOf(Box box) {
            double x = 0, y = 0;
            for (var b = box; b != null; b = b.Parent) { x += b.X; y += b.Y; }
            return (x, y);
        }

        [Test]
        public void Click_places_the_caret_at_the_clicked_line_and_column() {
            const string v = "hello world\nsecond line";
            var (ctrl, d, map, box) = WireTextArea(v);
            var (bx, by) = AbsoluteOf(box);
            var target = map.CaretRectFor(15); // 'o' region of "second line"
            d.DispatchPointerDown(bx + target.X + 0.5, by + target.Y + target.Height / 2, 0, KeyModifiers.None);
            d.DispatchPointerUp(bx + target.X + 0.5, by + target.Y + target.Height / 2, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(15));
            Assert.That(ctrl.Model.Selection.IsCollapsed, Is.True,
                "a textarea click places a collapsed caret at the clicked line/column");
        }

        [Test]
        public void Drag_across_lines_selects_the_swept_range() {
            const string v = "hello world\nsecond line";
            var (ctrl, d, map, box) = WireTextArea(v);
            var (bx, by) = AbsoluteOf(box);
            var from = map.CaretRectFor(3);
            var to = map.CaretRectFor(18);
            d.DispatchPointerDown(bx + from.X + 0.5, by + from.Y + from.Height / 2, 0, KeyModifiers.None);
            d.DispatchPointerMove(bx + to.X + 0.5, by + to.Y + to.Height / 2, KeyModifiers.None);
            d.DispatchPointerUp(bx + to.X + 0.5, by + to.Y + to.Height / 2, 0, KeyModifiers.None);
            Assert.That((ctrl.Model.Selection.Start, ctrl.Model.Selection.End), Is.EqualTo((3, 18)),
                "drag from line 0 to line 1 sweeps a cross-line selection");
        }

        [Test]
        public void Triple_click_selects_the_logical_line() {
            const string v = "hello world\nsecond line";
            var (ctrl, d, map, box) = WireTextArea(v);
            var (bx, by) = AbsoluteOf(box);
            var target = map.CaretRectFor(3);
            double px = bx + target.X + 0.5, py = by + target.Y + target.Height / 2;
            for (int i = 0; i < 3; i++) {
                d.Tick(0.1 * i);
                d.DispatchPointerDown(px, py, 0, KeyModifiers.None);
                d.DispatchPointerUp(px, py, 0, KeyModifiers.None);
            }
            Assert.That((ctrl.Model.Selection.Start, ctrl.Model.Selection.End), Is.EqualTo((0, 12)),
                "triple-click selects the logical line, newline inclusive (Chrome paragraph)");
        }

        [Test]
        public void LineRangeAt_covers_edges() {
            InputController.LineRangeAt("ab\ncd", 0, out int s, out int e);
            Assert.That((s, e), Is.EqualTo((0, 3)));
            InputController.LineRangeAt("ab\ncd", 4, out s, out e);
            Assert.That((s, e), Is.EqualTo((3, 5)));
            InputController.LineRangeAt("", 0, out s, out e);
            Assert.That((s, e), Is.EqualTo((0, 0)));
        }

        static Box FindBoxFor(Box root, Element el) {
            if (ReferenceEquals(root.Element, el)) return root;
            foreach (var c in root.ChildList) {
                var hit = FindBoxFor(c, el);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
