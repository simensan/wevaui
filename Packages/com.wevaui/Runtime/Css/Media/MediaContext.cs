namespace Weva.Css.Media {
    public readonly struct MediaContext {
        public double ViewportWidthPx { get; }
        public double ViewportHeightPx { get; }
        public double DpiPixelsPerInch { get; }
        public ColorScheme ColorScheme { get; }
        public HoverCapability Hover { get; }
        public PointerCapability Pointer { get; }
        public bool PrefersReducedMotion { get; }
        public MediaType Type { get; }

        public Orientation Orientation =>
            ViewportWidthPx >= ViewportHeightPx ? Orientation.Landscape : Orientation.Portrait;

        public MediaContext(
            double viewportWidthPx,
            double viewportHeightPx,
            double dpiPixelsPerInch,
            ColorScheme colorScheme,
            HoverCapability hover,
            PointerCapability pointer,
            bool prefersReducedMotion,
            MediaType type) {
            ViewportWidthPx = viewportWidthPx;
            ViewportHeightPx = viewportHeightPx;
            DpiPixelsPerInch = dpiPixelsPerInch;
            ColorScheme = colorScheme;
            Hover = hover;
            Pointer = pointer;
            PrefersReducedMotion = prefersReducedMotion;
            Type = type;
        }

        public static MediaContext Default(double width, double height) =>
            new MediaContext(
                width,
                height,
                96,
                ColorScheme.Light,
                HoverCapability.Hover,
                PointerCapability.Fine,
                false,
                MediaType.Screen);

        public MediaContext WithViewport(double width, double height) =>
            new MediaContext(width, height, DpiPixelsPerInch, ColorScheme, Hover, Pointer, PrefersReducedMotion, Type);

        public MediaContext WithDpi(double dpi) =>
            new MediaContext(ViewportWidthPx, ViewportHeightPx, dpi, ColorScheme, Hover, Pointer, PrefersReducedMotion, Type);

        public MediaContext WithColorScheme(ColorScheme scheme) =>
            new MediaContext(ViewportWidthPx, ViewportHeightPx, DpiPixelsPerInch, scheme, Hover, Pointer, PrefersReducedMotion, Type);

        public MediaContext WithHover(HoverCapability hover) =>
            new MediaContext(ViewportWidthPx, ViewportHeightPx, DpiPixelsPerInch, ColorScheme, hover, Pointer, PrefersReducedMotion, Type);

        public MediaContext WithPointer(PointerCapability pointer) =>
            new MediaContext(ViewportWidthPx, ViewportHeightPx, DpiPixelsPerInch, ColorScheme, Hover, pointer, PrefersReducedMotion, Type);

        public MediaContext WithReducedMotion(bool reduce) =>
            new MediaContext(ViewportWidthPx, ViewportHeightPx, DpiPixelsPerInch, ColorScheme, Hover, Pointer, reduce, Type);

        public MediaContext WithType(MediaType type) =>
            new MediaContext(ViewportWidthPx, ViewportHeightPx, DpiPixelsPerInch, ColorScheme, Hover, Pointer, PrefersReducedMotion, type);
    }
}
