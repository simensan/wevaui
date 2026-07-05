using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Sizing {
    // CSS Sizing L3 §5.1: fit-content(<length-percentage>) function form.
    //
    // Spec: fit-content(arg) = min(max-content, max(min-content, arg))
    //   - arg > max-content  → result = max-content (max-content is the ceiling)
    //   - arg < min-content  → result = min-content (min-content is the floor)
    //   - min-content <= arg <= max-content → result = arg (argument is binding)
    //
    // MonoFontMetrics constants (default ctor, 16px root font-size):
    //   charWidthEm = 0.5  →  8px per character
    //   "Hello World Again" = 17 chars unwrapped = 136px max-content
    //                         longest word "Hello" = 5 chars = 40px min-content
    //   "ABCDEFGHIJ KLMNOPQRSTU VWXYZ" = 28 chars unwrapped = 224px max-content
    //                                    longest word "KLMNOPQRSTU" = 11 = 88px min-content
    //   "ABCDEFGHIJ" = 10 chars = 80px (single word, min == max)
    public class FitContentFunctionTests {

        // ---- Helpers -----------------------------------------------------------

        static BlockBox FindById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.Id == id) return bb;
            }
            return null;
        }

        // ---- Inline-block: argument binding (min < arg < max) ------------------

        [Test]
        public void Inline_block_fit_content_arg_binds_when_between_min_and_max() {
            // "Hello World Again": max-content=136px, min-content=40px.
            // fit-content(100px) = min(136, max(40, 100)) = 100.
            var (root, _, _) = Build(
                "<p><span id=\"t\" style=\"display:inline-block;width:fit-content(100px)\">Hello World Again</span></p>",
                null, viewportWidth: 800);
            var span = FindById(root, "t");
            Assert.That(span, Is.Not.Null);
            Assert.That(span.Width, Is.EqualTo(100).Within(1));
        }

        // ---- Inline-block: min-content floor (arg < min-content) ---------------

        [Test]
        public void Inline_block_fit_content_clamps_to_min_content_when_arg_too_small() {
            // "Hello World Again": min-content=40px.
            // fit-content(20px) = min(136, max(40, 20)) = 40 (min-content floor).
            var (root, _, _) = Build(
                "<p><span id=\"t\" style=\"display:inline-block;width:fit-content(20px)\">Hello World Again</span></p>",
                null, viewportWidth: 800);
            var span = FindById(root, "t");
            Assert.That(span, Is.Not.Null);
            Assert.That(span.Width, Is.EqualTo(40).Within(1));
        }

        // ---- Inline-block: max-content ceiling (arg > max-content) -------------

        [Test]
        public void Inline_block_fit_content_clamps_to_max_content_when_arg_too_large() {
            // "Hello World Again": max-content=136px.
            // fit-content(500px) = min(136, max(40, 500)) = 136 (max-content ceiling).
            var (root, _, _) = Build(
                "<p><span id=\"t\" style=\"display:inline-block;width:fit-content(500px)\">Hello World Again</span></p>",
                null, viewportWidth: 800);
            var span = FindById(root, "t");
            Assert.That(span, Is.Not.Null);
            Assert.That(span.Width, Is.EqualTo(136).Within(1));
        }

        // ---- Inline-block: percentage argument ---------------------------------

        [Test]
        public void Inline_block_fit_content_percentage_resolves_against_containing_block() {
            // Parent container: 400px wide.
            // "ABCDEFGHIJ KLMNOPQRSTU VWXYZ": max=224px, min=88px (KLMNOPQRSTU=11 chars).
            // fit-content(50%) = fit-content(200px): min(224, max(88, 200)) = 200.
            const string css = ".wrap { width: 400px; }";
            var (root, _, _) = Build(
                "<div class=\"wrap\"><p><span id=\"t\" style=\"display:inline-block;width:fit-content(50%)\">ABCDEFGHIJ KLMNOPQRSTU VWXYZ</span></p></div>",
                css, viewportWidth: 800);
            var span = FindById(root, "t");
            Assert.That(span, Is.Not.Null);
            Assert.That(span.Width, Is.EqualTo(200).Within(1));
        }

        // ---- Block-level: argument binding ------------------------------------

        [Test]
        public void Block_level_fit_content_arg_binds_when_between_min_and_max() {
            // Block div (not inline-block) with fit-content(100px).
            // "Hello World Again": max=136px, min=40px.
            // fit-content(100px) = min(136, max(40, 100)) = 100.
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(100px)\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Width, Is.EqualTo(100).Within(1));
        }

        [Test]
        public void Block_level_fit_content_clamps_to_min_content_when_arg_too_small() {
            // fit-content(20px): min-content floor = 40px.
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(20px)\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Width, Is.EqualTo(40).Within(1));
        }

        [Test]
        public void Block_level_fit_content_clamps_to_max_content_when_arg_too_large() {
            // fit-content(500px): max-content ceiling = 136px.
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(500px)\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Width, Is.EqualTo(136).Within(1));
        }

        // ---- Grid track: fit-content(<length>) --------------------------------
        //
        // These complement GridFitContentTests.cs (which already covers the core
        // grid track behavior) by verifying the function form from the width/height
        // perspective (tests 5-6 from the task specification).

        [Test]
        public void Grid_track_fit_content_50px_clamps_when_content_max_exceeds_arg() {
            // Column content max-content = "ABCDEFGHIJ" = 10 chars * 8px = 80px.
            // fit-content(50px) = min(80, max(0_or_auto, 50)) = 50.
            const string css = @"
                .grid { display: grid; grid-template-columns: fit-content(50px) 1fr; width: 300px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div id=\"c0\">ABCDEFGHIJ</div><div id=\"c1\"></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            Assert.That(c0, Is.Not.Null);
            Assert.That(c1, Is.Not.Null);
            // fit-content(50px): max-content=80 > 50 → track = 50.
            Assert.That(c0.Width, Is.EqualTo(50).Within(0.5));
            // 1fr takes remaining 250px.
            Assert.That(c1.X, Is.EqualTo(50).Within(0.5));
            Assert.That(c1.Width, Is.EqualTo(250).Within(0.5));
        }

        [Test]
        public void Grid_track_fit_content_200px_uses_max_content_when_content_fits_within_arg() {
            // Column content max-content = "ABCDEFGHIJ" = 80px.
            // fit-content(200px) = min(80, max(0_or_auto, 200)) = 80.
            const string css = @"
                .grid { display: grid; grid-template-columns: fit-content(200px) 1fr; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div id=\"c0\">ABCDEFGHIJ</div><div id=\"c1\"></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            Assert.That(c0, Is.Not.Null);
            Assert.That(c1, Is.Not.Null);
            // fit-content(200px): max-content=80 < 200 → track = 80.
            Assert.That(c0.Width, Is.EqualTo(80).Within(0.5));
            // 1fr takes remaining 320px.
            Assert.That(c1.X, Is.EqualTo(80).Within(0.5));
            Assert.That(c1.Width, Is.EqualTo(320).Within(0.5));
        }
    }
}
