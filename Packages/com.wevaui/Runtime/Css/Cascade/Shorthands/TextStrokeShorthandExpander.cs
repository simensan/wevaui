using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Text Decoration L4 §10 `text-stroke` / `-webkit-text-stroke` shorthand.
    // Grammar: `<line-width> || <color>`. Either order is accepted; missing
    // components fall back to the longhand's initial value. The shorthand
    // splits into `-webkit-text-stroke-width` and `-webkit-text-stroke-color`.
    //
    // Examples:
    //   -webkit-text-stroke: 1px;                  -> width=1px,     color=currentcolor
    //   -webkit-text-stroke: red;                  -> width=0,       color=red
    //   -webkit-text-stroke: 2px black;            -> width=2px,     color=black
    //   -webkit-text-stroke: black 2px;            -> width=2px,     color=black
    public sealed class TextStrokeShorthandExpander : IShorthandExpander {
        readonly string shorthandName;
        public string ShorthandName => shorthandName;

        public TextStrokeShorthandExpander() : this("-webkit-text-stroke") { }
        public TextStrokeShorthandExpander(string name) { shorthandName = name; }

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;

            string width = null;
            string color = null;
            foreach (var t in tokens) {
                if (width == null && (ShorthandTokens.IsLength(t) || ShorthandTokens.IsBorderWidthKeyword(t) || ShorthandTokens.IsZeroNumber(t))) {
                    width = t;
                    continue;
                }
                if (color == null && ShorthandTokens.IsColor(t)) {
                    color = t;
                    continue;
                }
                // Unknown token — bail out rather than guess; CSS lets the
                // declaration be ignored as a whole.
                yield break;
            }
            yield return new KeyValuePair<string, string>("-webkit-text-stroke-width", width ?? "0");
            yield return new KeyValuePair<string, string>("-webkit-text-stroke-color", color ?? "currentcolor");
        }
    }
}
