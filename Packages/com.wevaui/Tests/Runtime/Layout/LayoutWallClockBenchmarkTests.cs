using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Layout {
    // Wall-clock benchmarks for the LAYOUT engine — companion to the
    // cascade benchmarks. These give us visibility into where time goes
    // in a full WevaDocument.Update cycle: cascade is now ~8ms cold for
    // 1500 elements (in Chrome range), so if total Update is still slow
    // the remainder must be layout or paint.
    public class LayoutWallClockBenchmarkTests {
        const int CardCount = 250;

        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static (Document doc, CascadeEngine engine, LayoutContext ctx, Dictionary<Element, ComputedStyle> styles, LayoutEngine le)
            Setup(int cardCount = CardCount, double viewportWidth = 1920) {
            var sb = new StringBuilder();
            sb.Append("<section style=\"display:flex;flex-direction:column;\">");
            for (int i = 0; i < cardCount; i++) {
                sb.Append("<div class=\"card\" style=\"display:flex;align-items:center;padding:8px;\">");
                sb.Append("<div class=\"icon\" style=\"width:32px;height:32px;\"></div>");
                sb.Append("<div class=\"body\" style=\"flex:1;\"><span class=\"name\">Card ").Append(i).Append("</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">").Append(i).Append("</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());

            var css = new StringBuilder();
            css.Append(".card { background: #222; gap: 8px; }");
            css.Append(".card .icon { background: #444; }");
            css.Append(".card .name { font-size: 14px; color: white; }");
            css.Append(".card .badge { color: #aaa; font-size: 11px; }");
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css.ToString()) });
            engine.ComputeAll(doc);

            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = 1080,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            return (doc, engine, ctx, styles, le);
        }

        [Test]
        public void Cold_layout_pass() {
            var (doc, engine, ctx, styles, le) = Setup();
            const int iterations = 5;
            long totalMs = 0;
            // Warm.
            le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            for (int i = 0; i < iterations; i++) {
                // Each iter uses a fresh LayoutEngine so we measure the
                // cold-cache cost.
                var freshLe = new LayoutEngine(new MonoFontMetrics());
                var sw = Stopwatch.StartNew();
                freshLe.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
                sw.Stop();
                totalMs += sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
            }
            long avgMs = totalMs / iterations;
            int elementCount = engine.ResultMap.Count;
            System.Console.WriteLine($"LAYOUT-COLD: {avgMs}ms avg over {iterations} iter, {elementCount} elements ({(double)avgMs / elementCount:F3}ms/element)");
            // Chrome layout pass: typically a few ms for a fixture this size.
            Assert.That(avgMs, Is.LessThan(200), $"cold layout pass averaged {avgMs}ms; ceiling 200ms");
        }

        [Test]
        public void Warm_layout_no_changes() {
            var (doc, engine, ctx, styles, le) = Setup();
            // Initial pass — supply a tracker so subsequent calls can
            // observe an empty dirty set and skip the rebuild.
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
            tracker.Clear();

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"LAYOUT-WARM-NOOP: {avgUs:F1}µs over {iterations} iter");
            Assert.That(avgUs, Is.LessThan(500), $"warm no-op layout averaged {avgUs:F1}µs; ceiling 500µs");
        }

        [Test]
        public void Warm_layout_after_class_flip() {
            // Real click scenario: cascade marks a single card dirty
            // (Style|Layout|Paint), layout should relayout JUST that card's
            // subtree — not the whole 1500-element tree.
            var (doc, engine, ctx, styles, le) = Setup();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
            tracker.Clear();

            // Find a card to flip.
            Element targetCard = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "card" && targetCard == null) targetCard = e;
            }
            Assert.That(targetCard, Is.Not.Null);

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                // Simulate the cascade marking dirty. We also need to rerun
                // cascade to populate styles for the new class, but for
                // pure layout perf we just mark + relayout with the same
                // styles (worst-case: a style-change pass that doesn't
                // actually change the box).
                tracker.MarkDirty(targetCard,
                    InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                tracker.Clear();
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"LAYOUT-WARM-FLIP: {avgUs:F1}µs/flip over {iterations} iter");
            // ~75µs headless after the subtree-scoped scroll/sticky + OOF-gate
            // work (was ~656µs when the incremental path walked the whole tree
            // for RepositionAbsolutes + scrollLayout.Run + sticky every flip).
            // 700µs ceiling guards against a regression back to the O(tree)
            // whole-tree sweep while leaving headroom for slower PlayMode/CI.
            Assert.That(avgUs, Is.LessThan(700), $"warm layout after class-flip averaged {avgUs:F1}µs; ceiling 700µs");
        }

#if NET5_0_OR_GREATER
        [Test]
        public void Allocation_per_noop_layout() {
            // Layout call with empty tracker — ShouldSkipLayout returns
            // true → engine returns cached lastRoot without doing work.
            // Whatever allocates here is per-call framework overhead
            // (delegate boxing, scope opening) that has nothing to do
            // with relayout work. Subtracting this from the warm-flip
            // alloc count isolates the actual incremental layout cost.
            var (doc, engine, ctx, styles, le) = Setup();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            // Settle.
            for (int i = 0; i < 10; i++) {
                le.Layout(doc, styleOf, ctx, tracker);
            }
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long bytesBefore = System.GC.GetTotalAllocatedBytes(true);
            const int iterations = 100;
            for (int i = 0; i < iterations; i++) {
                le.Layout(doc, styleOf, ctx, tracker);
            }
            long bytesAfter = System.GC.GetTotalAllocatedBytes(true);
            long perCall = (bytesAfter - bytesBefore) / iterations;
            System.Console.WriteLine($"LAYOUT-NOOP-ALLOCS: {perCall} bytes/call over {iterations} iter");
            Assert.That(perCall, Is.LessThan(1000),
                $"noop layout allocated {perCall}b; cap 1000b");
        }

        [Test]
        public void Allocation_pressure_per_warm_flip() {
            // Track GC alloc per warm flip — pure perf number, not a
            // visit-count test. Inflates with each allocation we missed
            // pooling. GC.GetTotalAllocatedBytes is .NET 5+; under Unity's
            // .NET Standard 2.1 runtime the test is excluded, but the
            // TestVerifyAll harness on .NET 8 still exercises it.
            var (doc, engine, ctx, styles, le) = Setup();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
            tracker.Clear();

            Element targetCard = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "card" && targetCard == null) targetCard = e;
            }

            // Cache the styleOf delegate so the closure isn't re-allocated
            // every iteration — that's a per-call cost the LayoutEngine
            // can't pool around, and we want to attribute alloc to the
            // engine itself.
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;

            // Settle.
            for (int i = 0; i < 5; i++) {
                tracker.MarkDirty(targetCard, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long bytesBefore = System.GC.GetTotalAllocatedBytes(true);
            const int iterations = 100;
            for (int i = 0; i < iterations; i++) {
                tracker.MarkDirty(targetCard, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }
            long bytesAfter = System.GC.GetTotalAllocatedBytes(true);
            long perCall = (bytesAfter - bytesBefore) / iterations;
            System.Console.WriteLine($"LAYOUT-WARM-ALLOCS: {perCall} bytes/flip over {iterations} iter");
            // Acceptable floor — anything over 8KB per warm flip is a leak.
            Assert.That(perCall, Is.LessThan(32_000),
                $"warm flip allocated {perCall}b; cap 32KB");
        }
#endif

#if NET5_0_OR_GREATER
        [Test]
        public void Allocation_per_markdirty_only() {
            // Just MarkDirty + Clear, no Layout call. Isolates the
            // tracker overhead from the relayout work.
            var (doc, engine, ctx, styles, le) = Setup();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            Element targetCard = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "card" && targetCard == null) targetCard = e;
            }

            // Settle.
            for (int i = 0; i < 10; i++) {
                tracker.MarkDirty(targetCard, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                tracker.Clear();
            }
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long bytesBefore = System.GC.GetTotalAllocatedBytes(true);
            const int iterations = 100;
            for (int i = 0; i < iterations; i++) {
                tracker.MarkDirty(targetCard, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                tracker.Clear();
            }
            long bytesAfter = System.GC.GetTotalAllocatedBytes(true);
            long perCall = (bytesAfter - bytesBefore) / iterations;
            System.Console.WriteLine($"TRACKER-MARK-CLEAR-ALLOCS: {perCall} bytes/cycle over {iterations} iter");
            Assert.That(perCall, Is.LessThan(200),
                $"tracker mark+clear allocated {perCall}b; cap 200b");
        }
#endif

        [Test]
        public void Warm_layout_geometry_change_animation_probe() {
            // Repro of layout-stress.html: a width-animating bar inside a small
            // .live section, sibling to a large grid. The width change fails
            // SameOuterGeometry, so we measure whether the subtree path holds or
            // bails to full layout (subtreeSkipHits tells us which), plus a
            // font-size variant. This is the "layout changes via animation"
            // cost we're trying to drive down 10x.
            var sb = new StringBuilder();
            sb.Append("<div class=\"app\">");
            sb.Append("<div class=\"live\"><div class=\"bar\"><div class=\"fill\"></div></div>");
            sb.Append("<div class=\"metric\"><span class=\"label\">CPU</span><span class=\"counter\">61%</span></div></div>");
            sb.Append("<div class=\"grid\">");
            for (int i = 0; i < 96; i++) {
                sb.Append("<div class=\"cell\"><span class=\"cn\">Cell ").Append(i).Append("</span><span class=\"cs\">ok</span></div>");
            }
            sb.Append("</div></div>");
            var doc = HtmlParser.Parse(sb.ToString());

            var css = new StringBuilder();
            css.Append(".app { display:flex; flex-direction:column; }");
            css.Append(".live { display:flex; flex-direction:column; }");
            css.Append(".bar { height:12px; overflow:hidden; }");
            css.Append(".fill { height:100%; width:50%; }");
            css.Append(".metric { display:flex; }");
            css.Append(".counter { width:72px; }");
            css.Append(".grid { display:grid; grid-template-columns:repeat(6,1fr); gap:10px; }");
            css.Append(".cell { padding:10px; display:flex; flex-direction:column; }");
            // Include the UA stylesheet so <div> resolves to display:block (the
            // CSS initial for `display` is `inline`; div→block is a UA rule).
            // Without it bare divs build as InlineBox and the test wouldn't
            // reflect how WevaDocument actually lays the sample out.
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(css.ToString()) });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                DpiPixelsPerInch = 96, Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            Element fill = null, counter = null;
            foreach (var e in engine.ResultMap.Keys) {
                var c = e.GetAttribute("class");
                if (c == "fill") fill = e;
                else if (c == "counter") counter = e;
            }
            Assert.That(fill, Is.Not.Null);
            Assert.That(counter, Is.Not.Null);
            var fillStyle = styles[fill];
            var counterStyle = styles[counter];

            const int iterations = 100;
            le.ResetSubtreeSkipStats();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                fillStyle.Set("width", (i % 2 == 0) ? "20%" : "90%");
                tracker.MarkDirty(fill, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }
            sw.Stop();
            double widthUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            long widthSkips = le.SubtreeSkipHits;

            le.ResetSubtreeSkipStats();
            sw.Restart();
            for (int i = 0; i < iterations; i++) {
                counterStyle.Set("font-size", (i % 2 == 0) ? "13px" : "21px");
                tracker.MarkDirty(counter, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }
            sw.Stop();
            double fontUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            long fontSkips = le.SubtreeSkipHits;

            System.Console.WriteLine($"LAYOUT-WIDTH-ANIM:  {widthUs:F1}µs/flip subtreeSkipHits={widthSkips}/{iterations}");
            System.Console.WriteLine($"LAYOUT-FONT-ANIM:   {fontUs:F1}µs/flip subtreeSkipHits={fontSkips}/{iterations}");
            // A clipped element animating its width (progress bar / fill /
            // slider — the dominant layout-animation pattern) MUST stay on the
            // subtree path by bubbling one level to its clipping parent, instead
            // of full-layouting the whole document every frame (~6900µs → ~18µs
            // headless). Guard both the path and the cost.
            Assert.That(widthSkips, Is.GreaterThan(90),
                $"clipped width animation must stay incremental; only {widthSkips}/{iterations} took the subtree path");
            Assert.That(widthUs, Is.LessThan(1000),
                $"clipped width animation flip averaged {widthUs:F1}µs; ceiling 1000µs (was ~6900µs full-layout-per-frame)");
            // NOTE: the font-size case (inline content reflowing a flex/grid line
            // up to a sibling grid) genuinely propagates and falls back to full
            // layout ({fontSkips}/{iterations} subtree) — not guarded here; that
            // would need incremental flex/grid line/track relayout.
        }

        static Weva.Layout.Boxes.Box FindBoxByClass(Weva.Layout.Boxes.Box root, string cls) {
            if (root == null) return null;
            if (root.Element != null && root.Element.GetAttribute("class") == cls) return root;
            for (int i = 0; i < root.Children.Count; i++) {
                var f = FindBoxByClass(root.Children[i], cls);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Subtree_relayout_matches_full_relayout_for_padding_animation() {
            // Regression: after the bubble-skip fix, a `.pad` padding animation
            // takes the subtree path instead of full layout. The subtree path
            // MUST produce geometry identical to a full relayout — a desync
            // shows up live as the centred value text drifting inside a frame
            // that stays put. This pins the subtree path against a fresh full
            // layout at the same animated value.
            string Markup() {
                var sb = new StringBuilder();
                sb.Append("<div class=\"live\"><div class=\"pads\">");
                for (int i = 0; i < 4; i++) {
                    sb.Append("<div class=\"pad\"><span class=\"pad-k\">K").Append(i)
                      .Append("</span><span class=\"pad-v\">V").Append(i).Append("</span></div>");
                }
                sb.Append("</div></div>");
                return sb.ToString();
            }
            string Css(string pad) {
                var sb = new StringBuilder();
                sb.Append("* { box-sizing: border-box; }");
                sb.Append(".live { display:flex; flex-direction:column; }");
                sb.Append(".pads { display:flex; gap:10px; }");
                sb.Append(".pad { flex:1; height:64px; display:flex; flex-direction:column; ")
                  .Append("justify-content:center; align-items:center; gap:4px; padding:").Append(pad).Append("; }");
                sb.Append(".pad-k { font-size:11px; }");
                sb.Append(".pad-v { font-size:16px; font-weight:800; }");
                return sb.ToString();
            }

            (LayoutEngine le, Weva.Layout.Boxes.Box root) FullLayout(string pad) {
                var d = HtmlParser.Parse(Markup());
                var eng = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css(pad)) });
                eng.ComputeAll(d);
                var st = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in eng.ResultMap) st[kv.Key] = kv.Value;
                var c = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                    DpiPixelsPerInch = 96, Snapshot = eng.LastSnapshot, SnapshotStyles = eng.Styles,
                };
                var engine = new LayoutEngine(new MonoFontMetrics());
                var r = engine.Layout(d, e => st.TryGetValue(e, out var cs) ? cs : null, c);
                return (engine, r);
            }

            // Subtree path: lay out at 6px, then flip one pad's padding to 22px
            // and relayout through the production bubble (MarkLayoutForElement).
            var doc = HtmlParser.Parse(Markup());
            var engineC = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css("6px")) });
            engineC.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engineC.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                DpiPixelsPerInch = 96, Snapshot = engineC.LastSnapshot, SnapshotStyles = engineC.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            // Flip every pad's padding to 22px (mirrors all four animating).
            foreach (var e in engineC.ResultMap.Keys) {
                if (e.GetAttribute("class") == "pad") {
                    // Set the LONGHANDS (mirrors an expanded `padding` keyframe
                    // sample — ApplyBoxModel reads padding-top/left, not the
                    // shorthand slot).
                    styles[e].Set("padding-top", "22px");
                    styles[e].Set("padding-right", "22px");
                    styles[e].Set("padding-bottom", "22px");
                    styles[e].Set("padding-left", "22px");
                    tracker.MarkLayoutForElement(e, styleOf);
                    tracker.MarkDirty(e, InvalidationKind.Paint);
                }
            }
            // Mirror the live animation path: UIDocumentLifecycle nulls
            // SnapshotStyles while compositions are active so layout reads the
            // freshly-animated ComputedStyle via styleOf instead of the stale
            // cascade StyleArray. Without this the subtree build would consult
            // the unchanged snapshot and lay out with the OLD padding.
            ctx.SnapshotStyles = null;
            var subtreeRoot = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            var (_, fullRoot) = FullLayout("22px");

            var padV_sub = FindBoxByClass(subtreeRoot, "pad-v");
            var padV_full = FindBoxByClass(fullRoot, "pad-v");
            var pad_sub = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(subtreeRoot, "pad");
            var pad_full = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(fullRoot, "pad");
            Assert.That(padV_sub, Is.Not.Null);
            Assert.That(padV_full, Is.Not.Null);
            // The centred value text's Y inside its pad must match a full layout.
            Assert.That(padV_sub.Y, Is.EqualTo(padV_full.Y).Within(0.5),
                $"pad-v Y desync: subtree={padV_sub.Y:F2} full={padV_full.Y:F2} (pad H sub={pad_sub.Height:F2} full={pad_full.Height:F2})");
            Assert.That(pad_sub.Height, Is.EqualTo(pad_full.Height).Within(0.5),
                $"pad height desync: subtree={pad_sub.Height:F2} full={pad_full.Height:F2}");
        }

        static string FirstGeometryDivergence(Weva.Layout.Boxes.Box a, Weva.Layout.Boxes.Box b, string path) {
            if (a == null || b == null) {
                if (a == b) return null;
                return $"{path}: one null (a={(a==null?"null":a.GetType().Name)} b={(b==null?"null":b.GetType().Name)})";
            }
            string lbl = a.Element != null ? a.Element.TagName + "." + a.Element.GetAttribute("class") : a.GetType().Name;
            string p = path + "/" + lbl;
            double tol = 0.5;
            if (System.Math.Abs(a.X - b.X) > tol || System.Math.Abs(a.Y - b.Y) > tol
                || System.Math.Abs(a.Width - b.Width) > tol || System.Math.Abs(a.Height - b.Height) > tol) {
                return $"{p}: subtree(X={a.X:F1} Y={a.Y:F1} W={a.Width:F1} H={a.Height:F1}) != full(X={b.X:F1} Y={b.Y:F1} W={b.Width:F1} H={b.Height:F1})";
            }
            if (a.Children.Count != b.Children.Count) {
                return $"{p}: child count {a.Children.Count} != {b.Children.Count}";
            }
            for (int i = 0; i < a.Children.Count; i++) {
                var d = FirstGeometryDivergence(a.Children[i], b.Children[i], p);
                if (d != null) return d;
            }
            return null;
        }

        [Test]
        public void Scroll_boundary_reuse_corrects_on_width_change() {
            // Exercises the self-correcting path: the scroll container's width is
            // NOT stable — it's a flex:1 sibling of a font-size-animating auto-
            // width box in a ROW flex, so growing the sibling shrinks the scroll
            // container. Reuse grafts optimistically, validation detects the width
            // change, and re-lays the subtree — result must still match no-reuse.
            string Markup() {
                var sb = new StringBuilder();
                sb.Append("<div class=\"outer\"><div class=\"gw\"><div class=\"grid\">");
                for (int i = 0; i < 24; i++)
                    sb.Append("<div class=\"cell\"><span class=\"cn\">Item ").Append(i).Append("</span></div>");
                sb.Append("</div></div></div>");
                return sb.ToString();
            }
            string Css(string w) {
                var sb = new StringBuilder();
                sb.Append("* { box-sizing:border-box; }");
                // .outer's WIDTH animates → a full layout (its outer geometry
                // changes) → .gw (width:100% of .outer) changes width too. .gw is
                // clean (the dirty node is its ANCESTOR), so it is grafted
                // optimistically and then must be width-corrected.
                sb.Append(".outer { width:").Append(w).Append("; }");
                sb.Append(".gw { width:100%; height:300px; overflow-y:auto; }");
                sb.Append(".grid { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; }");
                sb.Append(".cell { padding:8px; } .cn { font-size:12px; }");
                return sb.ToString();
            }
            Weva.Layout.Boxes.Box FullLayoutNoReuse(string w) {
                var d = HtmlParser.Parse(Markup());
                var eng = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css(w)) });
                eng.ComputeAll(d);
                var st = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in eng.ResultMap) st[kv.Key] = kv.Value;
                var c = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = eng.LastSnapshot, SnapshotStyles = eng.Styles,
                };
                return new LayoutEngine(new MonoFontMetrics()).Layout(d, e => st.TryGetValue(e, out var cs) ? cs : null, c);
            }

            bool savedFlag = LayoutEngine.EnableScrollBoundaryReuse;
            try {
                LayoutEngine.EnableScrollBoundaryReuse = true;
                var doc = HtmlParser.Parse(Markup());
                var engineC = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css("200px")) });
                engineC.ComputeAll(doc);
                var styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engineC.ResultMap) styles[kv.Key] = kv.Value;
                var ctx = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = engineC.LastSnapshot, SnapshotStyles = engineC.Styles,
                };
                var le = new LayoutEngine(new MonoFontMetrics());
                var tracker = new InvalidationTracker();
                tracker.Attach(doc);
                System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();

                foreach (var e in engineC.ResultMap.Keys) {
                    if (e.GetAttribute("class") == "outer") {
                        styles[e].Set("width", "500px"); // .outer widens → .gw (width:100%) widens → grid re-lays
                        tracker.MarkLayoutForElement(e, styleOf);
                        tracker.MarkDirty(e, InvalidationKind.Paint);
                    }
                }
                // Force the FULL-layout path (so the scroll-reuse graft runs) the
                // way a structural change elsewhere would, while .gw stays clean.
                tracker.MarkDirty(doc, InvalidationKind.Structure);
                ctx.SnapshotStyles = null;
                var reuseRoot = le.Layout(doc, styleOf, ctx, tracker);
                int corrects = le.LastScrollReuseCorrectCount;
                tracker.Clear();

                LayoutEngine.EnableScrollBoundaryReuse = false;
                var freshRoot = FullLayoutNoReuse("500px");

                var divergence = FirstGeometryDivergence(reuseRoot, freshRoot, "");
                Assert.That(divergence, Is.Null, $"corrected reuse vs no-reuse divergence: {divergence}");
                Assert.That(corrects, Is.GreaterThanOrEqualTo(1),
                    "the .gw width changed, so the optimistic graft MUST have been corrected");
            } finally {
                LayoutEngine.EnableScrollBoundaryReuse = savedFlag;
            }
        }

        [Test]
        public void Incremental_height_propagation_matches_full_layout() {
            // The genuine CPU fix: a font-size-animating counter changes .metric
            // HEIGHT, which propagates up the column-flex chain. Instead of a full
            // layout, the incremental path splices .metric and pushes the delta up
            // (grow auto .live, then the definite .app's flex-grow grid-wrap
            // absorbs + reuses its subtree). Result must equal a full layout, and
            // it must stay OFF the full-layout path.
            string Markup() {
                var sb = new StringBuilder();
                sb.Append("<div class=\"app\">");
                sb.Append("<div class=\"live\"><div class=\"metric\"><span class=\"label\">CPU</span><span class=\"counter\">61%</span></div></div>");
                sb.Append("<div class=\"gw\"><div class=\"grid\">");
                for (int i = 0; i < 24; i++)
                    sb.Append("<div class=\"cell\"><span class=\"cn\">Cell ").Append(i).Append("</span></div>");
                sb.Append("</div></div></div>");
                return sb.ToString();
            }
            string Css(string fs) {
                var sb = new StringBuilder();
                sb.Append("* { box-sizing:border-box; }");
                sb.Append(".app { display:flex; flex-direction:column; height:600px; }");
                sb.Append(".live { display:flex; flex-direction:column; }");
                sb.Append(".metric { display:flex; align-items:center; gap:12px; }");
                sb.Append(".label { font-size:12px; }");
                sb.Append(".counter { width:72px; font-weight:800; font-size:").Append(fs).Append("; }");
                sb.Append(".gw { flex:1; overflow-y:auto; }");
                sb.Append(".grid { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; }");
                sb.Append(".cell { padding:8px; }");
                return sb.ToString();
            }
            Weva.Layout.Boxes.Box FullLayout(string fs) {
                var d = HtmlParser.Parse(Markup());
                var eng = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css(fs)) });
                eng.ComputeAll(d);
                var st = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in eng.ResultMap) st[kv.Key] = kv.Value;
                var c = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1280, ViewportHeightPx = 800, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = eng.LastSnapshot, SnapshotStyles = eng.Styles,
                };
                return new LayoutEngine(new MonoFontMetrics()).Layout(d, e => st.TryGetValue(e, out var cs) ? cs : null, c);
            }

            bool savedProp = LayoutEngine.EnableIncrementalHeightPropagation;
            try {
                LayoutEngine.EnableIncrementalHeightPropagation = true;
                var doc = HtmlParser.Parse(Markup());
                var engineC = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css("13px")) });
                engineC.ComputeAll(doc);
                var styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engineC.ResultMap) styles[kv.Key] = kv.Value;
                var ctx = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1280, ViewportHeightPx = 800, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = engineC.LastSnapshot, SnapshotStyles = engineC.Styles,
                };
                var le = new LayoutEngine(new MonoFontMetrics());
                var tracker = new InvalidationTracker();
                tracker.Attach(doc);
                System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();

                foreach (var e in engineC.ResultMap.Keys) {
                    if (e.GetAttribute("class") == "counter") {
                        styles[e].Set("font-size", "21px");
                        tracker.MarkLayoutForElement(e, styleOf);
                        tracker.MarkDirty(e, InvalidationKind.Paint);
                    }
                }
                ctx.SnapshotStyles = null;
                le.CollectStageTimings = true;
                var incRoot = le.Layout(doc, styleOf, ctx, tracker);
                var path = le.LastPath;
                tracker.Clear();

                var fullRoot = FullLayout("21px");
                var divergence = FirstGeometryDivergence(incRoot, fullRoot, "");
                Assert.That(divergence, Is.Null, $"incremental height-propagation vs full divergence: {divergence}");
                Assert.That(path, Is.EqualTo(LayoutEngine.LayoutPath.Subtree),
                    "counter font-size change must stay on the incremental (subtree) path via height propagation, not fall back to FULL");
            } finally {
                LayoutEngine.EnableIncrementalHeightPropagation = savedProp;
            }
        }

        [Test]
        public void Scroll_boundary_reuse_matches_full_layout() {
            // A font-size-animating counter (genuinely propagating → full layout)
            // sits beside a large grid inside an overflow-y:auto wrapper. With
            // scroll-boundary reuse ON, the full layout should GRAFT the grid's
            // prior subtree (its width is unchanged) instead of re-laying it — and
            // the result must be byte-for-byte identical to a no-reuse full layout.
            string Markup() {
                var sb = new StringBuilder();
                sb.Append("<div class=\"app\">");
                sb.Append("<div class=\"live\"><div class=\"metric\"><span class=\"label\">CPU</span><span class=\"counter\">61%</span></div></div>");
                sb.Append("<div class=\"gw\"><div class=\"grid\">");
                for (int i = 0; i < 48; i++)
                    sb.Append("<div class=\"cell\"><span class=\"cn\">Cell ").Append(i).Append("</span><span class=\"cs\">ok</span></div>");
                sb.Append("</div></div></div>");
                return sb.ToString();
            }
            string Css(string fs) {
                var sb = new StringBuilder();
                sb.Append("* { box-sizing:border-box; }");
                sb.Append(".app { display:flex; flex-direction:column; height:600px; }");
                sb.Append(".live { display:flex; flex-direction:column; }");
                sb.Append(".metric { display:flex; align-items:center; gap:12px; }");
                sb.Append(".label { font-size:12px; }");
                sb.Append(".counter { width:72px; font-weight:800; font-size:").Append(fs).Append("; }");
                sb.Append(".gw { flex:1; overflow-y:auto; }");
                sb.Append(".grid { display:grid; grid-template-columns:repeat(6,1fr); gap:10px; }");
                sb.Append(".cell { padding:10px; display:flex; flex-direction:column; gap:6px; }");
                sb.Append(".cn { font-size:12px; } .cs { font-size:11px; }");
                return sb.ToString();
            }
            Weva.Layout.Boxes.Box FullLayoutNoReuse(string fs) {
                var d = HtmlParser.Parse(Markup());
                var eng = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css(fs)) });
                eng.ComputeAll(d);
                var st = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in eng.ResultMap) st[kv.Key] = kv.Value;
                var c = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = eng.LastSnapshot, SnapshotStyles = eng.Styles,
                };
                return new LayoutEngine(new MonoFontMetrics()).Layout(d, e => st.TryGetValue(e, out var cs) ? cs : null, c);
            }

            bool savedFlag = LayoutEngine.EnableScrollBoundaryReuse;
            bool savedProp = LayoutEngine.EnableIncrementalHeightPropagation;
            try {
                LayoutEngine.EnableScrollBoundaryReuse = true;
                // This test exercises the FULL-layout scroll-boundary GRAFT, so
                // disable height propagation (which would otherwise keep the
                // counter change on the incremental path and never reach the
                // graft). The two are alternative accelerations of the same case.
                LayoutEngine.EnableIncrementalHeightPropagation = false;
                var doc = HtmlParser.Parse(Markup());
                var engineC = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css("13px")) });
                engineC.ComputeAll(doc);
                var styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engineC.ResultMap) styles[kv.Key] = kv.Value;
                var ctx = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = engineC.LastSnapshot, SnapshotStyles = engineC.Styles,
                };
                var le = new LayoutEngine(new MonoFontMetrics());
                var tracker = new InvalidationTracker();
                tracker.Attach(doc);
                System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
                le.Layout(doc, styleOf, ctx, tracker); // baseline @13 → populates lastRoot + cache
                tracker.Clear();

                foreach (var e in engineC.ResultMap.Keys) {
                    if (e.GetAttribute("class") == "counter") {
                        styles[e].Set("font-size", "21px");
                        tracker.MarkLayoutForElement(e, styleOf);
                        tracker.MarkDirty(e, InvalidationKind.Paint);
                    }
                }
                ctx.SnapshotStyles = null;
                var reuseRoot = le.Layout(doc, styleOf, ctx, tracker);
                int grafts = le.LastScrollReuseGraftCount;
                int corrects = le.LastScrollReuseCorrectCount;
                tracker.Clear();

                LayoutEngine.EnableScrollBoundaryReuse = false;
                var freshRoot = FullLayoutNoReuse("21px");

                var divergence = FirstGeometryDivergence(reuseRoot, freshRoot, "");
                Assert.That(divergence, Is.Null, $"reuse vs no-reuse divergence: {divergence}");
                Assert.That(grafts, Is.GreaterThanOrEqualTo(1),
                    "the .gw scroll container should have been grafted (reused), not re-laid");
                Assert.That(corrects, Is.EqualTo(0),
                    "grid width is unchanged, so no width-mismatch correction should have been needed");
            } finally {
                LayoutEngine.EnableScrollBoundaryReuse = savedFlag;
                LayoutEngine.EnableIncrementalHeightPropagation = savedProp;
            }
        }

        [Test]
        public void Subtree_relayout_matches_full_for_combined_live_animation() {
            // Faithful layout-stress `.live` repro: metrics with width-animating
            // bar-fills AND font-size counters, plus padding-pulsing pads — all
            // animating in the SAME frame, then compared box-by-box against a
            // fresh full layout. This is the configuration the user reported
            // breaking ("counter text increase pushes text, frames stay").
            string Markup() {
                var sb = new StringBuilder();
                sb.Append("<div class=\"live\">");
                for (int i = 0; i < 4; i++) {
                    sb.Append("<div class=\"metric\"><span class=\"label\">L").Append(i)
                      .Append("</span><div class=\"bar\"><div class=\"fill\"></div></div>")
                      .Append("<span class=\"counter\">61%</span></div>");
                }
                sb.Append("<div class=\"pads\">");
                for (int i = 0; i < 4; i++)
                    sb.Append("<div class=\"pad\"><span class=\"pad-k\">K").Append(i)
                      .Append("</span><span class=\"pad-v\">V").Append(i).Append("</span></div>");
                sb.Append("</div></div>");
                return sb.ToString();
            }
            string Css(string fill, string counterFs, string pad) {
                var sb = new StringBuilder();
                sb.Append("* { box-sizing:border-box; }");
                sb.Append(".live { display:flex; flex-direction:column; gap:10px; }");
                sb.Append(".metric { display:flex; align-items:center; gap:12px; }");
                sb.Append(".label { font-size:12px; }");
                sb.Append(".bar { flex:1; height:12px; overflow:hidden; }");
                sb.Append(".fill { height:100%; width:").Append(fill).Append("; }");
                sb.Append(".counter { width:72px; text-align:right; font-weight:800; font-size:").Append(counterFs).Append("; }");
                sb.Append(".pads { display:flex; gap:10px; }");
                sb.Append(".pad { flex:1; height:64px; display:flex; flex-direction:column; justify-content:center; align-items:center; gap:4px; padding:").Append(pad).Append("; }");
                sb.Append(".pad-k { font-size:11px; } .pad-v { font-size:16px; font-weight:800; }");
                return sb.ToString();
            }
            Weva.Layout.Boxes.Box FullLayout(string fill, string fs, string pad) {
                var d = HtmlParser.Parse(Markup());
                var eng = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css(fill, fs, pad)) });
                eng.ComputeAll(d);
                var st = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in eng.ResultMap) st[kv.Key] = kv.Value;
                var c = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                    DpiPixelsPerInch = 96, Snapshot = eng.LastSnapshot, SnapshotStyles = eng.Styles,
                };
                return new LayoutEngine(new MonoFontMetrics()).Layout(d, e => st.TryGetValue(e, out var cs) ? cs : null, c);
            }

            var doc = HtmlParser.Parse(Markup());
            var engineC = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css("50%", "13px", "6px")) });
            engineC.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engineC.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                DpiPixelsPerInch = 96, Snapshot = engineC.LastSnapshot, SnapshotStyles = engineC.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            // Run MANY alternating animation flips (mirrors the live continuous
            // animation accumulating splices over hundreds of frames), ending on
            // a known state, then compare to a clean full layout at that state.
            Weva.Layout.Boxes.Box subtreeRoot = null;
            for (int iter = 0; iter < 40; iter++) {
                bool hi = (iter % 2) == 0; // end (iter 39, odd) on the LOW state
                string fillW = hi ? "90%" : "30%";
                string fs = hi ? "21px" : "13px";
                string padV = hi ? "22px" : "6px";
                foreach (var e in engineC.ResultMap.Keys) {
                    string cls = e.GetAttribute("class");
                    if (cls == "fill") { styles[e].Set("width", fillW); tracker.MarkLayoutForElement(e, styleOf); tracker.MarkDirty(e, InvalidationKind.Paint); }
                    else if (cls == "counter") { styles[e].Set("font-size", fs); tracker.MarkLayoutForElement(e, styleOf); tracker.MarkDirty(e, InvalidationKind.Paint); }
                    else if (cls == "pad") {
                        foreach (var lh in new[]{"padding-top","padding-right","padding-bottom","padding-left"}) styles[e].Set(lh, padV);
                        tracker.MarkLayoutForElement(e, styleOf); tracker.MarkDirty(e, InvalidationKind.Paint);
                    }
                }
                ctx.SnapshotStyles = null;
                subtreeRoot = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }
            // iter 39 is odd → low state.
            var fullRoot = FullLayout("30%", "13px", "6px");

            var divergence = FirstGeometryDivergence(subtreeRoot, fullRoot, "");
            Assert.That(divergence, Is.Null, $"subtree/full geometry divergence: {divergence}");
        }

        [Test]
        public void Subtree_relayout_matches_full_relayout_for_fontsize_animation() {
            // The user's reported regression was "text increase pushes text but
            // not the frames" — i.e. a font-size animation. Mirror layout-stress:
            // a `.counter` (font-size animating) inside a `.metric` flex row, with
            // a SIBLING metric below it, all inside a column `.live`. Verify the
            // subtree relayout produces the same geometry as a full relayout —
            // both the counter's own position AND the following sibling's Y (which
            // moves iff the metric's height actually changed).
            string Markup() {
                var sb = new StringBuilder();
                sb.Append("<div class=\"live\">");
                sb.Append("<div class=\"metric\"><span class=\"label\">CPU</span><span class=\"counter\">61%</span></div>");
                sb.Append("<div class=\"metric2\"><span class=\"label\">Mem</span><span class=\"counter\">47%</span></div>");
                sb.Append("</div>");
                return sb.ToString();
            }
            string Css(string fontSize) {
                var sb = new StringBuilder();
                sb.Append("* { box-sizing: border-box; }");
                sb.Append(".live { display:flex; flex-direction:column; gap:10px; }");
                sb.Append(".metric, .metric2 { display:flex; align-items:center; gap:12px; }");
                sb.Append(".label { font-size:12px; }");
                sb.Append(".counter { width:72px; font-weight:800; font-size:").Append(fontSize).Append("; }");
                return sb.ToString();
            }
            (LayoutEngine le, Weva.Layout.Boxes.Box root) FullLayout(string fs) {
                var d = HtmlParser.Parse(Markup());
                var eng = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css(fs)) });
                eng.ComputeAll(d);
                var st = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in eng.ResultMap) st[kv.Key] = kv.Value;
                var c = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                    DpiPixelsPerInch = 96, Snapshot = eng.LastSnapshot, SnapshotStyles = eng.Styles,
                };
                var engine = new LayoutEngine(new MonoFontMetrics());
                var r = engine.Layout(d, e => st.TryGetValue(e, out var cs) ? cs : null, c);
                return (engine, r);
            }

            var doc = HtmlParser.Parse(Markup());
            var engineC = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(Css("13px")) });
            engineC.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engineC.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                DpiPixelsPerInch = 96, Snapshot = engineC.LastSnapshot, SnapshotStyles = engineC.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            foreach (var e in engineC.ResultMap.Keys) {
                if (e.GetAttribute("class") == "counter") {
                    styles[e].Set("font-size", "21px");
                    tracker.MarkLayoutForElement(e, styleOf);
                    tracker.MarkDirty(e, InvalidationKind.Paint);
                }
            }
            ctx.SnapshotStyles = null; // live animation path nulls this
            var subtreeRoot = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();
            var (_, fullRoot) = FullLayout("21px");

            var m2_sub = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(subtreeRoot, "metric2");
            var m2_full = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(fullRoot, "metric2");
            var c1_sub = FindBoxByClass(subtreeRoot, "counter");
            var c1_full = FindBoxByClass(fullRoot, "counter");
            // The second metric's Y reflects whether the first metric's height
            // change propagated. Subtree and full MUST agree.
            Assert.That(m2_sub.Y, Is.EqualTo(m2_full.Y).Within(0.5),
                $"sibling metric Y desync after font-size grow: subtree={m2_sub.Y:F2} full={m2_full.Y:F2}");
            Assert.That(c1_sub.Y, Is.EqualTo(c1_full.Y).Within(0.5),
                $"counter Y desync: subtree={c1_sub.Y:F2} full={c1_full.Y:F2}");
            Assert.That(m2_sub.Height, Is.EqualTo(m2_full.Height).Within(0.5),
                $"metric height desync: subtree={m2_sub.Height:F2} full={m2_full.Height:F2}");
        }

        [Test]
        public void Height_only_viewport_change_relayouts() {
            // Repro of "UI doesn't resize in Y unless I also resize width": a
            // viewport HEIGHT-only change must re-resolve vh AND %-height chains.
            // Runs with scroll-boundary reuse at its DEFAULT (on) — the regression
            // was reuse grafting the html/body scroll containers (UA overflow)
            // across a viewport resize, freezing their viewport-height-dependent
            // content. Layout now disables reuse on any viewport change.
            var doc = HtmlParser.Parse("<div class=\"app\"><div class=\"vh\"></div><div class=\"pct\"></div></div>");
            var css = "html,body{height:100%;margin:0;} .app{height:100%;} .vh{height:100vh;} .pct{height:50%;}";
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(css) });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 600, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            var root0 = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            var app = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(root0, "app");
            var vh = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(root0, "vh");
            var pct = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(root0, "pct");
            Assert.That(app.Height, Is.EqualTo(600).Within(0.5), "app height @600 viewport");
            Assert.That(vh.Height, Is.EqualTo(600).Within(0.5), "vh height @600 viewport");
            Assert.That(pct.Height, Is.EqualTo(300).Within(0.5), "pct(50%) height @600 viewport");

            // HEIGHT-ONLY change (width stays 800).
            ctx.ViewportHeightPx = 900;
            tracker.MarkDirty(doc, InvalidationKind.Layout | InvalidationKind.Paint);
            var root1 = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            app = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(root1, "app");
            vh = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(root1, "vh");
            pct = (Weva.Layout.Boxes.BlockBox)FindBoxByClass(root1, "pct");
            Assert.That(vh.Height, Is.EqualTo(900).Within(0.5), "vh height did NOT track height-only viewport change");
            Assert.That(app.Height, Is.EqualTo(900).Within(0.5), "app(100%) height did NOT track height-only viewport change");
            Assert.That(pct.Height, Is.EqualTo(450).Within(0.5), "pct(50%) height did NOT track height-only viewport change");
        }

        [Test]
        public void Clipped_width_animation_with_ancestor_bubble_stays_incremental() {
            // Reproduces the LIVE layout-stress regression that the plain
            // Warm_layout_geometry_change probe missed: in production the
            // animation runner marks layout dirty via
            // InvalidationTracker.MarkLayoutForElement, which bubbles a Layout
            // mark up EVERY ancestor to the nearest width+height-pinned boundary
            // (the document root, when none pins both). Before the bubble-skip
            // fix, the subtree path seeded a candidate from each bubbled
            // ancestor and AddSubtreeDirtyCandidate collapsed them to <html>, so
            // RelayoutOneSubtree(<html>) rebuilt the whole 700-node tree every
            // frame (logged live as `first=<html> subtreeNodes=719 commit~52ms`).
            // A clipped progress-bar fill must stay LOCAL despite that bubble.
            var sb = new StringBuilder();
            sb.Append("<div class=\"app\">");
            sb.Append("<div class=\"live\"><div class=\"bar\"><div class=\"fill\"></div></div></div>");
            sb.Append("<div class=\"grid\">");
            for (int i = 0; i < 96; i++) {
                sb.Append("<div class=\"cell\"><span class=\"cn\">Cell ").Append(i).Append("</span></div>");
            }
            sb.Append("</div></div>");
            var doc = HtmlParser.Parse(sb.ToString());

            var css = new StringBuilder();
            css.Append(".app { display:flex; flex-direction:column; }");
            css.Append(".live { display:flex; flex-direction:column; }");
            css.Append(".bar { height:12px; overflow:hidden; }");
            css.Append(".fill { height:100%; width:50%; }");
            css.Append(".grid { display:grid; grid-template-columns:repeat(6,1fr); gap:10px; }");
            css.Append(".cell { padding:10px; display:flex; flex-direction:column; }");
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { UserAgentStylesheet.Parse(), Author(css.ToString()) });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920, ViewportHeightPx = 1080, RootFontSizePx = 16,
                DpiPixelsPerInch = 96, Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics()) { CollectStageTimings = true };
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            Element fill = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "fill") { fill = e; break; }
            }
            Assert.That(fill, Is.Not.Null);
            var fillStyle = styles[fill];

            le.ResetSubtreeSkipStats();
            for (int i = 0; i < 10; i++) {
                fillStyle.Set("width", (i % 2 == 0) ? "20%" : "90%");
                // The production path: bubble the Layout mark up the ancestor
                // chain exactly as CssAnimationRunner.MarkDirty does.
                tracker.MarkLayoutForElement(fill, styleOf);
                tracker.MarkDirty(fill, InvalidationKind.Paint);
                le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }

            // The bubble reaches <html>, but the clipped fill must stay local:
            // the subtree path is taken (not collapsed to a whole-tree rebuild),
            // and the dirty candidate is NOT the document root.
            Assert.That(le.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Subtree),
                "clipped width animation must stay on the subtree path despite ancestor bubbling");
            Assert.That(le.LastDirtyLabel, Does.Not.Contain("html"),
                $"subtree candidate collapsed to the root (<{le.LastDirtyLabel}>, {le.LastSubtreeNodeCount} nodes) — the bubble-skip failed");
            Assert.That(le.LastSubtreeNodeCount, Is.LessThan(20),
                $"rebuilt {le.LastSubtreeNodeCount}-node subtree; a clipped fill flip should touch a handful of nodes, not the whole tree");
        }

        [Test]
        public void Small_doc_layout() {
            // Single small card. Floor measurement.
            var (doc, engine, ctx, styles, le) = Setup(cardCount: 1);
            le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                var freshLe = new LayoutEngine(new MonoFontMetrics());
                freshLe.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"LAYOUT-SMALL: {avgUs:F1}µs/cold over {iterations} iter");
            Assert.That(avgUs, Is.LessThan(2000), $"small doc cold layout averaged {avgUs:F1}µs; ceiling 2000µs");
        }
    }
}
