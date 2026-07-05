using Weva.Css.Media;

namespace Weva.EditorTools.Preview {
    public enum PreviewViewportPreset {
        Mobile,
        Tablet,
        Desktop,
        Wide,
        Custom
    }

    public enum PreviewColorScheme {
        Light,
        Dark
    }

    public readonly struct PreviewViewport {
        public int Width { get; }
        public int Height { get; }
        public float Dpi { get; }
        public PreviewColorScheme ColorScheme { get; }
        public PreviewViewportPreset Preset { get; }

        public PreviewViewport(int width, int height, float dpi, PreviewColorScheme scheme, PreviewViewportPreset preset) {
            Width = width;
            Height = height;
            Dpi = dpi;
            ColorScheme = scheme;
            Preset = preset;
        }

        public static PreviewViewport Default => FromPreset(PreviewViewportPreset.Desktop, PreviewColorScheme.Light);

        public static PreviewViewport FromPreset(PreviewViewportPreset preset, PreviewColorScheme scheme) {
            switch (preset) {
                case PreviewViewportPreset.Mobile:  return new PreviewViewport(390, 844, 96f, scheme, preset);
                case PreviewViewportPreset.Tablet:  return new PreviewViewport(820, 1180, 96f, scheme, preset);
                case PreviewViewportPreset.Wide:    return new PreviewViewport(1920, 1080, 96f, scheme, preset);
                case PreviewViewportPreset.Custom:  return new PreviewViewport(1280, 720, 96f, scheme, preset);
                case PreviewViewportPreset.Desktop:
                default:                            return new PreviewViewport(1280, 720, 96f, scheme, preset);
            }
        }

        public PreviewViewport WithSize(int w, int h) =>
            new PreviewViewport(w, h, Dpi, ColorScheme, PreviewViewportPreset.Custom);

        public PreviewViewport WithColorScheme(PreviewColorScheme scheme) =>
            new PreviewViewport(Width, Height, Dpi, scheme, Preset);

        public MediaContext ToMediaContext() {
            var cs = ColorScheme == PreviewColorScheme.Dark
                ? Weva.Css.Media.ColorScheme.Dark
                : Weva.Css.Media.ColorScheme.Light;
            return new MediaContext(
                Width,
                Height,
                Dpi,
                cs,
                HoverCapability.Hover,
                PointerCapability.Fine,
                false,
                MediaType.Screen);
        }
    }
}
