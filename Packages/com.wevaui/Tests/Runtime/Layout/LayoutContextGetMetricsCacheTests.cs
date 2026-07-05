using System;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    // Regression coverage for CODE_AUDIT_FINDINGS.md P19:
    //   LayoutContext.GetMetrics(fontFamily) previously substring+Trim'd one
    //   head per comma on EVERY call, with ~15 Layout call-sites hitting it
    //   per layout pass. On `font-family: "Inter", system-ui, sans-serif`
    //   over a 100-element tree that's hundreds of small strings per layout.
    //
    // The fix adds a per-LayoutContext cache keyed on the RAW family string
    // (pre-normalisation, pre-split). These tests pin:
    //   1. Identity: same raw key returns the same IFontMetrics reference.
    //   2. Allocation: 100 repeat calls allocate ~zero (no Substring/Trim
    //      churn) — exercised via GC.GetAllocatedBytesForCurrentThread when
    //      available, GC.GetTotalMemory otherwise.
    //   3. Correctness: distinct raw keys still resolve to the correct
    //      registered metrics — the cache must not collapse them together.
    [Category("alloc")]
    public class LayoutContextGetMetricsCacheTests {
        // Per-call helper. The cache lives on the LayoutContext instance so
        // each test gets a fresh one — no cross-test leakage to reset.
        static LayoutContext NewCtx(IFontMetrics defaultMetrics) {
            return new LayoutContext(defaultMetrics);
        }

        [Test]
        public void GetMetrics_same_raw_family_returns_same_instance() {
            var defaultMetrics = new MonoFontMetrics();
            var inter = MonoFontMetrics.ChromeSansSerif();
            var ctx = NewCtx(defaultMetrics);
            // NOTE: NormalizeFamily only strips surrounding quotes when the
            // WHOLE string is quoted (not per-head inside a comma list); to
            // match the unquoted head produced by `Inter, system-ui, sans-serif`
            // we register the bare identifier.
            ctx.RegisterFont("Inter", inter);

            const string raw = "Inter, system-ui, sans-serif";
            var first = ctx.GetMetrics(raw);
            var second = ctx.GetMetrics(raw);
            var third = ctx.GetMetrics(raw);

            // ReferenceEquals — must be the exact same IFontMetrics instance,
            // i.e. the cache hit returned the cached object rather than
            // re-walking the comma-list and possibly returning a different
            // (but equivalent) reference.
            Assert.That(ReferenceEquals(first, inter), Is.True,
                "First lookup should resolve Inter head to the registered metrics");
            Assert.That(ReferenceEquals(second, first), Is.True,
                "Second lookup should hit the cache and return the same instance");
            Assert.That(ReferenceEquals(third, first), Is.True,
                "Third lookup should also hit the cache");
        }

        [Test, Explicit("alloc")]
        public void GetMetrics_repeat_calls_allocate_near_zero() {
            var defaultMetrics = new MonoFontMetrics();
            var inter = MonoFontMetrics.ChromeSansSerif();
            var ctx = NewCtx(defaultMetrics);
            ctx.RegisterFont("Inter", inter);

            const string raw = "Inter, system-ui, sans-serif";
            // Warm the cache + JIT the GetMetrics path.
            for (int i = 0; i < 5; i++) ctx.GetMetrics(raw);

            Stabilize();
            long before = SnapshotAlloc();
            const int iterations = 100;
            for (int i = 0; i < iterations; i++) {
                var m = ctx.GetMetrics(raw);
                // Touch the result so the JIT can't elide the call.
                if (m == null) Assert.Fail("Unexpected null metrics during alloc loop");
            }
            long after = SnapshotAlloc();
            long perCall = (after - before) / iterations;
            TestContext.Progress.WriteLine(
                $"LayoutContext.GetMetrics[cache hit]: {perCall} B/call over {iterations} reps");

            // Pre-fix: each call allocated 3 substrings ("Inter" trimmed,
            // " system-ui" trimmed, then " sans-serif" trimmed) plus the
            // ToLowerInvariantOrSame normalisation — well over 100 B/call.
            // Post-fix: the cache hit short-circuits BEFORE NormalizeFamily,
            // so per-call alloc should be in single-digit-bytes territory
            // (sampling noise only). Ceiling 32 B leaves headroom for runtime
            // accounting jitter (the precise GC counter on netcoreapp
            // sometimes credits a few bytes of cross-thread bookkeeping
            // even to an alloc-free loop).
            Assert.That(perCall, Is.LessThanOrEqualTo(32L),
                $"Expected near-zero per-call alloc on cache hit; got {perCall} B");
        }

        [Test]
        public void GetMetrics_distinct_raw_families_resolve_to_distinct_metrics() {
            var defaultMetrics = new MonoFontMetrics();
            var inter = MonoFontMetrics.ChromeSansSerif();
            var mono = MonoFontMetrics.ChromeMonospace();
            var ctx = NewCtx(defaultMetrics);
            ctx.RegisterFont("Inter", inter);
            ctx.RegisterFont("monospace", mono);

            // Three distinct raw family strings. The cache MUST key on the
            // raw string — collapsing any two of these into a single cache
            // entry would silently break authors who declare different fall-
            // back chains.
            var a = ctx.GetMetrics("Inter, system-ui, sans-serif");
            var b = ctx.GetMetrics("monospace");
            var c = ctx.GetMetrics("NoSuchFont, AlsoMissing"); // falls through to default

            Assert.That(ReferenceEquals(a, inter), Is.True,
                "Inter head should bind to the Inter registration");
            Assert.That(ReferenceEquals(b, mono), Is.True,
                "Bare 'monospace' should bind to the monospace registration");
            Assert.That(ReferenceEquals(c, defaultMetrics), Is.True,
                "All-missing fallback chain should return DefaultFontMetrics");

            // Second-pass: call each again. Cache hits MUST still return the
            // correct (distinct) metrics — this is the regression pin against
            // a future cache implementation that accidentally returns the
            // first cached value for every key.
            Assert.That(ReferenceEquals(ctx.GetMetrics("Inter, system-ui, sans-serif"), inter), Is.True);
            Assert.That(ReferenceEquals(ctx.GetMetrics("monospace"), mono), Is.True);
            Assert.That(ReferenceEquals(ctx.GetMetrics("NoSuchFont, AlsoMissing"), defaultMetrics), Is.True);
        }

        [Test]
        public void GetMetrics_cache_respects_drop_on_overflow_cap() {
            // Soft-cap regression: feeding distinct raw keys past the cap must
            // not grow the dictionary unbounded. Mirrors the MS3 ValueInter-
            // polator cap policy — drop-one-on-overflow, size stays AT cap.
            var ctx = NewCtx(new MonoFontMetrics());
            int cap = ctx.FamilyResolveCacheCap_TestOnly;
            for (int i = 0; i < cap + 50; i++) {
                // Each distinct raw string forces a fresh cache entry.
                ctx.GetMetrics("family-" + i + ", sans-serif");
            }
            Assert.That(ctx.FamilyResolveCacheCount_TestOnly, Is.EqualTo(cap),
                "Cache should sit at exactly Cap entries after overflow");
        }

        [Test]
        public void ResetCaches_TestOnly_empties_the_cache() {
            var ctx = NewCtx(new MonoFontMetrics());
            ctx.GetMetrics("Inter, system-ui, sans-serif");
            ctx.GetMetrics("monospace");
            Assert.That(ctx.FamilyResolveCacheCount_TestOnly, Is.GreaterThan(0));
            ctx.ResetCaches_TestOnly();
            Assert.That(ctx.FamilyResolveCacheCount_TestOnly, Is.EqualTo(0));
        }

        [Test]
        public void RegisterFont_invalidates_cache_so_late_registration_is_visible() {
            // If we cached a fall-through-to-default answer BEFORE a font was
            // registered, then registered the font, a stale cache would
            // continue to hand out the default. RegisterFont must clear.
            var defaultMetrics = new MonoFontMetrics();
            var inter = MonoFontMetrics.ChromeSansSerif();
            var ctx = NewCtx(defaultMetrics);

            const string raw = "Inter, system-ui, sans-serif";
            var pre = ctx.GetMetrics(raw);
            Assert.That(ReferenceEquals(pre, defaultMetrics), Is.True);

            ctx.RegisterFont("Inter", inter);
            var post = ctx.GetMetrics(raw);
            Assert.That(ReferenceEquals(post, inter), Is.True,
                "Post-registration lookup must see the new metrics, not the stale fall-through");
        }

        // --- alloc helpers, mirroring LayoutAllocFloorTests --------------

        static long SnapshotAlloc() {
#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            return GC.GetAllocatedBytesForCurrentThread();
#else
            return GC.GetTotalMemory(forceFullCollection: false);
#endif
        }

        static void Stabilize() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }
}
