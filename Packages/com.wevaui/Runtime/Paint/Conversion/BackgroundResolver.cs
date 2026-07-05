using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Paint.Images;

namespace Weva.Paint.Conversion {
    internal static class BackgroundResolver {
        // Parses a `url(handle)` segment, stripping optional double or single
        // quotes around the handle. Returns false for any other value (the
        // caller falls through to the gradient path or treats the layer as
        // unrenderable).
        //
        // Handles are NOT filesystem paths — they're opaque keys passed to
        // an `IImageRegistry`. The resolver doesn't know or care what the
        // backend will do with them.
        public static bool TryParseUrl(string raw, out string handle) {
            handle = null;
            if (string.IsNullOrEmpty(raw)) return false;
            if (!RawValueParser.TryParseFunctionCall(raw.Trim(), out var name, out var inner)) return false;
            if (name != "url") return false;
            string h = inner ?? "";
            // Strip surrounding quotes (CSS allows both forms).
            if (h.Length >= 2) {
                char first = h[0];
                if ((first == '"' || first == '\'') && h[h.Length - 1] == first) {
                    h = h.Substring(1, h.Length - 2);
                }
            }
            handle = h;
            return handle.Length > 0;
        }
        public static Brush ResolveBackground(ComputedStyle style, Rect bounds) {
            return ResolveBackground(style, bounds, LengthContext.Default);
        }

        public static Brush ResolveBackground(ComputedStyle style, Rect bounds, LengthContext lengthCtx) {
            if (style == null) return null;
            var current = ColorResolver.ResolveCurrentColor(style);
            double dpr = ImageSetResolver.DprFromLengthContext(lengthCtx);

            // Pull the parsed background-image tree directly from the
            // per-style cache. Each comma-separated layer arrives as one
            // entry in a CssValueList (multi-layer) or as a single CssValue
            // (one layer); we walk by type instead of re-tokenizing the raw
            // string. The url()/gradient(...) layers are CssFunctionCalls
            // whose Name + Arguments are already populated.
            var imageParsed = style.GetParsed(CssProperties.BackgroundImageId);
            if (imageParsed != null && !IsNoneValue(imageParsed)) {
                var rendering = ImageRenderingResolver.Resolve(style);
                if (imageParsed is CssValueList multi && multi.Separator == CssValueListSeparator.Comma) {
                    for (int i = 0; i < multi.Items.Count; i++) {
                        var brush = TryBuildFirstLayerBrush(multi.Items[i], current, bounds, rendering, dpr);
                        if (brush != null) return brush;
                    }
                } else {
                    var brush = TryBuildFirstLayerBrush(imageParsed, current, bounds, rendering, dpr);
                    if (brush != null) return brush;
                }
            } else {
                // GetParsed returns null when the value tree contains tokens
                // CssValueParser doesn't support yet (e.g. angle dimensions
                // inside `linear-gradient(45deg, ...)`). Fall through to raw-
                // string detection so authors don't lose their gradient just
                // because of an unsupported argument shape — the gradient
                // helpers below tokenize the raw themselves.
                string raw = style.Get(CssProperties.BackgroundImageId);
                if (!string.IsNullOrEmpty(raw) && raw != "none") {
                    var rendering = ImageRenderingResolver.Resolve(style);
                    var rawLayers = RawValueParser.SplitTopLevelCommas(raw);
                    for (int i = 0; i < rawLayers.Count; i++) {
                        var brush = TryBuildLayerBrushFromRaw(rawLayers[i], current, bounds, rendering, dpr);
                        if (brush != null) return brush;
                    }
                }
            }

            // background-color: read parsed tree, dispatch by type. Keeps
            // ColorResolver.TryResolveParsed off the global string->CssValue
            // cache. currentcolor / transparent shortcuts handled inside the
            // typed path (CssKeyword "transparent" yields LinearColor.Transparent).
            var colorParsed = style.GetParsed(CssProperties.BackgroundColorId);
            if (colorParsed == null) return null;
            if (IsTransparentOrNone(colorParsed)) return null;
            if (ColorResolver.TryResolveParsed(colorParsed, current, style, out var color)) {
                if (color.A <= 0f) return null;
                return Brush.SolidColor(color);
            }
            return null;
        }

        // Builds the brush for a single background-image layer expressed as
        // a parsed CssValue subtree. Returns null when the layer is "none"
        // or doesn't resolve to a url()/gradient(). Used by the single-layer
        // ResolveBackground entry point — multi-layer paths build layer
        // brushes inline because they need per-layer position/size/repeat.
        static Brush TryBuildFirstLayerBrush(CssValue layer, LinearColor current, Rect bounds, ImageRenderingMode rendering, double dpr) {
            if (layer == null || IsNoneValue(layer)) return null;
            // The parser maps `url("...")` to CssUrl directly (not a
            // CssFunctionCall named "url"), so url layers don't need to
            // round-trip through TryParseUrl on the raw text.
            if (layer is CssUrl u) {
                string h = u.Href ?? "";
                if (h.Length == 0) return null;
                return Brush.ImageFullRect(h, rendering);
            }
            if (layer is CssFunctionCall fn) {
                if (ImageSetResolver.IsImageSetName(fn.Name)
                    && ImageSetResolver.TryResolveFromFunctionCall(fn, dpr, out var pickedHandle)) {
                    return Brush.ImageFullRect(pickedHandle, rendering);
                }
                // cross-fade(): single-Brush API returns the dominant operand as
                // a best-effort. The two-layer compositing path runs via
                // ResolveBackgroundLayersInto.
                if (CrossFadeResolver.IsCrossFadeName(fn.Name)) {
                    string cfBody = fn.Raw != null
                        && RawValueParser.TryParseFunctionCall(fn.Raw, out _, out var bdy) ? bdy : null;
                    if (cfBody != null && CrossFadeResolver.TryParse(cfBody, out var cfFirst, out var cfSecond, out var cfAlpha)) {
                        string dominant = cfAlpha >= 0.5f ? cfSecond : cfFirst;
                        return CrossFadeResolver.ResolveOperand(dominant, current, bounds, rendering, dpr);
                    }
                    return null;
                }
                var grad = TryParseGradient(fn, current, bounds);
                if (grad != null) return Brush.Gradient(ResolveAbsoluteStops(grad, bounds.Width, bounds.Height));
            }
            return null;
        }

        // Raw-string fallback for the gradient-with-angle case: when
        // CssValueParser rejects an argument (e.g. `45deg` angle dimension),
        // GetParsed returns null and the typed dispatch above skips the
        // layer. Reach into RawValueParser, extract (name, body), and wrap
        // the body as a single-argument CssFunctionCall so the existing
        // typed-tree gradient builder can consume it. The builder reads
        // fn.Arguments[i].Raw — opaque raw text is enough.
        static Brush TryBuildLayerBrushFromRaw(string raw, LinearColor current, Rect bounds, ImageRenderingMode rendering, double dpr) {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            if (raw == "none") return null;
            if (!RawValueParser.TryParseFunctionCall(raw, out var name, out var body)) return null;
            if (string.IsNullOrEmpty(name)) return null;
            // url() raw form like url("...") — strip quotes via Body.
            if (name.Equals("url", System.StringComparison.OrdinalIgnoreCase)) {
                string handle = body.Trim().Trim('"', '\'');
                if (handle.Length == 0) return null;
                return Brush.ImageFullRect(handle, rendering);
            }
            // cross-fade() raw path: TryBuildLayerBrushFromRaw returns a single
            // Brush, so we can't expand to two sub-layers here. Return the second
            // operand's brush at 50% (the default) as a best-effort approximation
            // for the single-brush API. The full two-layer expansion runs via
            // ResolveBackgroundLayersInto, which is the primary paint path.
            if (CrossFadeResolver.IsCrossFadeName(name)) {
                if (CrossFadeResolver.TryParse(body, out var cfFirst, out var cfSecond, out var cfAlpha)) {
                    // Pick the dominant (higher-weighted) operand.
                    string dominant = cfAlpha >= 0.5f ? cfSecond : cfFirst;
                    return CrossFadeResolver.ResolveOperand(dominant, current, bounds, rendering, dpr);
                }
                return null;
            }
            if (ImageSetResolver.IsImageSetName(name)
                && ImageSetResolver.TryResolveRaw(body, dpr, out var pickedHandle)) {
                return Brush.ImageFullRect(pickedHandle, rendering);
            }
            // TryParseGradient reads fn.Arguments[i].Raw to recover per-comma
            // arg strings. Split the body on top-level commas so each arg is
            // its own faux CssIdentifier — same shape the parser would have
            // produced if it had supported every dimension inside.
            var argStrings = RawValueParser.SplitTopLevelCommas(body);
            var fauxArgs = new List<CssValue>(argStrings.Count);
            for (int i = 0; i < argStrings.Count; i++) {
                string a = argStrings[i].Trim();
                fauxArgs.Add(new CssIdentifier(a, a));
            }
            // CssFunctionCall ctor lowercases `name` itself — no need to pre-
            // lower here.
            var fauxFn = new CssFunctionCall(name, fauxArgs, raw);
            var grad = TryParseGradient(fauxFn, current, bounds);
            if (grad != null) return Brush.Gradient(ResolveAbsoluteStops(grad, bounds.Width, bounds.Height));
            return null;
        }

        // Builds a synthetic comma-separated CssValueList of CssFunctionCall /
        // CssUrl / CssKeyword leaves directly from a raw background-image
        // string. Used as a fallback when CssValueParser rejects one of the
        // argument types (e.g. angle dimensions inside `linear-gradient(45deg,
        // ...)`). The faux tree carries enough shape — Name + per-comma
        // Arguments — for the typed gradient builder to consume; it tokenizes
        // the comma-separated body itself, so unsupported leaf types are
        // tolerated.
        static CssValue BuildLayersFromRaw(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            if (raw == "none") return null;
            var rawLayers = RawValueParser.SplitTopLevelCommas(raw);
            if (rawLayers.Count == 0) return null;
            var layerValues = new List<CssValue>(rawLayers.Count);
            for (int i = 0; i < rawLayers.Count; i++) {
                string layer = rawLayers[i].Trim();
                if (layer == "none") {
                    layerValues.Add(new CssKeyword("none"));
                    continue;
                }
                if (!RawValueParser.TryParseFunctionCall(layer, out var name, out var body)) {
                    layerValues.Add(new CssKeyword("none"));
                    continue;
                }
                if (name.Equals("url", System.StringComparison.OrdinalIgnoreCase)) {
                    string href = body.Trim().Trim('"', '\'');
                    layerValues.Add(new CssUrl(href, layer));
                    continue;
                }
                var argStrings = RawValueParser.SplitTopLevelCommas(body);
                var args = new List<CssValue>(argStrings.Count);
                for (int j = 0; j < argStrings.Count; j++) {
                    string a = argStrings[j].Trim();
                    args.Add(new CssIdentifier(a, a));
                }
                // CssFunctionCall ctor lowercases itself; no need to pre-lower.
                layerValues.Add(new CssFunctionCall(name, args, layer));
            }
            if (layerValues.Count == 1) return layerValues[0];
            return new CssValueList(layerValues, CssValueListSeparator.Comma);
        }

        static bool IsNoneValue(CssValue v) {
            if (v is CssKeyword k) return k.Identifier == "none";
            if (v is CssIdentifier id) return id.Name.Equals("none", System.StringComparison.OrdinalIgnoreCase);
            return false;
        }

        static bool IsTransparentOrNone(CssValue v) {
            if (v is CssKeyword k) return k.Identifier == "none" || k.Identifier == "transparent";
            if (v is CssIdentifier id) {
                return id.Name.Equals("none", System.StringComparison.OrdinalIgnoreCase)
                    || id.Name.Equals("transparent", System.StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }


        // Resolves background-image into one Brush per layer in declaration order.
        // Layer order in the returned list matches declaration order: layers[0] is the
        // topmost layer in CSS terms. Caller is responsible for painting back-to-front
        // (paint layers[count-1] first). If background-color is non-transparent it is
        // returned as the *last* element of the list (it paints under all image layers).
        public static List<Brush> ResolveBackgroundLayers(ComputedStyle style, Rect bounds) {
            var result = new List<Brush>();
            ResolveBackgroundLayersInto(style, bounds, bounds, result, null, null, LengthContext.Default);
            return result;
        }

        // Pool-aware overload: appends layers into `output` and consults `brushCache`
        // for solid-color memoization. Used by BoxToPaintConverter on the hot path so
        // that a redrawn box reuses the same Brush instance for "background-color: red"
        // every time. The cache is keyed on the raw color string; gradient layers and
        // images are not memoized (their parsed object graph would defeat the win).
        // `bounds` is treated as both paint and origin rect; callers that need
        // clip/origin honoured separately call the 7-arg overload below.
        public static void ResolveBackgroundLayersInto(
            ComputedStyle style,
            Rect bounds,
            List<Brush> output,
            Dictionary<string, Brush> brushCache
        ) {
            ResolveBackgroundLayersInto(style, bounds, bounds, output, brushCache, null, LengthContext.Default);
        }

        // Clip/origin-aware overload. `paintBounds` is what the FillRect
        // command will draw into (`background-clip` rect); `originBounds`
        // is what `background-position` resolves against
        // (`background-origin` rect). The `Brush.Tile.OriginX/Y` returned
        // here is in `paintBounds`-relative coords so the renderer can
        // treat the FillRect's bounds as the local origin without
        // additional offsetting.
        public static void ResolveBackgroundLayersInto(
            ComputedStyle style,
            Rect paintBounds,
            Rect originBounds,
            List<Brush> output,
            Dictionary<string, Brush> brushCache,
            IImageRegistry imageRegistry,
            LengthContext lengthCtx
        ) {
            if (output == null || style == null) return;
            var current = ColorResolver.ResolveCurrentColor(style);

            // Per-style cached parse tree. CssValueList (comma) for multi-
            // layer; single CssValue (CssUrl / CssFunctionCall / CssKeyword
            // "none") otherwise. Skips the per-frame
            // RawValueParser.TryParseFunctionCall + SplitTopLevelCommas
            // scans that dominated this hot path.
            var imageParsed = style.GetParsed(CssProperties.BackgroundImageId);
            // Raw-string fallback for layers with parser-unsupported args
            // (e.g. angle dimensions inside linear-gradient). Builds a faux
            // CssValueList of CssFunctionCalls from the raw text so the
            // typed-tree path below still runs unchanged.
            if (imageParsed == null) {
                imageParsed = BuildLayersFromRaw(style.Get(CssProperties.BackgroundImageId));
            }
            if (imageParsed != null && !IsNoneValue(imageParsed)) {
                // CSS Backgrounds 3 §3.10: when a longhand list has fewer entries
                // than `background-image`, the values cycle to fill out the list;
                // when it has more, the extras are dropped. SplitLayered() handles
                // that for the position/size/repeat trio, indexed parallel to imgs.
                // We still keep these as string layers because BackgroundLayoutResolver
                // consumes raw text; the int-keyed Get(int) skips the string->id
                // dictionary lookup that style.Get(string) would do.
                var positions = SplitLayered(style.Get(CssProperties.BackgroundPositionId));
                var sizes = SplitLayered(style.Get(CssProperties.BackgroundSizeId));
                var repeats = SplitLayered(style.Get(CssProperties.BackgroundRepeatId));
                var rendering = ImageRenderingResolver.Resolve(style);
                double originDx = originBounds.X - paintBounds.X;
                double originDy = originBounds.Y - paintBounds.Y;

                // Inline iterator over the parsed-image layers. When the
                // tree is a comma-list we walk Items[]; otherwise it's a
                // single-layer value and we visit it once at index 0.
                int layerCount = imageParsed is CssValueList cl && cl.Separator == CssValueListSeparator.Comma
                    ? cl.Items.Count
                    : 1;
                for (int i = 0; i < layerCount; i++) {
                    CssValue layer = imageParsed is CssValueList list && list.Separator == CssValueListSeparator.Comma
                        ? list.Items[i]
                        : imageParsed;
                    if (layer == null || IsNoneValue(layer)) {
                        output.Add(null);
                        continue;
                    }
                    string posLayer = LayerAt(positions, i, "0% 0%");
                    string sizeLayer = LayerAt(sizes, i, "auto");
                    string repeatLayer = LayerAt(repeats, i, "repeat");
                    if (layer is CssUrl urlVal) {
                        string handle = urlVal.Href ?? "";
                        if (handle.Length == 0) {
                            output.Add(null);
                            continue;
                        }
                        BackgroundTile? tile = null;
                        if (imageRegistry != null && imageRegistry.TryResolve(handle, out var src) && src != null) {
                            // Resolve in origin-rect-local coords so position
                            // keywords (center, right, etc.) work against the
                            // background-origin region per CSS spec.
                            var t = BackgroundLayoutResolver.Resolve(style, originBounds,
                                src.Width, src.Height, lengthCtx,
                                posLayer, sizeLayer, repeatLayer);
                            // Translate the tile origin into paint-rect-local
                            // coords so the renderer can treat the FillRect's
                            // bounds as the (0,0) reference.
                            tile = new BackgroundTile(
                                t.TileWidth, t.TileHeight,
                                t.OriginX + originDx, t.OriginY + originDy,
                                t.RepeatX, t.RepeatY,
                                t.GapX, t.GapY);
                        }
                        // ImageTiled caches by (handle, (0,0,1,1), rendering,
                        // tile) so identical url() layers share Brush instances
                        // across boxes / frames.
                        output.Add(Brush.ImageTiled(handle, new Rect(0, 0, 1, 1), rendering, tile));
                        continue;
                    }
                    if (!(layer is CssFunctionCall fnLayer)) {
                        output.Add(null);
                        continue;
                    }
                    // cross-fade() / -webkit-cross-fade(): expand into TWO
                    // sub-layers at weighted alphas. The resolver appends both
                    // brushes directly so we skip the standard single-brush path.
                    // Per Chrome: an unparseable cross-fade → null layer (dropped).
                    if (CrossFadeResolver.IsCrossFadeName(fnLayer.Name)) {
                        double cfDpr = ImageSetResolver.DprFromLengthContext(lengthCtx);
                        // Recover the raw body from the function node. The parsed
                        // tree's arguments are per-comma CssIdentifiers whose .Raw
                        // fields hold the original text — we can reconstruct the
                        // full body by joining them with ", ".
                        string cfBody = fnLayer.Raw != null
                            ? RawValueParser.TryParseFunctionCall(fnLayer.Raw, out _, out var bdy) ? bdy : null
                            : null;
                        if (cfBody == null || !CrossFadeResolver.TryExpandIntoLayers(
                                cfBody, current, paintBounds, rendering, cfDpr, output)) {
                            // Failed parse → Chrome discards the layer.
                            output.Add(null);
                        }
                        continue;
                    }
                    // image-set() resolves to a single chosen URL handle; from
                    // there it follows the same tiling / positioning machinery
                    // as a bare url() layer. The picker runs against the
                    // host DPR (LengthContext.DpiPixelsPerInch / 96).
                    if (ImageSetResolver.IsImageSetName(fnLayer.Name)) {
                        double dpr = ImageSetResolver.DprFromLengthContext(lengthCtx);
                        if (!ImageSetResolver.TryResolveFromFunctionCall(fnLayer, dpr, out var pickedHandle)) {
                            output.Add(null);
                            continue;
                        }
                        BackgroundTile? imgSetTile = null;
                        if (imageRegistry != null && imageRegistry.TryResolve(pickedHandle, out var src2) && src2 != null) {
                            var t2 = BackgroundLayoutResolver.Resolve(style, originBounds,
                                src2.Width, src2.Height, lengthCtx,
                                posLayer, sizeLayer, repeatLayer);
                            imgSetTile = new BackgroundTile(
                                t2.TileWidth, t2.TileHeight,
                                t2.OriginX + originDx, t2.OriginY + originDy,
                                t2.RepeatX, t2.RepeatY,
                                t2.GapX, t2.GapY);
                        }
                        output.Add(Brush.ImageTiled(pickedHandle, new Rect(0, 0, 1, 1), rendering, imgSetTile));
                        continue;
                    }
                    // Gradient layers honour per-layer position/size/repeat by
                    // carrying a BackgroundTile on the brush (same machinery as
                    // image layers). CSS treats gradients as images with no
                    // intrinsic size, so we feed the origin-box size as the
                    // intrinsic dimensions — `auto auto` then resolves to the
                    // full box (legacy "fill the whole quad" behaviour) while
                    // explicit `<pos>/<size>` values clip / position the
                    // gradient within the box.
                    var gt = BackgroundLayoutResolver.Resolve(style, originBounds,
                        originBounds.Width, originBounds.Height, lengthCtx,
                        posLayer, sizeLayer, repeatLayer);
                    // Resolve the gradient itself against the *tile* rect:
                    // radial/conic center+radius keywords are relative to the
                    // gradient's own painting area (the tile), not the full
                    // box. Linear gradients only carry an angle so the bounds
                    // are unused for them.
                    var tileBounds = new Rect(0, 0,
                        gt.TileWidth > 0 ? gt.TileWidth : paintBounds.Width,
                        gt.TileHeight > 0 ? gt.TileHeight : paintBounds.Height);
                    var grad = TryParseGradient(fnLayer, current, tileBounds);
                    if (grad == null) {
                        output.Add(null);
                        continue;
                    }
                    // Translate origin into paint-rect-local coords (matches
                    // the image-layer path above).
                    var gradTile = new BackgroundTile(
                        gt.TileWidth, gt.TileHeight,
                        gt.OriginX + originDx, gt.OriginY + originDy,
                        gt.RepeatX, gt.RepeatY,
                        gt.GapX, gt.GapY);
                    // Resolve absolute-px stops against the TILE size so a 1px
                    // line in a 44px tile is a thin line, not the whole cell.
                    grad = ResolveAbsoluteStops(grad,
                        gt.TileWidth > 0 ? gt.TileWidth : tileBounds.Width,
                        gt.TileHeight > 0 ? gt.TileHeight : tileBounds.Height);
                    var brushKey = new GradientBrushKey(grad, gradTile);
                    Brush gradBrush;
                    if (!gradientBrushCache.TryGetValue(brushKey, out gradBrush)) {
                        gradBrush = Brush.Gradient(grad, gradTile);
                        // P16: slice-evict on overflow rather than drop-new, so
                        // an animated gradient flooding the cap can't lock
                        // reusable brushes out of caching for the session.
                        ParseCacheEviction.EnsureRoom(gradientBrushCache, GradientBrushCacheCap);
                        gradientBrushCache[brushKey] = gradBrush;
                    }
                    output.Add(gradBrush);
                }
            }

            // background-color: parsed-tree path. brushCache still keys by
            // the original style.Get raw text so existing solid-color
            // memoization survives across multiple boxes with the same
            // declaration. The raw is read lazily — only when we know we
            // might hit / populate the cache (matches the prior keying so
            // warm BoxToPaintConverter caches don't go cold after the
            // migration).
            var colorParsed = style.GetParsed(CssProperties.BackgroundColorId);
            if (colorParsed != null && !IsTransparentOrNone(colorParsed)) {
                bool isCurrentColor = colorParsed is CssKeyword ck && CssStringUtil.IsCurrentColor(ck.Identifier);
                bool cacheable = brushCache != null && !isCurrentColor;
                string colorRawForCache = cacheable ? style.Get(CssProperties.BackgroundColorId) : null;
                if (cacheable && colorRawForCache != null && brushCache.TryGetValue(colorRawForCache, out var cached)) {
                    output.Add(cached);
                } else if (ColorResolver.TryResolveParsed(colorParsed, current, style, out var color)
                        && color.A > 0f) {
                    var brush = Brush.SolidColor(color);
                    if (cacheable && colorRawForCache != null) brushCache[colorRawForCache] = brush;
                    output.Add(brush);
                }
            }
        }

        // Splits a longhand value (e.g. "0 0, 30vmin 0, 50vmin 0") into per-layer
        // entries on top-level commas. Returns null when the string is empty or
        // null so callers can use the layer-default fallback.
        static List<string> SplitLayered(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            var split = RawValueParser.SplitTopLevelCommas(raw);
            return split.Count == 0 ? null : split;
        }

        // Picks the layer-i value out of a comma-split longhand list. Per CSS
        // Backgrounds 3 §3.10, shorter longhand lists cycle to match the image
        // count: `background-image: a, b, c, d` + `background-position: 0 0, 50% 0`
        // resolves layers as (0 0), (50% 0), (0 0), (50% 0).
        static string LayerAt(List<string> list, int index, string fallback) {
            if (list == null || list.Count == 0) return fallback;
            string v = list[index % list.Count];
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        // Parsed-tree entry point. Uses the already-tokenized CssFunctionCall
        // from ComputedStyle.GetParsed: skips RawValueParser.TryParseFunctionCall
        // (a full string scan) and SplitTopLevelCommas (depth-tracked scan)
        // on every frame. The arg-level internals are still string-based —
        // TODO: future migration could push CssValue typing all the way into
        // BuildLinear/Radial/Conic and the stop / position / angle helpers,
        // but that's a wider change with more visual-regression surface than
        // fits in this commit.
        // Process-static cache for Brush.Gradient(grad, tile) instances.
        // Brush is `sealed class`, allocated fresh each call — for a multi-
        // gem scene where every tile has the same (gradient, tile-size)
        // every Convert was producing 60+ Brush instances/frame even though
        // the gradient cache already deduplicated the inner LinearGradient.
        // Keying on (gradient ref, tile struct) lets every tile share one
        // Brush reference.
        //
        // RC5: this cache, `gradientCache`, `gradientNoCache`, and the
        // `argsListPool` Stack are single-threaded by Unity main-thread
        // convention. The `argsListPool` pop/push pair is particularly
        // fragile — a contended pop could double-rent the same List<string>
        // and produce cross-callsite corruption. The public entrypoint
        // `TryParseGradient` calls UIMainThreadGuard.AssertMainThread so a
        // misuse from an Addressables / async completion fires a debug-
        // build assertion rather than silently corrupting the pool.
        const int GradientBrushCacheCap = 256;
        static readonly Dictionary<GradientBrushKey, Brush> gradientBrushCache = new Dictionary<GradientBrushKey, Brush>();

        // Process-static cache for ALL gradient kinds. Linear gradients are
        // bounds-independent (just angle + stops); radial/conic gradients are
        // bounds-dependent (center keywords resolve against the tile rect).
        // Key combines fn.Raw with (width, height) so the cache hits for the
        // common case where many identically-sized boxes share the same CSS
        // gradient (e.g. 16 same-size gem tiles with the same `radial-gradient
        // (circle, ...)`). Linear gradients use (0, 0) in the bounds slots so
        // they hit regardless of the box size.
        //
        // currentcolor is the one input whose resolved value DOES change
        // between callsites — we sidestep the issue by only caching gradients
        // whose stop list mentions no `currentcolor` keyword.
        const int GradientCacheCap = 256;
        readonly struct GradientCacheKey : System.IEquatable<GradientCacheKey> {
            public readonly string Raw;
            public readonly double Width;
            public readonly double Height;

            public GradientCacheKey(string raw, double width, double height) {
                Raw = raw;
                Width = width;
                Height = height;
            }

            public bool Equals(GradientCacheKey other) {
                return Raw == other.Raw && Width == other.Width && Height == other.Height;
            }

            public override bool Equals(object obj) => obj is GradientCacheKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Raw != null ? Raw.GetHashCode() : 0;
                    h = (h * 397) ^ Width.GetHashCode();
                    h = (h * 397) ^ Height.GetHashCode();
                    return h;
                }
            }
        }
        static readonly Dictionary<GradientCacheKey, Gradient> gradientCache = new Dictionary<GradientCacheKey, Gradient>();
        // Negative-cache: gradient strings that failed to parse (or that
        // contain `currentcolor` and thus aren't cacheable). Skips repeating
        // the case-insensitive scan on subsequent calls.
        //
        // Soft-capped at GradientNoCacheCap (256) entries; once full, new
        // failing/uncacheable strings are simply NOT recorded (the caller
        // re-runs the cheap ContainsCurrentColor scan on each subsequent hit
        // instead of memoizing the "skip" decision). This matches the
        // drop-new-on-overflow convention used by gradientCache /
        // gradientBrushCache above and by FilterResolver / BoxShadowResolver
        // — bounds the set against pathological inputs (e.g. animated
        // currentcolor gradients producing one novel raw string per frame)
        // without the cliff of a wholesale Clear(). Closes MC1 in
        // CODE_AUDIT_FINDINGS.md.
        const int GradientNoCacheCap = 256;
        static readonly HashSet<string> gradientNoCache = new HashSet<string>();

        // Drops every gradient cache. Called by tests for per-case isolation
        // (the process-static caches otherwise leak state across NUnit cases)
        // and reserved for hot-reload paths that want a clean parse slate
        // after a stylesheet swap.
        internal static void ResetCaches_TestOnly() {
            gradientCache.Clear();
            gradientBrushCache.Clear();
            gradientNoCache.Clear();
        }

        // Test-only window into the negative-cache occupancy so MC1
        // regression tests can assert the cap is respected without going
        // through reflection. The count itself is intentionally
        // implementation-detail: external callers should not gate behaviour
        // on it.
        internal static int GradientNoCacheCount_TestOnly() => gradientNoCache.Count;
        internal static int GradientNoCacheCap_TestOnly => GradientNoCacheCap;
        // Pool for the per-call args List<string>. Each TryParseGradient call
        // used to `new List<string>(fn.Arguments.Count)` even on cache hit-
        // miss-then-build-fresh paths; this pool returns the same backing
        // List instance each call.
        static readonly Stack<List<string>> argsListPool = new Stack<List<string>>(8);

        internal static Gradient TryParseGradient(CssFunctionCall fn, LinearColor currentColor, Rect bounds) {
            // RC5: gradient + brush + no-cache + argsListPool are all single-
            // threaded by Unity main-thread convention. Pop/push on the
            // pool is the highest re-entrancy risk — a contended pop could
            // double-rent the same List<string> and corrupt both callers.
            Weva.Diagnostics.UIMainThreadGuard.AssertMainThread(nameof(TryParseGradient));
            if (fn == null) return null;
            string name = fn.Name;
            if (string.IsNullOrEmpty(name)) return null;
            // Linear/repeating-linear is bounds-independent — zero in the
            // bounds slots so the key hits regardless of the painting box.
            bool isLinear = name == "linear-gradient" || name == "repeating-linear-gradient";
            string raw = fn.Raw;
            GradientCacheKey cacheKey = raw != null
                ? new GradientCacheKey(raw, isLinear ? 0 : bounds.Width, isLinear ? 0 : bounds.Height)
                : default;
            if (raw != null && gradientCache.TryGetValue(cacheKey, out var cached)) {
                return cached;
            }
            // Materialize the per-arg raw strings. The parser already split
            // on top-level commas; we just read .Raw on each entry. Pool
            // the List<string> to avoid per-call allocations on the cache-
            // miss path.
            var args = argsListPool.Count > 0 ? argsListPool.Pop() : new List<string>(8);
            args.Clear();
            if (fn.Arguments != null) {
                for (int i = 0; i < fn.Arguments.Count; i++) {
                    args.Add(fn.Arguments[i]?.Raw ?? "");
                }
            }
            try {
                if (args.Count == 0) return null;
                CssColorSpace space = CssColorSpace.Srgb;
                CssHueInterpolationMethod hueMethod = CssHueInterpolationMethod.Shorter;
                if (TryConsumeInterpolationPrefix(args, out var parsedSpace, out var parsedHueMethod)) {
                    space = parsedSpace;
                    hueMethod = parsedHueMethod;
                    // The helper already stripped the clause from args[0] (and
                    // removed args[0] entirely for the standalone form).
                    if (args.Count == 0) return null;
                }
                Gradient built;
                if (isLinear) {
                    bool repeating = name == "repeating-linear-gradient";
                    built = BuildLinear(args, currentColor, space, repeating, hueMethod);
                } else if (name == "radial-gradient" || name == "repeating-radial-gradient") {
                    bool repeating = name == "repeating-radial-gradient";
                    built = BuildRadial(args, currentColor, bounds, space, hueMethod, repeating);
                } else if (name == "conic-gradient" || name == "repeating-conic-gradient") {
                    bool repeating = name == "repeating-conic-gradient";
                    built = BuildConic(args, currentColor, bounds, space, hueMethod, repeating);
                } else {
                    return null;
                }
                if (built != null
                    && raw != null
                    && !gradientNoCache.Contains(raw)
                    && !ContainsCurrentColor(raw)) {
                    // P16: slice-evict on overflow rather than drop-new — an
                    // animated/currentcolor gradient producing a novel raw
                    // string per frame must not permanently lock reusable
                    // gradients out of the cache.
                    ParseCacheEviction.EnsureRoom(gradientCache, GradientCacheCap);
                    gradientCache[cacheKey] = built;
                } else if (raw != null && (built == null || ContainsCurrentColor(raw))) {
                    ParseCacheEviction.EnsureRoom(gradientNoCache, GradientNoCacheCap);
                    gradientNoCache.Add(raw);
                }
                return built;
            } finally {
                args.Clear();
                if (argsListPool.Count < 8) argsListPool.Push(args);
            }
        }

        // Allocation-free case-insensitive substring scan. The string-search
        // overload `IndexOf("currentcolor", OrdinalIgnoreCase)` doesn't allocate
        // on modern runtimes, but is easy to break with .ToLowerInvariant().
        //
        // D8: this is a SUBSTRING scan, not an exact-match token check —
        // we use it to detect `currentcolor` appearing anywhere inside a
        // longhand background value (e.g. nested inside a gradient stop).
        // The shared CssStringUtil.IsCurrentColor helper is exact-match only,
        // so it cannot replace this — distinct semantic, kept local.
        static bool ContainsCurrentColor(string raw) {
            return raw != null
                && raw.IndexOf("currentcolor", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Extracts a CSS `<color-interpolation-method>` (`in <color-space>
        // [<hue-interpolation-method> hue]`) from the gradient's FIRST
        // comma-segment. Per CSS Images 4, the interpolation clause shares the
        // first segment with the optional direction/angle, in EITHER order:
        //   linear-gradient(to right in oklab, …)
        //   linear-gradient(in oklab to right, …)
        //   linear-gradient(in oklab, …)        (standalone, no direction)
        //   linear-gradient(45deg in oklab, …)
        // On success the clause is stripped from args[0]: if a direction
        // remains, args[0] is rewritten to just the direction; if nothing
        // remains (standalone interpolation), args[0] is removed entirely.
        // The previous implementation only matched the standalone first-arg
        // form, so the common `to right in oklab` was silently dropped to
        // sRGB (G1b).
        static bool TryConsumeInterpolationPrefix(List<string> args, out CssColorSpace space, out CssHueInterpolationMethod hueMethod) {
            space = CssColorSpace.Srgb;
            hueMethod = CssHueInterpolationMethod.Shorter;
            if (args.Count == 0) return false;
            var tokens = RawValueParser.SplitTopLevelSpaces(args[0].Trim());
            if (tokens.Count == 0) return false;
            int inIdx = -1;
            for (int i = 0; i < tokens.Count; i++) {
                if (string.Equals(tokens[i], "in", System.StringComparison.OrdinalIgnoreCase)) { inIdx = i; break; }
            }
            // Need `in <space>` — the space token immediately follows `in`.
            if (inIdx < 0 || inIdx + 1 >= tokens.Count) return false;
            if (!ColorMixer.TryParseSpaceName(tokens[inIdx + 1], out space)) return false;
            int consumedEnd = inIdx + 1; // last token index belonging to the clause
            // Optional `<shorter|longer|increasing|decreasing> hue` — cylindrical spaces only.
            if (ColorMixer.IsCylindricalSpace(space) && inIdx + 3 < tokens.Count
                && ColorMixer.TryParseHueMethod(tokens[inIdx + 2], out var parsedMethod)
                && string.Equals(tokens[inIdx + 3], "hue", System.StringComparison.OrdinalIgnoreCase)) {
                hueMethod = parsedMethod;
                consumedEnd = inIdx + 3;
            }
            // Rebuild the direction from the tokens OUTSIDE the interpolation clause.
            var dir = new System.Text.StringBuilder();
            for (int i = 0; i < tokens.Count; i++) {
                if (i >= inIdx && i <= consumedEnd) continue;
                if (dir.Length > 0) dir.Append(' ');
                dir.Append(tokens[i]);
            }
            if (dir.Length == 0) {
                args.RemoveAt(0); // standalone interpolation — no direction
            } else {
                args[0] = dir.ToString(); // leave the direction for BuildLinear/Radial/Conic
            }
            return true;
        }

        static LinearGradient BuildLinear(List<string> args, LinearColor currentColor, CssColorSpace space, bool isRepeating, CssHueInterpolationMethod hueMethod) {
            int startIdx = 0;
            double angle = 180.0;
            string first = args[0];
            if (LooksLikeAngleOrToKeyword(first, out double parsedAngle)) {
                angle = parsedAngle;
                startIdx = 1;
            }
            var stops = new List<GradientStop>();
            for (int i = startIdx; i < args.Count; i++) {
                AppendStops(args[i], currentColor, stops);
            }
            if (stops.Count < 2) return null;
            NormalizeStopPositions(stops);
            return new LinearGradient(angle, stops, space, isRepeating, hueMethod);
        }

        // CSS Images 4 §3.5.2 double-position stops: `<color> <pos1> <pos2>`
        // is shorthand for two stops with the same color at pos1 and pos2.
        // Falls back to the single-stop parser for the normal case.
        static void AppendStops(string raw, LinearColor currentColor, List<GradientStop> stops) {
            if (string.IsNullOrEmpty(raw)) return;
            var parts = RawValueParser.SplitTopLevelSpaces(raw.Trim());
            // Look for the "<color> <pos1> <pos2>" shape: one color token and
            // exactly two position tokens. Positions can be % or px or a
            // bare number; any other ordering / token count falls through to
            // the single-stop parser so we don't break existing inputs.
            if (parts.Count == 3) {
                int colorIdx = -1;
                for (int i = 0; i < 3; i++) {
                    if (IsColorToken(parts[i])) { colorIdx = i; break; }
                }
                if (colorIdx >= 0) {
                    string p1Token = null;
                    string p2Token = null;
                    for (int i = 0; i < 3; i++) {
                        if (i == colorIdx) continue;
                        if (p1Token == null) p1Token = parts[i];
                        else p2Token = parts[i];
                    }
                    if (p1Token != null && p2Token != null
                        && LooksLikeStopPosition(p1Token) && LooksLikeStopPosition(p2Token)) {
                        if (ColorResolver.TryResolve(parts[colorIdx], currentColor, null, out var col)) {
                            double pos1 = ParseStopPosition(p1Token, out bool px1);
                            double pos2 = ParseStopPosition(p2Token, out bool px2);
                            stops.Add(new GradientStop(col, pos1, px1));
                            stops.Add(new GradientStop(col, pos2, px2));
                            return;
                        }
                    }
                }
            }
            if (TryParseStop(raw, currentColor, out var s)) stops.Add(s);
        }

        static bool LooksLikeStopPosition(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.EndsWith("%")) return double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (s.EndsWith("px")) return double.TryParse(s.AsSpan(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        static double ParseStopPosition(string s, out bool isPx) {
            isPx = false;
            if (s.EndsWith("%")) {
                if (double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return pct * 0.01;
            } else if (s.EndsWith("px")) {
                if (double.TryParse(s.AsSpan(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) { isPx = true; return px; }
            } else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) {
                return n;
            }
            return double.NaN;
        }

        static RadialGradient BuildRadial(List<string> args, LinearColor currentColor, Rect bounds, CssColorSpace space, CssHueInterpolationMethod hueMethod, bool isRepeating) {
            int startIdx = 0;
            RadialGradientShape shape = RadialGradientShape.Ellipse;
            double cx = bounds.Width * 0.5;
            double cy = bounds.Height * 0.5;
            // Per CSS Images 3 §3.7.1 the default sizing keyword is `farthest-corner`.
            string first = args.Count > 0 ? args[0].Trim() : "";
            double? overrideRx = null, overrideRy = null;
            RadialGradientSizing sizing = RadialGradientSizing.FarthestCorner;
            if (LooksLikeRadialShapeSpec(first)) {
                ParseRadialShapeSpec(first, ref shape, ref cx, ref cy, ref overrideRx, ref overrideRy, ref sizing, bounds);
                startIdx = 1;
            }
            double rx, ry;
            if (overrideRx.HasValue && overrideRy.HasValue) {
                // Author wrote explicit radii (e.g. `radial-gradient(1px 1px at
                // 20% 30%, …)`). Use them verbatim and skip the size-keyword
                // computation — without this, `bg-stars`-style pinpoints render
                // as huge ellipses covering the whole layer.
                rx = overrideRx.Value;
                ry = overrideRy.Value;
            } else {
                // Per-axis side distances from the center to the box edges.
                double nearX = System.Math.Min(cx, bounds.Width - cx);
                double farX = System.Math.Max(cx, bounds.Width - cx);
                double nearY = System.Math.Min(cy, bounds.Height - cy);
                double farY = System.Math.Max(cy, bounds.Height - cy);
                switch (sizing) {
                    case RadialGradientSizing.ClosestSide:
                        if (shape == RadialGradientShape.Circle) {
                            rx = ry = System.Math.Min(nearX, nearY);
                        } else {
                            rx = nearX; ry = nearY;
                        }
                        break;
                    case RadialGradientSizing.FarthestSide:
                        if (shape == RadialGradientShape.Circle) {
                            rx = ry = System.Math.Max(farX, farY);
                        } else {
                            rx = farX; ry = farY;
                        }
                        break;
                    case RadialGradientSizing.ClosestCorner: {
                        // Distance from the center to the nearest corner.
                        double dcc = System.Math.Sqrt(nearX * nearX + nearY * nearY);
                        if (shape == RadialGradientShape.Circle) {
                            rx = ry = dcc;
                        } else {
                            // CSS Images 3 §3.7.1: closest-corner ellipse has the same
                            // aspect ratio as closest-side; scale that base so the
                            // ellipse passes through the closest corner.
                            const double Sqrt2 = 1.4142135623730951;
                            rx = nearX * Sqrt2;
                            ry = nearY * Sqrt2;
                        }
                        break;
                    }
                    case RadialGradientSizing.FarthestCorner:
                    default: {
                        double fcx = farX;
                        double fcy = farY;
                        if (shape == RadialGradientShape.Circle) {
                            rx = ry = System.Math.Sqrt(fcx * fcx + fcy * fcy);
                        } else {
                            // CSS Images 3 §3.7.1: ellipse passes through the farthest
                            // corner while keeping the box's aspect ratio. With farthest-
                            // side distances (fcx, fcy), the constraint collapses to
                            // rx = fcx*sqrt(2), ry = fcy*sqrt(2).
                            const double Sqrt2 = 1.4142135623730951;
                            rx = fcx * Sqrt2;
                            ry = fcy * Sqrt2;
                        }
                        break;
                    }
                }
            }
            var stops = new List<GradientStop>();
            for (int i = startIdx; i < args.Count; i++) {
                if (TryParseStop(args[i], currentColor, out var s)) stops.Add(s);
            }
            if (stops.Count < 2) return null;
            NormalizeStopPositions(stops);
            return new RadialGradient(cx, cy, rx, ry, shape, stops, space, hueMethod, isRepeating);
        }

        // Distance from a point on an axis to the farthest edge of the [lo, hi]
        // range. For a center at 0.2*box on a 1000-wide box, returns 800; for a
        // center at 0.7*box, returns 700. Used to seed the farthest-corner
        // default radius for radial-gradient.
        static double FarthestCornerDistance(double center, double lo, double hi) {
            double left = center - lo;
            double right = hi - center;
            return left > right ? left : right;
        }

        static ConicGradient BuildConic(List<string> args, LinearColor currentColor, Rect bounds, CssColorSpace space, CssHueInterpolationMethod hueMethod, bool isRepeating) {
            int startIdx = 0;
            double fromAngle = 0;
            double cx = bounds.Width * 0.5;
            double cy = bounds.Height * 0.5;
            string first = args[0].Trim();
            if (LooksLikeConicSpec(first)) {
                ParseConicSpec(first, ref fromAngle, ref cx, ref cy, bounds);
                startIdx = 1;
            }
            var stops = new List<GradientStop>();
            for (int i = startIdx; i < args.Count; i++) {
                if (TryParseConicStop(args[i], currentColor, out var s)) stops.Add(s);
            }
            if (stops.Count < 2) return null;
            NormalizeStopPositions(stops);
            return new ConicGradient(fromAngle, cx, cy, stops, space, hueMethod, isRepeating);
        }

        static bool LooksLikeConicSpec(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            // Allocation-free check: the prefix variants use StartsWithIgnoreCase,
            // the " at " substring uses IndexOf(... OrdinalIgnoreCase) which
            // doesn't allocate.
            return CssStringUtil.StartsWithIgnoreCase(s, "from ")
                || CssStringUtil.StartsWithIgnoreCase(s, "at ")
                || s.IndexOf(" at ", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void ParseConicSpec(string s, ref double fromAngle, ref double cx, ref double cy, Rect bounds) {
            var parts = RawValueParser.SplitTopLevelSpaces(s);
            int i = 0;
            if (i < parts.Count && CssStringUtil.EqualsIgnoreCase(parts[i], "from")) {
                i++;
                if (i < parts.Count && RawValueParser.TryParseAngleDegrees(parts[i], out double deg)) {
                    fromAngle = deg;
                    i++;
                }
            }
            for (; i < parts.Count; i++) {
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "at")) {
                    if (i + 1 < parts.Count) cx = ResolvePositionAxis(parts[i + 1], bounds.Width, true);
                    if (i + 2 < parts.Count) cy = ResolvePositionAxis(parts[i + 2], bounds.Height, false);
                    break;
                }
            }
        }

        static bool TryParseConicStop(string raw, LinearColor currentColor, out GradientStop stop) {
            stop = default;
            if (string.IsNullOrEmpty(raw)) return false;
            var parts = RawValueParser.SplitTopLevelSpaces(raw.Trim());
            if (parts.Count == 0) return false;
            string colorToken = null;
            string posToken = null;
            foreach (var part in parts) {
                if (colorToken == null && IsColorToken(part)) colorToken = part;
                else posToken = part;
            }
            if (colorToken == null) {
                if (IsColorToken(raw)) colorToken = raw;
                else return false;
            }
            if (!ColorResolver.TryResolve(colorToken, currentColor, null, out var color)) return false;
            double pos = double.NaN;
            if (posToken != null) {
                if (posToken.EndsWith("%")) {
                    if (double.TryParse(posToken.AsSpan(0, posToken.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) {
                        pos = pct * 0.01;
                    }
                } else if (RawValueParser.TryParseAngleDegrees(posToken, out double deg)) {
                    pos = deg / 360.0;
                } else if (double.TryParse(posToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) {
                    pos = n;
                } else if (TryEvaluateConicAngleCalc(posToken, out double calcDeg)) {
                    // CSS `conic-gradient(<color> calc(var(--x) * 360deg), …)`
                    // — common pattern for cooldown overlays and progress
                    // dials. Without this branch the calc() falls through
                    // every literal-position parser and the stop position
                    // stays NaN, which NormalizeStopPositions then
                    // back-fills to (0, 1) — degrading the gradient to a
                    // full-circle smooth lerp instead of the declared
                    // angular slice.
                    pos = calcDeg / 360.0;
                }
            }
            stop = new GradientStop(color, pos);
            return true;
        }

        // Evaluates simple calc() forms that appear as conic-gradient stop
        // positions: `calc(<num> * <angle>)`, `calc(<angle> * <num>)`,
        // `calc(<angle> + <angle>)`, `calc(<angle>)`. Returns the result
        // in degrees. Text-level evaluator — the CSS value parser doesn't
        // model angle units yet (CssLengthUnit has no Deg/Rad/Grad/Turn),
        // so the typed CssCalc tree can't represent the operands the
        // author wrote. Restricted to the patterns the demo HUD's
        // cooldown overlay needs; anything more elaborate falls back to
        // false and the caller leaves the stop position at NaN.
        static bool TryEvaluateConicAngleCalc(string raw, out double degrees) {
            degrees = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            string trimmed = raw.Trim();
            if (!trimmed.StartsWith("calc(", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (!trimmed.EndsWith(")")) return false;
            string inner = trimmed.Substring(5, trimmed.Length - 6).Trim();
            // Try the multiplication forms first since that's the common
            // cooldown pattern.
            foreach (char op in new[] {'*', '/', '+', '-'}) {
                int sep = FindTopLevelOp(inner, op);
                if (sep < 0) continue;
                string ls = inner.Substring(0, sep).Trim();
                string rs = inner.Substring(sep + 1).Trim();
                if (!TryEvalCalcAngleLeaf(ls, out double lv)) continue;
                if (!TryEvalCalcAngleLeaf(rs, out double rv)) continue;
                switch (op) {
                    case '*': degrees = lv * rv; return true;
                    case '/':
                        if (rv == 0) return false;
                        degrees = lv / rv; return true;
                    case '+': degrees = lv + rv; return true;
                    case '-': degrees = lv - rv; return true;
                }
            }
            return TryEvalCalcAngleLeaf(inner, out degrees);
        }

        // Returns the leaf as degrees (or a unit-less number — the caller
        // multiplies/divides it through). Recognises:
        //   - "360deg" / "1turn" / "1.57rad" / "100grad"  → in degrees
        //   - "0.75"                                     → unit-less
        //   - "1%"                                       → percentage of
        //     360° (3.6°). Conic-gradient stop positions accept percentages
        //     where 100% = 360°. Without this branch the canonical
        //     progress-ring pattern `conic-gradient(blue calc(var(--p) *
        //     1%), gray 0)` returns false → NaN stop position → both
        //     stops collapse to 0% and the gradient paints as a single
        //     solid colour.
        // Returns false for unrecognised tokens.
        static bool TryEvalCalcAngleLeaf(string token, out double value) {
            value = 0;
            if (string.IsNullOrEmpty(token)) return false;
            if (RawValueParser.TryParseAngleDegrees(token, out double deg)) {
                value = deg; return true;
            }
            if (token.EndsWith("%")
                && double.TryParse(token.AsSpan(0, token.Length - 1),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) {
                value = pct * 3.6; // 1% of the full conic turn (360°)
                return true;
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) {
                value = n; return true;
            }
            return false;
        }

        static int FindTopLevelOp(string s, char op) {
            int depth = 0;
            // Skip the FIRST character so a leading unary -/+ doesn't get
            // picked as the operator (which would split "-90deg" into ""
            // and "90deg").
            for (int i = 1; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth--; continue; }
                if (depth == 0 && c == op) return i;
            }
            return -1;
        }

        static bool LooksLikeRadialShapeSpec(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (CssStringUtil.StartsWithIgnoreCase(s, "circle")) return true;
            if (CssStringUtil.StartsWithIgnoreCase(s, "ellipse")) return true;
            if (s.IndexOf(" at ", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // Spec-prefixed forms like `1px 1px` / `30%` (no shape keyword
            // and no `at`) carry explicit radii — detect them by checking
            // whether the first token parses as a length / percent. Without
            // this the gradient stops never get past arg[0] and the
            // explicit-radii branch in BuildRadial never fires.
            var parts = RawValueParser.SplitTopLevelSpaces(s);
            if (parts.Count == 0) return false;
            string first = parts[0];
            if (CssStringUtil.EqualsIgnoreCase(first, "closest-side")
                || CssStringUtil.EqualsIgnoreCase(first, "closest-corner")
                || CssStringUtil.EqualsIgnoreCase(first, "farthest-side")
                || CssStringUtil.EqualsIgnoreCase(first, "farthest-corner")) {
                return true;
            }
            return !double.IsNaN(ResolveRadiusToken(first, 0));
        }

        // overrideRx / overrideRy are set when the spec carries explicit
        // length / percent radii (`radial-gradient(1px 1px at …)` or
        // `radial-gradient(30% 50%, …)`). For circle, one length sets
        // both axes; for ellipse, two lengths set rx, ry separately.
        // sizing receives the parsed CSS Images 3 §3.7.1 size keyword
        // (closest-side / closest-corner / farthest-side / farthest-corner);
        // if none is present it stays at FarthestCorner (the spec default).
        static void ParseRadialShapeSpec(string s, ref RadialGradientShape shape, ref double cx, ref double cy,
                                          ref double? overrideRx, ref double? overrideRy,
                                          ref RadialGradientSizing sizing, Rect bounds) {
            var parts = RawValueParser.SplitTopLevelSpaces(s);
            int i = 0;
            if (i < parts.Count) {
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "circle")) { shape = RadialGradientShape.Circle; i++; }
                else if (CssStringUtil.EqualsIgnoreCase(parts[i], "ellipse")) { shape = RadialGradientShape.Ellipse; i++; }
            }
            // Collect explicit length / percent radii OR a sizing keyword
            // before the optional `at <x> <y>` clause. CSS Images 3 §3.7.1
            // allows the size keyword to appear in any order relative to
            // the shape token.
            double? r1 = null, r2 = null;
            while (i < parts.Count && !CssStringUtil.EqualsIgnoreCase(parts[i], "at")) {
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "closest-side")) {
                    sizing = RadialGradientSizing.ClosestSide; i++; continue;
                }
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "closest-corner")) {
                    sizing = RadialGradientSizing.ClosestCorner; i++; continue;
                }
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "farthest-side")) {
                    sizing = RadialGradientSizing.FarthestSide; i++; continue;
                }
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "farthest-corner")) {
                    sizing = RadialGradientSizing.FarthestCorner; i++; continue;
                }
                double axis = (r1 == null) ? bounds.Width : bounds.Height;
                double v = ResolveRadiusToken(parts[i], axis);
                if (double.IsNaN(v)) break;
                if (r1 == null) r1 = v;
                else if (r2 == null) r2 = v;
                else break;
                i++;
            }
            if (r1.HasValue) {
                overrideRx = r1.Value;
                overrideRy = r2 ?? r1.Value;
            }
            // Look for "at <pos-x> <pos-y>"
            for (; i < parts.Count; i++) {
                if (CssStringUtil.EqualsIgnoreCase(parts[i], "at")) {
                    if (i + 1 < parts.Count) cx = ResolvePositionAxis(parts[i + 1], bounds.Width, true);
                    if (i + 2 < parts.Count) cy = ResolvePositionAxis(parts[i + 2], bounds.Height, false);
                    break;
                }
            }
        }

        // Resolves a single radial-gradient radius token to pixels.
        // Accepts `<length>` (px) and `<percent>` (resolved against the
        // corresponding axis per CSS Images 3 §3.6.2 — caller passes box
        // width for rx and box height for ry).
        static double ResolveRadiusToken(string token, double axis) {
            if (string.IsNullOrEmpty(token)) return double.NaN;
            if (token.EndsWith("%")) {
                if (double.TryParse(token.AsSpan(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) {
                    return axis * pct * 0.01;
                }
                return double.NaN;
            }
            if (token.EndsWith("px", System.StringComparison.OrdinalIgnoreCase)) {
                if (double.TryParse(token.AsSpan(0, token.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) {
                    return px;
                }
                return double.NaN;
            }
            // Bare number (no unit) — treat as pixels for forgiveness.
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
            return double.NaN;
        }

        static double ResolvePositionAxis(string token, double basis, bool xAxis) {
            // Allocation-free keyword checks via CssStringUtil — the previous
            // `string lower = token.ToLowerInvariant()` allocated a fresh
            // string per call regardless of whether the token actually had
            // uppercase letters. Position resolution runs once per axis per
            // gradient layer per paint.
            if (xAxis) {
                if (CssStringUtil.EqualsIgnoreCase(token, "left")) return 0;
                if (CssStringUtil.EqualsIgnoreCase(token, "right")) return basis;
            } else {
                if (CssStringUtil.EqualsIgnoreCase(token, "top")) return 0;
                if (CssStringUtil.EqualsIgnoreCase(token, "bottom")) return basis;
            }
            if (CssStringUtil.EqualsIgnoreCase(token, "center")) return basis * 0.5;
            if (token != null && token.EndsWith("%")) {
                if (double.TryParse(token.AsSpan(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) {
                    return basis * pct * 0.01;
                }
            }
            if (token != null && token.EndsWith("px", System.StringComparison.OrdinalIgnoreCase)) {
                if (double.TryParse(token.AsSpan(0, token.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) {
                    return px;
                }
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
            return basis * 0.5;
        }

        static bool LooksLikeAngleOrToKeyword(string s, out double degrees) {
            degrees = 0;
            if (string.IsNullOrEmpty(s)) return false;
            // Allocation-free version: the Trim/ToLowerInvariant chain copied
            // the string twice on every call. Each check uses culture-free
            // case-insensitive ordinal comparison directly on the original
            // input. The "to <side>" branch is the only one that needs a
            // lowercased substring (passed to AngleFromTo); that allocation
            // is unavoidable for the slow path but at least we don't pay it
            // when the gradient is plain `45deg`.
            string trimmed = s.Trim();
            if (CssStringUtil.StartsWithIgnoreCase(trimmed, "to ")) {
                degrees = AngleFromTo(trimmed.Substring(3));
                return true;
            }
            if (trimmed.EndsWith("deg", System.StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("turn", System.StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("rad", System.StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("grad", System.StringComparison.OrdinalIgnoreCase)) {
                return RawValueParser.TryParseAngleDegrees(s, out degrees);
            }
            return false;
        }

        static double AngleFromTo(string body) {
            bool top = false, right = false, bottom = false, left = false;
            foreach (var rawPart in body.Split(' ', '\t')) {
                var part = rawPart.Trim();
                if (CssStringUtil.EqualsIgnoreCase(part, "top")) top = true;
                else if (CssStringUtil.EqualsIgnoreCase(part, "right")) right = true;
                else if (CssStringUtil.EqualsIgnoreCase(part, "bottom")) bottom = true;
                else if (CssStringUtil.EqualsIgnoreCase(part, "left")) left = true;
            }
            if (top && right) return 45;
            if (right && bottom) return 135;
            if (bottom && left) return 225;
            if (top && left) return 315;
            if (top) return 0;
            if (right) return 90;
            if (bottom) return 180;
            if (left) return 270;
            return 180;
        }

        static bool TryParseStop(string raw, LinearColor currentColor, out GradientStop stop) {
            stop = default;
            if (string.IsNullOrEmpty(raw)) return false;
            var parts = RawValueParser.SplitTopLevelSpaces(raw.Trim());
            if (parts.Count == 0) return false;
            // Find which token is the color and which is the position.
            string colorToken = null;
            string posToken = null;
            foreach (var part in parts) {
                if (colorToken == null && IsColorToken(part)) colorToken = part;
                else posToken = part;
            }
            if (colorToken == null) {
                // try whole thing
                if (IsColorToken(raw)) colorToken = raw;
                else return false;
            }
            if (!ColorResolver.TryResolve(colorToken, currentColor, null, out var color)) return false;
            double pos = double.NaN;
            bool isPx = false;
            if (posToken != null) {
                if (posToken.StartsWith("calc(", System.StringComparison.OrdinalIgnoreCase)) {
                    if (TryEvaluateLinearStopCalc(posToken, out double calcVal)) {
                        pos = calcVal;
                        // A px-only calc (e.g. calc(20px + 5px)) evaluates to an
                        // absolute pixel offset just like a literal px stop, so
                        // mark it for line-length resolution. Mixed %+px calc is
                        // left unresolved (rare; pre-existing behavior).
                        bool hasPx = posToken.IndexOf("px", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        bool hasPct = posToken.IndexOf('%') >= 0;
                        if (hasPx && !hasPct) isPx = true;
                    } else {
                        Weva.Diagnostics.UICssDiagnostics.Warn(
                            "BackgroundResolver",
                            "calc() in gradient stop position failed to evaluate, using fallback");
                    }
                } else if (posToken.EndsWith("%")) {
                    if (double.TryParse(posToken.AsSpan(0, posToken.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) {
                        pos = pct * 0.01;
                    }
                } else if (posToken.EndsWith("px")) {
                    if (double.TryParse(posToken.AsSpan(0, posToken.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) {
                        // Absolute pixel offset. Stored as-is and resolved to a
                        // fraction of the gradient-line length once the gradient
                        // is bound to a box/tile (ResolveAbsoluteStops) — a 1px
                        // stop in a 44px tile is a thin line, not 100%.
                        pos = px;
                        isPx = true;
                    }
                } else if (double.TryParse(posToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) {
                    pos = n;
                }
            }
            stop = new GradientStop(color, pos, isPx);
            return true;
        }

        // Evaluates calc() in linear-gradient stop positions through the full
        // L4 math evaluator. Linear gradients are bounds-independent (cached
        // by raw text alone), so the percentage basis is 1.0 — keeping the
        // result in the same `%`-as-fraction convention as the literal stop
        // parser above (`50%` stores 0.5). Returns false on any parse or
        // evaluation failure so the caller leaves the stop at NaN and
        // NormalizeStopPositions auto-spaces it.
        static bool TryEvaluateLinearStopCalc(string raw, out double value) {
            value = 0;
            try {
                var parsed = CssValueParser.Parse(raw);
                if (parsed is CssCalc calc) {
                    var ctx = LengthContext.Default;
                    ctx.BasisPixels = 1.0;
                    value = calc.Evaluate(ctx);
                    if (double.IsNaN(value) || double.IsInfinity(value)) return false;
                    return true;
                }
                return false;
            } catch {
                return false;
            }
        }

        static bool IsColorToken(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            if (t[0] == '#') return true;
            if (CssStringUtil.StartsWithIgnoreCase(t, "rgb(")
                || CssStringUtil.StartsWithIgnoreCase(t, "rgba(")
                || CssStringUtil.StartsWithIgnoreCase(t, "hsl(")
                || CssStringUtil.StartsWithIgnoreCase(t, "hsla(")) return true;
            if (CssStringUtil.StartsWithIgnoreCase(t, "hwb(")
                || CssStringUtil.StartsWithIgnoreCase(t, "oklab(")
                || CssStringUtil.StartsWithIgnoreCase(t, "oklch(")
                || CssStringUtil.StartsWithIgnoreCase(t, "lab(")
                || CssStringUtil.StartsWithIgnoreCase(t, "lch(")
                || CssStringUtil.StartsWithIgnoreCase(t, "color(")
                || CssStringUtil.StartsWithIgnoreCase(t, "color-mix(")) return true;
            if (CssStringUtil.EqualsIgnoreCase(t, "transparent")
                || CssStringUtil.IsCurrentColor(t)) return true;
            // CssColor.TryFromName requires lowercase; only allocate when we
            // can't rule the token out via the cheaper checks above.
            return CssColor.TryFromName(CssStringUtil.ToLowerInvariantOrSame(t), out _);
        }

        // Resolve absolute-px stop positions (e.g. `linear-gradient(c 1px,
        // transparent 1px)`) into 0–1 fractions of the gradient line, now that
        // the line length is known from the box (or tile) the gradient paints
        // into. Returns the SAME instance when no stop is absolute, so %-only
        // gradients keep sharing the bounds-independent parse cache and the
        // gradient-brush cache. Linear is resolved precisely (angle-aware line
        // length); radial/conic px stops are rare and pass through unchanged.
        internal static Gradient ResolveAbsoluteStops(Gradient g, double lineW, double lineH) {
            if (g == null) return g;
            var stops = g.Stops;
            bool any = false;
            for (int i = 0; i < stops.Count; i++) { if (stops[i].IsAbsolutePx) { any = true; break; } }
            if (!any) return g;
            if (!(g is LinearGradient lin)) return g; // radial/conic: leave as-is (rare)

            // CSS Images §3.4 gradient-line length for angle θ in a WxH box:
            // |W·sin θ| + |H·cos θ|. Axis-aligned angles reduce to W or H.
            double rad = lin.AngleDegrees * System.Math.PI / 180.0;
            double lineLen = System.Math.Abs(lineW * System.Math.Sin(rad))
                           + System.Math.Abs(lineH * System.Math.Cos(rad));
            if (lineLen <= 0.0001) return g;

            var resolved = new List<GradientStop>(stops.Count);
            for (int i = 0; i < stops.Count; i++) {
                var s = stops[i];
                double pos = s.IsAbsolutePx ? s.Position / lineLen : s.Position;
                resolved.Add(new GradientStop(s.Color, pos)); // fraction now; flag cleared
            }
            NormalizeStopPositions(resolved);
            return new LinearGradient(lin.AngleDegrees, resolved, lin.InterpolationSpace,
                lin.IsRepeating, lin.HueMethod);
        }

        static void NormalizeStopPositions(List<GradientStop> stops) {
            int n = stops.Count;
            if (n == 0) return;
            double[] pos = new double[n];
            bool[] explicitPos = new bool[n];
            for (int i = 0; i < n; i++) {
                pos[i] = stops[i].Position;
                explicitPos[i] = !double.IsNaN(stops[i].Position);
            }
            if (!explicitPos[0]) { pos[0] = 0; explicitPos[0] = true; }
            if (!explicitPos[n - 1]) { pos[n - 1] = 1; explicitPos[n - 1] = true; }
            int i0 = 0;
            while (i0 < n) {
                int j = i0 + 1;
                while (j < n && !explicitPos[j]) j++;
                if (j >= n) break;
                int gap = j - i0;
                if (gap > 1) {
                    double a = pos[i0];
                    double b = pos[j];
                    for (int k = 1; k < gap; k++) {
                        pos[i0 + k] = a + (b - a) * (k / (double)gap);
                    }
                }
                i0 = j;
            }
            for (int i = 1; i < n; i++) {
                if (pos[i] < pos[i - 1]) pos[i] = pos[i - 1];
            }
            for (int i = 0; i < n; i++) {
                stops[i] = new GradientStop(stops[i].Color, pos[i], stops[i].IsAbsolutePx);
            }
        }
    }
}
