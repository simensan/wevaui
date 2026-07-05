namespace Weva.Layout.Grid {
    public enum GridTrackKind {
        Length,
        Percentage,
        Auto,
        MinContent,
        MaxContent,
        Fr,
        Minmax,
        // CSS Grid L1 §7.2.3: fit-content(<length-percentage>) — equivalent to
        // minmax(auto, max-content) but the upper bound is clamped to the
        // argument. Encoded with MaxKind/MaxValue carrying the limit
        // (Length px or Percentage value).
        FitContent
    }
}
