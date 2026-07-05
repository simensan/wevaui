using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Regression (map.html player marker): the static position of an
    // out-of-flow child of a FLEX container must honour the container's
    // justify-content (main axis) and align-items/align-self (cross axis) —
    // CSS Flexbox §4.1. `.player { display:flex; justify-content:center;
    // align-items:center } > .player-fov { position:absolute }` left the
    // 80×80 glow at the container's top-left, rendering it offset down-right
    // of the centred icon instead of behind it.
    public class AbsoluteFlexStaticPositionTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Auto_inset_abs_child_centers_in_center_center_flex() {
            const string css =
                ".host { position: relative; width: 400px; height: 300px; }" +
                ".player { position: absolute; left: 200px; top: 150px; display: flex; " +
                "          justify-content: center; align-items: center; }" +
                ".icon { width: 16px; height: 24px; }" +
                ".glow { position: absolute; width: 80px; height: 80px; }";
            const string html =
                "<div class='host'><div class='player'>" +
                "<div class='icon'></div><div class='glow'></div>" +
                "</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var player = FirstWithClass(root, "player");
            var glow = FirstWithClass(root, "glow");
            Assert.That(player, Is.Not.Null);
            Assert.That(glow, Is.Not.Null);
            // player shrinks to the 16×24 icon; the 80×80 glow centers on it →
            // glow local origin = ((16-80)/2, (24-80)/2) = (-32, -28).
            Assert.That(glow.X, Is.EqualTo(-32).Within(1.0),
                $"glow X must center on the flex axis, got {glow.X:F1}");
            Assert.That(glow.Y, Is.EqualTo(-28).Within(1.0),
                $"glow Y must center on the flex cross axis, got {glow.Y:F1}");
            // glow center == player center.
            Assert.That(glow.X + glow.Width * 0.5, Is.EqualTo(player.Width * 0.5).Within(1.0));
            Assert.That(glow.Y + glow.Height * 0.5, Is.EqualTo(player.Height * 0.5).Within(1.0));
        }

        [Test]
        public void Auto_inset_abs_child_at_flex_start_stays_at_content_origin() {
            // Control: default justify/align (flex-start) keeps the abs child
            // at the content origin (offset only by the container's padding).
            const string css =
                ".host { position: relative; width: 400px; height: 300px; }" +
                ".player { position: absolute; left: 0; top: 0; display: flex; padding: 10px; }" +
                ".icon { width: 16px; height: 24px; }" +
                ".glow { position: absolute; width: 80px; height: 80px; }";
            const string html =
                "<div class='host'><div class='player'>" +
                "<div class='icon'></div><div class='glow'></div>" +
                "</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var glow = FirstWithClass(root, "glow");
            // flex-start static position sits at the content box origin (the
            // 10px padding), per CSS Flexbox §4.1.
            Assert.That(glow.X, Is.EqualTo(10).Within(1.0),
                $"flex-start abs child starts at the content origin, got {glow.X:F1}");
            Assert.That(glow.Y, Is.EqualTo(10).Within(1.0),
                $"flex-start abs child starts at the content origin, got {glow.Y:F1}");
        }

        [Test]
        public void Explicit_inset_overrides_flex_static_position() {
            // An explicit inset wins over the flex static position on that axis.
            const string css =
                ".host { position: relative; width: 400px; height: 300px; }" +
                ".player { position: absolute; left: 0; top: 0; width: 200px; height: 200px; " +
                "          display: flex; justify-content: center; align-items: center; }" +
                ".glow { position: absolute; left: 5px; width: 40px; height: 40px; }";
            const string html =
                "<div class='host'><div class='player'><div class='glow'></div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var glow = FirstWithClass(root, "glow");
            // left:5px pins X; top auto → cross-centered: (200-40)/2 = 80.
            Assert.That(glow.X, Is.EqualTo(5).Within(1.0),
                $"explicit left pins X, got {glow.X:F1}");
            Assert.That(glow.Y, Is.EqualTo(80).Within(1.0),
                $"auto top → flex cross-centered, got {glow.Y:F1}");
        }

        [Test]
        public void Column_flex_center_centers_abs_child_on_both_axes() {
            const string css =
                ".host { position: relative; width: 400px; height: 300px; }" +
                ".player { position: absolute; left: 0; top: 0; width: 120px; height: 160px; " +
                "          display: flex; flex-direction: column; justify-content: center; align-items: center; }" +
                ".glow { position: absolute; width: 40px; height: 40px; }";
            const string html =
                "<div class='host'><div class='player'><div class='glow'></div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var glow = FirstWithClass(root, "glow");
            // column: main=height (justify-center → (160-40)/2=60), cross=width
            // (align-center → (120-40)/2=40).
            Assert.That(glow.X, Is.EqualTo(40).Within(1.0), $"cross-centered X, got {glow.X:F1}");
            Assert.That(glow.Y, Is.EqualTo(60).Within(1.0), $"main-centered Y, got {glow.Y:F1}");
        }
    }
}
