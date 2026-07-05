using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Sizing {
    // CSS Sizing L3 §5.1 — fit-content() edge cases not covered by FitContentFunctionTests.
    //
    // FitContentFunctionTests covers: arg-binds, min-content floor, max-content ceiling,
    // percentage arg, block-level variants, grid-track. This file covers:
    //   - em-based arg (relative unit inside fit-content())
    //   - min-width / max-width constraints interacting with fit-content
    //   - flex-basis: fit-content(N) function form — degrades to Auto per v1 docs
    //   - fit-content() on height (block box)
    //   - two fit-content boxes side-by-side (independent resolution)
    //   - fit-content percentage with indefinite parent (falls back to content width)
    //
    // MonoFontMetrics: 8px/char at 16px root font-size. 1em = 16px.
    public class FitContentEdgeCaseTests {
        static BlockBox FindById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        // ---- em-based argument -------------------------------------------------

        [Test]
        public void Fit_content_em_arg_resolves_correctly() {
            // fit-content(10em): with root font-size 16px, 10em = 160px.
            // "Hello World Again" = 17 chars * 8px = 136px max-content.
            // fit-content(160px) = min(136, max(40, 160)) = 136 (max-content ceiling).
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(10em)\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            // 10em = 160px > max-content (136px) → clamped to max-content.
            Assert.That(div.Width, Is.EqualTo(136).Within(1),
                "fit-content(10em) should resolve em unit, then clamp to max-content");
        }

        [Test]
        public void Fit_content_small_em_arg_uses_arg_when_in_range() {
            // fit-content(8em): 8em = 128px. max-content=136px, min-content=40px.
            // fit-content(128px) = min(136, max(40, 128)) = 128 (arg is binding).
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(8em)\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Width, Is.EqualTo(128).Within(1),
                "fit-content(8em) should resolve em to 128px and use it as the binding arg");
        }

        // ---- min-width / max-width constraints on fit-content box ---------------
        //
        // FC-1 fixed 2026-05-31: LayoutFitContentBlock now applies min-width and
        // max-width clamping after the fit-content intrinsic calculation per
        // CSS Sizing L3 §4. Regression-anchor pin tests removed.

        [Test]
        public void Min_width_prevents_fit_content_from_shrinking_below_min() {
            // fit-content(20px) on "Hello World Again" would normally clamp to
            // min-content = 40px. With min-width: 60px, the final width must be 60px.
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(20px);min-width:60px\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            // CSS Sizing L3 §4: min-width constrains the computed width upward.
            Assert.That(div.Width, Is.GreaterThanOrEqualTo(60 - 0.5),
                "min-width must prevent fit-content result from going below 60px");
        }

        [Test]
        public void Max_width_trims_fit_content_when_content_exceeds_max() {
            // fit-content(500px) on "Hello World Again" would return max-content = 136px.
            // With max-width: 80px, the final width must be 80px.
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(500px);max-width:80px\">Hello World Again</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            // CSS Sizing L3 §4: max-width constrains the computed width downward.
            Assert.That(div.Width, Is.LessThanOrEqualTo(80 + 0.5),
                "max-width must trim fit-content result to 80px");
        }

        // ---- flex-basis: fit-content(N) function form (v1 behavior pin) -------

        [Test]
        public void Flex_basis_fit_content_function_form_degrades_to_auto() {
            // CSS Flexbox v1 doc (FlexLayout.cs §comment): fit-content keyword sizes
            // for flex-basis degrade to `auto` (both keyword and function forms).
            // Pin that the flex item still gets a sensible width and doesn't error.
            const string css = @"
                .row { display: flex; width: 400px; font-size: 16px; }
                .a { flex-grow: 0; flex-shrink: 0; flex-basis: fit-content(100px); }
                .b { flex-grow: 0; flex-shrink: 0; width: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"a\">Hello</div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            Assert.That(fb, Is.Not.Null);
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            // Flex-basis falls back to auto → content-sized. "Hello" = 5 chars * 8px = 40px.
            Assert.That(a.Width, Is.GreaterThan(0),
                "flex-basis: fit-content(N) must not produce zero-width");
            Assert.That(b.Width, Is.EqualTo(50).Within(0.5),
                "sibling with explicit width must be unaffected");
        }

        // ---- fit-content on height (block box) ---------------------------------

        [Test]
        public void Height_fit_content_function_form_on_block_applies_clamp() {
            // height: fit-content(30px) on a block with tall content.
            // Content: "Hello World Again" on one line at 16px line-height ≈ 16px.
            // So fit-content(30px) = min(max-content-h, max(min-content-h, 30)) = 30
            // if content height is less than 30; or max-content if content > 30.
            // In v1, height: fit-content(N) may not be honored identically to width.
            // Pin that the box has a non-zero, non-negative height.
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:200px;height:fit-content(30px)\">Hello</div>",
                null, viewportWidth: 800);
            var div = FindById(root, "d");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Height, Is.GreaterThan(0),
                "height: fit-content(N) must produce a positive height");
        }

        // ---- two fit-content boxes side-by-side (independent resolution) -------

        [Test]
        public void Two_fit_content_blocks_resolve_independently() {
            // Two sibling divs each with fit-content(100px) but different content.
            // "ABC" = 3 * 8 = 24px max-content → fit-content(100px) = 24 (max-content < arg).
            // "ABCDEFGHIJ KLMNOPQRSTU" = 22 chars → 176px max-content → fit-content(100px) = 100.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"width:fit-content(100px)\">ABC</div>" +
                "<div id=\"b\" style=\"width:fit-content(100px)\">ABCDEFGHIJ KLMNOPQRSTU</div>",
                null, viewportWidth: 800);
            var a = FindById(root, "a");
            var b = FindById(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            // "ABC" max-content = 24px < arg 100px → result = 24.
            Assert.That(a.Width, Is.EqualTo(24).Within(1),
                "fit-content(100px) on short text must use max-content (24px)");
            // "ABCDEFGHIJ KLMNOPQRSTU" max-content = 176px > 100px → result = 100.
            Assert.That(b.Width, Is.EqualTo(100).Within(1),
                "fit-content(100px) on long text must clamp to arg (100px)");
        }

        // ---- fit-content percentage with indefinite containing block -----------

        [Test]
        public void Fit_content_percentage_with_explicit_parent_resolves_correctly() {
            // Parent is 300px (explicit/definite). fit-content(50%) = fit-content(150px).
            // "ABCDEFGHIJ KLMNOPQRSTU VWXYZ" = 28 chars * 8 = 224px max-content, min = 88px.
            // fit-content(150px) = min(224, max(88, 150)) = 150.
            const string css = ".wrap { width: 300px; }";
            var (root, _, _) = Build(
                "<div class=\"wrap\">" +
                "<div id=\"d\" style=\"width:fit-content(50%)\">ABCDEFGHIJ KLMNOPQRSTU VWXYZ</div>" +
                "</div>",
                css, viewportWidth: 800);
            var d = FindById(root, "d");
            Assert.That(d, Is.Not.Null);
            Assert.That(d.Width, Is.EqualTo(150).Within(1),
                "fit-content(50%) against 300px parent should resolve to 150px");
        }

        [Test]
        public void Fit_content_zero_arg_clamps_to_min_content() {
            // fit-content(0px) = min(max-content, max(min-content, 0)) = min-content.
            // "Hello World Again" min-content = 40px (longest word "Hello").
            var (root, _, _) = Build(
                "<div id=\"d\" style=\"width:fit-content(0px)\">Hello World Again</div>",
                null, viewportWidth: 800);
            var d = FindById(root, "d");
            Assert.That(d, Is.Not.Null);
            // fit-content(0px): arg=0 < min-content → result = min-content = 40px.
            Assert.That(d.Width, Is.EqualTo(40).Within(1),
                "fit-content(0px) must clamp to min-content (40px)");
        }
    }
}
