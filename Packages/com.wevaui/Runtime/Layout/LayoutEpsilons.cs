namespace Weva.Layout {
    // MN2 (CODE_AUDIT_FINDINGS.md) — central home for epsilon values used by
    // sub-pixel / cache-equality comparisons across the layout passes.
    //
    // Before this class existed, layout used a "zoo" of inline literals
    // (1e-9, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 0.5) for the same conceptual
    // questions ("are two positions equal?", "did the layout change?"). The
    // values themselves are not always interchangeable — they describe
    // different scales of noise — so this class names each band rather than
    // collapsing them to one number. The naming refactor (MN2) was a
    // documentation tightening only; threshold values match what was already
    // in-source.
    //
    // Spec/rationale notes per constant below. Picking the wrong constant for
    // a new call site is a correctness concern (e.g. using HalfPixelEqual
    // where SubPixelEqual is wanted will mask sub-px wrap-decision drift),
    // so prefer to think about the question being asked rather than the
    // numeric value.
    internal static class LayoutEpsilons {
        // "Two layout positions/sizes agree within sub-pixel rounding."
        //
        // Used when the question is "did the layout produce the same number
        // this frame as last frame, modulo end-of-pass rounding noise?".
        // Examples: flex base-size redistribution thresholds (totalBase vs
        // availableSpace), inline-layout wrap-boundary checks, LayoutEngine's
        // own NearlySame cached-vs-fresh geometry check. CSS Sizing L4 §6
        // (definite/indefinite resolution) doesn't pin a tolerance — this
        // 1e-3 was chosen empirically as one order of magnitude below the
        // smallest meaningful CSS px and survives ~hundreds of accumulated
        // double additions before drift exceeds it.
        public const double SubPixelEqual = 0.001;

        // "Two layout values agree within half a CSS pixel."
        //
        // Used when the question is "would these two values rasterise to the
        // same pixel?". A half-pixel tolerance is the safe rounding boundary
        // for an integer-pixel output target: two values within ±0.5 round
        // to the same pixel and so are visually indistinguishable. Examples:
        // ShrinkFit cache equality (BlockLayout / FlexLayout / GridLayout /
        // PositioningPass), Grid cell-fit alignment tolerance,
        // LayoutEngine.Incremental's NearlyEqual cached-vs-fresh check (see
        // D5 below), Flex container-cross-size equality.
        //
        // D5 (CODE_AUDIT_FINDINGS.md): LayoutEngine.cs's NearlySame at
        // SubPixelEqual and LayoutEngine.Incremental.cs's NearlyEqual at
        // HalfPixelEqual share a signature but answer different questions:
        // NearlySame asks "did the new pass produce the same numeric output
        // as the cached one" (strict, sub-px) — NearlyEqual asks "is the
        // outer geometry close enough that an incremental skip is safe"
        // (looser, half-px, because incremental skip is a paint-equivalence
        // call and paint can't tell a sub-px diff). The two constants intend
        // to be different. Do not consolidate them.
        public const double HalfPixelEqual = 0.5;

        // D10 (CODE_AUDIT_FINDINGS.md) — helper for the "are these two values
        // half-pixel-equal?" question. Use this in preference to inlining
        // `Math.Abs(a - b) < HalfPixelEqual` so the literal 0.5 isn't easily
        // confused with the centering factor 0.5 that appears in adjacent
        // arithmetic (e.g. `extra * 0.5` in JustifyContent.Center) — see MN3.
        //
        // Boundary is inclusive (`<=`): two values exactly 0.5 apart still
        // round to the same pixel (banker's rounding picks one consistently),
        // so they're paint-equivalent. The pre-helper sites used `<` (strict);
        // moving to `<=` widens the equality bucket by exactly the boundary
        // value and is safe because the boundary case is measure-zero in
        // practice (no layout pass produces values whose difference is
        // bit-exactly 0.5).
        public static bool IsHalfPixelEqual(double a, double b)
            => System.Math.Abs(a - b) <= HalfPixelEqual;

        // "Below this threshold is FontEngine subpixel jitter or accumulated
        // double rounding from many additions."
        //
        // The relative-epsilon floor for flex wrap decisions
        // (FlexLayout.FlexWrapEpsilonMinPx = 0.01 is a px-floor for the
        // wrap-specific relative epsilon; this constant is for the relative
        // half itself: a fraction-of-container scale). Examples: FlexLayout
        // wrap-boundary `containerMainSize * 1e-5`, FlexLayout relative
        // fraction floor when distributing free space. Two orders of
        // magnitude below half a CSS px so it never masks a legitimate
        // half-px overflow.
        public const double LayoutNoise = 1e-5;

        // "Below this is double-precision noise."
        //
        // Used when the question is "is this value a bare zero?". A double
        // has ~15 significant digits, so any non-zero arithmetic result
        // below 1e-9 is almost certainly the trailing roundoff bits of a
        // computation that intended zero. Examples: LineBreaker tab-stop
        // delta = 0, ValueInterpolator bare-zero length detection (CSS
        // Values L4 §6.1: 0 with no unit is equivalent to 0px). Distinct
        // from LayoutNoise because LayoutNoise scales with layout
        // magnitudes; MachineEpsilon scales with floating-point precision.
        public const double MachineEpsilon = 1e-9;
    }
}
