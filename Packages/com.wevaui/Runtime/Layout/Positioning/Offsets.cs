namespace Weva.Layout.Positioning {
    public readonly struct Offsets {
        public readonly double? Top;
        public readonly double? Right;
        public readonly double? Bottom;
        public readonly double? Left;

        public Offsets(double? top, double? right, double? bottom, double? left) {
            Top = top;
            Right = right;
            Bottom = bottom;
            Left = left;
        }

        public static Offsets AllAuto => new Offsets(null, null, null, null);

        public bool AllUnset => !Top.HasValue && !Right.HasValue && !Bottom.HasValue && !Left.HasValue;
    }
}
