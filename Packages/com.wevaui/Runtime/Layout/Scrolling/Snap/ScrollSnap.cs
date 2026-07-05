using System;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Layout.Scrolling.Snap {
    public enum SnapStrictness {
        None,
        Mandatory,
        Proximity
    }

    public enum SnapAxis {
        Both,
        X,
        Y
    }

    public enum SnapAlign {
        None,
        Start,
        End,
        Center
    }

    public enum SnapStop {
        Normal,
        Always
    }

    public readonly struct SnapType {
        public readonly SnapStrictness Strictness;
        public readonly SnapAxis Axis;

        public SnapType(SnapStrictness strictness, SnapAxis axis) {
            Strictness = strictness;
            Axis = axis;
        }

        public bool IsActive => Strictness != SnapStrictness.None;

        public static SnapType None => new SnapType(SnapStrictness.None, SnapAxis.Both);
    }

    public readonly struct SnapAlignPair {
        public readonly SnapAlign Block;
        public readonly SnapAlign Inline;

        public SnapAlignPair(SnapAlign block, SnapAlign inline) {
            Block = block;
            Inline = inline;
        }

        public static SnapAlignPair None => new SnapAlignPair(SnapAlign.None, SnapAlign.None);

        public bool IsActive => Block != SnapAlign.None || Inline != SnapAlign.None;
    }

    public static class SnapParser {
        // Shared so each Parse call doesn't allocate a fresh delimiter array.
        static readonly char[] s_TokenSeparators = { ' ', '\t' };

        public static SnapType ParseType(string raw) {
            if (string.IsNullOrEmpty(raw)) return SnapType.None;
            string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            if (s == "none") return SnapType.None;

            SnapAxis axis = SnapAxis.Both;
            SnapStrictness strict = SnapStrictness.Proximity;
            bool sawStrict = false;

            var parts = s.Split(s_TokenSeparators, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts) {
                switch (p) {
                    case "x": axis = SnapAxis.X; break;
                    case "y": axis = SnapAxis.Y; break;
                    case "both": axis = SnapAxis.Both; break;
                    case "block": axis = SnapAxis.Y; break;
                    case "inline": axis = SnapAxis.X; break;
                    case "mandatory": strict = SnapStrictness.Mandatory; sawStrict = true; break;
                    case "proximity": strict = SnapStrictness.Proximity; sawStrict = true; break;
                }
            }
            if (!sawStrict) strict = SnapStrictness.Proximity;
            return new SnapType(strict, axis);
        }

        public static SnapAlignPair ParseAlign(string raw) {
            if (string.IsNullOrEmpty(raw)) return SnapAlignPair.None;
            string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            if (s == "none") return SnapAlignPair.None;
            var parts = s.Split(s_TokenSeparators, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) {
                var a = ParseAlignKeyword(parts[0]);
                return new SnapAlignPair(a, a);
            }
            var block = ParseAlignKeyword(parts[0]);
            var inline = ParseAlignKeyword(parts[1]);
            return new SnapAlignPair(block, inline);
        }

        public static SnapStop ParseStop(string raw) {
            if (string.IsNullOrEmpty(raw)) return SnapStop.Normal;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "always") ? SnapStop.Always : SnapStop.Normal;
        }

        public static double ParsePadding(string raw) {
            if (string.IsNullOrEmpty(raw)) return 0;
            string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            if (s == "auto" || s == "none") return 0;
            return ParseLengthPx(s);
        }

        static double ParseLengthPx(string s) {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-' || s[i] == '+')) i++;
            if (!double.TryParse(s.AsSpan(0, i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return 0;
            return v;
        }

        static SnapAlign ParseAlignKeyword(string s) {
            switch (s) {
                case "start": return SnapAlign.Start;
                case "end": return SnapAlign.End;
                case "center": return SnapAlign.Center;
                default: return SnapAlign.None;
            }
        }
    }

    public static class ScrollSnapProperties {
        static bool registered;

        public static void EnsureRegistered() {
            if (registered) return;
            registered = true;
            CssProperties.Register("scroll-snap-type", false, "none");
            CssProperties.Register("scroll-snap-align", false, "none");
            CssProperties.Register("scroll-snap-stop", false, "normal");
            CssProperties.Register("scroll-padding", false, "auto");
            CssProperties.Register("scroll-padding-top", false, "auto");
            CssProperties.Register("scroll-padding-right", false, "auto");
            CssProperties.Register("scroll-padding-bottom", false, "auto");
            CssProperties.Register("scroll-padding-left", false, "auto");
            CssProperties.Register("scroll-margin", false, "0");
            CssProperties.Register("scroll-margin-top", false, "0");
            CssProperties.Register("scroll-margin-right", false, "0");
            CssProperties.Register("scroll-margin-bottom", false, "0");
            CssProperties.Register("scroll-margin-left", false, "0");
        }
    }
}
