using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // Expands CSS logical border shorthands into logical longhands. The
    // cascade later aliases those logical longhands onto physical edges using
    // the element's computed writing-mode/direction.
    public sealed class LogicalBorderShorthandExpander : IShorthandExpander {
        enum Mode {
            AxisBorder,
            SideBorder,
            AxisWidth,
            AxisStyle,
            AxisColor
        }

        readonly Mode mode;
        readonly string axis;
        readonly string side;

        public string ShorthandName { get; }

        public LogicalBorderShorthandExpander(string shorthandName, string axis, string side) {
            ShorthandName = shorthandName;
            this.axis = axis;
            this.side = side;
            mode = side != null ? Mode.SideBorder : Mode.AxisBorder;
        }

        LogicalBorderShorthandExpander(string shorthandName, string axis, Mode mode) {
            ShorthandName = shorthandName;
            this.axis = axis;
            this.mode = mode;
        }

        public static LogicalBorderShorthandExpander AxisWidth(string shorthandName, string axis) =>
            new LogicalBorderShorthandExpander(shorthandName, axis, Mode.AxisWidth);

        public static LogicalBorderShorthandExpander AxisStyle(string shorthandName, string axis) =>
            new LogicalBorderShorthandExpander(shorthandName, axis, Mode.AxisStyle);

        public static LogicalBorderShorthandExpander AxisColor(string shorthandName, string axis) =>
            new LogicalBorderShorthandExpander(shorthandName, axis, Mode.AxisColor);

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            switch (mode) {
                case Mode.SideBorder:
                    foreach (var kv in ExpandBorderSide(tokens, axis + "-" + side)) yield return kv;
                    yield break;
                case Mode.AxisBorder:
                    foreach (var kv in ExpandBorderSide(tokens, axis + "-start")) yield return kv;
                    foreach (var kv in ExpandBorderSide(tokens, axis + "-end")) yield return kv;
                    yield break;
                case Mode.AxisWidth:
                    foreach (var kv in ExpandAxis(tokens, "width", IsWidth)) yield return kv;
                    yield break;
                case Mode.AxisStyle:
                    foreach (var kv in ExpandAxis(tokens, "style", ShorthandTokens.IsBorderStyle)) yield return kv;
                    yield break;
                case Mode.AxisColor:
                    foreach (var kv in ExpandAxis(tokens, "color", ShorthandTokens.IsColor)) yield return kv;
                    yield break;
            }
        }

        IEnumerable<KeyValuePair<string, string>> ExpandBorderSide(List<string> tokens, string logicalSide) {
            if (!ParseBorderTriplet(tokens, out string width, out string style, out string color)) yield break;
            yield return new KeyValuePair<string, string>("border-" + logicalSide + "-width", width);
            yield return new KeyValuePair<string, string>("border-" + logicalSide + "-style", style);
            yield return new KeyValuePair<string, string>("border-" + logicalSide + "-color", color);
        }

        IEnumerable<KeyValuePair<string, string>> ExpandAxis(List<string> tokens, string component, System.Func<string, bool> isValid) {
            if (tokens.Count == 0 || tokens.Count > 2) yield break;
            for (int i = 0; i < tokens.Count; i++) {
                if (!isValid(tokens[i])) yield break;
            }
            string start = tokens[0];
            string end = tokens.Count == 2 ? tokens[1] : tokens[0];
            yield return new KeyValuePair<string, string>("border-" + axis + "-start-" + component, start);
            yield return new KeyValuePair<string, string>("border-" + axis + "-end-" + component, end);
        }

        static bool ParseBorderTriplet(List<string> tokens, out string width, out string style, out string color) {
            width = "medium";
            style = "none";
            color = "currentcolor";
            if (tokens.Count == 0 || tokens.Count > 3) return false;
            bool hasWidth = false, hasStyle = false, hasColor = false;
            for (int i = 0; i < tokens.Count; i++) {
                var t = tokens[i];
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

        static bool IsWidth(string s) {
            if (ShorthandTokens.IsBorderWidthKeyword(s)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLength(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
