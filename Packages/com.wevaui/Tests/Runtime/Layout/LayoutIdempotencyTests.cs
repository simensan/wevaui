using System;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Idempotency check: running Layout twice on the same input must produce
    // identical box geometries. Divergence means the first pass left mutable
    // state somewhere (stale offset, pooled box not reset).
    // The destructive-probe bug class shows as first-run positions being wrong
    // compared to a fresh re-run.
    public class LayoutIdempotencyTests {

        // Build the same HTML/CSS twice and return both roots.
        static (Box first, Box second) BuildTwice(string html, string css,
                double viewportWidth = 800, double viewportHeight = 600) {
            var (r1, _, _) = Build(html, css, viewportWidth, viewportHeight);
            var (r2, _, _) = Build(html, css, viewportWidth, viewportHeight);
            return (r1, r2);
        }

        // Walk two box trees in parallel and assert every box has matching
        // X, Y, Width, Height (within 0.001 px).
        static void AssertTreesIdentical(Box a, Box b, string path = "root") {
            AssertBoxGeom(a, b, path);
            if (a.Children.Count != b.Children.Count) {
                Assert.Fail($"[idempotency] child count differs at {path}: " +
                            $"first={a.Children.Count} second={b.Children.Count}");
                return;
            }
            for (int i = 0; i < a.Children.Count; i++) {
                string childPath = $"{path}[{i}:{ChildLabel(a.Children[i])}]";
                AssertTreesIdentical(a.Children[i], b.Children[i], childPath);
            }
        }

        static void AssertBoxGeom(Box a, Box b, string path) {
            const double tol = 0.001;
            if (Math.Abs(a.X - b.X) > tol)
                Assert.Fail($"[idempotency] X differs at {path}: first={a.X:F4} second={b.X:F4}");
            if (Math.Abs(a.Y - b.Y) > tol)
                Assert.Fail($"[idempotency] Y differs at {path}: first={a.Y:F4} second={b.Y:F4}");
            if (Math.Abs(a.Width - b.Width) > tol)
                Assert.Fail($"[idempotency] Width differs at {path}: first={a.Width:F4} second={b.Width:F4}");
            if (Math.Abs(a.Height - b.Height) > tol)
                Assert.Fail($"[idempotency] Height differs at {path}: first={a.Height:F4} second={b.Height:F4}");
        }

        static string ChildLabel(Box b) {
            if (b is TextRun tr) return $"TextRun({tr.Text?.Substring(0, Math.Min(tr.Text?.Length ?? 0, 8)) ?? ""})";
            if (b is LineBox) return "LineBox";
            if (b is BlockBox bb) return $"Block({bb.Element?.TagName ?? "anon"})";
            return b.GetType().Name;
        }

        // ---- Topology 1: full topbar (grid 1fr auto 1fr → nested flex → chips) ----
        // Mirrors the full-topbar regression fixture's constants (kept local to avoid
        // a cross-class dependency that would force internal visibility changes).

        const string FullTopbarCss = @"
            button { display: flex; align-items: center; }
            img    { display: block; }
            .hud { position: relative; width: 100%; height: 100%; background: transparent; }
            .topbar {
                position: fixed; top: 0; left: 0; right: 0;
                height: 58px; padding: 0 20px;
                display: grid; grid-template-columns: 1fr auto 1fr;
                align-items: center; gap: 24px;
            }
            .topbar-left  { display: flex; align-items: center; gap: 14px; }
            .topbar-right { display: flex; align-items: center; gap: 14px; justify-self: end; }
            .hero-chip {
                display: flex; align-items: center; gap: 12px;
                min-width: 110px; max-width: 168px;
                padding: 4px 14px 4px 4px; overflow: hidden;
            }
            .hero-chip-portrait { width: 40px; height: 40px; flex-shrink: 0; }
            .hero-chip-name { min-width: 0; white-space: nowrap; overflow: hidden; }
            .tabs { display: flex; align-items: center; gap: 2px; padding: 3px; }
            .tab  { height: 30px; padding: 0 12px; }
            .wallet-strip { display: flex; gap: 8px; }
            .wallet-pill  { min-width: 96px; padding: 7px 10px; }
            .exit-btn { height: 30px; padding: 0 10px; }
        ";

        const string FullTopbarHtml =
            "<body style=\"width:1920px;height:1080px;\">" +
            "  <main class=\"hud\">" +
            "    <header class=\"topbar\">" +
            "      <div class=\"topbar-left\">" +
            "        <button class=\"hero-chip\">" +
            "          <img class=\"hero-chip-portrait\" />" +
            "          <div class=\"hero-chip-name\">Selina</div>" +
            "        </button>" +
            "      </div>" +
            "      <nav class=\"tabs\">" +
            "        <button class=\"tab\">Play</button>" +
            "        <button class=\"tab\">Mastery</button>" +
            "      </nav>" +
            "      <div class=\"topbar-right\">" +
            "        <div class=\"wallet-strip\">" +
            "          <span class=\"wallet-pill\">Coins 19</span>" +
            "          <span class=\"wallet-pill\">Essence 4</span>" +
            "        </div>" +
            "        <button class=\"exit-btn\">Exit</button>" +
            "      </div>" +
            "    </header>" +
            "  </main>" +
            "</body>";

        [Test]
        public void Full_topbar_topology_is_idempotent_1920() {
            var (first, second) = BuildTwice(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1920, viewportHeight: 1080);
            AssertTreesIdentical(first, second);
        }

        [Test]
        public void Full_topbar_topology_is_idempotent_1280() {
            var (first, second) = BuildTwice(FullTopbarHtml, FullTopbarCss,
                viewportWidth: 1280, viewportHeight: 720);
            AssertTreesIdentical(first, second);
        }

        // ---- Topology 2: upgrade-buy-btn (flex + justify-content:center + text-align) ----
        // Text-align stale-delta bug: first pass used wider provisional content width;
        // second pass corrected it. A re-run should produce the same result.

        const string BtnCss = @"
            button { display: flex; align-items: center; justify-content: center;
                     gap: 10px; height: 44px; width: 400px; padding: 0; border: 0; }
            .lbl, .cst { display: inline-block; padding: 0; }
        ";
        const string BtnHtml =
            "<button><span class=\"lbl\">LABEL-X</span><span class=\"cst\">COST-Y</span></button>";

        [Test]
        public void Flex_button_centered_text_is_idempotent() {
            var (first, second) = BuildTwice(BtnHtml, BtnCss);
            AssertTreesIdentical(first, second);
        }

        // ---- Topology 3: hero-chip (row-flex with min-width + img + overflow:hidden name) ----

        const string ChipCss = @"
            img { display: block; }
            .chip { display: flex; align-items: center; gap: 12px;
                    min-width: 110px; max-width: 168px;
                    padding: 4px 14px 4px 4px; overflow: hidden; }
            .portrait { width: 40px; height: 40px; flex-shrink: 0; }
            .name { min-width: 0; white-space: nowrap; overflow: hidden; }
        ";
        const string ChipHtml =
            "<div class=\"chip\"><img class=\"portrait\"/><div class=\"name\">Aptus</div></div>";

        [Test]
        public void Hero_chip_topology_is_idempotent() {
            var (first, second) = BuildTwice(ChipHtml, ChipCss);
            AssertTreesIdentical(first, second);
        }

        // ---- Topology 4: hero-picker body (column-flex + grid with flex:1 child) ----
        // The destructive-probe bug in GridLayout.ApplyItemAlignment caused flex
        // children to stack at Y=0 on the first pass.

        const string HeroBodyCss = @"
            header  { display: block; }
            ul      { display: block; margin: 0; padding: 0; }
            button  { display: block; }
            img     { display: inline-block; }
            section { display: block; }
            .hero-picker-body {
                flex: 1; display: grid; grid-template-columns: 240px 1fr;
                gap: 0; overflow: hidden;
            }
            .hero-picker-list {
                display: flex; flex-direction: column; gap: 4px; padding: 12px;
                list-style: none; margin: 0;
            }
            .hero-picker-card {
                display: flex; align-items: center; gap: 12px;
                padding: 10px 12px;
            }
            .hero-picker-card-icon { width: 48px; height: 48px; flex-shrink: 0; }
        ";
        const string HeroBodyHtml =
            "<div style=\"position:fixed;top:80px;left:80px;width:800px;height:500px;" +
            "            display:flex;flex-direction:column;\">" +
            "  <header style=\"height:60px;\">Header</header>" +
            "  <div class=\"hero-picker-body\">" +
            "    <ul class=\"hero-picker-list\">" +
            "      <button class=\"hero-picker-card\"><img class=\"hero-picker-card-icon\"/>A</button>" +
            "      <button class=\"hero-picker-card\"><img class=\"hero-picker-card-icon\"/>B</button>" +
            "    </ul>" +
            "    <section>detail</section>" +
            "  </div>" +
            "</div>";

        [Test]
        public void Hero_picker_body_topology_is_idempotent() {
            var (first, second) = BuildTwice(HeroBodyHtml, HeroBodyCss,
                viewportWidth: 1200, viewportHeight: 700);
            AssertTreesIdentical(first, second);
        }

        // ---- Topology 5: nested grid-in-flex with justify-self:end ----

        const string GridChipCss = @"
            img { display: block; }
            .grid { display: grid; grid-template-columns: 1fr auto 1fr;
                    gap: 24px; width: 1880px; height: 58px;
                    align-items: center; padding: 0 20px; }
            .col-left { display: flex; align-items: center; gap: 14px; }
            .col-mid  { display: flex; gap: 2px; padding: 3px; }
            .col-right { display: flex; align-items: center; gap: 14px; justify-self: end; }
            .chip { display: flex; align-items: center; gap: 12px;
                    min-width: 110px; max-width: 168px;
                    padding: 4px 14px 4px 4px; overflow: hidden; }
            .portrait { width: 40px; height: 40px; flex-shrink: 0; }
            .name { min-width: 0; white-space: nowrap; overflow: hidden; }
            .tab { padding: 0 12px; height: 30px; }
            .pill { min-width: 96px; padding: 7px 10px; }
        ";
        const string GridChipHtml =
            "<div class=\"grid\">" +
            "  <div class=\"col-left\">" +
            "    <div class=\"chip\"><img class=\"portrait\"/><div class=\"name\">Aptus</div></div>" +
            "  </div>" +
            "  <div class=\"col-mid\">" +
            "    <div class=\"tab\">Play</div><div class=\"tab\">Mastery</div>" +
            "  </div>" +
            "  <div class=\"col-right\">" +
            "    <div class=\"pill\">Coins</div><div class=\"pill\">Essence</div>" +
            "  </div>" +
            "</div>";

        [Test]
        public void Nested_grid_flex_chip_topology_is_idempotent() {
            var (first, second) = BuildTwice(GridChipHtml, GridChipCss,
                viewportWidth: 1920, viewportHeight: 1080);
            AssertTreesIdentical(first, second);
        }

        // ---- Topology 6: simple block text ----
        // Baseline sanity: plain block layout must trivially be idempotent.

        [Test]
        public void Simple_block_text_is_idempotent() {
            var (first, second) = BuildTwice(
                "<div style=\"width:400px\"><p>Hello world</p><p>Second paragraph</p></div>", null);
            AssertTreesIdentical(first, second);
        }
    }
}
