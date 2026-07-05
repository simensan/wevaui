using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (INLINE-ATOM-TEXTALIGN-RESET): an inline-flex atom inside a
    // block with text-align:right that ALSO contains a block sibling. The atom
    // sits in an anonymous block box (mixed block + inline children). BlockLayout
    // right-aligns it correctly, but the flex pass's ReapplyTextAlignOnContainer
    // read the anon block's null Style as "left", UNDID the offset, and the atom
    // snapped back to X=0. Now the flex copy falls back to the parent's Style
    // (CSS 2.1 §9.2.1.1) like InlineLayout already does.
    public class InlineAtomTextAlignResetTests {
        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Inline_flex_atom_right_aligns_inside_flex_item_with_block_sibling() {
            const string css = @"
                .row { display: flex; width: 300px; }
                .r-px { flex: 0 0 auto; text-align: right; }
                .r-price { }
                .pill { display: inline-flex; padding: 1px 4px; }";
            const string html =
                @"<div class='row'><div class='r-px'><div class='r-price'>1,234.56</div><span class='pill'>+1.2%</span></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var rpx = FirstWithClass(root, "r-px");
            var pill = FirstWithClass(root, "pill");
            Assert.That(rpx, Is.Not.Null);
            Assert.That(pill, Is.Not.Null);

            // The pill atom lives in an anon block that fills .r-px's content
            // width (= the wider .r-price). text-align:right must push the
            // narrower pill to the right edge: pill.X ≈ r-px.ContentWidth - pill.Width.
            double expected = rpx.ContentWidth - pill.Width;
            Assert.That(pill.X, Is.EqualTo(expected).Within(1.0),
                $"pill should be right-aligned (X≈{expected:F0}), got X={pill.X:F0}");
            Assert.That(pill.X, Is.GreaterThan(1.0),
                $"pill must not be pinned left (X={pill.X:F0})");
        }
    }
}
