using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // Expands `border-radius: <h1> [<h2> [<h3> [<h4>]]] [ / <v1> [<v2> [<v3> [<v4>]]] ]`
    // to the four corner longhands. Each side is the standard 1-4 value box-corner
    // fill (TL, TR, BR, BL). With a "/" the values before it are the horizontal
    // radii and the values after are the vertical radii, producing elliptical
    // corners — emitted as a two-token "rx ry" longhand (BorderRadiiResolver.
    // ResolveCorner reads that as a CornerRadius(x, y)). CSS Backgrounds & Borders L3 §5.
    public sealed class BorderRadiusShorthandExpander : IShorthandExpander {
        public string ShorthandName => "border-radius";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;

            // Split horizontal / vertical groups at the "/" delimiter.
            int slash = tokens.IndexOf("/");
            List<string> h, v;
            if (slash >= 0) {
                h = tokens.GetRange(0, slash);
                v = tokens.GetRange(slash + 1, tokens.Count - slash - 1);
            } else {
                h = tokens;
                v = null;
            }

            if (h.Count == 0 || h.Count > 4) yield break;
            if (v != null && (v.Count == 0 || v.Count > 4)) yield break;
            for (int i = 0; i < h.Count; i++) if (!IsValidRadius(h[i])) yield break;
            if (v != null) for (int i = 0; i < v.Count; i++) if (!IsValidRadius(v[i])) yield break;

            var hc = ExpandFour(h);
            var vc = v != null ? ExpandFour(v) : hc;

            yield return Corner("border-top-left-radius", hc[0], vc[0]);
            yield return Corner("border-top-right-radius", hc[1], vc[1]);
            yield return Corner("border-bottom-right-radius", hc[2], vc[2]);
            yield return Corner("border-bottom-left-radius", hc[3], vc[3]);
        }

        // Box-corner 1-4 fill order: TL, TR, BR, BL.
        static string[] ExpandFour(List<string> t) {
            switch (t.Count) {
                case 1: return new[] { t[0], t[0], t[0], t[0] };
                case 2: return new[] { t[0], t[1], t[0], t[1] };
                case 3: return new[] { t[0], t[1], t[2], t[1] };
                default: return new[] { t[0], t[1], t[2], t[3] };
            }
        }

        static KeyValuePair<string, string> Corner(string name, string x, string y) {
            // Collapse to a single token when the axes match (the common circular
            // case) so the longhand round-trips identically to the pre-elliptical
            // behaviour; emit "x y" only for genuinely elliptical corners.
            string val = x == y ? x : x + " " + y;
            return new KeyValuePair<string, string>(name, val);
        }

        static bool IsValidRadius(string s) {
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
