namespace Weva.Layout {
    // CSS Sizing L4 §5 — deriving one dimension from the other through
    // `aspect-ratio`.
    //
    // The ratio relates the two dimensions of the box that `box-sizing`
    // selects. With the default `content-box` that means it relates CONTENT
    // width to CONTENT height: the horizontal frame comes off before the
    // divide, and the vertical frame goes back on after. Dividing the
    // BORDER-box width and then adding the vertical frame counts the frame
    // twice — `width: 101px; border: 1px; aspect-ratio: 1/1` came out 105 where
    // Chrome and the C++ port both say 103, and inventory.html's `.slot` was
    // 208.5 against their 206.5.
    //
    // This lives in one place because the derivation had been written out four
    // times — BlockLayout.FinalizeBlockSize, LayoutEngine's aspect-ratio fixup,
    // and both of FlexLayout's directional helpers — and every copy had the
    // same bug. Boxes carry BORDER-box Width/Height throughout the engine, so
    // both the input and the result here are border-box values.
    static class AspectRatioMath {
        // Border-box height for a box whose border-box width is known.
        // `ratio` is width / height, as `aspect-ratio` is written.
        public static double HeightFromWidth(double borderBoxWidth, double ratio, bool borderBox,
                                             double widthFrame, double heightFrame) {
            if (ratio <= 0) return 0;
            double basis = borderBox ? borderBoxWidth : borderBoxWidth - widthFrame;
            if (basis < 0) basis = 0;
            double derived = basis / ratio;
            return borderBox ? derived : derived + heightFrame;
        }

        // Border-box width for a box whose border-box height is known.
        public static double WidthFromHeight(double borderBoxHeight, double ratio, bool borderBox,
                                             double heightFrame, double widthFrame) {
            if (ratio <= 0) return 0;
            double basis = borderBox ? borderBoxHeight : borderBoxHeight - heightFrame;
            if (basis < 0) basis = 0;
            double derived = basis * ratio;
            return borderBox ? derived : derived + widthFrame;
        }
    }
}
