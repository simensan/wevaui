namespace Weva.Layout.Grid {
    public readonly struct GridTrackSize {
        public GridTrackKind Kind { get; }
        public double Value { get; }
        // Minmax carries two child track sizes; we encode them in two parallel
        // optional fields rather than allocating, since track sizes are value
        // types and compose recursively only one level deep in v1 (no nested
        // minmax(minmax(...), ...)).
        public GridTrackKind MinKind { get; }
        public double MinValue { get; }
        public GridTrackKind MaxKind { get; }
        public double MaxValue { get; }

        GridTrackSize(GridTrackKind kind, double value,
            GridTrackKind minKind, double minValue,
            GridTrackKind maxKind, double maxValue) {
            Kind = kind;
            Value = value;
            MinKind = minKind;
            MinValue = minValue;
            MaxKind = maxKind;
            MaxValue = maxValue;
        }

        public static GridTrackSize Length(double px) => new GridTrackSize(GridTrackKind.Length, px, default, 0, default, 0);
        public static GridTrackSize Percentage(double pct) => new GridTrackSize(GridTrackKind.Percentage, pct, default, 0, default, 0);
        public static GridTrackSize Auto => new GridTrackSize(GridTrackKind.Auto, 0, default, 0, default, 0);
        public static GridTrackSize MinContent => new GridTrackSize(GridTrackKind.MinContent, 0, default, 0, default, 0);
        public static GridTrackSize MaxContent => new GridTrackSize(GridTrackKind.MaxContent, 0, default, 0, default, 0);
        public static GridTrackSize Fr(double fr) => new GridTrackSize(GridTrackKind.Fr, fr, default, 0, default, 0);
        public static GridTrackSize Minmax(GridTrackSize min, GridTrackSize max) =>
            new GridTrackSize(GridTrackKind.Minmax, 0, min.Kind, min.Value, max.Kind, max.Value);

        // CSS Grid L1 §7.2.3: fit-content(<length-percentage>). The limit
        // travels in the Max slot (MinKind/MinValue unused) so resolution can
        // reuse the existing Length/Percentage dispatch.
        public static GridTrackSize FitContent(GridTrackSize limit) =>
            new GridTrackSize(GridTrackKind.FitContent, 0, default, 0, limit.Kind, limit.Value);

        public GridTrackSize MinChild() => Kind == GridTrackKind.Minmax ? UnpackChild(MinKind, MinValue) : this;
        public GridTrackSize MaxChild() => Kind == GridTrackKind.Minmax ? UnpackChild(MaxKind, MaxValue) : this;

        static GridTrackSize UnpackChild(GridTrackKind k, double v) {
            switch (k) {
                case GridTrackKind.Length: return Length(v);
                case GridTrackKind.Percentage: return Percentage(v);
                case GridTrackKind.Fr: return Fr(v);
                case GridTrackKind.MinContent: return MinContent;
                case GridTrackKind.MaxContent: return MaxContent;
                case GridTrackKind.Auto: return Auto;
            }
            return Auto;
        }

        public bool IsIntrinsic {
            get {
                switch (Kind) {
                    case GridTrackKind.Auto:
                    case GridTrackKind.MinContent:
                    case GridTrackKind.MaxContent:
                    case GridTrackKind.FitContent: return true;
                    case GridTrackKind.Minmax:
                        return MinKind == GridTrackKind.Auto || MinKind == GridTrackKind.MinContent || MinKind == GridTrackKind.MaxContent ||
                               MaxKind == GridTrackKind.Auto || MaxKind == GridTrackKind.MinContent || MaxKind == GridTrackKind.MaxContent;
                }
                return false;
            }
        }

        public bool IsFlexible {
            get {
                if (Kind == GridTrackKind.Fr) return true;
                if (Kind == GridTrackKind.Minmax) return MaxKind == GridTrackKind.Fr;
                return false;
            }
        }
    }
}
