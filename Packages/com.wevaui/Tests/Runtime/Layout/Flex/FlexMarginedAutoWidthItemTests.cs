using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (inputtest footer `.kbd`): a blockified inline flex item with
    // an auto width AND a margin. BlockLayout's pre-flex pass fills a block to
    // the container content width MINUS its own margin (CSS 2.1 §10.3.3), so the
    // item lands at containerMainSize - margin, NOT >= containerMainSize. The
    // flex-base "wide inflation" probe used a bare `natural >= containerMainSize`
    // test which the margin made it miss — the base stayed the inflated
    // fill-width and flex-shrink then scaled the item to a fraction of the row
    // instead of its content width (the `.kbd` legend boxes ballooned to ~681px
    // each in an 1658px footer). The probe now subtracts the item's main-axis
    // margin so the inflation is still detected and max-content is used.
    public class FlexMarginedAutoWidthItemTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Row_flex_margined_autowidth_item_sizes_to_content_not_fill() {
            const string css = @"
                .row { display: flex; width: 800px; gap: 10px; }
                .tag { margin-left: 16px; padding: 3px 9px; }";
            const string html = @"<div class='row'><span class='tag'>Tab</span><span class='tag'>Shift</span></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 1000);
            var tag = FirstWithClass(root, "tag");
            Assert.That(tag, Is.Not.Null);
            // "Tab" text + 18px padding ≈ <60px. The bug made it ~ (800-margins)/2 ≈ 380.
            Assert.That(tag.Width, Is.LessThan(140.0),
                $".tag must size to content, not absorb the row (got {tag.Width:F0})");
        }

        // Control: an item WITHOUT a margin was already detected by the bare
        // `>=` test — make sure the margin-aware change doesn't regress it.
        [Test]
        public void Row_flex_no_margin_autowidth_item_still_content_sized() {
            const string css = @"
                .row { display: flex; width: 800px; gap: 10px; }
                .tag { padding: 3px 9px; }";
            const string html = @"<div class='row'><span class='tag'>Tab</span><span class='tag'>Shift</span></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 1000);
            var tag = FirstWithClass(root, "tag");
            Assert.That(tag, Is.Not.Null);
            Assert.That(tag.Width, Is.LessThan(140.0),
                $".tag (no margin) must size to content (got {tag.Width:F0})");
        }
    }
}
