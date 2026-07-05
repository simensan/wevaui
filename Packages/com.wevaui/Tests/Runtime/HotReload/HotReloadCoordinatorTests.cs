using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Events;
using Weva.HotReload;
using Weva.Reactive;

namespace Weva.Tests.HotReload {
    public class HotReloadCoordinatorTests {
        string tempRoot;

        [SetUp]
        public void Setup() {
            tempRoot = Path.Combine(Path.GetTempPath(), "weva-hotreload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void Teardown() {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { }
        }

        UIDocumentState BuildState(string html, params (string path, string css)[] sheets) {
            var sources = new List<string>();
            var paths = new List<string>();
            foreach (var s in sheets) {
                var p = Path.Combine(tempRoot, s.path);
                File.WriteAllText(p, s.css);
                sources.Add(s.css);
                paths.Add(p);
            }
            var b = new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = sources,
                StylesheetPaths = paths,
                MediaContext = MediaContext.Default(1024, 768),
                Clock = new FakeUIClock()
            };
            return b.Build();
        }

        [Test]
        public void Modified_rule_applies_within_one_tick() {
            var s = BuildState(
                "<main><p id='msg'>hi</p></main>",
                ("a.css", "p { color: red; }"));

            var p = s.Doc.GetElementById("msg");
            var before = s.Cascade.Compute(p, s.State);
            Assert.That(before.Get("color"), Is.EqualTo("red"));

            // Simulate a save by re-writing the file AND directly enqueueing
            // its path on the coordinator's queue (so the test does not
            // depend on FileSystemWatcher's async dispatch).
            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);

            Assert.That(coord.Tick(1.0), Is.True);
            // Fresh cascade — re-query through the coordinator's
            // replacement engine.
            var after = s.Cascade.Compute(p, s.State);
            Assert.That(after.Get("color"), Is.EqualTo("blue"));
            Assert.That(coord.ReloadCount, Is.EqualTo(1));
        }

        [Test]
        public void Removed_rule_unsticks_element() {
            var s = BuildState(
                "<main><p id='msg'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            var p = s.Doc.GetElementById("msg");
            Assert.That(s.Cascade.Compute(p, s.State).Get("color"), Is.EqualTo("red"));

            File.WriteAllText(s.StylesheetPaths[0], "");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            Assert.That(coord.Tick(1.0), Is.True);

            // No author rule: color falls back to UA default (or empty).
            var after = s.Cascade.Compute(p, s.State);
            Assert.That(after.Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Multiple_sheets_reload_independently() {
            var s = BuildState(
                "<main><p id='m'>hi</p><span id='s'>x</span></main>",
                ("a.css", "p { color: red; }"),
                ("b.css", "span { color: green; }"));
            var p = s.Doc.GetElementById("m");
            var sp = s.Doc.GetElementById("s");

            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            coord.Tick(1.0);

            Assert.That(s.Cascade.Compute(p, s.State).Get("color"), Is.EqualTo("blue"));
            Assert.That(s.Cascade.Compute(sp, s.State).Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Parse_error_keeps_previous_styles() {
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            var p = s.Doc.GetElementById("m");
            Assert.That(s.Cascade.Compute(p, s.State).Get("color"), Is.EqualTo("red"));

            // Even broken CSS doesn't actually throw with lenient parsing,
            // so we use a path-not-found scenario to simulate "broken
            // intermediate state": delete the file mid-edit.
            File.Delete(s.StylesheetPaths[0]);
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            coord.Tick(1.0);

            // Previous cascade survives the failed read.
            Assert.That(s.Cascade.Compute(p, s.State).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Reload_preserves_dom_state_attributes() {
            var s = BuildState(
                "<main><input id='i' value='preserved'/></main>",
                ("a.css", "input { color: red; }"));
            var input = s.Doc.GetElementById("i");
            input.SetAttribute("data-runtime", "live");

            File.WriteAllText(s.StylesheetPaths[0], "input { color: blue; }");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            coord.Tick(1.0);

            // Same Element instance, same attributes.
            var input2 = s.Doc.GetElementById("i");
            Assert.That(input2, Is.SameAs(input));
            Assert.That(input2.GetAttribute("value"), Is.EqualTo("preserved"));
            Assert.That(input2.GetAttribute("data-runtime"), Is.EqualTo("live"));
        }

        [Test]
        public void Tick_is_a_noop_when_queue_is_empty() {
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            Assert.That(coord.Tick(1.0), Is.False);
            Assert.That(coord.ReloadCount, Is.EqualTo(0));
        }

        [Test]
        public void Debounce_collapses_repeated_saves_within_50ms() {
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);

            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            queue.Enqueue(s.StylesheetPaths[0]);
            Assert.That(coord.Tick(1.000), Is.True);

            // A second event 10ms later should be debounced away.
            queue.Enqueue(s.StylesheetPaths[0]);
            // Note: Tick returns true if it dequeued and applied. With
            // debounce, the path is dequeued but skipped — so anyApplied
            // is false.
            Assert.That(coord.Tick(1.010), Is.False);
            Assert.That(coord.ReloadCount, Is.EqualTo(1));

            // Past the 50ms window, a real save applies again.
            File.WriteAllText(s.StylesheetPaths[0], "p { color: green; }");
            queue.Enqueue(s.StylesheetPaths[0]);
            Assert.That(coord.Tick(1.100), Is.True);
            Assert.That(coord.ReloadCount, Is.EqualTo(2));
        }

        [Test]
        public void Reload_marks_invalidation_so_layout_reruns() {
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            // Drive a first layout pass.
            UIDocumentLifecycle.Update(s, null, 0.0);
            Assert.That(s.RootBox, Is.Not.Null);
            long painterVersion = s.Painter.ContextVersion;

            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            coord.Tick(1.0);

            // After a hot-reload, RootBox should be cleared so the next
            // tick rebuilds layout from scratch.
            Assert.That(s.RootBox, Is.Null);
            // And the invalidation tracker has all elements marked dirty.
            Assert.That(s.Invalidation.HasAny(InvalidationKind.Style), Is.True);
            Assert.That(s.Painter.ContextVersion, Is.GreaterThan(painterVersion),
                "CSS hot reload must drop retained paint/subtree batches so removed animations or pseudo content cannot keep drawing");
            Assert.That(s.PaintInvalidated, Is.True);
            Assert.That(s.HasEmittedPaint, Is.False);
        }

        [Test]
        public void Unknown_path_is_ignored_and_does_not_throw() {
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(Path.Combine(tempRoot, "definitely-not-registered.css"));
            Assert.DoesNotThrow(() => coord.Tick(1.0));
            Assert.That(coord.ReloadCount, Is.EqualTo(0));
        }

        [Test]
        public void Concurrent_enqueue_during_tick_does_not_crash() {
            // Simulates the FileSystemWatcher emitting another event while
            // the coordinator is mid-Tick — the second save should be
            // picked up in a subsequent Tick, not crash.
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);

            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            queue.Enqueue(s.StylesheetPaths[0]);

            // Race-ish sequence: while coord is processing path A,
            // another Enqueue lands on the queue. The second enqueue is
            // not lost, and a Tick after the debounce window applies it.
            Assert.That(coord.Tick(1.0), Is.True);
            File.WriteAllText(s.StylesheetPaths[0], "p { color: green; }");
            queue.Enqueue(s.StylesheetPaths[0]);
            Assert.That(coord.Tick(2.0), Is.True);
            Assert.That(s.Cascade.Compute(s.Doc.GetElementById("m"), s.State).Get("color"), Is.EqualTo("green"));
        }

        // Regression: a CSS hot-reload must mark Style|Layout|Paint subtree
        // dirty AND null state.RootBox so the next Update rebuilds layout.
        // Audit caught: if MarkAllElementsDirty stops setting RootBox=null,
        // a stale layout snapshot would survive even though the cascade was
        // swapped — matching boxes would re-paint with old computed styles.
        [Test]
        public void Reload_invalidates_subtree_and_drops_root_box_so_paint_cache_misses() {
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));
            UIDocumentLifecycle.Update(s, null, 0.0);
            var rootBefore = s.RootBox;
            Assert.That(rootBefore, Is.Not.Null);

            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            coord.Tick(1.0);

            // RootBox cleared so SnapshotBoxBuilder runs fresh.
            Assert.That(s.RootBox, Is.Null);
            // Subtree marked dirty in all three flags — paint converter's Apply
            // pass will null PaintCache for every dirty element.
            var p = s.Doc.GetElementById("m");
            Assert.That(s.Invalidation.IsDirty(p, InvalidationKind.Style), Is.True);
            Assert.That(s.Invalidation.IsDirty(p, InvalidationKind.Layout), Is.True);
            Assert.That(s.Invalidation.IsDirty(p, InvalidationKind.Paint), Is.True);
        }

        // DD4 regression: a stylesheet hot-reload must invalidate the
        // CssValue negative-parse cache. Without the invalidation, a
        // previously-failed raw text stays cached as a failure for the
        // process lifetime — so if the author was iterating on a value
        // (e.g. `color: badvalue` then re-saves with the same text
        // appearing in the sheet for any other property) the cached
        // null short-circuits and the parser never retries. We seed the
        // failure directly via TryParseSilent (the path internal callers
        // use) and verify the reload drops it.
        [Test]
        public void Reload_invalidates_css_value_negative_parse_cache() {
            Weva.Css.Values.CssValue.ClearCachesForTests();
            var s = BuildState(
                "<main><p id='m'>hi</p></main>",
                ("a.css", "p { color: red; }"));

            // Seed the negative cache for a value the parser rejects.
            const string Bad = "rgb(not-a-color)";
            Assert.That(Weva.Css.Values.CssValue.TryParseSilent(Bad, out _), Is.False);
            long failedHitsBeforeReload = Weva.Css.Values.CssValue.ParseCacheFailedHits;
            Assert.That(Weva.Css.Values.CssValue.TryParseSilent(Bad, out _), Is.False);
            Assert.That(Weva.Css.Values.CssValue.ParseCacheFailedHits,
                Is.EqualTo(failedHitsBeforeReload + 1),
                "sanity: failed parse must be served from negative cache before the reload");

            // Trigger a stylesheet reload — RebuildCascade must clear
            // the negative cache. (The CSS content change here is
            // irrelevant; what matters is that the reload path runs.)
            File.WriteAllText(s.StylesheetPaths[0], "p { color: blue; }");
            var queue = new CssReloadQueue();
            var coord = new HotReloadCoordinator(s, queue);
            queue.Enqueue(s.StylesheetPaths[0]);
            Assert.That(coord.Tick(1.0), Is.True);

            // After the reload, the same bad text must re-parse (miss
            // path) rather than short-circuit through failedCache.
            long failedHitsAfterReload = Weva.Css.Values.CssValue.ParseCacheFailedHits;
            long missesBeforeRetry = Weva.Css.Values.CssValue.ParseCacheMisses;
            Assert.That(Weva.Css.Values.CssValue.TryParseSilent(Bad, out _), Is.False);
            Assert.That(Weva.Css.Values.CssValue.ParseCacheFailedHits,
                Is.EqualTo(failedHitsAfterReload),
                "after reload the negative cache must be empty — no failed-cache hit on retry");
            Assert.That(Weva.Css.Values.CssValue.ParseCacheMisses,
                Is.EqualTo(missesBeforeRetry + 1),
                "after reload the bad text must miss-then-reparse");
        }
    }
}
