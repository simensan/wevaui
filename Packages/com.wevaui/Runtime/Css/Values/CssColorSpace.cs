namespace Weva.Css.Values {
    public enum CssColorSpace {
        Srgb,
        LinearRgb,
        Oklch,
        Oklab,
        Hsl,
        Hwb,
        // CSS Color 4 §10 — CIELab / CIELCh
        Lab,
        Lch,
        // CSS Color 4 §15/§17 — wide-gamut color() spaces
        DisplayP3,
        Rec2020,
        A98Rgb,
        ProPhotoRgb,
        XyzD65,
        XyzD50,
    }
}
