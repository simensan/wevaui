namespace Weva.Layout.Flex {
    public enum FlexBasisKind {
        Length,
        Percentage,
        Auto,
        Content
    }

    public readonly struct FlexBasis {
        public FlexBasisKind Kind { get; }
        public double Value { get; }

        public FlexBasis(FlexBasisKind kind, double value) {
            Kind = kind;
            Value = value;
        }

        public static FlexBasis Auto => new FlexBasis(FlexBasisKind.Auto, 0);
        public static FlexBasis Content => new FlexBasis(FlexBasisKind.Content, 0);
        public static FlexBasis Length(double px) => new FlexBasis(FlexBasisKind.Length, px);
        public static FlexBasis Percentage(double pct) => new FlexBasis(FlexBasisKind.Percentage, pct);
    }
}
