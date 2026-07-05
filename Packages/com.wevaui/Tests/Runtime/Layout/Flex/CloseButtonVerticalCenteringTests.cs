// User report (a real game, image #50): the Close button in the hero-
// picker dialog renders with its "Close" text glued to the top of the
// 38px-tall button instead of vertically centred.
//
// CSS (from a real main-menu.css):
//   .btn                { height: 38px; padding: 0 18px;
//                         font-size: 13px; font-weight: 900;
//                         border: none; border-radius: var(--radius-sm); }
//   .btn-ghost          { background: #2a3346; color: var(--text-secondary); }
//   .hero-picker-close  { padding: 6px 14px; }
//
// HTML:
//   <button class="btn btn-ghost hero-picker-close">Close</button>
//
// Expectation: the single-line "Close" text is vertically centred
// inside the 38px-tall button. Chrome achieves this via native-button
// anonymous content-box centring; Weva matches via the UA's
// `button { display: inline-flex; align-items: center }`.
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class CloseButtonVerticalCenteringTests {
        // Mirror of the relevant production UA fragment plus the .btn
        // author CSS. The test helper's BuiltinUserAgent omits the
        // button rule (it's a stripped subset), so we prepend the
        // production UA here to reproduce the cascade a real game sees.
        static readonly string Css = Weva.Css.UserAgentStylesheet.Source + @"
            .btn {
                height: 38px;
                padding: 0 18px;
                font-size: 13px;
                font-weight: 900;
                border: none;
            }
            .btn-ghost { background: #2a3346; color: #cfd6e2; }
            .hero-picker-close { padding: 6px 14px; }
        ";

        [Test]
        public void Close_button_text_is_vertically_centred_in_38px_box() {
            // Spec: CSS Flexbox L1 §8.3 align-items: center. With UA
            // `button { display: inline-flex; align-items: center }`
            // and author `height: 38px; padding: 6px 14px`, the single
            // text line (height ≈ 17.29 in MonoFont at 13px) must be
            // centred — top gap ≈ bottom gap ≈ 10.36 each.
            var (root, _, _) = Build(
                "<button class=\"btn btn-ghost hero-picker-close\">Close</button>",
                Css, viewportWidth: 400);
            var btn = FindFirstBoxByClass(root, "btn");
            Assert.That(btn, Is.Not.Null, "<button.btn> box must exist");
            Assert.That(btn.Height, Is.EqualTo(38.0).Within(0.5),
                "author height:38px must win");

            // The text-bearing inner box is the button's only content line
            // (the line box wrapping the text run).
            var inner = btn.ChildList.Count > 0 ? btn.ChildList[0] : null;
            Assert.That(inner, Is.Not.Null, "button must contain a content line box");
            double topGap = inner.Y - btn.Y;
            double bottomGap = (btn.Y + btn.Height) - (inner.Y + inner.Height);
            TestContext.WriteLine($"btn Y={btn.Y} H={btn.Height} | inner Y={inner.Y} H={inner.Height} | topGap={topGap} bottomGap={bottomGap}");
            Assert.That(topGap, Is.EqualTo(bottomGap).Within(0.5),
                $"text must be vertically centred — topGap={topGap} vs bottomGap={bottomGap}. " +
                "If topGap ≈ 6 and bottomGap is much larger, align-items has been dropped from the UA button rule.");
        }

        [Test]
        public void Bare_btn_text_is_vertically_centred_in_38px_box() {
            // Same invariant, no hero-picker padding override. .btn has
            // padding: 0 18px so any vertical centring must come from
            // the UA's inline-flex/align-items combo.
            var (root, _, _) = Build(
                "<button class=\"btn\">Apply</button>",
                Css, viewportWidth: 400);
            var btn = FindFirstBoxByClass(root, "btn");
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.Height, Is.EqualTo(38.0).Within(0.5));
            var inner = btn.ChildList.Count > 0 ? btn.ChildList[0] : null;
            Assert.That(inner, Is.Not.Null);
            double topGap = inner.Y - btn.Y;
            double bottomGap = (btn.Y + btn.Height) - (inner.Y + inner.Height);
            TestContext.WriteLine($"bare btn topGap={topGap} bottomGap={bottomGap}");
            Assert.That(topGap, Is.EqualTo(bottomGap).Within(0.5),
                "bare .btn text must vertically centre via UA align-items:center");
        }

        [Test]
        public void Heropick_card_icon_still_flex_start_after_button_align_fix() {
            // Regression guard: restoring `align-items: center` to the UA
            // `button` rule must NOT bring back the `justify-content`
            // bleed that HEROPICK-1 was about. Author CSS sets
            // `display: flex; align-items: center` (so the UA's align-
            // items is moot anyway), and does NOT set justify-content.
            // First flex child must remain at flex-start (X=padding-left).
            const string CardCss = @"
                .card {
                    display: flex; align-items: center; gap: 12px;
                    padding: 10px 12px; width: 280px;
                    box-sizing: border-box;
                }
                .card-icon { width: 48px; height: 48px; flex-shrink: 0; }
                .card-info { display: flex; flex-direction: column; min-width: 0; }
            ";
            var (root, _, _) = Build(
                "<button class=\"card\">"
                + "<div class=\"card-icon\"></div>"
                + "<div class=\"card-info\"><span>Vanguard</span></div>"
                + "</button>",
                Weva.Css.UserAgentStylesheet.Source + CardCss, viewportWidth: 600);
            var card = FindFlex(root, "button");
            Assert.That(card, Is.Not.Null);
            var icon = ChildAt(card, 0);
            double iconRel = icon.X - card.X;
            TestContext.WriteLine($"icon X={icon.X} card X={card.X} (rel={iconRel})");
            Assert.That(iconRel, Is.EqualTo(12.0).Within(0.5),
                "HEROPICK-1 guard: icon must be flush-left (padding-left), " +
                $"not centred. Got rel X={iconRel}");
        }

        static Box FindFirstBoxByClass(Box root, string className) {
            if (root.Element != null) {
                string cls = root.Element.ClassName ?? "";
                if (cls.Contains(className)) return root;
            }
            foreach (var c in root.ChildList) {
                var hit = FindFirstBoxByClass(c, className);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
