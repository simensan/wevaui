using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexGapTests {
        const string Css = @"
            .flex { display: flex; width: 600px; }
            .item { width: 100px; height: 50px; }
        ";

        [Test]
        public void Gap_separates_items_on_main_axis() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"gap:20px\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(120).Within(0.001));
            Assert.That(c.X, Is.EqualTo(240).Within(0.001));
        }

        [Test]
        public void No_gap_before_first_or_after_last_item() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"gap:30px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(130).Within(0.001));
        }

        [Test]
        public void Column_gap_only_applies_main_axis() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"column-gap:40px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(140).Within(0.001));
        }

        [Test]
        public void Row_gap_in_column_direction_separates_main_axis() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:400px;row-gap:25px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(75).Within(0.001));
        }

        // E2 (CSS Box Alignment L3 §8.3): row-gap percentages on a row-wrap
        // flex container resolve against the container's block-axis size
        // (height in horizontal writing modes), NOT the inline axis (width).
        // 200px-wide items wrap into 2 lines inside a 300x400 container;
        // `row-gap: 25%` of height(400) = 100px between the lines.
        [Test]
        public void Row_gap_percent_in_row_wrap_resolves_against_container_height_E2() {
            // Pin `align-content: flex-start` so the two wrap lines don't
            // stretch to share the container height — otherwise lines grow
            // to (400 - gap) / 2 and the assertion can't isolate the gap.
            const string rowWrapCss = @"
                .rw { display: flex; flex-wrap: wrap; align-content: flex-start; width: 300px; height: 400px; row-gap: 25%; }
                .wide { width: 200px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"rw\"><div class=\"wide\"></div><div class=\"wide\"></div></div>",
                rowWrapCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            // First item on line 1 at Y=0; second wraps to line 2 at
            // 50 (line1 height) + 100 (25% of 400) = 150.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(b.Y, Is.EqualTo(150).Within(0.01));
        }

        // Regression pin for E2: column-gap percentages MUST stay resolved
        // against width (inline-axis), regardless of the height-driven
        // row-gap change. 200x400 container, `column-gap: 50%` → 100px.
        [Test]
        public void Column_gap_percent_resolves_against_container_width_E2_regression() {
            const string css = @"
                .row { display: flex; width: 200px; height: 400px; column-gap: 50%; }
                .cell { width: 40px; height: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"cell\"></div><div class=\"cell\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            // a.X=0, b.X = 40 (a.width) + 100 (50% of 200) = 140.
            Assert.That(a.X, Is.EqualTo(0).Within(0.01));
            Assert.That(b.X, Is.EqualTo(140).Within(0.01));
        }
    }
}
