using NUnit.Framework;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // Regression coverage for CODE_AUDIT_FINDINGS.md MS3:
    //   ValueInterpolator.transformFnCache (256 cap) and identityListCache
    //   (64 cap) were soft-capped but had NO eviction — once full, every
    //   novel transform string was simply dropped from the cache and paid
    //   the parse + identity-build cost on every subsequent Tick forever.
    //
    // The first fix added drop-ONE-on-overflow eviction, but that THRASHED
    // at capacity (particles.html): a burst of per-frame-unique interpolated
    // strings filled the dictionary once and squatted the early hash slots,
    // after which every stable keyframe string inserted evicted another
    // stable key — every transform tick paid two full re-parses + a typed
    // overlay rebuild (~270 us per call, ~60 ms/frame across 420 anims).
    // Current policy: FULL CLEAR at the cap (TextRunSnapshotCache pattern).
    // The live working set re-inserts within one frame and stays cached;
    // one-shot garbage only costs a clear once per ~cap distinct strings.
    //
    // These tests exercise both caches via the public `Interpolate` entry
    // point with `PropertyKind.Transform`. `Interpolate("none", "rotate(Xdeg)", ...)`
    // populates transformFnCache (one entry per distinct raw string) AND
    // identityListCache (the parsed list ref is the identity key, and a
    // fresh list is created per unique input string).
    public class ValueInterpolatorTransformCacheEvictionTests {
        static LengthContext Ctx() => LengthContext.Default;

        static int TransformCap => ValueInterpolator.TransformFnCacheCap_TestOnly;
        static int IdentityCap => ValueInterpolator.IdentityListCacheCap_TestOnly;

        [SetUp]
        public void ResetCaches() {
            // Caches are process-static; without this reset the cap-exhaustion
            // math in one test would depend on prior tests' insertions.
            ValueInterpolator.ResetCaches_TestOnly();
        }

        // Unique-per-i rotate string. The integer angle keeps the string
        // distinct (== distinct key in transformFnCache) without depending
        // on floating-point formatting quirks.
        static string Rotate(int i) => "rotate(" + i + "deg)";

        // Force interpolation along the `none -> non-matrix` branch which
        // routes through BOTH ParseTransformFunctionsCached (populates
        // transformFnCache) AND MakeIdentityListCached (populates
        // identityListCache). The mid-progress t value forces the
        // interpolation path (t in (0,1)); t==0 or t==1 short-circuits and
        // skips the parse.
        static string InterpolateRotate(int i) =>
            ValueInterpolator.Interpolate("none", Rotate(i), 0.5, PropertyKind.Transform, Ctx());

        [Test]
        public void TransformFnCache_overflow_clears_wholesale() {
            // Fill the cache exactly to its cap, then insert one more
            // distinct key. The full-clear policy empties the dictionary
            // and lands just the overflow key — count == 1, NOT cap.
            // (The old drop-one policy kept count == cap, which thrashed:
            // stale early-slot entries stayed resident forever and the
            // LIVE working set rotated through the single free slot.)
            int i = 0;
            while (ValueInterpolator.TransformFnCacheCount_TestOnly() < TransformCap
                   && i < TransformCap * 2) {
                InterpolateRotate(i++);
            }
            Assume.That(ValueInterpolator.TransformFnCacheCount_TestOnly(), Is.EqualTo(TransformCap));

            InterpolateRotate(99999); // "none" hits pre-clear; "rotate(99999deg)" trips the clear
            Assert.That(ValueInterpolator.TransformFnCacheCount_TestOnly(),
                Is.EqualTo(1),
                "the cap overflow must clear the cache wholesale and land only the overflow key");
        }

        [Test]
        public void TransformFnCache_hot_working_set_stays_cached_after_overflow() {
            // The particles.html thrash regression: after a cap overflow,
            // the hot working set must re-insert ONCE and then stay —
            // repeated resolution of the same strings must not change the
            // cache contents (no per-tick re-parse / overlay rebuild).
            int i = 0;
            while (ValueInterpolator.TransformFnCacheCount_TestOnly() < TransformCap
                   && i < TransformCap * 2) {
                InterpolateRotate(i++);
            }
            InterpolateRotate(99999); // trips the clear → count 1
            InterpolateRotate(0);     // hot pair re-inserts ("none" + "rotate(0deg)")
            int stable = ValueInterpolator.TransformFnCacheCount_TestOnly();
            for (int r = 0; r < 50; r++) {
                InterpolateRotate(0);
                InterpolateRotate(99999);
            }
            Assert.That(ValueInterpolator.TransformFnCacheCount_TestOnly(),
                Is.EqualTo(stable),
                "repeated resolution of the post-overflow working set must be pure cache hits");
        }

        [Test]
        public void IdentityListCache_overflow_clears_wholesale() {
            // Same full-clear policy as transformFnCache: the `none ->
            // rotate(Xdeg)` branch invokes MakeIdentityListCached with the
            // parsed `to` list as the source — each distinct string parses
            // to a distinct List<TransformFn> reference, so each call
            // hashes to a fresh identity cache slot. The insert that finds
            // the cache at cap clears it wholesale and lands alone.
            int i = 0;
            while (ValueInterpolator.IdentityListCacheCount_TestOnly() < IdentityCap
                   && i < IdentityCap * 2) {
                InterpolateRotate(i++);
            }
            Assume.That(ValueInterpolator.IdentityListCacheCount_TestOnly(), Is.EqualTo(IdentityCap));
            InterpolateRotate(99999);
            Assert.That(ValueInterpolator.IdentityListCacheCount_TestOnly(),
                Is.EqualTo(1),
                "the identity-cache cap overflow must clear wholesale and land only the overflow entry");
        }

        [Test]
        public void Reset_clears_both_caches() {
            // Populate both, reset, verify both empty. Pins
            // ResetCaches_TestOnly as a real Clear of BOTH caches (not
            // just one) — the regression hazard is forgetting to add a
            // new cache to the reset helper, leaving cross-test state
            // contamination.
            for (int i = 0; i < 10; i++) {
                InterpolateRotate(i);
            }
            Assume.That(ValueInterpolator.TransformFnCacheCount_TestOnly(),
                Is.GreaterThan(0),
                "Precondition: transformFnCache should have been populated.");
            Assume.That(ValueInterpolator.IdentityListCacheCount_TestOnly(),
                Is.GreaterThan(0),
                "Precondition: identityListCache should have been populated.");

            ValueInterpolator.ResetCaches_TestOnly();

            Assert.That(ValueInterpolator.TransformFnCacheCount_TestOnly(),
                Is.EqualTo(0),
                "ResetCaches_TestOnly must empty transformFnCache.");
            Assert.That(ValueInterpolator.IdentityListCacheCount_TestOnly(),
                Is.EqualTo(0),
                "ResetCaches_TestOnly must empty identityListCache.");

            // Sanity: insert path still works post-reset.
            // InterpolateRotate(0) caches TWO transform strings: "none" (the
            // from-side) + "rotate(0deg)" (the to-side). Both pass through
            // ParseTransformFunctionsCached, so the post-reset count is 2.
            InterpolateRotate(0);
            Assert.That(ValueInterpolator.TransformFnCacheCount_TestOnly(),
                Is.EqualTo(2),
                "Insert path must work normally after reset (both 'none' and 'rotate(0deg)' are cached).");
        }

        [Test]
        public void Repeated_lookups_do_not_grow_either_cache() {
            // Hit-path regression: resolving the SAME pair of transform strings many
            // times must not grow either cache beyond the initial population. Guards
            // against an eviction-path bug that fires on cache hits (e.g. the cap
            // check moved above the TryGetValue early-return).
            //
            // InterpolateRotate(0) maps "none" → "rotate(0deg)". Both strings pass
            // through ParseTransformFunctionsCached, so after the first call the
            // transformFnCache holds TWO entries ("none" and "rotate(0deg)").
            // The identity cache holds ONE entry (keyed on the "rotate(0deg)"
            // list reference, reused across all 50 calls via the transform cache).
            for (int i = 0; i < 50; i++) {
                InterpolateRotate(0);
            }
            Assert.That(ValueInterpolator.TransformFnCacheCount_TestOnly(),
                Is.EqualTo(2),
                "Repeated lookups must collapse to two transformFnCache entries: one for 'none', one for 'rotate(0deg)'.");
            Assert.That(ValueInterpolator.IdentityListCacheCount_TestOnly(),
                Is.EqualTo(1),
                "Repeated lookups of the same key must collapse to one identityListCache entry.");
        }
    }
}
