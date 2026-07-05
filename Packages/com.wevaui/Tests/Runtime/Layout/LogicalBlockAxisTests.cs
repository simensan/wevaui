using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Logical Properties L1 §4 — block-axis logical properties in layout.
    //
    // In LTR horizontal writing (the default), the block axis runs top-to-bottom:
    //   block-start = top, block-end = bottom.
    //
    // These tests verify that block-axis logical properties produce the same
    // layout metric as the equivalent physical property. The LogicalLayoutTests.cs
    // already covers inline-axis (margin-inline-*, padding-inline-*, border-inline-*)
    // and inset-block under positioned writing-mode contexts. This file focuses on
    // the block-axis in normal block flow: padding-block-*, margin-block-*,
    // border-block-*, block-size, and inset-block-* on positioned elements.
    //
    // Spec: CSS Logical Properties L1 §4.
    //   block-start maps to `top`, block-end maps to `bottom` (ltr, horizontal-tb).
    public class LogicalBlockAxisTests {
        static BlockBox FindBlock(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        // ---- padding-block-start / padding-block-end ----

        [Test]
        public void Padding_block_start_maps_to_padding_top_in_ltr() {
            // LTR + horizontal-tb: block-start = top. padding-block-start: 20px
            // must yield the same top-padding as padding-top: 20px.
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { padding-block-start: 20px; width: 100px; height: 40px; } " +
                "#b { padding-top: 20px; width: 100px; height: 40px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.PaddingTop, Is.EqualTo(b.PaddingTop).Within(0.001),
                "padding-block-start must equal padding-top in LTR horizontal writing");
        }

        [Test]
        public void Padding_block_end_maps_to_padding_bottom_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { padding-block-end: 15px; width: 100px; height: 40px; } " +
                "#b { padding-bottom: 15px; width: 100px; height: 40px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a.PaddingBottom, Is.EqualTo(b.PaddingBottom).Within(0.001),
                "padding-block-end must equal padding-bottom in LTR horizontal writing");
        }

        [Test]
        public void Padding_block_shorthand_sets_top_and_bottom() {
            // padding-block: 10px 20px expands to padding-block-start=10px,
            // padding-block-end=20px → padding-top=10px, padding-bottom=20px.
            var (root, _, _) = Build(
                "<div id=\"x\"></div>",
                "#x { padding-block: 10px 20px; width: 80px; height: 30px; }",
                viewportWidth: 400);
            var box = FindBlock(root, "x");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.PaddingTop, Is.EqualTo(10).Within(0.001));
            Assert.That(box.PaddingBottom, Is.EqualTo(20).Within(0.001));
        }

        // ---- margin-block-start / margin-block-end ----

        [Test]
        public void Margin_block_start_maps_to_margin_top_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { margin-block-start: 30px; width: 100px; height: 20px; } " +
                "#b { margin-top: 30px; width: 100px; height: 20px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a.MarginTop, Is.EqualTo(b.MarginTop).Within(0.001),
                "margin-block-start must equal margin-top in LTR horizontal writing");
        }

        [Test]
        public void Margin_block_end_maps_to_margin_bottom_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { margin-block-end: 12px; width: 100px; height: 20px; } " +
                "#b { margin-bottom: 12px; width: 100px; height: 20px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a.MarginBottom, Is.EqualTo(b.MarginBottom).Within(0.001),
                "margin-block-end must equal margin-bottom in LTR horizontal writing");
        }

        [Test]
        public void Margin_block_shorthand_single_value_applies_to_both_sides() {
            // margin-block: 8px → margin-top=8px AND margin-bottom=8px.
            var (root, _, _) = Build(
                "<div id=\"x\"></div>",
                "#x { margin-block: 8px; width: 80px; height: 20px; }",
                viewportWidth: 400);
            var box = FindBlock(root, "x");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.MarginTop, Is.EqualTo(8).Within(0.001));
            Assert.That(box.MarginBottom, Is.EqualTo(8).Within(0.001));
        }

        // ---- border-block-start / border-block-end ----

        [Test]
        public void Border_block_start_maps_to_border_top_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { border-block-start: 4px solid red; width: 100px; height: 20px; } " +
                "#b { border-top: 4px solid red; width: 100px; height: 20px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a.BorderTop, Is.EqualTo(b.BorderTop).Within(0.001),
                "border-block-start must produce the same top-border thickness as border-top");
        }

        [Test]
        public void Border_block_end_maps_to_border_bottom_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { border-block-end: 6px solid blue; width: 100px; height: 20px; } " +
                "#b { border-bottom: 6px solid blue; width: 100px; height: 20px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a.BorderBottom, Is.EqualTo(b.BorderBottom).Within(0.001),
                "border-block-end must produce the same bottom-border thickness as border-bottom");
        }

        // ---- block-size (height in LTR horizontal-tb) ----

        [Test]
        public void Block_size_maps_to_height_in_ltr() {
            // block-size: 80px → height: 80px for LTR horizontal-tb writing.
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { block-size: 80px; width: 100px; } " +
                "#b { height: 80px; width: 100px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Height, Is.EqualTo(b.Height).Within(0.001),
                "block-size must equal height in LTR horizontal writing");
        }

        // ---- inset-block-start / inset-block-end in absolute positioning ----

        [Test]
        public void Inset_block_start_maps_to_top_in_absolute_positioned_ltr() {
            // In LTR horizontal-tb, inset-block-start = top.
            // CSS Logical Properties L1 §4.
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 200px; } " +
                "#abs  { position: absolute; block-size: 40px; inline-size: 60px; " +
                "        inset-block-start: 50px; inset-inline-start: 0px; }",
                viewportWidth: 400);
            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.Y, Is.EqualTo(50).Within(0.001),
                "inset-block-start must map to `top` offset (Y) in LTR horizontal writing");
        }

        [Test]
        public void Inset_block_end_positions_bottom_edge_in_ltr() {
            // inset-block-end (= bottom) combined with block-size defines a
            // box pinned from the bottom of the containing block.
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 200px; } " +
                "#abs  { position: absolute; block-size: 40px; inline-size: 60px; " +
                "        inset-block-end: 20px; inset-inline-start: 0px; }",
                viewportWidth: 400);
            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            // bottom edge = host-height(200) - inset-block-end(20) = 180 → Y = 140
            Assert.That(abs.Y, Is.EqualTo(200 - 20 - 40).Within(0.001),
                "inset-block-end must map to `bottom` in LTR; Y = containerH - bottom - height");
        }

        // ---- cascade conflict: logical vs physical same axis ----

        [Test]
        public void Physical_padding_top_overrides_block_start_when_declared_later() {
            // CSS Logical Properties L1 §1.2: physical and logical map to the same
            // property; the later declaration in source order wins.
            var (root, _, _) = Build(
                "<div id=\"x\"></div>",
                "#x { padding-block-start: 30px; padding-top: 5px; width: 80px; height: 20px; }",
                viewportWidth: 400);
            var box = FindBlock(root, "x");
            Assert.That(box, Is.Not.Null);
            // padding-top (5px) is declared after padding-block-start (30px) → 5px wins.
            Assert.That(box.PaddingTop, Is.EqualTo(5).Within(0.001),
                "physical padding-top declared after logical must win via cascade order");
        }

        [Test]
        public void Logical_padding_block_start_overrides_physical_when_declared_later() {
            var (root, _, _) = Build(
                "<div id=\"x\"></div>",
                "#x { padding-top: 5px; padding-block-start: 30px; width: 80px; height: 20px; }",
                viewportWidth: 400);
            var box = FindBlock(root, "x");
            Assert.That(box, Is.Not.Null);
            // padding-block-start (30px) is declared after padding-top (5px) → 30px wins.
            Assert.That(box.PaddingTop, Is.EqualTo(30).Within(0.001),
                "logical padding-block-start declared after physical must win via cascade order");
        }
    }
}
