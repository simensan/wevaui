// §12 — Auto margins on flex items (CSS Flexbox L1 §8.1)
// "If free space is positive, auto margins expand to absorb extra space in the
// corresponding dimension." Auto margins are consumed BEFORE justify-content,
// so they override the parent's justification.
// This is a completely new area with zero prior coverage.
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAutoMarginTests {

        // ── Main axis: margin-left/right auto ─────────────────────────────

        [Test]
        public void Margin_left_auto_pushes_item_to_end_of_main_axis() {
            // 600px container, item A=100px, item B=100px with margin-left:auto.
            // Free space = 400. margin-left:auto on B absorbs all free space.
            // A at X=0, B at X=500 (400+100=500).
            const string css = @"
                .flex { display: flex; width: 600px; }
                .a { width: 100px; height: 50px; }
                .b { width: 100px; height: 50px; margin-left: auto; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"a\"></div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(500).Within(0.001),
                "margin-left:auto should push item B to the far end");
        }

        [Test]
        public void Margin_right_auto_pushes_subsequent_items_to_end() {
            // Item A has margin-right:auto, absorbing all free space before B.
            // 600px container, A=100, B=100. Free=400. A at 0, B at 500.
            const string css = @"
                .flex { display: flex; width: 600px; }
                .a { width: 100px; height: 50px; margin-right: auto; }
                .b { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"a\"></div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(500).Within(0.001),
                "margin-right:auto on A absorbs free space and pushes B to end");
        }

        [Test]
        public void Margin_left_right_auto_centers_single_item_on_main_axis() {
            // margin-left:auto + margin-right:auto on a single flex item → equal
            // margins on both sides → item centered on main axis.
            const string css = @"
                .flex { display: flex; width: 600px; justify-content: flex-start; }
                .item { width: 100px; height: 50px; margin-left: auto; margin-right: auto; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(250).Within(0.001),
                "margin-left+right:auto centers item regardless of justify-content");
        }

        [Test]
        public void Auto_margin_overrides_justify_content_flex_end() {
            // Even with justify-content:flex-end, an auto left-margin on an early
            // item absorbs free space first, potentially neutralizing the align.
            // Container=600, item=100, margin-left:auto → item is pushed right,
            // effectively at X=500 (same as if justify-content were flex-end).
            const string css = @"
                .flex { display: flex; width: 600px; justify-content: flex-start; }
                .item { width: 100px; height: 50px; margin-left: auto; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(500).Within(0.001),
                "margin-left:auto absorbs all free space; item lands at the end even with flex-start");
        }

        // ── Cross axis: margin-top/bottom auto ────────────────────────────
        // CSS Flexbox L1 §8.1: auto margins on the cross axis absorb free
        // space and center (or pin) the item within the line cross size.
        // AlignItemsInLine now handles cross-axis auto margins before align-self.

        [Test]
        public void Margin_top_bottom_auto_centers_item_on_cross_axis() {
            // margin-top:auto + margin-bottom:auto in a row flex centers item
            // vertically within the line cross size.
            const string css = @"
                .flex { display: flex; width: 600px; height: 200px; align-items: flex-start; }
                .item { width: 100px; height: 50px; margin-top: auto; margin-bottom: auto; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // Free cross = 150. Split: 75 each side. Y = 75.
            Assert.That(a.Y, Is.EqualTo(75).Within(0.001),
                "margin-top+bottom:auto should center item on the cross axis");
        }

        [Test]
        public void Margin_top_auto_pins_item_to_bottom_of_cross_axis() {
            const string css = @"
                .flex { display: flex; width: 600px; height: 200px; align-items: flex-start; }
                .item { width: 100px; height: 50px; margin-top: auto; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(150).Within(0.001),
                "margin-top:auto absorbs all free cross space; item at Y=150");
        }

        [Test]
        public void Three_items_middle_one_pushed_right_by_auto_margin() {
            // A nav pattern: logo | [auto margin] | nav-links
            // Logo at left, nav-links at right, auto margin between them.
            const string css = @"
                .flex { display: flex; width: 600px; }
                .logo { width: 80px; height: 40px; }
                .spacer { flex: 1; }
                .links { width: 150px; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"logo\"></div>"
                + "<div class=\"spacer\"></div>"
                + "<div class=\"links\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var logo = ChildAt(fb, 0);
            var links = ChildAt(fb, 2);
            Assert.That(logo.X, Is.EqualTo(0).Within(0.001));
            Assert.That(links.X, Is.EqualTo(450).Within(0.001),
                "logo at left, links at right (600-150=450)");
        }
    }
}
