// Real game-UI visible bug probe — the `.reward-chip` (inline-flex) inside
// `.objective-reward` (a default block wrapper) inside `.objective-body`
// (column flex with overflow:hidden) inside `.objective-card`
// (row flex with overflow:hidden). User report: the chip's bottom edge
// is clipped by an ancestor's `overflow: hidden` because the ancestor
// chain's computed height does NOT include the chip's outer height.
//
// The visible chain at the time of the report:
//   .objective-body (column flex, overflow:hidden) — outline ENDS above chip
//   .objective-reward (default block) — outline ENDS above chip
//   .reward-chip (inline-flex) — sticks out below both parents
//   .objective-card (overflow:hidden) clips the chip's bottom
//
// This test pins the inline-flex contribution to its parent block's
// height. Per CSS 2.1 §10.6.3, a block containing only inline-level
// children sizes its height to the line-box stack — and inline-flex
// contributes its outer (margin) box to that line.
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Inline {
    public class RewardChipObjectiveTests {
        const string Css = @"
            .objective-body {
                display: flex; flex-direction: column; gap: 4px;
                min-width: 0; overflow: hidden;
            }
            .objective-name { font-size: 14px; font-weight: 800; }
            .objective-desc { font-size: 12px; line-height: 1.35; }
            .objective-reward { /* default block */ }
            .reward-chip {
                display: inline-flex;
                padding: 3px 8px;
                background: #2a2645;
                font-size: 11px;
                font-weight: 700;
            }
        ";

        [Test]
        public void Reward_wrapper_height_includes_inline_flex_chip_outer_height() {
            // Cardinal check: `.objective-reward` (default block wrapper) must
            // be at least as tall as its single inline-flex child `.reward-chip`.
            // Spec: a block with only inline-level content sizes to its line
            // box stack; inline-flex contributes its OUTER height (margin box,
            // = border-box for chip without margins).
            //
            // For MonoFontMetrics at 11px (line-height ratio 1.2 default):
            //   text line-box ≈ 11 * 1.2 = 13.2
            //   chip content height = 13.2
            //   chip outer (border-box) = 13.2 + 6 padding = 19.2
            //   reward.Height should ≈ 19.2
            var (root, _, _) = Build(
                "<div class=\"objective-body\" style=\"width:240px\">"
                + "<div class=\"objective-name\">The Camp</div>"
                + "<div class=\"objective-desc\">Complete this objective.</div>"
                + "<div class=\"objective-reward\">"
                + "<span class=\"reward-chip\">Spirit Guardians</span>"
                + "</div>"
                + "</div>",
                Css, viewportWidth: 800);
            var body = FindFlex(root, "div");
            Assert.That(body, Is.Not.Null);
            var reward = ChildAt(body, 2);
            Assert.That(reward, Is.Not.Null, "third child = objective-reward");
            // reward-chip is an inline-flex element → wrapped in a LineBox
            // inside `.objective-reward` (the parent block treats inline-flex
            // as an inline-level atom). Walk reward.LineBox.Children to find
            // the chip atom, not reward.ChildList directly.
            BlockBox chip = FindByClass(reward, "reward-chip");
            Assert.That(chip, Is.Not.Null, "reward-chip must build as a BlockBox descendant");
            System.Console.WriteLine($"reward.W={reward.Width} reward.H={reward.Height}");
            System.Console.WriteLine($"chip.W={chip.Width} chip.H={chip.Height} (outer={chip.Height + chip.MarginTop + chip.MarginBottom})");
            // reward wrapper must contain the chip's outer height vertically.
            Assert.That(reward.Height, Is.GreaterThanOrEqualTo(chip.Height - 0.5),
                $".objective-reward.Height ({reward.Height}) must be >= chip.Height ({chip.Height}). " +
                "If smaller, the wrapper collapsed and ancestor overflow:hidden will clip the chip.");
        }

        static BlockBox FindByClass(Box root, string cls) {
            if (root is BlockBox bb && bb.Element?.ClassName != null
                && bb.Element.ClassName.Contains(cls)) return bb;
            foreach (var c in root.ChildList) {
                var hit = FindByClass(c, cls);
                if (hit != null) return hit;
            }
            return null;
        }

        [Test]
        public void Row_flex_parent_reads_column_flex_child_height_with_gaps() {
            // The canonical real-world bug: a column-flex body (with `gap`)
            // is placed inside a row-flex card. ComputeLineCrossSize on the
            // card was reading `body.PreFlexCrossHeight` (BlockLayout's
            // sum-WITHOUT-gaps stamp) instead of `body.Box.Height`
            // (FinalizeContainerMainSize's correct sum-with-gaps). The row's
            // cross extent under-reported by (n-1) * row-gap, and the card's
            // overflow:hidden then clipped the body's last child.
            //
            // Topology:
            //   card  (row-flex, padding:10, overflow:hidden)
            //     check  (18x18, flex-shrink:0)
            //     body   (column-flex, gap:4, items: name, desc, reward)
            //       reward (inline-flex chip)
            const string CardCss = @"
                .card {
                    display: flex; align-items: center; gap: 10px;
                    padding: 10px; overflow: hidden;
                    width: 360px; box-sizing: border-box;
                }
                .check { width: 18px; height: 18px; flex-shrink: 0; }
                .body {
                    flex: 1 1 auto;
                    display: flex; flex-direction: column; gap: 4px;
                    min-width: 0; overflow: hidden;
                }
                .name { font-size: 14px; font-weight: 800; }
                .desc { font-size: 12px; }
                .reward { /* default block */ }
                .chip {
                    display: inline-flex;
                    padding: 3px 8px;
                    font-size: 11px; font-weight: 700;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"card\">"
                + "<div class=\"check\"></div>"
                + "<div class=\"body\">"
                +   "<div class=\"name\">The Camp</div>"
                +   "<div class=\"desc\">Complete this objective.</div>"
                +   "<div class=\"reward\">"
                +     "<span class=\"chip\">Spirit Guardians</span>"
                +   "</div>"
                + "</div>"
                + "</div>",
                CardCss, viewportWidth: 800);
            var card = FindFlex(root, "div");
            Assert.That(card, Is.Not.Null);
            // card has padding:10, so content area = card.Height - 20.
            // body's height (column flex with gap:4 and three items) MUST
            // include both gaps. card.contentH MUST >= body.outerH so the
            // body's last child is INSIDE the card's content rect (no clip
            // by overflow:hidden).
            var body = FindByClass(card, "body");
            Assert.That(body, Is.Not.Null);
            System.Console.WriteLine($"card.H={card.Height} body.H={body.Height} card.contentH={card.Height - 20}");
            double cardContentH = card.Height - 20;
            Assert.That(cardContentH, Is.GreaterThanOrEqualTo(body.Height - 0.5),
                $"card.contentH ({cardContentH}) must contain body.Height ({body.Height}) without clipping");
        }

        [Test]
        public void Body_column_flex_height_includes_reward_row() {
            // Sibling check: the column-flex `.objective-body`'s computed
            // Height must sum all three rows (name + desc + reward + gaps),
            // not just (name + desc). If the third item (reward) collapses,
            // body.Height = name + 4 + desc + 4 ≈ short, and the reward
            // overflows BELOW body's bottom edge — which is exactly what
            // the user sees in the screenshot (orange outline ends above
            // the chip).
            var (root, _, _) = Build(
                "<div class=\"objective-body\" style=\"width:240px\">"
                + "<div class=\"objective-name\">X</div>"
                + "<div class=\"objective-desc\">Y</div>"
                + "<div class=\"objective-reward\">"
                + "<span class=\"reward-chip\">Z</span>"
                + "</div>"
                + "</div>",
                Css, viewportWidth: 800);
            var body = FindFlex(root, "div");
            var name = ChildAt(body, 0);
            var desc = ChildAt(body, 1);
            var reward = ChildAt(body, 2);
            double sum = name.Height + desc.Height + reward.Height + 8; // 2 gaps of 4
            System.Console.WriteLine($"body.H={body.Height} (sum N+D+R+2*gap = {sum})");
            System.Console.WriteLine($"name.H={name.Height} desc.H={desc.Height} reward.H={reward.Height}");
            Assert.That(body.Height, Is.GreaterThanOrEqualTo(sum - 0.5),
                $"body.Height ({body.Height}) must cover all three rows + gaps ({sum})");
        }
    }
}
