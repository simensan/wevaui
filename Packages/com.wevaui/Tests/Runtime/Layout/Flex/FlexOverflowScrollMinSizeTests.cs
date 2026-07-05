using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // CSS Flexbox L1 §4.5 — Automatic Minimum Size of Flex Items.
    //
    //   "for scroll containers the automatic minimum size is zero"
    //
    // A flex item that is a scroll container on the main axis (overflow:
    // auto / scroll / hidden / overlay) MUST be allowed to shrink below
    // its content-based min, so that the assigned flex space wins and
    // overflow can scroll. Without this, a `.scroll { flex:1; overflow-y:
    // auto }` child of a height-constrained column-flex parent grows to
    // fit its content (e.g. 800 px) instead of staying clipped to the
    // parent's 300 px, and overflow:auto never triggers a scrollbar.
    //
    // Repro from a real game UI: the hero-picker detail panel, the challenge
    // list, the game-over skill list, and the mastery tree all needed
    // `height: 0; min-height: 0` workarounds before this regression test
    // shipped. With the fix, natural `flex: 1 + overflow: auto` works as
    // it does in Chrome.
    public class FlexOverflowScrollMinSizeTests {
        const string Css = @"
            .parent {
                height: 300px;
                display: flex;
                flex-direction: column;
            }
            .scroll {
                flex: 1;
                overflow-y: auto;
                display: flex;
                flex-direction: column;
            }
            .filler { height: 200px; }
        ";

        static FlexBox ScrollBox(Box root) {
            // The first FlexBox is the parent; the .scroll inner flex is
            // the next FlexBox in tree-order.
            int seen = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is FlexBox fb) {
                    if (seen == 1) return fb;
                    seen++;
                }
            }
            return null;
        }

        [Test]
        public void Flex_child_with_overflow_y_auto_does_not_exceed_parent_height() {
            // Four 200-px fillers (= 800 px total content) inside a 300-px
            // parent. The .scroll child has flex:1 + overflow-y:auto, so it
            // must clip to 300 px and let its inner content scroll — NOT
            // grow to fit the 800 px of content.
            var (root, _, _) = Build(
                "<div class=\"parent\"><div class=\"scroll\">" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "</div></div>",
                Css, viewportWidth: 800);
            var scroll = ScrollBox(root);
            Assert.That(scroll, Is.Not.Null, "expected an inner .scroll FlexBox");
            Assert.That(scroll.Height, Is.EqualTo(300.0).Within(0.5),
                "scroll container should clip to parent's 300 px allotment, not grow to fit 800 px of content");
        }

        [Test]
        public void Flex_child_with_overflow_y_scroll_keyword_clips_too() {
            // `overflow-y: scroll` is the always-shows-scrollbar variant of
            // `auto`; CSS Flexbox §4.5 lumps them together for the auto-min
            // resolution.
            var css = Css.Replace("overflow-y: auto", "overflow-y: scroll");
            var (root, _, _) = Build(
                "<div class=\"parent\"><div class=\"scroll\">" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "</div></div>",
                css, viewportWidth: 800);
            var scroll = ScrollBox(root);
            Assert.That(scroll.Height, Is.EqualTo(300.0).Within(0.5));
        }

        [Test]
        public void Flex_child_with_overflow_hidden_clips_too() {
            // `overflow: hidden` also creates a scroll container per the
            // §4.5 grouping, so the same auto-min: 0 resolution applies.
            var css = Css.Replace("overflow-y: auto", "overflow-y: hidden");
            var (root, _, _) = Build(
                "<div class=\"parent\"><div class=\"scroll\">" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "</div></div>",
                css, viewportWidth: 800);
            var scroll = ScrollBox(root);
            Assert.That(scroll.Height, Is.EqualTo(300.0).Within(0.5));
        }

        [Test]
        public void Flex_child_with_overflow_visible_still_grows_to_fit_content() {
            // Regression guard: the fix must ONLY kick in for scroll
            // containers. A `overflow: visible` child must keep the
            // legacy content-driven sizing — otherwise content-driven
            // column-flex layouts like settings.html where each `.group`
            // grows to its measured content height would silently
            // collapse to the assigned flex space.
            var css = Css.Replace("overflow-y: auto", "overflow-y: visible");
            var (root, _, _) = Build(
                "<div class=\"parent\"><div class=\"scroll\">" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "<div class=\"filler\"></div>" +
                "</div></div>",
                css, viewportWidth: 800);
            var scroll = ScrollBox(root);
            Assert.That(scroll.Height, Is.GreaterThanOrEqualTo(700.0),
                "with overflow: visible the child should grow to fit its 800 px of content (legacy behaviour)");
        }
    }
}
