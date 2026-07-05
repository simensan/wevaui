using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class AspectRatioTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        [Test]
        public void Aspect_ratio_16_by_9_with_explicit_width_derives_height() {
            var (root, _, _) = Build(
                "<div style=\"width:300px;aspect-ratio:16/9\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(300.0 * 9.0 / 16.0).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_with_explicit_height_derives_width() {
            var (root, _, _) = Build(
                "<div style=\"height:90px;aspect-ratio:16/9\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(90).Within(0.001));
            Assert.That(div.Width, Is.EqualTo(160).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_ignored_when_both_dimensions_set() {
            var (root, _, _) = Build(
                "<div style=\"width:200px;height:50px;aspect-ratio:16/9\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Aspect_ratio_auto_has_no_effect() {
            var (root, _, _) = Build(
                "<div style=\"width:300px;aspect-ratio:auto\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Aspect_ratio_single_number_means_n_over_1() {
            var (root, _, _) = Build(
                "<div style=\"width:200px;aspect-ratio:2\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_zero_or_negative_does_nothing() {
            var (root, _, _) = Build(
                "<div style=\"width:200px;aspect-ratio:0\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Aspect_ratio_one_to_one_makes_square() {
            var (root, _, _) = Build(
                "<div style=\"width:120px;aspect-ratio:1/1\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(120).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_overrides_content_height_when_height_auto() {
            var (root, _, _) = Build(
                "<div style=\"width:160px;aspect-ratio:16/9\"><span>tiny</span></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            // 160 / (16/9) = 90
            Assert.That(div.Height, Is.EqualTo(90).Within(0.5));
        }

        [Test]
        public void Aspect_ratio_decimal_number() {
            var (root, _, _) = Build(
                "<div style=\"width:300px;aspect-ratio:1.5\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(200).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_initial_value_is_auto_no_effect() {
            var (root, _, _) = Build("<div style=\"width:150px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(150).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(0).Within(0.001));
        }

        // Spec test (randhtml audit): the demo's `.minimap, .ratio-box, .world::after`
        // pattern relies on `width: <something>; aspect-ratio: 1` resolving to a
        // square box. Without ratio derivation the sun (`.world::after`) collapses
        // to height 0 and never renders.
        [Test]
        public void Aspect_ratio_one_with_explicit_width_sets_height() {
            var (root, _, _) = Build(
                "<div style=\"width:100px;aspect-ratio:1\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(100).Within(0.01));
        }

        // Symmetric case: explicit height with auto width must derive width
        // from height * ratio.
        [Test]
        public void Aspect_ratio_with_explicit_height_sets_width() {
            var (root, _, _) = Build(
                "<div style=\"height:100px;aspect-ratio:2\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(100).Within(0.001));
            Assert.That(div.Width, Is.EqualTo(200).Within(0.01));
        }
    }
}
