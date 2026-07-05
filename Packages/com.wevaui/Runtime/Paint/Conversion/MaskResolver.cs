using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using LayoutBox = Weva.Layout.Boxes.Box;
using Weva.Paint.Images;

namespace Weva.Paint.Conversion {
    internal static class MaskResolver {
        public static MaskDefinition Resolve(ComputedStyle style, LayoutBox box, double absX, double absY,
                                             LengthContext ctx, IImageRegistry imageRegistry) {
            if (style == null || box == null) return null;
            if (!HasAnyMaskImage(style)) return null;
            var clips = SplitLayered(style.Get(CssProperties.MaskClipId));
            var origins = SplitLayered(style.Get(CssProperties.MaskOriginId));
            return Resolve(style, delegate(int layerIndex) {
                var clipKind = ParseMaskBox(LayerAt(clips, layerIndex, "border-box"), BackgroundClipOrigin.Box.BorderBox);
                var originKind = ParseMaskBox(LayerAt(origins, layerIndex, "border-box"), BackgroundClipOrigin.Box.BorderBox);
                var clipLocal = BackgroundClipOrigin.RectFor(clipKind, box);
                var originLocal = BackgroundClipOrigin.RectFor(originKind, box);
                var clip = new Rect(absX + clipLocal.X, absY + clipLocal.Y, clipLocal.Width, clipLocal.Height);
                var origin = new Rect(absX + originLocal.X, absY + originLocal.Y, originLocal.Width, originLocal.Height);
                return (clip, origin);
            }, ctx, imageRegistry);
        }

        public static MaskDefinition Resolve(ComputedStyle style, Rect bounds, LengthContext ctx, IImageRegistry imageRegistry) {
            if (style == null) return null;
            if (!HasAnyMaskImage(style)) return null;
            return Resolve(style, bounds, bounds, ctx, imageRegistry);
        }

        public static MaskDefinition Resolve(ComputedStyle style, Rect clipBounds, Rect originBounds,
                                             LengthContext ctx, IImageRegistry imageRegistry) {
            if (style == null) return null;
            if (!HasAnyMaskImage(style)) return null;
            return Resolve(style, _ => (clipBounds, originBounds), ctx, imageRegistry);
        }

        static bool HasAnyMaskImage(ComputedStyle style) {
            string raw = style.Get(CssProperties.MaskImageId);
            return !string.IsNullOrWhiteSpace(raw) && !CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none");
        }

        static MaskDefinition Resolve(ComputedStyle style, Func<int, (Rect clipBounds, Rect originBounds)> rectForLayer,
                                      LengthContext ctx, IImageRegistry imageRegistry) {
            if (style == null || rectForLayer == null) return null;
            var imageLayers = SplitLayered(style.Get(CssProperties.MaskImageId));
            if (imageLayers == null || imageLayers.Count == 0) return null;

            var positions = SplitLayered(style.Get(CssProperties.MaskPositionId));
            var sizes = SplitLayered(style.Get(CssProperties.MaskSizeId));
            var repeats = SplitLayered(style.Get(CssProperties.MaskRepeatId));
            var modes = SplitLayered(style.Get(CssProperties.MaskModeId));
            var composites = SplitLayered(style.Get(CssProperties.MaskCompositeId));

            var layers = new List<MaskLayer>(imageLayers.Count);
            bool hasRenderableLayer = false;
            for (int i = 0; i < imageLayers.Count; i++) {
                string image = LayerAt(imageLayers, i, "none");
                var rects = rectForLayer(i);
                var layer = ResolveLayer(
                    style,
                    image,
                    LayerAt(positions, i, "0% 0%"),
                    LayerAt(sizes, i, "auto"),
                    LayerAt(repeats, i, "repeat"),
                    ResolveMode(LayerAt(modes, i, "match-source")),
                    ResolveComposite(LayerAt(composites, i, "add")),
                    rects.clipBounds,
                    rects.originBounds,
                    ctx,
                    imageRegistry);
                if (layer == null) continue;
                if (layer.Brush != null) hasRenderableLayer = true;
                layers.Add(layer);
            }

            if (!hasRenderableLayer) return null;
            return new MaskDefinition(layers);
        }

        static MaskLayer ResolveLayer(ComputedStyle style, string layer, string posLayer, string sizeLayer, string repeatLayer,
                                      MaskMode mode, MaskComposite composite, Rect clipBounds, Rect originBounds,
                                      LengthContext ctx, IImageRegistry imageRegistry) {
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(layer, "none")) {
                return new MaskLayer(clipBounds, null, mode, composite, null);
            }
            if (string.IsNullOrWhiteSpace(layer)) return null;

            Brush brush = null;
            BackgroundTile? tile = null;
            double originDx = originBounds.X - clipBounds.X;
            double originDy = originBounds.Y - clipBounds.Y;

            if (RawValueParser.TryParseFunctionCall(layer.Trim(), out var name, out var body)) {
                if (name == "url") {
                    // NG5: url()-based mask images require an imageRegistry to resolve
                    // the image handle. Gradient-based masks (linear-gradient, etc.)
                    // do not use the registry and can proceed with a null registry.
                    if (imageRegistry == null) throw new ArgumentNullException(nameof(imageRegistry));
                    string handle = (body ?? "").Trim().Trim('"', '\'');
                    if (handle.Length > 0) {
                        double iw = originBounds.Width;
                        double ih = originBounds.Height;
                        if (imageRegistry.TryResolve(handle, out var src) && src != null) {
                            iw = src.Width > 0 ? src.Width : iw;
                            ih = src.Height > 0 ? src.Height : ih;
                        }
                        var t = BackgroundLayoutResolver.Resolve(style, originBounds, iw, ih, ctx,
                            posLayer, sizeLayer, repeatLayer);
                        tile = new BackgroundTile(
                            t.TileWidth, t.TileHeight,
                            t.OriginX + originDx, t.OriginY + originDy,
                            t.RepeatX, t.RepeatY,
                            t.GapX, t.GapY);
                        brush = Brush.ImageFullRect(handle, ImageRenderingResolver.Resolve(style));
                    }
                } else if (IsGradientName(name)) {
                    var t = BackgroundLayoutResolver.Resolve(style, originBounds,
                        originBounds.Width, originBounds.Height, ctx,
                        posLayer, sizeLayer, repeatLayer);
                    tile = new BackgroundTile(
                        t.TileWidth, t.TileHeight,
                        t.OriginX + originDx, t.OriginY + originDy,
                        t.RepeatX, t.RepeatY,
                        t.GapX, t.GapY);
                    var tileBounds = new Rect(0, 0,
                        t.TileWidth > 0 ? t.TileWidth : clipBounds.Width,
                        t.TileHeight > 0 ? t.TileHeight : clipBounds.Height);
                    var args = RawValueParser.SplitTopLevelCommas(body ?? "");
                    var fauxArgs = new List<CssValue>(args.Count);
                    for (int i = 0; i < args.Count; i++) {
                        string a = args[i].Trim();
                        fauxArgs.Add(new CssIdentifier(a, a));
                    }
                    var fn = new CssFunctionCall(name, fauxArgs, layer);
                    var grad = BackgroundResolver.TryParseGradient(fn, ColorResolver.ResolveCurrentColor(style), tileBounds);
                    if (grad != null) brush = Brush.Gradient(grad);
                }
            }

            if (brush == null) return null;
            return new MaskLayer(clipBounds, brush, mode, composite, tile);
        }

        static BackgroundClipOrigin.Box ParseMaskBox(string raw, BackgroundClipOrigin.Box fallback) {
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "no-clip")) return BackgroundClipOrigin.Box.BorderBox;
            return BackgroundClipOrigin.ParseBox(raw, fallback);
        }

        static bool IsGradientName(string name) {
            return name == "linear-gradient"
                || name == "repeating-linear-gradient"
                || name == "radial-gradient"
                || name == "repeating-radial-gradient"
                || name == "conic-gradient"
                || name == "repeating-conic-gradient";
        }

        static MaskMode ResolveMode(string raw) {
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "alpha")) return MaskMode.Alpha;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "luminance")) return MaskMode.Luminance;
            return MaskMode.MatchSource;
        }

        static MaskComposite ResolveComposite(string raw) {
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "subtract")) return MaskComposite.Subtract;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "intersect")) return MaskComposite.Intersect;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "exclude")) return MaskComposite.Exclude;
            return MaskComposite.Add;
        }

        static List<string> SplitLayered(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var split = RawValueParser.SplitTopLevelCommas(raw);
            return split.Count == 0 ? null : split;
        }

        static string LayerAt(List<string> list, int index, string fallback) {
            if (list == null || list.Count == 0) return fallback;
            string v = list[index % list.Count];
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }
    }
}
