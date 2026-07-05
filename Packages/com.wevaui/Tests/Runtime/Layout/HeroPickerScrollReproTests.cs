using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Repro for a real game's hero-picker scroll bug. The actual chain (see
    // a production main-menu.css §1686+):
    //
    //   .hero-picker          (flex column, height: 100%)
    //     .hero-picker-header (flex row)
    //     .hero-picker-body   (display: GRID, grid-template-columns: 240px 1fr,
    //                          flex: 1, overflow: hidden)
    //       .hero-picker-list   (flex column, overflow-y: auto)
    //       .hero-picker-detail (flex column, overflow-y: auto,
    //                            min-height: 0, max-height: 100%)
    //
    // User report: .hero-picker-detail's scroll thumb reaches the bottom but
    // content (skills + the margin-top:auto confirm button) is cut off below
    // the visible viewport. This pins what the engine ACTUALLY does for this
    // topology so we can compare against the spec-correct behaviour:
    //
    //   .hero-picker-body grid row size = container.Height - header
    //   .hero-picker-detail grid-cell-stretched to that row size
    //   .hero-picker-detail.Height should clip to grid-cell, content scrolls
    public class HeroPickerScrollReproTests {
        const string Css = @"
            .picker {
                width: 800px;
                height: 600px;
                display: flex;
                flex-direction: column;
            }
            .picker-header {
                height: 48px;
                flex-shrink: 0;
            }
            .picker-body {
                flex: 1;
                display: grid;
                grid-template-columns: 240px 1fr;
                overflow: hidden;
            }
            .picker-list {
                display: flex;
                flex-direction: column;
                overflow-y: auto;
            }
            .picker-detail {
                display: flex;
                flex-direction: column;
                overflow-y: auto;
                min-height: 0;
                max-height: 100%;
                padding: 20px;
            }
            .filler { height: 200px; flex-shrink: 0; }
        ";

        static BlockBox FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null
                    && bb.Element.GetAttribute("class") is string c && c.Contains(cls)) {
                    return bb;
                }
            }
            return null;
        }

        [Test]
        public void Hero_picker_detail_height_clips_to_grid_row_when_content_overflows() {
            // .picker-body grid row size = 600 (.picker.Height) - 48 (.picker-header)
            // = 552 px. With four 200-px fillers (= 800 px) inside .picker-detail,
            // it MUST clip to 552 so its overflow-y:auto scrolls. If it grows to
            // 800 (or anything > 552), the scroll mechanism's content-extent
            // calculation has nothing to clip against and the user can't scroll
            // to see the rest of the content.
            var (root, _, _) = Build(
                "<div class=\"picker\">" +
                "  <div class=\"picker-header\"></div>" +
                "  <div class=\"picker-body\">" +
                "    <div class=\"picker-list\"></div>" +
                "    <div class=\"picker-detail\">" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "    </div>" +
                "  </div>" +
                "</div>",
                Css, viewportWidth: 1200);
            var detail = FindByClass(root, "picker-detail");
            Assert.That(detail, Is.Not.Null, "expected .picker-detail box");
            // Diagnostic — log everything so the failure tells us WHICH height
            // the engine picked, not just "not 552".
            Assert.That(detail.Height, Is.EqualTo(552.0).Within(1.0),
                "hero-picker-detail should clip to the grid row size (552) so its overflow-y:auto can scroll the 800 px of content");
        }

        // Closer-to-real repro: mirrors an actual production hero-picker
        // markup (nested flex sections, stat grid, skill cards with their
        // own flex chains, margin-top:auto confirm button at the bottom).
        // The real .hero-picker-detail content has been observed at >800 px
        // — same shape as the simpler repro but with all the inner
        // formatting context that the first test omits.
        [Test]
        public void Hero_picker_detail_with_realistic_inner_layout_clips_to_grid_row() {
            const string realCss = @"
                .picker { width: 1200px; height: 700px; display: flex; flex-direction: column; }
                .picker-header { height: 56px; flex-shrink: 0; }
                .picker-body {
                    flex: 1;
                    display: grid;
                    grid-template-columns: 240px 1fr;
                    overflow: hidden;
                }
                .picker-list {
                    display: flex;
                    flex-direction: column;
                    overflow-y: auto;
                }
                .picker-detail {
                    display: flex;
                    flex-direction: column;
                    gap: 16px;
                    padding: 20px 28px;
                    overflow-y: auto;
                    min-height: 0;
                    max-height: 100%;
                }
                .heading { height: 32px; flex-shrink: 0; }
                .desc { height: 40px; flex-shrink: 0; }
                .section { display: flex; flex-direction: column; gap: 8px; }
                .stat-grid {
                    display: grid;
                    grid-template-columns: repeat(5, 1fr);
                    gap: 6px;
                }
                .stat-tile { height: 60px; }
                .skills {
                    display: flex;
                    flex-direction: column;
                    gap: 8px;
                }
                .skill { display: flex; gap: 12px; height: 110px; flex-shrink: 0; }
                .confirm {
                    margin-top: auto;
                    flex-shrink: 0;
                    height: 44px;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"picker\">" +
                "  <div class=\"picker-header\"></div>" +
                "  <div class=\"picker-body\">" +
                "    <div class=\"picker-list\"></div>" +
                "    <div class=\"picker-detail\">" +
                "      <div class=\"heading\"></div>" +
                "      <div class=\"desc\"></div>" +
                "      <div class=\"section\">" +
                "        <div class=\"stat-grid\">" +
                "          <div class=\"stat-tile\"></div>" +
                "          <div class=\"stat-tile\"></div>" +
                "          <div class=\"stat-tile\"></div>" +
                "          <div class=\"stat-tile\"></div>" +
                "          <div class=\"stat-tile\"></div>" +
                "        </div>" +
                "      </div>" +
                "      <div class=\"section\">" +
                "        <div class=\"skills\">" +
                "          <div class=\"skill\"></div>" +
                "          <div class=\"skill\"></div>" +
                "          <div class=\"skill\"></div>" +
                "          <div class=\"skill\"></div>" +
                "          <div class=\"skill\"></div>" +
                "        </div>" +
                "      </div>" +
                "      <div class=\"confirm\"></div>" +
                "    </div>" +
                "  </div>" +
                "</div>",
                realCss, viewportWidth: 1400);
            var detail = FindByClass(root, "picker-detail");
            // .picker-body grid row = 700 - 56 = 644.
            Assert.That(detail.Height, Is.EqualTo(644.0).Within(2.0),
                "realistic hero-picker-detail should clip to grid row size (644), not inflate to content sum");
        }

        // KNOWN LIMITATION — pinned but failing.
        //
        // Exact mirror of a real game's outer chain: position:fixed +
        // inset:0 modal-overlay, .hero-picker with width/height:100%, the
        // grid body with flex:1. This is the topology actually exercised
        // in a real `.hero-picker-overlay` and remains broken
        // because of a multi-pass convergence gap:
        //
        //   1. BlockLayout / FlexLayout / GridLayout all run BEFORE
        //      PositioningPass.
        //   2. `.modal-overlay { inset: 0 }` resolves to viewport-bounds
        //      only at PositioningPass time.
        //   3. So `.hero-picker { height: 100% }` reads its containing
        //      block as INDEFINITE during the pre-positioning passes and
        //      falls back to content-stack height (1240 px from
        //      detail's descendants).
        //   4. .hero-picker-body inherits the 1240 px height during all
        //      flex/grid passes. Grid stamps its row at 1240, my fix
        //      restores Height to 1240 (still correct given inputs).
        //   5. PositioningPass corrects `.modal-overlay` and propagates
        //      `.hero-picker.Height = 504` → body = 448 px. But no
        //      subsequent flex/grid pass re-runs to re-size detail's row.
        //   6. Final state: body.Height = 448, detail.Height = 1240 —
        //      scroll thumb collapses, content overflow unreachable.
        //
        // Proper fix needs an extra layout iteration after
        // PositioningPass for grid/flex containers whose ancestor sizes
        // changed during positioning, OR percent-height resolution moved
        // earlier in the pipeline. Both are non-trivial orchestration
        // changes. Marked Ignore'd so the test stays as a regression
        // hook — un-ignore when the orchestration lands.
        [Test]
        public void Hero_picker_detail_clips_when_outer_is_fixed_overlay_with_percent_height() {
            const string realChain = @"
                .modal-overlay {
                    position: fixed;
                    inset: 0;
                    display: flex;
                    align-items: stretch;
                    justify-content: stretch;
                    padding: 48px 64px;
                }
                .hero-picker {
                    width: 100%;
                    height: 100%;
                    display: flex;
                    flex-direction: column;
                    overflow: hidden;
                }
                .hero-picker-header {
                    height: 56px;
                    flex-shrink: 0;
                }
                .hero-picker-body {
                    flex: 1;
                    display: grid;
                    grid-template-columns: 240px 1fr;
                    overflow: hidden;
                }
                .hero-picker-list {
                    display: flex;
                    flex-direction: column;
                    overflow-y: auto;
                }
                .hero-picker-detail {
                    display: flex;
                    flex-direction: column;
                    gap: 16px;
                    padding: 20px 28px;
                    overflow-y: auto;
                    min-height: 0;
                    max-height: 100%;
                }
                .filler { height: 200px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"modal-overlay\">" +
                "  <div class=\"hero-picker\">" +
                "    <div class=\"hero-picker-header\"></div>" +
                "    <div class=\"hero-picker-body\">" +
                "      <div class=\"hero-picker-list\"></div>" +
                "      <div class=\"hero-picker-detail\">" +
                "        <div class=\"filler\"></div>" +
                "        <div class=\"filler\"></div>" +
                "        <div class=\"filler\"></div>" +
                "        <div class=\"filler\"></div>" +
                "        <div class=\"filler\"></div>" +
                "        <div class=\"filler\"></div>" +
                "      </div>" +
                "    </div>" +
                "  </div>" +
                "</div>",
                realChain, viewportWidth: 1280);
            var detail = FindByClass(root, "hero-picker-detail");
            Assert.That(detail, Is.Not.Null);
            var body = FindByClass(root, "hero-picker-body");
            Assert.That(body, Is.Not.Null);
            // Whatever the engine computes for body.Height (it's the flex:1
            // residual under .hero-picker minus the 56px header), detail MUST
            // clip to that — not exceed it.
            Assert.That(detail.Height, Is.LessThanOrEqualTo(body.Height + 1.0),
                $"detail.Height={detail.Height} must clip to grid row size body.Height={body.Height}");
        }

        [Test]
        public void Hero_picker_detail_with_overflow_visible_still_grows_to_content() {
            // Regression guard: the fix MUST be scoped to scroll containers.
            // A grid-item that doesn't have overflow-y:auto/scroll/hidden must
            // keep the old behaviour where the auto row sizes to its content
            // sum — otherwise settings-page-style grids where each item grows
            // to its measured content height silently collapse to zero.
            var css = Css.Replace("overflow-y: auto", "overflow-y: visible");
            var (root, _, _) = Build(
                "<div class=\"picker\">" +
                "  <div class=\"picker-header\"></div>" +
                "  <div class=\"picker-body\">" +
                "    <div class=\"picker-list\"></div>" +
                "    <div class=\"picker-detail\">" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "    </div>" +
                "  </div>" +
                "</div>",
                css, viewportWidth: 1200);
            var detail = FindByClass(root, "picker-detail");
            Assert.That(detail.Height, Is.GreaterThanOrEqualTo(800.0),
                "overflow-y:visible should keep the content-driven auto row sizing (≥800), not clip");
        }
    }
}
