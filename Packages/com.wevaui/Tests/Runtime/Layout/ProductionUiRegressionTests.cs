using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout {
    // Regression repros for two real game-UI visual bugs surfaced after the
    // flex/sizing commit storm of 2026-05-30.
    //
    // Regression 1 — topbar-right: Essence pill and Exit button overlap.
    //   The 14px gap between .wallet-strip and .exit-btn must be honoured.
    //
    // Regression 2 — hero picker body: flex:1 child of a column-flex whose
    //   parent has an explicit height must receive the residual height
    //   (parent − sibling), not collapse to zero or content-height.
    //
    // Regression 3 — full topbar topology (1920×1080 viewport).
    //   The topbar uses display:grid with grid-template-columns:1fr auto 1fr.
    //   The 1fr columns must be symmetric, topbar-right must stay within the
    //   viewport, essence pill must not overlap exit button, and the auto
    //   column (tabs) must not overflow its grid cell.
    public class ProductionUiRegressionTests {

        // ---------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------

        // True when `cls` is one of the space-separated tokens in the class attribute.
        static bool HasExactClass(string classAttr, string cls) {
            if (string.IsNullOrEmpty(classAttr)) return false;
            foreach (var token in classAttr.Split(' '))
                if (token == cls) return true;
            return false;
        }

        static BlockBox FindByExactClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string c = bb.Element.GetAttribute("class");
                    if (c != null && HasExactClass(c, cls)) return bb;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Regression 1 — CSS
        // ---------------------------------------------------------------

        const string TopbarCss = @"
            /* UA overrides: span and button must be block-level for
               inline-block display to give them box coordinates. */
            span   { display: inline-block; }
            button { display: inline-block; }

            .topbar-right  { display: flex; align-items: center; gap: 14px; width: 400px; }
            .wallet-strip  { display: flex; gap: 8px; }
            .wallet-pill   {
                min-width: 96px; padding: 7px 10px; text-align: center;
                background: rgba(251, 191, 36, 0.09);
                border: 1px solid rgba(251, 191, 36, 0.3);
                border-radius: 4px; color: #fbbf24; font-size: 12px; font-weight: 800;
            }
            .exit-btn {
                height: 30px; padding: 0 10px; background: transparent;
                border: 1px solid #475569; border-radius: 4px; color: #94a3b8;
                font-size: 12px; font-weight: 700;
            }
        ";

        const string TopbarHtml =
            "<div class=\"topbar-right\">" +
            "  <div class=\"wallet-strip\">" +
            "    <span class=\"wallet-pill\">Coins 19</span>" +
            "    <span class=\"wallet-pill essence\">Essence 4</span>" +
            "  </div>" +
            "  <button class=\"exit-btn\">Exit</button>" +
            "</div>";

        static BlockBox FindExitBtn(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "button") return bb;
            }
            return null;
        }

        static BlockBox FindNthWalletPill(Box root, int n) {
            int seen = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string cls = bb.Element.GetAttribute("class");
                    if (cls != null && HasExactClass(cls, "wallet-pill")) {
                        seen++;
                        if (seen == n) return bb;
                    }
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Regression 1 — Tests
        // ---------------------------------------------------------------

        [Test]
        public void Topbar_right_essence_pill_does_not_overlap_exit_button() {
            // Spec-correct: 14px gap between .wallet-strip and .exit-btn means
            //   essencePill.absRight + 14 == exitBtn.absLeft (within 1px rounding).
            // Minimum guard: no overlap at all.
            var (root, _, _) = Build(TopbarHtml, TopbarCss, viewportWidth: 800);

            var essence = FindNthWalletPill(root, 2); // second wallet-pill = Essence
            var exit    = FindExitBtn(root);

            Assert.That(essence, Is.Not.Null, "essence pill box not found");
            Assert.That(exit,    Is.Not.Null, "exit button box not found");

            var (essX, _) = AbsoluteOriginOf(essence);
            var (exitX, _) = AbsoluteOriginOf(exit);
            double essenceRight = essX + essence.Width;

            // No overlap.
            Assert.That(exitX, Is.GreaterThan(essenceRight),
                $"exit button (left={exitX:F1}) overlaps essence pill (right={essenceRight:F1})");

            // Gap is at least 12px (14px nominal, 2px rounding slack).
            double actualGap = exitX - essenceRight;
            Assert.That(actualGap, Is.GreaterThanOrEqualTo(12.0),
                $"gap between essence pill and exit button is only {actualGap:F1}px; expected >=12 (spec: 14)");
        }

        [Test]
        public void Topbar_right_exit_button_gap_is_roughly_14px() {
            // Positive assertion: the gap is the specified 14px (within 2px tolerance).
            var (root, _, _) = Build(TopbarHtml, TopbarCss, viewportWidth: 800);

            var essence = FindNthWalletPill(root, 2);
            var exit    = FindExitBtn(root);

            Assert.That(essence, Is.Not.Null, "essence pill box not found");
            Assert.That(exit,    Is.Not.Null, "exit button box not found");

            var (essX, _) = AbsoluteOriginOf(essence);
            var (exitX, _) = AbsoluteOriginOf(exit);

            double gap = exitX - (essX + essence.Width);
            Assert.That(gap, Is.EqualTo(14.0).Within(2.0),
                $"expected ~14px gap between essence pill and exit button; got {gap:F1}px");
        }

        [Test]
        public void Topbar_right_wallet_strip_pills_have_8px_gap_between_them() {
            // The two pills inside .wallet-strip must be separated by 8px (gap: 8px).
            var (root, _, _) = Build(TopbarHtml, TopbarCss, viewportWidth: 800);

            var coins   = FindNthWalletPill(root, 1);
            var essence = FindNthWalletPill(root, 2);

            Assert.That(coins,   Is.Not.Null, "coins pill not found");
            Assert.That(essence, Is.Not.Null, "essence pill not found");

            var (coinsX, _)   = AbsoluteOriginOf(coins);
            var (essenceX, _) = AbsoluteOriginOf(essence);

            double gap = essenceX - (coinsX + coins.Width);
            Assert.That(gap, Is.EqualTo(8.0).Within(2.0),
                $"expected 8px gap between coins and essence pills; got {gap:F1}px");
        }

        // ---------------------------------------------------------------
        // Regression 2 — CSS + HTML
        // ---------------------------------------------------------------

        const string HeroPickerCss = @"
            /* UA overrides for elements not in the built-in UA sheet. */
            header  { display: block; }
            ul      { display: block; margin: 0; padding: 0; }
            button  { display: block; }
            img     { display: inline-block; }
            section { display: block; }

            .hero-picker-body {
                flex: 1;
                display: grid;
                grid-template-columns: 240px 1fr;
                gap: 0;
                overflow: hidden;
            }
            .hero-picker-list {
                display: flex;
                flex-direction: column;
                gap: 4px;
                padding: 12px;
                list-style: none;
                margin: 0;
                overflow-y: auto;
                border-right: 1px solid #475569;
            }
            .hero-picker-card {
                display: flex;
                align-items: center;
                gap: 12px;
                padding: 10px 12px;
                background: transparent;
                border: 2px solid transparent;
                border-radius: 6px;
            }
            .hero-picker-card-icon {
                width: 48px;
                height: 48px;
                border-radius: 4px;
                object-fit: cover;
                flex-shrink: 0;
                background: #1c2435;
            }
        ";

        const string HeroPickerHtml =
            "<div style=\"position: fixed; top: 80px; left: 80px; width: 800px; height: 500px;" +
            "            display: flex; flex-direction: column;\">" +
            "  <header style=\"height: 60px; background: #2a3654;\">Header</header>" +
            "  <div class=\"hero-picker-body\">" +
            "    <ul class=\"hero-picker-list\">" +
            "      <button class=\"hero-picker-card\"><img class=\"hero-picker-card-icon\"/>A</button>" +
            "      <button class=\"hero-picker-card\"><img class=\"hero-picker-card-icon\"/>B</button>" +
            "      <button class=\"hero-picker-card\"><img class=\"hero-picker-card-icon\"/>C</button>" +
            "      <button class=\"hero-picker-card\"><img class=\"hero-picker-card-icon\"/>D</button>" +
            "    </ul>" +
            "    <section style=\"background: #1c2435;\">detail pane</section>" +
            "  </div>" +
            "</div>";

        // ---------------------------------------------------------------
        // Regression 2 — Tests
        // ---------------------------------------------------------------

        [Test]
        public void Hero_picker_body_resolves_height_from_parent_flex_minus_header() {
            // Layout chain:
            //   outer div: position:fixed, width:800, height:500, flex-direction:column
            //   header:    height:60px (block-level, flex-shrink defaults to 1 but
            //              explicit height pins it to 60)
            //   .hero-picker-body: flex:1 -> receives 500 - 60 = 440px
            //
            // If the engine collapses the flex:1 residual to 0 when the parent
            // is position:fixed (the known regression), body.Height will be wrong.
            var (root, _, _) = Build(HeroPickerHtml, HeroPickerCss,
                viewportWidth: 1200, viewportHeight: 700);

            var body = FindByExactClass(root, "hero-picker-body");
            Assert.That(body, Is.Not.Null, ".hero-picker-body box not found");

            // Allow ±1px for border/rounding.
            Assert.That(body.Height, Is.EqualTo(440.0).Within(1.0),
                $".hero-picker-body.Height={body.Height:F1}; expected 440 (500 - 60 header)");
        }

        [Test]
        public void Hero_picker_list_contains_all_four_cards_visible() {
            // With .hero-picker-body at 440px, .hero-picker-list is the left grid
            // column (240px wide, 440px tall). Four cards x ~68px each = ~280px
            // total — all four fit without scrolling, so each card's Y (relative
            // to the scroll container) is less than the container height.
            var (root, _, _) = Build(HeroPickerHtml, HeroPickerCss,
                viewportWidth: 1200, viewportHeight: 700);

            var list = FindByExactClass(root, "hero-picker-list");
            Assert.That(list, Is.Not.Null, ".hero-picker-list box not found");

            int cardCount = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string cls = bb.Element.GetAttribute("class");
                    // Match exactly "hero-picker-card", not "hero-picker-card-icon".
                    if (cls != null && HasExactClass(cls, "hero-picker-card")) {
                        cardCount++;
                        Assert.That(bb.Y, Is.LessThan(list.Height),
                            $"card #{cardCount} Y={bb.Y:F1} >= list.Height={list.Height:F1}; card outside visible scroll area");
                    }
                }
            }

            Assert.That(cardCount, Is.EqualTo(4),
                $"expected 4 hero-picker-card boxes; found {cardCount}");
        }

        [Test]
        public void Hero_picker_body_is_not_zero_height() {
            // Smoke check: .hero-picker-body must never collapse to zero when
            // its parent flex container has an explicit height.
            var (root, _, _) = Build(HeroPickerHtml, HeroPickerCss,
                viewportWidth: 1200, viewportHeight: 700);

            var body = FindByExactClass(root, "hero-picker-body");
            Assert.That(body, Is.Not.Null, ".hero-picker-body box not found");

            Assert.That(body.Height, Is.GreaterThan(10.0),
                $".hero-picker-body.Height={body.Height:F1}; collapsed to near-zero");
        }

        [Test]
        public void Hero_picker_cards_have_distinct_increasing_Y_positions() {
            // Regression pin for the second real game-UI bug: only ONE hero icon
            // visible in the picker. The symptom was that GridLayout.
            // ApplyItemAlignment's destructive RelayoutContentAt probe
            // overwrote the flex column's children-Y back to 0 without
            // re-running flex layout, so all 4 cards stacked at Y=0 and only
            // the top one was visible.
            //
            // Spec: with `.hero-picker-list { display:flex; flex-direction:column;
            // gap:4px; padding:12px }`, four sequential cards must land at
            // Y values 12, 12+H+4, 12+2(H+4), 12+3(H+4) — strictly increasing,
            // not all clustered at 0.
            var (root, _, _) = Build(HeroPickerHtml, HeroPickerCss,
                viewportWidth: 1200, viewportHeight: 700);

            // Collect cards in DOM order.
            var cardYs = new System.Collections.Generic.List<double>();
            var cardHs = new System.Collections.Generic.List<double>();
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string cls = bb.Element.GetAttribute("class");
                    if (cls != null && HasExactClass(cls, "hero-picker-card")) {
                        cardYs.Add(bb.Y);
                        cardHs.Add(bb.Height);
                    }
                }
            }
            Assert.That(cardYs.Count, Is.EqualTo(4), "expected 4 cards in DOM");

            // Pin: each card's Y must be greater than the previous card's
            // Y by AT LEAST the previous card's height. Anything less
            // means cards are overlapping or stacked-at-zero (the bug).
            for (int i = 1; i < cardYs.Count; i++) {
                double minExpected = cardYs[i - 1] + cardHs[i - 1];
                Assert.That(cardYs[i], Is.GreaterThanOrEqualTo(minExpected - 0.5),
                    $"card[{i}].Y={cardYs[i]:F1} must be >= card[{i-1}].Y({cardYs[i-1]:F1}) + card[{i-1}].Height({cardHs[i-1]:F1}); cards are overlapping (regression where flex children re-stacked at Y=0)");
            }

            // Sanity: at least one card should be appreciably below the first.
            // If all four are within a few pixels of each other, the flex
            // column layout has collapsed.
            Assert.That(cardYs[3], Is.GreaterThan(cardYs[0] + 30.0),
                $"card[3].Y={cardYs[3]:F1} must be appreciably below card[0].Y={cardYs[0]:F1}; cards have stacked at one Y offset");
        }

        // ---------------------------------------------------------------
        // Regression 3 — Full topbar topology (faithful real game-UI repro)
        // ---------------------------------------------------------------
        //
        // The topbar is `position:fixed; display:grid; grid-template-columns: 1fr auto 1fr`
        // with padding 0 20px and gap 24px at 1920×1080 and narrower viewports.
        //
        // Grid math (box-sizing: content-box default):
        //   topbar content width = viewport - 40 (padding) px
        //   available after gaps = content - 24*2 = content - 48px
        //   tabs (auto column) = content-sized by children
        //   remaining / 2 = each 1fr column
        //
        // topbar-right is `justify-self: end` so it packs to the right edge
        // of its 1fr grid cell.  The wallet-strip + 14px gap + exit-btn must
        // all fit within the right-padding boundary at x = viewport - 20.

        // Full CSS mirroring a real main-menu.css.  Buttons get
        // display:flex here because the UA sheet in tests treats them
        // as inline; we promote them so the layout engine gives them boxes.
        const string FullTopbarCss = @"
            /* Promote elements that need box dimensions in the headless runner. */
            button { display: flex; align-items: center; }
            img    { display: block; }

            .hud { position: relative; width: 100%; height: 100%; background: transparent; }

            .topbar {
                position: fixed; top: 0; left: 0; right: 0;
                height: 58px; padding: 0 20px;
                display: grid; grid-template-columns: 1fr auto 1fr;
                align-items: center; gap: 24px;
                background: rgba(10,14,22,0.88);
                border-bottom: 1px solid #475569;
                z-index: 10;
            }

            .topbar-left  { display: flex; align-items: center; gap: 14px; }
            .topbar-right { display: flex; align-items: center; gap: 14px; justify-self: end; }

            .hero-chip {
                display: flex; align-items: center; gap: 12px;
                min-width: 110px; max-width: 168px;
                padding: 4px 14px 4px 4px;
                background: #1c2435; border: 1px solid #475569;
                border-radius: 6px; color: #fff; overflow: hidden;
            }
            .hero-chip-portrait { width: 40px; height: 40px; border-radius: 4px; flex-shrink: 0; }
            .hero-chip-name { min-width: 0; font-size: 14px; font-weight: 700;
                white-space: nowrap; overflow: hidden; }

            .player-chip { display: flex; align-items: center; gap: 8px; padding-left: 16px; }
            .player-chip-name { max-width: 130px; font-size: 14px; font-weight: 600; white-space: nowrap; overflow: hidden; }
            .player-chip-edit { height: 30px; padding: 0 10px; }

            .tabs { display: flex; align-items: center; gap: 2px; padding: 3px;
                background: #1c2435; border-radius: 6px; }
            .tab  { height: 30px; padding: 0 12px; background: transparent; border: 0;
                font-size: 13px; font-weight: 700; border-radius: 4px; }
            .tab.active { background: #2a3654; }

            .wallet-strip { display: flex; gap: 8px; }
            .wallet-pill  {
                min-width: 96px; padding: 7px 10px; text-align: center;
                border: 1px solid rgba(251,191,36,0.3);
                border-radius: 4px; font-size: 12px; font-weight: 800;
            }
            .wallet-pill.essence { border-color: rgba(167,139,250,0.32); }
            .exit-btn { height: 30px; padding: 0 10px;
                border: 1px solid #475569; border-radius: 4px;
                font-size: 12px; font-weight: 700; }
        ";

        const string FullTopbarHtml =
            "<body style=\"width: 1920px; height: 1080px;\">" +
            "  <main class=\"hud\">" +
            "    <header class=\"topbar\">" +
            "      <div class=\"topbar-left\">" +
            "        <button class=\"hero-chip\">" +
            "          <img class=\"hero-chip-portrait\" />" +
            "          <div class=\"hero-chip-name\">Selina</div>" +
            "        </button>" +
            "        <div class=\"player-chip\">" +
            "          <span class=\"player-chip-name\">Player</span>" +
            "          <button class=\"player-chip-edit\">edit</button>" +
            "        </div>" +
            "      </div>" +
            "      <nav class=\"tabs\">" +
            "        <button class=\"tab active\">Play</button>" +
            "        <button class=\"tab\">Mastery</button>" +
            "        <button class=\"tab\">Challenges</button>" +
            "        <button class=\"tab\">Upgrades</button>" +
            "      </nav>" +
            "      <div class=\"topbar-right\">" +
            "        <div class=\"wallet-strip\">" +
            "          <span class=\"wallet-pill\">Coins 19</span>" +
            "          <span class=\"wallet-pill essence\">Essence 4</span>" +
            "        </div>" +
            "        <button class=\"exit-btn\">Exit</button>" +
            "      </div>" +
            "    </header>" +
            "  </main>" +
            "</body>";

        // Helper to find a named box in the full-topology tree.
        static BlockBox FindFullTopbarBox(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string c = bb.Element.GetAttribute("class");
                    if (c != null && HasExactClass(c, cls)) return bb;
                }
            }
            return null;
        }

        // Returns the second wallet-pill (Essence) box.
        static BlockBox FindEssencePill(Box root) {
            int seen = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string cls = bb.Element.GetAttribute("class");
                    if (cls != null && HasExactClass(cls, "wallet-pill")) {
                        seen++;
                        if (seen == 2) return bb;
                    }
                }
            }
            return null;
        }

        // Returns the exit-btn button box.
        static BlockBox FindExitBtnFull(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string cls = bb.Element.GetAttribute("class");
                    if (cls != null && HasExactClass(cls, "exit-btn")) return bb;
                }
            }
            return null;
        }

        [Test]
        public void Full_topbar_essence_pill_does_not_overlap_exit_button_1920() {
            // At 1920×1080: essence pill right + 14px gap → exit-btn left.
            // No overlap is the hard constraint; gap ≥ 12px is the soft one.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var essence = FindEssencePill(root);
            var exit    = FindExitBtnFull(root);

            Assert.That(essence, Is.Not.Null, "essence pill not found in full topbar (1920)");
            Assert.That(exit,    Is.Not.Null, "exit-btn not found in full topbar (1920)");

            var (essX, _) = AbsoluteOriginOf(essence);
            var (exitX, _) = AbsoluteOriginOf(exit);
            double essenceRight = essX + essence.Width;

            Assert.That(exitX, Is.GreaterThan(essenceRight),
                $"[1920] exit-btn (left={exitX:F1}) overlaps essence pill (right={essenceRight:F1})");

            double actualGap = exitX - essenceRight;
            Assert.That(actualGap, Is.GreaterThanOrEqualTo(12.0),
                $"[1920] gap={actualGap:F1}px < 12px (spec: 14px)");
        }

        [Test]
        public void Full_topbar_exit_button_within_viewport_1920() {
            // exit-btn right edge must not exceed the 1920px viewport width.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var exit = FindExitBtnFull(root);
            Assert.That(exit, Is.Not.Null, "exit-btn not found in full topbar (1920)");

            var (exitX, _) = AbsoluteOriginOf(exit);
            double exitRight = exitX + exit.Width;
            Assert.That(exitRight, Is.LessThanOrEqualTo(1920.5),
                $"[1920] exit-btn right={exitRight:F1} overflows viewport");
        }

        [Test]
        public void Full_topbar_grid_1fr_columns_are_symmetric_1920() {
            // grid-template-columns: 1fr auto 1fr — left and right 1fr columns
            // must have equal width (within 1px rounding).
            // Measured by comparing cell spans via absolute positions.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var tabs  = FindFullTopbarBox(root, "tabs");
            Assert.That(tabs, Is.Not.Null, "tabs nav not found");

            var (tabsX, _) = AbsoluteOriginOf(tabs);

            // Cell 1 span: from topbar padding-left (20px) to tabs start minus gap (24px).
            double cell1End   = tabsX - 24;
            double cell1Width = cell1End - 20;

            // Cell 3 span: from tabs end plus gap to topbar right edge minus padding.
            double cell3Start = tabsX + tabs.Width + 24;
            double cell3Width = (1920 - 20) - cell3Start;

            Assert.That(cell1Width, Is.EqualTo(cell3Width).Within(2.0),
                $"[1920] 1fr col1={cell1Width:F1}px vs col3={cell3Width:F1}px; expected equal");
        }

        [Test]
        public void Full_topbar_tabs_auto_column_does_not_overflow_1920() {
            // The tabs nav lives in the auto column. Its width must not exceed
            // the available grid width minus padding and gaps.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var topbar = FindFullTopbarBox(root, "topbar");
            var tabs   = FindFullTopbarBox(root, "tabs");

            Assert.That(topbar, Is.Not.Null, "topbar header not found");
            Assert.That(tabs,   Is.Not.Null, "tabs nav not found");

            double available = topbar.Width - 40 - 2 * 24;
            Assert.That(tabs.Width, Is.LessThanOrEqualTo(available + 2),
                $"[1920] tabs.Width={tabs.Width:F1} > available={available:F1} (overflow)");
        }

        [Test]
        public void Full_topbar_right_edge_respects_padding_right_1920() {
            // topbar-right is in col 3. Its right edge (absolute) must not
            // exceed 1920 - 20px (the topbar's right padding boundary).
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var right = FindFullTopbarBox(root, "topbar-right");
            Assert.That(right, Is.Not.Null, "topbar-right not found");

            var (rightX, _) = AbsoluteOriginOf(right);
            double rightEdge = rightX + right.Width;
            Assert.That(rightEdge, Is.LessThanOrEqualTo(1920 - 20 + 0.5),
                $"[1920] topbar-right right edge={rightEdge:F1} > 1900 (violates padding-right)");
        }

        // -------- Narrower viewport variants to catch width-dependent bugs --------

        [Test]
        public void Full_topbar_essence_pill_does_not_overlap_exit_button_1280() {
            // Regression: at 1280×720 the exit-btn landed left of the essence
            // pill due to incorrect flex ordering inside topbar-right.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1280, viewportHeight: 720);

            var essence = FindEssencePill(root);
            var exit    = FindExitBtnFull(root);

            Assert.That(essence, Is.Not.Null, "essence pill not found (1280)");
            Assert.That(exit,    Is.Not.Null, "exit-btn not found (1280)");

            var (essX, _) = AbsoluteOriginOf(essence);
            var (exitX, _) = AbsoluteOriginOf(exit);

            Assert.That(exitX, Is.GreaterThan(essX + essence.Width),
                $"[1280] exit-btn overlaps essence pill (exitX={exitX:F1}, essRight={essX + essence.Width:F1})");
        }

        [Test]
        public void Full_topbar_right_edge_respects_padding_right_1280() {
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1280, viewportHeight: 720);

            var right = FindFullTopbarBox(root, "topbar-right");
            Assert.That(right, Is.Not.Null, "topbar-right not found (1280)");

            var (rightX, _) = AbsoluteOriginOf(right);
            double rightEdge = rightX + right.Width;
            Assert.That(rightEdge, Is.LessThanOrEqualTo(1280 - 20 + 0.5),
                $"[1280] topbar-right right edge={rightEdge:F1} > 1260");
        }

        [Test]
        public void Full_topbar_exit_button_within_viewport_1024() {
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1024, viewportHeight: 768);

            var exit = FindExitBtnFull(root);
            Assert.That(exit, Is.Not.Null, "exit-btn not found (1024)");

            var (exitX, _) = AbsoluteOriginOf(exit);
            double exitRight = exitX + exit.Width;
            Assert.That(exitRight, Is.LessThanOrEqualTo(1024.5),
                $"[1024] exit-btn right={exitRight:F1} overflows viewport");
        }

        // ---------------------------------------------------------------
        // Issue 1 — ApplyItemAlignment non-flex probe: both horizontal paddings/borders
        //
        // The non-flex/grid branch of GridLayout.ApplyItemAlignment previously
        // computed mc = MaxContentWidth(box) + PaddingRight + BorderRight (missing
        // the left-side frame). This under-counted the border-box width, causing
        // justify-self:end / center items to be placed too far right.
        //
        // MonoFontMetrics default: CharWidthEm=0.5, font-size 16px → 8px/char.
        // "content" = 7 chars × 8px = 56px content width.
        // ---------------------------------------------------------------

        [Test]
        public void Grid_item_justify_self_end_includes_both_horizontal_paddings() {
            // Regression pin for the asymmetric + box.PaddingRight + box.BorderRight bug.
            // Cell width = 400px (auto track in 400px-wide grid).
            // Item: padding-left:20px; padding-right:20px; border:2px solid → frame = 44px.
            // Content "content" = 7 chars × 8px = 56px.
            // Correct border-box width = 56 + 44 = 100px.
            // With justify-self:end: item.X = cellW - item.Width = 400 - 100 = 300px.
            // Before fix: mc was 56+20+2=78 (missing 22px left frame) → item.X=322 (2px off).
            const string css = ".grid { display: grid; width: 400px; }";
            const string html =
                "<div class=\"grid\">" +
                "<div class=\"item\" style=\"padding-left:20px; padding-right:20px; border:2px solid; justify-self:end\">content</div>" +
                "</div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var item = FindByExactClass(root, "item");
            Assert.That(item, Is.Not.Null, "item box not found");

            // Border-box width must include BOTH paddings + BOTH borders.
            double expectedWidth = 56 + 44; // 100px
            Assert.That(item.Width, Is.EqualTo(expectedWidth).Within(1.0),
                $"item.Width={item.Width:F1} should be content(56) + full frame(44) = 100px; " +
                "missing PaddingLeft/BorderLeft would give ~78px");

            // X position: cellW(400) - item.Width(100) = 300. Missing left frame → 322.
            Assert.That(item.X, Is.EqualTo(300.0).Within(1.0),
                $"item.X={item.X:F1} should be 300px (justify-self:end); missing left frame gives wrong position");
        }

        [Test]
        public void Grid_item_justify_self_end_asymmetric_padding_uses_both_sides() {
            // Asymmetric padding case: padding-left:50px; padding-right:5px.
            // Total left-heavy frame = 50+5 = 55px. Content = 56px. Width = 111px.
            // justify-self:end → X = 400 - 111 = 289px.
            const string css = ".grid { display: grid; width: 400px; }";
            const string html =
                "<div class=\"grid\">" +
                "<div class=\"item\" style=\"padding-left:50px; padding-right:5px; justify-self:end\">content</div>" +
                "</div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var item = FindByExactClass(root, "item");
            Assert.That(item, Is.Not.Null, "item box not found (asymmetric padding)");

            // Width includes both paddings.
            double expectedWidth = 56 + 55; // 111px
            Assert.That(item.Width, Is.EqualTo(expectedWidth).Within(1.0),
                $"item.Width={item.Width:F1}; expected content(56)+frame(55)=111; " +
                "missing PaddingLeft(50) would give 56+5=61");

            Assert.That(item.X, Is.EqualTo(289.0).Within(1.0),
                $"item.X={item.X:F1}; expected 400-111=289 for justify-self:end");
        }

        [Test]
        public void Grid_item_justify_self_center_with_padding_positions_symmetrically() {
            // Same geometry as test 1 but justify-self:center.
            // item.Width = 100px. offsetX = (400 - 100) / 2 = 150px.
            const string css = ".grid { display: grid; width: 400px; }";
            const string html =
                "<div class=\"grid\">" +
                "<div class=\"item\" style=\"padding-left:20px; padding-right:20px; border:2px solid; justify-self:center\">content</div>" +
                "</div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var item = FindByExactClass(root, "item");
            Assert.That(item, Is.Not.Null, "item box not found (center)");

            double expectedWidth = 56 + 44; // 100px
            Assert.That(item.Width, Is.EqualTo(expectedWidth).Within(1.0),
                $"item.Width={item.Width:F1}; expected 100");

            // Center: (400 - 100) / 2 = 150.
            Assert.That(item.X, Is.EqualTo(150.0).Within(1.0),
                $"item.X={item.X:F1}; expected 150 for justify-self:center with width=100 in 400px cell");
        }

        // ---------------------------------------------------------------
        // Issue 2 — Non-destructive max-content probe for flex/grid container
        //   items in FlexLayout (row flex base-size and cross-axis shrink probes).
        //
        // The old code called RelayoutContentAt(item, 1e6) before the isRigid2
        // check, then restored via RelayoutContentAt(item, snapshotWidth) in
        // finally. If the item is a flex container AND its final assigned width
        // equals snapshotWidth (no ReflowIfShrunk), the flex children remain in
        // block-stacked positions — same class as b2f0d02.
        //
        // Fix: for flex/grid items, check isRigid2 BEFORE calling
        // RelayoutContentAt and take the MaxContentWidth-only path.
        // ---------------------------------------------------------------

        [Test]
        public void Flex_row_base_size_probe_flex_item_children_not_block_stacked() {
            // A row-flex container whose only item is a nested row-flex (inner-flex).
            // inner-flex has two block-promoted spans. The inner-flex children must
            // be laid out in a row (X increasing left-to-right), NOT stacked
            // vertically (block-layout order Y=0 for both).
            //
            // Without the fix, the probe calls RelayoutContentAt(inner-flex, 1e6)
            // then restores to snapshotWidth via block layout, leaving the spans
            // at Y positions from block flow. The outer flex doesn't re-run inner-flex,
            // so the spans stay stacked.
            const string css = @"
                span { display: inline-block; }
                .outer { display: flex; width: 300px; }
                .inner-flex { display: flex; flex-direction: row; gap: 20px; }
            ";
            const string html =
                "<div class=\"outer\">" +
                "<div class=\"inner-flex\">" +
                "<span class=\"a\">Hello</span><span class=\"b\">World</span>" +
                "</div>" +
                "</div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var a = FindByExactClass(root, "a");
            var b = FindByExactClass(root, "b");
            Assert.That(a, Is.Not.Null, "span.a not found");
            Assert.That(b, Is.Not.Null, "span.b not found");

            // In a row flex, b.X must be greater than a.X + a.Width (laid out to
            // the right of a). If they're block-stacked, both have X=0 or nearly 0.
            var (aX, _) = AbsoluteOriginOf(a);
            var (bX, _) = AbsoluteOriginOf(b);
            double aRight = aX + a.Width;
            Assert.That(bX, Is.GreaterThan(aRight - 0.5),
                $"span.b.X={bX:F1} must be right of span.a right edge ({aRight:F1}); " +
                "block-stacked: both at X=0 (destructive probe bug)");
        }

        [Test]
        public void Column_flex_cross_axis_probe_flex_item_not_block_stacked() {
            // A column-flex container (cross-axis = width) whose item is a nested
            // row-flex with align-self:flex-start (non-stretch). The column-flex
            // cross-axis probe should not stomp the inner-flex's children.
            //
            // The inner-flex contains two spans. They must be laid out in a row
            // (different X positions), not stacked (same X=0 from block layout).
            const string css = @"
                span { display: inline-block; }
                .outer { display: flex; flex-direction: column; width: 400px; height: 200px; }
                .inner-flex { display: flex; flex-direction: row; gap: 16px; align-self: flex-start; }
            ";
            const string html =
                "<div class=\"outer\">" +
                "<div class=\"inner-flex\">" +
                "<span class=\"c\">Left</span><span class=\"d\">Right</span>" +
                "</div>" +
                "</div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var c = FindByExactClass(root, "c");
            var d = FindByExactClass(root, "d");
            Assert.That(c, Is.Not.Null, "span.c not found");
            Assert.That(d, Is.Not.Null, "span.d not found");

            // In a row flex, d.X > c.X + c.Width.
            var (cX, _) = AbsoluteOriginOf(c);
            var (dX, _) = AbsoluteOriginOf(d);
            double cRight = cX + c.Width;
            Assert.That(dX, Is.GreaterThan(cRight - 0.5),
                $"span.d.X={dX:F1} must be right of span.c right edge ({cRight:F1}); " +
                "column-flex cross-axis probe destroyed inner-flex row layout");
        }

        // ---------------------------------------------------------------
        // Issue 3 — hero-chip min-width must be honoured in the full
        //           topbar topology (grid 1fr auto 1fr → row-flex →
        //           row-flex chip). The "Aptus name missing" bug.
        //
        // The chip declares `min-width: 110px; max-width: 168px;` with two
        // children (40px portrait + ellipsis name). Outer grid + flex
        // columns put it in a nested row-flex-inside-row-flex topology —
        // the same one Issue 2 partially fixed. In the broken state the
        // chip shrinks below min-width (often to ~60-80px = just the
        // portrait), leaving no room for the name child which then
        // collapses to zero width and gets clipped by overflow:hidden.
        //
        // Sibling of Issue 2: probably another probe codepath in
        // GridLayout or FlexLayout that overwrites the chip's resolved
        // width without re-honouring its min-width floor.
        // ---------------------------------------------------------------

        // Helper: walk down a chain of class names and return the descendant box
        // matching the last class, found by traversing children that match
        // the corresponding class at each level. Simpler than dragging in a
        // full selector engine.
        static BlockBox FindChildByClass(BlockBox parent, string cls) {
            if (parent == null) return null;
            foreach (var b in AllBoxes(parent)) {
                if (b == parent) continue;
                if (b is BlockBox bb && bb.Element != null) {
                    string c = bb.Element.GetAttribute("class");
                    if (c != null && HasExactClass(c, cls)) return bb;
                }
            }
            return null;
        }

        [Test]
        public void Full_topbar_hero_chip_honours_min_width_1920() {
            // hero-chip declares min-width: 110px in FullTopbarCss above.
            // The parent grid cell (1fr) at 1920 is ~890px wide — far more
            // than enough — so min-width is the only floor in play.
            // If the chip's width drops below 110, a probe path is
            // overwriting the resolved width without honouring min-width.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var chip = FindFullTopbarBox(root, "hero-chip");
            Assert.That(chip, Is.Not.Null, "hero-chip not found in full topbar (1920)");

            // 0.5 px rounding slack on the spec floor of 110.
            Assert.That(chip.Width, Is.GreaterThanOrEqualTo(109.5),
                $"[1920] hero-chip.Width={chip.Width:F1} < 110px min-width; " +
                "engine probe is overwriting resolved width without re-honouring min-width " +
                "(sibling of the FlexLayout/GridLayout probe class cleaned up in 2d922e4)");
        }

        [Test]
        public void Full_topbar_hero_chip_name_has_visible_width_1920() {
            // The "Aptus name missing" bug: chip < 110px → name div squeezed
            // to zero width by flex shrink + overflow:hidden. The user can't
            // see the hero name in the topbar.
            //
            // The chip's content frame after subtracting padding (4+14=18px)
            // gap (12px) and 40px portrait = 110-70 = 40px MINIMUM available
            // for the name div. "Selina" / "Aptus" both render comfortably
            // in that. Anything < 5px means the name is invisible.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var chip = FindFullTopbarBox(root, "hero-chip");
            Assert.That(chip, Is.Not.Null, "hero-chip not found in full topbar (1920)");

            var name = FindChildByClass(chip, "hero-chip-name");
            Assert.That(name, Is.Not.Null, "hero-chip-name child div not found");

            Assert.That(name.Width, Is.GreaterThan(5.0),
                $"[1920] hero-chip-name.Width={name.Width:F1} ≈ 0 → name invisible. " +
                "Chip likely shrunk below its 110px min-width, squeezing the name " +
                "child to 0 width which then clipped via overflow:hidden.");
        }

        [Test]
        public void Full_topbar_hero_chip_portrait_and_name_lay_out_in_a_row_1920() {
            // Pin the row-flex direction inside the chip: portrait's right
            // edge must be left of name's left edge. If a probe codepath
            // block-stacks the chip's children, the name div lands at X=0
            // (same as portrait) instead of after it horizontally.
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);

            var chip = FindFullTopbarBox(root, "hero-chip");
            Assert.That(chip, Is.Not.Null, "hero-chip not found");

            var portrait = FindChildByClass(chip, "hero-chip-portrait");
            var name     = FindChildByClass(chip, "hero-chip-name");

            Assert.That(portrait, Is.Not.Null, "hero-chip-portrait not found");
            Assert.That(name,     Is.Not.Null, "hero-chip-name not found");

            var (px, _) = AbsoluteOriginOf(portrait);
            var (nx, _) = AbsoluteOriginOf(name);

            // 0.5 px rounding slack; without row layout, name.X == portrait.X.
            Assert.That(nx, Is.GreaterThan(px + portrait.Width - 0.5),
                $"[1920] name.X={nx:F1} must be right of portrait right edge " +
                $"({px + portrait.Width:F1}); chip children are block-stacked, not row-flexed");
        }

        [Test]
        public void Full_topbar_hero_chip_honours_min_width_1280() {
            // Same constraint at the narrower viewport that surfaced the
            // first wave of nested-flex bugs (commit b6ac9ab era).
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1280, viewportHeight: 720);

            var chip = FindFullTopbarBox(root, "hero-chip");
            Assert.That(chip, Is.Not.Null, "hero-chip not found in full topbar (1280)");

            Assert.That(chip.Width, Is.GreaterThanOrEqualTo(109.5),
                $"[1280] hero-chip.Width={chip.Width:F1} < 110px min-width");
        }

        [Test]
        public void Full_topbar_hero_chip_name_has_visible_width_1280() {
            var (root, _, _) = Build(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1280, viewportHeight: 720);

            var chip = FindFullTopbarBox(root, "hero-chip");
            Assert.That(chip, Is.Not.Null, "hero-chip not found (1280)");

            var name = FindChildByClass(chip, "hero-chip-name");
            Assert.That(name, Is.Not.Null, "hero-chip-name child div not found (1280)");

            Assert.That(name.Width, Is.GreaterThan(5.0),
                $"[1280] hero-chip-name.Width={name.Width:F1} ≈ 0 → name invisible");
        }

        // ---------------------------------------------------------------
        // Minimal repro of the same bug class — strips the topbar down to
        // the smallest topology that still violates min-width. Useful for
        // bisecting and as the unit test once the engine is fixed.
        // ---------------------------------------------------------------

        [Test]
        public void Full_topbar_hero_chip_honours_min_width_with_backdrop_filter() {
            // The shipping production CSS has `backdrop-filter: blur(8px)` on
            // the .topbar; the test FullTopbarCss above does not. backdrop-filter
            // promotes the topbar to its own stacking context AND triggers the
            // filter pipeline, which (per FilterPipeline.cs) re-rasterises into
            // an offscreen RT. If that pipeline re-runs layout with a different
            // available width, the chip's min-width could be violated on the
            // re-pass even though the first pass was correct.
            //
            // Mirror FullTopbarCss but add backdrop-filter on .topbar.
            const string cssWithBackdrop = @"
                button { display: flex; align-items: center; }
                img    { display: block; }
                .hud { position: relative; width: 100%; height: 100%; background: transparent; }
                .topbar {
                    position: fixed; top: 0; left: 0; right: 0;
                    height: 58px; padding: 0 20px;
                    display: grid; grid-template-columns: 1fr auto 1fr;
                    align-items: center; gap: 24px;
                    background: rgba(10,14,22,0.88);
                    backdrop-filter: blur(8px);
                    border-bottom: 1px solid #475569;
                    z-index: 10;
                }
                .topbar-left  { display: flex; align-items: center; gap: 14px; }
                .topbar-right { display: flex; align-items: center; gap: 14px; justify-self: end; }
                .hero-chip {
                    display: flex; align-items: center; gap: 12px;
                    min-width: 110px; max-width: 168px;
                    padding: 4px 14px 4px 4px;
                    background: #1c2435; border: 1px solid #475569;
                    border-radius: 6px; color: #fff; overflow: hidden;
                }
                .hero-chip-portrait { width: 40px; height: 40px; border-radius: 4px; flex-shrink: 0; }
                .hero-chip-name { min-width: 0; font-size: 14px; font-weight: 700;
                    white-space: nowrap; overflow: hidden; }
                .player-chip { display: flex; align-items: center; gap: 8px; padding-left: 16px; }
                .player-chip-name { max-width: 130px; font-size: 14px; font-weight: 600; white-space: nowrap; overflow: hidden; }
                .player-chip-edit { height: 30px; padding: 0 10px; }
                .tabs { display: flex; align-items: center; gap: 2px; padding: 3px; background: #1c2435; border-radius: 6px; }
                .tab  { height: 30px; padding: 0 12px; background: transparent; border: 0; font-size: 13px; font-weight: 700; border-radius: 4px; }
                .tab.active { background: #2a3654; }
                .wallet-strip { display: flex; gap: 8px; }
                .wallet-pill  { min-width: 96px; padding: 7px 10px; text-align: center;
                    border: 1px solid rgba(251,191,36,0.3); border-radius: 4px;
                    font-size: 12px; font-weight: 800; }
                .wallet-pill.essence { border-color: rgba(167,139,250,0.32); }
                .exit-btn { height: 30px; padding: 0 10px;
                    border: 1px solid #475569; border-radius: 4px;
                    font-size: 12px; font-weight: 700; }
            ";

            var (root, _, _) = Build(FullTopbarHtml, cssWithBackdrop,
                viewportWidth: 1920, viewportHeight: 1080);

            var chip = FindFullTopbarBox(root, "hero-chip");
            Assert.That(chip, Is.Not.Null, "hero-chip not found (backdrop-filter variant)");

            Assert.That(chip.Width, Is.GreaterThanOrEqualTo(109.5),
                $"[backdrop-filter] hero-chip.Width={chip.Width:F1} < 110px; " +
                "filter pipeline may be re-running layout without min-width");

            var name = FindChildByClass(chip, "hero-chip-name");
            Assert.That(name, Is.Not.Null, "hero-chip-name not found (backdrop-filter variant)");
            Assert.That(name.Width, Is.GreaterThan(5.0),
                $"[backdrop-filter] hero-chip-name.Width={name.Width:F1} ≈ 0 → invisible");
        }

        [Test]
        public void Nested_row_flex_chip_min_width_is_honoured_in_grid_cell() {
            // grid(1fr auto 1fr) → row-flex(.col-left) → row-flex(.chip)
            // .chip declares min-width:110px and contains a 40px img + a div
            // with min-width:0. Engine must keep .chip at >= 110px no matter
            // what intrinsic probe runs on it.
            const string css = @"
                img { display: block; }
                .grid { display: grid; grid-template-columns: 1fr auto 1fr;
                        gap: 24px; width: 1880px; height: 58px;
                        align-items: center; padding: 0 20px; }
                .col-left { display: flex; align-items: center; gap: 14px; }
                .col-mid  { display: flex; gap: 2px; padding: 3px;
                            background: #222; }
                .col-right { display: flex; align-items: center; gap: 14px;
                             justify-self: end; }
                .chip { display: flex; align-items: center; gap: 12px;
                        min-width: 110px; max-width: 168px;
                        padding: 4px 14px 4px 4px; overflow: hidden; }
                .portrait { width: 40px; height: 40px; flex-shrink: 0; }
                .name { min-width: 0; white-space: nowrap; overflow: hidden; }
                .tab { padding: 0 12px; height: 30px; }
                .pill { min-width: 96px; padding: 7px 10px; }
                .exit { height: 30px; padding: 0 10px; }
            ";
            const string html =
                "<div class=\"grid\">" +
                "  <div class=\"col-left\">" +
                "    <div class=\"chip\">" +
                "      <img class=\"portrait\"/>" +
                "      <div class=\"name\">Aptus</div>" +
                "    </div>" +
                "  </div>" +
                "  <div class=\"col-mid\">" +
                "    <div class=\"tab\">Play</div><div class=\"tab\">Mastery</div>" +
                "    <div class=\"tab\">Challenges</div><div class=\"tab\">Upgrades</div>" +
                "  </div>" +
                "  <div class=\"col-right\">" +
                "    <div class=\"pill\">Coins</div><div class=\"pill\">Essence</div>" +
                "    <div class=\"exit\">Exit</div>" +
                "  </div>" +
                "</div>";

            var (root, _, _) = Build(html, css, viewportWidth: 1920, viewportHeight: 1080);

            var chip = FindByExactClass(root, "chip");
            Assert.That(chip, Is.Not.Null, "chip not found");

            Assert.That(chip.Width, Is.GreaterThanOrEqualTo(109.5),
                $"chip.Width={chip.Width:F1} < 110px min-width; " +
                "nested-row-flex-in-grid-cell probe is overwriting resolved width");

            var name = FindChildByClass(chip, "name");
            Assert.That(name, Is.Not.Null, "name child not found");
            Assert.That(name.Width, Is.GreaterThan(5.0),
                $"name.Width={name.Width:F1} ≈ 0 → child collapsed because chip didn't honour min-width");
        }

        // ---------------------------------------------------------------
        // Issue 4 — Buy Upgrade button: text content of block-level
        //           children of a `display:flex; justify-content:center`
        //           container renders OUTSIDE the parent box bounds.
        //
        // Visible in editor + player: the .upgrade-buy-btn renders the
        // two child spans (label + cost) as EMPTY colored boxes on the
        // left, with the actual text "Buy Upgrade" floating to the right
        // of the boxes. Confirmed via probe screenshots — text glyphs
        // are decoupled from their owning element's box.
        //
        // Triggered topology:
        //   <button display:flex; justify-content:center; gap:10px>
        //     <span/div with text>...</span/div>
        //     <span/div with text>...</span/div>
        //   </button>
        //
        // Production workaround (cfae1fda / 1debd129): switch button to
        // display:block + text-align:center + inline-block children.
        // ---------------------------------------------------------------

        // Walk every Box subtree rooted at `parent` and return the first
        // InlineBox or BlockBox descendant whose Element is `wantedElement`.
        // The bug should leave the text run located OUTSIDE that element's
        // bounding box; this helper plus AbsoluteOriginOf lets the test
        // detect that detachment.
        static (double minX, double maxX) ContentXRange(Box root) {
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            foreach (var b in AllBoxes(root)) {
                if (b is InlineBox || b is BlockBox) {
                    var (bx, _) = AbsoluteOriginOf(b);
                    if (bx < minX) minX = bx;
                    double right = bx + (b is BlockBox bb ? bb.Width : ((InlineBox)b).Width);
                    if (right > maxX) maxX = right;
                }
            }
            return (minX, maxX);
        }

        [Test]
        public void Flex_button_child_span_text_renders_inside_parent_box() {
            // Repro of the Buy Upgrade bug.
            // Expected: each .label span's box contains its text content.
            //  - label1.X within button.X..(button.X + button.Width)
            //  - label1's text run's X is inside label1's bounds (not floating beyond)
            const string css = @"
                button { display: flex; align-items: center; justify-content: center; gap: 10px;
                         height: 44px; width: 400px; }
                .lbl, .cst { display: inline-block; padding: 0; }
            ";
            const string html =
                "<button>" +
                "  <span class=\"lbl\">LABEL-X</span>" +
                "  <span class=\"cst\">COST-Y</span>" +
                "</button>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var lbl = FindByExactClass(root, "lbl");
            var cst = FindByExactClass(root, "cst");
            Assert.That(lbl, Is.Not.Null, "label span not found");
            Assert.That(cst, Is.Not.Null, "cost span not found");

            // Each span must have non-zero width (= its content was measured).
            Assert.That(lbl.Width, Is.GreaterThan(5.0),
                $".lbl.Width={lbl.Width:F1} ≈ 0 → text content didn't size the span");
            Assert.That(cst.Width, Is.GreaterThan(5.0),
                $".cst.Width={cst.Width:F1} ≈ 0 → text content didn't size the span");

            // The text RUN for the span's text must live inside the span's
            // box bounds. If the engine detaches it (visible bug), the text
            // run's X would be > span's right edge.
            var (lblMin, lblMax) = ContentXRange(lbl);
            Assert.That(lblMin, Is.GreaterThanOrEqualTo(lbl.X - 0.5),
                $".lbl content min-X={lblMin:F1} < .lbl.X={lbl.X:F1} → text detached left");
            Assert.That(lblMax, Is.LessThanOrEqualTo(lbl.X + lbl.Width + 0.5),
                $".lbl content max-X={lblMax:F1} > .lbl right edge ({lbl.X + lbl.Width:F1}) → text detached right");
        }

        [Test]
        public void Flex_button_child_div_text_renders_inside_parent_box() {
            // Same shape with <div> children — the user's screenshot proves
            // the bug also fires with block children, not just inline-spans.
            const string css = @"
                button { display: flex; align-items: center; justify-content: center; gap: 10px;
                         height: 44px; width: 400px; }
                .lbl, .cst { padding: 0; }
            ";
            const string html =
                "<button>" +
                "  <div class=\"lbl\">LABEL-X</div>" +
                "  <div class=\"cst\">COST-Y</div>" +
                "</button>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var lbl = FindByExactClass(root, "lbl");
            var cst = FindByExactClass(root, "cst");
            Assert.That(lbl, Is.Not.Null, "label div not found");
            Assert.That(cst, Is.Not.Null, "cost div not found");

            Assert.That(lbl.Width, Is.GreaterThan(5.0),
                $".lbl.Width={lbl.Width:F1} ≈ 0");
            Assert.That(cst.Width, Is.GreaterThan(5.0),
                $".cst.Width={cst.Width:F1} ≈ 0");

            var (lblMin, lblMax) = ContentXRange(lbl);
            Assert.That(lblMax, Is.LessThanOrEqualTo(lbl.X + lbl.Width + 0.5),
                $".lbl content max-X={lblMax:F1} > .lbl right edge ({lbl.X + lbl.Width:F1}) → text detached");
        }

        [Test]
        public void Flex_button_centered_two_children_widths_sum_to_centered_layout() {
            // Indirect repro: in a 400px button with two ~80px children + 10px gap,
            // total content = 170px; the children should occupy 115..285. If the
            // text gets pulled out into anonymous flex items, the visible glyphs
            // float past 285 (toward the right edge of the button), and the
            // total content X-range will exceed the centered span.
            const string css = @"
                button { display: flex; align-items: center; justify-content: center; gap: 10px;
                         height: 44px; width: 400px; padding: 0; border: 0; }
                .lbl, .cst { padding: 0; }
            ";
            const string html =
                "<button>" +
                "  <span class=\"lbl\">LABEL-X</span>" +
                "  <span class=\"cst\">COST-Y</span>" +
                "</button>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var btn = FindFirstButton(root);
            Assert.That(btn, Is.Not.Null, "button not found");

            // Find the right-most descendant box.
            double maxRight = 0;
            foreach (var b in AllBoxes(btn)) {
                if (b == btn) continue;
                var (bx, _) = AbsoluteOriginOf(b);
                double right = bx + (b is BlockBox bb ? bb.Width : (b is InlineBox ib ? ib.Width : 0));
                if (right > maxRight) maxRight = right;
            }

            var (btnX, _) = AbsoluteOriginOf(btn);
            double btnRight = btnX + btn.Width;
            // Centered layout: total content ~170px, button 400px, so right edge of
            // content should land around btnX + (400+170)/2 = btnX + 285. If text
            // is detached and floats right, maxRight approaches btnRight (400).
            // Hard floor: content must stay within 80% of the button width on the
            // right side (320px from start) for a "centered" claim.
            double centeredRightBudget = btnX + btn.Width * 0.85;
            Assert.That(maxRight, Is.LessThanOrEqualTo(centeredRightBudget),
                $"right-most content X={maxRight:F1} crowds past 85% of button width " +
                $"(budget={centeredRightBudget:F1}); centered children should not reach the right edge — " +
                "text detached from spans and rendered as separate inline runs");
        }

        static BlockBox FindFirstButton(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "button") return bb;
            }
            return null;
        }
    }
}
