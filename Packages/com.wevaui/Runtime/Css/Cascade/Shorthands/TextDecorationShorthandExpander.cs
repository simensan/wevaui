using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    public sealed class TextDecorationShorthandExpander : IShorthandExpander {
        public string ShorthandName => "text-decoration";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;

            string line = "none";
            string style = "solid";
            string color = "currentcolor";
            bool hasLine = false, hasStyle = false, hasColor = false;
            // text-decoration-line accepts a space-separated combination of underline,
            // overline, line-through, blink (or `none`). Collect them all.
            var lines = new List<string>();
            foreach (var t in tokens) {
                if (ShorthandTokens.IsTextDecorationLine(t)) {
                    if (CssStringUtil.EqualsIgnoreCase(t, "none")) {
                        if (lines.Count > 0) yield break;
                        lines.Add(t);
                        hasLine = true;
                        continue;
                    }
                    if (lines.Count == 1 && CssStringUtil.EqualsIgnoreCase(lines[0], "none")) yield break;
                    lines.Add(t);
                    hasLine = true;
                    continue;
                }
                if (!hasStyle && ShorthandTokens.IsTextDecorationStyle(t)) {
                    style = t;
                    hasStyle = true;
                    continue;
                }
                if (!hasColor && ShorthandTokens.IsColor(t)) {
                    color = t;
                    hasColor = true;
                    continue;
                }
                yield break;
            }
            if (hasLine) line = string.Join(" ", lines);

            yield return new KeyValuePair<string, string>("text-decoration-line", line);
            yield return new KeyValuePair<string, string>("text-decoration-style", style);
            yield return new KeyValuePair<string, string>("text-decoration-color", color);
        }
    }
}
