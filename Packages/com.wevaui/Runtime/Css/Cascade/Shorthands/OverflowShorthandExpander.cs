using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    public sealed class OverflowShorthandExpander : IShorthandExpander {
        public string ShorthandName => "overflow";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;
            foreach (var t in tokens) {
                if (!IsOverflow(t)) yield break;
            }
            string x = tokens[0];
            string y = tokens.Count == 2 ? tokens[1] : tokens[0];
            yield return new KeyValuePair<string, string>("overflow-x", x);
            yield return new KeyValuePair<string, string>("overflow-y", y);
        }

        static bool IsOverflow(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "visible": case "hidden": case "scroll": case "auto": case "clip":
                    return true;
                default:
                    return false;
            }
        }
    }
}
