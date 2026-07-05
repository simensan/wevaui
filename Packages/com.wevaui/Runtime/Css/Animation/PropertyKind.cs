namespace Weva.Css.Animation {
    public enum PropertyKind {
        Length,
        Color,
        Number,
        // CSS Transitions L1 §2.3 — integer-typed animatable values
        // interpolate as real numbers, then round to nearest integer for
        // exposure via computed-value / getComputedStyle. Distinct from
        // Number so `z-index: 0 → 11` at t=0.5 emits "6" rather than "5.5".
        Integer,
        Percentage,
        Transform,
        Filter,
        // Multi-component animatable kinds — H18b.
        // BackgroundPosition / BackgroundSize: comma-separated layer list of
        // <length-percentage> pairs (or auto/cover/contain keywords). Each
        // layer's two axes lerp independently when both sides are numeric;
        // mismatched layer counts or keyword presence falls back to discrete.
        BackgroundPosition,
        BackgroundSize,
        // BoxShadow / TextShadow: comma-separated list of shadow components
        // (offset-x, offset-y, blur, [spread], color, [inset]). When both
        // lists have the same length, interpolate per-shadow per-component;
        // mismatched inset flag on a paired shadow falls back to discrete
        // for that interpolation (CSS Backgrounds 3 §3.5).
        BoxShadow,
        TextShadow,
        // ClipPath: basic-shape interpolation. Same-shape pairs
        // (inset()↔inset(), circle()↔circle(), ellipse()↔ellipse(),
        // polygon()↔polygon() with matching point counts) lerp per component;
        // different shapes are discrete (CSS Masking §6).
        ClipPath,
        // CSS Transforms L2 §13 — individual transform properties interpolate
        // per-component (the value is a tuple of lengths/angles/numbers and
        // each component lerps independently). Distinct from PropertyKind.
        // Transform (the `transform` shorthand) which is a function-list and
        // routes through the per-function path with matrix-decomposition for
        // mismatched shapes (G9). Missing components fill from the property's
        // identity (translate→0, scale→1, rotate→0deg) per spec.
        Translate,
        Rotate,
        Scale,
        // CSS Images L3 §3.5 + CSS Transitions L1 §2.3 — gradient-valued
        // properties interpolate per-stop when both endpoints are the same
        // gradient type with the same angle/direction/shape and the same
        // number of stops. Each stop lerps its position and color independently.
        // When shapes differ (mismatched type, angle, or stop count), or when
        // either endpoint is `none`/a url(), the interpolation falls back to
        // discrete (t < 0.5 ? from : to). A9.
        Gradient,
        Discrete
    }
}
