using System.Text;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Forms {
    // Regression for menu.html's "Click me" button: a fixed-width <button>
    // (wider than its label) must center the label horizontally, matching
    // Chrome's native button rendering. The UA sheet makes <button> an
    // inline-flex with align-items:center but leaves justify-content unset;
    // FlexLayout now defaults a UA-default-display button to center on the
    // main axis so an explicit width wider than the text doesn't pack the
    // label to the left edge.
    public class ButtonTextCenteringTests {
        static Box FirstContentLine(Box b) {
            // The button's label lives in a LineBox (possibly under an
            // anonymous block). Return the first line/text box found.
            if (b is LineBox || (b is TextRun tr && !string.IsNullOrWhiteSpace(tr.Text))) return b;
            for (int i = 0; i < b.Children.Count; i++) {
                var r = FirstContentLine(b.Children[i]);
                if (r != null) return r;
            }
            return null;
        }

        static Box FindButton(Box root) {
            if (root.Element != null && root.Element.TagName == "button") return root;
            for (int i = 0; i < root.Children.Count; i++) {
                var r = FindButton(root.Children[i]);
                if (r != null) return r;
            }
            return null;
        }

        static string Tree(Box b, int depth = 0) {
            var sb = new StringBuilder();
            sb.Append(' ', depth * 2).Append(b.GetType().Name)
              .Append(b is TextRun t ? $" '{t.Text}'" : "")
              .Append($" ({b.X:F1},{b.Y:F1} {b.Width:F1}x{b.Height:F1})\n");
            for (int i = 0; i < b.Children.Count; i++) sb.Append(Tree(b.Children[i], depth + 1));
            return sb.ToString();
        }

        static double AbsX(Box b) { double x = 0; for (Box p = b; p != null; p = p.Parent) x += p.X; return x; }

        [Test]
        public void Fixed_width_button_centers_its_label() {
            const string css = @"
                * { box-sizing: border-box; }
                button { width: 140px; height: 40px; padding: 8px 20px; }
            ";
            var (root, _, _) = Build("<button>Click me</button>", css, viewportWidth: 400);
            var button = FindButton(root);
            Assert.That(button, Is.Not.Null, "button box exists");

            var line = FirstContentLine(button);
            Assert.That(line, Is.Not.Null, "button has a content line:\n" + Tree(button));

            double buttonCenter = AbsX(button) + button.Width / 2.0;
            double lineCenter = AbsX(line) + line.Width / 2.0;
            Assert.That(lineCenter, Is.EqualTo(buttonCenter).Within(1.5),
                $"label should be horizontally centered (button {buttonCenter:F1}, label {lineCenter:F1})\n" + Tree(button));
        }

        [Test]
        public void Auto_width_button_does_not_strand_its_label() {
            // Regression (map.html .travel-btn): an auto-width <button> shrinks
            // to fit its label, so there is NO main-axis free space. The UA
            // button center default must not apply — centering the label
            // against the button's pre-shrink main size strands it far to the
            // right of the (shrunk) button box, overlapping neighbours. The
            // label must sit at the content-left edge and stay within the box.
            const string css = @"
                * { box-sizing: border-box; }
                .row { display: flex; gap: 4px; }
                button { padding: 6px 12px; }   /* no width: shrink-to-fit */
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><button>Brackwater</button><button>Ashfall</button></div>",
                css, viewportWidth: 800);
            var button = FindButton(root);
            Assert.That(button, Is.Not.Null);
            var line = FirstContentLine(button);
            Assert.That(line, Is.Not.Null, "button has a content line:\n" + Tree(button));

            double labelLeft = AbsX(line);
            double contentLeft = AbsX(button) + button.PaddingLeft + button.BorderLeft;
            Assert.That(labelLeft, Is.EqualTo(contentLeft).Within(1.5),
                "auto-width button label must sit at content-left, not strand right:\n" + Tree(button));
            Assert.That(labelLeft + line.Width, Is.LessThanOrEqualTo(AbsX(button) + button.Width + 1.5),
                "label must stay within the button box:\n" + Tree(button));
        }

        [Test]
        public void Author_display_flex_button_keeps_flex_start() {
            // When the author overrides display to flex, the special button
            // centering must NOT apply (Chrome packs flex-start), so an
            // explicit justify-content:flex-start / unset stays left.
            const string css = @"
                * { box-sizing: border-box; }
                button { width: 200px; height: 40px; display: flex; }
                .ico { width: 20px; height: 20px; }
            ";
            var (root, _, _) = Build("<button><span class=\"ico\"></span></button>", css, viewportWidth: 400);
            var button = FindButton(root);
            Assert.That(button, Is.Not.Null);
            var ico = FirstByClass(root, "ico");
            // flex-start: icon hugs the content-box left edge (x ~= button x).
            Assert.That(AbsX(ico), Is.EqualTo(AbsX(button)).Within(1.5),
                "author display:flex button should keep flex-start packing");
        }
    }
}
