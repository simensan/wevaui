using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // Resolved (axisTag, value) tuple. AxisTag is the 4-char OpenType axis
    // name — `wght` (weight 1..1000), `opsz` (optical size in points),
    // `wdth` (width percentage), `slnt` (slant degrees), `ital` (0..1),
    // plus any author-defined custom axis on a variable font.
    public readonly struct FontAxis : System.IEquatable<FontAxis> {
        public readonly string Tag;   // exactly 4 chars (OpenType tag)
        public readonly float Value;
        public FontAxis(string tag, float value) { Tag = tag; Value = value; }
        public bool Equals(FontAxis other) => Tag == other.Tag && Value == other.Value;
        public override bool Equals(object obj) => obj is FontAxis a && Equals(a);
        public override int GetHashCode() {
            unchecked { return ((Tag != null ? Tag.GetHashCode() : 0) * 397) ^ Value.GetHashCode(); }
        }
    }

    // Parses CSS Fonts 4 `font-variation-settings` declarations and emits
    // FontAxis tuples that the text-run resolver can hand to FontAsset.
    //
    // Grammar: `font-variation-settings: normal | <feature-tag-value>#`
    //   <feature-tag-value> = <string> [ <number> | <percentage> | <integer> ]
    //
    // We accept the parsed CssValueList form produced by CssValueParser. Each
    // comma-separated item is a Space-separated list whose first child is a
    // CssString (the axis tag) and second is a CssNumber/CssPercentage/CssLength
    // (for `opsz` authors sometimes use a px length, which we coerce to the
    // numeric value).
    public static class FontVariationResolver {
        // Single shared empty array — most runs have no variation settings, so
        // returning a fresh List<> per call would dominate the resolver's
        // allocation profile.
        static readonly IReadOnlyList<FontAxis> Empty = System.Array.Empty<FontAxis>();

        public static IReadOnlyList<FontAxis> Resolve(ComputedStyle style, LengthContext ctx) {
            if (style == null) return Empty;
            var parsed = style.GetParsed(CssProperties.FontVariationSettingsId);

            var list = new List<FontAxis>();
            bool parsedIsNormal = parsed == null
                || (parsed is CssKeyword k && k.Identifier == "normal")
                || (parsed is CssIdentifier id && CssStringUtil.EqualsIgnoreCaseTrimmed(id.Name, "normal"));
            if (!parsedIsNormal) {
                if (parsed is CssValueList outer && outer.Separator == CssValueListSeparator.Comma) {
                    for (int i = 0; i < outer.Items.Count; i++) AppendOne(outer.Items[i], ctx, list);
                } else {
                    AppendOne(parsed, ctx, list);
                }
            }

            // CSS Fonts 4 §6.3 — font-stretch maps to the `wdth` variable-font
            // axis as a percentage. Author-explicit `wdth` via
            // font-variation-settings wins per cascade (it lands in `list`
            // first via the loop above); we only synthesize an entry when
            // no `wdth` already exists.
            float? stretchAsWdth = ResolveStretchAsWdth(style);
            if (stretchAsWdth.HasValue && !ListContainsAxis(list, "wdth")) {
                list.Add(new FontAxis("wdth", stretchAsWdth.Value));
            }

            if (list.Count == 0) return Empty;
            return list;
        }

        static bool ListContainsAxis(List<FontAxis> list, string tag) {
            for (int i = 0; i < list.Count; i++) {
                if (list[i].Tag == tag) return true;
            }
            return false;
        }

        // CSS Fonts 4 §6.3 keyword → wdth percentage mapping. The keyword
        // axis values are normative; engines without a matching variable
        // font axis pick the closest static face. Returns null when the
        // value is `normal` (default 100% — same as no axis override).
        static float? ResolveStretchAsWdth(ComputedStyle style) {
            string raw = style.Get("font-stretch");
            if (string.IsNullOrEmpty(raw)) return null;
            var trimmed = raw.Trim();
            // Fast keyword switch — case-insensitive equality is on a small
            // closed set so we do the cheap byte-compare via lowercase
            // normalisation via CssStringUtil.
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "normal")) return null;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "ultra-condensed")) return 50f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "extra-condensed")) return 62.5f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "condensed")) return 75f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "semi-condensed")) return 87.5f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "semi-expanded")) return 112.5f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "expanded")) return 125f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "extra-expanded")) return 150f;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(trimmed, "ultra-expanded")) return 200f;
            // Explicit percentage — parse via the per-style parsed cache.
            var parsedStretch = style.GetParsed(CssProperties.GetId("font-stretch"));
            if (parsedStretch is CssPercentage p) return (float)p.Value;
            // Bare numbers in CSS aren't valid for font-stretch per spec; ignore.
            return null;
        }

        static void AppendOne(CssValue v, LengthContext ctx, List<FontAxis> output) {
            string tag = null;
            float number = float.NaN;
            if (v is CssValueList inner && inner.Separator == CssValueListSeparator.Space) {
                for (int i = 0; i < inner.Items.Count; i++) {
                    var child = inner.Items[i];
                    if (child is CssString s && tag == null) tag = s.Value;
                    else if (child is CssNumber n && float.IsNaN(number)) number = (float)n.Value;
                    else if (child is CssPercentage p && float.IsNaN(number)) number = (float)p.Value;
                    else if (child is CssLength l && float.IsNaN(number)) number = (float)l.ToPixels(ctx);
                }
            }
            if (tag == null || tag.Length != 4 || float.IsNaN(number)) return;
            output.Add(new FontAxis(tag, number));
        }

        // Drives the `opsz` axis from the resolved font-size when
        // `font-optical-sizing: auto` (the spec default). Returns false when
        // the author explicitly set `font-optical-sizing: none`. CSS Fonts 4
        // §6.10: `auto` ties opsz to the computed font-size in points.
        public static bool ShouldAutoOpticalSize(ComputedStyle style) {
            if (style == null) return true;
            var parsed = style.GetParsed(CssProperties.FontOpticalSizingId);
            if (parsed is CssKeyword k) return k.Identifier != "none";
            if (parsed is CssIdentifier id) return !CssStringUtil.EqualsIgnoreCaseTrimmed(id.Name, "none");
            return true;
        }
    }
}
