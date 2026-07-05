using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Audit item 6 (match3-endgame `.reward-text`): a SPAN child of a flex
    // container blockifies into a flex item (CSS Display 3 §2.7 / Flexbox §4),
    // so its box must wrap its text — shrink-to-fit width, line-height height.
    // The audit measured the span's box at width ≈0-11 while Chrome reports
    // 57-73 for the same text, i.e. the inline shell's box collapsed and the
    // text laid out outside it.
    public class InlineSpanFlexItemTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Span_in_column_flex_blockifies_and_wraps_its_text() {
            // Mirror of match3-endgame's reward card.
            const string css =
                ".reward { display: flex; flex-direction: column; align-items: center; " +
                "          gap: 7px; width: 120px; padding: 12px 8px; box-sizing: border-box; }" +
                ".reward-icon { width: 34px; height: 34px; }" +
                ".reward-text { font-size: 13px; font-weight: 700; text-align: center; }";
            const string html =
                "<div class='reward'>" +
                "<span class='reward-icon'></span>" +
                "<span class='reward-text'>850 coins</span>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var text = FirstWithClass(root, "reward-text");
            Assert.That(text, Is.Not.Null);
            // Blockified flex item: the span's box wraps "850 coins" — its
            // width is the text advance (tens of px at 13px), never ~0.
            Assert.That(text.Width, Is.GreaterThan(30),
                $"span flex item must shrink-to-fit its text, got W={text.Width:F1}");
            Assert.That(text.Height, Is.GreaterThan(10),
                $"span flex item must be one line tall, got H={text.Height:F1}");

            // And it is cross-axis centered inside the 120px reward card
            // (content 104px after padding).
            var reward = FirstWithClass(root, "reward");
            double center = text.X + text.Width * 0.5;
            Assert.That(center, Is.EqualTo(reward.Width * 0.5).Within(2.0),
                $"align-items:center must center the text item, center={center:F1}");
        }

        [Test]
        public void Span_in_flex_in_grid_keeps_text_width() {
            // The FULL match3-endgame structure: grid(3×1fr) → flex-column
            // reward → icon + text span. The audit measured the span at
            // W≈0-11 in the live sample, so the grid-item context is the
            // suspected trigger.
            const string css =
                ".rewards { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; width: 380px; }" +
                ".reward { display: flex; flex-direction: column; align-items: center; gap: 7px; " +
                "          min-height: 78px; padding: 12px 8px; box-sizing: border-box; }" +
                ".reward-icon { position: relative; width: 34px; height: 34px; }" +
                ".reward-text { font-size: 13px; font-weight: 700; text-align: center; }";
            const string html =
                "<div class='rewards'>" +
                "<div class='reward'><span class='reward-icon coin'></span><span class='reward-text'>850 coins</span></div>" +
                "<div class='reward'><span class='reward-icon hammer'></span><span class='reward-text'>Hammer +1</span></div>" +
                "<div class='reward'><span class='reward-icon chest'></span><span class='reward-text'>Gold chest</span></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var texts = AllBoxes(root).Where(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains("reward-text")).ToList();
            Assert.That(texts.Count, Is.EqualTo(3));
            foreach (var t in texts) {
                System.Console.WriteLine(
                    $"reward-text: {t.GetType().Name} X={t.X:F1} W={t.Width:F1} H={t.Height:F1} parent={t.Parent?.GetType().Name}");
                Assert.That(t.Width, Is.GreaterThan(30),
                    $"reward-text must wrap its text inside a grid-item flex card, got W={t.Width:F1}");
            }
        }

        [Test]
        public void Span_in_row_flex_blockifies_and_wraps_its_text() {
            const string css =
                ".row { display: flex; gap: 8px; width: 300px; }" +
                ".tag { font-size: 13px; }";
            const string html =
                "<div class='row'><span class='tag'>alpha</span><span class='tag'>beta</span></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            // TextRuns reference their span element for styling — element
            // "ownership" of the layout box means the non-TextRun box.
            var tags = AllBoxes(root).Where(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "") == "tag").ToList();
            Assert.That(tags.Count, Is.EqualTo(2));
            foreach (var t in tags) {
                Assert.That(t.Width, Is.GreaterThan(15),
                    $"row flex span must wrap its text, got W={t.Width:F1}");
            }
            // Second tag sits after the first plus the gap.
            Assert.That(tags[1].X, Is.EqualTo(tags[0].X + tags[0].Width + 8).Within(1.0));
        }
    }
}
