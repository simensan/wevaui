using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    public sealed class FlexFlowShorthandExpander : IShorthandExpander {
        public string ShorthandName => "flex-flow";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;

            string direction = "row";
            string wrap = "nowrap";
            bool hasDir = false, hasWrap = false;

            foreach (var t in tokens) {
                if (!hasDir && IsDirection(t)) { direction = t; hasDir = true; continue; }
                if (!hasWrap && IsWrap(t)) { wrap = t; hasWrap = true; continue; }
                yield break;
            }
            if (!hasDir && !hasWrap) yield break;

            yield return new KeyValuePair<string, string>("flex-direction", direction);
            yield return new KeyValuePair<string, string>("flex-wrap", wrap);
        }

        static bool IsDirection(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "row": case "row-reverse": case "column": case "column-reverse":
                    return true;
                default:
                    return false;
            }
        }

        static bool IsWrap(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "nowrap": case "wrap": case "wrap-reverse":
                    return true;
                default:
                    return false;
            }
        }
    }
}
