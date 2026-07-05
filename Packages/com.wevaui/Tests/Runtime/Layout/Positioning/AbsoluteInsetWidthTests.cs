using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Audit item (nook-dialogue `.dialog`/`.say`): an absolutely-positioned
    // box with BOTH `left` and `right` set derives its width from the
    // containing block minus the insets (CSS Position §3.7 / CSS2 §10.3.7).
    // The audit measured the dialog's inner `p.say` at 1330 = viewport−padding
    // instead of 1270 = viewport−insets−padding, i.e. the dialog spanned the
    // full CB width and ignored the 30px left/right insets when deriving width.
    public class AbsoluteInsetWidthTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Abs_left_right_in_relative_parent_derives_width() {
            // Mirror of nook-dialogue: relative .room (100% wide, min-height
            // viewport) → absolute .dialog pinned left/right/bottom with
            // horizontal padding → block p inside.
            const string css =
                ".room { position: relative; width: 100%; min-height: 100vh; }" +
                ".dialog { position: absolute; left: 30px; right: 30px; bottom: 26px; " +
                "          min-height: 186px; padding: 34px 52px 30px 52px; box-sizing: content-box; }" +
                ".say { margin: 0 0 6px; font-size: 35px; line-height: 1.28; }";
            const string html =
                "<main class='room'><section class='dialog'>" +
                "<p class='say'>Welcome, Matt!</p>" +
                "</section></main>";
            var (root, _, _) = Build(html, css, viewportWidth: 1434, viewportHeight: 781);

            var dialog = FirstWithClass(root, "dialog");
            var say = FirstWithClass(root, "say");
            Assert.That(dialog, Is.Not.Null);
            Assert.That(say, Is.Not.Null);

            System.Console.WriteLine(
                $"dialog X={dialog.X:F1} W={dialog.Width:F1} padL={dialog.PaddingLeft:F1} padR={dialog.PaddingRight:F1} " +
                $"| say X={say.X:F1} W={say.Width:F1}");

            // Per CSS2 §10.3.7 (over-constrained solve for width): left +
            // margins + borders + padding + content-width + right = CB. The
            // BORDER-BOX is 1374 wide (1434 − 30 − 30); with 52+52 padding the
            // content box — and the inner block — is 1270, matching Chrome.
            Assert.That(dialog.Width, Is.EqualTo(1374).Within(1.0),
                $"abs left+right width = CB − insets, got {dialog.Width:F1}");
            Assert.That(say.Width, Is.EqualTo(1270).Within(1.0),
                $"inner block fills the dialog content box, got {say.Width:F1}");
            Assert.That(dialog.X, Is.EqualTo(30).Within(1.0));
        }

        [Test]
        public void Abs_left_right_in_min_height_only_parent_derives_width() {
            // Variation: the relative CB has NO width property at all (block
            // auto-width) and only min-height — same derivation must hold.
            const string css =
                ".host { position: relative; min-height: 400px; }" +
                ".bar { position: absolute; left: 50px; right: 50px; top: 10px; height: 40px; }";
            const string html = "<div class='host'><div class='bar'></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 1000, viewportHeight: 600);

            var bar = FirstWithClass(root, "bar");
            Assert.That(bar.Width, Is.EqualTo(900).Within(1.0),
                $"abs left+right width = 1000 − 100, got {bar.Width:F1}");
        }
    }
}
