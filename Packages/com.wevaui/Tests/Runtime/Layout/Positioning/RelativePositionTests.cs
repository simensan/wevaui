using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    public class RelativePositionTests {
        [Test]
        public void Top_offset_shifts_box_down() {
            const string css = ".rel { position: relative; top: 10px; height: 30px; }";
            var (root, _, _) = Build("<div class=\"rel\"></div>", css, viewportWidth: 800);
            var rel = FirstByClass(root, "rel");
            Assert.That(rel.Y, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Left_offset_shifts_box_right() {
            const string css = ".rel { position: relative; left: 20px; height: 30px; width: 100px; }";
            var (root, _, _) = Build("<div class=\"rel\"></div>", css, viewportWidth: 800);
            var rel = FirstByClass(root, "rel");
            Assert.That(rel.X, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Subsequent_siblings_flow_as_if_box_unmoved() {
            const string css = @"
                #a { position: relative; top: 50px; height: 30px; }
                #b { height: 40px; }
            ";
            var (root, _, _) = Build("<div id=\"a\"></div><div id=\"b\"></div>", css, viewportWidth: 800);
            var a = FirstById(root, "a");
            var b = FirstById(root, "b");
            // a moved to Y=50 by relative offset, but b should still flow at Y=30 (right
            // after a's pre-offset bottom).
            Assert.That(a.Y, Is.EqualTo(50).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Negative_top_offset_moves_box_up() {
            const string css = @"
                #a { height: 50px; }
                #b { position: relative; top: -20px; height: 30px; }
            ";
            var (root, _, _) = Build("<div id=\"a\"></div><div id=\"b\"></div>", css, viewportWidth: 800);
            var b = FirstById(root, "b");
            // a takes 0..50; b would normally be at Y=50; with top:-20, paint Y=30.
            Assert.That(b.Y, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Negative_left_offset_moves_box_leftward() {
            const string css = ".rel { position: relative; left: -15px; height: 30px; width: 100px; }";
            var (root, _, _) = Build("<div class=\"rel\"></div>", css, viewportWidth: 800);
            var rel = FirstByClass(root, "rel");
            Assert.That(rel.X, Is.EqualTo(-15).Within(0.001));
        }

        [Test]
        public void Percent_top_resolves_against_parent_height() {
            const string css = @"
                .outer { height: 200px; }
                .inner { position: relative; top: 25%; height: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"outer\"><div class=\"inner\"></div></div>", css, viewportWidth: 800);
            var inner = FirstByClass(root, "inner");
            // 25% of parent's 200px height = 50px; inner originally at Y=0 inside parent,
            // shifted by 50, so local Y = 50.
            Assert.That(inner.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Right_offset_only_applies_when_left_is_auto() {
            const string css = @"
                #a { position: relative; left: 10px; right: 30px; height: 20px; width: 100px; }
                #b { position: relative; right: 30px; height: 20px; width: 100px; }
            ";
            var (root, _, _) = Build("<div id=\"a\"></div><div id=\"b\"></div>", css, viewportWidth: 800);
            var a = FirstById(root, "a");
            var b = FirstById(root, "b");
            Assert.That(a.X, Is.EqualTo(10).Within(0.001));
            Assert.That(b.X, Is.EqualTo(-30).Within(0.001));
        }

        [Test]
        public void Both_top_and_left_compose() {
            const string css = ".rel { position: relative; top: 7px; left: 13px; height: 30px; width: 100px; }";
            var (root, _, _) = Build("<div class=\"rel\"></div>", css, viewportWidth: 800);
            var rel = FirstByClass(root, "rel");
            Assert.That(rel.X, Is.EqualTo(13).Within(0.001));
            Assert.That(rel.Y, Is.EqualTo(7).Within(0.001));
        }

        [Test]
        public void Percent_top_resolves_against_parent_content_box_not_border_box() {
            // CSS 2.1 §10.6 / Positioned Layout L3 §10.3: relative offsets
            // resolve against the parent's content area, excluding padding
            // and border. Parent content height = 200, border-box height
            // = 280; 50% of content height = 100 (NOT 50% of 280 = 140).
            // inner's in-flow Y inside parent = PaddingTop + BorderTop = 40,
            // plus relative offset 100 → final local Y = 140 (not 180).
            const string css = @"
                .outer { width: 200px; height: 200px; padding: 30px; border: 10px solid black; box-sizing: content-box; }
                .inner { position: relative; top: 50%; height: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"outer\"><div class=\"inner\"></div></div>", css, viewportWidth: 800);
            var inner = FirstByClass(root, "inner");
            Assert.That(inner.Y, Is.EqualTo(140).Within(0.001));
        }

        [Test]
        public void Pixel_top_unaffected_by_parent_padding_and_border() {
            // Regression guard: absolute-length offsets must not change
            // when the containing-block basis switches from border-box to
            // content-box (no percentage involved). In-flow Y = 40
            // (parent's PaddingTop+BorderTop), plus 50px offset = 90.
            const string css = @"
                .outer { width: 200px; height: 200px; padding: 30px; border: 10px solid black; box-sizing: content-box; }
                .inner { position: relative; top: 50px; height: 20px; }
            ";
            var (root, _, _) = Build("<div class=\"outer\"><div class=\"inner\"></div></div>", css, viewportWidth: 800);
            var inner = FirstByClass(root, "inner");
            Assert.That(inner.Y, Is.EqualTo(90).Within(0.001));
        }
    }
}
