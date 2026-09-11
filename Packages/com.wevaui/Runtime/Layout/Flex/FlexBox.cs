using Weva.Layout.Boxes;

namespace Weva.Layout.Flex {
    public sealed class FlexBox : BlockBox {
        // The gaps as FlexLayout resolved them, with the container's real
        // LengthContext. PositioningPass's intrinsic helpers have no context —
        // they are static and take only the box — so they used to read the gap
        // by scanning the declaration for its first number. That is right for
        // `12px` and wrong for anything computed: `clamp(4px, 0.6vmin, 7px)`
        // gave 4 where the real value is 4.32, so a column flex measured as a
        // grid or flex ITEM came out one gap-difference short of the children
        // it then placed. randhtml's `.party` reported 107.706 while its own
        // children spanned 108.026.
        //
        // NaN means "not resolved yet" — 0 is a legitimate gap.
        public double ResolvedRowGap { get; internal set; } = double.NaN;
        public double ResolvedColumnGap { get; internal set; } = double.NaN;
        public bool IsInline { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            IsInline = false;
            // Back to "not resolved yet" — a recycled box must not hand the
            // intrinsic helpers the previous document's gap.
            ResolvedRowGap = double.NaN;
            ResolvedColumnGap = double.NaN;
        }
    }
}
