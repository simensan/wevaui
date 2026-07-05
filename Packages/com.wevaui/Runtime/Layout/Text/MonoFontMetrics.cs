namespace Weva.Layout.Text {
    // MonoFontMetrics is the headless / test-fixture shim that returns
    // deterministic per-em values for IFontMetrics queries. It exists so
    // NUnit tests + the SoftwareRasterizer + the LayoutDiff harness can
    // exercise the full layout pipeline without a Unity FontEngine.
    //
    // Two presets are exposed beyond the (back-compat) parameterless ctor:
    //
    //   ChromeSansSerif() — calibrated to Chrome's default body font on
    //                       fragment snippets (Times New Roman serif).
    //                       GoldenRunner + LayoutDiffTests use this so
    //                       text-bearing layouts measure within tolerance
    //                       of Chrome's getBoundingClientRect ground truth.
    //
    //   ChromeMonospace() — calibrated to Chrome's monospace stack. Wired
    //                       via LayoutContext.RegisterFont("monospace", …)
    //                       so snippets that opt into font-family: monospace
    //                       get the wider per-glyph advance Chrome's
    //                       monospace font actually produces.
    //
    // The parameterless ctor preserves the historical 0.5/1.2/0.8/0.4 shape.
    // ~3,900 PlayMode tests hard-code expectations against those values
    // (e.g. "5 chars × 16px = 40px wide", "line-height = 19.2") — changing
    // the parameterless ctor would cascade into dozens of unrelated
    // assertion failures, so it stays stable. New callers should prefer
    // the named factories when Chrome parity is the goal.
    //
    // Calibration notes (Chrome 124, default sans-serif body):
    //   - line-height `normal` ≈ 1.143em across the common system stack.
    //     We split that into 0.85em ascent + 0.293em descent.
    //   - Avg per-glyph advance for proportional fonts is roughly 0.45em
    //     across mixed casing at body sizes. This is a compromise: real
    //     Chrome metrics vary by glyph (i ≈ 0.28em, w ≈ 0.71em) and by
    //     size (hinting tightens letterforms at large sizes), so any
    //     single scalar will be off by a few percent in either direction.
    //     0.45em closes most of the X-axis drift the per-snippet tolerance
    //     JSONs were widened to absorb.
    //   - Monospace advance is roughly 0.6em across Chrome's "Courier
    //     New" / "Consolas" stack at body sizes.
    //
    // For full per-glyph fidelity, see SdfFontMetrics / TmpFontMetrics —
    // those route through a real TTF or TMP_FontAsset and produce
    // per-codepoint advances. They're the production path (SdfBootstrap
    // picks them up) but require a live Unity FontEngine, which the
    // headless tests + GoldenRunner can't supply.
    //
    // K2 (CSS_COMPLIANCE_ISSUES.md) — DELIBERATE DIVERGENCE from production.
    // TextCoreFontMetrics derives line-height from face Ascent+Descent+LineGap
    // (real font metadata). MonoFontMetrics does fontSize * LineHeightEm
    // (a flat 1.2 in the parameterless ctor; 1.143 in the Chrome* factories).
    // This is intentional: MonoFontMetrics is the headless test stand-in and
    // must produce identical numbers across machines / Unity versions / fonts
    // installed. It is never reached in production unless the entire bootstrap
    // chain (TMP -> Sdf -> UnityGUI) fails — SdfBootstrap.cs overwrites
    // UIDocumentDefaults.FontMetricsFactory at SubsystemRegistration. The
    // 1.2x factor is also what TextCoreFontMetrics itself falls back to when
    // a face reports zero LineGap AND zero LineHeight (see
    // TextCoreFontMetrics.ScaleFor:173), so the two formulas line up in the
    // degenerate-face case anyway. Pinned by MonoFontMetricsTests.
    public sealed class MonoFontMetrics : IFontMetrics {
        public double CharWidthEm { get; }
        public double LineHeightEm { get; }
        public double AscentEm { get; }
        public double DescentEm { get; }

        // Legacy default. Hard-coded into hundreds of inline-layout / line-
        // breaker / box-tree assertions; do not change without auditing
        // every direct `new MonoFontMetrics()` call site. The 1.2 ratio
        // mirrors `StyleResolver.DefaultLineHeightFactor` (CSS Values L4
        // §6.2) — kept named to make the intent explicit.
        public MonoFontMetrics() : this(0.5, Weva.Layout.StyleResolver.DefaultLineHeightFactor, 0.8, 0.4) { }

        public MonoFontMetrics(double charWidthEm, double lineHeightEm, double ascentEm, double descentEm) {
            CharWidthEm = charWidthEm;
            LineHeightEm = lineHeightEm;
            AscentEm = ascentEm;
            DescentEm = descentEm;
        }

        // Chrome's default body font (proportional sans-serif / Times New
        // Roman fallback on fragment snippets). See class doc for the
        // calibration source. Closes most of the X / Y drift in the
        // LayoutDiff snippet suite. Prefer this over the parameterless
        // ctor for any code path that compares against Chrome.
        public static MonoFontMetrics ChromeSansSerif() {
            return new MonoFontMetrics(
                charWidthEm: 0.45,
                lineHeightEm: 1.143,
                ascentEm: 0.85,
                descentEm: 0.293);
        }

        // Chrome's default monospace stack. Wider per-glyph advance than
        // sans-serif (monospace's "natural" em is around 0.6), same line
        // metrics. Note this does NOT replicate Chrome's font-size quirk
        // where `font: monospace` without an explicit size resolves to
        // 13px instead of 16px — that's a separate cascade-level concern.
        public static MonoFontMetrics ChromeMonospace() {
            return new MonoFontMetrics(
                charWidthEm: 0.6,
                lineHeightEm: 1.143,
                ascentEm: 0.85,
                descentEm: 0.293);
        }

        public double LineHeight(double fontSize) => fontSize * LineHeightEm;
        public double Ascent(double fontSize) => fontSize * AscentEm;
        public double Descent(double fontSize) => fontSize * DescentEm;
        public double Measure(string text, double fontSize) {
            if (string.IsNullOrEmpty(text)) return 0;
            return Measure(text, 0, text.Length, fontSize);
        }

        public double Measure(string text, int start, int length, double fontSize) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            int end = start + length;
            if (end > text.Length) end = text.Length;
            // Approximate Chrome's full-width emoji rendering: emoji codepoints
            // (BMP symbols U+2600-U+27BF and supplementary U+1F000+) measure
            // ~2em wide in browsers, while the surrounding ASCII stays at the
            // monospace 0.5em. Without this, the dump test underestimates
            // emoji glyph widths by ~17px each (e.g. "⚡" reports 9px instead
            // of ~26px). Detect via codepoint range and apply 1.3em.
            double total = 0;
            for (int i = start; i < end; i++) {
                int cp;
                if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                } else {
                    cp = text[i];
                }
                // Wide-glyph (emoji) detection: Chrome renders certain
                // codepoints at ~1.3em width via the system emoji font.
                // - U+1F000+ Supplementary Plane: reliably emoji-wide.
                // - U+2700-27BF Dingbats: most are emoji-presented in Chrome
                //   (✓ ✕ ✦ ❄ ➤ etc.). Some text-presentation variants exist
                //   but the demo uses fully-qualified emoji forms.
                // - Specific BMP allowlist: ⚡ U+26A1, ⛔ U+26D4, ☀ U+2600,
                //   ☔ U+2614, ☕ U+2615, ☘ U+2618, ☠ U+2620 etc.
                // Excluded: U+2300-23FF Miscellaneous Technical (⌂ U+2302
                // is text-presented), U+2600-269F mostly text symbols
                // (☾ U+263E text), U+2200-22FF Math.
                bool isWideEmoji = (cp >= 0x1F000 && cp <= 0x1FAFF)
                            || cp == 0x26A1
                            || cp == 0x26D4
                            || cp == 0x2600
                            || cp == 0x2614
                            || cp == 0x2615
                            || cp == 0x2618
                            || cp == 0x2620
                            || cp == 0x2705;
                bool isMediumEmoji = (cp >= 0x2700 && cp <= 0x27BF)
                            || cp == 0x2699
                            || cp == 0x2298;  // ⊘ U+2298 CIRCLED DIVISION SLASH
                bool isEmoji = isWideEmoji || isMediumEmoji;
                double widthEm = isWideEmoji ? 1.3 : (isMediumEmoji ? 1.0 : CharWidthEm);
                total += widthEm * fontSize;
            }
            return total;
        }
    }
}
