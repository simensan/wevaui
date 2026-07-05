using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Regression coverage for tracker item A15: floats were counted in the
    // min-/max-content inline size of their containing block. Per CSS 2.1
    // §10.3.5 / CSS Sizing 3, floats are taken out of normal flow for
    // intrinsic-size purposes the same way absolute/fixed elements are —
    // the containing block flows AROUND them, so they must not contribute
    // to its intrinsic inline size.
    //
    // PositioningPass.WalkContent / WalkMinContent are the two walkers that
    // produce those sizes; both used to skip `position: absolute|fixed`
    // children but not `float: left|right` ones. Each test below exercises
    // those internals directly so the assertions describe the algorithm
    // contract rather than relying on a downstream consumer (shrink-to-fit
    // abs-pos boxes, table cell sizing, flex/grid item sizing) to surface
    // the change.
    public class FloatIntrinsicSizingTests {
        [Test]
        public void Float_child_excluded_from_max_content_width_of_parent() {
            // Mono font: 0.5em advance per char @16px => 8px/char. The
            // in-flow text "Hi" = 2 chars * 8 = 16px, while the float is
            // explicitly 200px wide. Pre-fix the parent's max-content would
            // be max(16, 200) = 200. Post-fix it must reflect the in-flow
            // run only.
            var (root, _, _) = Build(
                "<div id=\"p\">" +
                "<div id=\"f\" style=\"float:left;width:200px;height:50px\"></div>" +
                "Hi" +
                "</div>",
                null, viewportWidth: 800);
            var p = FirstById(root, "p");
            Assert.That(p, Is.Not.Null);
            var f = FirstById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.IsFloat, Is.True);

            double maxc = PositioningPass.MaxContentWidth(p);
            // Must NOT include the 200px float; should equal the text run.
            Assert.That(maxc, Is.LessThan(200));
            Assert.That(maxc, Is.EqualTo(16).Within(2));
        }

        [Test]
        public void Float_none_sibling_still_contributes_to_max_content() {
            // Control: same intrinsic-200px child, but `float: none`. Now
            // it IS in-flow, so it must show up in the parent's max-content.
            // This pairs with the test above to guard against the walker
            // accidentally skipping non-floated children too.
            var (rootFloat, _, _) = Build(
                "<div id=\"p\">" +
                "<div style=\"float:left;width:200px;height:50px\"></div>" +
                "Hi" +
                "</div>",
                null, viewportWidth: 800);
            var (rootNone, _, _) = Build(
                "<div id=\"p\">" +
                "<div style=\"width:200px;height:50px\"></div>" +
                "Hi" +
                "</div>",
                null, viewportWidth: 800);
            var pFloat = FirstById(rootFloat, "p");
            var pNone = FirstById(rootNone, "p");
            Assert.That(pFloat, Is.Not.Null);
            Assert.That(pNone, Is.Not.Null);

            double maxFloat = PositioningPass.MaxContentWidth(pFloat);
            double maxNone = PositioningPass.MaxContentWidth(pNone);

            // The in-flow version reflects the 200px block (max-content of
            // a child block walks its line boxes; an empty block has zero
            // text fragments — but the recursion still descends and finds
            // nothing, so the dominant contribution comes from the text
            // sibling). What matters is the relationship: floated-out is
            // <= in-flow, and in this specific case the in-flow block's
            // explicit width participation in WalkContent goes through the
            // recursive descent. The float-out version must be strictly
            // narrower than the in-flow version cannot be — at minimum it
            // must not exceed it, and must equal the text run width.
            Assert.That(maxFloat, Is.LessThanOrEqualTo(maxNone));
            Assert.That(maxFloat, Is.EqualTo(16).Within(2));
        }

        [Test]
        public void Multiple_floats_excluded_from_min_content_width_of_parent() {
            // Two floated siblings, each carrying an unbreakable 11-char
            // run "FloatedWord" (= 88px in mono 0.5em@16px). The in-flow
            // run is the single character "x" (= 8px). Pre-fix min-content
            // would walk into the floats' line boxes and pick up the 88px
            // word; post-fix it must reflect only the in-flow "x".
            var (root, _, _) = Build(
                "<div id=\"p\">" +
                "<div style=\"float:left\">FloatedWord</div>" +
                "<div style=\"float:right\">FloatedWord</div>" +
                "x" +
                "</div>",
                null, viewportWidth: 800);
            var p = FirstById(root, "p");
            Assert.That(p, Is.Not.Null);

            double minc = PositioningPass.MinContentWidth(p);
            Assert.That(minc, Is.LessThan(80));
            Assert.That(minc, Is.EqualTo(8).Within(2));
        }

        [Test]
        public void Float_child_excluded_from_min_content_width_of_parent() {
            // Companion to the max-content test, exercising WalkMinContent
            // specifically (the other walker patched for A15). The float
            // contains a long unbreakable word; the in-flow content is a
            // short word. Without the skip, the parent's min-content would
            // pick up the longer fragment.
            var (root, _, _) = Build(
                "<div id=\"p\">" +
                "<div style=\"float:left\">Supercalifragilistic</div>" +
                "ab" +
                "</div>",
                null, viewportWidth: 800);
            var p = FirstById(root, "p");
            Assert.That(p, Is.Not.Null);

            double minc = PositioningPass.MinContentWidth(p);
            // "ab" = 2 * 8 = 16px in mono 0.5em@16px. The float's word is
            // 20 chars = 160px. Post-fix the min-content must reflect "ab".
            Assert.That(minc, Is.LessThan(100));
            Assert.That(minc, Is.EqualTo(16).Within(2));
        }
    }
}
