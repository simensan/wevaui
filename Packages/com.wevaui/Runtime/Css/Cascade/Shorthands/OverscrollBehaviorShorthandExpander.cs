using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Overscroll Behavior 1 §2 — `overscroll-behavior: <value>{1,2}` is a
    // shorthand for `overscroll-behavior-x` / `-y`. One value applies to both
    // axes; two values map to x then y per the spec's standard 2-value rule.
    public sealed class OverscrollBehaviorShorthandExpander : IShorthandExpander {
        public string ShorthandName => "overscroll-behavior";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;
            foreach (var t in tokens) {
                if (!IsOverscrollKeyword(t)) yield break;
            }
            string x = tokens[0];
            string y = tokens.Count == 2 ? tokens[1] : tokens[0];
            yield return new KeyValuePair<string, string>("overscroll-behavior-x", x);
            yield return new KeyValuePair<string, string>("overscroll-behavior-y", y);
        }

        static bool IsOverscrollKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "auto":
                case "contain":
                case "none":
                    return true;
                default:
                    return false;
            }
        }
    }
}
