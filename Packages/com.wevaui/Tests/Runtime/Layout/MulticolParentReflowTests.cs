using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Multicol;

namespace Weva.Tests.Layout {
    // Tests for MULTICOL-PARENT-REFLOW: auto-height multicol containers must
    // re-position following siblings using the balanced (post-pass) container
    // height, not the BlockLayout pre-balance stacked height.
    //
    // Root cause: MulticolLayout.FinalizeContainerHeight shrinks the container
    // to the balanced column height AFTER BlockLayout already placed following
    // siblings using the inflated stacked estimate.  The fix calls
    // BlockFlowAdjuster.PropagateHeightDelta after every Layout pass, exactly
    // as FlexLayout and GridLayout do.
    //
    // Coverage:
    //   P1 — auto-height multicol: sibling Y equals balanced height + margin
    //   P2 — delta propagates through an auto-height parent chain
    //   P3 — min-height floor on an auto-height ancestor is respected during shrink
    //   P4 — explicit-height multicol is unaffected (delta == 0)
    //   P5 — multicol inside a flex item: convergence stress (idempotence)
    public class MulticolParentReflowTests {

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children)
                foreach (var d in Walk(c)) yield return d;
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        static List<T> FindAll<T>(Box root) where T : Box {
            var result = new List<T>();
            foreach (var b in Walk(root)) if (b is T t) result.Add(t);
            return result;
        }

        // Find a box by its CSS id attribute.
        static BlockBox BoxById(Box root, string id) {
            foreach (var b in Walk(root))
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            return null;
        }

        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx)
            Build(string html, string css = null, double viewportWidth = 900, double viewportHeight = 600)
            => LayoutTestHelpers.Build(html, css, viewportWidth, viewportHeight);

        const double Tol = 0.6; // half-pixel tolerance

        // -----------------------------------------------------------------------
        // P1 — auto-height multicol: following sibling Y must equal balanced height
        //
        // Setup: one auto-height multicol container (column-count:3, 6 items each
        // 60px) followed by a sibling div.  BlockLayout stacks 6×60=360px for the
        // mc, then places the sibling at Y=360+margin.  After balancing the mc
        // height becomes max(col heights) = 2×60=120px.  The sibling must move to
        // Y = 120 + margin-bottom of mc = 120 + 20 = 140.
        // -----------------------------------------------------------------------
        [Test]
        public void Auto_height_multicol_sibling_Y_uses_balanced_height() {
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "</div>" +
                "<div id='sibling'></div>",
                "body { margin: 0; }" +
                "#mc { width: 760px; column-count: 3; column-gap: 20px; margin-bottom: 20px; }" +
                ".item { height: 60px; }" +
                "#sibling { height: 40px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox must be produced");

            // Balanced height: 6 items × 60px / 3 cols = 2 per column → 120px.
            Assert.That(mc.Height, Is.EqualTo(120).Within(Tol),
                "Multicol container height must equal the balanced column height (120px)");

            var sibling = BoxById(root, "sibling");
            Assert.That(sibling, Is.Not.Null, "Sibling div must be found");

            // Sibling Y = mc.Y + mc.Height + mc.MarginBottom.
            double expectedY = mc.Y + mc.Height + mc.MarginBottom;
            Assert.That(sibling.Y, Is.EqualTo(expectedY).Within(Tol),
                "Following sibling must be positioned below the balanced multicol height, not the stacked pre-balance height");
        }

        // -----------------------------------------------------------------------
        // P2 — delta propagates through an auto-height parent chain
        //
        // The multicol is nested inside an auto-height wrapper div; the wrapper
        // is followed by a sibling at the body level.  After balancing, the
        // wrapper must shrink by the same delta and the body-level sibling must
        // move up accordingly.
        // -----------------------------------------------------------------------
        [Test]
        public void Height_delta_propagates_through_auto_height_parent_chain() {
            var (root, _, _) = Build(
                "<div id='wrapper'>" +
                "  <div id='mc'>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "  </div>" +
                "</div>" +
                "<div id='after'></div>",
                "body { margin: 0; }" +
                "#wrapper { /* auto height */ }" +
                "#mc { width: 600px; column-count: 2; column-gap: 20px; margin-bottom: 0; }" +
                ".item { height: 50px; }" +
                "#after { height: 30px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            // 4 items × 50px / 2 cols = 2 per column → balanced = 100px.
            Assert.That(mc.Height, Is.EqualTo(100).Within(Tol),
                "MulticolBox must be 100px (2 items per column × 50px)");

            var wrapper = BoxById(root, "wrapper");
            Assert.That(wrapper, Is.Not.Null);
            // Wrapper auto-height must equal the multicol height.
            Assert.That(wrapper.Height, Is.EqualTo(mc.Height).Within(Tol),
                "Auto-height wrapper must shrink to match the balanced multicol height");

            var after = BoxById(root, "after");
            Assert.That(after, Is.Not.Null);
            double expectedAfterY = wrapper.Y + wrapper.Height + wrapper.MarginBottom;
            Assert.That(after.Y, Is.EqualTo(expectedAfterY).Within(Tol),
                "Body-level sibling after the wrapper must be positioned using the propagated balanced height");
        }

        // -----------------------------------------------------------------------
        // P3 — min-height floor on an auto-height ancestor is respected during shrink
        //
        // A multicol container sits inside a wrapper that has min-height:200px.
        // The balanced multicol height is 100px which would shrink the wrapper
        // below its floor.  PropagateHeightDelta must clamp the wrapper at 200px
        // and carry only the effective (zero) delta to the body-level sibling.
        // -----------------------------------------------------------------------
        [Test]
        public void Min_height_floor_on_ancestor_respected_during_propagation() {
            var (root, _, _) = Build(
                "<div id='wrapper'>" +
                "  <div id='mc'>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "  </div>" +
                "</div>" +
                "<div id='after'></div>",
                "body { margin: 0; }" +
                "#wrapper { min-height: 200px; }" +
                "#mc { width: 600px; column-count: 2; column-gap: 20px; }" +
                ".item { height: 50px; }" +
                "#after { height: 30px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            // Balanced: 4 items × 50px / 2 cols = 100px.
            Assert.That(mc.Height, Is.EqualTo(100).Within(Tol));

            var wrapper = BoxById(root, "wrapper");
            Assert.That(wrapper, Is.Not.Null);
            // Wrapper must NOT shrink below its 200px min-height floor.
            Assert.That(wrapper.Height, Is.EqualTo(200).Within(Tol),
                "Auto-height ancestor with min-height:200px must not shrink below its floor");

            // The effective delta reaching the body-level sibling is 0 because
            // the wrapper clamped at its min-height (no height change visible outside).
            var after = BoxById(root, "after");
            Assert.That(after, Is.Not.Null);
            double expectedAfterY = wrapper.Y + wrapper.Height + wrapper.MarginBottom;
            Assert.That(after.Y, Is.EqualTo(expectedAfterY).Within(Tol),
                "Sibling after the min-height-floored wrapper must still be correctly positioned");
        }

        // -----------------------------------------------------------------------
        // P4 — explicit-height multicol: no displacement (delta == 0)
        //
        // When the multicol container has an explicit height BlockLayout already
        // knows the correct height; FinalizeContainerHeight writes the same value,
        // so delta == 0 and no sibling shift occurs.  Explicit-height containers
        // use sequential fill rather than balancing.
        // -----------------------------------------------------------------------
        [Test]
        public void Explicit_height_multicol_sibling_Y_is_unchanged() {
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "  <div class='item'></div>" +
                "</div>" +
                "<div id='sibling'></div>",
                "body { margin: 0; }" +
                "#mc { width: 600px; column-count: 3; height: 80px; }" +
                ".item { height: 60px; }" +
                "#sibling { height: 40px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            // Explicit height: the engine honours it regardless of children.
            Assert.That(mc.Height, Is.EqualTo(80).Within(Tol),
                "Multicol container must keep its explicit height of 80px");

            var sibling = BoxById(root, "sibling");
            Assert.That(sibling, Is.Not.Null);
            // Sibling must sit right below the explicit-height mc container.
            double expectedY = mc.Y + mc.Height + mc.MarginBottom;
            Assert.That(sibling.Y, Is.EqualTo(expectedY).Within(Tol),
                "Sibling Y after an explicit-height multicol must be exactly below the explicit height");
        }

        // -----------------------------------------------------------------------
        // P5 — multicol inside a flex item: layout converges (idempotence)
        //
        // A column-flex container holds a multicol item.  Running Layout twice
        // must produce the same geometry (no divergence across passes).
        // This mirrors the convergence stress tests for flex/grid.
        // -----------------------------------------------------------------------
        [Test]
        public void Multicol_inside_flex_item_layout_is_idempotent() {
            // We run Build twice with the same HTML/CSS and compare the mc height.
            string html =
                "<div id='flex'>" +
                "  <div id='mc'>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "    <div class='item'></div>" +
                "  </div>" +
                "  <div id='sibling'></div>" +
                "</div>";
            string css =
                "body { margin: 0; }" +
                "#flex { display: flex; flex-direction: column; width: 700px; }" +
                "#mc { column-count: 2; column-gap: 10px; }" +
                ".item { height: 50px; }" +
                "#sibling { height: 30px; }";

            var (root1, _, _) = Build(html, css);
            var (root2, _, _) = Build(html, css);

            var mc1 = FindFirst<MulticolBox>(root1);
            var mc2 = FindFirst<MulticolBox>(root2);
            Assert.That(mc1, Is.Not.Null);
            Assert.That(mc2, Is.Not.Null);

            Assert.That(mc1.Height, Is.EqualTo(mc2.Height).Within(Tol),
                "Two independent Layout calls must produce the same multicol container height (idempotence)");
            Assert.That(mc1.Y, Is.EqualTo(mc2.Y).Within(Tol),
                "Multicol Y must be identical across two independent builds");

            // Find sibling in both trees and verify Y is stable.
            BlockBox sib1 = null, sib2 = null;
            foreach (var b in Walk(root1)) if (b is BlockBox bb && bb.Element?.GetAttribute("id") == "sibling") { sib1 = bb; break; }
            foreach (var b in Walk(root2)) if (b is BlockBox bb && bb.Element?.GetAttribute("id") == "sibling") { sib2 = bb; break; }
            Assert.That(sib1, Is.Not.Null);
            Assert.That(sib2, Is.Not.Null);
            Assert.That(sib1.Y, Is.EqualTo(sib2.Y).Within(Tol),
                "Sibling Y must be identical across two independent builds (idempotence)");
        }
    }
}
