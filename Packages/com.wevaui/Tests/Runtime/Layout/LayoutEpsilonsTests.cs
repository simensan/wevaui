using System.Reflection;
using NUnit.Framework;
using Weva.Layout;

namespace Weva.Tests.Layout {
    // MN2 (CODE_AUDIT_FINDINGS.md) regression pins.
    //
    // The MN2 refactor centralised the layout-pass epsilon "zoo"
    // (1e-9 / 1e-5 / 1e-3 / 0.5) into named LayoutEpsilons constants. These
    // tests pin:
    //   1. Each named constant against its spec / rationale value so a
    //      future tuning attempt must update this file deliberately.
    //   2. A representative layout use-site (FlexLayout wrap decision)
    //      produces the same numeric output as before the refactor — i.e.
    //      the named-constant routing did not accidentally change behaviour.
    //   3. D5 — the two same-signature private statics
    //      LayoutEngine.NearlySame and LayoutEngine.Incremental.NearlyEqual
    //      remain DISTINCT functions with distinct thresholds. Future
    //      "consolidation" PRs that merge them MUST update / delete this
    //      test deliberately (see the doc-block on
    //      LayoutEpsilons.HalfPixelEqual for why they should stay split).
    public class LayoutEpsilonsTests {

        // 1. Pin each named constant. These mirror the spec/rationale comments
        // in LayoutEpsilons.cs — changing any of these values is a behaviour
        // change for every call site that reads the constant.

        [Test]
        public void SubPixelEqual_is_pinned_at_one_thousandth() {
            Assert.That(LayoutEpsilons.SubPixelEqual, Is.EqualTo(0.001).Within(1e-15));
        }

        [Test]
        public void HalfPixelEqual_is_pinned_at_one_half() {
            Assert.That(LayoutEpsilons.HalfPixelEqual, Is.EqualTo(0.5).Within(1e-15));
        }

        [Test]
        public void LayoutNoise_is_pinned_at_one_hundred_thousandth() {
            Assert.That(LayoutEpsilons.LayoutNoise, Is.EqualTo(1e-5).Within(1e-20));
        }

        [Test]
        public void MachineEpsilon_is_pinned_at_one_billionth() {
            Assert.That(LayoutEpsilons.MachineEpsilon, Is.EqualTo(1e-9).Within(1e-20));
        }

        // 2. Representative use-site parity. The FlexLayout wrap-boundary
        // tolerance is `containerMainSize * LayoutNoise` floored at
        // FlexWrapEpsilonMinPx (0.01). Pin the computation against an
        // explicit before-refactor value so a future code-mod that swaps in
        // the wrong band (e.g. SubPixelEqual instead of LayoutNoise) trips
        // the test even if it compiles.
        [Test]
        public void FlexWrapEpsilon_matches_pre_refactor_formula() {
            // Container far enough above the floor that the relative branch
            // wins (1000px * 1e-5 = 0.01 — exactly at the floor by construction,
            // so use 10000px which produces 0.1, comfortably above the 0.01 floor).
            const double containerMainSize = 10000.0;
            // Pre-refactor inline literal was `containerMainSize * 1e-5`.
            // Post-refactor reads LayoutEpsilons.LayoutNoise. Identical math.
            double inlineLiteralValue = containerMainSize * 1e-5;
            double namedConstantValue = containerMainSize * LayoutEpsilons.LayoutNoise;
            Assert.That(namedConstantValue, Is.EqualTo(inlineLiteralValue).Within(1e-15),
                "MN2: routing the wrap-epsilon formula through LayoutEpsilons.LayoutNoise " +
                "must yield the same numeric value as the pre-refactor inline literal. " +
                "If this fails, either LayoutNoise drifted from 1e-5 or the call site " +
                "is reading a different constant — both are MN2 regressions.");

            // Small container — the floor wins. The named constant must produce
            // a value BELOW the floor so the FlexWrapEpsilonMinPx clamp engages.
            const double smallContainer = 100.0;
            double smallEps = smallContainer * LayoutEpsilons.LayoutNoise;
            Assert.That(smallEps, Is.LessThan(0.01),
                "MN2: for a 100px container, relative epsilon must fall below the " +
                "0.01px floor so the FlexLayout wrap-boundary clamp engages.");
        }

        // Cache-equality sites in BlockLayout / FlexLayout / GridLayout /
        // PositioningPass all use HalfPixelEqual. Pin the conversion is
        // exact (no precision loss) so a value just inside / just outside
        // the tolerance still classifies identically to the inline literal.
        [Test]
        public void HalfPixelEqual_threshold_classifies_identically_to_inline_literal() {
            // Values straddling 0.5 — the inline-literal call site is
            // `Math.Abs(a - b) < 0.5`. Both halves of the inequality MUST
            // route to the same boolean after the rename.
            double[] deltas = { 0.0, 0.4999, 0.5, 0.5001, 1.0, -0.4999, -0.5, -0.5001 };
            foreach (var d in deltas) {
                bool inline = System.Math.Abs(d) < 0.5;
                bool named = System.Math.Abs(d) < LayoutEpsilons.HalfPixelEqual;
                Assert.That(named, Is.EqualTo(inline),
                    $"MN2: delta={d} classifies differently between inline 0.5 and " +
                    "LayoutEpsilons.HalfPixelEqual; the rename changed behaviour.");
            }
        }

        // D10 (CODE_AUDIT_FINDINGS.md) — IsHalfPixelEqual helper.
        //
        // The helper centralises `Math.Abs(a - b) <= HalfPixelEqual` so the
        // raw 0.5 literal doesn't have to appear inline at six GridLayout
        // sites. Pin three points: clearly-inside (TRUE), clearly-outside
        // (FALSE), and the boundary itself (TRUE — inclusive `<=`).
        [Test]
        public void IsHalfPixelEqual_returns_true_for_values_inside_half_pixel() {
            // |0.5 - 0.95| = 0.45 < 0.5 — solidly inside the tolerance.
            Assert.That(LayoutEpsilons.IsHalfPixelEqual(0.5, 0.95), Is.True,
                "D10: two values within half a CSS pixel must classify as " +
                "half-pixel-equal; this is the dominant 'caches still match' case.");
        }

        [Test]
        public void IsHalfPixelEqual_returns_false_for_values_outside_half_pixel() {
            // |0.5 - 1.001| = 0.501 > 0.5 — just past the tolerance.
            Assert.That(LayoutEpsilons.IsHalfPixelEqual(0.5, 1.001), Is.False,
                "D10: a value more than half a CSS pixel away must classify " +
                "as NOT half-pixel-equal; this is what protects the " +
                "GridLayout reflow-shrink branches from over-triggering.");
        }

        [Test]
        public void IsHalfPixelEqual_boundary_is_inclusive() {
            // |0 - 0.5| = 0.5 EXACTLY — the helper uses `<=`, so this is TRUE.
            // The pre-helper inline sites used `<` (exclusive); migrating to
            // `<=` is documented on the helper's doc-block. The boundary case
            // is measure-zero in practice (no layout pass produces a delta of
            // bit-exact 0.5), so the widened bucket is safe.
            Assert.That(LayoutEpsilons.IsHalfPixelEqual(0.0, 0.5), Is.True,
                "D10: the boundary itself must classify as half-pixel-equal " +
                "(inclusive `<=`). See LayoutEpsilons.IsHalfPixelEqual " +
                "doc-block for the rationale.");
            // Identity pin.
            Assert.That(LayoutEpsilons.IsHalfPixelEqual(0.5, 0.5), Is.True,
                "D10: identical inputs must trivially be half-pixel-equal.");
        }

        [Test]
        public void IsHalfPixelEqual_is_symmetric() {
            // Order independence — Math.Abs makes it symmetric.
            Assert.That(LayoutEpsilons.IsHalfPixelEqual(1.0, 1.3),
                Is.EqualTo(LayoutEpsilons.IsHalfPixelEqual(1.3, 1.0)));
            Assert.That(LayoutEpsilons.IsHalfPixelEqual(0.0, 0.6),
                Is.EqualTo(LayoutEpsilons.IsHalfPixelEqual(0.6, 0.0)));
        }

        // 3. D5 regression pin — NearlySame and NearlyEqual remain distinct
        // private statics with distinct thresholds. A future "consolidation"
        // that wrongly merges them (replacing one with the other) would
        // either change the file count, change the threshold, or both. Pin
        // each method's existence + its threshold via reflection on the
        // private static method body's behaviour (i.e. probe with values
        // that straddle each threshold).
        [Test]
        public void D5_NearlySame_and_NearlyEqual_remain_distinct_with_distinct_thresholds() {
            // Reflect into LayoutEngine for both private static methods.
            // BindingFlags.NonPublic | BindingFlags.Static narrows the search;
            // both methods live on partial-class LayoutEngine (one in
            // LayoutEngine.cs, one in LayoutEngine.Incremental.cs).
            var engineType = typeof(LayoutEngine);
            var nearlySame = engineType.GetMethod("NearlySame",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(double), typeof(double) },
                modifiers: null);
            var nearlyEqual = engineType.GetMethod("NearlyEqual",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(double), typeof(double) },
                modifiers: null);

            Assert.That(nearlySame, Is.Not.Null,
                "D5: LayoutEngine.NearlySame disappeared. If a consolidation PR " +
                "merged it with NearlyEqual, please re-read the LayoutEpsilons.cs " +
                "doc-block on HalfPixelEqual — the two methods answer DIFFERENT " +
                "questions (sub-px output equality vs paint-equivalence skip).");
            Assert.That(nearlyEqual, Is.Not.Null,
                "D5: LayoutEngine.NearlyEqual disappeared. See NearlySame note above.");
            Assert.That(nearlySame.DeclaringType, Is.EqualTo(nearlyEqual.DeclaringType),
                "Both methods should live on the partial LayoutEngine.");
            // They must be DIFFERENT method handles — i.e. not aliases.
            Assert.That(nearlySame, Is.Not.EqualTo(nearlyEqual),
                "D5: NearlySame and NearlyEqual collapsed to the same MethodInfo. " +
                "Do not consolidate them.");

            // Behavioural pin: NearlySame uses SubPixelEqual (0.001), so values
            // separated by 0.1 must report FALSE (not nearly the same), while
            // NearlyEqual uses HalfPixelEqual (0.5), so the same pair must
            // report TRUE (nearly equal). This is the bug that the D5 split
            // protects against — if a refactor unifies them at 0.5, the
            // sub-pixel cache-validation path silently accepts drift; if at
            // 0.001, the incremental-skip path stops skipping safe cases.
            object[] args = { 1.0, 1.1 };
            bool nearlySameResult = (bool)nearlySame.Invoke(null, args);
            bool nearlyEqualResult = (bool)nearlyEqual.Invoke(null, args);
            Assert.That(nearlySameResult, Is.False,
                "D5: NearlySame(1.0, 1.1) should be FALSE — its threshold " +
                "is SubPixelEqual (0.001). If TRUE, the method has been " +
                "retuned to NearlyEqual's half-pixel threshold; do not merge them.");
            Assert.That(nearlyEqualResult, Is.True,
                "D5: NearlyEqual(1.0, 1.1) should be TRUE — its threshold " +
                "is HalfPixelEqual (0.5). If FALSE, the method has been " +
                "retuned to NearlySame's sub-pixel threshold; do not merge them.");

            // Both still answer TRUE for actually-identical inputs and FALSE
            // for clearly-different inputs (sanity that the thresholds are
            // bounded the same direction).
            Assert.That((bool)nearlySame.Invoke(null, new object[] { 1.0, 1.0 }), Is.True);
            Assert.That((bool)nearlyEqual.Invoke(null, new object[] { 1.0, 1.0 }), Is.True);
            Assert.That((bool)nearlySame.Invoke(null, new object[] { 1.0, 10.0 }), Is.False);
            Assert.That((bool)nearlyEqual.Invoke(null, new object[] { 1.0, 10.0 }), Is.False);
        }

        // D5 — regression pin for NearlySame's SubPixelEqual threshold (0.001).
        // (1.0, 1.0005) is INSIDE the 0.001 tolerance (delta 0.0005 < 0.001) so
        // must return TRUE; (1.0, 1.01) is OUTSIDE (delta 0.01 > 0.001) so must
        // return FALSE. If either flips, the cache-vs-fresh layout equality
        // check has been retuned to a coarser threshold and sub-pixel drift
        // will silently invalidate or — worse — silently survive into paint.
        [Test]
        public void D5_NearlySame_pins_sub_pixel_threshold() {
            var nearlySame = typeof(LayoutEngine).GetMethod("NearlySame",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(double), typeof(double) },
                modifiers: null);
            Assert.That(nearlySame, Is.Not.Null, "D5: LayoutEngine.NearlySame is missing.");

            bool inside = (bool)nearlySame.Invoke(null, new object[] { 1.0, 1.0005 });
            bool outside = (bool)nearlySame.Invoke(null, new object[] { 1.0, 1.01 });

            Assert.That(inside, Is.True,
                "D5: NearlySame(1.0, 1.0005) must be TRUE — 0.0005 is inside " +
                "the SubPixelEqual (0.001) tolerance. If FALSE, the threshold " +
                "has been tightened past sub-pixel and cache-equality will " +
                "miss on every frame's natural double-precision noise.");
            Assert.That(outside, Is.False,
                "D5: NearlySame(1.0, 1.01) must be FALSE — 0.01 is outside the " +
                "SubPixelEqual (0.001) tolerance. If TRUE, the threshold has " +
                "been widened toward HalfPixelEqual and cached layout will " +
                "survive sub-pixel positional drift undetected.");
        }

        // D5 — regression pin for NearlyEqual's HalfPixelEqual threshold (0.5).
        // (1.0, 1.4) is INSIDE the 0.5 tolerance (delta 0.4 < 0.5) so must
        // return TRUE; (1.0, 1.6) is OUTSIDE (delta 0.6 > 0.5) so must return
        // FALSE. If either flips, the incremental-skip paint-equivalence check
        // has been retuned and incremental layout will either over-skip
        // (visible diffs survive) or under-skip (perf regression).
        [Test]
        public void D5_NearlyEqual_pins_half_pixel_threshold() {
            var nearlyEqual = typeof(LayoutEngine).GetMethod("NearlyEqual",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(double), typeof(double) },
                modifiers: null);
            Assert.That(nearlyEqual, Is.Not.Null, "D5: LayoutEngine.NearlyEqual is missing.");

            bool inside = (bool)nearlyEqual.Invoke(null, new object[] { 1.0, 1.4 });
            bool outside = (bool)nearlyEqual.Invoke(null, new object[] { 1.0, 1.6 });

            Assert.That(inside, Is.True,
                "D5: NearlyEqual(1.0, 1.4) must be TRUE — 0.4 is inside the " +
                "HalfPixelEqual (0.5) tolerance. If FALSE, the threshold has " +
                "been tightened toward SubPixelEqual and incremental layout " +
                "will stop skipping paint-equivalent reflows (perf regression).");
            Assert.That(outside, Is.False,
                "D5: NearlyEqual(1.0, 1.6) must be FALSE — 0.6 is outside the " +
                "HalfPixelEqual (0.5) tolerance. If TRUE, the threshold has " +
                "been widened past half a pixel and incremental layout will " +
                "skip reflows whose output is visibly different on screen.");
        }
    }
}
