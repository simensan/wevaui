using System.Collections.Generic;
using System.Text;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // Expands `animation: <name> <duration> <timing> <delay> <iter> <dir> <fill> <state>[, ...]`
    // into the eight longhand value lists. Tokens are identified by category.
    public sealed class AnimationShorthandExpander : IShorthandExpander {
        public string ShorthandName => "animation";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;
            var groups = ShorthandTokenizer.SplitOnComma(tokens);

            var names = new List<string>();
            var durs = new List<string>();
            var timings = new List<string>();
            var delays = new List<string>();
            var iters = new List<string>();
            var dirs = new List<string>();
            var fills = new List<string>();
            var states = new List<string>();

            foreach (var group in groups) {
                if (group.Count == 0) yield break;
                if (!ParseLayer(group,
                        out string name, out string dur, out string tf, out string delay,
                        out string iter, out string dir, out string fill, out string state)) yield break;
                names.Add(name);
                durs.Add(dur);
                timings.Add(tf);
                delays.Add(delay);
                iters.Add(iter);
                dirs.Add(dir);
                fills.Add(fill);
                states.Add(state);
            }

            yield return new KeyValuePair<string, string>("animation-name", Join(names));
            yield return new KeyValuePair<string, string>("animation-duration", Join(durs));
            yield return new KeyValuePair<string, string>("animation-timing-function", Join(timings));
            yield return new KeyValuePair<string, string>("animation-delay", Join(delays));
            yield return new KeyValuePair<string, string>("animation-iteration-count", Join(iters));
            yield return new KeyValuePair<string, string>("animation-direction", Join(dirs));
            yield return new KeyValuePair<string, string>("animation-fill-mode", Join(fills));
            yield return new KeyValuePair<string, string>("animation-play-state", Join(states));
        }

        static bool ParseLayer(List<string> tokens,
                               out string name, out string duration, out string timing, out string delay,
                               out string iterCount, out string direction, out string fillMode, out string playState) {
            name = "none";
            duration = "0s";
            timing = "ease";
            delay = "0s";
            iterCount = "1";
            direction = "normal";
            fillMode = "none";
            playState = "running";
            bool hasName = false, hasDuration = false, hasTiming = false, hasDelay = false;
            bool hasIter = false, hasDir = false, hasFill = false, hasState = false;
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
                if (!hasIter && ShorthandTokens.IsIterationCount(t)) {
                    iterCount = t;
                    hasIter = true;
                    continue;
                }
                if (!hasDir && ShorthandTokens.IsAnimationDirection(t)) {
                    direction = t;
                    hasDir = true;
                    continue;
                }
                if (!hasFill && ShorthandTokens.IsAnimationFillMode(t)) {
                    // `none` is also the initial animation-name. To prefer the specced
                    // "first identifier consumed by the first matching slot", we accept
                    // `none` here first only if name is already set.
                    if (CssStringUtil.EqualsIgnoreCase(t, "none") && !hasName) {
                        name = t;
                        hasName = true;
                        continue;
                    }
                    fillMode = t;
                    hasFill = true;
                    continue;
                }
                if (!hasState && ShorthandTokens.IsAnimationPlayState(t)) {
                    playState = t;
                    hasState = true;
                    continue;
                }
                if (!hasName && IsAnimationName(t)) {
                    name = t;
                    hasName = true;
                    continue;
                }
                return false;
            }
            return true;
        }

        static bool IsAnimationName(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (s == "none") return true;
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
