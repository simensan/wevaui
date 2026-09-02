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

        // CSS Sizing L4 §5. The ratio relates the two dimensions of the box
        // `box-sizing` selects, so with the default content-box it relates
        // CONTENT width to CONTENT height. Every one of the four places that
        // derived a dimension divided the BORDER-box width and then added the
        // vertical frame, counting the frame twice. Verified against Chrome on
        // each of these three shapes: all give 103, not 105.
        static double HeightOf(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.ClassName == cls) return b.Height;
            }
            return double.NaN;
        }

        static double WidthOf(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.ClassName == cls) return b.Width;
            }
            return double.NaN;
        }

        [Test]
        public void Content_box_ratio_does_not_count_the_border_twice_in_block_flow() {
            const string css = @"
                body { margin: 0 }
                .p { width: 103px }
                .s { width: 101px; aspect-ratio: 1 / 1; border-width: 1px;
                     border-style: solid }
            ";
            var (root, _, _) = Build(@"<div class=""p""><div class=""s""></div></div>", css);
            // content 101 + 1px border each side = 103 border-box, both axes.
            Assert.That(WidthOf(root, "s"), Is.EqualTo(103).Within(1e-9));
            Assert.That(HeightOf(root, "s"), Is.EqualTo(103).Within(1e-9));
        }

        [Test]
        public void Content_box_ratio_does_not_count_the_border_twice_in_a_grid_item() {
            const string css = @"
                body { margin: 0 }
                .g { width: 103px; display: grid; grid-template-columns: 1fr }
                .s { aspect-ratio: 1 / 1; border-width: 1px; border-style: solid }
            ";
            var (root, _, _) = Build(@"<div class=""g""><div class=""s""></div></div>", css);
            Assert.That(WidthOf(root, "s"), Is.EqualTo(103).Within(1e-9));
            Assert.That(HeightOf(root, "s"), Is.EqualTo(103).Within(1e-9));
        }

        [Test]
        public void Content_box_ratio_does_not_count_the_border_twice_in_a_flex_item() {
            const string css = @"
                body { margin: 0 }
                .f { width: 103px; display: flex }
                .s { flex: 1; aspect-ratio: 1 / 1; border-width: 1px;
                     border-style: solid }
            ";
            var (root, _, _) = Build(@"<div class=""f""><div class=""s""></div></div>", css);
            Assert.That(WidthOf(root, "s"), Is.EqualTo(103).Within(1e-9));
            Assert.That(HeightOf(root, "s"), Is.EqualTo(103).Within(1e-9));
        }

        [Test]
        public void Border_box_ratio_is_unchanged() {
            // The border-box case has no frame to subtract, and must stay put.
            const string css = @"
                body { margin: 0 }
                .p { width: 103px }
                .s { width: 103px; box-sizing: border-box; aspect-ratio: 1 / 1;
                     border-width: 1px; border-style: solid }
            ";
            var (root, _, _) = Build(@"<div class=""p""><div class=""s""></div></div>", css);
            Assert.That(WidthOf(root, "s"), Is.EqualTo(103).Within(1e-9));
            Assert.That(HeightOf(root, "s"), Is.EqualTo(103).Within(1e-9));
        }


        static double TopOf(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.ClassName == cls) {
                    double y = 0;
                    for (var p = b; p != null; p = p.Parent) y += p.Y;
                    return y;
                }
            }
            return double.NaN;
        }

        // A flex container whose cross size comes from `aspect-ratio` derived
        // that cross as a BORDER-box size and assigned it into the variable
        // holding the CONTENT cross size. The line then had an extra frame of
        // free space, and `align-items: center` pushed the item down by half of
        // it on top of the content-top offset it already had. The error tracked
        // the border exactly, which is what identified it.
        [Test]
        public void Flex_item_centres_in_the_content_box_of_an_aspect_ratio_container() {
            const string css = @"
                body { margin: 0 }
                .s { width: 200px; aspect-ratio: 2 / 1; display: flex;
                     align-items: center; justify-content: center;
                     border-width: 1px; border-style: solid }
                .g { font-size: 28px }
            ";
            var (root, _, _) = Build(@"<div class=""s""><span class=""g"">A</span></div>", css);
            // Border box 202 wide -> content 200 -> content height 100 -> border
            // box 102. Chrome puts the 32px glyph at 1 + (100 - 32) / 2 = 35.
            double container = HeightOf(root, "s");
            Assert.That(container, Is.EqualTo(102).Within(1e-9));
            double glyph = TopOf(root, "g");
            double glyphH = HeightOf(root, "g");
            Assert.That(glyph, Is.EqualTo(1 + (100 - glyphH) / 2).Within(1e-6));
        }

        [Test]
        public void The_centring_error_does_not_scale_with_the_border() {
            // Same shape with a 5px border: the old code was out by the border
            // width, so this case pins that the frame is not double-counted.
            const string css = @"
                body { margin: 0 }
                .s { width: 200px; aspect-ratio: 2 / 1; display: flex;
                     align-items: center; justify-content: center;
                     border-width: 5px; border-style: solid }
                .g { font-size: 28px }
            ";
            var (root, _, _) = Build(@"<div class=""s""><span class=""g"">A</span></div>", css);
            Assert.That(HeightOf(root, "s"), Is.EqualTo(110).Within(1e-9));
            double glyphH = HeightOf(root, "g");
            Assert.That(TopOf(root, "g"), Is.EqualTo(5 + (100 - glyphH) / 2).Within(1e-6));
        }

    }
}
