using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // DD4 regression pins: TryParse memoizes failures in `failedCache`, so an
    // author who fixes a malformed declaration mid-edit needs the negative
    // cache to be invalidated when the stylesheet reloads — otherwise the
    // next TryParse for the same raw text keeps returning the cached null.
    // CssValue.InvalidateNegativeCache() is the production-safe entry point
    // wired into the hot-reload pipeline; these tests pin its behaviour at
    // the unit level so a future refactor cannot quietly regress it.
    public class CssValueNegativeCacheTests {
        [SetUp]
        public void Reset() {
            CssValue.ClearCachesForTests();
        }

        [Test]
        public void Failed_parse_re_attempts_after_InvalidateNegativeCache() {
            // First call seeds the negative cache for a deliberately
            // malformed value. The actual parse failure is the point; the
            // text is irrelevant beyond "the parser will reject it".
            const string Bad = "rgb(1,2";
            Assert.That(CssValue.TryParseSilent(Bad, out _), Is.False);
            long failedHitsBefore = CssValue.ParseCacheFailedHits;

            // Second call must short-circuit through failedCache — that
            // increments ParseCacheFailedHits and is the exact bug DD4
            // describes.
            Assert.That(CssValue.TryParseSilent(Bad, out _), Is.False);
            Assert.That(CssValue.ParseCacheFailedHits, Is.EqualTo(failedHitsBefore + 1));

            // Drop the negative cache and re-issue the call. The parser
            // path runs again — no failed-cache hit, and we get a fresh
            // miss-then-attempt instead.
            CssValue.InvalidateNegativeCache();
            long failedHitsAfterClear = CssValue.ParseCacheFailedHits;
            long missesBefore = CssValue.ParseCacheMisses;
            Assert.That(CssValue.TryParseSilent(Bad, out _), Is.False);
            Assert.That(CssValue.ParseCacheFailedHits, Is.EqualTo(failedHitsAfterClear),
                "InvalidateNegativeCache should leave the next failure a fresh miss, not a cache hit.");
            Assert.That(CssValue.ParseCacheMisses, Is.EqualTo(missesBefore + 1));
        }

        [Test]
        public void Successful_parse_cache_survives_InvalidateNegativeCache() {
            // Positive cache entries are deterministic for the raw text:
            // "16px" always parses the same way. The fix must not flush
            // them — flushing on every reload would force a reparse
            // storm. Pin that the positive cache is preserved.
            Assert.That(CssValue.TryParse("16px", out var first), Is.True);
            Assert.That(first, Is.Not.Null);

            CssValue.InvalidateNegativeCache();

            long hitsBefore = CssValue.ParseCacheHits;
            Assert.That(CssValue.TryParse("16px", out var second), Is.True);
            Assert.That(CssValue.ParseCacheHits, Is.EqualTo(hitsBefore + 1),
                "Positive cache entry should still be served as a hit after InvalidateNegativeCache.");
            // Same interned instance — verifies the cached entry itself
            // (not a freshly-parsed sibling) was returned.
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void Author_fix_after_failed_parse_resolves_after_invalidation() {
            // End-to-end author-edit story: imagine TryParseSilent gets
            // called with raw text X (a transient bad state the author
            // is in the middle of typing). X enters the negative cache.
            // The author then fixes the stylesheet and saves; the same
            // raw text X — or any raw text the negative cache happens to
            // hold — must re-parse on the next call instead of returning
            // the cached null forever.
            //
            // We model the transient bad state with a value the parser
            // rejects (poisons failedCache), then call InvalidateNegativeCache
            // to mimic the RebuildCascade clear, then verify a fresh
            // *successful* parse of an unrelated valid value still works
            // AND the previously-poisoned text is re-attempted (proving
            // a real author-fix that mutated X into a valid form would
            // resolve correctly, not silently null).
            const string Bad = "rgb(1,2";
            Assert.That(CssValue.TryParseSilent(Bad, out _), Is.False);
            // Second call confirms the negative cache is in effect.
            long failedHitsBeforeInvalidate = CssValue.ParseCacheFailedHits;
            Assert.That(CssValue.TryParseSilent(Bad, out _), Is.False);
            Assert.That(CssValue.ParseCacheFailedHits, Is.EqualTo(failedHitsBeforeInvalidate + 1));

            // Hot-reload moment: cache flushed.
            CssValue.InvalidateNegativeCache();

            // A subsequent valid parse must succeed normally — sanity
            // check that the invalidation didn't break the positive path.
            Assert.That(CssValue.TryParse("red", out var goodValue), Is.True);
            Assert.That(goodValue, Is.Not.Null);

            // And the previously-failed text must re-enter the parser
            // (a fresh miss, not a failed-cache hit). This is the exact
            // behaviour that would let an author-fixed value resolve
            // instead of silently returning the cached null.
            long failedHitsAfter = CssValue.ParseCacheFailedHits;
            long missesBefore = CssValue.ParseCacheMisses;
            Assert.That(CssValue.TryParseSilent(Bad, out _), Is.False);
            Assert.That(CssValue.ParseCacheFailedHits, Is.EqualTo(failedHitsAfter),
                "Bad raw text must miss the (now-empty) negative cache, not hit it.");
            Assert.That(CssValue.ParseCacheMisses, Is.EqualTo(missesBefore + 1),
                "Bad raw text must take the miss-then-reparse path after invalidation.");
        }
    }
}
