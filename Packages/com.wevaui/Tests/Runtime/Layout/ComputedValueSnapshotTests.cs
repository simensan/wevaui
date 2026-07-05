using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // §17 Computed-value snapshots — regression-pinning real-world game-UI
    // layout patterns so future engine changes can't silently regress them.
    //
    // Each test is a self-contained HTML+CSS snippet (no external files) that mirrors
    // a real game-UI topology, then asserts X/Y/Width/Height of key
    // named elements. A 0.5 px tolerance covers floating-point rounding at the last
    // step; any structural shift (wrong track size, missing gap, wrong flex residual,
    // etc.) produces errors larger than 1 px.
    //
    // Patterns covered:
    //   1. Card grid           — display:grid repeat(3,1fr) + aspect-ratio + gap
    //   2. Top bar + body      — flex column, fixed-height header + auto-height body
    //   3. Centered modal      — position:fixed + flex centering + explicit W/H
    //   4. Inventory sidebar   — position:absolute + grid(260px 1fr) + gap
    //   5. HUD named areas     — grid-template-areas spanning topbar across all cols
    //   6. Settings panel      — sidebar + main flex column, margin-top:auto footer
    //   7. Stat tile row       — flex row with fixed-width tiles + gap
    //   8. Ability bar grid    — 2×2 grid inside a fixed-width right column
    //   9. List item height    — flex column items with gap inside fixed container
    //  10. Nested scroll clip  — inner flex child clips to grid-row not content height

    public class ComputedValueSnapshotTests {
        // -----------------------------------------------------------------------
        // Helper: find a BlockBox whose element id attribute matches `id`.
        // -----------------------------------------------------------------------
        static BlockBox FindById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null
                    && bb.Element.GetAttribute("id") == id)
                    return bb;
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // 1. Card grid — repeat(3, 1fr) + aspect-ratio:1 + gap + padding
        //
        // Viewport 800 px. Grid is content-box (default), width:780px so the
        // arithmetic is clean. Padding 20px all sides. Two column gaps of 10px.
        // Grid content area = 780px. Tracks: (780 - 20) / 3 = 253.333 px each.
        // With aspect-ratio:1 card height == card width.
        //
        // Card positions (local to grid border-box: X/Y are relative to grid's own
        // upper-left corner, which is where the parent places it):
        //   c0: X = 20 (padding-left), Y = 20 (padding-top)
        //   c1: X = 20 + 253.333 + 10 = 283.333, Y = 20
        //   c2: X = 283.333 + 253.333 + 10 = 546.667, Y = 20
        //   c3: X = 20, Y = 20 + 253.333 + 10 = 283.333   (second row)
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_card_grid_3x2_tracks_and_positions() {
            const string html =
                "<div id='grid'>" +
                  "<div id='c0'>A</div>" +
                  "<div id='c1'>B</div>" +
                  "<div id='c2'>C</div>" +
                  "<div id='c3'>D</div>" +
                  "<div id='c4'>E</div>" +
                  "<div id='c5'>F</div>" +
                "</div>";
            // width:780px (content-box) so math is: (780 - 2*10) / 3 = 253.333.
            const string css =
                "#grid { display: grid; grid-template-columns: repeat(3, 1fr);" +
                "        gap: 10px; padding: 20px; width: 780px; }" +
                "#grid > div { aspect-ratio: 1; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var c0 = FindById(root, "c0");
            var c1 = FindById(root, "c1");
            var c2 = FindById(root, "c2");
            var c3 = FindById(root, "c3");

            Assert.That(c0, Is.Not.Null, "expected #c0 box");
            Assert.That(c1, Is.Not.Null, "expected #c1 box");

            // Track width: (780 - 2*10) / 3 = 253.333
            const double trackW = (780.0 - 20.0) / 3.0;
            Assert.That(c0.Width, Is.EqualTo(trackW).Within(0.5),
                "c0: card width = 1/3 of content area minus gaps");
            // Aspect-ratio 1: height == width
            Assert.That(c0.Height, Is.EqualTo(c0.Width).Within(0.5),
                "c0: aspect-ratio:1 means height == width");

            // c0 sits at padding-left, padding-top (relative to #grid's box)
            Assert.That(c0.X, Is.EqualTo(20).Within(0.5), "c0: X = padding-left");
            Assert.That(c0.Y, Is.EqualTo(20).Within(0.5), "c0: Y = padding-top");

            // c1 starts after c0 width + gap
            Assert.That(c1.X, Is.EqualTo(c0.X + c0.Width + 10).Within(0.5),
                "c1: X = c0.X + c0.Width + gap");
            Assert.That(c1.Y, Is.EqualTo(20).Within(0.5), "c1: same row as c0");

            // c2 is the third column
            Assert.That(c2.X, Is.EqualTo(c1.X + c1.Width + 10).Within(0.5),
                "c2: X = c1.X + c1.Width + gap");

            // c3 starts the second row
            Assert.That(c3.X, Is.EqualTo(20).Within(0.5), "c3: wraps to first column");
            Assert.That(c3.Y, Is.EqualTo(c0.Y + c0.Height + 10).Within(0.5),
                "c3: Y = row0 height + gap");
        }

        // -----------------------------------------------------------------------
        // 2. Top bar + scrollable body — flex column with fixed header + flex:1 body
        //
        // Viewport 800 × 600. Container is 800 × 600 (width:100%, height:600px).
        // Header: height 60px, flex-shrink:0.
        // Body: flex:1 → 600 - 60 = 540 px tall.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_top_bar_and_body_heights() {
            const string html =
                "<div id='shell'>" +
                  "<div id='topbar'></div>" +
                  "<div id='body'></div>" +
                "</div>";
            const string css =
                "#shell  { display: flex; flex-direction: column;" +
                "          width: 800px; height: 600px; }" +
                "#topbar { height: 60px; flex-shrink: 0; }" +
                "#body   { flex: 1; overflow-y: auto; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var shell  = FindById(root, "shell");
            var topbar = FindById(root, "topbar");
            var body   = FindById(root, "body");

            Assert.That(shell,  Is.Not.Null, "expected #shell");
            Assert.That(topbar, Is.Not.Null, "expected #topbar");
            Assert.That(body,   Is.Not.Null, "expected #body");

            Assert.That(shell.Width,  Is.EqualTo(800).Within(0.5), "shell: 800px wide");
            Assert.That(shell.Height, Is.EqualTo(600).Within(0.5), "shell: 600px tall");

            Assert.That(topbar.Height, Is.EqualTo(60).Within(0.5), "topbar: explicit 60px");
            Assert.That(topbar.Y, Is.EqualTo(0).Within(0.5), "topbar: at top of shell");

            // Body occupies the remaining height
            Assert.That(body.Height, Is.EqualTo(540).Within(0.5),
                "body: flex:1 residual = 600 - 60 = 540");
            Assert.That(body.Y, Is.EqualTo(60).Within(0.5),
                "body: positioned immediately below topbar");
            Assert.That(body.Width, Is.EqualTo(800).Within(0.5),
                "body: stretches to full shell width");
        }

        // -----------------------------------------------------------------------
        // 3. Centered modal — position:fixed inset:0 + flex centering + explicit size
        //
        // Viewport 800 × 600.
        // Overlay covers the full viewport (inset:0 → 800×600).
        // Modal is 400 × 300 centered via align-items:center + justify-content:center.
        // Expected modal left = (800 - 400) / 2 = 200; top = (600 - 300) / 2 = 150.
        //
        // Note: centering of positioned elements via flex uses the positioned child's
        // margin-box. If position:fixed + inset:0 resolves the overlay to viewport
        // dimensions before the flex pass (it does in this engine after the flex-
        // residual fix), the modal should land at exactly 200, 150.
        //
        // If the engine has multi-pass convergence issues (like the real
        // hero-picker fixed overlay) this test will diverge — the [Ignore] is removed
        // once the topology actually works, mirroring HeroPickerScrollReproTests.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_centered_modal_via_fixed_overlay() {
            const string html =
                "<div id='overlay'>" +
                  "<div id='modal'></div>" +
                "</div>";
            const string css =
                "#overlay { position: fixed; inset: 0; display: flex;" +
                "           align-items: center; justify-content: center; }" +
                "#modal   { width: 400px; height: 300px; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var modal = FindById(root, "modal");
            Assert.That(modal, Is.Not.Null, "expected #modal");

            Assert.That(modal.Width,  Is.EqualTo(400).Within(0.5), "modal: explicit 400px wide");
            Assert.That(modal.Height, Is.EqualTo(300).Within(0.5), "modal: explicit 300px tall");

            // Centering: (800 - 400) / 2 = 200; (600 - 300) / 2 = 150
            Assert.That(modal.X, Is.EqualTo(200).Within(0.5),
                "modal: horizontally centered in 800px overlay → X = 200");
            Assert.That(modal.Y, Is.EqualTo(150).Within(0.5),
                "modal: vertically centered in 600px overlay → Y = 150");
        }

        // -----------------------------------------------------------------------
        // 4. Inventory sidebar + grid layout
        //
        // Mirrors Halcyon's inventory screen: position:absolute inset:0 on main,
        // grid of 260px sidebar + 1fr content, gap:16px, padding:24px all sides.
        // Viewport 1200 × 800.
        //   main covers: 1200 × 800.
        //   usable width: 1200 - 2×24 - 16 = 1136 px.
        //   sidebar width = 260 px.
        //   content width = 1136 - 260 = 876 px.
        //   sidebar X = 24 (padding-left).
        //   content X = 24 + 260 + 16 = 300.
        //   sidebar Y = content Y = 24 (padding-top).
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_inventory_sidebar_and_content_column_widths() {
            const string html =
                "<div id='main'>" +
                  "<div id='sidebar'></div>" +
                  "<div id='content'></div>" +
                "</div>";
            const string css =
                "#main    { position: absolute; inset: 0;" +
                "           display: grid;" +
                "           grid-template-columns: 260px 1fr;" +
                "           gap: 16px; padding: 24px; box-sizing: border-box; }" +
                "#sidebar { min-height: 0; }" +
                "#content { min-height: 0; }";

            var (root, _, _) = Build(html, css, viewportWidth: 1200, viewportHeight: 800);

            var sidebar = FindById(root, "sidebar");
            var content = FindById(root, "content");

            Assert.That(sidebar, Is.Not.Null, "expected #sidebar");
            Assert.That(content, Is.Not.Null, "expected #content");

            // Sidebar: 260 px fixed track
            Assert.That(sidebar.Width, Is.EqualTo(260).Within(0.5),
                "sidebar: first grid track = 260px");
            Assert.That(sidebar.X, Is.EqualTo(24).Within(0.5),
                "sidebar: X = padding-left = 24");
            Assert.That(sidebar.Y, Is.EqualTo(24).Within(0.5),
                "sidebar: Y = padding-top = 24");

            // Content column: (1200 - 48 - 16 - 260) = 876 px
            double expectedContentWidth = 1200 - 24 - 24 - 16 - 260;
            Assert.That(content.Width, Is.EqualTo(expectedContentWidth).Within(0.5),
                $"content: 1fr track = {expectedContentWidth}px");
            Assert.That(content.X, Is.EqualTo(24 + 260 + 16).Within(0.5),
                "content: X = padding + sidebar + gap");
        }

        // -----------------------------------------------------------------------
        // 5. HUD named grid areas — topbar spans all 3 columns
        //
        // Mirrors Halcyon HUD: grid with 3 named col tracks, 3 rows,
        // grid-template-areas where topbar spans the full first row.
        //
        // Viewport 1280 × 720. Padding 16px. Gap 12px.
        // Total width = 1280. With padding 2×16 = 32, available = 1248.
        // Cols: 320px + 1fr + 360px. Gaps: 2×12 = 24.
        // 1fr = 1248 - 320 - 360 - 24 = 544 px.
        //
        // topbar spans all 3 cols → width = 320 + 12 + 544 + 12 + 360 = 1248 px.
        // topbar X = 16 (padding-left). topbar Y = 16 (padding-top).
        //
        // character panel: X = 16, Y = 16 + topbarH + 12.
        // topbarH is auto (content-sized); since the box is empty it will be 0.
        // So character Y = 16 + 0 + 12 = 28.
        // character width = 320.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_hud_topbar_spans_all_columns() {
            const string html =
                "<div id='hud'>" +
                  "<div id='topbar'></div>" +
                  "<div id='character'></div>" +
                  "<div id='effects'></div>" +
                  "<div id='actions'></div>" +
                "</div>";
            const string css =
                "#hud { display: grid;" +
                "       grid-template-columns: 320px 1fr 360px;" +
                "       grid-template-rows: 60px 1fr;" +
                "       grid-template-areas:" +
                "         'topbar topbar topbar'" +
                "         'character effects actions';" +
                "       gap: 12px; padding: 16px; box-sizing: border-box;" +
                "       width: 1280px; height: 720px; }" +
                "#topbar    { grid-area: topbar; }" +
                "#character { grid-area: character; }" +
                "#effects   { grid-area: effects; }" +
                "#actions   { grid-area: actions; }";

            var (root, _, _) = Build(html, css, viewportWidth: 1280, viewportHeight: 720);

            var topbar    = FindById(root, "topbar");
            var character = FindById(root, "character");
            var effects   = FindById(root, "effects");
            var actions   = FindById(root, "actions");

            Assert.That(topbar,    Is.Not.Null, "expected #topbar");
            Assert.That(character, Is.Not.Null, "expected #character");
            Assert.That(effects,   Is.Not.Null, "expected #effects");
            Assert.That(actions,   Is.Not.Null, "expected #actions");

            // topbar spans 3 cols: 320 + 12 + 1fr + 12 + 360 inside 1280px - 32px padding = 1248px
            // 1fr = 1248 - 320 - 360 - 24 = 544; topbar.width = 320 + 12 + 544 + 12 + 360 = 1248
            Assert.That(topbar.X, Is.EqualTo(16).Within(0.5), "topbar: X = padding-left");
            Assert.That(topbar.Y, Is.EqualTo(16).Within(0.5), "topbar: Y = padding-top");
            Assert.That(topbar.Width, Is.EqualTo(1248).Within(0.5),
                "topbar: spans all 3 columns = full inner width");
            Assert.That(topbar.Height, Is.EqualTo(60).Within(0.5),
                "topbar: explicit 60px row");

            // character: column 1 (320px wide), row 2 starts at 16 + 60 + 12 = 88
            Assert.That(character.X, Is.EqualTo(16).Within(0.5),
                "character: left column at padding-left");
            Assert.That(character.Y, Is.EqualTo(88).Within(0.5),
                "character: Y = padding-top + topbar-row + gap = 16 + 60 + 12 = 88");
            Assert.That(character.Width, Is.EqualTo(320).Within(0.5),
                "character: fixed 320px column");

            // actions: column 3 (360px), X = 16 + 320 + 12 + 544 + 12 = 904
            double actionsX = 16 + 320 + 12 + 544 + 12;
            Assert.That(actions.X, Is.EqualTo(actionsX).Within(0.5),
                "actions: rightmost column");
            Assert.That(actions.Width, Is.EqualTo(360).Within(0.5),
                "actions: fixed 360px column");

            // effects: column 2 (1fr = 544px), X = 16 + 320 + 12 = 348
            Assert.That(effects.X, Is.EqualTo(348).Within(0.5),
                "effects: middle 1fr column");
            Assert.That(effects.Width, Is.EqualTo(544).Within(0.5),
                "effects: 1fr = 544px");
        }

        // -----------------------------------------------------------------------
        // 6. Settings panel — sidebar (260px fixed) + main panel flex column
        //    with panel-head, auto-scroll body, and margin-top:auto footer
        //
        // Viewport 1024 × 768. Container position:absolute, inset:0.
        // Grid: 260px 1fr. Gap 16px. Padding 24px.
        // Panel inside main column is a flex column:
        //   - panel-head: height 64px
        //   - panel-body: flex:1 (= 768-48-64-44 = 612... but let's use concrete values)
        //   - panel-foot: height 44px, margin-top:auto (moves to bottom)
        //
        // Available height inside panel = 768 - 2×24 = 720 px (box-sizing:border-box).
        // panel-head Y = 0, height = 64.
        // panel-foot height = 44, Y = 720 - 44 = 676.
        // panel-body is between head and foot: Y = 64, height = 720 - 64 - 44 = 612.
        // (margin-top:auto on footer collapses body's flex:1 to remaining after footer.)
        //
        // Actually with flex column and margin-top:auto on footer:
        //   flex items: head (64) + body (flex:1) + foot (44 + margin-top:auto)
        //   "margin-top: auto" eats all remaining space BEFORE the footer.
        //   body has flex:1 which also eats remaining space.
        //   Since both compete, the flex:1 on body wins (flex-grow on body > 0,
        //   margin-top:auto on foot pushes the foot to the end after body).
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_settings_panel_head_body_foot_layout() {
            const string html =
                "<div id='panel'>" +
                  "<div id='head'></div>" +
                  "<div id='body'></div>" +
                  "<div id='foot'></div>" +
                "</div>";
            const string css =
                "#panel { display: flex; flex-direction: column;" +
                "         width: 700px; height: 600px; box-sizing: border-box; }" +
                "#head  { height: 64px; flex-shrink: 0; }" +
                "#body  { flex: 1; overflow-y: auto; min-height: 0; }" +
                "#foot  { height: 44px; flex-shrink: 0; }";

            var (root, _, _) = Build(html, css, viewportWidth: 1024, viewportHeight: 768);

            var panel = FindById(root, "panel");
            var head  = FindById(root, "head");
            var body  = FindById(root, "body");
            var foot  = FindById(root, "foot");

            Assert.That(panel, Is.Not.Null);
            Assert.That(head,  Is.Not.Null);
            Assert.That(body,  Is.Not.Null);
            Assert.That(foot,  Is.Not.Null);

            Assert.That(panel.Height, Is.EqualTo(600).Within(0.5), "panel: explicit 600px");

            Assert.That(head.Height, Is.EqualTo(64).Within(0.5), "head: explicit 64px");
            Assert.That(head.Y,      Is.EqualTo(0).Within(0.5),  "head: at top of panel");

            // body: flex:1 takes the remaining 600 - 64 - 44 = 492 px
            Assert.That(body.Height, Is.EqualTo(492).Within(0.5),
                "body: flex:1 residual = 600 - 64 - 44 = 492");
            Assert.That(body.Y, Is.EqualTo(64).Within(0.5), "body: below head");

            Assert.That(foot.Height, Is.EqualTo(44).Within(0.5), "foot: explicit 44px");
            Assert.That(foot.Y, Is.EqualTo(556).Within(0.5), "foot: 600 - 44 = 556");
        }

        // -----------------------------------------------------------------------
        // 7. Stat tile row — flex row with 5 fixed-width tiles + gap
        //
        // Mirrors Halcyon hero-picker stat grid (5 tiles in a flex row).
        // Container: width:640px, display:flex, gap:8px, padding:12px.
        // Tiles: width:80px each.
        // t0 X = 12, t1 X = 12 + 80 + 8 = 100, t2 X = 188, t3 X = 276, t4 X = 364.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_stat_tile_row_five_tiles_with_gap() {
            const string html =
                "<div id='row'>" +
                  "<div id='t0'></div>" +
                  "<div id='t1'></div>" +
                  "<div id='t2'></div>" +
                  "<div id='t3'></div>" +
                  "<div id='t4'></div>" +
                "</div>";
            const string css =
                "#row { display: flex; flex-direction: row; gap: 8px; padding: 12px;" +
                "       width: 640px; height: 120px; box-sizing: border-box; }" +
                "#row > div { width: 80px; height: 60px; flex-shrink: 0; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var t0 = FindById(root, "t0");
            var t1 = FindById(root, "t1");
            var t2 = FindById(root, "t2");
            var t4 = FindById(root, "t4");

            Assert.That(t0, Is.Not.Null);
            Assert.That(t1, Is.Not.Null);

            Assert.That(t0.Width,  Is.EqualTo(80).Within(0.5), "t0: explicit 80px");
            Assert.That(t0.Height, Is.EqualTo(60).Within(0.5), "t0: explicit 60px");
            Assert.That(t0.X,      Is.EqualTo(12).Within(0.5), "t0: X = padding-left");
            Assert.That(t0.Y,      Is.EqualTo(12).Within(0.5), "t0: Y = padding-top");

            Assert.That(t1.X, Is.EqualTo(12 + 80 + 8).Within(0.5),
                "t1: X = padding + tile + gap = 100");

            Assert.That(t2.X, Is.EqualTo(12 + 2 * (80 + 8)).Within(0.5),
                "t2: X = padding + 2*(tile+gap) = 188");

            Assert.That(t4.X, Is.EqualTo(12 + 4 * (80 + 8)).Within(0.5),
                "t4: X = padding + 4*(tile+gap) = 364");
        }

        // -----------------------------------------------------------------------
        // 8. Ability bar — 2×2 grid inside a fixed-width right column
        //
        // Mirrors Halcyon HUD right-panel ability buttons: grid 1fr 1fr, gap:8px,
        // padding:16px, container 360px wide.
        // Cell width = (360 - 32 - 8) / 2 = 160 px.
        // a0: X=16, Y=16; a1: X=16+160+8=184, Y=16.
        // a2: X=16, Y=16+h+8; a3: X=184, Y=same as a2.
        // Height of a0 is explicit: 80px.
        // So a2 Y = 16 + 80 + 8 = 104.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_ability_bar_2x2_grid_positions() {
            const string html =
                "<div id='bar'>" +
                  "<div id='a0'></div>" +
                  "<div id='a1'></div>" +
                  "<div id='a2'></div>" +
                  "<div id='a3'></div>" +
                "</div>";
            const string css =
                "#bar { display: grid; grid-template-columns: 1fr 1fr;" +
                "       gap: 8px; padding: 16px; box-sizing: border-box;" +
                "       width: 360px; }" +
                "#bar > div { height: 80px; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var a0 = FindById(root, "a0");
            var a1 = FindById(root, "a1");
            var a2 = FindById(root, "a2");
            var a3 = FindById(root, "a3");

            Assert.That(a0, Is.Not.Null);
            Assert.That(a1, Is.Not.Null);
            Assert.That(a2, Is.Not.Null);
            Assert.That(a3, Is.Not.Null);

            // Cell width: (360 - 32 - 8) / 2 = 160 px
            Assert.That(a0.Width, Is.EqualTo(160).Within(0.5), "a0: 1fr cell = 160px");
            Assert.That(a0.Height, Is.EqualTo(80).Within(0.5), "a0: explicit 80px");
            Assert.That(a0.X, Is.EqualTo(16).Within(0.5), "a0: X = padding-left");
            Assert.That(a0.Y, Is.EqualTo(16).Within(0.5), "a0: Y = padding-top");

            Assert.That(a1.X, Is.EqualTo(16 + 160 + 8).Within(0.5),
                "a1: second column = 184");
            Assert.That(a1.Y, Is.EqualTo(16).Within(0.5), "a1: same row as a0");

            // Row 2: Y = 16 + 80 + 8 = 104
            Assert.That(a2.X, Is.EqualTo(16).Within(0.5), "a2: first column, row 2");
            Assert.That(a2.Y, Is.EqualTo(104).Within(0.5),
                "a2: Y = padding-top + row-height + gap = 16 + 80 + 8 = 104");

            Assert.That(a3.X, Is.EqualTo(16 + 160 + 8).Within(0.5),
                "a3: second column, row 2");
            Assert.That(a3.Y, Is.EqualTo(104).Within(0.5), "a3: same row as a2");
        }

        // -----------------------------------------------------------------------
        // 9. List items with gap inside a fixed-height container
        //
        // Mirrors a real stage-select list: flex column, 5 items each 60px tall,
        // gap 8px, padding 12px, total content height = 5×60 + 4×8 = 332 px.
        // Container is 400 × 500 px; overflow-y:auto so it clips but items lay out
        // at their natural Y coordinates.
        // item0 Y=12, item1 Y=80, item2 Y=148, item3 Y=216, item4 Y=284.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_list_items_vertical_layout_with_gap() {
            const string html =
                "<div id='list'>" +
                  "<div id='item0'></div>" +
                  "<div id='item1'></div>" +
                  "<div id='item2'></div>" +
                  "<div id='item3'></div>" +
                  "<div id='item4'></div>" +
                "</div>";
            const string css =
                "#list { display: flex; flex-direction: column; gap: 8px; padding: 12px;" +
                "        width: 400px; height: 500px; overflow-y: auto;" +
                "        box-sizing: border-box; }" +
                "#list > div { height: 60px; flex-shrink: 0; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var item0 = FindById(root, "item0");
            var item1 = FindById(root, "item1");
            var item4 = FindById(root, "item4");

            Assert.That(item0, Is.Not.Null);
            Assert.That(item1, Is.Not.Null);

            // item0 at padding-top
            Assert.That(item0.Y, Is.EqualTo(12).Within(0.5), "item0: Y = padding-top = 12");
            Assert.That(item0.Height, Is.EqualTo(60).Within(0.5), "item0: height = 60px");

            // item1: 12 + 60 + 8 = 80
            Assert.That(item1.Y, Is.EqualTo(80).Within(0.5),
                "item1: Y = padding + item0 + gap = 12+60+8 = 80");

            // item4: 12 + 4*(60+8) = 12 + 272 = 284
            Assert.That(item4.Y, Is.EqualTo(284).Within(0.5),
                "item4: Y = padding + 4*(item+gap) = 12+4*68 = 284");

            // Items are full content width: 400 - 2*12 = 376
            Assert.That(item0.Width, Is.EqualTo(376).Within(0.5),
                "items: stretch to content width = 400 - 24 = 376");
        }

        // -----------------------------------------------------------------------
        // 10. Nested scroll-container clips to grid row, not content height
        //
        // This is the canonical hero-picker topology (simplified):
        // a flex column with a fixed-height header + a grid body with flex:1.
        // The grid body has two columns; the right column is a flex scroll container
        // with overflow-y:auto + min-height:0. The scroll container must clip to
        // the grid-row height, NOT inflate to its children's total height.
        //
        // Outer: flex column 800×600. Header: 48px. Body: flex:1 → 552px.
        // Body grid: 240px + 1fr. Left col: 240px. Right col: 800-240=560px.
        // detail height must == 552 (clipped), not the sum of 4×150=600 px fillers.
        // -----------------------------------------------------------------------
        [Test]
        public void Snapshot_scroll_container_clips_to_grid_row_not_content_height() {
            const string html =
                "<div id='shell'>" +
                  "<div id='header'></div>" +
                  "<div id='body'>" +
                    "<div id='left'></div>" +
                    "<div id='detail'>" +
                      "<div id='f0'></div>" +
                      "<div id='f1'></div>" +
                      "<div id='f2'></div>" +
                      "<div id='f3'></div>" +
                    "</div>" +
                  "</div>" +
                "</div>";
            const string css =
                "#shell  { display: flex; flex-direction: column;" +
                "          width: 800px; height: 600px; }" +
                "#header { height: 48px; flex-shrink: 0; }" +
                "#body   { flex: 1; display: grid;" +
                "          grid-template-columns: 240px 1fr; overflow: hidden; }" +
                "#left   { /* list col */ }" +
                "#detail { display: flex; flex-direction: column;" +
                "          overflow-y: auto; min-height: 0; max-height: 100%; }" +
                "#detail > div { height: 150px; flex-shrink: 0; }";

            var (root, _, _) = Build(html, css, viewportWidth: 1024, viewportHeight: 768);

            var header = FindById(root, "header");
            var body   = FindById(root, "body");
            var left   = FindById(root, "left");
            var detail = FindById(root, "detail");

            Assert.That(header, Is.Not.Null);
            Assert.That(body,   Is.Not.Null);
            Assert.That(detail, Is.Not.Null);

            // header is always 48 px
            Assert.That(header.Height, Is.EqualTo(48).Within(0.5),
                "header: explicit 48px");

            // body: flex:1 residual = 600 - 48 = 552
            Assert.That(body.Height, Is.EqualTo(552).Within(0.5),
                "body: flex:1 residual = 600 - 48 = 552");

            // detail must clip to body row (552), not inflate to 4×150=600 content
            Assert.That(detail.Height, Is.EqualTo(552).Within(1.0),
                "detail: overflow-y:auto + min-height:0 must clip to grid-row=552, not content=600");
            Assert.That(detail.Height, Is.LessThanOrEqualTo(552 + 1.0),
                "detail: must not exceed grid-row height");

            // left column: 240px track
            Assert.That(left.Width, Is.EqualTo(240).Within(0.5),
                "left: first grid column = 240px");

            // detail (right): 800 - 240 = 560 px (1fr)
            Assert.That(detail.Width, Is.EqualTo(560).Within(0.5),
                "detail: 1fr = 800 - 240 = 560px");
        }
    }
}
