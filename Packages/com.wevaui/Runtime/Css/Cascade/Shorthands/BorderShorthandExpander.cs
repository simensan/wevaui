using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // Handles `border`, `border-top|right|bottom|left`, `border-width`, `border-style`,
    // and `border-color`. Per CSS spec, missing components reset to their initial values:
    // width → medium, style → none, color → currentcolor.
    public sealed class BorderShorthandExpander : IShorthandExpander {
        public enum Mode {
            Border,         // border: → all 12 longhands
            BorderSide,     // border-{side}: → 3 longhands for that side
            BorderWidth,    // border-width: → 4 sides' width
            BorderStyle,    // border-style: → 4 sides' style
            BorderColor     // border-color: → 4 sides' color
        }

        static readonly string[] Sides = { "top", "right", "bottom", "left" };

        readonly Mode mode;
        readonly string side;

        public string ShorthandName { get; }

        public BorderShorthandExpander(string shorthandName, Mode mode, string side) {
            ShorthandName = shorthandName;
            this.mode = mode;
            this.side = side;
        }

        public static BorderShorthandExpander Border() => new BorderShorthandExpander("border", Mode.Border, null);
        public static BorderShorthandExpander BorderTop() => new BorderShorthandExpander("border-top", Mode.BorderSide, "top");
        public static BorderShorthandExpander BorderRight() => new BorderShorthandExpander("border-right", Mode.BorderSide, "right");
        public static BorderShorthandExpander BorderBottom() => new BorderShorthandExpander("border-bottom", Mode.BorderSide, "bottom");
        public static BorderShorthandExpander BorderLeft() => new BorderShorthandExpander("border-left", Mode.BorderSide, "left");
        public static BorderShorthandExpander BorderWidth() => new BorderShorthandExpander("border-width", Mode.BorderWidth, null);
        public static BorderShorthandExpander BorderStyle() => new BorderShorthandExpander("border-style", Mode.BorderStyle, null);
        public static BorderShorthandExpander BorderColor() => new BorderShorthandExpander("border-color", Mode.BorderColor, null);

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            switch (mode) {
                case Mode.Border:
                    return ExpandBorder(tokens);
                case Mode.BorderSide:
                    return ExpandBorderSide(tokens);
                case Mode.BorderWidth:
                    return ExpandFourSided(tokens, "border-{0}-width", IsWidth);
                case Mode.BorderStyle:
                    return ExpandFourSided(tokens, "border-{0}-style", ShorthandTokens.IsBorderStyle);
                case Mode.BorderColor:
                    return ExpandFourSided(tokens, "border-{0}-color", ShorthandTokens.IsColor);
            }
            return System.Linq.Enumerable.Empty<KeyValuePair<string, string>>();
        }

        IEnumerable<KeyValuePair<string, string>> ExpandBorder(List<string> tokens) {
            if (!ParseBorderTriplet(tokens, out string width, out string style, out string color)) yield break;
            foreach (var s in Sides) {
                yield return new KeyValuePair<string, string>("border-" + s + "-width", width);
                yield return new KeyValuePair<string, string>("border-" + s + "-style", style);
                yield return new KeyValuePair<string, string>("border-" + s + "-color", color);
            }
        }

        IEnumerable<KeyValuePair<string, string>> ExpandBorderSide(List<string> tokens) {
            if (!ParseBorderTriplet(tokens, out string width, out string style, out string color)) yield break;
            yield return new KeyValuePair<string, string>("border-" + side + "-width", width);
            yield return new KeyValuePair<string, string>("border-" + side + "-style", style);
            yield return new KeyValuePair<string, string>("border-" + side + "-color", color);
        }

        // Parses up to three space-separated tokens in any order. Each token must be
        // a width, a style, or a color (each category at most once). Missing components
        // reset to their initial values per the CSS Backgrounds & Borders spec.
        static bool ParseBorderTriplet(List<string> tokens, out string width, out string style, out string color) {
            width = "medium";
            style = "none";
            color = "currentcolor";
            if (tokens.Count == 0 || tokens.Count > 3) return false;
            bool hasWidth = false, hasStyle = false, hasColor = false;
            foreach (var t in tokens) {
                if (!hasStyle && ShorthandTokens.IsBorderStyle(t)) {
                    style = t;
                    hasStyle = true;
                    continue;
                }
                if (!hasWidth && IsWidth(t)) {
                    width = t;
                    hasWidth = true;
                    continue;
                }
                if (!hasColor && ShorthandTokens.IsColor(t)) {
                    color = t;
                    hasColor = true;
                    continue;
                }
                return false;
            }
            return true;
        }

        static IEnumerable<KeyValuePair<string, string>> ExpandFourSided(List<string> tokens, string pattern, System.Func<string, bool> isValid) {
            if (tokens.Count == 0 || tokens.Count > 4) yield break;
            foreach (var t in tokens) {
                if (!isValid(t)) yield break;
            }
            string top, right, bottom, left;
            switch (tokens.Count) {
                case 1: top = right = bottom = left = tokens[0]; break;
                case 2: top = bottom = tokens[0]; right = left = tokens[1]; break;
                case 3: top = tokens[0]; right = left = tokens[1]; bottom = tokens[2]; break;
                default: top = tokens[0]; right = tokens[1]; bottom = tokens[2]; left = tokens[3]; break;
            }
            yield return new KeyValuePair<string, string>(string.Format(pattern, "top"), top);
            yield return new KeyValuePair<string, string>(string.Format(pattern, "right"), right);
            yield return new KeyValuePair<string, string>(string.Format(pattern, "bottom"), bottom);
            yield return new KeyValuePair<string, string>(string.Format(pattern, "left"), left);
        }

        static bool IsWidth(string s) {
            if (ShorthandTokens.IsBorderWidthKeyword(s)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLength(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
