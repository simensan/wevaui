// §4 — align-content extension coverage (CSS Flexbox L1 §8.4)
// FlexAlignContentTests.cs covers flex-start/flex-end/center/space-between/
// stretch on a two-line container. This file adds:
//   - space-around / space-evenly (multi-line)
//   - single-line container: align-content is ignored (line fills container)
//   - align-content interaction with column-wrap
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAlignContentExtTests {
        // Two-line container: 3×120px items in 250px → lines at 0 and natural.
        // Container height 400px so free space = 400 - 2*50 = 300.
        const string Css = @"
            .flex { display: flex; flex-wrap: wrap; width: 250px; height: 400px; }
            .item { width: 120px; height: 50px; }
        ";

        [Test]
        public void SpaceAround_puts_half_gaps_at_edges_of_line_stack() {
            // 2 lines, free=300. space-around: each line gets 150 around it.
            // Half-gap at top = 75. Line1 at Y=75, line2 at Y=75+50+150=275.
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:space-around\">"
                + "<div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(75).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(275).Within(0.001));
        }

        [Test]
        public void SpaceEvenly_distributes_equal_gaps_including_edges() {
            // 2 lines, free=300. space-evenly: 3 equal gaps of 100.
            // Line1 at Y=100, line2 at Y=100+50+100=250.
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:space-evenly\">"
                + "<div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(100).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(250).Within(0.001));
        }

        [Test]
        public void Single_line_container_align_content_stretch_line_fills_container() {
            // Single-line: only one flex line → it expands to fill the cross axis.
            // align-content is irrelevant for single-line; all items share that line.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:300px;height:200px;align-content:flex-start\">"
                + "<div style=\"width:80px;height:50px\"></div>"
                + "<div style=\"width:80px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            // Single line: both items start at Y=0
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Single_line_container_align_content_flex_end_does_not_push_line() {
            // align-content:flex-end in a single-line container: the spec says
            // align-content applies only when there are multiple lines. Single-
            // line is effectively stretch (line = container height).
            // Items still start at Y=0 because align-items:stretch raises them.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:300px;height:200px;align-content:flex-end;align-items:flex-start\">"
                + "<div style=\"width:80px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // With a single line and align-items:flex-start, item is at Y=0
            // regardless of align-content.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001),
                "align-content must have no effect in a single-line container");
        }

        [Test]
        public void Align_content_start_packs_lines_at_top() {
            // 'start' keyword should behave like 'flex-start' in CSS Align L3.
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:start\">"
                + "<div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Align_content_end_packs_lines_at_bottom() {
            // 'end' keyword should behave like 'flex-end' in CSS Align L3.
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:end\">"
                + "<div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(300).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void Column_wrap_align_content_center_centers_columns_horizontally() {
            // In column flex-direction, the cross axis is horizontal.
            // Two columns of items; align-content:center centers them.
            var css = @"
                .flex { display: flex; flex-direction: column; flex-wrap: wrap; align-content: center; width: 300px; height: 120px; }
                .item { width: 80px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\"></div><div class=\"item\"></div>"
                + "<div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); // first column, item 0
            // Two columns, each 80px wide; total = 160px, free = 140px.
            // center: offset = 70px.
            Assert.That(a.X, Is.EqualTo(70).Within(0.001));
        }
    }
}
