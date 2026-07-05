using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS 2.1 §10.8.1 inline-formatting `vertical-align`. Each case uses a
    // 20x20 inline-block atom alongside the text "x" so the surrounding line
    // contributes a known ascent / descent (MonoFontMetrics @ 16px: ascent
    // 12.8, descent 6.4, line-height 19.2). The atom has no inner content,
    // so its synthesized baseline = atom.Height = 20 (content-bottom rule).
    public class VerticalAlignInlineTests {
        static (BlockBox atom, LineBox line) Probe(string verticalAlign) {
            string atomStyle = "display:inline-block;width:20px;height:20px";
            if (!string.IsNullOrEmpty(verticalAlign)) {
                atomStyle += ";vertical-align:" + verticalAlign;
            }
            string html = "<p>x<span style=\"" + atomStyle + "\"></span></p>";
            var (root, _, _) = Build(html, null, 800);
            BlockBox atom = null;
            LineBox line = null;
            foreach (var b in AllBoxes(root)) {
                if (atom == null && b is BlockBox bb && bb.IsInlineBlock) atom = bb;
                if (line == null && b is LineBox lb && lb.Parent is BlockBox parent && parent.Element?.TagName == "p") line = lb;
            }
            return (atom, line);
        }

        [Test]
        public void Vertical_align_inline_block_cases() {
            const double Fs = 16;
            const double Ascent = 12.8;     // 0.8 * 16
            const double Descent = 6.4;     // 0.4 * 16
            const double LineHeight = 19.2; // 1.2 * 16
            const double Height = 20;
            const double XHeight = Fs * 0.5; // v1 x-height approximation

            string[] values = {
                null, "baseline", "sub", "super", "middle",
                "text-top", "text-bottom", "4px", "50%"
            };

            foreach (var raw in values) {
                var (atom, line) = Probe(raw);
                Assert.That(atom, Is.Not.Null, "atom missing for vertical-align=" + (raw ?? "<unset>"));
                Assert.That(line, Is.Not.Null, "line missing for vertical-align=" + (raw ?? "<unset>"));

                double atomBaselineInLine = atom.Y + atom.Height;
                double atomMidInLine = atom.Y + Height * 0.5;

                switch (raw) {
                    case null:
                    case "baseline":
                        Assert.That(atomBaselineInLine, Is.EqualTo(line.Baseline).Within(0.001),
                            "baseline: atom baseline must coincide with line baseline");
                        break;
                    case "sub":
                        Assert.That(atomBaselineInLine, Is.EqualTo(line.Baseline + Fs * 0.2).Within(0.001),
                            "sub: atom baseline lowered by 0.2*fs");
                        break;
                    case "super":
                        Assert.That(atomBaselineInLine, Is.EqualTo(line.Baseline - Fs * 0.3).Within(0.001),
                            "super: atom baseline raised by 0.3*fs");
                        break;
                    case "middle":
                        Assert.That(atomMidInLine, Is.EqualTo(line.Baseline + XHeight * 0.5).Within(0.001),
                            "middle: atom vertical center at baseline + x-height/2");
                        break;
                    case "text-top":
                        Assert.That(atom.Y, Is.EqualTo(line.Baseline - Ascent).Within(0.001),
                            "text-top: atom top at parent content-area top");
                        break;
                    case "text-bottom":
                        Assert.That(atom.Y + atom.Height, Is.EqualTo(line.Baseline + Descent).Within(0.001),
                            "text-bottom: atom bottom at parent content-area bottom");
                        break;
                    case "4px":
                        Assert.That(atomBaselineInLine, Is.EqualTo(line.Baseline - 4).Within(0.001),
                            "<length>: positive length raises baseline by that amount");
                        break;
                    case "50%":
                        // Percentage resolves against the atom's used line-height.
                        // The atom inherits font-size 16 and has no own line-height,
                        // so used line-height = MonoFontMetrics.LineHeight(16) = 19.2.
                        Assert.That(atomBaselineInLine, Is.EqualTo(line.Baseline - LineHeight * 0.5).Within(0.001),
                            "<percentage>: raises baseline by % of atom's used line-height");
                        break;
                    default:
                        Assert.Fail("unhandled vertical-align case: " + raw);
                        break;
                }
            }
        }
    }
}
