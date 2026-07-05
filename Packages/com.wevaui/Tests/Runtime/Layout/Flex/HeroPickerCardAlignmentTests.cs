// Probe for a real hero-picker visual bug. Vanguard card renders
// visibly misaligned vs the other hero cards in the picker list. Layout
// topology (from main-menu.html + main-menu.css):
//
//   .hero-picker-card {
//     display: flex; align-items: center; gap: 12px; padding: 10px 12px;
//   }
//   .hero-picker-card-icon { width: 48px; height: 48px; flex-shrink: 0; }
//   .hero-picker-card-info { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
//   .hero-picker-card-name { font-size: 14px; }
//   .hero-picker-card-status { font-size: 10px; }
//
//   <button class=hero-picker-card>
//     <img class=hero-picker-card-icon src="...">
//     <div class=hero-picker-card-info>
//       <span class=hero-picker-card-name>NAME</span>
//       <span class=hero-picker-card-status>STATUS</span>   <-- empty for Vanguard
//     </div>
//   </button>
//
// User screenshot shows the Vanguard row (single-line info, no status text)
// visibly different from the other rows (two-line info). align-items:center
// on the parent flex should center both kinds of rows the same way relative
// to the 48px icon.
//
// These tests probe the actual layout numbers — if they pass, layout is
// correct and the visual bug is in paint; if they fail, layout has the bug.
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class HeroPickerCardAlignmentTests {
        const string Css = @"
            .hero-picker-card {
                display: flex; align-items: center; gap: 12px;
                padding: 10px 12px; width: 280px;
                box-sizing: border-box;
            }
            .hero-picker-card-icon {
                width: 48px; height: 48px;
                flex-shrink: 0;
            }
            .hero-picker-card-info {
                display: flex; flex-direction: column; gap: 2px; min-width: 0;
            }
            .hero-picker-card-name { font-size: 14px; }
            .hero-picker-card-status { font-size: 10px; }
        ";

        [Test]
        public void Two_line_info_card_icon_and_info_share_centered_y() {
            // Aptus-style card: icon + (name + status). Icon y should equal
            // ((rowH - 48) / 2) since align-items:center centers it vertically.
            // Info column has ~14 + 2 + 10 = 26px content; aligned to center.
            var (root, _, _) = Build(
                "<button class=\"hero-picker-card\">"
                + "<div class=\"hero-picker-card-icon\"></div>"
                + "<div class=\"hero-picker-card-info\">"
                +   "<span class=\"hero-picker-card-name\">Aptus</span>"
                +   "<span class=\"hero-picker-card-status\">Current</span>"
                + "</div>"
                + "</button>",
                Css, viewportWidth: 800);
            var card = FindFlex(root, "button");
            Assert.That(card, Is.Not.Null);
            var icon = ChildAt(card, 0);
            var info = ChildAt(card, 1);
            Assert.That(icon, Is.Not.Null);
            Assert.That(info, Is.Not.Null);

            double rowH = card.Height;
            double iconCenter = icon.Y + icon.Height / 2.0;
            double infoCenter = info.Y + info.Height / 2.0;
            double rowCenter = card.Y + rowH / 2.0;

            TestContext.WriteLine($"card y={card.Y} h={rowH} (center={rowCenter})");
            TestContext.WriteLine($"icon y={icon.Y} h={icon.Height} (center={iconCenter})");
            TestContext.WriteLine($"info y={info.Y} h={info.Height} (center={infoCenter})");

            Assert.That(iconCenter, Is.EqualTo(rowCenter).Within(0.5),
                "icon must be vertically centered in row");
            Assert.That(infoCenter, Is.EqualTo(rowCenter).Within(0.5),
                "info must be vertically centered in row");
        }

        [Test]
        public void One_line_info_card_icon_and_info_share_centered_y() {
            // Vanguard-style card: empty status → info column has only the
            // name span (~14px). align-items:center should still center both
            // children relative to the row.
            var (root, _, _) = Build(
                "<button class=\"hero-picker-card\">"
                + "<div class=\"hero-picker-card-icon\"></div>"
                + "<div class=\"hero-picker-card-info\">"
                +   "<span class=\"hero-picker-card-name\">Vanguard</span>"
                +   "<span class=\"hero-picker-card-status\"></span>"
                + "</div>"
                + "</button>",
                Css, viewportWidth: 800);
            var card = FindFlex(root, "button");
            Assert.That(card, Is.Not.Null);
            var icon = ChildAt(card, 0);
            var info = ChildAt(card, 1);

            double rowH = card.Height;
            double iconCenter = icon.Y + icon.Height / 2.0;
            double infoCenter = info.Y + info.Height / 2.0;
            double rowCenter = card.Y + rowH / 2.0;

            TestContext.WriteLine($"VANGUARD card y={card.Y} h={rowH} (center={rowCenter})");
            TestContext.WriteLine($"VANGUARD icon y={icon.Y} h={icon.Height} (center={iconCenter})");
            TestContext.WriteLine($"VANGUARD info y={info.Y} h={info.Height} (center={infoCenter})");

            Assert.That(iconCenter, Is.EqualTo(rowCenter).Within(0.5),
                "Vanguard icon must be vertically centered in row");
            Assert.That(infoCenter, Is.EqualTo(rowCenter).Within(0.5),
                "Vanguard info must be vertically centered in row");
        }

        // Source the production UA so changes to it automatically reflect
        // here. The test helper's `BuiltinUserAgent` is a stripped subset
        // that misses the button rule; without the production UA the bug
        // can't be reproduced in tests.
        static readonly string ProductionButtonUa = Weva.Css.UserAgentStylesheet.Source;

        [Test]
        public void Icon_x_position_is_flex_start_not_centred() {
            // CSS Flexbox L1 §8.1: when the flex container doesn't set
            // justify-content, the initial value `flex-start` applies. The
            // icon (first flex child) must be flush-left against the
            // padding edge — NOT centred along the main axis.
            //
            // Regression: UA stylesheet sets `justify-content: center` on
            // <button>. When author CSS overrides `display: flex` without
            // also overriding `justify-content`, the UA value bleeds
            // through and pushes the icon into the middle of the card.
            // Symptom in a real game: hero-picker icons appear floating in
            // the middle of the row instead of flush-left.
            //
            // The test helper's BuiltinUserAgent omits the button rule, so
            // we explicitly inject it to reproduce the production cascade.
            var (root, _, _) = Build(
                "<button class=\"hero-picker-card\">"
                + "<div class=\"hero-picker-card-icon\"></div>"
                + "<div class=\"hero-picker-card-info\">"
                +   "<span class=\"hero-picker-card-name\">Vanguard</span>"
                + "</div>"
                + "</button>",
                ProductionButtonUa + Css, viewportWidth: 800);
            var card = FindFlex(root, "button");
            Assert.That(card, Is.Not.Null);
            var icon = ChildAt(card, 0);
            var info = ChildAt(card, 1);
            double iconCardRelativeX = icon.X - card.X;
            TestContext.WriteLine($"card X={card.X} W={card.Width} icon X={icon.X} (card-relative={iconCardRelativeX}) info X={info.X}");
            Assert.That(iconCardRelativeX, Is.EqualTo(12.0).Within(0.5),
                $"icon must be at flex-start (X=card.X + padding-left = {card.X + 12}); got X={icon.X}. " +
                "If this fails, justify-content is leaking from UA `button` rule.");
        }

        [Test]
        public void Button_with_author_display_flex_uses_default_justify_content() {
            // Spec-level regression test: a <button> with author
            // `display: flex` (NO justify-content) must place its first
            // flex item at content-box-left. The UA's button rule must
            // not impose `justify-content: center` that leaks when the
            // author swaps display from the UA's `inline-flex` to `flex`.
            const string CssMin = @"
                .row {
                    display: flex;
                    width: 200px;
                    padding: 0;
                    border: 0;
                }
                .row > .item { width: 40px; height: 20px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<button class=\"row\">"
                + "<div class=\"item\"></div>"
                + "</button>",
                ProductionButtonUa + CssMin, viewportWidth: 400);
            var btn = FindFlex(root, "button");
            Assert.That(btn, Is.Not.Null);
            var item = ChildAt(btn, 0);
            double itemRel = item.X - btn.X;
            TestContext.WriteLine($"btn X={btn.X} W={btn.Width} item X={item.X} (rel={itemRel})");
            Assert.That(itemRel, Is.EqualTo(0.0).Within(0.5),
                $"author <button display:flex> with no justify-content must default to flex-start; got rel X={itemRel}");
        }

        [Test]
        public void Two_card_row_heights_should_be_equal_when_icon_dominates() {
            // Both cards have a 48px icon — the row height should be at least
            // 48 + 20 (padding). Whether info has 1 or 2 lines, the icon's
            // 48px dominates and the row height should be the SAME.
            // If Vanguard's row is shorter (info collapses without expanding
            // to icon height), the icon may overflow the row — that's the bug.
            var (twoLineRoot, _, _) = Build(
                "<button class=\"hero-picker-card\">"
                + "<div class=\"hero-picker-card-icon\"></div>"
                + "<div class=\"hero-picker-card-info\">"
                +   "<span class=\"hero-picker-card-name\">Aptus</span>"
                +   "<span class=\"hero-picker-card-status\">Current</span>"
                + "</div>"
                + "</button>",
                Css, viewportWidth: 800);
            var (oneLineRoot, _, _) = Build(
                "<button class=\"hero-picker-card\">"
                + "<div class=\"hero-picker-card-icon\"></div>"
                + "<div class=\"hero-picker-card-info\">"
                +   "<span class=\"hero-picker-card-name\">Vanguard</span>"
                +   "<span class=\"hero-picker-card-status\"></span>"
                + "</div>"
                + "</button>",
                Css, viewportWidth: 800);
            var twoLine = FindFlex(twoLineRoot, "button");
            var oneLine = FindFlex(oneLineRoot, "button");
            Assert.That(twoLine.Height, Is.EqualTo(oneLine.Height).Within(0.5),
                $"row heights diverge: 2-line={twoLine.Height} vs 1-line={oneLine.Height}");
            Assert.That(twoLine.Height, Is.GreaterThanOrEqualTo(48 + 20 - 0.5),
                "row must be at least 48 (icon) + 20 (2*10 padding) tall");
        }

        [Test]
        public void Info_column_with_min_width_0_shrinks_below_nowrap_text_max_content() {
            // CSS Flexbox L1 §4.5: an authored `min-width` value (including
            // explicit `0`) OVERRIDES the automatic content-based minimum.
            // `.hero-picker-card-info` is the canonical case:
            //   - row-flex parent card has 280px-of-card-content
            //   - icon takes 48 + 12 (gap)
            //   - info gets 280 - 24 (pad) - 48 - 12 = 196 of main-axis room
            //   - info's children are `white-space: nowrap;
            //     text-overflow: ellipsis` so per spec info clamps to 196 and
            //     the spans ellipsise (Chrome's render)
            // Pre-fix bug: FlexLayout.ResolveFlexibleLengths' rigid-sub-container
            // min-content floor (line 1519+) unconditionally raises any
            // GridBox/FlexBox child's TargetMainSize back up to MaxContentWidth,
            // ignoring the authored min-width:0. Info inflates to the full
            // nowrap text width, the card overflows, names render full-width.
            const string CssNoWrap = @"
                .hero-picker-card {
                    display: flex; align-items: center; gap: 12px;
                    padding: 10px 12px; width: 280px;
                    box-sizing: border-box;
                }
                .hero-picker-card-icon {
                    width: 48px; height: 48px;
                    flex-shrink: 0;
                }
                .hero-picker-card-info {
                    display: flex; flex-direction: column; gap: 2px; min-width: 0;
                }
                .hero-picker-card-name {
                    font-size: 14px; white-space: nowrap;
                    overflow: hidden; text-overflow: ellipsis;
                }
                .hero-picker-card-status {
                    font-size: 10px; white-space: nowrap;
                    overflow: hidden; text-overflow: ellipsis;
                }
            ";
            var (root, _, _) = Build(
                "<button class=\"hero-picker-card\">"
                + "<div class=\"hero-picker-card-icon\"></div>"
                + "<div class=\"hero-picker-card-info\">"
                +   "<span class=\"hero-picker-card-name\">A Really Long Hero Name That Should Not Inflate The Card</span>"
                +   "<span class=\"hero-picker-card-status\">Reach Hero Level 12 to unlock</span>"
                + "</div>"
                + "</button>",
                CssNoWrap, viewportWidth: 800);
            var card = FindFlex(root, "button");
            Assert.That(card, Is.Not.Null);
            var icon = ChildAt(card, 0);
            var info = ChildAt(card, 1);
            // card content-box width = 280 - 24 padding = 256
            // icon takes 48 of main axis, gap 12, info must take the remaining 196.
            double expectedInfo = 256 - 48 - 12;
            System.Console.WriteLine($"card W={card.Width} icon W={icon.Width} info W={info.Width} (expected info ≈ {expectedInfo})");
            Assert.That(info.Width, Is.EqualTo(expectedInfo).Within(1.0),
                $"min-width:0 must let info shrink to the row-flex remainder; got {info.Width}, expected ~{expectedInfo}");
        }

        [Test]
        public void Info_column_with_min_width_auto_keeps_max_content_floor() {
            // Inverse of the above: when min-width is `auto` (no authored
            // override), the rigid-sub-container floor at FlexLayout.cs:1519
            // SHOULD fire — preserves the match3-board fix where a grid
            // container with fixed tracks must hold its intrinsic min-content
            // width even if the parent row-flex doesn't have room. Spec:
            // CSS Flexbox §4.5 — automatic minimum size is content-based for
            // grid/flex sub-containers.
            const string CssAutoMin = @"
                .card {
                    display: flex; align-items: center; gap: 12px;
                    padding: 10px 12px; width: 280px;
                    box-sizing: border-box;
                }
                .icon { width: 48px; height: 48px; flex-shrink: 0; }
                .info {
                    display: flex; flex-direction: column; gap: 2px;
                    /* no min-width — defaults to auto */
                }
                .name { font-size: 14px; white-space: nowrap; }
            ";
            var (root, _, _) = Build(
                "<div class=\"card\">"
                + "<div class=\"icon\"></div>"
                + "<div class=\"info\">"
                +   "<span class=\"name\">A Really Long Hero Name</span>"
                + "</div>"
                + "</div>",
                CssAutoMin, viewportWidth: 800);
            var card = FindFlex(root, "div");
            var info = ChildAt(card, 1);
            // With min-width:auto, info's auto-min == its max-content. MonoFont
            // at 14px: "A Really Long Hero Name" = 22 chars * 7px = 154.
            // info must NOT collapse below this value.
            System.Console.WriteLine($"auto-min info W={info.Width}");
            Assert.That(info.Width, Is.GreaterThan(140),
                $"min-width:auto must keep the content-based minimum floor; got {info.Width}");
        }
    }
}
