using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (chat sample): a column-flex item with non-stretch align-self
    // and `max-width` must shrink-to-fit its content, not sit at the max-width
    // clamp. BlockLayout pre-fills auto-width children to the container width
    // and the max-width clamp then HID the over-stretch from the column-shrink
    // probe (its trigger was `width >= container width`), so chat's
    // `.msg { max-width:70%; align-self:flex-end }` bubbles filled the full
    // 70% (746px) instead of hugging their text (~100px), and the flex-end
    // bubble's left edge sat mid-screen.
    public class FlexColumnMaxWidthShrinkTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Column_item_with_max_width_shrinks_to_content() {
            const string css =
                ".col { display: flex; flex-direction: column; width: 600px; }" +
                ".msg { max-width: 70%; align-self: flex-end; }";
            const string html =
                "<div class='col'><div class='msg'>hi there</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var msg = FirstWithClass(root, "msg");
            Assert.That(msg, Is.Not.Null);
            // "hi there" is ~tens of px — far below the 420px (70%) clamp.
            Assert.That(msg.Width, Is.LessThan(200),
                $"non-stretch column item must hug its content, got W={msg.Width:F1}");
            // flex-end: right edge at the container's right edge.
            double right = msg.X + msg.Width;
            Assert.That(right, Is.EqualTo(600).Within(1.5),
                $"flex-end pins the right edge at 600, got {right:F1} (X={msg.X:F1} W={msg.Width:F1})");
        }

        [Test]
        public void Column_item_with_long_content_still_clamps_at_max_width() {
            // Control: content wider than the clamp stays clamped at 70%.
            const string css =
                ".col { display: flex; flex-direction: column; width: 600px; }" +
                ".msg { max-width: 70%; align-self: flex-start; }";
            const string html =
                "<div class='col'><div class='msg'>" +
                "a very long message that absolutely exceeds seventy percent of six hundred pixels of space easily" +
                "</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var msg = FirstWithClass(root, "msg");
            Assert.That(msg.Width, Is.EqualTo(420).Within(2.0),
                $"long content clamps at max-width 70% of 600 = 420, got {msg.Width:F1}");
        }
    }
}
