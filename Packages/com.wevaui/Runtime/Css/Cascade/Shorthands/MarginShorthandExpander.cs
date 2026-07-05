using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    public sealed class MarginShorthandExpander : IShorthandExpander {
        readonly string prefix;
        readonly bool allowAuto;

        public string ShorthandName { get; }

        public MarginShorthandExpander(string shorthandName, string longhandPrefix, bool allowAuto) {
            ShorthandName = shorthandName;
            prefix = longhandPrefix;
            this.allowAuto = allowAuto;
        }

        public static MarginShorthandExpander Margin() => new MarginShorthandExpander("margin", "margin", true);
        public static MarginShorthandExpander Padding() => new MarginShorthandExpander("padding", "padding", false);
        // CSS Scroll Snap 1 §3: scroll-padding accepts `auto | <length-percentage>`,
        // scroll-margin accepts `<length>` only. Reuse the same 1-4 value
        // decomposition so the cascade emits per-side longhands and the snap
        // resolver doesn't need to re-parse the shorthand at consumption time.
        public static MarginShorthandExpander ScrollPadding() => new MarginShorthandExpander("scroll-padding", "scroll-padding", true);
        public static MarginShorthandExpander ScrollMargin() => new MarginShorthandExpander("scroll-margin", "scroll-margin", false);

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 4) yield break;
            for (int i = 0; i < tokens.Count; i++) {
                if (!IsValidEdge(tokens[i])) yield break;
            }
            string top, right, bottom, left;
            switch (tokens.Count) {
                case 1:
                    top = right = bottom = left = tokens[0];
                    break;
                case 2:
                    top = bottom = tokens[0];
                    right = left = tokens[1];
                    break;
                case 3:
                    top = tokens[0];
                    right = left = tokens[1];
                    bottom = tokens[2];
                    break;
                default:
                    top = tokens[0];
                    right = tokens[1];
                    bottom = tokens[2];
                    left = tokens[3];
                    break;
            }
            yield return new KeyValuePair<string, string>(prefix + "-top", top);
            yield return new KeyValuePair<string, string>(prefix + "-right", right);
            yield return new KeyValuePair<string, string>(prefix + "-bottom", bottom);
            yield return new KeyValuePair<string, string>(prefix + "-left", left);
        }

        bool IsValidEdge(string s) {
            if (allowAuto && string.Equals(s, "auto", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
