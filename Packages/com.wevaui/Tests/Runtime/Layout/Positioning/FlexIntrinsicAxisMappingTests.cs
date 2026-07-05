using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Positioning;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Regression coverage for tracker item E9: PositioningPass.FlexIntrinsicInline
    // and FlexIntrinsicCross are supposed to consult the container's resolved
    // `flex-direction` so they sum / max along the correct child-axis.
    //
    // Spec contract (CSS Flexbox L1 §3 / §9.9):
    //   - The main axis is determined by `flex-direction`; the cross axis is
    //     perpendicular to it.
    //   - `flex-direction: row` / `row-reverse`: items lay out on the inline
    //     axis, so intrinsic-inline ≈ Σ(child outer widths) + gaps and
    //     intrinsic-cross ≈ max(child outer heights).
    //   - `flex-direction: column` / `column-reverse`: items lay out on the
    //     block axis, so intrinsic-inline ≈ max(child outer widths) and
    //     intrinsic-cross ≈ Σ(child outer heights) + gaps.
    //   - `*-reverse` is purely a paint-order flip: intrinsic-size queries
    //     return the same value as their non-reverse siblings.
    //   - `direction: rtl` is irrelevant at the intrinsic-size level — both
    //     sum and max are commutative over the physical axis.
    //
    // The agent audit landed the tracker entry with stale line cites; the
    // helpers themselves consult `flex-direction` and route sum-vs-max
    // correctly. These tests pin that contract so a future refactor can't
    // silently re-introduce the bug, and prove the *-reverse invariant.
    public class FlexIntrinsicAxisMappingTests {
        // Mono font: 0.5em advance @16px => 8px/char. Use explicit widths /
        // heights on the children so the intrinsics don't depend on text
        // measurement and the maths are exact.
        const string ColumnFlexHtml =
            "<div id=\"f\" style=\"display:flex;flex-direction:column\">" +
                "<div style=\"width:50px;height:100px\"></div>" +
                "<div style=\"width:100px;height:100px\"></div>" +
                "<div style=\"width:150px;height:100px\"></div>" +
            "</div>";

        const string RowFlexHtml =
            "<div id=\"f\" style=\"display:flex;flex-direction:row\">" +
                "<div style=\"width:50px;height:30px\"></div>" +
                "<div style=\"width:100px;height:50px\"></div>" +
                "<div style=\"width:150px;height:40px\"></div>" +
            "</div>";

        const string RowReverseFlexHtml =
            "<div id=\"f\" style=\"display:flex;flex-direction:row-reverse\">" +
                "<div style=\"width:50px;height:30px\"></div>" +
                "<div style=\"width:100px;height:50px\"></div>" +
                "<div style=\"width:150px;height:40px\"></div>" +
            "</div>";

        const string ColumnReverseFlexHtml =
            "<div id=\"f\" style=\"display:flex;flex-direction:column-reverse\">" +
                "<div style=\"width:50px;height:100px\"></div>" +
                "<div style=\"width:100px;height:100px\"></div>" +
                "<div style=\"width:150px;height:100px\"></div>" +
            "</div>";

        static FlexBox FindFlexById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is FlexBox fb && fb.Element != null && fb.Element.Id == id) return fb;
            }
            return null;
        }

        [Test]
        public void Column_flex_intrinsic_inline_is_max_of_child_widths_E9() {
            // flex-direction:column => items stack on the block axis, so the
            // container's intrinsic inline size is max(child outer widths) =
            // max(50, 100, 150) = 150. MaxContentWidth is the public surface
            // that routes through FlexIntrinsicInline + HorizontalFrame; with
            // no padding/border on the container the result is just the inner
            // intrinsic.
            var (root, _, _) = Build(ColumnFlexHtml, null, viewportWidth: 800);
            var f = FindFlexById(root, "f");
            Assert.That(f, Is.Not.Null);

            double inline = PositioningPass.MaxContentWidth(f);
            Assert.That(inline, Is.EqualTo(150).Within(0.5),
                "column-flex intrinsic-inline must be MAX of child widths, not the sum");
        }

        [Test]
        public void Column_flex_intrinsic_cross_is_sum_of_child_heights_E9() {
            // flex-direction:column => items stack vertically, so the
            // container's intrinsic cross size is Σ(child outer heights) =
            // 100+100+100 = 300 (plus gaps, none here). The cross helper is
            // internal — accessible via the InternalsVisibleTo grant.
            var (root, _, _) = Build(ColumnFlexHtml, null, viewportWidth: 800);
            var f = FindFlexById(root, "f");
            Assert.That(f, Is.Not.Null);

            double cross = PositioningPass.FlexIntrinsicCross(f);
            Assert.That(cross, Is.EqualTo(300).Within(0.5),
                "column-flex intrinsic-cross must be SUM of child heights");
        }

        [Test]
        public void Row_flex_intrinsic_inline_is_sum_of_child_widths_E9() {
            // flex-direction:row (the engine default) => items lay out on the
            // inline axis, so the container's intrinsic inline size is
            // Σ(child outer widths) = 50+100+150 = 300 (plus gaps, none here).
            var (root, _, _) = Build(RowFlexHtml, null, viewportWidth: 800);
            var f = FindFlexById(root, "f");
            Assert.That(f, Is.Not.Null);

            double inline = PositioningPass.MaxContentWidth(f);
            Assert.That(inline, Is.EqualTo(300).Within(0.5),
                "row-flex intrinsic-inline must be SUM of child widths");
        }

        [Test]
        public void Row_flex_intrinsic_cross_is_max_of_child_heights_E9() {
            // flex-direction:row => items lay out on a single (unwrapped) line,
            // so the container's intrinsic cross size is max(child outer
            // heights) = max(30, 50, 40) = 50.
            var (root, _, _) = Build(RowFlexHtml, null, viewportWidth: 800);
            var f = FindFlexById(root, "f");
            Assert.That(f, Is.Not.Null);

            double cross = PositioningPass.FlexIntrinsicCross(f);
            Assert.That(cross, Is.EqualTo(50).Within(0.5),
                "row-flex intrinsic-cross must be MAX of child heights");
        }

        [Test]
        public void Row_reverse_flex_intrinsics_match_row_E9_spec_invariant() {
            // CSS Flexbox §3: *-reverse is paint-order only. Intrinsic sizes
            // must equal their non-reverse counterparts. This test pins both
            // axes against the row baseline so a regression that special-cases
            // `row-reverse` (e.g. swapping sum/max for RTL-ish reasoning) is
            // caught.
            var (rootRow, _, _) = Build(RowFlexHtml, null, viewportWidth: 800);
            var (rootRev, _, _) = Build(RowReverseFlexHtml, null, viewportWidth: 800);
            var fRow = FindFlexById(rootRow, "f");
            var fRev = FindFlexById(rootRev, "f");
            Assert.That(fRow, Is.Not.Null);
            Assert.That(fRev, Is.Not.Null);

            double rowInline = PositioningPass.MaxContentWidth(fRow);
            double revInline = PositioningPass.MaxContentWidth(fRev);
            Assert.That(revInline, Is.EqualTo(rowInline).Within(0.5),
                "row-reverse intrinsic-inline must equal row intrinsic-inline");

            double rowCross = PositioningPass.FlexIntrinsicCross(fRow);
            double revCross = PositioningPass.FlexIntrinsicCross(fRev);
            Assert.That(revCross, Is.EqualTo(rowCross).Within(0.5),
                "row-reverse intrinsic-cross must equal row intrinsic-cross");
        }

        [Test]
        public void Column_reverse_flex_intrinsics_match_column_E9_spec_invariant() {
            // Companion to the row-reverse invariant: `column-reverse` must
            // match `column` on both axes. This pins the second `*-reverse`
            // branch in the FlexIntrinsicInline/Cross dir string-compare.
            var (rootCol, _, _) = Build(ColumnFlexHtml, null, viewportWidth: 800);
            var (rootRev, _, _) = Build(ColumnReverseFlexHtml, null, viewportWidth: 800);
            var fCol = FindFlexById(rootCol, "f");
            var fRev = FindFlexById(rootRev, "f");
            Assert.That(fCol, Is.Not.Null);
            Assert.That(fRev, Is.Not.Null);

            double colInline = PositioningPass.MaxContentWidth(fCol);
            double revInline = PositioningPass.MaxContentWidth(fRev);
            Assert.That(revInline, Is.EqualTo(colInline).Within(0.5),
                "column-reverse intrinsic-inline must equal column intrinsic-inline");

            double colCross = PositioningPass.FlexIntrinsicCross(fCol);
            double revCross = PositioningPass.FlexIntrinsicCross(fRev);
            Assert.That(revCross, Is.EqualTo(colCross).Within(0.5),
                "column-reverse intrinsic-cross must equal column intrinsic-cross");
        }
    }
}
