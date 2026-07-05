using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // Expands `place-items`, `place-content`, `place-self` to their align-/justify-
    // longhand pairs. Per spec, when only one value is given it applies to both axes.
    public sealed class PlaceShorthandExpander : IShorthandExpander {
        readonly string alignLonghand;
        readonly string justifyLonghand;

        public string ShorthandName { get; }

        public PlaceShorthandExpander(string shorthandName, string alignLonghand, string justifyLonghand) {
            ShorthandName = shorthandName;
            this.alignLonghand = alignLonghand;
            this.justifyLonghand = justifyLonghand;
        }

        public static PlaceShorthandExpander PlaceItems() => new PlaceShorthandExpander("place-items", "align-items", "justify-items");
        public static PlaceShorthandExpander PlaceContent() => new PlaceShorthandExpander("place-content", "align-content", "justify-content");
        public static PlaceShorthandExpander PlaceSelf() => new PlaceShorthandExpander("place-self", "align-self", "justify-self");

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;
            foreach (var t in tokens) {
                if (t == "," || t == "/") yield break;
            }
            string a = tokens[0];
            string b = tokens.Count == 2 ? tokens[1] : tokens[0];
            yield return new KeyValuePair<string, string>(alignLonghand, a);
            yield return new KeyValuePair<string, string>(justifyLonghand, b);
        }
    }
}
