// §2 — justify-content extension coverage (CSS Flexbox L1 §8.2)
// FlexJustifyContentTests.cs already covers row+flex-start/flex-end/center/
// space-between/space-around/space-evenly/start. This file adds:
//   - column direction variants
//   - end / left / right keyword aliases
//   - single-item centering edge cases
//   - overflow (items sum > container) with flex-start
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexJustifyContentExtTests {
        // ── Column direction variants ──────────────────────────────────────

        [Test]
        public void Column_flex_start_stacks_from_top() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:300px;justify-content:flex-start\">"
                + "<div style=\"height:50px\"></div><div style=\"height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Column_flex_end_stacks_from_bottom() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:300px;justify-content:flex-end\">"
                + "<div style=\"height:50px\"></div><div style=\"height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            // free = 300 - 100 = 200; a at 200, b at 250
            Assert.That(a.Y, Is.EqualTo(200).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(250).Within(0.001));
        }

        [Test]
        public void Column_center_groups_items_in_middle() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:300px;justify-content:center\">"
                + "<div style=\"height:50px\"></div><div style=\"height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            // free = 200, half = 100; a at 100, b at 150
            Assert.That(a.Y, Is.EqualTo(100).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(150).Within(0.001));
        }

        [Test]
        public void Column_space_between_first_and_last_at_edges() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:400px;justify-content:space-between\">"
                + "<div style=\"height:50px\"></div><div style=\"height:50px\"></div><div style=\"height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(175).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void Column_space_evenly_all_gaps_equal() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:400px;justify-content:space-evenly\">"
                + "<div style=\"height:50px\"></div><div style=\"height:50px\"></div><div style=\"height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // free = 400-150 = 250; 4 gaps each 62.5. a=62.5, b=162.5, c=262.5
            Assert.That(a.Y, Is.EqualTo(62.5).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(175.0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(287.5).Within(0.001));
        }

        // ── Keyword aliases: end / left / right ───────────────────────────

        [Test]
        public void End_keyword_aliases_flex_end_in_row() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:end\">"
                + "<div style=\"width:100px;height:50px\"></div><div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(400).Within(0.001));
            Assert.That(b.X, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void Left_keyword_aliases_flex_start_in_ltr_row() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:left\">"
                + "<div style=\"width:100px;height:50px\"></div><div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Right_keyword_aliases_flex_end_in_ltr_row() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:right\">"
                + "<div style=\"width:100px;height:50px\"></div><div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(400).Within(0.001));
            Assert.That(b.X, Is.EqualTo(500).Within(0.001));
        }

        // ── Single-item edge cases ─────────────────────────────────────────

        [Test]
        public void Single_item_center_is_horizontally_centered() {
            // Space-evenly and center with a single item should still center.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:center\">"
                + "<div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(250).Within(0.001), "single item must be centered at 250px");
        }

        [Test]
        public void Single_item_space_evenly_centers_item() {
            // With 1 item, space-evenly distributes 2 equal gaps (before and
            // after), which equals center.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:space-evenly\">"
                + "<div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(250).Within(0.001), "single item must be centered under space-evenly");
        }

        [Test]
        public void Single_item_space_between_packs_at_start() {
            // With 1 item, space-between has no inter-item gaps, so
            // the item sits at the start of the main axis.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:space-between\">"
                + "<div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001), "single item under space-between goes to start");
        }

        // ── Overflow main axis ────────────────────────────────────────────

        [Test]
        public void FlexStart_does_not_pull_items_into_negative_space_on_overflow() {
            // Three 300px items in a 600px container with flex-shrink:0.
            // Total = 900 > 600; overflow = 300. flex-start: first item at 0.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:flex-start\">"
                + "<div style=\"width:300px;height:50px;flex-shrink:0\"></div>"
                + "<div style=\"width:300px;height:50px;flex-shrink:0\"></div>"
                + "<div style=\"width:300px;height:50px;flex-shrink:0\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001), "overflow with flex-start: first item at 0");
        }

        [Test]
        public void Space_around_with_two_items_distributes_half_gaps_at_edges() {
            // Row, 600px, 2×100px items → free=400 → 4 quarter-gaps of 100.
            // a.X=100, b.X=400.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;justify-content:space-around\">"
                + "<div style=\"width:100px;height:50px\"></div>"
                + "<div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(100).Within(0.001));
            Assert.That(b.X, Is.EqualTo(400).Within(0.001));
        }

        // ── Gap interaction with justify-content ──────────────────────────

        [Test]
        public void Gap_is_subtracted_from_free_space_before_justify_content_distribution() {
            // 600px container, 2×100px items, gap:40px → free after gaps = 360.
            // space-between: a at 0, b at 500 (= 100 + 360 + 40).
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;gap:40px;justify-content:space-between\">"
                + "<div style=\"width:100px;height:50px\"></div>"
                + "<div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            // b at 100+gap+freespace = 500 (free after gap = 360, only one slot between 2 items)
            Assert.That(b.X, Is.EqualTo(500).Within(0.001));
        }
    }
}
