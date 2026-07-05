using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Regression for load-game's "page doesn't fill the viewport" bug. An
    // outer block sized by `min-height: 100vh` that contains a flex column
    // (whose content is shorter than the viewport) got shrunk below its
    // min-height: FlexLayout.ShiftFollowingSiblingsIfHeightChanged propagated
    // the flex container's post-pass shrink up through the auto-height ancestor
    // via BlockFlowAdjuster.PropagateHeightDelta, which only guarded on explicit
    // `height` and ignored `min-height`. The ancestor collapsed to content
    // height, leaving a gap at the bottom of the screen. The fix re-clamps the
    // propagated height to min/max-height and carries only the effective delta.
    public class MinHeightFlexChildPropagationTests {
        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.ClassName;
                if (!string.IsNullOrEmpty(c) && c.IndexOf(cls, System.StringComparison.Ordinal) >= 0)
                    return b;
            }
            return null;
        }

        [Test]
        public void MinHeight_vh_survives_flex_child_shrink_propagation() {
            var (root, _, _) = Build(
                "<div class='screen'>" +
                "  <div class='list'><div class='item'>A</div><div class='item'>B</div></div>" +
                "</div>",
                ".screen{min-height:100vh;}" +
                ".list{display:flex;flex-direction:column;gap:10px;}" +
                ".item{height:40px;}",
                viewportWidth: 1000, viewportHeight: 900);
            var screen = FindByClass(root, "screen");
            Assert.That(screen, Is.Not.Null);
            // The contract is "fills the viewport": min-height:100vh is a floor,
            // so the block must be at least 900 even after the flex child's
            // height change propagates up. (Before the fix it collapsed to the
            // ~90px content height, leaving a gap at the bottom of the screen.)
            Assert.That(screen.Height, Is.GreaterThanOrEqualTo(900.0 - 1.0),
                $"min-height:100vh must keep the block filling the viewport (>=900) after the " +
                $"flex child's height-change propagates up; got {screen.Height:F1}");
        }

        [Test]
        public void MinHeight_vh_survives_flex_child_shrink_with_absolute_sibling() {
            // Closest to load-game: the floored block also has an out-of-flow
            // child (a bottom-anchored footer). Both the OOF-compression path
            // and the flex-shrink propagation must leave the floor intact.
            var (root, _, _) = Build(
                "<div class='screen'>" +
                "  <div class='list'><div class='item'>A</div><div class='item'>B</div></div>" +
                "  <div class='footer'></div>" +
                "</div>",
                ".screen{position:relative;min-height:100vh;}" +
                ".list{display:flex;flex-direction:column;gap:10px;}" +
                ".item{height:40px;}" +
                ".footer{position:absolute;bottom:20px;left:0;height:30px;width:100px;}",
                viewportWidth: 1000, viewportHeight: 900);
            var screen = FindByClass(root, "screen");
            var footer = FindByClass(root, "footer");
            Assert.That(screen, Is.Not.Null);
            Assert.That(screen.Height, Is.EqualTo(900.0).Within(1.0),
                $"min-height:100vh floor must hold; got {screen.Height:F1}");
            // footer bottom:20 + height:30 → Y = 900 - 20 - 30 = 850.
            Assert.That(footer.Y, Is.EqualTo(850.0).Within(1.0),
                $"bottom-anchored footer should sit against the floored viewport bottom; got Y={footer.Y:F1}");
        }
    }
}
