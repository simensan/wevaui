using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (flex-playground): a DEEP alternating cross-stretch / flex-grow
    // chain (column ▸ row ▸ column ▸ row ▸ leaf, all auto-height) propagates one
    // nesting level per flex pass. A pure-flex tree gets a single RunFlexPasses,
    // so a deep chain was left under-converged after one Layout — fine in a
    // forced multi-pass re-layout, but the per-frame incremental gate ran one
    // Layout and skipped the rest, so the leaf flickered to height 0
    // (FLEX-DEEP-CROSS-STRETCH-INCREMENTAL). LayoutEngine now iterates the flex
    // pass to a fixed-point, so ONE Layout (what Build runs) converges the chain.
    public class FlexDeepCrossStretchConvergenceTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Deep_cross_stretch_chain_converges_in_one_layout() {
            // l0(col,400) > top0(20) + l1(row,flex1) > l2(col,flex1) > top2(20) +
            //   l3(row,flex1) > leaf(40-wide, stretch). The leaf's height is the
            // result of 4 stacked cross-stretch / flex-grow resolutions:
            //   l1 = 400-20 = 380 ; l2 stretched to 380 ; l3 = 380-20 = 360 ;
            //   leaf stretched to 360.
            const string css = @"
                .l0   { display: flex; flex-direction: column; height: 400px; width: 400px; }
                .top0 { height: 20px; }
                .l1   { display: flex; flex: 1 1 auto; align-items: stretch; }
                .l2   { display: flex; flex-direction: column; flex: 1 1 auto; }
                .top2 { height: 20px; }
                .l3   { display: flex; flex: 1 1 auto; align-items: stretch; }
                .leaf { flex: 0 0 40px; }";
            const string html =
                @"<div class='l0'><div class='top0'></div>" +
                @"<div class='l1'><div class='l2'><div class='top2'></div>" +
                @"<div class='l3'><div class='leaf'></div></div></div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var l1 = FirstWithClass(root, "l1");
            var l3 = FirstWithClass(root, "l3");
            var leaf = FirstWithClass(root, "leaf");
            Assert.That(l1, Is.Not.Null);
            Assert.That(l3, Is.Not.Null);
            Assert.That(leaf, Is.Not.Null);
            Assert.That(l1.Height, Is.EqualTo(380).Within(1.0), $"l1 fills l0, got {l1.Height:F1}");
            Assert.That(l3.Height, Is.EqualTo(360).Within(1.0), $"l3 fills l2, got {l3.Height:F1}");
            // The deepest stretched child must fill the fully-propagated height,
            // not collapse to 0 from an under-converged single pass.
            Assert.That(leaf.Height, Is.EqualTo(360).Within(1.5),
                $"leaf should stretch to the propagated height (~360), got {leaf.Height:F1}");
        }
    }
}
