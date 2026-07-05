using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (episode-stats "EPISODE 1" buttons): bare text inside a flex
    // container is wrapped in an anonymous flex item; `align-items: center`
    // must centre that anonymous item on the cross axis like any other item.
    // The text rendered top-aligned in the 52px buttons (anon block at the
    // content origin) instead of centred.
    public class FlexAnonymousTextCrossAlignTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Align_items_center_centres_bare_text_in_fixed_height_flex() {
            const string css =
                ".c { display: flex; align-items: center; justify-content: center; height: 52px; font-size: 19px; }";
            const string html = "<div class='c'>EPISODE 1</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400);

            var c = FirstWithClass(root, "c");
            Assert.That(c, Is.Not.Null);
            Assert.That(c.Height, Is.EqualTo(52).Within(0.5));
            // The anonymous item wrapping the text.
            var anon = c.Children.FirstOrDefault(ch => ch is BlockBox);
            Assert.That(anon, Is.Not.Null, "anonymous flex item exists");
            double free = 52 - anon.Height;
            Assert.That(anon.Y, Is.EqualTo(free * 0.5).Within(1.0),
                $"anon item must be cross-centred: Y={anon.Y:F2} H={anon.Height:F2} in 52px container");
        }

        [Test]
        public void Align_items_center_survives_nested_flex_relayout() {
            // The real page shape: a fixed-height flex ROW (.episodes) whose
            // stretched items are THEMSELVES flex containers centring bare
            // text. The outer flex re-layout passes must not leave the inner
            // anonymous item re-stacked at the content origin.
            const string css =
                ".row { display: flex; align-items: stretch; height: 52px; }" +
                ".ep { flex: 1 1 0; display: flex; align-items: center; justify-content: center; " +
                "      font-size: 19px; font-weight: 700; border: 1px solid #555; }";
            const string html =
                "<div class='row'><button class='ep'>EPISODE 1</button><button class='ep'>EPISODE 2</button></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var ep = FirstWithClass(root, "ep");
            Assert.That(ep, Is.Not.Null);
            Assert.That(ep.Height, Is.EqualTo(52).Within(0.5), "stretched to row height");
            var anon = ep.Children.FirstOrDefault(ch => ch is BlockBox);
            Assert.That(anon, Is.Not.Null);
            double contentTop = ep.PaddingTop + ep.BorderTop;
            double contentH = ep.Height - ep.PaddingTop - ep.PaddingBottom - ep.BorderTop - ep.BorderBottom;
            double expected = contentTop + (contentH - anon.Height) * 0.5;
            Assert.That(anon.Y, Is.EqualTo(expected).Within(1.0),
                $"inner anon item centred: Y={anon.Y:F2} expected≈{expected:F2} (anonH={anon.Height:F2})");
        }
    }
}
