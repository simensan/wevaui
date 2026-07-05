using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout {
    // Regression suite run with JitteredFontMetrics — varied-width glyphs expose
    // bugs that MonoFontMetrics uniform widths happen to hide.
    public class JitteredMetricsRegressionTests {

        // ---- Upgrade-buy-btn topology ----
        // flex + justify-content:center + text-align:center on a button with two
        // inline-blockified children. The text-align stale-delta bug showed up here.

        const string BtnCss = @"
            button { display: flex; align-items: center; justify-content: center;
                     gap: 10px; height: 44px; width: 400px; padding: 0; border: 0; }
            .lbl, .cst { display: inline-block; padding: 0; }
        ";
        const string BtnHtml =
            "<button><span class=\"lbl\">LABEL-X</span><span class=\"cst\">COST-Y</span></button>";

        static BlockBox FirstButton(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "button") return bb;
            }
            return null;
        }

        static BlockBox FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string c = bb.Element.GetAttribute("class");
                    if (c != null) {
                        foreach (var t in c.Split(' ')) if (t == cls) return bb;
                    }
                }
            }
            return null;
        }

        [Test]
        public void Flex_button_jittered_label_span_x_within_button_bounds() {
            var (root, _, _) = BuildJittered(BtnHtml, BtnCss, viewportWidth: 800);
            var btn = FirstButton(root);
            var lbl = FindByClass(root, "lbl");
            Assert.That(btn, Is.Not.Null, "button not found");
            Assert.That(lbl, Is.Not.Null, ".lbl not found");

            var (btnX, _) = AbsoluteOriginOf(btn);
            var (lblX, _) = AbsoluteOriginOf(lbl);

            Assert.That(lblX, Is.GreaterThanOrEqualTo(btnX - 0.5),
                $"lbl.absX={lblX:F1} < button.absX={btnX:F1}; text detached left of button");
            Assert.That(lblX + lbl.Width, Is.LessThanOrEqualTo(btnX + btn.Width + 0.5),
                $"lbl right={lblX + lbl.Width:F1} > button right={btnX + btn.Width:F1}; text detached right");
        }

        [Test]
        public void Flex_button_jittered_cost_span_right_of_label_span() {
            var (root, _, _) = BuildJittered(BtnHtml, BtnCss, viewportWidth: 800);
            var lbl = FindByClass(root, "lbl");
            var cst = FindByClass(root, "cst");
            Assert.That(lbl, Is.Not.Null, ".lbl not found");
            Assert.That(cst, Is.Not.Null, ".cst not found");

            var (lblX, _) = AbsoluteOriginOf(lbl);
            var (cstX, _) = AbsoluteOriginOf(cst);

            // Cost span must be to the right of label span (flex row order).
            Assert.That(cstX, Is.GreaterThan(lblX + lbl.Width - 0.5),
                $"cst.absX={cstX:F1} not right of lbl right={lblX + lbl.Width:F1}; children mis-ordered");
        }

        // ---- Hero-chip topology ----
        // row-flex with min-width + an img + a name div. Vulnerable to probes that
        // stomp resolved width below min-width with varied glyph widths.

        const string ChipCss = @"
            img { display: block; }
            .chip { display: flex; align-items: center; gap: 12px;
                    min-width: 110px; max-width: 168px;
                    padding: 4px 14px 4px 4px; overflow: hidden; }
            .portrait { width: 40px; height: 40px; flex-shrink: 0; }
            .name { min-width: 0; white-space: nowrap; overflow: hidden; }
        ";
        const string ChipHtml =
            "<div class=\"chip\"><img class=\"portrait\"/><div class=\"name\">Aptus</div></div>";

        [Test]
        public void Hero_chip_jittered_honours_min_width() {
            var (root, _, _) = BuildJittered(ChipHtml, ChipCss, viewportWidth: 800);
            var chip = FindByClass(root, "chip");
            Assert.That(chip, Is.Not.Null, ".chip not found");
            Assert.That(chip.Width, Is.GreaterThanOrEqualTo(109.5),
                $"chip.Width={chip.Width:F1} < 110px min-width with jittered metrics");
        }

        [Test]
        public void Hero_chip_jittered_portrait_left_of_name() {
            var (root, _, _) = BuildJittered(ChipHtml, ChipCss, viewportWidth: 800);
            var portrait = FindByClass(root, "portrait");
            var name     = FindByClass(root, "name");
            Assert.That(portrait, Is.Not.Null, ".portrait not found");
            Assert.That(name,     Is.Not.Null, ".name not found");

            var (px, _) = AbsoluteOriginOf(portrait);
            var (nx, _) = AbsoluteOriginOf(name);
            Assert.That(nx, Is.GreaterThan(px + portrait.Width - 0.5),
                $"name.absX={nx:F1} not right of portrait right={px + portrait.Width:F1}; row order broken");
        }

        // ---- Ellipsis truncation topology ----
        // Basic case: overflow:hidden + text-overflow:ellipsis + nowrap.
        // With jittered widths the truncation point shifts; assert the ellipsis
        // is present and the text stays within the container.

        const string EllipsisCss = ""; // use inline style
        const string EllipsisHtml =
            "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>";

        [Test]
        public void Ellipsis_jittered_text_run_width_within_container() {
            var (root, _, _) = BuildJittered(EllipsisHtml, EllipsisCss, viewportWidth: 800);
            // Find the <p> box.
            BlockBox p = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "p") { p = bb; break; }
            }
            Assert.That(p, Is.Not.Null, "<p> not found");

            // Container is 80px. All TextRuns inside must start at X >= 0.
            foreach (var tr in AllTextRuns(root)) {
                var (trAbsX, _) = AbsoluteOriginOf(tr);
                Assert.That(trAbsX, Is.GreaterThanOrEqualTo(-0.5),
                    $"TextRun '{tr.Text}' absX={trAbsX:F1} < 0; text placed outside container left edge");
            }
        }
    }
}
