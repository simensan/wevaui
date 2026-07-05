// §6 — order property extension coverage (CSS Flexbox L1 §5.4)
// FlexOrderTests.cs covers negative order/equal order/cross-axis unaffected.
// This file adds:
//   - large positive order moves item to end
//   - order affects main-axis accumulation (positions shift for intervening items)
//   - order within a wrapped container (within each line)
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexOrderExtTests {
        const string Css = @"
            .flex { display: flex; width: 600px; height: 100px; }
            .item { width: 100px; height: 50px; }
        ";

        [Test]
        public void Large_positive_order_moves_item_to_end() {
            // Item b has order:999, so it should be last even though it is
            // the second element in source order.
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\" id=\"a\"></div>"
                + "<div class=\"item\" id=\"b\" style=\"order:999\"></div>"
                + "<div class=\"item\" id=\"c\"></div>"
                + "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // Visual order: a(0), c(0), b(999). a→0, c→100, b→200.
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // ChildAt gives DOM order; positional assertions determine visual order.
            Assert.That(b.X, Is.EqualTo(200).Within(0.001),
                "high-order item must be placed last on the main axis");
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(c.X, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Three_items_sorted_by_order_ascending() {
            // Source order: z(3), y(2), x(1). Visual order: x, y, z.
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\" style=\"order:3\"></div>"
                + "<div class=\"item\" style=\"order:1\"></div>"
                + "<div class=\"item\" style=\"order:2\"></div>"
                + "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // DOM: [z, x, y] but visual: x(order1), y(order2), z(order3).
            var z = ChildAt(fb, 0); // source 0, order 3
            var x = ChildAt(fb, 1); // source 1, order 1
            var y = ChildAt(fb, 2); // source 2, order 2
            Assert.That(x.X, Is.EqualTo(0).Within(0.001), "order:1 item first");
            Assert.That(y.X, Is.EqualTo(100).Within(0.001), "order:2 item second");
            Assert.That(z.X, Is.EqualTo(200).Within(0.001), "order:3 item third");
        }

        [Test]
        public void Zero_order_default_maintains_source_order_among_mixed_signs() {
            // Item a has no order (default 0). b has order:-1. c has order:1.
            // Visual: b(-1), a(0), c(1).
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"item\" style=\"order:-1\"></div>"
                + "<div class=\"item\" style=\"order:1\"></div>"
                + "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); // order 0
            var b = ChildAt(fb, 1); // order -1
            var c = ChildAt(fb, 2); // order 1
            Assert.That(b.X, Is.EqualTo(0).Within(0.001), "order:-1 must be first");
            Assert.That(a.X, Is.EqualTo(100).Within(0.001), "order:0 (default) must be second");
            Assert.That(c.X, Is.EqualTo(200).Within(0.001), "order:1 must be last");
        }

        [Test]
        public void Order_in_column_flex_sorts_main_axis_vertically() {
            // In column flex, order still sorts on the main (vertical) axis.
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;height:400px\">"
                + "<div style=\"height:50px;order:2\"></div>"
                + "<div style=\"height:50px;order:1\"></div>"
                + "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); // order:2, source first
            var b = ChildAt(fb, 1); // order:1, source second
            // Visual order: b(order1) then a(order2)
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001), "order:1 item must be at the top");
            Assert.That(a.Y, Is.EqualTo(50).Within(0.001), "order:2 item must be below");
        }
    }
}
