using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Cascade.Shorthands {
    // Expands the `background` shorthand. Per CSS spec, the shorthand resets all
    // background-* longhands to their initial values; any longhand not explicitly
    // present in the value is emitted at its initial. The position/size pair is
    // separated by '/' (e.g. `center/cover`).
    //
    // Multi-layer (comma-separated) backgrounds: each layer is parsed independently
    // and the longhands are emitted as comma-joined per-layer values. Per spec only
    // the *final* layer may carry `background-color`; if a non-final layer specifies
    // a color the whole shorthand is invalid and we emit nothing.
    public sealed class BackgroundShorthandExpander : IShorthandExpander {
        public string ShorthandName => "background";

        // PA: per-instance pooled scratch. The cascade engine is single-
        // threaded by contract so a singleton expander can safely keep
        // mutable buffers — they get fully consumed each call before the
        // next call's reset. With these in place, the inline `background:`
        // hot path allocates 0 bytes for tokenization / layer parsing on
        // single-layer common-case values (every real-world animated
        // `background: conic-gradient(...)` value falls in this case).
        readonly List<string> tokensScratch = new(16);
        readonly List<List<string>> layerGroupsScratch = new(4);
        readonly List<List<string>> innerListPool = new(4);
        readonly List<LayerLonghands> layersScratch = new(4);
        readonly List<string> positionScratch = new(8);
        readonly List<string> sizeScratch = new(8);
        readonly List<KeyValuePair<string, string>> outputScratch = new(8);

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            ExpandInto(value, outputScratch);
            return outputScratch;
        }

        // PA: alloc-free expansion entry. `output` is the caller's pool —
        // cleared before fill. Pre-emptive yield-style early returns become
        // "clear output and return". Single-layer values short-circuit the
        // per-layer Join (1-element list → return the token verbatim).
        public void ExpandInto(string value, List<KeyValuePair<string, string>> output) {
            output.Clear();
            ShorthandTokenizer.TokenizeInto(value, tokensScratch);
            if (tokensScratch.Count == 0) return;

            int innerPoolUsed = 0;
            ShorthandTokenizer.SplitOnCommaInto(tokensScratch, layerGroupsScratch, innerListPool, ref innerPoolUsed);
            layersScratch.Clear();
            for (int li = 0; li < layerGroupsScratch.Count; li++) {
                bool isFinalLayer = li == layerGroupsScratch.Count - 1;
                if (!TryParseLayer(layerGroupsScratch[li], isFinalLayer, out var parsed)) {
                    return;
                }
                layersScratch.Add(parsed);
            }

            string color = "transparent";
            for (int li = 0; li < layersScratch.Count; li++) {
                if (layersScratch[li].Color != null) {
                    if (li != layersScratch.Count - 1) return;
                    color = layersScratch[li].Color;
                }
            }

            output.Add(new KeyValuePair<string, string>("background-color", color));
            output.Add(new KeyValuePair<string, string>("background-image", JoinPerLayer(layersScratch, BgImageSel)));
            output.Add(new KeyValuePair<string, string>("background-repeat", JoinPerLayer(layersScratch, BgRepeatSel)));
            output.Add(new KeyValuePair<string, string>("background-attachment", JoinPerLayer(layersScratch, BgAttachSel)));
            output.Add(new KeyValuePair<string, string>("background-position", JoinPerLayer(layersScratch, BgPositionSel)));
            output.Add(new KeyValuePair<string, string>("background-size", JoinPerLayer(layersScratch, BgSizeSel)));
            output.Add(new KeyValuePair<string, string>("background-origin", JoinPerLayer(layersScratch, BgOriginSel)));
            output.Add(new KeyValuePair<string, string>("background-clip", JoinPerLayer(layersScratch, BgClipSel)));
        }

        // Static delegate fields so each call site below doesn't allocate a
        // fresh lambda. `JoinPerLayer` takes a delegate; passing a lambda
        // would generate a closure-bound delegate per call.
        static readonly System.Func<LayerLonghands, string> BgImageSel = l => l.Image;
        static readonly System.Func<LayerLonghands, string> BgRepeatSel = l => l.Repeat;
        static readonly System.Func<LayerLonghands, string> BgAttachSel = l => l.Attachment;
        static readonly System.Func<LayerLonghands, string> BgPositionSel = l => l.Position;
        static readonly System.Func<LayerLonghands, string> BgSizeSel = l => l.Size;
        static readonly System.Func<LayerLonghands, string> BgOriginSel = l => l.Origin;
        static readonly System.Func<LayerLonghands, string> BgClipSel = l => l.Clip;

        bool TryParseLayer(List<string> tokens, bool allowColor, out LayerLonghands layer) {
            layer = LayerLonghands.Initial();
            if (tokens.Count == 0) return false;

            bool hasImage = false, hasRepeat = false, hasAttachment = false;
            bool hasPosition = false, hasSize = false, hasColor = false;
            int boxKeywordsSeen = 0;

            int i = 0;
            // PA: scratch lists belong to the expander instance — reuse
            // their backing arrays across every parse call. The per-call
            // pattern previously allocated 2 fresh `new List<string>` per
            // layer, which fired every frame for animated inline values.
            var positionTokens = positionScratch;
            var sizeTokens = sizeScratch;
            positionTokens.Clear();
            sizeTokens.Clear();

            while (i < tokens.Count) {
                string t = tokens[i];
                if (t == "/") {
                    i++;
                    while (i < tokens.Count && sizeTokens.Count < 2) {
                        string st = tokens[i];
                        if (IsBgSizeToken(st)) { sizeTokens.Add(st); i++; continue; }
                        break;
                    }
                    if (sizeTokens.Count == 0) return false;
                    hasSize = true;
                    continue;
                }
                if (!hasImage && ShorthandTokens.IsImageValue(t)) {
                    layer.Image = t; hasImage = true; i++; continue;
                }
                if (!hasRepeat && ShorthandTokens.IsRepeatKeyword(t)) {
                    layer.Repeat = t; hasRepeat = true; i++; continue;
                }
                if (!hasAttachment && ShorthandTokens.IsAttachmentKeyword(t)) {
                    layer.Attachment = t; hasAttachment = true; i++; continue;
                }
                if (ShorthandTokens.IsBoxKeyword(t)) {
                    if (boxKeywordsSeen == 0) { layer.Origin = t; layer.Clip = t; }
                    else if (boxKeywordsSeen == 1) { layer.Clip = t; }
                    else return false;
                    boxKeywordsSeen++;
                    i++;
                    continue;
                }
                if (IsBgPositionToken(t)) {
                    if (positionTokens.Count >= 4) return false;
                    positionTokens.Add(t);
                    hasPosition = true;
                    i++;
                    continue;
                }
                if (!hasColor && ShorthandTokens.IsColor(t)) {
                    if (!allowColor) return false;
                    layer.Color = t;
                    hasColor = true;
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

        static bool IsBgPositionToken(string s) {
            if (ShorthandTokens.IsPositionKeyword(s)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }

        static bool IsBgSizeToken(string s) {
            if (ShorthandTokens.IsBackgroundSizeKeyword(s)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }

        struct LayerLonghands {
            public string Color;
            public string Image;
            public string Repeat;
            public string Attachment;
            public string Position;
            public string Size;
            public string Origin;
            public string Clip;

            public static LayerLonghands Initial() {
                return new LayerLonghands {
                    Color = null,
                    Image = "none",
                    Repeat = "repeat",
                    Attachment = "scroll",
                    Position = "0% 0%",
                    Size = "auto",
                    Origin = "padding-box",
                    Clip = "border-box",
                };
            }
        }
    }
}
