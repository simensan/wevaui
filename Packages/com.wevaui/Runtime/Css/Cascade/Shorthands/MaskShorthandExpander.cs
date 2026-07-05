using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Masking 1 `mask` shorthand. Mirrors the background shorthand shape
    // for the subset Weva renders: image / position-size / repeat /
    // origin / clip / mode / composite. Multi-layer values are kept as
    // comma-joined longhand lists.
    public sealed class MaskShorthandExpander : IShorthandExpander {
        public string ShorthandName => "mask";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;

            var layerTokenLists = ShorthandTokenizer.SplitOnComma(tokens);
            var layers = new List<LayerLonghands>(layerTokenLists.Count);
            for (int li = 0; li < layerTokenLists.Count; li++) {
                if (!TryParseLayer(layerTokenLists[li], out var parsed)) yield break;
                layers.Add(parsed);
            }

            yield return new KeyValuePair<string, string>("mask-image", JoinPerLayer(layers, l => l.Image));
            yield return new KeyValuePair<string, string>("mask-mode", JoinPerLayer(layers, l => l.Mode));
            yield return new KeyValuePair<string, string>("mask-repeat", JoinPerLayer(layers, l => l.Repeat));
            yield return new KeyValuePair<string, string>("mask-position", JoinPerLayer(layers, l => l.Position));
            yield return new KeyValuePair<string, string>("mask-size", JoinPerLayer(layers, l => l.Size));
            yield return new KeyValuePair<string, string>("mask-origin", JoinPerLayer(layers, l => l.Origin));
            yield return new KeyValuePair<string, string>("mask-clip", JoinPerLayer(layers, l => l.Clip));
            yield return new KeyValuePair<string, string>("mask-composite", JoinPerLayer(layers, l => l.Composite));
        }

        static bool TryParseLayer(List<string> tokens, out LayerLonghands layer) {
            layer = LayerLonghands.Initial();
            if (tokens.Count == 0) return false;

            bool hasImage = false, hasRepeat = false, hasPosition = false, hasSize = false;
            bool hasMode = false, hasComposite = false;
            int boxKeywordsSeen = 0;
            int i = 0;
            var positionTokens = new List<string>();
            var sizeTokens = new List<string>();

            while (i < tokens.Count) {
                string t = tokens[i];
                if (t == "/") {
                    i++;
                    while (i < tokens.Count && sizeTokens.Count < 2) {
                        string st = tokens[i];
                        if (IsSizeToken(st)) { sizeTokens.Add(st); i++; continue; }
                        break;
                    }
                    if (sizeTokens.Count == 0) return false;
                    hasSize = true;
                    continue;
                }
                if (!hasImage && (ShorthandTokens.IsImageValue(t) || t == "none")) {
                    layer.Image = t; hasImage = true; i++; continue;
                }
                if (!hasRepeat && ShorthandTokens.IsRepeatKeyword(t)) {
                    layer.Repeat = t; hasRepeat = true; i++; continue;
                }
                if (!hasMode && IsModeKeyword(t)) {
                    layer.Mode = t; hasMode = true; i++; continue;
                }
                if (!hasComposite && IsCompositeKeyword(t)) {
                    layer.Composite = t; hasComposite = true; i++; continue;
                }
                if (ShorthandTokens.IsBoxKeyword(t) || t == "no-clip") {
                    if (boxKeywordsSeen == 0) { layer.Origin = t; layer.Clip = t; }
                    else if (boxKeywordsSeen == 1) { layer.Clip = t; }
                    else return false;
                    boxKeywordsSeen++;
                    i++;
                    continue;
                }
                if (IsPositionToken(t)) {
                    if (positionTokens.Count >= 4) return false;
                    positionTokens.Add(t);
                    hasPosition = true;
                    i++;
                    continue;
                }
                return false;
            }

            if (hasPosition) layer.Position = ShorthandTokenizer.Join(positionTokens);
            if (hasSize) layer.Size = ShorthandTokenizer.Join(sizeTokens);
            return true;
        }

        static string JoinPerLayer(List<LayerLonghands> layers, System.Func<LayerLonghands, string> sel) {
            if (layers.Count == 1) return sel(layers[0]);
            var sb = new StringBuilder();
            for (int i = 0; i < layers.Count; i++) {
                if (i > 0) sb.Append(", ");
                sb.Append(sel(layers[i]));
            }
            return sb.ToString();
        }

        static bool IsPositionToken(string s) {
            return ShorthandTokens.IsPositionKeyword(s)
                || s == "0"
                || ShorthandTokens.IsLengthOrPercentage(s)
                || ShorthandTokens.IsCalc(s);
        }

        static bool IsSizeToken(string s) {
            return ShorthandTokens.IsBackgroundSizeKeyword(s)
                || s == "0"
                || ShorthandTokens.IsLengthOrPercentage(s)
                || ShorthandTokens.IsCalc(s);
        }

        static bool IsModeKeyword(string s) {
            return s == "alpha" || s == "luminance" || s == "match-source";
        }

        static bool IsCompositeKeyword(string s) {
            return s == "add" || s == "subtract" || s == "intersect" || s == "exclude";
        }

        struct LayerLonghands {
            public string Image;
            public string Mode;
            public string Repeat;
            public string Position;
            public string Size;
            public string Origin;
            public string Clip;
            public string Composite;

            public static LayerLonghands Initial() {
                return new LayerLonghands {
                    Image = "none",
                    Mode = "match-source",
                    Repeat = "repeat",
                    Position = "0% 0%",
                    Size = "auto",
                    Origin = "border-box",
                    Clip = "border-box",
                    Composite = "add",
                };
            }
        }
    }
}
