// Regression coverage for K4 (CSS_COMPLIANCE_ISSUES.md): the flex wrap
// classification used a fixed 0.001px epsilon, which is tighter than
// realistic sub-pixel layout jitter (FontEngine fractional advances,
// accumulated double rounding in flex base-size resolution). Items that
// resolved a hair under the container width could spuriously wrap across
// re-layouts. The new tolerance is `containerMainSize * 1e-5` clamped to a
// 0.01px floor — below half a CSS px, so genuine half-pixel overflows still
// wrap.

using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexWrapEpsilonTests {
        [Test]
        public void Wrap_does_not_trigger_when_total_within_layout_noise_K4() {
            // Three items of (100/3 + 0.0003)px ≈ 33.3336333...px each. Sum is
            // 100.0009px in a 100px container — 0.0009px over, well below the
            // 0.01px tolerance floor. Pre-fix (0.001 epsilon) this DID wrap;
            // post-fix it must remain on a single line.
            const string css = @"
                .flex { display: flex; flex-wrap: wrap; width: 100px; }
                .item { width: 33.3336333333px; height: 10px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // All three items must share the same line (Y == 0).
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001), "first item should be on line 1");
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001), "second item should be on line 1");
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001),
                "third item must NOT wrap to line 2 — total overflow (0.0009px) is below the K4 epsilon floor");
        }

        [Test]
        public void Wrap_triggers_when_overflow_exceeds_half_pixel_K4() {
            // Three items of 33.5px each = 100.5px in a 100px container. The
            // overflow (0.5px) is far above the epsilon floor (0.01px) and is
            // visually distinguishable, so wrap MUST fire on the third item.
            const string css = @"
                .flex { display: flex; flex-wrap: wrap; width: 100px; }
                .item { width: 33.5px; height: 10px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001), "first item should be on line 1");
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001), "second item should be on line 1");
            Assert.That(c.Y, Is.EqualTo(10).Within(0.001),
                "third item must wrap to line 2 — 0.5px overflow is well above the K4 epsilon");
            Assert.That(c.X, Is.EqualTo(0).Within(0.001), "wrapped item resets to inline-start");
        }

        [Test]
        public void Wrap_does_not_trigger_at_exact_container_size_K4() {
            // Canonical boundary: items sum to exactly the container's main
            // size. Spec wraps only when the line's hypothetical main size
            // EXCEEDS the container, so all items stay on one line.
            const string css = @"
                .flex { display: flex; flex-wrap: wrap; width: 100px; }
                .item { height: 10px; flex-shrink: 0; }
                .a { width: 30px; }
                .b { width: 30px; }
                .c { width: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item a\"></div><div class=\"item b\"></div><div class=\"item c\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001),
                "items summing to exactly the container width must not wrap (spec: strict >)");
            Assert.That(c.X, Is.EqualTo(60).Within(0.001), "third item sits at 30+30 along the main axis");
        }
    }
}
