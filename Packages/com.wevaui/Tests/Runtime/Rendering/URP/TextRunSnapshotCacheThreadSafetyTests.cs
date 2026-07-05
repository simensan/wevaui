using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Diagnostics;
using Weva.Paint;
using Weva.Rendering.URP;
using FontStyle = Weva.Paint.FontStyle;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // RC6 — TextRunSnapshotCache.Store is single-threaded by Unity main-thread
    // convention. The soft-cap `cache.Clear()` at the top of Store is the most
    // dangerous mutation in the file — a concurrent reader would observe a
    // partially-cleared dictionary. Store now asserts main-thread via
    // UIMainThreadGuard.
    public class TextRunSnapshotCacheThreadSafetyTests {
        [SetUp]
        public void ResetCache() {
            TextRunSnapshotCache.Clear();
        }

        [TearDown]
        public void TearDown() {
            TextRunSnapshotCache.Clear();
        }

        static DrawTextCommand MakeCommand() {
            var bounds = new PaintRect(0, 0, 100, 16);
            var font = new FontHandle("sans-serif", 14, 400, FontStyle.Normal);
            return new DrawTextCommand(bounds, "Hi", font, LinearColor.White, TextDecoration.None);
        }

        [Test]
        public void Store_on_main_thread_succeeds_RC6() {
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId);
            try {
                var cmd = MakeCommand();
                var key = new TextRunSnapshotKey(cmd);
                TextRunSnapshotCache.Store(in key, new List<SdfGlyphQuad>(), 1, cmd);
                Assert.That(TextRunSnapshotCache.Count, Is.EqualTo(1));
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                TextRunSnapshotCache.Clear();
            }
        }

#if UNITY_EDITOR
        [Test]
        public void Store_off_main_thread_fires_assertion_RC6() {
            // RC-1: positive non-colliding wrong-id avoids AssertMainThread's
            // `captured < 0` early-exit. See CSS_OPEN_GAPS.md RC-1 history.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId + 100_000);
            try {
                var cmd = MakeCommand();
                var key = new TextRunSnapshotKey(cmd);
                LogAssert.Expect(LogType.Assert,
                    new System.Text.RegularExpressions.Regex("Store"));
                TextRunSnapshotCache.Store(in key, new List<SdfGlyphQuad>(), 1, cmd);
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                TextRunSnapshotCache.Clear();
            }
        }
#endif

        // Regression (match3 `.combo-banner`): the snapshot key must include
        // the layout baseline. The glyph baker places the run's glyphs at
        // DrawTextCommand.LayoutBaseline, so two otherwise-identical runs with
        // a DIFFERENT baseline produce different quads and must NOT share a
        // cache entry — and the key change from "no baseline" (NaN) to a real
        // baseline self-invalidates quads cached before the baseline was
        // plumbed. Without this the cache replayed the pre-fix glyph positions
        // (text jammed to the pill bottom) even after the fix landed.
        [Test]
        public void Snapshot_key_distinguishes_layout_baseline() {
            var bounds = new PaintRect(0, 0, 100, 30);
            var font = new FontHandle("sans-serif", 23, 800, FontStyle.Normal);
            var a = new DrawTextCommand(bounds, "SWEET!", font, LinearColor.White, TextDecoration.None);
            var b = new DrawTextCommand(bounds, "SWEET!", font, LinearColor.White, TextDecoration.None);
            a.SetLayoutBaseline(24.8);
            b.SetLayoutBaseline(18.0);
            var ka = new TextRunSnapshotKey(a);
            var kb = new TextRunSnapshotKey(b);
            Assert.That(ka.Equals(kb), Is.False,
                "runs with different layout baselines must key differently");

            // Same baseline → same key (cache reuse still works for the run
            // moving across the screen).
            b.SetLayoutBaseline(24.8);
            var kb2 = new TextRunSnapshotKey(b);
            Assert.That(ka.Equals(kb2), Is.True,
                "identical text/font/baseline must share a cache entry");
            Assert.That(ka.GetHashCode(), Is.EqualTo(kb2.GetHashCode()));
        }

        // T10: overflow must evict a SLICE, not Clear() the whole cache. The
        // old full wipe turned a document with > MaxEntries distinct visible
        // runs into a permanent re-shape storm (every run re-shaped every
        // frame). After crossing the cap the cache must stay near-full.
        [Test]
        public void Overflow_evicts_a_slice_not_the_whole_cache() {
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId);
            try {
                TextRunSnapshotCache.Clear();
                int max = TextRunSnapshotCache.MaxEntriesForTest;
                int batch = TextRunSnapshotCache.EvictBatchForTest;
                var font = new FontHandle("sans-serif", 14, 400, FontStyle.Normal);
                var bounds = new PaintRect(0, 0, 100, 16);
                // Store one past the cap with distinct keys (Text varies).
                for (int i = 0; i <= max; i++) {
                    var cmd = new DrawTextCommand(bounds, "run-" + i, font, LinearColor.White, TextDecoration.None);
                    var key = new TextRunSnapshotKey(cmd);
                    TextRunSnapshotCache.Store(in key, new List<SdfGlyphQuad>(), 1, cmd);
                }
                // Exactly one eviction fired (at count==max), dropping `batch`,
                // then the (max+1)th entry was added: max - batch + 1.
                Assert.That(TextRunSnapshotCache.Count, Is.EqualTo(max - batch + 1),
                    "overflow must drop one slice and keep the rest — not wipe to ~0 (the old Clear cliff)");
                Assert.That(TextRunSnapshotCache.Count, Is.GreaterThan(max / 2),
                    "cache must stay near-full after overflow so a large document doesn't thrash");
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                TextRunSnapshotCache.Clear();
            }
        }

        [Test]
        public void Snapshot_key_legacy_nan_baseline_is_stable() {
            // A command with no layout baseline (NaN) must key consistently
            // against another NaN command (NaN != NaN normally would break the
            // dictionary).
            var bounds = new PaintRect(0, 0, 100, 16);
            var font = new FontHandle("sans-serif", 14, 400, FontStyle.Normal);
            var a = new DrawTextCommand(bounds, "Hi", font, LinearColor.White, TextDecoration.None);
            var b = new DrawTextCommand(bounds, "Hi", font, LinearColor.White, TextDecoration.None);
            // both LayoutBaseline default NaN
            var ka = new TextRunSnapshotKey(a);
            var kb = new TextRunSnapshotKey(b);
            Assert.That(ka.Equals(kb), Is.True, "two NaN-baseline runs must share a key");
            Assert.That(ka.GetHashCode(), Is.EqualTo(kb.GetHashCode()));
        }
    }
}
