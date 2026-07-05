using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Cascade.Shorthands {
    // Expands `transition: <prop> <duration> <timing> <delay>[, ...]` into the four
    // longhands as comma-separated value lists. Each layer is parsed independently;
    // if any layer is malformed, no longhands are produced.
    public sealed class TransitionShorthandExpander : IShorthandExpander {
        public string ShorthandName => "transition";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;
            var groups = ShorthandTokenizer.SplitOnComma(tokens);

            var props = new List<string>();
            var durs = new List<string>();
            var timings = new List<string>();
            var delays = new List<string>();

            foreach (var group in groups) {
                if (group.Count == 0) yield break;
                if (!ParseLayer(group, out string p, out string d, out string tf, out string dl)) yield break;
                props.Add(p);
                durs.Add(d);
                timings.Add(tf);
                delays.Add(dl);
            }

            yield return new KeyValuePair<string, string>("transition-property", Join(props));
            yield return new KeyValuePair<string, string>("transition-duration", Join(durs));
            yield return new KeyValuePair<string, string>("transition-timing-function", Join(timings));
            yield return new KeyValuePair<string, string>("transition-delay", Join(delays));
        }

        static bool ParseLayer(List<string> tokens, out string prop, out string duration, out string timing, out string delay) {
            prop = "all";
            duration = "0s";
            timing = "ease";
            delay = "0s";
            bool hasProp = false, hasDuration = false, hasTiming = false, hasDelay = false;
            foreach (var t in tokens) {
                if (!hasDuration && ShorthandTokens.IsTime(t)) {
                    duration = t;
                    hasDuration = true;
                    continue;
                }
                if (hasDuration && !hasDelay && ShorthandTokens.IsTime(t)) {
                    delay = t;
                    hasDelay = true;
                    continue;
                }
                if (!hasTiming && ShorthandTokens.IsTimingFunction(t)) {
                    timing = t;
                    hasTiming = true;
                    continue;
                }
                if (!hasProp && IsTransitionProperty(t)) {
                    prop = t;
                    hasProp = true;
                    continue;
                }
                return false;
            }
            return true;
        }

        static bool IsTransitionProperty(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (s == "all" || s == "none") return true;
            // Loose CSS-identifier match: starts with letter/underscore/hyphen, then ident chars.
            char c0 = s[0];
            if (!(char.IsLetter(c0) || c0 == '_' || c0 == '-')) return false;
            for (int i = 1; i < s.Length; i++) {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return true;
        }

        static string Join(List<string> values) {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++) {
                if (i > 0) sb.Append(", ");
                sb.Append(values[i]);
            }
            return sb.ToString();
        }
    }
}
