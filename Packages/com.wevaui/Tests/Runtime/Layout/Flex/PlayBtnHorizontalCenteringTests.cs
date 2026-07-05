// User report (a real game, 2026-06-01): the "PLAY" text in the play-btn
// appears horizontally offset to the LEFT of "BEGIN STAGE" under it,
// even though both are centred by `align-items: center` in a column flex.
// The two spans use different letter-spacing values:
//
//   .play-btn-label { font-size: 28px; font-weight: 900; letter-spacing: 4px; }
//   .play-btn-sub   { font-size: 11px; font-weight: 600; letter-spacing: 1.2px; }
//
// Hypothesis: layout measures box width as sum(chars) + (N-1)*LS (correct,
// no trailing letter-spacing), but the SDF baker at SdfTextRunBaker.cs:293
// adds `req.LetterSpacingPx` after EVERY iteration (including the last
// character). If something downstream uses the SDF-baker's `AdvanceX` as
// the box width, the box is one extra LS wider than the visible glyph
// row, and centering the box pulls the visible text to the left.
//
// These tests probe whether the LAYOUT (BlockBox.Width) and the visible
// glyph extent (last-glyph-right-edge - first-glyph-left-edge) agree, and
// whether the two stacked spans land at the same x-centre.
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class PlayBtnHorizontalCenteringTests {
        const string Css = @"
            .play-btn {
                display: flex; flex-direction: column;
                align-items: center; justify-content: center;
                gap: 2px;
                width: 260px; height: 76px;
                padding: 0 48px;
                box-sizing: border-box;
            }
            .play-btn-label { font-size: 28px; font-weight: 900; letter-spacing: 4px; }
            .play-btn-sub   { font-size: 11px; font-weight: 600; letter-spacing: 1.2px; }
        ";

        [Test]
        public void Both_text_boxes_share_x_centre() {
            // align-items:center on the parent column flex must place both
            // child boxes with the same x-centre. They have different widths
            // (PLAY is 28px bold; BEGIN STAGE is 11px) but their box centres
            // must agree to within rounding.
            var (root, _, _) = Build(
                "<button class=\"play-btn\">"
                + "<span class=\"play-btn-label\">PLAY</span>"
                + "<span class=\"play-btn-sub\">BEGIN STAGE</span>"
                + "</button>",
                Css, viewportWidth: 800);
            var btn = FindFlex(root, "button");
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            Assert.That(label, Is.Not.Null);
            Assert.That(sub, Is.Not.Null);
            double labelCenter = label.X + label.Width / 2.0;
            double subCenter   = sub.X   + sub.Width   / 2.0;
            TestContext.WriteLine($"label X={label.X} W={label.Width} (center={labelCenter})");
            TestContext.WriteLine($"sub   X={sub.X}   W={sub.Width}   (center={subCenter})");
            Assert.That(labelCenter, Is.EqualTo(subCenter).Within(0.5),
                $"both child boxes must share x-centre; label={labelCenter}, sub={subCenter}");
        }

        [Test]
        public void Layout_box_width_excludes_trailing_letter_spacing() {
            // The LAYOUT measure (InlineLayout MeasureFastCached at L418)
            // uses `(text.Length - 1) * letterSpacingPx` — N-1 gaps, no
            // trailing. So Box.Width for "PLAY" with letter-spacing 4px
            // and a known char-width font should equal sum(chars) + 3*4 = 12
            // EXTRA pixels on top of the bare glyph widths. The exact value
            // depends on font metrics (mono = 0.5em per char), but we can
            // assert width(no-spacing) + (N-1)*4 == width(with-spacing).
            var (rootBare, _, _)    = Build(
                "<div style=\"font-size:28px;font-weight:900\">PLAY</div>", null, viewportWidth: 800);
            var (rootSpaced, _, _) = Build(
                "<div style=\"font-size:28px;font-weight:900;letter-spacing:4px\">PLAY</div>", null, viewportWidth: 800);
            var bare = FindFirst<BlockBox>(rootBare,    e => e?.TagName == "div");
            var spaced = FindFirst<BlockBox>(rootSpaced, e => e?.TagName == "div");
            Assert.That(bare,   Is.Not.Null);
            Assert.That(spaced, Is.Not.Null);
            // Find the inner LineBox inside each div to read measured text width.
            var bareLine = FindFirst<LineBox>(bare,    null);
            var spacedLine = FindFirst<LineBox>(spaced, null);
            Assert.That(bareLine,   Is.Not.Null);
            Assert.That(spacedLine, Is.Not.Null);
            TestContext.WriteLine($"bare   line W={bareLine.Width}");
            TestContext.WriteLine($"spaced line W={spacedLine.Width}");
            // "PLAY" has 4 chars → 3 inter-char gaps. 3 * 4px = 12px extra.
            double diff = spacedLine.Width - bareLine.Width;
            Assert.That(diff, Is.EqualTo(12.0).Within(0.5),
                $"letter-spacing must add (N-1)*4 = 12px to measured width; got diff={diff}");
        }

        static T FindFirst<T>(Box root, System.Func<Weva.Dom.Element, bool> pred) where T : Box {
            if (root is T t && (pred == null || pred(root.Element))) return t;
            foreach (var c in root.ChildList) {
                var hit = FindFirst<T>(c, pred);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
