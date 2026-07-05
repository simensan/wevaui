using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (inventory / advanced-dashboard audit): a column flex
    // container with `height: 100%` under a definite parent chain collapsed to
    // its content sum — FinalizeContainerMainSize's explicit-size guard only
    // honoured Length, so the percent height BlockLayout had already resolved
    // (e.g. the 100vh->100%->100% viewport chain) was thrown away. The
    // container shrank to its first child's height and its `flex:1` body then
    // grew unconstrained. CSS Sizing §5.1: a percentage with a definite basis
    // is definite.
    public class FlexPercentMainSizeTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Column_flex_height_100pct_keeps_definite_parent_chain() {
            // outer: definite 600px. inner column flex: height:100% -> 600.
            // flex:1 body fills the remainder under the 100px header.
            const string css =
                ".outer { height: 600px; }" +
                ".col { height: 100%; display: flex; flex-direction: column; }" +
                ".head { height: 100px; }" +
                ".body { flex: 1 1 0; min-height: 0; }";
            const string html =
                "<div class='outer'><div class='col'>" +
                "<div class='head'></div><div class='body'></div>" +
                "</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 700);

            var col = FirstWithClass(root, "col");
            var body = FirstWithClass(root, "body");
            Assert.That(col.Height, Is.EqualTo(600).Within(0.5),
                $"column flex height:100% must keep the parent's 600px, got {col.Height:F1}");
            Assert.That(body.Height, Is.EqualTo(500).Within(1.0),
                $"flex:1 body fills the remainder (600-100), got {body.Height:F1}");
        }

        [Test]
        public void Column_flex_height_100pct_with_indefinite_parent_still_hugs_content() {
            // Control: percent against an auto-height parent behaves as auto
            // (CSS 2.1 §10.5) — the container still collapses to content.
            const string css =
                ".col { height: 100%; display: flex; flex-direction: column; }" +
                ".head { height: 100px; }";
            const string html =
                "<div class='wrap'><div class='col'><div class='head'></div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 700);

            var col = FirstWithClass(root, "col");
            // wrap is auto-height; the col hugs its 100px content (it must NOT
            // balloon to the viewport or freeze at some unrelated size).
            Assert.That(col.Height, Is.EqualTo(100).Within(1.0),
                $"percent height against indefinite parent hugs content, got {col.Height:F1}");
        }
    }
}
