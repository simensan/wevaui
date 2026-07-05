// Probe for a real play-button topology:
//
//   .play-btn { display:flex; flex-direction:column; align-items:center;
//                justify-content:center; gap:2px; height:76px; padding:0 48px; }
//   .play-btn-label { font-size:28px; font-weight:900; }
//   .play-btn-sub   { font-size:11px; }
//
//   <button class=play-btn>
//     <span class=play-btn-label>PLAY</span>
//     <span class=play-btn-sub>BEGIN STAGE</span>
//   </button>
//
// User-reported symptom: visually top-heavy in Unity render vs Chrome reference
// (Chrome centers both lines as a block; Unity appears to place them in the
// upper portion of the button with a visible gap at the bottom).
//
// These tests probe the layout numbers directly. With MonoFontMetrics (8px/char
// width, line-height ≈ fontSize × 1.2 default), we expect:
//   label line-box ≈ 28 × 1.2 = 33.6
//   sub   line-box ≈ 11 × 1.2 = 13.2
//   total content = 33.6 + 2 + 13.2 = 48.8
//   centered top pad = (76 - 48.8) / 2 ≈ 13.6
//   label.y ≈ 13.6, sub.y ≈ 13.6 + 33.6 + 2 = 49.2
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexColumnTextCenteringTests {
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

        [Test]
        public void PlayBtn_topology_centers_two_line_text_block_vertically() {
            // The two spans (blockified as anonymous flex items per CSS Flexbox §4)
            // are stacked column-wise. justify-content:center should center the
            // combined block (label + 2px gap + sub) vertically inside the 76px
            // button — equal padding above the label and below the sub.
            var (root, _, _) = Build(
                "<button class=\"play-btn\">"
                + "<span class=\"play-btn-label\">PLAY</span>"
                + "<span class=\"play-btn-sub\">BEGIN STAGE</span>"
                + "</button>",
                PlayBtnCss, viewportWidth: 800);
            var btn = FindFlex(root, "button");
            Assert.That(btn, Is.Not.Null, "play-btn must build as a flex container");
            Assert.That(btn.Height, Is.EqualTo(76).Within(0.5),
                "button height pinned by CSS");

            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            Assert.That(label, Is.Not.Null);
            Assert.That(sub, Is.Not.Null);

            // The combined content block top should equal the button top + topPad.
            double contentTop = label.Y;
            double contentBottom = sub.Y + sub.Height;
            double topPad = contentTop - btn.Y;
            double bottomPad = (btn.Y + btn.Height) - contentBottom;

            TestContext.WriteLine($"btn y={btn.Y} h={btn.Height}");
            TestContext.WriteLine($"label y={label.Y} h={label.Height}");
            TestContext.WriteLine($"sub y={sub.Y} h={sub.Height}");
            TestContext.WriteLine($"topPad={topPad} bottomPad={bottomPad}");

            // CSS Flexbox §8.2 justify-content:center — content centered means
            // top and bottom padding equal (within sub-pixel rounding).
            Assert.That(topPad, Is.EqualTo(bottomPad).Within(0.5),
                $"justify-content:center must produce equal top/bottom padding " +
                $"(top={topPad}, bottom={bottomPad}). Visible-top-heavy bug?");
        }

        [Test]
        public void PlayBtn_topology_gap_between_label_and_sub_is_2px() {
            // CSS Flexbox L1 §8.1 — `gap: 2px` on a column flex container
            // inserts exactly 2px between consecutive items along the main axis.
            var (root, _, _) = Build(
                "<button class=\"play-btn\">"
                + "<span class=\"play-btn-label\">PLAY</span>"
                + "<span class=\"play-btn-sub\">BEGIN STAGE</span>"
                + "</button>",
                PlayBtnCss, viewportWidth: 800);
            var btn = FindFlex(root, "button");
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            double gap = sub.Y - (label.Y + label.Height);
            Assert.That(gap, Is.EqualTo(2.0).Within(0.5),
                $"gap:2px must produce exactly 2px between label and sub (actual={gap})");
        }

        [Test]
        public void PlayBtn_topology_label_and_sub_are_anonymous_flex_items() {
            // CSS Flexbox §4: inline-display children of a flex container are
            // blockified — promoted to block-level so they become flex items.
            // Without blockification, a <span> child would be skipped by FlexLayout
            // (which only collects BlockBox children) and the column would collapse.
            var (root, _, _) = Build(
                "<button class=\"play-btn\">"
                + "<span class=\"play-btn-label\">PLAY</span>"
                + "<span class=\"play-btn-sub\">BEGIN STAGE</span>"
                + "</button>",
                PlayBtnCss, viewportWidth: 800);
            var btn = FindFlex(root, "button");
            Assert.That(btn.ChildList.Count, Is.EqualTo(2),
                "flex container must have 2 items (blockified label + sub)");
        }
    }
}
