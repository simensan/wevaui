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

        // PositioningPass's intrinsic helpers are static and take only the box,
        // so they had no LengthContext and read the gap by scanning its
        // declaration for the first number. That is right for `12px` and wrong
        // for anything computed: `clamp(4px, 0.6vmin, 7px)` was read as 4 where
        // the resolved value is 4.32, so a column flex measured AS AN ITEM of
        // an outer flex came out short of the children it then placed —
        // randhtml's `.party` reported 107.706 while its own children spanned
        // 108.026, a container not containing its own contents.
        [Test]
        public void A_computed_gap_survives_being_measured_as_a_flex_item() {
            // 0.6vmin at 1280x720 is 4.32, inside the clamp's 4..7 range.
            const string css = @"
                body { margin: 0 }
                .outer { display: flex; flex-direction: column; width: 300px }
                .inner { display: flex; flex-direction: column;
                         gap: clamp(4px, 0.6vmin, 7px) }
                .k { height: 20px }
            ";
            var (root, _, _) = Build(
                @"<div class=""outer""><div class=""inner"">" +
                @"<div class=""k""></div><div class=""k""></div></div></div>",
                css, viewportWidth: 1280, viewportHeight: 720);

            Weva.Layout.Boxes.BlockBox inner = null;
            var kids = new System.Collections.Generic.List<Weva.Layout.Boxes.BlockBox>();
            foreach (var b in AllBoxes(root)) {
                if (b is Weva.Layout.Boxes.BlockBox bb && bb.Element?.ClassName == "inner") inner = bb;
                if (b is Weva.Layout.Boxes.BlockBox kb && kb.Element?.ClassName == "k") kids.Add(kb);
            }
            Assert.That(inner, Is.Not.Null);
            Assert.That(kids.Count, Is.EqualTo(2));

            // Whatever the gap resolves to, the container must contain the
            // children it placed — that is the invariant the first-number scan
            // broke, and it holds without hard-coding 4.32.
            double span = (kids[1].Y + kids[1].Height) - kids[0].Y;
            Assert.That(inner.Height, Is.EqualTo(span).Within(1e-9),
                "a flex container must be as tall as the children it placed");
            // And the gap really is the computed one, not the clamp's floor.
            Assert.That(kids[1].Y - (kids[0].Y + kids[0].Height),
                        Is.EqualTo(4.32).Within(1e-9));
        }

    }
}
