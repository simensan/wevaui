using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Paint {
    // Wall-clock benchmarks for the paint conversion phase. Companion to
    // the cascade and layout benchmarks: gives visibility into where
    // time goes in a full WevaDocument.Update.
    public class PaintWallClockBenchmarkTests {
        const int CardCount = 250;

        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static (Document doc, CascadeEngine engine, Box root, LayoutEngine le, LayoutContext ctx,
                Dictionary<Element, ComputedStyle> styles, BoxToPaintConverter conv, ScrollContainer scroll)
            Setup() {
            var sb = new StringBuilder();
            sb.Append("<section style=\"display:flex;flex-direction:column;\">");
            for (int i = 0; i < CardCount; i++) {
                sb.Append("<div class=\"card\" style=\"display:flex;background:#333;padding:8px;\">");
                sb.Append("<div class=\"icon\" style=\"width:32px;height:32px;background:#666;\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\" style=\"color:white;\">Card ").Append(i).Append("</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\" style=\"color:#aaa;\">").Append(i).Append("</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());

            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(".card { border-radius:4px; }") });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920,
                ViewportHeightPx = 1080,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            var conv = new BoxToPaintConverter();
            return (doc, engine, root, le, ctx, styles, conv, le.ScrollContainer);
        }

        [Test]
        public void Cold_paint_convert() {
            var (doc, engine, root, le, ctx, styles, conv, scroll) = Setup();

            const int iterations = 5;
            long totalTicks = 0;
            // Warm.
            conv.Convert(root);
            for (int i = 0; i < iterations; i++) {
                var freshConv = new BoxToPaintConverter();
                var sw = Stopwatch.StartNew();
                var paintList = freshConv.Convert(root);
                sw.Stop();
                totalTicks += sw.ElapsedTicks;
                freshConv.Return(paintList);
            }
            double avgUs = (double)(totalTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            int boxCount = engine.ResultMap.Count;
            System.Console.WriteLine($"PAINT-COLD: {avgUs:F0}µs avg over {iterations} iter, {boxCount} boxes ({avgUs / boxCount:F3}µs/box)");
            Assert.That(avgUs, Is.LessThan(50000), $"cold paint averaged {avgUs:F0}µs; ceiling 50000µs");
        }

        [Test]
        public void Warm_paint_no_changes() {
            var (doc, engine, root, le, ctx, styles, conv, scroll) = Setup();
            // Prime with tracker so subsequent calls can short-circuit.
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            var list1 = conv.Convert(root, tracker, e => FindBox(root, e), scroll, null);
            conv.Return(list1);
            tracker.Clear();

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                var pl = conv.Convert(root, tracker, e => FindBox(root, e), scroll, null);
                conv.Return(pl);
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"PAINT-WARM-NOOP: {avgUs:F1}µs over {iterations} iter");
            // Current baseline ~5.6ms — high because every box with a
            // TextRun child bypasses snapshot replay (text rendering is
            // glyph-position dependent and the snapshot doesn't capture
            // font fallback state). Real game-UI fixtures have text
            // in every card → most snapshots invalidated → tree walk
            // every frame. A real optimization target.
            Assert.That(avgUs, Is.LessThan(10000), $"warm paint averaged {avgUs:F1}µs; ceiling 10000µs");
        }

        [Test]
        public void Warm_paint_with_text_replay_after_flip() {
            // Same as Warm_paint_after_flip but with the text-subtree
            // replay opt-in enabled. Render backends with atlas tracking
            // (URP) flip this on; isolates the per-frame win on a
            // text-heavy fixture.
            var (doc, engine, root, le, ctx, styles, conv, scroll) = Setup();
            conv.AllowTextSubtreeSnapshotReplay = true;
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            var list1 = conv.Convert(root, tracker, e => FindBox(root, e), scroll, null);
            conv.Return(list1);
            tracker.Clear();

            Element targetCard = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "card" && targetCard == null) targetCard = e;
            }

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                tracker.MarkDirty(targetCard, InvalidationKind.Paint);
                var pl = conv.Convert(root, tracker, e => FindBox(root, e), scroll, null);
                conv.Return(pl);
                tracker.Clear();
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"PAINT-WARM-FLIP-TEXTREPLAY: {avgUs:F1}µs/flip over {iterations} iter");
            Assert.That(avgUs, Is.LessThan(2000), $"warm paint after flip averaged {avgUs:F1}µs; ceiling 2000µs");
        }

        [Test]
        public void Warm_paint_after_flip() {
            var (doc, engine, root, le, ctx, styles, conv, scroll) = Setup();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            var list1 = conv.Convert(root, tracker, e => FindBox(root, e), scroll, null);
            conv.Return(list1);
            tracker.Clear();

            Element targetCard = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "card" && targetCard == null) targetCard = e;
            }
            Assert.That(targetCard, Is.Not.Null);

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                tracker.MarkDirty(targetCard, InvalidationKind.Paint);
                var pl = conv.Convert(root, tracker, e => FindBox(root, e), scroll, null);
                conv.Return(pl);
                tracker.Clear();
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"PAINT-WARM-FLIP: {avgUs:F1}µs/flip over {iterations} iter");
            Assert.That(avgUs, Is.LessThan(2000), $"warm paint after flip averaged {avgUs:F1}µs; ceiling 2000µs");
        }

#if NET5_0_OR_GREATER
        [Test]
        public void Allocation_per_warm_paint() {
            // Mirror LAYOUT-WARM-ALLOCS / CASCADE-WARM-ALLOCS. Steady-state
            // alloc per warm Convert with no tracker marks.
            var (doc, engine, root, le, ctx, styles, conv, scroll) = Setup();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, Box> elementToBox = e => FindBox(root, e);
            var list1 = conv.Convert(root, tracker, elementToBox, scroll, null);
            conv.Return(list1);
            tracker.Clear();

            // Settle.
            for (int i = 0; i < 10; i++) {
                var pl = conv.Convert(root, tracker, elementToBox, scroll, null);
                conv.Return(pl);
            }
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long bytesBefore = System.GC.GetTotalAllocatedBytes(true);
            const int iterations = 100;
            for (int i = 0; i < iterations; i++) {
                var pl = conv.Convert(root, tracker, elementToBox, scroll, null);
                conv.Return(pl);
            }
            long bytesAfter = System.GC.GetTotalAllocatedBytes(true);
            long perCall = (bytesAfter - bytesBefore) / iterations;
            System.Console.WriteLine($"PAINT-WARM-ALLOCS: {perCall} bytes/call over {iterations} iter");
            Assert.That(perCall, Is.LessThan(8000),
                $"warm paint allocated {perCall}b; cap 8KB");
        }
#endif

        static Box FindBox(Box root, Element target) {
            if (root.Element == target) return root;
            for (int i = 0; i < root.Children.Count; i++) {
                var f = FindBox(root.Children[i], target);
                if (f != null) return f;
            }
            return null;
        }
    }
}
