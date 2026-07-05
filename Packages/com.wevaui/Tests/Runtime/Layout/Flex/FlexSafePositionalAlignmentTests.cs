using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Bug E6: CSS Align L3 lets authors prefix an alignment keyword with
    // `safe`/`unsafe` (e.g. `justify-content: safe center`). Before the fix
    // those keywords fell through to the `fallback` value and the alignment
    // was silently ignored. For v1 we accept the syntax and apply the
    // alignment keyword; overflow-safe fallback semantics at layout time are
    // a follow-up.
    public class FlexSafePositionalAlignmentTests {
        const string Css = @"
            .flex { display: flex; flex-direction: row; width: 600px; height: 200px; }
            .item { width: 100px; height: 50px; }
        ";

        static (double x0, double x1, double x2) RunThreeJustify(string justify) {
            var html = "<div class=\"flex\" style=\"justify-content:" + justify + "\">"
                + "<div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>";
            var (root, _, _) = Build(html, Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            return (ChildAt(fb, 0).X, ChildAt(fb, 1).X, ChildAt(fb, 2).X);
        }

        [Test]
        public void JustifyContent_safe_center_centers_items() {
            var (a, b, c) = RunThreeJustify("safe center");
            Assert.That(a, Is.EqualTo(150).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void JustifyContent_unsafe_center_centers_items() {
            var (a, b, c) = RunThreeJustify("unsafe center");
            Assert.That(a, Is.EqualTo(150).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void JustifyContent_safe_end_packs_items_at_end() {
            var (a, b, c) = RunThreeJustify("safe end");
            Assert.That(a, Is.EqualTo(300).Within(0.001));
            Assert.That(b, Is.EqualTo(400).Within(0.001));
            Assert.That(c, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void JustifyContent_safe_flex_end_packs_items_at_end() {
            var (a, b, c) = RunThreeJustify("safe flex-end");
            Assert.That(a, Is.EqualTo(300).Within(0.001));
            Assert.That(b, Is.EqualTo(400).Within(0.001));
            Assert.That(c, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void JustifyContent_bare_center_still_centers_items() {
            // Regression guard: the unwrap path must not disturb the
            // single-keyword form.
            var (a, b, c) = RunThreeJustify("center");
            Assert.That(a, Is.EqualTo(150).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void AlignItems_safe_center_centers_items_on_cross_axis() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:safe center\"><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // container height 200, item height 50 -> centered at y = 75
            Assert.That(ChildAt(fb, 0).Y, Is.EqualTo(75).Within(0.001));
        }

        [Test]
        public void AlignSelf_safe_end_overrides_align_items_per_child() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:flex-start\">"
                + "<div class=\"item\" style=\"align-self:safe end\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // container height 200, item height 50 -> end at y = 150
            Assert.That(ChildAt(fb, 0).Y, Is.EqualTo(150).Within(0.001));
        }
    }
}
