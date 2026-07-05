using System.Collections.Generic;

namespace Weva.Css {
    // CSS Fonts 4 §11 — `@font-face` descriptor set.
    // v2 adds the full descriptor set on top of the v1 (family, src) pair:
    //   font-weight   — single value or range (variable-axis)
    //   font-style    — normal | italic | oblique [<angle> [<angle>]]
    //   font-stretch  — keyword or percentage, optionally a two-value range
    //   unicode-range — list of U+ ranges (start, end) pairs; wildcards expanded
    //   font-display  — auto | block | swap | fallback | optional
    //   src           — ordered list of FontSrcEntry (url/local + optional format)
    //
    // Runtime-honour status (as of this implementation):
    //   font-family   — runtime-honoured (FontResolver.RegisterFont)
    //   src url()     — runtime-honoured (first url() used by FontResolver)
    //   All other descriptors — parse-only; available on the AST node for
    //   future font-matching (weight/style/stretch axis) and subsetting
    //   (unicode-range) consumers. font-display is parse-only until a
    //   deferred-load pipeline is wired.
    public sealed class FontFaceRule : Rule {
        // Descriptor: font-family (required, runtime-honoured)
        public string FontFamily;

        // Descriptor: src — ordered list. The first usable entry wins.
        // For back-compat, Src (string) still holds the first resolved url() path.
        public string Src;
        public List<FontSrcEntry> SrcList = new List<FontSrcEntry>();

        // Descriptor: font-weight (parse-only)
        // Single value: WeightMin == WeightMax. Unset == null.
        public float? WeightMin;
        public float? WeightMax;

        // Descriptor: font-style (parse-only)
        public FontFaceStyleValue FontStyle = FontFaceStyleValue.Normal;
        // For oblique: angle in degrees. ObliqueAngleMin == ObliqueAngleMax for single angle.
        public float ObliqueAngleMin;
        public float ObliqueAngleMax;

        // Descriptor: font-stretch (parse-only)
        // Stored as percentage (e.g. 100f = normal, 75f = condensed).
        // Single value: StretchMin == StretchMax. Unset means no descriptor given.
        public float? StretchMin;
        public float? StretchMax;

        // Descriptor: unicode-range (parse-only)
        // Each entry is a (start codepoint, end codepoint) inclusive pair.
        public List<(int Start, int End)> UnicodeRange = new List<(int, int)>();

        // Descriptor: font-display (parse-only)
        public FontDisplayValue FontDisplay = FontDisplayValue.Auto;
    }

    public sealed class FontSrcEntry {
        // Either Url or LocalName is set, not both.
        public string Url;      // from url("...") — the path inside the url()
        public string LocalName; // from local("...") or local(Name)
        // Optional format hint, lowercased (e.g. "woff2", "truetype", "opentype")
        public string Format;

        public bool IsLocal => LocalName != null;
    }

    public enum FontFaceStyleValue {
        Normal,
        Italic,
        Oblique
    }

    public enum FontDisplayValue {
        Auto,
        Block,
        Swap,
        Fallback,
        Optional
    }
}
