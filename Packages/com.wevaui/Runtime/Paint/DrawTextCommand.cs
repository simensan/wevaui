using System;

namespace Weva.Paint {
    public sealed class DrawTextCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public string Text { get; private set; }
        public FontHandle Font { get; private set; }
        public LinearColor Color { get; private set; }
        public TextDecoration Decoration { get; private set; }
        // CSS Text Module Level 3 §10.1: extra inter-character spacing in pixels.
        // Resolved at the layout step (em → px) so the text agent can stay unit-agnostic.
        // Default 0 means "use the font's natural advance".
        public double LetterSpacingPx { get; private set; }

        // CSS Text Decoration 4 fields. `DecorationColor` carries the parsed
        // text-decoration-color; HasDecorationColor=false means "fall back to
        // the run color" (the v0 behaviour, preserved for back-compat). Style
        // selects Solid / Double / Dotted / Dashed / Wavy. Thickness/Offset
        // are pre-resolved px (auto -> font-derived defaults at the resolver).
        public LinearColor DecorationColor { get; private set; }
        public bool HasDecorationColor { get; private set; }
        public DecorationStyle DecorationStyle { get; private set; }
        public double DecorationThickness { get; private set; }
        public double DecorationOffset { get; private set; }

        // CSS Text Decoration §6 `text-shadow` blur-radius, in CSS pixels.
        // Non-zero only on the per-shadow phantom DrawTextCommand emitted from
        // BoxToPaintConverter.EmitTextRun. The URP/SDF backend tags each
        // SdfGlyphQuad with this radius; the shader widens its SDF AA band by
        // `blur` screen pixels so the glyph silhouette feathers outward.
        // Zero means "render the text crisp" — the default for non-shadow
        // text and for zero-blur drop shadows like `text-shadow: 1px 1px 0
        // black`. The renderer fast-paths zero blur (no extra cost).
        //
        // Path A tradeoffs (non-Gaussian SDF dilation): falloff is linear in
        // SDF distance rather than the spec's Gaussian convolution, and the
        // halo is capped by the atlas's SDF padding (typically ~4–8 px).
        // For typical CSS values (blur ≤ 6 px) the result is visually
        // indistinguishable from Gaussian; very wide blur (≥ 12 px) reads
        // as a soft-but-truncated halo. A v2 RT-Gaussian path can replace
        // this without touching the converter or per-glyph plumbing.
        public double BlurRadius { get; private set; }

        // CSS Fonts L4 §6.5 — `font-kerning: none` disables glyph-pair
        // kerning at the shaper level. true is the cascade default (`auto`
        // / `normal`); only the explicit `none` resolves to false.
        public bool KerningEnabled { get; private set; } = true;

        // CSS Backgrounds 4 `background-clip: text`: when non-null, the glyphs
        // are filled by sampling this gradient over `Bounds` (instead of the
        // solid `Color`), so the element's gradient appears clipped to the
        // text. Set by BoxToPaintConverter when the element has
        // background-clip:text and a gradient background; consumed by the
        // SDF text backend, which recolours each shaped glyph. Null = solid
        // `Color` (the default for all normal text). v1: the gradient spans the
        // run's box (correct for single-line text such as the gradient stats /
        // headline span); multi-line elements would tile the gradient per line.
        public Gradient TextFillGradient { get; private set; }

        // Layout-computed baseline offset from Bounds.Y (CSS Inline Layout §3:
        // the run's alphabetic baseline within its line box). When set (not
        // NaN), the glyph baker places the baseline HERE instead of deriving it
        // from the text shaper's own vertical layout — the shaper (TextCore's
        // TextGenerator) puts the baseline at the bottom of the run box, which
        // for tight line boxes pushes the glyphs low and can overflow the box
        // (canonical: match3 `.combo-banner` "SWEET!" jammed against the pill
        // bottom). Aligning paint to the layout baseline keeps text rendered
        // where layout placed it. NaN = "no layout baseline; use the shaper's"
        // (forms value/placeholder commands and any legacy caller).
        public double LayoutBaseline { get; private set; } = double.NaN;

        public DrawTextCommand() : base(PaintCommandKind.DrawText) { }

        public DrawTextCommand(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration)
            : this(bounds, text, font, color, decoration, 0) { }

        public DrawTextCommand(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration, double letterSpacingPx) : base(PaintCommandKind.DrawText) {
            Bounds = bounds;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Font = font;
            Color = color;
            Decoration = decoration;
            LetterSpacingPx = letterSpacingPx;
            DecorationColor = default;
            HasDecorationColor = false;
            DecorationStyle = DecorationStyle.Solid;
            DecorationThickness = 0;
            DecorationOffset = 0;
            BlurRadius = 0;
            KerningEnabled = true;
            LayoutBaseline = double.NaN;
        }

        public DrawTextCommand(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration,
                               double letterSpacingPx, LinearColor? decorationColor, DecorationStyle decorationStyle,
                               double decorationThickness, double decorationOffset) : base(PaintCommandKind.DrawText) {
            Bounds = bounds;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Font = font;
            Color = color;
            Decoration = decoration;
            LetterSpacingPx = letterSpacingPx;
            DecorationColor = decorationColor ?? default;
            HasDecorationColor = decorationColor.HasValue;
            DecorationStyle = decorationStyle;
            DecorationThickness = decorationThickness;
            DecorationOffset = decorationOffset;
            BlurRadius = 0;
            KerningEnabled = true;
            LayoutBaseline = double.NaN;
        }

        public void Set(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration) {
            Set(bounds, text, font, color, decoration, 0);
        }

        public void Set(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration, double letterSpacingPx) {
            Bounds = bounds;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Font = font;
            Color = color;
            Decoration = decoration;
            LetterSpacingPx = letterSpacingPx;
            DecorationColor = default;
            HasDecorationColor = false;
            DecorationStyle = DecorationStyle.Solid;
            DecorationThickness = 0;
            DecorationOffset = 0;
            BlurRadius = 0;
            KerningEnabled = true;
            LayoutBaseline = double.NaN;
        }

        public void Set(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration,
                        double letterSpacingPx, LinearColor? decorationColor, DecorationStyle decorationStyle,
                        double decorationThickness, double decorationOffset) {
            Bounds = bounds;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Font = font;
            Color = color;
            Decoration = decoration;
            LetterSpacingPx = letterSpacingPx;
            DecorationColor = decorationColor ?? default;
            HasDecorationColor = decorationColor.HasValue;
            DecorationStyle = decorationStyle;
            DecorationThickness = decorationThickness;
            DecorationOffset = decorationOffset;
            BlurRadius = 0;
            KerningEnabled = true;
            LayoutBaseline = double.NaN;
        }

        // Text-shadow overload. `blurRadius` is the CSS Text Decoration §6
        // blur-radius value in CSS pixels (already clamped to >= 0 by the
        // resolver). Renderer treats this as the SDF dilation amount.
        public void Set(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration,
                        double letterSpacingPx, double blurRadius) {
            Bounds = bounds;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Font = font;
            Color = color;
            Decoration = decoration;
            LetterSpacingPx = letterSpacingPx;
            DecorationColor = default;
            HasDecorationColor = false;
            DecorationStyle = DecorationStyle.Solid;
            DecorationThickness = 0;
            DecorationOffset = 0;
            BlurRadius = blurRadius > 0 ? blurRadius : 0;
            KerningEnabled = true;
            LayoutBaseline = double.NaN;
        }

        public void SetKerningEnabled(bool kerningEnabled) {
            KerningEnabled = kerningEnabled;
        }

        public void SetLayoutBaseline(double baselineFromBoundsTop) {
            LayoutBaseline = baselineFromBoundsTop;
        }

        public void SetTextFillGradient(Gradient gradient) {
            TextFillGradient = gradient;
        }

        public void Reset() {
            Bounds = default;
            Text = null;
            Font = default;
            Color = default;
            Decoration = default;
            LetterSpacingPx = 0;
            DecorationColor = default;
            HasDecorationColor = false;
            DecorationStyle = DecorationStyle.Solid;
            DecorationThickness = 0;
            DecorationOffset = 0;
            BlurRadius = 0;
            KerningEnabled = true;
            TextFillGradient = null;
            LayoutBaseline = double.NaN;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
