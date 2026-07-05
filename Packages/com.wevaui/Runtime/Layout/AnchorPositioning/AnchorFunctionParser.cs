using System;
using System.Globalization;

namespace Weva.Layout.AnchorPositioning {
    // AnchorFunctionParser — parses CSS `anchor()` and `anchor-size()` function calls.
    //
    // `anchor()` (v1 — appears in inset properties top/right/bottom/left):
    //   anchor( <anchor-name>? <anchor-edge> [ +/- <length> ]? )
    // Examples:
    //   anchor(bottom)
    //   anchor(--tooltip bottom)
    //   anchor(bottom + 8px)
    //   anchor(--tooltip bottom - 4px)
    //
    // `anchor-size()` (v2 — appears in width/height/min-*/max-*):
    //   anchor-size( <anchor-name>? <anchor-axis>? )
    // Examples:
    //   anchor-size(width)
    //   anchor-size(--btn width)
    //   anchor-size(height)
    //
    // The optional `<anchor-name>` is the explicit anchor reference; when
    // absent, the resolver falls back to the box's `position-anchor` property.
    //
    // We do not support fallback values inside the function or `<percentage>`
    // arguments to anchor-size(); v1+ as needed.
    public static class AnchorFunctionParser {
        public static bool TryParse(string raw, out AnchorFunctionCall call) {
            call = default;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (!s.StartsWith("anchor(", StringComparison.OrdinalIgnoreCase)) return false;
            if (!s.EndsWith(")", StringComparison.Ordinal)) return false;

            string inner = s.Substring("anchor(".Length, s.Length - "anchor(".Length - 1).Trim();
            if (inner.Length == 0) return false;

            string lhs = inner;
            string offsetPart = null;
            int sign = +1;
            for (int i = 1; i < inner.Length; i++) {
                char c = inner[i];
                if (c == '+' || c == '-') {
                    char prev = inner[i - 1];
                    // Skip `--` (custom-property prefix) and other paired
                    // sign chars like `+-` that aren't operators.
                    if (c == '-' && prev == '-') continue;
                    if (prev == '+' || prev == '-') continue;
                    // Decide whether THIS `+`/`-` is the operator between
                    // lhs and an offset length, or just part of lhs (e.g.,
                    // mid-name `--anchor-name-extra`). The disambiguator:
                    // look at what comes after. A length starts with a
                    // digit or `.`. Whitespace between them is OK and we
                    // skip it. Without this, `anchor(bottom+8px)` failed
                    // to split because prev='m' is not whitespace, and
                    // `anchor(--a-name-extra top)` would now wrongly split
                    // on the inner `-` if we just allowed any non-ws prev.
                    int j = i + 1;
                    while (j < inner.Length && IsWhitespace(inner[j])) j++;
                    if (j >= inner.Length) continue;
                    char next = inner[j];
                    if (!((next >= '0' && next <= '9') || next == '.')) continue;
                    lhs = inner.Substring(0, i).Trim();
                    offsetPart = inner.Substring(i + 1).Trim();
                    sign = c == '-' ? -1 : +1;
                    break;
                }
            }

            string name = null;
            string edgeStr;
            var lhsParts = SplitOnWhitespace(lhs);
            if (lhsParts.Length == 1) {
                edgeStr = lhsParts[0];
            } else if (lhsParts.Length == 2) {
                name = lhsParts[0];
                edgeStr = lhsParts[1];
            } else {
                return false;
            }

            if (!TryParseEdge(edgeStr, out var edge)) return false;

            double offsetPx = 0;
            if (offsetPart != null) {
                if (!TryParsePixelLength(offsetPart, out double px)) return false;
                offsetPx = px * sign;
            }

            call = new AnchorFunctionCall(name, edge, offsetPx);
            return true;
        }

        // anchor-size(<anchor-name>? <axis>?) — `axis` is one of width / height /
        // block / inline / self-block / self-inline. v1: width/height literally,
        // and the logical axes map to the same physical axis (LTR-only).
        public static bool TryParseSize(string raw, out AnchorSizeCall call) {
            call = default;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (!s.StartsWith("anchor-size(", StringComparison.OrdinalIgnoreCase)) return false;
            if (!s.EndsWith(")", StringComparison.Ordinal)) return false;
            string inner = s.Substring("anchor-size(".Length, s.Length - "anchor-size(".Length - 1).Trim();

            string name = null;
            AnchorSizeAxis axis = AnchorSizeAxis.Inferred;
            if (inner.Length > 0) {
                var parts = SplitOnWhitespace(inner);
                if (parts.Length == 1) {
                    if (parts[0].StartsWith("--")) {
                        name = parts[0];
                    } else if (!TryParseAxis(parts[0], out axis)) {
                        return false;
                    }
                } else if (parts.Length == 2) {
                    name = parts[0];
                    if (!TryParseAxis(parts[1], out axis)) return false;
                } else {
                    return false;
                }
            }
            call = new AnchorSizeCall(name, axis);
            return true;
        }

        public static bool TryParseEdge(string raw, out AnchorEdge edge) {
            edge = default;
            if (string.IsNullOrEmpty(raw)) return false;
            // Allocation-free keyword dispatch — the prior
            // raw.Trim().ToLowerInvariant() copied the string twice per
            // anchor() resolution. Anchor positioning fires once per
            // consumer side per layout pass.
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "top")) { edge = AnchorEdge.Top; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "right")) { edge = AnchorEdge.Right; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "bottom")) { edge = AnchorEdge.Bottom; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "left")) { edge = AnchorEdge.Left; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "start")) { edge = AnchorEdge.Start; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "end")) { edge = AnchorEdge.End; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "self-start")) { edge = AnchorEdge.SelfStart; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "self-end")) { edge = AnchorEdge.SelfEnd; return true; }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "center")) { edge = AnchorEdge.Center; return true; }
            return false;
        }

        static bool TryParseAxis(string raw, out AnchorSizeAxis axis) {
            axis = default;
            if (string.IsNullOrEmpty(raw)) return false;
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "width")
                || Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline")
                || Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "self-inline")) {
                axis = AnchorSizeAxis.Width; return true;
            }
            if (Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "height")
                || Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "block")
                || Weva.Css.Values.CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "self-block")) {
                axis = AnchorSizeAxis.Height; return true;
            }
            return false;
        }

        static bool TryParsePixelLength(string raw, out double pixels) {
            pixels = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) {
                return double.TryParse(s.AsSpan(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
        }

        // Shared so each SplitOnWhitespace doesn't allocate a fresh delimiter array.
        static readonly char[] s_WhitespaceSeparators = { ' ', '\t' };

        static string[] SplitOnWhitespace(string s) {
            return s.Split(s_WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        }

        static bool IsWhitespace(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r';
        }
    }

    public readonly struct AnchorFunctionCall {
        public string AnchorName { get; }
        public AnchorEdge Edge { get; }
        public double OffsetPx { get; }

        public AnchorFunctionCall(string anchorName, AnchorEdge edge, double offsetPx) {
            AnchorName = anchorName;
            Edge = edge;
            OffsetPx = offsetPx;
        }
    }

    public enum AnchorSizeAxis {
        // Inferred: caller picks based on the property being resolved (width
        // properties → Width axis; height properties → Height).
        Inferred,
        Width,
        Height
    }

    public readonly struct AnchorSizeCall {
        public string AnchorName { get; }
        public AnchorSizeAxis Axis { get; }

        public AnchorSizeCall(string anchorName, AnchorSizeAxis axis) {
            AnchorName = anchorName;
            Axis = axis;
        }
    }
}
