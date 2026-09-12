#if WEVA_URP
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Weva.Testing.Goldens;

namespace Weva.Tests.Rendering.URP {
    // GPU golden tests for the 10 game-UI snippet (29-38). These run through the
    // REAL BatchedURPRenderBackend + Hidden/Weva/Quad shader path in Unity
    // Play mode, catching shader / URP / RenderGraph regressions that the software
    // rasterizer (SoftwareRasterizer-backed GoldenSuiteTests) cannot see.
    //
    // The SAME snippet files are used as the software goldens so layout source-of-
    // truth is shared. Baselines are stored separately (Baselines.GPU/) and must be
    // generated inside Unity:
    //
    //   1. Open the Unity project that includes this package (e.g. a game project).
    //   2. Open the Test Runner (Window > General > Test Runner).
    //   3. Switch to Play Mode tests.
    //   4. Run all tests under GameUIGpuGoldenTests.
    //   5. On first run each test auto-seeds Baselines.GPU/<name>.png and reports
    //      INCONCLUSIVE — it compared the render against nothing. Inspect each
    //      PNG visually, then commit it.
    //   6. Future runs diff against the committed baselines over the real GPU path.
    //
    // No GPU baseline is committed yet, so all ten are inconclusive until someone
    // does steps 1-5. They must be seeded from the editor: run headless, the
    // render happens before TextCore bakes a glyph atlas and the image has no text
    // in it, so GpuGoldenAssert refuses to write one there.
    //
    // To regenerate: set env var WEVA_REGENERATE_GOLDENS=1 before launching Unity.
    //
    // Pre-existing failures NOT caused by this file:
    //   - LetterSpacingLineBreakingTests.Letter_spacing_applies_at_inter_fragment_seams
    //   - SizingConstraintsTests.Absolute_shrink_to_fit_honors_flex_child_min_width
    //   - The 3 fixture-IO tests (missing quests.html / match3-endgame.html).
    public class GameUIGpuGoldenTests {
        // Resolves the directory of *this* source file at compile time. The Snippets/
        // and Baselines.GPU/ directories live as siblings of this file's parent (the
        // Goldens/ folder two directories up from URP/).
        static string GoldensDir([CallerFilePath] string callerPath = null) {
            // This test file is at:
            //   Tests/Runtime/Rendering/URP/GameUIGpuGoldenTests.cs
            // The Goldens directory is at:
            //   Tests/Runtime/Goldens/
            string urpDir = Path.GetDirectoryName(callerPath) ?? ".";
            return Path.GetFullPath(Path.Combine(urpDir, "..", "..", "Goldens"));
        }

        static string SnippetPath(string name) =>
            Path.Combine(GoldensDir(), "Snippets", name);

        static string BaselinePath(string name) =>
            Path.Combine(GoldensDir(), "Baselines.GPU", name);

        // One place to turn "no baseline, so nothing was compared" into an
        // inconclusive result. It is not a pass — these ten reported green on
        // every clean checkout for as long as they have existed, while
        // comparing each render against nothing at all — and it is not a
        // failure either, because the renderer is not what is missing.
        static void Golden(string name, int width, int height, double tolerance = 0.02) {
            try {
                GpuGoldenAssert.Match(
                    SnippetPath(name + ".html"), BaselinePath(name + ".png"),
                    width: width, height: height, tolerance: tolerance);
            } catch (GoldenNotVerifiedException e) {
                Assert.Inconclusive(e.Message);
            }
        }

        // ── Game-UI GPU goldens (mirrors GoldenSuiteTests Golden_29 … Golden_38) ──

        // 29: 3x2 card grid — repeat(3,1fr) + aspect-ratio:1 + gap + rounded borders.
        [Test]
        public void Gpu_Golden_29_card_grid_3x2() {
            Golden("29-card-grid-3x2", width: 800, height: 600, tolerance: 0.02);
        }

        // 30: Flex column shell with 60px dark top bar + flex:1 content body.
        [Test]
        public void Gpu_Golden_30_top_bar_and_body() {
            Golden("30-top-bar-and-body", width: 800, height: 600, tolerance: 0.02);
        }

        // 31: Full-viewport fixed overlay + centered 400x300 modal with rounded corners.
        [Test]
        public void Gpu_Golden_31_centered_modal() {
            Golden("31-centered-modal", width: 800, height: 600, tolerance: 0.02);
        }

        // 32: 2-column grid — 260px dark sidebar (icon stack) + light content area.
        [Test]
        public void Gpu_Golden_32_sidebar_content() {
            Golden("32-sidebar-content", width: 800, height: 600, tolerance: 0.02);
        }

        // 33: HUD grid-template-areas with topbar spanning all 3 columns.
        [Test]
        public void Gpu_Golden_33_hud_grid_areas() {
            Golden("33-hud-grid-areas", width: 1200, height: 900, tolerance: 0.02);
        }

        // 34: Settings panel — flex column with 64px header, flex:1 body, 44px footer.
        [Test]
        public void Gpu_Golden_34_settings_panel() {
            Golden("34-settings-panel", width: 800, height: 600, tolerance: 0.02);
        }

        // 35: Stat tile row — 5 fixed-width 90px tiles with numeric value + label.
        [Test]
        public void Gpu_Golden_35_stat_tile_row() {
            Golden("35-stat-tile-row", width: 800, height: 200, tolerance: 0.02);
        }

        // 36: 2x2 ability card grid inside a 360px-wide container.
        [Test]
        public void Gpu_Golden_36_ability_bar_2x2() {
            Golden("36-ability-bar-2x2", width: 400, height: 300, tolerance: 0.02);
        }

        // 37: Scroll container (400x500 overflow:auto) with 5 list items + 8px gap.
        [Test]
        public void Gpu_Golden_37_list_with_gap() {
            Golden("37-list-with-gap", width: 400, height: 500, tolerance: 0.02);
        }

        // 38: Hero-picker scroll-clip — grid-template-rows:auto 552px auto; middle
        //     row overflow:hidden with scrollable detail column (must clip to 552px).
        [Test]
        public void Gpu_Golden_38_hero_picker_scroll_clip() {
            Golden("38-hero-picker-scroll-clip", width: 800, height: 700, tolerance: 0.02);
        }
    }
}
#endif
