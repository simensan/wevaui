// §13 — display / visibility interactions in flex containers
// (CSS Flexbox L1 §4, CSS Display L3 §2)
// FlexAbsoluteFlowTests.cs covers absolute children not taking a flex slot.
// This file adds:
//   - display:none in-flow removes item from layout
//   - visibility:hidden item still takes space
//   - display:contents child hoists its own children as flex items
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexDisplayVisibilityTests {

        // ── display:none ──────────────────────────────────────────────────

        [Test]
        public void Display_none_flex_item_is_removed_from_layout() {
            // Three items; middle one is display:none. The remaining two
            // should pack as if the middle one were not in the DOM.
            const string css = @"
                .flex { display: flex; width: 600px; }
                .item { width: 100px; height: 50px; }
                .gone { display: none; width: 200px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"gone\"></div>"
                + "<div class=\"item\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // display:none element has no box in the flex container.
            // Two in-flow children only.
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            // b should be adjacent to a (no 200px gap for the none item).
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001),
                "display:none item must leave no gap on the main axis");
        }

        [Test]
        public void Display_none_flex_item_container_still_sized_correctly() {
            // With one of two items hidden, flex-grow on the remaining item
            // should expand to fill the container.
            const string css = @"
                .flex { display: flex; width: 400px; }
                .grow { flex: 1; height: 50px; }
                .gone { display: none; width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"grow\"></div>"
                + "<div class=\"gone\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var grow = ChildAt(fb, 0);
            Assert.That(grow.Width, Is.EqualTo(400).Within(0.001),
                "flex:1 item should fill entire container when other item is display:none");
        }

        // ── visibility:hidden ─────────────────────────────────────────────

        [Test]
        public void Visibility_hidden_flex_item_still_takes_up_space() {
            // visibility:hidden is NOT display:none. The item is invisible
            // but still occupies its main-axis slot.
            const string css = @"
                .flex { display: flex; width: 600px; }
                .item { width: 100px; height: 50px; }
                .hidden { width: 200px; height: 50px; visibility: hidden; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"hidden\"></div>"
                + "<div class=\"item\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // hidden item (200px) is still in layout; third item at X=300.
            var c = ChildAt(fb, 2);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(c.X, Is.EqualTo(300).Within(0.001),
                "visibility:hidden item must still occupy 200px of main-axis space");
        }

        [Test]
        public void Visibility_hidden_item_has_correct_dimensions() {
            const string css = @"
                .flex { display: flex; width: 600px; height: 150px; }
                .hidden { width: 200px; height: 80px; visibility: hidden; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"hidden\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // Even with visibility:hidden, the box must have layout dimensions.
            Assert.That(a.Width, Is.EqualTo(200).Within(0.001));
        }

        // ── display:contents ─────────────────────────────────────────────

        [Test]
        public void Display_contents_flex_item_children_participate_as_flex_items() {
            // A flex item with display:contents is removed from the box tree;
            // its children become direct flex items of the container.
            const string css = @"
                .flex { display: flex; width: 600px; height: 100px; }
                .wrapper { display: contents; }
                .item { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"wrapper\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"item\"></div>"
                + "</div>"
                + "<div class=\"item\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // There should be 3 flex items: wrapper's 2 children + 1 sibling.
            // They all line up horizontally.
            var childCount = 0;
            for (int i = 0; ; i++) {
                var c = ChildAt(fb, i);
                if (c == null) break;
                childCount++;
            }
            Assert.That(childCount, Is.EqualTo(3),
                "display:contents wrapper must hoist its children as flex items (3 total)");
        }

        [Test]
        public void Display_contents_wrapper_leaves_no_gap_on_main_axis() {
            // The display:contents wrapper itself contributes no width; its
            // children pack contiguously with the outer sibling.
            const string css = @"
                .flex { display: flex; width: 600px; height: 100px; }
                .wrapper { display: contents; }
                .item { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"wrapper\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"item\"></div>"
                + "</div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var first = ChildAt(fb, 0);
            var second = ChildAt(fb, 1);
            var third = ChildAt(fb, 2);
            Assert.That(first.X, Is.EqualTo(0).Within(0.001));
            Assert.That(second.X, Is.EqualTo(100).Within(0.001),
                "hoisted first child must follow directly after outer sibling");
            Assert.That(third.X, Is.EqualTo(200).Within(0.001));
        }
    }
}
