// §11 — Percentage resolution in flex items (CSS Flexbox L1 §9.2 + CSS Sizing §5)
// FlexLayoutIntegrationTests.cs covers width:50%/100% in column flex.
// This file adds:
//   - Row flex item width:% resolves against flex container main size
//   - height:% in row flex requires definite container height
//   - flex-basis:% resolves against container main size in layout
//   - Nested flex: percentage resolves against immediate flex container
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexPercentageSizingTests {

        // ── Row flex item: width:% ─────────────────────────────────────────

        [Test]
        public void Row_flex_item_width_50pct_resolves_against_container_main_size() {
            // Row flex, 600px container. Child with width:50% should be 300px.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"width:50%;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001),
                "width:50% in row flex should resolve against 600px container → 300px");
        }

        [Test]
        public void Row_flex_item_width_25pct_resolves_against_container() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:800px\">"
                + "<div style=\"width:25%;height:50px\"></div>"
                + "<div style=\"width:25%;height:50px\"></div></div>",
                null, viewportWidth: 1024);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(b.X, Is.EqualTo(200).Within(0.001));
        }

        // ── Row flex item: height:% requires definite container height ─────

        [Test]
        public void Row_flex_item_height_100pct_resolves_against_definite_container_height() {
            // Container has explicit height:200px (definite). Child height:100%
            // should resolve to 200px.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;height:200px\">"
                + "<div style=\"width:100px;height:100%\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(200).Within(0.001),
                "height:100% should resolve to 200px against definite container height");
        }

        [Test]
        public void Row_flex_item_height_50pct_with_definite_container() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;height:400px\">"
                + "<div style=\"width:100px;height:50%\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(200).Within(0.001));
        }

        // ── Flex-basis:% in layout ────────────────────────────────────────

        [Test]
        public void Flex_basis_percentage_in_layout_acts_as_main_size() {
            // flex: 0 0 33% in a 600px container → each item = 198px (33% of 600).
            // With 3 items: 3×198=594px, 6px free. flex-grow=0 so no redistrib.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:0 0 33%;height:50px\"></div>"
                + "<div style=\"flex:0 0 33%;height:50px\"></div>"
                + "<div style=\"flex:0 0 33%;height:50px\"></div>"
                + "</div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Width, Is.EqualTo(198).Within(0.001),
                "flex-basis:33% should resolve to 198px in a 600px container");
        }

        // ── Nested percentage ─────────────────────────────────────────────

        [Test]
        public void Nested_row_flex_item_width_pct_resolves_against_inner_container() {
            // Outer: block 800px. Inner: flex 400px. Grandchild: width:50%.
            // Grandchild width should be 50% of 400 = 200px (not 50% of 800).
            const string css = @"
                .outer { width: 800px; }
                .inner { display: flex; width: 400px; }
                .child { width: 50%; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"outer\"><div class=\"inner\"><div class=\"child\"></div></div></div>",
                css, viewportWidth: 1024);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            Assert.That(child.Width, Is.EqualTo(200).Within(0.001),
                "percentage should resolve against the immediate flex container (400px), not outer (800px)");
        }

        [Test]
        public void Column_flex_item_height_pct_resolves_against_container_block_size() {
            // Column flex with explicit height 400px. Child height:25%.
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:200px;height:400px\">"
                + "<div style=\"height:25%;width:100px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(100).Within(0.001),
                "height:25% in column flex should resolve against 400px container → 100px");
        }
    }
}
