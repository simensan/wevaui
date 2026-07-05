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
    //   5. On first run each test auto-seeds Baselines.GPU/<name>.png — inspect
    //      each PNG visually, then commit it.
    //   6. Future runs diff against the committed baselines over the real GPU path.
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

        // ── Game-UI GPU goldens (mirrors GoldenSuiteTests Golden_29 … Golden_38) ──

        // 29: 3x2 card grid — repeat(3,1fr) + aspect-ratio:1 + gap + rounded borders.
        [Test]
        public void Gpu_Golden_29_card_grid_3x2() {
            GpuGoldenAssert.Match(
                SnippetPath("29-card-grid-3x2.html"),
                BaselinePath("29-card-grid-3x2.png"),
                width: 800, height: 600, tolerance: 0.02);
        }

        // 30: Flex column shell with 60px dark top bar + flex:1 content body.
        [Test]
        public void Gpu_Golden_30_top_bar_and_body() {
            GpuGoldenAssert.Match(
                SnippetPath("30-top-bar-and-body.html"),
                BaselinePath("30-top-bar-and-body.png"),
                width: 800, height: 600, tolerance: 0.02);
        }

        // 31: Full-viewport fixed overlay + centered 400x300 modal with rounded corners.
        [Test]
        public void Gpu_Golden_31_centered_modal() {
            GpuGoldenAssert.Match(
                SnippetPath("31-centered-modal.html"),
                BaselinePath("31-centered-modal.png"),
                width: 800, height: 600, tolerance: 0.02);
        }

        // 32: 2-column grid — 260px dark sidebar (icon stack) + light content area.
        [Test]
        public void Gpu_Golden_32_sidebar_content() {
            GpuGoldenAssert.Match(
                SnippetPath("32-sidebar-content.html"),
                BaselinePath("32-sidebar-content.png"),
                width: 800, height: 600, tolerance: 0.02);
        }

        // 33: HUD grid-template-areas with topbar spanning all 3 columns.
        [Test]
        public void Gpu_Golden_33_hud_grid_areas() {
            GpuGoldenAssert.Match(
                SnippetPath("33-hud-grid-areas.html"),
                BaselinePath("33-hud-grid-areas.png"),
                width: 1200, height: 900, tolerance: 0.02);
        }

        // 34: Settings panel — flex column with 64px header, flex:1 body, 44px footer.
        [Test]
        public void Gpu_Golden_34_settings_panel() {
            GpuGoldenAssert.Match(
                SnippetPath("34-settings-panel.html"),
                BaselinePath("34-settings-panel.png"),
                width: 800, height: 600, tolerance: 0.02);
        }

        // 35: Stat tile row — 5 fixed-width 90px tiles with numeric value + label.
        [Test]
        public void Gpu_Golden_35_stat_tile_row() {
            GpuGoldenAssert.Match(
                SnippetPath("35-stat-tile-row.html"),
                BaselinePath("35-stat-tile-row.png"),
                width: 800, height: 200, tolerance: 0.02);
        }

        // 36: 2x2 ability card grid inside a 360px-wide container.
        [Test]
        public void Gpu_Golden_36_ability_bar_2x2() {
            GpuGoldenAssert.Match(
                SnippetPath("36-ability-bar-2x2.html"),
                BaselinePath("36-ability-bar-2x2.png"),
                width: 400, height: 300, tolerance: 0.02);
        }

        // 37: Scroll container (400x500 overflow:auto) with 5 list items + 8px gap.
        [Test]
        public void Gpu_Golden_37_list_with_gap() {
            GpuGoldenAssert.Match(
                SnippetPath("37-list-with-gap.html"),
                BaselinePath("37-list-with-gap.png"),
                width: 400, height: 500, tolerance: 0.02);
        }

        // 38: Hero-picker scroll-clip — grid-template-rows:auto 552px auto; middle
        //     row overflow:hidden with scrollable detail column (must clip to 552px).
        [Test]
        public void Gpu_Golden_38_hero_picker_scroll_clip() {
            GpuGoldenAssert.Match(
                SnippetPath("38-hero-picker-scroll-clip.html"),
                BaselinePath("38-hero-picker-scroll-clip.png"),
                width: 800, height: 700, tolerance: 0.02);
        }
    }
}
#endif
