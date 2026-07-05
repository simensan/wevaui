using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Weva.Testing.Goldens;

namespace Weva.Tests.Goldens {
    public class GoldenSuiteTests {
        // Resolves the directory of *this* source file at compile time so tests can
        // find the committed snippet/baseline assets regardless of where the test
        // runner cwd is. Works under NUnit on macOS/Linux/Windows alike.
        static string GoldensDir([CallerFilePath] string callerPath = null) {
            return Path.GetDirectoryName(callerPath);
        }

        static string SnippetPath(string name) {
            return Path.Combine(GoldensDir(), "Snippets", name);
        }

        static string BaselinePath(string name) {
            return Path.Combine(GoldensDir(), "Baselines", name);
        }

        [Test] public void Golden_01_empty()                  => GoldenAssert.Match(SnippetPath("01-empty.html"),                  BaselinePath("01-empty.png"));
        [Test] public void Golden_02_single_block()           => GoldenAssert.Match(SnippetPath("02-single-block.html"),           BaselinePath("02-single-block.png"));
        [Test] public void Golden_03_block_margin_padding()   => GoldenAssert.Match(SnippetPath("03-block-margin-padding.html"),   BaselinePath("03-block-margin-padding.png"));
        [Test] public void Golden_04_text_paragraph()         => GoldenAssert.Match(SnippetPath("04-text-paragraph.html"),         BaselinePath("04-text-paragraph.png"));
        [Test] public void Golden_05_flex_row()               => GoldenAssert.Match(SnippetPath("05-flex-row.html"),               BaselinePath("05-flex-row.png"));
        [Test] public void Golden_06_flex_column()            => GoldenAssert.Match(SnippetPath("06-flex-column.html"),            BaselinePath("06-flex-column.png"));
        [Test] public void Golden_07_grid_3x3()               => GoldenAssert.Match(SnippetPath("07-grid-3x3.html"),               BaselinePath("07-grid-3x3.png"));
        [Test] public void Golden_08_positioning_absolute()   => GoldenAssert.Match(SnippetPath("08-positioning-absolute.html"),   BaselinePath("08-positioning-absolute.png"));
        [Test] public void Golden_09_borders_radii()          => GoldenAssert.Match(SnippetPath("09-borders-radii.html"),          BaselinePath("09-borders-radii.png"));
        [Test] public void Golden_10_gradient_background()    => GoldenAssert.Match(SnippetPath("10-gradient-background.html"),    BaselinePath("10-gradient-background.png"));
        [Test] public void Golden_11_shadow()                 => GoldenAssert.Match(SnippetPath("11-shadow.html"),                 BaselinePath("11-shadow.png"));
        [Test] public void Golden_12_the_demo()               => GoldenAssert.Match(SnippetPath("12-the-demo.html"),               BaselinePath("12-the-demo.png"));
        [Test] public void Golden_13_inset_shadow()           => GoldenAssert.Match(SnippetPath("13-inset-shadow.html"),           BaselinePath("13-inset-shadow.png"));
        [Test] public void Golden_14_dashed_border()          => GoldenAssert.Match(SnippetPath("14-dashed-border.html"),          BaselinePath("14-dashed-border.png"));
        [Test] public void Golden_15_dotted_border()          => GoldenAssert.Match(SnippetPath("15-dotted-border.html"),          BaselinePath("15-dotted-border.png"));
        [Test] public void Golden_16_drop_shadow_filter()     => GoldenAssert.Match(SnippetPath("16-drop-shadow-filter.html"),     BaselinePath("16-drop-shadow-filter.png"));
        [Test] public void Golden_17_multi_layer_background() => GoldenAssert.Match(SnippetPath("17-multi-layer-background.html"), BaselinePath("17-multi-layer-background.png"));
        [Test] public void Golden_18_margin_collapse()        => GoldenAssert.Match(SnippetPath("18-margin-collapse.html"),        BaselinePath("18-margin-collapse.png"));
        [Test] public void Golden_19_inline_block_row()       => GoldenAssert.Match(SnippetPath("19-inline-block-row.html"),       BaselinePath("19-inline-block-row.png"));
        [Test] public void Golden_20_flex_baseline()          => GoldenAssert.Match(SnippetPath("20-flex-baseline.html"),          BaselinePath("20-flex-baseline.png"));
        [Test] public void Golden_21_word_break()             => GoldenAssert.Match(SnippetPath("21-word-break.html"),             BaselinePath("21-word-break.png"));
        [Test] public void Golden_22_flex_with_absolute()     => GoldenAssert.Match(SnippetPath("22-flex-with-absolute.html"),     BaselinePath("22-flex-with-absolute.png"));
        [Test] public void Golden_23_inline_splitting()       => GoldenAssert.Match(SnippetPath("23-inline-splitting.html"),       BaselinePath("23-inline-splitting.png"));
        [Test] public void Golden_24_text_overflow_ellipsis() => GoldenAssert.Match(SnippetPath("24-text-overflow-ellipsis.html"), BaselinePath("24-text-overflow-ellipsis.png"));
        [Test] public void Golden_25_backdrop_modal_dialog()  => GoldenAssert.Match(SnippetPath("25-backdrop-modal-dialog.html"), BaselinePath("25-backdrop-modal-dialog.png"));
        [Test] public void Golden_26_text_shadow()            => GoldenAssert.Match(SnippetPath("26-text-shadow.html"),           BaselinePath("26-text-shadow.png"));
        // 27-text-shadow-blur exercises Path A SDF dilation across blur
        // radii 0/2/6/12 px. The baseline image is NOT committed yet — the
        // first run (or `WEVA_REGENERATE_GOLDENS=1`) seeds it. See the
        // .css for the rationale on each row.
        [Test] public void Golden_27_text_shadow_blur()       => GoldenAssert.Match(SnippetPath("27-text-shadow-blur.html"),      BaselinePath("27-text-shadow-blur.png"));
        // 28-floats exercises CSS 2.1 §9.5 float layout — left/right floats
        // with prose wrapping, two stacked left floats, and a `clear: left`
        // block. The baseline image is NOT committed yet; the first run
        // (or `WEVA_REGENERATE_GOLDENS=1`) seeds it.
        [Test] public void Golden_28_floats()                 => GoldenAssert.Match(SnippetPath("28-floats.html"),                BaselinePath("28-floats.png"));

        // --- Game-UI visual regression goldens (patterns parallel ComputedValueSnapshotTests) ---

        // 29: 3x2 card grid — repeat(3,1fr) + aspect-ratio:1 + gap + rounded borders.
        // Viewport 800x600. Baseline auto-seeded on first run.
        [Test] public void Golden_29_card_grid_3x2()          => GoldenAssert.Match(SnippetPath("29-card-grid-3x2.html"),         BaselinePath("29-card-grid-3x2.png"),         width: 800, height: 600, tolerance: 0.02);

        // 30: Flex column shell with 60px dark top bar + flex:1 content body.
        // Viewport 800x600. Baseline auto-seeded on first run.
        [Test] public void Golden_30_top_bar_and_body()       => GoldenAssert.Match(SnippetPath("30-top-bar-and-body.html"),       BaselinePath("30-top-bar-and-body.png"),       width: 800, height: 600, tolerance: 0.02);

        // 31: Full-viewport fixed overlay + centered 400x300 modal with rounded corners.
        // Viewport 800x600. Baseline auto-seeded on first run.
        [Test] public void Golden_31_centered_modal()         => GoldenAssert.Match(SnippetPath("31-centered-modal.html"),         BaselinePath("31-centered-modal.png"),         width: 800, height: 600, tolerance: 0.02);

        // 32: 2-column grid — 260px dark sidebar (icon stack) + light content area.
        // Viewport 800x600. Baseline auto-seeded on first run.
        [Test] public void Golden_32_sidebar_content()        => GoldenAssert.Match(SnippetPath("32-sidebar-content.html"),        BaselinePath("32-sidebar-content.png"),        width: 800, height: 600, tolerance: 0.02);

        // 33: HUD grid-template-areas with topbar spanning all 3 columns.
        // Viewport 1200x900. Baseline auto-seeded on first run.
        [Test] public void Golden_33_hud_grid_areas()         => GoldenAssert.Match(SnippetPath("33-hud-grid-areas.html"),         BaselinePath("33-hud-grid-areas.png"),         width: 1200, height: 900, tolerance: 0.02);

        // 34: Settings panel — flex column with 64px header, flex:1 body, 44px footer.
        // Viewport 800x600. Baseline auto-seeded on first run.
        [Test] public void Golden_34_settings_panel()         => GoldenAssert.Match(SnippetPath("34-settings-panel.html"),         BaselinePath("34-settings-panel.png"),         width: 800, height: 600, tolerance: 0.02);

        // 35: Stat tile row — 5 fixed-width 90px tiles with numeric value + label.
        // Viewport 800x200. Baseline auto-seeded on first run.
        [Test] public void Golden_35_stat_tile_row()          => GoldenAssert.Match(SnippetPath("35-stat-tile-row.html"),          BaselinePath("35-stat-tile-row.png"),          width: 800, height: 200, tolerance: 0.02);

        // 36: 2x2 ability card grid inside a 360px-wide container.
        // Viewport 400x300. Baseline auto-seeded on first run.
        [Test] public void Golden_36_ability_bar_2x2()        => GoldenAssert.Match(SnippetPath("36-ability-bar-2x2.html"),        BaselinePath("36-ability-bar-2x2.png"),        width: 400, height: 300, tolerance: 0.02);

        // 37: Scroll container (400x500 overflow:auto) with 5 list items + 8px gap.
        // Viewport 400x500. Baseline auto-seeded on first run.
        [Test] public void Golden_37_list_with_gap()          => GoldenAssert.Match(SnippetPath("37-list-with-gap.html"),          BaselinePath("37-list-with-gap.png"),          width: 400, height: 500, tolerance: 0.02);

        // 38: Hero-picker scroll-clip — grid-template-rows:auto 552px auto; middle
        // row overflow:hidden with scrollable detail column (must clip to 552px).
        // Viewport 800x700. Baseline auto-seeded on first run.
        [Test] public void Golden_38_hero_picker_scroll_clip() => GoldenAssert.Match(SnippetPath("38-hero-picker-scroll-clip.html"), BaselinePath("38-hero-picker-scroll-clip.png"), width: 800, height: 700, tolerance: 0.02);
    }
}
