using NUnit.Framework;
using System.Collections.Generic;
using Weva.Documents;
using Weva.Layout.Positioning;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    public class AbsolutePositionTests {
        [Test]
        public void Absolute_box_does_not_shift_following_siblings() {
            const string css = @"
                #abs { position: absolute; top: 0; left: 0; height: 30px; width: 50px; }
                #after { height: 20px; }
            ";
            var (root, _, _) = Build("<div id=\"abs\"></div><div id=\"after\"></div>", css, viewportWidth: 800);
            var after = FirstById(root, "after");
            Assert.That(after.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Abs_box_with_left_percent_and_auto_margins_resolves_margins_to_zero() {
            // CSS 2.1 §10.3.7: an absolutely-positioned box with `left` set and
            // `right: auto` resolves its inline auto margins to 0 — it must NOT
            // pick up the in-flow `margin: auto` centring. Regression: a
            // `position: absolute; left: 50%; margin: 0 auto` box (as used to
            // centre with a paired translate(-50%)) was shifted right by the
            // in-flow centring amount (cb − width)/2, landing far off-centre.
            const string css = @"
                .cb { position: relative; width: 1000px; height: 600px; }
                .box { position: absolute; left: 50%; top: 0; width: 400px; height: 100px; margin: 0 auto; }
            ";
            var (root, _, _) = Build(
                "<div class=\"cb\"><div class=\"box\"></div></div>", css, viewportWidth: 1200);
            var cb = FirstByClass(root, "cb");
            var box = FirstByClass(root, "box");
            var (cbx, _) = AbsoluteOriginOf(cb);
            var (bx, _) = AbsoluteOriginOf(box);
            // left:50% of 1000 = 500, margin-left auto → 0. The bug added
            // (1000 − 400)/2 = 300, giving 800.
            Assert.That(bx - cbx, Is.EqualTo(500).Within(0.5),
                "abs left:50% with auto inline margins must resolve margin-left to 0, not in-flow centre");
        }

        [Test]
        public void Containing_block_is_nearest_positioned_ancestor() {
            const string css = @"
                .outer { width: 400px; height: 400px; }
                .mid { position: relative; width: 300px; height: 300px; margin-top: 20px; margin-left: 30px; }
                .grandparent { width: 600px; height: 600px; }
                .abs { position: absolute; top: 0; left: 0; height: 50px; width: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grandparent\"><div class=\"outer\"><div class=\"mid\"><div class=\"abs\"></div></div></div></div>",
                css, viewportWidth: 1000);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            // Mid is at (30, 20) absolute; abs top:0,left:0 places it at (30, 20).
            Assert.That(ax, Is.EqualTo(30).Within(0.001));
            Assert.That(ay, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Containing_block_falls_back_to_viewport_when_no_positioned_ancestor() {
            const string css = ".abs { position: absolute; top: 100px; left: 200px; height: 50px; width: 50px; }";
            var (root, _, _) = Build("<div class=\"abs\"></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(200).Within(0.001));
            Assert.That(ay, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Top_zero_pins_to_top_of_containing_block() {
            const string css = @"
                .cb { position: relative; width: 300px; height: 300px; margin-top: 50px; margin-left: 40px; }
                .abs { position: absolute; top: 0; left: 0; height: 20px; width: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(40).Within(0.001));
            Assert.That(ay, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void All_four_offsets_zero_stretches_to_fill_containing_block() {
            const string css = @"
                .cb { position: relative; width: 300px; height: 200px; }
                .abs { position: absolute; top: 0; right: 0; bottom: 0; left: 0; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            Assert.That(abs.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(abs.Height, Is.EqualTo(200).Within(0.001));
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(0).Within(0.001));
            Assert.That(ay, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Inset_zero_flex_container_keeps_pinned_height_through_second_flex_pass() {
            // Regression: `position: absolute; inset: 0; display: flex` was
            // losing its pinned Height on the second flex pass — FlexLayout's
            // FinalizeContainerCrossSize collapsed it back to text-content
            // height. PositioningPass now stamps GridStretchedHeight after
            // pinning so the existing guard in FlexLayout treats the size as
            // externally allocated and doesn't collapse it. Canonical case
            // from match3.html: `.tile.boost-bomb::after { inset: 0;
            // display: flex; align-items: center }` rendering 💣 at the
            // top of the tile instead of vertically centred.
            const string css = @"
                .cb { position: relative; width: 54px; height: 54px; }
                .abs { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\">X</div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            Assert.That(abs.Width, Is.EqualTo(54).Within(0.5));
            Assert.That(abs.Height, Is.EqualTo(54).Within(0.5));
        }

        [Test]
        public void Percent_top_left_resolve_against_containing_block_size() {
            const string css = @"
                .cb { position: relative; width: 400px; height: 200px; }
                .abs { position: absolute; top: 50%; left: 50%; width: 20px; height: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(200).Within(0.001));
            Assert.That(ay, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Absolute_explicit_content_box_height_includes_vertical_border() {
            const string css = @"
                .cb { position: relative; width: 200px; height: 80px; }
                .abs {
                    position: absolute;
                    top: -5px;
                    left: 10px;
                    width: 20px;
                    height: 20px;
                    border-top-style: solid; border-top-width: 2px;
                    border-right-style: solid; border-right-width: 2px;
                    border-bottom-style: solid; border-bottom-width: 2px;
                    border-left-style: solid; border-left-width: 2px;
                }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");

            Assert.That(abs.Width, Is.EqualTo(24).Within(0.001));
            Assert.That(abs.Height, Is.EqualTo(24).Within(0.001));
        }

        [Test]
        public void Absolute_left_top_offsets_include_non_auto_margins() {
            const string css = @"
                .cb { position: relative; width: 400px; height: 200px; }
                .abs { position: absolute; top: 25%; left: 50%; margin-left: -70px; margin-top: 12px; width: 40px; height: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(130).Within(0.001));
            Assert.That(ay, Is.EqualTo(62).Within(0.001));
        }

        [Test]
        public void Absolute_right_bottom_offsets_include_non_auto_margins() {
            const string css = @"
                .cb { position: relative; width: 400px; height: 200px; }
                .abs { position: absolute; right: 20px; bottom: 30px; margin-right: 10px; margin-bottom: 5px; width: 40px; height: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(330).Within(0.001));
            Assert.That(ay, Is.EqualTo(145).Within(0.001));
        }

        [Test]
        public void Both_top_and_bottom_pinned_stretch_height() {
            const string css = @"
                .cb { position: relative; width: 200px; height: 300px; }
                .abs { position: absolute; top: 20px; bottom: 30px; left: 0; width: 100px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            // Height = 300 - 20 - 30 = 250.
            Assert.That(abs.Height, Is.EqualTo(250).Within(0.001));
            var (_, ay) = AbsoluteOriginOf(abs);
            Assert.That(ay, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Inside_relative_parent_positions_relative_to_parent() {
            const string css = @"
                .parent { position: relative; margin-top: 100px; margin-left: 50px; width: 200px; height: 200px; }
                .child { position: absolute; top: 10px; left: 15px; width: 30px; height: 30px; }
            ";
            var (root, _, _) = Build("<div class=\"parent\"><div class=\"child\"></div></div>", css, viewportWidth: 800);
            var child = FirstByClass(root, "child");
            var (cx, cy) = AbsoluteOriginOf(child);
            Assert.That(cx, Is.EqualTo(50 + 15).Within(0.001));
            Assert.That(cy, Is.EqualTo(100 + 10).Within(0.001));
        }

        [Test]
        public void Inside_non_positioned_parent_skips_to_grandparent() {
            const string css = @"
                .gp { position: relative; margin-top: 60px; margin-left: 40px; width: 400px; height: 400px; }
                .parent { width: 200px; height: 200px; margin-top: 20px; margin-left: 30px; }
                .abs { position: absolute; top: 0; left: 0; width: 20px; height: 20px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"gp\"><div class=\"parent\"><div class=\"abs\"></div></div></div>",
                css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(40).Within(0.001));
            Assert.That(ay, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Right_offset_pins_to_right_edge_of_containing_block() {
            const string css = @"
                .cb { position: relative; width: 400px; height: 200px; }
                .abs { position: absolute; top: 0; right: 0; width: 80px; height: 30px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, _) = AbsoluteOriginOf(abs);
            Assert.That(ax, Is.EqualTo(400 - 80).Within(0.001));
        }

        [Test]
        public void Position_field_set_to_absolute() {
            const string css = ".abs { position: absolute; }";
            var (root, _, _) = Build("<div class=\"abs\"></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            Assert.That(abs.Position, Is.EqualTo(PositionType.Absolute));
        }

        [Test]
        public void Bottom_offset_pins_to_bottom_when_top_is_auto() {
            const string css = @"
                .cb { position: relative; width: 200px; height: 200px; }
                .abs { position: absolute; bottom: 10px; left: 0; width: 50px; height: 30px; }
            ";
            var (root, _, _) = Build("<div class=\"cb\"><div class=\"abs\"></div></div>", css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (_, ay) = AbsoluteOriginOf(abs);
            // bottom=10, height=30, cb height=200 → top = 200-10-30 = 160.
            Assert.That(ay, Is.EqualTo(160).Within(0.001));
        }

        [Test]
        public void Absolute_before_marker_uses_padded_li_padding_edge() {
            const string css = @"
                ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
                .obj { position: relative; padding-left: 22px; font-size: 12px; }
                .obj::before { content: ""*""; position: absolute; left: 0; top: 0; }
            ";
            var state = new UIDocumentBuilder {
                DocumentSource = "<ul><li id=\"a\" class=\"obj\">Investigate the lower archive</li></ul>",
                StylesheetSources = new List<string> { css },
                FontMetricsOverride = new MonoFontMetrics()
            }.Build();

            UIDocumentLifecycle.Update(state, null, 0);

            var li = FirstById(state.RootBox, "a");
            var marker = FirstText(state.RootBox, "*");
            var text = FirstText(state.RootBox, "Investigate");
            Assert.That(li, Is.Not.Null);
            Assert.That(marker, Is.Not.Null);
            Assert.That(text, Is.Not.Null);

            var (liX, _) = AbsoluteOriginOf(li);
            var (markerX, _) = AbsoluteOriginOf(marker);
            var (textX, _) = AbsoluteOriginOf(text);

            Assert.That(markerX, Is.EqualTo(liX).Within(0.001),
                "absolute ::before left:0 should pin to the li padding edge, not the text content edge");
            Assert.That(textX, Is.EqualTo(liX + 22).Within(0.001));
        }

        static Weva.Layout.Boxes.TextRun FirstText(Weva.Layout.Boxes.Box root, string text) {
            foreach (var run in AllTextRuns(root)) {
                if (run.Text != null && run.Text.Contains(text)) return run;
            }
            return null;
        }
    }
}
