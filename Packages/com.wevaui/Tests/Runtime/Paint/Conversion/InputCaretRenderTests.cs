using System;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Coverage for the LIVE caret + selection rendering in
    // BoxToPaintConverter.EmitInputOverlays (driven by the InputCaretOf hook).
    // Before this, the converter painted the value text but no caret/selection —
    // a focused text field showed no cursor. The geometry comes from a hook so
    // the converter stays dumb; these tests stub the hook and assert the painted
    // caret bar / selection highlight.
    public class InputCaretRenderTests {
        // border:none + transparent bg so the ONLY fill rects are the caret /
        // selection the test is asserting on (no UA border/background quads).
        // NOTE: the harness BuiltinUserAgent lacks the real UA's
        // `input { display:inline-block }`, so declare it or width/height
        // are ignored (inline boxes take no explicit size).
        const string Css = "input { display:inline-block; width:120px; height:24px; padding:2px; border:none; background:transparent; }";

        static Box FindInput(Box root) {
            foreach (var b in AllBoxes(root))
                if (b.Element != null && b.Element.TagName == "input") return b;
            return null;
        }

        static int CountRectsOfWidth(System.Collections.Generic.IEnumerable<PaintCommand> cmds, double w) {
            int n = 0;
            foreach (var c in cmds)
                if (c is FillRectCommand f && Math.Abs(f.Bounds.Width - w) < 1e-6) n++;
            return n;
        }

        [Test]
        public void Focused_input_paints_a_one_pixel_caret_bar() {
            var (root, _, _) = Build("<input type=\"text\" value=\"hi\">", Css, 300);
            var input = FindInput(root);
            Assert.That(input, Is.Not.Null);

            var conv = new BoxToPaintConverter();
            conv.InputCaretOf = _ => new BoxToPaintConverter.InputCaretGeometry(17.0, false, 0, 0);
            var cmds = conv.Convert(root).Commands;

            Assert.That(CountRectsOfWidth(cmds, 1.0), Is.EqualTo(1),
                "focused input paints exactly one 1px caret bar");
        }

        [Test]
        public void Unfocused_input_paints_no_caret() {
            var (root, _, _) = Build("<input type=\"text\" value=\"hi\">", Css, 300);
            var conv = new BoxToPaintConverter();
            // InputCaretOf unset (null) → element is treated as not focused.
            var cmds = conv.Convert(root).Commands;

            Assert.That(CountRectsOfWidth(cmds, 1.0), Is.EqualTo(0),
                "an unfocused input paints no caret");
        }

        [Test]
        public void Selection_paints_a_highlight_rect_of_the_selected_width() {
            var (root, _, _) = Build("<input type=\"text\" value=\"hello\">", Css, 300);
            var conv = new BoxToPaintConverter();
            // Selection spanning px 10..40 (width 30); caret at the focus end.
            conv.InputCaretOf = _ => new BoxToPaintConverter.InputCaretGeometry(40.0, true, 10.0, 40.0);
            var cmds = conv.Convert(root).Commands;

            Assert.That(CountRectsOfWidth(cmds, 30.0), Is.EqualTo(1),
                "selection paints one highlight rect spanning the selected range");
            // Chrome shows the highlight OR the caret, never both — a
            // non-collapsed selection suppresses the caret bar (input/
            // selection audit #3; this assertion previously pinned the
            // divergence as "caret still paints alongside the selection").
            Assert.That(CountRectsOfWidth(cmds, 1.0), Is.EqualTo(0),
                "no caret bar while a selection is non-collapsed");
        }

        [Test]
        public void Caret_hidden_during_blink_off_phase() {
            var (root, _, _) = Build("<input type=\"text\" value=\"hi\">", Css, 300);
            var conv = new BoxToPaintConverter();
            // Focused, but the lifecycle is in the off half of the blink cycle —
            // CaretVisible=false suppresses the bar without dropping the geometry.
            conv.InputCaretOf = _ => new BoxToPaintConverter.InputCaretGeometry(17.0, false, 0, 0, caretVisible: false);
            var cmds = conv.Convert(root).Commands;

            Assert.That(CountRectsOfWidth(cmds, 1.0), Is.EqualTo(0),
                "no caret bar paints during the blink-off phase");
        }

        [Test]
        public void Selection_highlight_honors_selection_background_override() {
            // The span carries a red background; we feed its composed style in as
            // the ::selection style so ResolveSelectionColor reads its bg color.
            var (root, styles, _) = Build(
                "<input type=\"text\" value=\"hello\"><span class=\"s\"></span>",
                Css + " .s { background: rgb(255,0,0); }", 300);
            ComputedStyle selStyle = null;
            foreach (var kv in styles)
                if (kv.Key.TagName == "span") selStyle = kv.Value;
            Assert.That(selStyle, Is.Not.Null);

            var conv = new BoxToPaintConverter();
            conv.InputCaretOf = _ => new BoxToPaintConverter.InputCaretGeometry(40.0, true, 10.0, 40.0);
            conv.SelectionStyleOf = _ => selStyle;
            var cmds = conv.Convert(root).Commands;

            FillRectCommand sel = null;
            foreach (var c in cmds)
                if (c is FillRectCommand f && Math.Abs(f.Bounds.Width - 30.0) < 1e-6) sel = f;
            Assert.That(sel, Is.Not.Null, "selection highlight present");
            // Red override, not the translucent-blue UA default (R≈0.20, B≈0.95).
            Assert.That(sel.Brush.Color.R, Is.GreaterThan(0.5f), "red channel from ::selection override");
            Assert.That(sel.Brush.Color.B, Is.LessThan(0.5f), "not the UA blue default");
        }

        // ── audit TX4: overlay clip + edit-scroll ──────────────────────────
        // Pre-fix EmitInputOverlays pushed no clip and knew no scroll: a value
        // wider than the field painted past the border box over adjacent UI,
        // and the caret bar walked off-field as you typed.

        // input width:120 padding:2 border:none, content-box sizing (the UA
        // sheet sets border-box only on button) → border-box 124, content 120.
        const double AvailW = 120.0;

        static PushClipCommand FindOverlayClip(System.Collections.Generic.IEnumerable<PaintCommand> cmds) {
            foreach (var c in cmds)
                if (c is PushClipCommand p && Math.Abs(p.Bounds.Width - AvailW) < 1e-6) return p;
            return null;
        }

        [Test]
        public void Overlay_is_clipped_to_the_content_box_TX4() {
            var (root, _, _) = Build("<input type=\"text\" value=\"a-very-long-value-that-overflows\">", Css, 300);
            var conv = new BoxToPaintConverter();
            var cmds = conv.Convert(root).Commands;
            Assert.That(FindOverlayClip(cmds), Is.Not.Null,
                "the input overlay must clip to the content box — long values painted past the field (audit TX4)");
            bool popSeen = false;
            foreach (var c in cmds) if (c is PopClipCommand) popSeen = true;
            Assert.That(popSeen, Is.True, "the overlay clip must be popped");
        }

        [Test]
        public void Caret_beyond_field_width_scrolls_into_view_TX4() {
            var (root, _, _) = Build("<input type=\"text\" value=\"a-very-long-value-that-overflows\">", Css, 300);
            var conv = new BoxToPaintConverter();
            // Caret at px 300 of the value — far past the 116px content box.
            conv.InputCaretOf = _ => new BoxToPaintConverter.InputCaretGeometry(300.0, false, 0, 0);
            var cmds = conv.Convert(root).Commands;

            var clip = FindOverlayClip(cmds);
            Assert.That(clip, Is.Not.Null);
            FillRectCommand caretBar = null;
            foreach (var c in cmds)
                if (c is FillRectCommand f && Math.Abs(f.Bounds.Width - 1.0) < 1e-6) caretBar = f;
            Assert.That(caretBar, Is.Not.Null, "caret bar painted");
            Assert.That(caretBar.Bounds.X, Is.GreaterThanOrEqualTo(clip.Bounds.X),
                "scrolled caret stays inside the content box");
            Assert.That(caretBar.Bounds.X + caretBar.Bounds.Width,
                Is.LessThanOrEqualTo(clip.Bounds.X + clip.Bounds.Width + 1e-6),
                "the caret must scroll into view, not walk off-field (audit TX4)");
        }

        [Test]
        public void Caret_inside_field_width_does_not_scroll_TX4() {
            var (root, _, _) = Build("<input type=\"text\" value=\"hi\">", Css, 300);
            var conv = new BoxToPaintConverter();
            conv.InputCaretOf = _ => new BoxToPaintConverter.InputCaretGeometry(17.0, false, 0, 0);
            var cmds = conv.Convert(root).Commands;
            var clip = FindOverlayClip(cmds);
            Assert.That(clip, Is.Not.Null);
            FillRectCommand caretBar = null;
            foreach (var c in cmds)
                if (c is FillRectCommand f && Math.Abs(f.Bounds.Width - 1.0) < 1e-6) caretBar = f;
            Assert.That(caretBar.Bounds.X - clip.Bounds.X, Is.EqualTo(17.0).Within(1e-6),
                "a caret that fits paints at its unscrolled position");
        }
    }
}
