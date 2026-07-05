// PAINT-1 probe: verifies that the paint pipeline emits DrawTextCommand.Bounds.Y
// values that correctly encode the flex item's absolute Y position.
//
// Background: FlexColumnTextCenteringTests already proves that FlexLayout places
// the two flex items (label, sub) with topPad == bottomPad within 0.5px.
// These tests probe the NEXT seam: does BoxToPaintConverter thread those Y
// coordinates through to DrawTextCommand.Bounds without adding any spurious
// offset?
//
// Math for MonoFontMetrics (ascentEm=0.8, lineHeightEm=1.2, default ctor):
//   label fontSize = 28px
//     metricLineHeight = 28 * 1.2 = 33.6
//     halfLeading     = (usedLineHeight - metricLineHeight) / 2 = 0
//     run.Y           = halfLeading = 0
//     DrawTextCommand.Bounds.Y = labelItem.AbsY + lineBox.Y(0) + run.Y(0)
//                              = labelItem.AbsY
//
//   sub fontSize = 11px
//     metricLineHeight = 11 * 1.2 = 13.2
//     halfLeading     = 0
//     DrawTextCommand.Bounds.Y = subItem.AbsY
//
// Baker (SdfTextRunBaker line 163):
//   baselineY = Bounds.Y + ascent = labelItem.AbsY + 0.8*28 = labelItem.AbsY + 22.4
//
// This must equal the InlineLayout spec position:
//   labelItem.Y + halfLeading + ascent = labelItem.Y + 0 + 22.4  (correct)
//
// These tests pin that the C# pipeline produces the right Bounds.Y. If the
// visual is still wrong in Unity after these tests pass, the bug is in the
// SDF/TMP rendering path (AscentLine mismatch vs our layout's Ascent()).
// That is documented as PAINT-1 in CSS_OPEN_GAPS.md.
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    public class FlexColumnTextPaintYTests {
        const string PlayBtnCss = @"
            .play-btn {
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 2px;
                width: 260px;
                height: 76px;
                padding: 0 48px;
                box-sizing: border-box;
            }
            .play-btn-label { font-size: 28px; font-weight: 900; }
            .play-btn-sub   { font-size: 11px; }
        ";

        const string PlayBtnHtml = "<button class=\"play-btn\">"
            + "<span class=\"play-btn-label\">PLAY</span>"
            + "<span class=\"play-btn-sub\">BEGIN STAGE</span>"
            + "</button>";

        // Helper: build and convert, return all DrawTextCommands.
        static List<DrawTextCommand> PaintTextCommands() {
            var (root, _, _) = Build(PlayBtnHtml, PlayBtnCss, viewportWidth: 800);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            return cmds.OfType<DrawTextCommand>().ToList();
        }

        // --- Test 1 ----------------------------------------------------------
        // The PLAY run must appear as a DrawTextCommand whose Bounds.Y is the
        // label flex-item's absolute Y position. Since the button has no top
        // margin/padding and lives at the top of the viewport, and justify-
        // content:center places the label at topPad = (76 - (33.6+2+13.2))/2
        // = 13.6, the expected Bounds.Y for PLAY ≈ 13.6.
        [Test]
        public void Play_label_DrawText_BoundsY_matches_flex_item_top() {
            var (root, _, _) = Build(PlayBtnHtml, PlayBtnCss, viewportWidth: 800);
            // Capture the label flex item's layout Y via FlexTestHelpers-style
            // walk. The layout tree has the button as a flex container; its
            // first BlockBox child is the label item.
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var playText = cmds.OfType<DrawTextCommand>()
                               .FirstOrDefault(c => c.Text.Contains("PLAY"));

            Assert.That(playText, Is.Not.Null, "Expected a DrawTextCommand for PLAY");

            // Compute expected topPad from the layout (same math as
            // FlexColumnTextCenteringTests but at paint resolution).
            // MonoFontMetrics default: label lineHeight=33.6, sub=13.2, gap=2.
            double labelH  = 28 * 1.2;  // 33.6
            double subH    = 11 * 1.2;  // 13.2
            double totalH  = labelH + 2 + subH; // 48.8
            double topPad  = (76 - totalH) / 2; // 13.6

            // The DrawTextCommand Bounds.Y should be the label item's absolute
            // Y (= topPad, since the button is at the top of the viewport).
            // halfLeading = 0 for this font/size, so run.Y = 0 and doesn't shift.
            TestContext.WriteLine($"PLAY DrawText Bounds.Y = {playText.Bounds.Y}, expected ≈ {topPad}");
            Assert.That(playText.Bounds.Y, Is.EqualTo(topPad).Within(0.5),
                "DrawTextCommand for PLAY must have Bounds.Y = label flex-item top " +
                "(halfLeading=0 with default MonoFontMetrics; no spurious vertical shift)");
        }

        // --- Test 2 ----------------------------------------------------------
        // The BEGIN STAGE run's Bounds.Y must equal topPad + labelH + gap.
        // This pins that the SECOND DrawTextCommand's Y is also correct,
        // confirming neither item's paint Y has a spurious shift applied.
        [Test]
        public void Sub_label_DrawText_BoundsY_matches_flex_item_top() {
            var cmds = PaintTextCommands();
            var subText = cmds.FirstOrDefault(c => c.Text.Contains("BEGIN STAGE"));

            Assert.That(subText, Is.Not.Null, "Expected a DrawTextCommand for BEGIN STAGE");

            double labelH  = 28 * 1.2; // 33.6
            double subH    = 11 * 1.2; // 13.2
            double totalH  = labelH + 2 + subH;
            double topPad  = (76 - totalH) / 2; // 13.6
            double expectedSubY = topPad + labelH + 2; // 49.2

            TestContext.WriteLine($"BEGIN STAGE DrawText Bounds.Y = {subText.Bounds.Y}, expected ≈ {expectedSubY}");
            Assert.That(subText.Bounds.Y, Is.EqualTo(expectedSubY).Within(0.5),
                "DrawTextCommand for BEGIN STAGE must have Bounds.Y = topPad + labelH + gap " +
                "(subItem.AbsY with halfLeading=0)");
        }

        // --- Test 3 ----------------------------------------------------------
        // The gap between the two Bounds.Y values must be exactly labelH + 2px
        // (= label line-box height + gap). This regression test catches any
        // bug where the Y of one run is shifted independently of the other.
        [Test]
        public void DrawText_BoundsY_gap_between_label_and_sub_equals_labelH_plus_gap() {
            var cmds = PaintTextCommands();
            var playText = cmds.FirstOrDefault(c => c.Text.Contains("PLAY"));
            var subText  = cmds.FirstOrDefault(c => c.Text.Contains("BEGIN STAGE"));

            Assert.That(playText, Is.Not.Null);
            Assert.That(subText,  Is.Not.Null);

            double labelLineBoxH = 28 * 1.2; // 33.6
            double expectedGap   = labelLineBoxH + 2; // 35.6

            double actualGap = subText.Bounds.Y - playText.Bounds.Y;
            TestContext.WriteLine($"Bounds.Y gap = {actualGap}, expected ≈ {expectedGap}");
            Assert.That(actualGap, Is.EqualTo(expectedGap).Within(0.5),
                "The Y distance between PLAY and BEGIN STAGE DrawTextCommands must equal " +
                "labelLineBoxHeight (28*1.2=33.6) + gap (2px) = 35.6px");
        }

        // --- Test 4 ----------------------------------------------------------
        // The bake math: baselineY = Bounds.Y + ascent.
        // For PLAY at 28px (MonoFontMetrics ascentEm=0.8):
        //   ascent = 28 * 0.8 = 22.4
        //   baselineY = Bounds.Y + 22.4
        // The baseline must lie inside the button height (0..76px). This pins
        // that no offset in the paint->baker handoff doubles the leading or
        // otherwise inflates the baseline beyond the button bounds.
        [Test]
        public void Play_label_implicit_baselineY_within_button_bounds() {
            var cmds = PaintTextCommands();
            var playText = cmds.FirstOrDefault(c => c.Text.Contains("PLAY"));
            Assert.That(playText, Is.Not.Null);

            // Baker formula: baselineY = Bounds.Y + ascent(fontSize)
            double fontSize = 28;
            double ascentEm = 0.8; // MonoFontMetrics default
            double ascent = fontSize * ascentEm; // 22.4
            double baselineY = playText.Bounds.Y + ascent;

            // Spec position: topPad + ascent (both = 13.6 + 22.4 = 36)
            double labelH = 28 * 1.2;
            double subH   = 11 * 1.2;
            double topPad = (76 - (labelH + 2 + subH)) / 2;
            double expectedBaseline = topPad + ascent;

            TestContext.WriteLine($"Implicit baselineY = {baselineY}, expected ≈ {expectedBaseline}");
            Assert.That(baselineY, Is.EqualTo(expectedBaseline).Within(0.5),
                "Baker would place baseline at Bounds.Y + ascent; this must equal " +
                "topPad + ascent (the spec-correct line-box baseline position).");
            // Also confirm the baseline is inside the button (0..76).
            Assert.That(baselineY, Is.GreaterThanOrEqualTo(0),
                "Baseline must be >= 0 (not above the button)");
            Assert.That(baselineY, Is.LessThanOrEqualTo(76),
                "Baseline must be <= 76 (not below the button)");
        }
    }
}
