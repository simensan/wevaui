// PAINT-1 follow-up (2026-06-01): diagnostic log shows
//   FlexLayout.Place <span.play-btn-label>: ... container[play-btn] ...
//   line.CrossSize=1777 ... → Box.X=856.65
// for a button that is visibly only ~260px wide. line.CrossSize is the
// flex container's cross-axis size (= ContentWidth for a column flex);
// 1777 ≈ viewport (1801) minus 12 (border-box UA `button { padding: 2 6 }`).
//
// Topology:
//   .play-action { position: absolute; bottom: 32px; left: 50%;
//                  transform: translateX(-50%); }            -- no width
//     <button class="play-btn" />                             -- min-width: 260; height: 76
//       <span class="play-btn-label">PLAY</span>
//       <span class="play-btn-sub">PRESS TO START</span>
//
// CSS Positioned Layout L3 §10.3.7 — width:auto with at most one
// horizontal pin shrinks to fit: width = min(max-content,
// max(min-content, available)). With .play-btn's `min-width: 260px`,
// max-content of .play-action is at least 260, so the shrunk width
// should be ~260.
//
// These tests assert the post-layout state for the play-action subtree:
//   1. .play-action.Width ≈ 260   (shrink-to-fit)
//   2. .play-btn.Width ≈ 260       (min-width clamp on the flex container)
//   3. .play-btn.ContentWidth ≈ 248 (260 - UA button padding 6+6)
//   4. Each child span's X is centred inside the button's content box
//      (label.X + label.W/2 ≈ btn.X + 6 + 248/2 = btn.X + 130).
//
// If any of these fail, the bug is reproduced under unit-test conditions
// (MonoFontMetrics, deterministic viewport) and we have a fixture for
// the engine fix.
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class PlayActionAbsoluteShrinkTests {
        const string Html =
            "<div style=\"position:relative;width:1801px;height:744px\">"
              + "<section class=\"tab-page play-grid\">"
                + "<div class=\"play-action\">"
                  + "<button class=\"play-btn\">"
                    + "<span class=\"play-btn-label\">PLAY</span>"
                    + "<span class=\"play-btn-sub\">PRESS TO START</span>"
                  + "</button>"
                + "</div>"
              + "</section>"
            + "</div>";

        // .tab-page mirror — a real game's actual containing-block ancestor for
        // .play-action. position: absolute pinned all four sides, display: grid,
        // 24px 28px padding. The grid container is the CB for the absolute
        // .play-action (CSS Grid §9 — abs descendants of a grid container CB
        // to the grid container's padding edges by default).
        const string CssWithTabPage = @"
            .tab-page {
                position: absolute;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                display: grid;
                padding: 24px 28px;
                gap: 24px;
            }
            .play-grid {
                grid-template-columns: 360px 420px;
                grid-template-rows: 1fr;
                justify-content: space-between;
            }
            .play-action {
                position: absolute;
                bottom: 32px;
                left: 50%;
                transform: translateX(-50%);
                z-index: 5;
            }
            .play-btn {
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 2px;
                min-width: 260px;
                height: 76px;
                border-radius: 8px;
            }
            .play-btn-label {
                font-size: 28px;
                font-weight: 900;
                letter-spacing: 4px;
            }
            .play-btn-sub {
                font-size: 11px;
                font-weight: 800;
                text-transform: uppercase;
            }
        ";

        [Test]
        public void PlayBtn_text_block_is_vertically_centred_in_button() {
            // CSS Flexbox §8.2: justify-content:center on a column flex
            // centres the items along the main (vertical) axis. The combined
            // block (label + gap + sub) must have equal pad above and below
            // inside the button's content box.
            var (root, _, _) = Build(Html, CssWithTabPage, viewportWidth: 1801, viewportHeight: 744);
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null);
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            Assert.That(label, Is.Not.Null);
            Assert.That(sub, Is.Not.Null);
            double padTop = btn.PaddingTop + btn.BorderTop;
            double padBottom = btn.PaddingBottom + btn.BorderBottom;
            double topGap = label.Y - padTop;
            double bottomGap = (btn.Height - padBottom) - (sub.Y + sub.Height);
            TestContext.WriteLine($"btn W={btn.Width} CW={btn.ContentWidth} H={btn.Height} CH={btn.ContentHeight}");
            TestContext.WriteLine($"btn padTop={padTop} padBottom={padBottom}");
            TestContext.WriteLine($"label.X={label.X} label.Y={label.Y} label.W={label.Width} label.H={label.Height}");
            TestContext.WriteLine($"sub.X={sub.X} sub.Y={sub.Y} sub.W={sub.Width} sub.H={sub.Height}");
            TestContext.WriteLine($"topGap={topGap} bottomGap={bottomGap}");
            Assert.That(topGap, Is.EqualTo(bottomGap).Within(0.5),
                $"justify-content:center must produce equal gaps; topGap={topGap}, bottomGap={bottomGap}");
        }

        [Test]
        public void PlayAction_inside_grid_tab_page_centres_play_btn() {
            // Real topology: .play-action is `position:absolute` inside
            // `.tab-page` (which is `display:grid; position:absolute`). The
            // shrunk play-action width must still drive a 260-wide play-btn
            // whose children are centred at content-box centre.
            var (root, _, _) = Build(Html, CssWithTabPage, viewportWidth: 1801, viewportHeight: 744);
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            var playAction = FindFirstByClass<BlockBox>(root, "play-action");
            Assert.That(playAction, Is.Not.Null);
            Assert.That(btn, Is.Not.Null);
            TestContext.WriteLine($"play-action W={playAction.Width} H={playAction.Height} X={playAction.X} Y={playAction.Y}");
            TestContext.WriteLine($"play-btn   W={btn.Width} CW={btn.ContentWidth} X={btn.X} Y={btn.Y}");
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            Assert.That(label, Is.Not.Null);
            Assert.That(sub, Is.Not.Null);
            TestContext.WriteLine($"label X={label.X} W={label.Width}");
            TestContext.WriteLine($"sub   X={sub.X}   W={sub.Width}");
            // Cardinal check: the children's X is INSIDE the button, not
            // at the pre-shrink container-cross-size centre (which would
            // place them at X ≈ 800+).
            Assert.That(label.X, Is.LessThan(btn.Width),
                $"label.X must be inside button; got X={label.X}, btn.W={btn.Width}");
            Assert.That(sub.X, Is.LessThan(btn.Width),
                $"sub.X must be inside button; got X={sub.X}, btn.W={btn.Width}");
            Assert.That(playAction.Width, Is.EqualTo(260).Within(2),
                $".play-action width should be ~260 (clamped by play-btn min-width); got {playAction.Width}");
            Assert.That(btn.Width, Is.EqualTo(260).Within(2),
                $".play-btn width should be ~260; got {btn.Width}");
        }

        [Test]
        public void PlayBtnLabel_width_excludes_trailing_letter_spacing() {
            // CSS Text §7.2 — letter-spacing inserts space BETWEEN typographic
            // letter units, NOT after the last one. For "PLAY" (4 chars) at
            // 28px MonoFontMetrics (0.5em advance = 14px each) with
            // letter-spacing: 4px, the inline content width should be
            //   4 * 14 + (4 - 1) * 4 = 56 + 12 = 68
            // NOT 56 + 16 = 72. The button is wider via min-width:260 so the
            // span box becomes the SHRINK-TO-FIT content extent — any
            // trailing LS would inflate it by 4px and become visible as a
            // sliver of background past the last glyph.
            var (root, _, _) = Build(Html, Css, viewportWidth: 1801, viewportHeight: 744);
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null);
            var label = ChildAt(btn, 0);
            Assert.That(label, Is.Not.Null);
            System.Console.WriteLine($"label.W={label.Width}");
            Assert.That(label.Width, Is.EqualTo(68.0).Within(0.5),
                $"label box must be 56 + 3*4 = 68 wide (N-1 letter-spacing gaps); got {label.Width}");
        }

        [Test]
        public void PlayBtn_text_block_centres_inside_simple_absolute_wrapper() {
            // Same as the grid-tab-page test, but with the simpler topology
            // (no .tab-page grid container). Isolates whether the Y-centring
            // bug needs the grid parent or fires for any `position:absolute`
            // wrapper.
            var (root, _, _) = Build(
                "<div style=\"position:relative;width:1801px;height:744px\">"
                  + "<div class=\"play-action\">"
                    + "<button class=\"play-btn\">"
                      + "<span class=\"play-btn-label\">PLAY</span>"
                      + "<span class=\"play-btn-sub\">PRESS TO START</span>"
                    + "</button>"
                  + "</div>"
                + "</div>",
                Css, viewportWidth: 1801, viewportHeight: 744);
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null);
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            double padTop = btn.PaddingTop + btn.BorderTop;
            double padBottom = btn.PaddingBottom + btn.BorderBottom;
            double topGap = label.Y - padTop;
            double bottomGap = (btn.Height - padBottom) - (sub.Y + sub.Height);
            System.Console.WriteLine($"[simple] btn W={btn.Width} CW={btn.ContentWidth} H={btn.Height}");
            System.Console.WriteLine($"[simple] label.Y={label.Y} H={label.Height} | sub.Y={sub.Y} H={sub.Height}");
            System.Console.WriteLine($"[simple] topGap={topGap} bottomGap={bottomGap}");
            Assert.That(topGap, Is.EqualTo(bottomGap).Within(0.5),
                $"simple .play-action wrapper: topGap={topGap}, bottomGap={bottomGap}");
        }

        const string Css = @"
            .play-action {
                position: absolute;
                bottom: 32px;
                left: 50%;
                transform: translateX(-50%);
                z-index: 5;
            }
            .play-btn {
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 2px;
                min-width: 260px;
                height: 76px;
                border-radius: 8px;
            }
            .play-btn-label {
                font-size: 28px;
                font-weight: 900;
                letter-spacing: 4px;
            }
            .play-btn-sub {
                font-size: 11px;
                font-weight: 800;
                text-transform: uppercase;
            }
        ";

        [Test]
        public void PlayAction_shrinks_to_fit_around_play_btn_min_width() {
            // CSS Positioned Layout L3 §10.3.7: shrink-to-fit width =
            // min(max-content, max(min-content, available)). With the
            // button's min-width: 260px, max-content of the parent is
            // at least 260. The 1801-wide containing block makes
            // `available` huge, so fitted ≈ 260.
            var (root, _, _) = Build(Html, Css, viewportWidth: 1801, viewportHeight: 744);
            var playAction = FindFirstByClass<BlockBox>(root, "play-action");
            Assert.That(playAction, Is.Not.Null, "play-action must build");
            TestContext.WriteLine($"play-action W={playAction.Width} H={playAction.Height} X={playAction.X} Y={playAction.Y}");
            Assert.That(playAction.Width, Is.LessThan(800),
                $".play-action must shrink to fit its content; got W={playAction.Width} (should be ~260)");
            Assert.That(playAction.Width, Is.EqualTo(260).Within(20),
                $".play-action width should be ~260 (driven by play-btn min-width); got {playAction.Width}");
        }

        [Test]
        public void PlayBtn_inside_play_action_has_min_width_260() {
            var (root, _, _) = Build(Html, Css, viewportWidth: 1801, viewportHeight: 744);
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null, "play-btn flex container must build");
            TestContext.WriteLine($"play-btn W={btn.Width} CW={btn.ContentWidth} H={btn.Height} CH={btn.ContentHeight} X={btn.X} Y={btn.Y}");
            // min-width clamps to 260; with UA button border-box (padding 2 6)
            // the border-box is 260 and content-box is 248.
            Assert.That(btn.Width, Is.EqualTo(260).Within(2),
                $".play-btn should be ~260 wide; got W={btn.Width}");
            // CSS Sizing L3: `min-width: 260px` is interpreted against the
            // box-sizing CB. Weva currently applies min-width to the
            // content-box even when box-sizing is border-box (UA `button`
            // sets border-box). When that compliance issue is fixed,
            // ContentWidth will tighten to 248 (260 - 12 UA padding).
            Assert.That(btn.ContentWidth, Is.EqualTo(260).Within(2),
                $".play-btn ContentWidth got CW={btn.ContentWidth}");
        }

        [Test]
        public void FlexLayout_uses_post_positioning_container_width_for_line_cross_size() {
            // Direct regression for the diagnostic showing line.CrossSize=1777
            // on a play-btn that is actually ~260 wide. line.CrossSize for a
            // column flex equals max(items' cross size) clamped by container's
            // cross-axis size (ContentWidth). It MUST NOT be the viewport-wide
            // pre-shrink value left over from the first flex pass.
            //
            // We assert via the label/sub child boxes: their X (relative to the
            // button) must place them inside the button's content area, not
            // hundreds of pixels to the right at the pre-shrink centre.
            var (root, _, _) = Build(Html, Css, viewportWidth: 1801, viewportHeight: 744);
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null);
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            Assert.That(label, Is.Not.Null);
            Assert.That(sub, Is.Not.Null);
            TestContext.WriteLine($"label X={label.X} W={label.Width}");
            TestContext.WriteLine($"sub   X={sub.X}   W={sub.Width}");
            // Both child X's are LOCAL to .play-btn (FlexLayout.Place writes
            // padLeft+crossPos into the child Box.X). They must be inside [0, btn.Width].
            Assert.That(label.X, Is.GreaterThanOrEqualTo(0).And.LessThan(btn.Width),
                $"label.X must be inside button; got X={label.X}, btn.W={btn.Width}");
            Assert.That(sub.X, Is.GreaterThanOrEqualTo(0).And.LessThan(btn.Width),
                $"sub.X must be inside button; got X={sub.X}, btn.W={btn.Width}");
            // align-items:center on a column flex: both items share the
            // content-area centre = padLeft + ContentWidth/2.
            double expectedCenter = btn.PaddingLeft + btn.ContentWidth / 2.0;
            double labelCenter = label.X + label.Width / 2.0;
            double subCenter   = sub.X   + sub.Width   / 2.0;
            TestContext.WriteLine($"expected center={expectedCenter}, label center={labelCenter}, sub center={subCenter}");
            Assert.That(labelCenter, Is.EqualTo(expectedCenter).Within(1.0),
                $"label centre must equal content-box centre; got {labelCenter} vs expected {expectedCenter}");
            Assert.That(subCenter, Is.EqualTo(expectedCenter).Within(1.0),
                $"sub centre must equal content-box centre; got {subCenter} vs expected {expectedCenter}");
        }

        static T FindFirstByClass<T>(Box root, string className) where T : Box {
            if (root is T t && root.Element != null) {
                string cls = root.Element.ClassName ?? "";
                if (cls == className || cls.Contains(" " + className) || cls.Contains(className + " ") || cls == className) {
                    return t;
                }
            }
            foreach (var c in root.ChildList) {
                var hit = FindFirstByClass<T>(c, className);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
