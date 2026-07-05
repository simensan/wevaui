namespace Weva.Css.Values {
    public struct LengthContext {
        public double BaseFontSizePx;
        public double RootFontSizePx;
        public double ViewportWidthPx;
        public double ViewportHeightPx;
        public double DpiPixelsPerInch;
        public double? BasisPixels;
        public double LineHeightPx;
        public double RootLineHeightPx;

        public static LengthContext Default => new LengthContext {
            BaseFontSizePx = 16,
            RootFontSizePx = 16,
            ViewportWidthPx = 1920,
            ViewportHeightPx = 1080,
            DpiPixelsPerInch = 96,
            BasisPixels = null,
            LineHeightPx = 0,
            RootLineHeightPx = 0
        };

        public LengthContext WithBasis(double basisPx) {
            var c = this;
            c.BasisPixels = basisPx;
            return c;
        }
    }
}
