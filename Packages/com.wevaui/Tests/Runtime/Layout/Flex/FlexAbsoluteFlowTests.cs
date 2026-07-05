using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAbsoluteFlowTests {
        // CSS Flexbox §3: an absolutely-positioned child of a flex container is
        // not a flex item — it doesn't take a slot in the line, doesn't contribute
        // to main-axis sizing math, and is positioned by the regular `position`
        // pass against its containing block (which is the flex container if the
        // container is itself positioned).

        [Test]
        public void Absolute_flex_child_takes_no_main_axis_slot() {
            const string css = @"
                .flex { display: flex; width: 600px; height: 100px; position: relative; }
                .a { width: 100px; height: 50px; }
                .b { width: 100px; height: 50px; }
                .ghost { position: absolute; top: 0; left: 0; width: 80px; height: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"a\"></div><div class=\"ghost\"></div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // ChildAt(fb, 1) is the ghost (still a child of the box tree); the
            // in-flow item we want to assert about is at child-position 2.
            var b = ChildAt(fb, 2);
            // Without `ghost` consuming a slot, "b" sits immediately after "a".
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Fixed_flex_child_takes_no_main_axis_slot() {
            const string css = @"
                .flex { display: flex; width: 600px; height: 100px; position: relative; }
                .a { width: 100px; height: 50px; }
                .b { width: 100px; height: 50px; }
                .pinned { position: fixed; top: 0; left: 0; width: 80px; height: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"a\"></div><div class=\"pinned\"></div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 2);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Sibling_positions_match_what_they_would_be_without_absolute_child() {
            // Three in-flow children with one abs interleaved → in-flow children's
            // positions should be identical to the no-abs case.
            const string cssWith = @"
                .flex { display: flex; gap: 10px; width: 600px; height: 100px; position: relative; }
                .item { width: 80px; height: 50px; }
                .abs { position: absolute; top: 0; left: 200px; width: 50px; height: 50px; }
            ";
            const string cssWithout = @"
                .flex { display: flex; gap: 10px; width: 600px; height: 100px; position: relative; }
                .item { width: 80px; height: 50px; }
            ";
            var (rootA, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"abs\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                cssWith, viewportWidth: 800);
            var (rootB, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                cssWithout, viewportWidth: 800);

            var fbA = FindFlex(rootA, "div");
            var fbB = FindFlex(rootB, "div");
            // In-flow indices in A: 0, 2, 3. In B: 0, 1, 2.
            var aFirst = ChildAt(fbA, 0);
            var aSecond = ChildAt(fbA, 2);
            var aThird = ChildAt(fbA, 3);
            var bFirst = ChildAt(fbB, 0);
            var bSecond = ChildAt(fbB, 1);
            var bThird = ChildAt(fbB, 2);

            Assert.That(aFirst.X, Is.EqualTo(bFirst.X).Within(0.001));
            Assert.That(aSecond.X, Is.EqualTo(bSecond.X).Within(0.001));
            Assert.That(aThird.X, Is.EqualTo(bThird.X).Within(0.001));
        }

        [Test]
        public void Absolute_flex_child_positioned_by_top_left_against_positioned_flex_container() {
            const string css = @"
                .flex { display: flex; position: relative; width: 600px; height: 200px; margin-top: 50px; margin-left: 80px; }
                .item { width: 100px; height: 50px; }
                .abs { position: absolute; top: 30px; left: 200px; width: 40px; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"abs\"></div></div>",
                css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            var (ax, ay) = AbsoluteOriginOf(abs);
            // Flex container starts at (80, 50); the abs child is positioned
            // relative to that with top:30, left:200.
            Assert.That(ax, Is.EqualTo(80 + 200).Within(0.001));
            Assert.That(ay, Is.EqualTo(50 + 30).Within(0.001));
        }

        [Test]
        public void Absolute_flex_child_stretches_with_all_four_offsets_zero() {
            const string css = @"
                .flex { display: flex; position: relative; width: 400px; height: 300px; }
                .item { width: 100px; height: 50px; }
                .abs { position: absolute; top: 0; right: 0; bottom: 0; left: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"abs\"></div></div>",
                css, viewportWidth: 800);
            var abs = FirstByClass(root, "abs");
            Assert.That(abs.Width, Is.EqualTo(400).Within(0.001));
            Assert.That(abs.Height, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Mixing_absolute_and_in_flow_does_not_break_flex_grow_math() {
            // Two flex:1 children + one absolute. The two grow children should
            // each receive half the container's width (600/2 = 300), regardless
            // of the absolute child's presence.
            const string css = @"
                .flex { display: flex; position: relative; width: 600px; height: 100px; }
                .grow { flex: 1; height: 50px; }
                .abs { position: absolute; top: 0; left: 0; width: 99px; height: 99px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"grow\"></div><div class=\"abs\"></div><div class=\"grow\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var g1 = ChildAt(fb, 0);
            var g2 = ChildAt(fb, 2);
            Assert.That(g1.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(g2.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(g1.X, Is.EqualTo(0).Within(0.001));
            Assert.That(g2.X, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Absolute_flex_child_does_not_count_in_justify_content_distribution() {
            // justify-content: space-between with two in-flow items + one abs:
            // the two in-flow items should be distributed to the two ends of
            // the container (the abs doesn't pull on the distribution).
            const string css = @"
                .flex { display: flex; justify-content: space-between; position: relative; width: 600px; height: 100px; }
                .item { width: 100px; height: 50px; }
                .abs { position: absolute; top: 0; left: 0; width: 50px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"abs\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var first = ChildAt(fb, 0);
            var last = ChildAt(fb, 2);
            Assert.That(first.X, Is.EqualTo(0).Within(0.001));
            Assert.That(last.X, Is.EqualTo(500).Within(0.001));
        }
    }
}
