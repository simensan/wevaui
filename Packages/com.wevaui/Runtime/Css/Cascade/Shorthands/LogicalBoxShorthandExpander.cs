using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // Expands CSS logical two-sided shorthands:
    // margin-inline/block, padding-inline/block, and inset-inline/block.
    public sealed class LogicalBoxShorthandExpander : IShorthandExpander {
        readonly string prefix;
        readonly string axis;
        readonly bool allowAuto;

        public string ShorthandName { get; }

        public LogicalBoxShorthandExpander(string shorthandName, string prefix, string axis, bool allowAuto) {
            ShorthandName = shorthandName;
            this.prefix = prefix;
            this.axis = axis;
            this.allowAuto = allowAuto;
        }

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;
            for (int i = 0; i < tokens.Count; i++) {
                if (!IsValid(tokens[i])) yield break;
            }
            string start = tokens[0];
            string end = tokens.Count == 2 ? tokens[1] : tokens[0];
            yield return new KeyValuePair<string, string>(prefix + "-" + axis + "-start", start);
            yield return new KeyValuePair<string, string>(prefix + "-" + axis + "-end", end);
        }

        bool IsValid(string s) {
            if (allowAuto && string.Equals(s, "auto", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
