using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.PerfBench {
    // Long-running stress harness for a real game UI hot path: a large
    // HUD-scale document with many state-pseudo selectors, animated inline
    // styles updating every frame, and incremental cascade refresh through
    // CascadeEngine.Compute(element). Designed to be wrapped under
    // dotnet-trace / dotnet-counters so the hot-path attribution is visible
    // without needing Unity.
    static class StressRunner {
        // ── HUD-like author stylesheet ─────────────────────────────────
        // 70+ class selectors with ~38 state pseudo-classes — mirrors
        // game-hud.css in a production HUD where most of the cascade cost lives.
        const string HudCss = @"
.hud-root { position: fixed; inset: 0; display: flex; flex-direction: column; }
.hud-top { display: flex; justify-content: center; padding: 12px 0; }
.hud-bottom { display: flex; flex-direction: column; padding: 0 24px 16px; }
.skill-bar { display: flex; align-items: flex-end; gap: 20px; }
.skill-group { display: flex; align-items: flex-end; gap: 4px; }
.skill-slot { position: relative; width: 52px; height: 52px; border: 2px solid #444; background: #0a0e16; }
.skill-slot:hover { border-color: white; }
.skill-slot:focus { border-color: cyan; }
.skill-slot:active { background: red; }
.skill-slot.active { width: 60px; height: 60px; border-color: #aaa; }
.skill-slot.active:hover { border-color: white; }
.skill-slot.ultimate { border-color: #a78bfa; }
.skill-slot.ultimate:hover { border-color: white; }
.skill-slot.passive { width: 44px; height: 44px; }
.skill-slot.passive:hover { border-color: white; }
.skill-slot.on-cooldown { border-color: rgba(255,255,255,0.15); }
.skill-slot.on-cooldown:hover { border-color: rgba(255,255,255,0.4); }
.skill-slot.active-now { border-color: #22c55e; }
.skill-slot.ready-flash { border-color: #93c5fd; }
.skill-slot-icon { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
.skill-slot.on-cooldown .skill-slot-icon { opacity: 0.4; }
.skill-slot:hover .skill-slot-icon { transform: scale(1.05); }
.skill-slot-cd-sweep { position: absolute; inset: 0; }
.skill-slot-cd-text { position: absolute; top: 50%; left: 50%; font-size: 14px; font-weight: 900; color: #fff; }
.skill-slot-cd-text.hidden { display: none; }
.skill-slot-key { position: absolute; bottom: 2px; left: 4px; font-size: 10px; color: rgba(255,255,255,0.8); }
.skill-slot-level { position: absolute; top: 2px; right: 4px; font-size: 10px; color: #fbbf24; }
.skill-slot-level.hidden { display: none; }
.stat-slot { width: 28px; height: 28px; background: rgba(255,255,255,0.05); }
.stat-slot:hover { background: rgba(255,255,255,0.15); }
.stat-slot.empty { opacity: 0.3; }
.stat-slot.locked { opacity: 0.1; }
.stat-slot-icon { width: 100%; height: 100%; }
.stat-slot-count { position: absolute; bottom: 0; right: 2px; font-size: 9px; font-weight: 900; color: #fbbf24; }
.stat-slot-count.hidden { display: none; }
.boss-bar { position: fixed; top: 50px; padding: 10px 20px; background: rgba(12,16,26,0.85); border: 1px solid rgba(239,68,68,0.3); }
.boss-bar.hidden { display: none; }
.boss-name { font-size: 16px; font-weight: 900; color: #ef4444; }
.boss-hp-track { padding: 3px 0; background: rgba(255,255,255,0.1); border-radius: 999px; }
.boss-hp-fill { background: #ef4444; }
.boss-cast { font-size: 12px; color: #fbbf24; }
.boss-cast.hidden { display: none; }
.game-timer { padding: 6px 20px; background: rgba(10,14,22,0.7); font-size: 16px; font-weight: 800; color: #f3f4f6; }
.coins-display { padding: 6px 16px; background: rgba(10,14,22,0.7); border: 1px solid rgba(251,191,36,0.25); font-size: 13px; font-weight: 900; color: #fbbf24; }
.objective-item { padding: 8px 10px; background: rgba(255,255,255,0.04); border-left: 3px solid #60a5fa; }
.objective-item.urgent { border-left-color: #ef4444; }
.objective-item.success { border-left-color: #22c55e; }
.objective-item:hover { background: rgba(255,255,255,0.08); }
.objective-item-title { font-size: 12px; font-weight: 700; color: #f3f4f6; }
.objective-item-timer { font-size: 11px; font-weight: 800; color: #60a5fa; }
.objective-item.urgent .objective-item-timer { color: #ef4444; }
.objective-item-timer.hidden { display: none; }
.objective-item-reward { font-size: 10px; color: #a78bfa; }
.objective-item-reward.hidden { display: none; }
.discovery-toast { position: fixed; top: 48px; left: 50%; padding: 10px 28px; background: rgba(12,16,26,0.9); border-radius: 999px; opacity: 0; }
.discovery-toast.visible { opacity: 1; }
.discovery-toast-text { font-size: 15px; font-weight: 700; color: #fbbf24; }
.xp-bar { width: 100%; height: 6px; background: rgba(255,255,255,0.14); }
.xp-fill { height: 6px; background: #3b82f6; }
.xp-gain { display: none; }
.xp-debug { font-size: 10px; font-weight: 800; color: rgba(255,255,255,0.75); }
.level-badge { padding: 3px 10px; background: rgba(10,14,22,0.85); border: 1px solid rgba(96,165,250,0.4); font-size: 13px; font-weight: 900; color: #60a5fa; }
button { padding: 4px 8px; background: #4f46e5; color: white; }
button:hover { background: #6366f1; }
button:focus { outline: 2px solid yellow; }
button:active { transform: translateY(1px); }
button:disabled { opacity: 0.5; }
a { color: #60a5fa; }
a:hover { text-decoration: underline; }
a:focus { outline: 1px solid white; }
a:active { color: red; }
input { padding: 2px; }
input:focus { border-color: cyan; }
input:disabled { opacity: 0.5; }
.tab { padding: 8px; background: #222; }
.tab:hover { background: #333; }
.tab:focus { background: #444; }
.tab.active { background: #555; }
.tab.active:hover { background: #666; }
";

        // ~200-element HUD-shape (skill slots, stat slots, objectives, boss
        // bar). Matches a real game UI's runtime DOM shape so cascade timings
        // map directly to in-game expectations.
        static string BuildHudHtml() {
            var sb = new StringBuilder();
            sb.Append("<div class=\"hud-root\">");
            sb.Append("  <div class=\"hud-top\">");
            sb.Append("    <span class=\"game-timer\">00:42</span>");
            sb.Append("    <span class=\"coins-display\">COINS 1280</span>");
            sb.Append("  </div>");

            sb.Append("  <div class=\"boss-bar\">");
            sb.Append("    <span class=\"boss-name\">Obsidian Wyrm</span>");
            sb.Append("    <div class=\"boss-hp-track\"><div class=\"boss-hp-fill\"></div></div>");
            sb.Append("    <span class=\"boss-cast\">Channeling...</span>");
            sb.Append("  </div>");

            sb.Append("  <div class=\"objectives-panel\">");
            for (int i = 0; i < 5; i++) {
                var cls = i == 0 ? "urgent" : (i == 1 ? "success" : "");
                sb.Append("    <div class=\"objective-item ").Append(cls).Append("\">");
                sb.Append("      <span class=\"objective-item-title\">Obj ").Append(i).Append("</span>");
                sb.Append("      <span class=\"objective-item-timer\">").Append(60 - i * 10).Append("s</span>");
                sb.Append("      <span class=\"objective-item-reward\">+").Append((i + 1) * 100).Append(" gold</span>");
                sb.Append("    </div>");
            }
            sb.Append("  </div>");

            sb.Append("  <div class=\"hud-bottom\">");
            sb.Append("    <span class=\"level-badge\">L 12</span>");
            sb.Append("    <div class=\"skill-bar\">");
            sb.Append("      <div class=\"skill-group\">");
            // 6 passives
            for (int i = 0; i < 6; i++) {
                bool cd = (i % 3) == 0;
                sb.Append("        <button class=\"skill-slot passive").Append(cd ? " on-cooldown" : "").Append("\">");
                sb.Append("          <img class=\"skill-slot-icon\" src=\"icon").Append(i).Append("\" />");
                sb.Append("          <div class=\"skill-slot-cd-sweep\" style=\"background: red;\"></div>");
                sb.Append("          <span class=\"skill-slot-level\">").Append(i + 1).Append("</span>");
                sb.Append("        </button>");
            }
            sb.Append("      </div>");
            sb.Append("      <div class=\"skill-group\">");
            sb.Append("        <button class=\"skill-slot active\">");
            sb.Append("          <img class=\"skill-slot-icon\" src=\"icon-e\" />");
            sb.Append("          <div class=\"skill-slot-cd-sweep\" style=\"background: conic-gradient(red 0deg, transparent 90deg);\"></div>");
            sb.Append("          <span class=\"skill-slot-key\">E</span>");
            sb.Append("          <span class=\"skill-slot-cd-text\">3</span>");
            sb.Append("        </button>");
            sb.Append("        <button class=\"skill-slot ultimate\">");
            sb.Append("          <img class=\"skill-slot-icon\" src=\"icon-r\" />");
            sb.Append("          <span class=\"skill-slot-key\">R</span>");
            sb.Append("        </button>");
            sb.Append("      </div>");
            sb.Append("      <div class=\"skill-group\">");
            // 8 stat slots
            for (int i = 0; i < 8; i++) {
                string cls = (i < 5) ? "" : (i < 7 ? "empty" : "locked");
                sb.Append("        <div class=\"stat-slot ").Append(cls).Append("\">");
                sb.Append("          <img class=\"stat-slot-icon\" src=\"stat").Append(i).Append("\" />");
                sb.Append("          <span class=\"stat-slot-count\">").Append(i + 1).Append("</span>");
                sb.Append("        </div>");
            }
            sb.Append("      </div>");
            sb.Append("    </div>");
            sb.Append("    <div class=\"xp-bar\"><div class=\"xp-fill\" style=\"width: 67%;\"></div></div>");
            sb.Append("    <span class=\"xp-debug\">670 / 1000</span>");
            sb.Append("  </div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        public static void Run(int frameCount, bool simulateAnimations, bool verbose) {
            var doc = HtmlParser.Parse(BuildHudHtml());
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BenchScenes.UA)),
                OriginatedStylesheet.Author(CssParser.Parse(HudCss)),
            };
            var cascade = new CascadeEngine(sheets, true);

            // Collect elements that animations will target (the skill-slot-
            // cd-sweep elements + the xp-fill — these are the ones whose
            // inline `style` mutates every frame in a real game HUD).
            var animatedElements = new List<Element>();
            CollectByClass(doc, "skill-slot-cd-sweep", animatedElements);
            CollectByClass(doc, "xp-fill", animatedElements);

            // Cold cascade pass.
            cascade.ComputeAll(doc);
            int elementCount = 0;
            foreach (var _ in BenchScenes.AllElements(doc)) elementCount++;

            Console.WriteLine($"[StressRunner] doc={elementCount} elements, animatedTargets={animatedElements.Count}, frames={frameCount}, simulateAnimations={simulateAnimations}");

            // Warmup (untimed).
            for (int i = 0; i < 30; i++) {
                if (simulateAnimations) MutateAnimatedStyles(animatedElements, i);
                foreach (var e in animatedElements) cascade.Compute(e);
            }

            BenchScenes.StabilizeGC();
            long gcStart = BenchScenes.AllocatedBytes();
            int gc0Start = GC.CollectionCount(0);
            int gc1Start = GC.CollectionCount(1);
            int gc2Start = GC.CollectionCount(2);

            // Per-stage byte attribution: measure how much SetAttribute alone
            // allocates vs Compute alone. Sub-frame granularity helps decide
            // whether the win lives in the cascade or in callers' string
            // formatting.
            long mutBytes = 0;
            long computeBytes = 0;

            var sw = Stopwatch.StartNew();
            var perFrame = new double[frameCount];
            for (int f = 0; f < frameCount; f++) {
                long ts = Stopwatch.GetTimestamp();
                if (simulateAnimations) {
                    long b0 = BenchScenes.AllocatedBytes();
                    MutateAnimatedStyles(animatedElements, f);
                    mutBytes += BenchScenes.AllocatedBytes() - b0;
                }
                long c0 = BenchScenes.AllocatedBytes();
                foreach (var e in animatedElements) cascade.Compute(e);
                computeBytes += BenchScenes.AllocatedBytes() - c0;
                perFrame[f] = (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency;
            }
            sw.Stop();

            long gcEnd = BenchScenes.AllocatedBytes();
            int gc0End = GC.CollectionCount(0);
            int gc1End = GC.CollectionCount(1);
            int gc2End = GC.CollectionCount(2);

            double total = sw.Elapsed.TotalMilliseconds;
            double median = BenchScenes.Median(perFrame);
            double p95 = BenchScenes.Percentile(perFrame, 0.95);
            double p99 = BenchScenes.Percentile(perFrame, 0.99);
            double max = 0; foreach (var v in perFrame) if (v > max) max = v;
            long bytesPerFrame = (gcEnd - gcStart) / Math.Max(1, frameCount);

            Console.WriteLine($"  total: {total:F1}ms over {frameCount} frames");
            Console.WriteLine($"  per-frame median: {median:F4}ms, p95: {p95:F4}ms, p99: {p99:F4}ms, max: {max:F4}ms");
            Console.WriteLine($"  alloc/frame: {bytesPerFrame} B  GC: g0={gc0End - gc0Start} g1={gc1End - gc1Start} g2={gc2End - gc2Start}");
            Console.WriteLine($"  alloc breakdown: SetAttribute={mutBytes / Math.Max(1, frameCount)} B/frame, Compute={computeBytes / Math.Max(1, frameCount)} B/frame");

            if (verbose) {
                // Surface the top per-frame outliers.
                int outliers = Math.Min(10, frameCount);
                var idxs = new int[frameCount];
                for (int i = 0; i < frameCount; i++) idxs[i] = i;
                Array.Sort(idxs, (a, b) => perFrame[b].CompareTo(perFrame[a]));
                Console.WriteLine($"  top {outliers} slow frames:");
                for (int i = 0; i < outliers; i++) {
                    Console.WriteLine($"    frame {idxs[i]}: {perFrame[idxs[i]]:F4}ms");
                }
            }
        }

        static void CollectByClass(Node n, string cls, List<Element> output) {
            if (n is Element e && HasClass(e, cls)) output.Add(e);
            foreach (var c in n.Children) CollectByClass(c, cls, output);
        }

        static bool HasClass(Element e, string cls) {
            var c = e.GetAttribute("class");
            if (string.IsNullOrEmpty(c)) return false;
            foreach (var token in c.Split(' ', '\t', '\n')) {
                if (token == cls) return true;
            }
            return false;
        }

        // Simulates a real game's binding system writing a fresh inline
        // `style` string each frame (the conic-gradient cooldown sweep + the
        // XP bar width). This is what bumps Element.Version every frame and
        // triggers the per-element re-cascade in RefreshPaintOnlyStyles.
        static void MutateAnimatedStyles(List<Element> animated, int frame) {
            for (int i = 0; i < animated.Count; i++) {
                var e = animated[i];
                double angle = ((frame + i * 7) % 100) * 3.6;
                e.SetAttribute("style", $"background: conic-gradient(red 0deg, transparent {angle:F1}deg);");
            }
        }
    }
}
