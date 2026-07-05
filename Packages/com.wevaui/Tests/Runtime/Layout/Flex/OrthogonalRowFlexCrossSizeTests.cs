using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression for inventory.html's "tall empty header" bug. A row-flex
    // container (`.inv-header { display:flex; align-items:center }`, auto
    // height, flex-grow:0) nested in a column-flex parent (`.inventory
    // { display:flex; flex-direction:column; height:100% }`) rendered at the
    // BLOCK pass's vertically-stacked child sum (~250px) instead of its real
    // flex-line height (max child + padding, ~54px). FinalizeContainerCrossSize
    // was being skipped because IsStretchedByFlexParent returned true for ANY
    // orthogonal child of a parent with a definite main axis — even a
    // flex-grow:0 child with an auto cross size, which the parent does NOT
    // stretch and which must size to its own content.
    public class OrthogonalRowFlexCrossSizeTests {
        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.ClassName;
                if (!string.IsNullOrEmpty(c) && c.IndexOf(cls, System.StringComparison.Ordinal) >= 0)
                    return b;
            }
            return null;
        }

        [Test]
        public void Row_flex_header_in_column_flex_parent_sizes_to_line_height_not_stacked_sum() {
            var (root, _, _) = Build(
                "<div class='col'>" +
                "  <div class='hdr'><h1>Title</h1><div class='a'>A</div><nav class='tabs'>" +
                "    <button>One</button><button>Two</button></nav></div>" +
                "  <div class='body'></div>" +
                "</div>",
                ".col{display:flex;flex-direction:column;height:100%;}" +
                ".hdr{display:flex;align-items:center;gap:24px;padding:12px 16px;}" +
                ".hdr h1{margin:0;font-size:20px;}" +
                ".tabs{display:flex;gap:8px;}" +
                ".tabs button{padding:6px 10px;}" +
                ".body{flex:1;}",
                viewportWidth: 1000, viewportHeight: 800);
            var hdr = FindByClass(root, "hdr");
            Assert.That(hdr, Is.Not.Null);
            // Tallest child (button ~ font line-height + 12px padding ≈ 30) plus
            // the header's own 24px vertical padding → well under 100px. The bug
            // produced ~3× this (the stacked h1 + row + tabs sum).
            Assert.That(hdr.Height, Is.LessThan(100.0),
                $"row-flex header must size to its flex-line height, not the block-stacked " +
                $"child sum; got {hdr.Height:F1}");
        }

        [Test]
        public void Orthogonal_flex_child_with_explicit_cross_min_still_honoured() {
            // The complementary case the early-return was protecting: a column
            // flex child with an explicit min-width inside a row-flex parent must
            // keep that min (the parent owns its cross extent), not collapse to
            // its label's intrinsic width.
            var (root, _, _) = Build(
                "<div class='row'><div class='chip'><span>Lv</span></div></div>",
                ".row{display:flex;align-items:center;height:80px;}" +
                ".chip{display:flex;flex-direction:column;min-width:120px;padding:8px;}",
                viewportWidth: 1000, viewportHeight: 400);
            var chip = FindByClass(root, "chip");
            Assert.That(chip, Is.Not.Null);
            Assert.That(chip.Width, Is.GreaterThanOrEqualTo(120.0 - 0.5),
                $"min-width must hold on an orthogonal flex child; got {chip.Width:F1}");
        }

        [Test]
        public void Column_flex_min_content_uses_widest_item_not_container_max_content() {
            // The core engine fix: a COLUMN flex container's min-content INLINE
            // size is the WIDEST ITEM's min-content, not the container's
            // max-content. PositioningPass.MinContentWidth used to return
            // MaxContentWidth for every flex container, so the flex shrink floor
            // pinned a nested text column to its unwrapped width and it could
            // never shrink / wrap (episode-stats `.stat` needed a min-width:0
            // band-aid). CSS Sizing 3 §5.1 / Flexbox §9.9.
            //
            // Deterministic check independent of text-wrap run state: two
            // stacked items of DIFFERENT explicit widths. The column's
            // min-content must equal the WIDER item (200), and importantly must
            // NOT exceed it — the old code returned the same value here, but the
            // assertion pins the "max of items, not sum / not larger" contract
            // that the text case relies on.
            var (root, _, _) = Build(
                "<div class='col'><div class='a'></div><div class='b'></div></div>",
                ".col{display:flex;flex-direction:column;}" +
                ".a{width:200px;height:20px;}" +
                ".b{width:90px;height:20px;}",
                viewportWidth: 1000);
            var col = FindByClass(root, "col") as Weva.Layout.Boxes.BlockBox;
            Assert.That(col, Is.Not.Null);
            double minC = Weva.Layout.Positioning.PositioningPass.MinContentWidth(col);
            // Widest item is 200; the column min-content is exactly that (max of
            // items), never their sum (290) nor anything larger.
            Assert.That(minC, Is.EqualTo(200.0).Within(1.0),
                $"column-flex min-content = widest item (200), got {minC:F0}");
        }
    }
}
