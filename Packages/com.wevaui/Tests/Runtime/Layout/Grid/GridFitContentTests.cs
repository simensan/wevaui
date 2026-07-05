using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // CSS Grid L1 §7.2.3: `fit-content(<length-percentage>)` is a track sizing
    // function meaning `minmax(auto, max-content)` with an upper-bound clamp
    // to the argument. Tracks E3 in CSS_COMPLIANCE_ISSUES.md.
    public class GridFitContentTests {
        [Test]
        public void Fit_content_track_resolves_to_intrinsic_when_under_limit() {
            const string css = @"
                .grid { display: grid; grid-template-columns: fit-content(100px) 1fr; width: 500px; }
                .narrow { width: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"narrow\"></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Item 0's intrinsic is 50px which is under the 100px limit, so the
            // fit-content track sizes to 50px and the 1fr track starts at X=50.
            var c1 = ChildAt(grid, 1);
            Assert.That(c1.X, Is.EqualTo(50).Within(0.01));
            Assert.That(c1.Width, Is.EqualTo(450).Within(0.01));
        }

        [Test]
        public void Fit_content_track_clamps_to_limit_when_intrinsic_exceeds_it() {
            const string css = @"
                .grid { display: grid; grid-template-columns: fit-content(100px) 1fr; width: 500px; }
                .wide { width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"wide\"></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Item 0's intrinsic is 200px which exceeds the 100px limit, so the
            // fit-content track clamps to 100px and the 1fr track starts at X=100.
            var c1 = ChildAt(grid, 1);
            Assert.That(c1.X, Is.EqualTo(100).Within(0.01));
            Assert.That(c1.Width, Is.EqualTo(400).Within(0.01));
        }

        [Test]
        public void Fit_content_in_three_track_template_is_not_dropped() {
            // Before E3, `1fr fit-content(150px) 1fr` parsed to an empty
            // template, so the grid had zero tracks. After E3 it parses to
            // three tracks; with an empty middle item the middle track
            // contributes 0 and the two 1fr tracks split the container evenly.
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr fit-content(150px) 1fr; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            var c2 = ChildAt(grid, 2);
            // Middle (fit-content) track is empty → 0px.
            Assert.That(c1.X, Is.EqualTo(200).Within(0.01));
            // Two 1fr tracks split 400px evenly.
            Assert.That(c0.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(c2.X, Is.EqualTo(200).Within(0.01));
            Assert.That(c2.Width, Is.EqualTo(200).Within(0.01));
        }
    }
}
