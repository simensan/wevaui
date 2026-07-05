using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Regression coverage for bug D4: PositioningPass.HasExplicitDim used to
    // compare the raw width/height string against "auto" only, so authors
    // writing `width: max-content` / `min-content` / `fit-content(...)` on an
    // abs-pos box bypassed the shrink-to-fit branch and ended up at the
    // containing block's full width. StyleResolver already collapses these
    // keywords to Auto (CSS Sizing L3 §5 — v1 simplification documented in
    // PLAN.md), so HasExplicitDim must treat them as auto-like.
    public class AbsolutePositionIntrinsicSizingTests {
        [Test]
        public void Absolute_width_max_content_shrinks_to_fit() {
            // One horizontal pin + intrinsic width keyword: per CSS Position
            // L3 §10.3.7 the box shrink-to-fits. Mono 0.5em @16px = 8px/char,
            // "Hello World" = 11 chars => 88px. Viewport = 800px; without
            // shrink-to-fit the box would stretch to the full 800.
            var (root, _, _) = Build(
                "<div id=\"abs\" style=\"position:absolute;left:0;width:max-content\">Hello World</div>",
                null, viewportWidth: 800);
            var abs = FirstById(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.Width, Is.EqualTo(88).Within(0.5));
        }

        [Test]
        public void Absolute_width_auto_matches_max_content_shrink_to_fit() {
            // Regression guard: the pre-existing `width: auto` behaviour must
            // still produce the same shrink-to-fit width as `max-content` for
            // an identical box, so the D4 fix doesn't drift the auto path.
            var (root, _, _) = Build(
                "<div id=\"abs\" style=\"position:absolute;left:0;width:auto\">Hello World</div>",
                null, viewportWidth: 800);
            var abs = FirstById(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.Width, Is.EqualTo(88).Within(0.5));
        }

        [Test]
        public void Absolute_width_min_content_shrinks_to_fit() {
            var (root, _, _) = Build(
                "<div id=\"abs\" style=\"position:absolute;left:0;width:min-content\">Hello World</div>",
                null, viewportWidth: 800);
            var abs = FirstById(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.Width, Is.LessThan(200));
        }

        [Test]
        public void Absolute_width_fit_content_length_shrinks_to_fit() {
            var (root, _, _) = Build(
                "<div id=\"abs\" style=\"position:absolute;left:0;width:fit-content(200px)\">Hello World</div>",
                null, viewportWidth: 800);
            var abs = FirstById(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.Width, Is.LessThan(200));
        }

        [Test]
        public void Absolute_shrink_to_fit_respects_flex_child_min_width() {
            var css = @"
                .action { position: absolute; left: 50%; }
                .btn { display: flex; min-width: 260px; height: 76px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"action\" id=\"a\"><div class=\"btn\">PLAY</div></div>",
                css, viewportWidth: 800);
            var action = FirstById(root, "a");
            Assert.That(action, Is.Not.Null);
            Assert.That(action.Width, Is.GreaterThanOrEqualTo(260));
        }

        [Test]
        public void Absolute_shrink_to_fit_respects_flex_child_min_width_border_box() {
            var css = @"
                .action { position: absolute; left: 50%; }
                .btn { display: flex; min-width: 260px; height: 76px; box-sizing: border-box; padding: 0 12px; border: 2px solid green; }
            ";
            var (root, _, _) = Build(
                "<div class=\"action\" id=\"a\"><div class=\"btn\">PLAY</div></div>",
                css, viewportWidth: 800);
            var action = FirstById(root, "a");
            Assert.That(action, Is.Not.Null);
            Assert.That(action.Width, Is.GreaterThanOrEqualTo(260));
        }

        [Test]
        public void Absolute_shrink_to_fit_respects_flex_child_min_width_content_box() {
            var css = @"
                .action { position: absolute; left: 50%; }
                .btn { display: flex; min-width: 260px; height: 76px; box-sizing: content-box; padding: 0 12px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"action\" id=\"a\"><div class=\"btn\">PLAY</div></div>",
                css, viewportWidth: 800);
            var action = FirstById(root, "a");
            Assert.That(action, Is.Not.Null);
            // content-box: min-width 260 + padding 24 = 284 outer
            Assert.That(action.Width, Is.GreaterThanOrEqualTo(284));
        }

        [Test]
        public void Absolute_shrink_to_fit_respects_grid_child_min_width() {
            var css = @"
                .action { position: absolute; left: 50%; }
                .grid { display: grid; min-width: 300px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"action\" id=\"a\"><div class=\"grid\">Content</div></div>",
                css, viewportWidth: 800);
            var action = FirstById(root, "a");
            Assert.That(action, Is.Not.Null);
            Assert.That(action.Width, Is.GreaterThanOrEqualTo(300));
        }

        [Test]
        public void Absolute_shrink_to_fit_text_align_center_repositions_text_at_shrunk_width() {
            // A real `.skill-slot-key` regression. An OOF box with a
            // single horizontal pin + `text-align: center` (inherited from
            // a `<button>` parent's UA `text-align: center`) shrinks to
            // its text width. BlockLayout.ApplyBoxModel earlier laid out
            // the inline content at the CB's content width (`avail`),
            // placing the centered TextRun at `(avail - textW) / 2`.
            // PositioningPass.ApplyAbsoluteAgainst's cache-hit fast path
            // used to ONLY assign `bb.Width = ShrinkFitCachedWidth` and
            // skip RelayoutContentAt — so the TextRun stayed at the
            // pre-shrink center-of-`avail` X, visibly floating past the
            // shrunk box's right edge. Fix re-runs LayoutContent at the
            // cached width on every cache hit so the TextRun is re-
            // centered against the now-correct content width.
            //
            // Concretely: 60×60 button-slot, padding box 56×56. A
            // `.skill-slot-key` at `bottom: 2px; left: 3px; padding:
            // 1px 4px; font-size: 11px; text-align: center` containing
            // "E" should shrink to (text width + 8) px, with the TextRun
            // sitting at X=4 inside it (= padding-left). NOT at
            // (56 - text - 8) / 2 px.
            const string css = @"
                * { box-sizing: border-box; }
                button { display: inline-flex; text-align: center; }
                .slot {
                    position: relative;
                    width: 60px;
                    height: 60px;
                    border: 2px solid #fff;
                }
                .key {
                    position: absolute;
                    bottom: 2px;
                    left: 3px;
                    padding: 1px 4px;
                    font-size: 11px;
                }
            ";
            var (root, _, _) = Build(
                "<button class=\"slot\"><span class=\"key\">E</span></button>",
                css, viewportWidth: 800);
            var key = FirstByClass(root, "key");
            Assert.That(key, Is.Not.Null);
            // box should be shrink-to-fit at (textW + 8) px, well under
            // the slot's 56-wide padding box.
            Assert.That(key.Width, Is.LessThan(30),
                "key span should shrink to its text width + padding (8), not stretch to the slot's content width");
            // The TextRun (or LineBox holding it) should sit inside the
            // shrunk content box. The key behavior: NOT placed at
            // approximately the center of the slot's content area (which
            // would be the cache-hit bug).
            var inner = FirstTextDescendantBox(key);
            Assert.That(inner, Is.Not.Null, "no inline content box under .key");
            // X relative to the key box. Anything > key.ContentWidth means
            // the text is overflowing the key box (the regressed state).
            double overflowSlackPx = 1.0; // sub-pixel rounding tolerance
            Assert.That(inner.X, Is.LessThanOrEqualTo(key.ContentWidth + overflowSlackPx),
                "TextRun X overflowing the key's content width — cache-hit path placed text at center-of-avail instead of center-of-shrunk-width");
        }

        [Test]
        public void Absolute_flex_shrink_to_fit_does_not_double_count_padding() {
            // Regression: the abs/fixed shrink-to-fit branch computed
            // MaxContentWidth(box) + frame, but MaxContentWidth ALREADY folds
            // the horizontal frame in for flex/grid containers, so padding was
            // counted twice — an absolute display:flex bubble (story-bubble's
            // `.thought`) came out one padding-pair too wide with dead space
            // after its content. Three 9px items + two 7px gaps = 41px content;
            // + padding 16px each side = 73px. The bug produced 73 + 32 = 105.
            var (root, _, _) = Build(
                "<div id=\"abs\" style=\"position:absolute;left:0;display:flex;gap:7px;padding:0 16px\">" +
                "<div style=\"width:9px;height:9px\"></div>" +
                "<div style=\"width:9px;height:9px\"></div>" +
                "<div style=\"width:9px;height:9px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var abs = FirstById(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.Width, Is.EqualTo(73).Within(0.5),
                "abs flex shrink-to-fit should be content(41) + padding(32) = 73, not 105 (padding double-counted)");
        }

        static Weva.Layout.Boxes.Box FirstTextDescendantBox(Weva.Layout.Boxes.Box box) {
            if (box is Weva.Layout.Boxes.TextRun) return box;
            for (int i = 0; i < box.Children.Count; i++) {
                var found = FirstTextDescendantBox(box.Children[i]);
                if (found != null) return found;
            }
            return null;
        }
    }
}
