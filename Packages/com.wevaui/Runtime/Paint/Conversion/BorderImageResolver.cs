using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Paint.Images;

namespace Weva.Paint.Conversion {
    // CSS Backgrounds 3 §6 `border-image`. Splits a source image into 9
    // sub-regions (4 corners, 4 edges, 1 center) and paints them into 9
    // matching dest sub-regions of the border-image area. Corners always
    // paint at their slice size unstretched; edges and center repeat or
    // stretch per `border-image-repeat`.
    //
    // Coordinate spaces:
    //   * Source slice values come from `border-image-slice` and
    //     describe pixel insets INTO the source image (top, right,
    //     bottom, left). Percentages resolve against the source image
    //     dimensions.
    //   * Border-image area = border-box expanded by
    //     `border-image-outset`. Edge thickness defaults to the regular
    //     `border-{top,right,bottom,left}-width`, but
    //     `border-image-width` overrides per side.
    //   * Returned `BorderImagePart` rects are box-local (paint-rect-
    //     relative). The converter passes them straight into FillRect
    //     commands.
    //
    // v1 simplifications: gradient sources unsupported (url() only);
    // `space` repeat collapses to `repeat` at the renderer (the source
    // enum is preserved upstream). The 9-slice approach renders fine for
    // typical HUD frames; exact pixel-rounding behaviour vs browsers is
    // not bit-perfect.
    internal static class BorderImageResolver {
        public readonly struct BorderImagePart {
            public readonly string Handle;             // image registry key
            public readonly Rect DestRect;             // box-local, paint-rect-relative
            public readonly Rect SourceRect;           // atlas UV (0..1)
            public readonly BackgroundTile? Tile;      // null = stretch
            public BorderImagePart(string handle, Rect dest, Rect source, BackgroundTile? tile) {
                Handle = handle;
                DestRect = dest;
                SourceRect = source;
                Tile = tile;
            }
        }

        // Back-compat overload. Callers that don't have a LengthContext
        // (e.g. older tests) fall back to LengthContext.Default — that's
        // fine for px/% slices and outsets but font-relative units (em,
        // rem) on border-image-{width,outset} will resolve against the
        // default 16px font size.
        public static void Resolve(
            ComputedStyle style,
            Box box,
            IImageRegistry imageRegistry,
            List<BorderImagePart> output
        ) {
            Resolve(style, box, imageRegistry, LengthContext.Default, output);
        }

        // Returns 8 or 9 parts when border-image-source resolves; empty
        // list otherwise. 8 parts = 4 corners + 4 edges; the 9th is the
        // center fill when `border-image-slice` ends with the `fill`
        // keyword. The caller (BoxToPaintConverter) iterates and emits
        // one FillRectCommand per part.
        public static void Resolve(
            ComputedStyle style,
            Box box,
            IImageRegistry imageRegistry,
            LengthContext ctx,
            List<BorderImagePart> output
        ) {
            output.Clear();
            if (style == null || box == null || imageRegistry == null) return;

            // All five longhands read directly from the per-style parsed
            // cache (ComputedStyle.GetParsed) — one O(1) slot lookup, no
            // dictionary probe and no per-frame CssValue.TryParse round
            // trip. Source resolves to CssUrl / CssKeyword "none"; slice,
            // width, outset, and repeat dispatch by typed CssValue kind
            // below.
            var srcParsed = style.GetParsed(CssProperties.BorderImageSourceId);
            if (!TryResolveSourceHandle(srcParsed, out var handle)) return;
            if (!imageRegistry.TryResolve(handle, out var source) || source == null) return;
            int srcW = source.Width;
            int srcH = source.Height;
            if (srcW <= 0 || srcH <= 0) return;

            var sliceParsed = style.GetParsed(CssProperties.BorderImageSliceId);
            bool fill;
            double sTop, sRight, sBottom, sLeft;
            if (TryResolveSourceNineSlice(source, sliceParsed,
                    out sTop, out sRight, out sBottom, out sLeft, out bool authorFill)) {
                // Sprite-supplied 9-slice (Unity sprite.border, etc.). The
                // center of a sliced sprite is meaningful content — Unity
                // UGUI's Image with Sliced sprite paints it by default. Match
                // that ergonomic: fill=true. An earlier version tried to let
                // `border-image-slice: 100%` (no `fill`) opt out via
                // `sliceParsed == null`, but the `border:` SHORTHAND resets
                // border-image-slice to its initial `100%` (CSS Backgrounds 3
                // §6), so on any element with a `border:` declaration the
                // parsed slice is never null and the documented default never
                // fired (9slice-demo's "Default (sprite borders + fill)" card
                // painted no center). Author 100% and reset 100% are
                // indistinguishable here; the sprite ergonomic wins.
                fill = true;
            } else {
                ParseSliceFour(sliceParsed, srcW, srcH,
                    out sTop, out sRight, out sBottom, out sLeft,
                    out fill);
            }
            ParseRepeatPair(style.GetParsed(CssProperties.BorderImageRepeatId),
                out var repeatX, out var repeatY);

            // CSS Backgrounds 3 §6.3 — border-image-outset extends the
            // border-image area beyond the border-box on each side. The
            // dest origin shifts by -outset and the dest size grows by
            // 2*outset. Resolved per side because `outset` accepts up to
            // four values.
            ParseOutsetFour(style.GetParsed(CssProperties.BorderImageOutsetId), ctx,
                box.BorderTop, box.BorderRight, box.BorderBottom, box.BorderLeft,
                out double oTop, out double oRight, out double oBottom, out double oLeft);

            // CSS Backgrounds 3 §6.4 — border-image-width replaces the
            // dest edge thickness (which would otherwise come from
            // border-{side}-width). A unitless number multiplies the
            // border width; `auto` falls back to the slice value
            // (resolved as source pixels). Percentage resolves against
            // the border-image area dimension.
            double areaW = box.Width + oLeft + oRight;
            double areaH = box.Height + oTop + oBottom;
            ParseBorderImageWidth(style.GetParsed(CssProperties.BorderImageWidthId), ctx,
                box.BorderTop, box.BorderRight, box.BorderBottom, box.BorderLeft,
                sTop, sRight, sBottom, sLeft,
                areaW, areaH,
                out double wTop, out double wRight, out double wBottom, out double wLeft);

            // Per spec, if the sum of opposite widths exceeds the
            // border-image area, scale them proportionally so the
            // corners don't overlap and the center collapses to zero.
            if (wLeft + wRight > areaW && wLeft + wRight > 0) {
                double scale = areaW / (wLeft + wRight);
                wLeft *= scale; wRight *= scale;
            }
            if (wTop + wBottom > areaH && wTop + wBottom > 0) {
                double scale = areaH / (wTop + wBottom);
                wTop *= scale; wBottom *= scale;
            }

            // Border-image area, expressed in box-local coordinates.
            // (-outset, -outset) origin; (areaW, areaH) size.
            double ax = -oLeft;
            double ay = -oTop;

            // Dest size of the center column / row in box pixels:
            double dCenterW = System.Math.Max(0, areaW - wLeft - wRight);
            double dCenterH = System.Math.Max(0, areaH - wTop - wBottom);
            // Source center column / row in source pixels:
            double sCenterW = System.Math.Max(0, srcW - sLeft - sRight);
            double sCenterH = System.Math.Max(0, srcH - sTop - sBottom);

            float u0 = (float)(sLeft / srcW);
            float u1 = (float)((srcW - sRight) / srcW);
            float v0 = (float)(sTop / srcH);
            float v1 = (float)((srcH - sBottom) / srcH);
            float wL = (float)(sLeft / srcW);
            float wR = (float)(sRight / srcW);
            float wT = (float)(sTop / srcH);
            float wB = (float)(sBottom / srcH);

            // Source-rect V is BOTTOM-UP (the image sampler maps quad UV.y via
            // lerp(v1, v0, uv.y) for Unity's bottom-left texture origin). The
            // slices are computed top-down (sTop = top inset), so each part's
            // source V must be flipped into bottom-up space or the top/bottom
            // edges + corners sample the opposite half of the source and render
            // swapped. See the matching note in BoxToPaintConverter.EmitImageNineSlice.
            //   top band    → V start 1 - wT
            //   bottom band → V start 0
            //   center band → V start wB, height v1 - v0
            float topV = 1f - wT;
            float botV = 0f;
            float midV = wB;
            float midVH = v1 - v0;

            // Half-texel inset to kill seam bleed — see the matching note in
            // BoxToPaintConverter.EmitImageNineSlice. Bilinear filtering at a
            // slice boundary samples half a texel into the neighbouring slice,
            // leaving a thin dark line between parts; pulling each source
            // sub-rect inward by half a texel keeps the footprint inside the
            // slice.
            float tu = 0.5f / srcW;
            float tv = 0.5f / srcH;
            Rect SI(float x, float y, float w, float h) =>
                new Rect(x + tu, y + tv, System.Math.Max(0f, w - 2f * tu), System.Math.Max(0f, h - 2f * tv));

            // Corners: stretched into their dest cell; just maps source corner
            // → dest corner. Tile null = stretch.
            output.Add(new BorderImagePart(handle,
                new Rect(ax, ay, wLeft, wTop),
                SI(0, topV, wL, wT),
                null));
            output.Add(new BorderImagePart(handle,
                new Rect(ax + areaW - wRight, ay, wRight, wTop),
                SI(u1, topV, wR, wT),
                null));
            output.Add(new BorderImagePart(handle,
                new Rect(ax + areaW - wRight, ay + areaH - wBottom, wRight, wBottom),
                SI(u1, botV, wR, wB),
                null));
            output.Add(new BorderImagePart(handle,
                new Rect(ax, ay + areaH - wBottom, wLeft, wBottom),
                SI(0, botV, wL, wB),
                null));

            // Edges: tile along the edge's axis when repeat=Repeat/Round/
            // Space; stretch when repeat=Stretch. Source tile size for
            // an edge = (centerSizeInSource, sliceWidth); dest tile size
            // scales by the dest/source ratio in the unscaled axis so
            // the edge-thickness pixel ratio matches the corner's.
            EmitEdge(output, handle,
                destRect: new Rect(ax + wLeft, ay, dCenterW, wTop),
                sourceRect: SI(u0, topV, u1 - u0, wT),
                isHorizontal: true,
                repeat: repeatX,
                sourceTileLength: sCenterW,
                destEdgeThickness: wTop);
            EmitEdge(output, handle,
                destRect: new Rect(ax + areaW - wRight, ay + wTop, wRight, dCenterH),
                sourceRect: SI(u1, midV, wR, midVH),
                isHorizontal: false,
                repeat: repeatY,
                sourceTileLength: sCenterH,
                destEdgeThickness: wRight);
            EmitEdge(output, handle,
                destRect: new Rect(ax + wLeft, ay + areaH - wBottom, dCenterW, wBottom),
                sourceRect: SI(u0, botV, u1 - u0, wB),
                isHorizontal: true,
                repeat: repeatX,
                sourceTileLength: sCenterW,
                destEdgeThickness: wBottom);
            EmitEdge(output, handle,
                destRect: new Rect(ax, ay + wTop, wLeft, dCenterH),
                sourceRect: SI(0, midV, wL, midVH),
                isHorizontal: false,
                repeat: repeatY,
                sourceTileLength: sCenterH,
                destEdgeThickness: wLeft);

            // CSS Backgrounds 3 §6.1 — `fill` keyword in border-image-slice
            // preserves the middle slice (otherwise discarded). The center
            // dest area is the area inside the four edges and the source
            // UV rect is (u0,v0)-(u1,v1). We honour repeatX/repeatY on the
            // center the same way edges do, except the tile must cover
            // both axes if any axis is non-stretch.
            if (fill && dCenterW > 0 && dCenterH > 0) {
                var centerDest = new Rect(ax + wLeft, ay + wTop, dCenterW, dCenterH);
                var centerSrc = SI(u0, midV, u1 - u0, midVH);
                BackgroundTile? centerTile = null;
                bool repX = repeatX != BackgroundRepeatMode.NoRepeat;
                bool repY = repeatY != BackgroundRepeatMode.NoRepeat;
                if (repX || repY) {
                    double tileW = repX ? System.Math.Max(1, sCenterW) : dCenterW;
                    double tileH = repY ? System.Math.Max(1, sCenterH) : dCenterH;
                    centerTile = new BackgroundTile(
                        tileW, tileH, 0, 0,
                        repX ? BackgroundRepeatMode.Repeat : BackgroundRepeatMode.NoRepeat,
                        repY ? BackgroundRepeatMode.Repeat : BackgroundRepeatMode.NoRepeat);
                }
                output.Add(new BorderImagePart(handle, centerDest, centerSrc, centerTile));
            }
        }

        // CSS Backgrounds 3 §6.2 — border-image-source: `none` | url(...)
        // | <gradient>. v1 only paints url() sources, so anything else
        // returns false. The parser maps an unquoted url(...) to CssUrl
        // directly; quoted forms also surface as CssUrl via ParseUrlFn.
        // Function-call sources (gradients) fall through — they'd render
        // as a textured edge atlas, which we don't model here yet.
        static bool TryResolveSourceHandle(CssValue parsed, out string handle) {
            handle = null;
            if (parsed == null) return false;
            // `none` keyword / identifier shortcuts.
            if (parsed is CssKeyword k && k.Identifier == "none") return false;
            if (parsed is CssIdentifier id && CssStringUtil.EqualsIgnoreCase(id.Name, "none")) return false;
            if (parsed is CssUrl url) {
                handle = url.Href ?? "";
                return handle.Length > 0;
            }
            return false;
        }

        static bool TryResolveSourceNineSlice(IImageSource source, CssValue sliceParsed,
            out double top, out double right, out double bottom, out double left,
            out bool authorFill) {
            top = right = bottom = left = 0;
            authorFill = false;
            if (!IsInitialOrFillOnlySlice(sliceParsed, out authorFill)) return false;
            if (source is not IImageNineSliceSource nineSliceSource) return false;
            if (!nineSliceSource.TryGetNineSlice(out var slice) || slice.IsEmpty) return false;
            top = slice.Top;
            right = slice.Right;
            bottom = slice.Bottom;
            left = slice.Left;
            return true;
        }

        // Slice value is "initial" — i.e. the author hasn't supplied numeric
        // slice components, so the source's native 9-slice metadata (e.g.
        // Unity sprite.border) should drive the slice. Accepts: null (unset),
        // 100%, `fill` alone, or any mix of 100%/fill values. `authorFill` is
        // true when the parsed value contains the `fill` keyword.
        static bool IsInitialOrFillOnlySlice(CssValue parsed, out bool authorFill) {
            authorFill = false;
            if (parsed == null) return true;
            if (parsed is CssPercentage p) {
                return System.Math.Abs(p.Value - 100.0) < 0.0001;
            }
            if (IsFillKeyword(parsed)) {
                authorFill = true;
                return true;
            }
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                bool sawFill = false;
                foreach (var item in list.Items) {
                    if (IsFillKeyword(item)) { sawFill = true; continue; }
                    if (item is CssPercentage pp && System.Math.Abs(pp.Value - 100.0) < 0.0001) continue;
                    return false;
                }
                authorFill = sawFill;
                return true;
            }
            return false;
        }

        static void EmitEdge(
            List<BorderImagePart> output,
            string handle,
            Rect destRect, Rect sourceRect,
            bool isHorizontal,
            BackgroundRepeatMode repeat,
            double sourceTileLength,
            double destEdgeThickness
        ) {
            if (destRect.Width <= 0 || destRect.Height <= 0) return;

            BackgroundTile? tile;
            if (repeat == BackgroundRepeatMode.Repeat || repeat == BackgroundRepeatMode.Round
                || repeat == BackgroundRepeatMode.Space) {
                // Compute one tile's dest length along the repeating axis.
                //
                // `repeat`: use the natural source-pixel length 1:1 so each
                // tile occupies exactly `sourceTileLength` dest pixels, matching
                // CSS Backgrounds 3 §6.2 (partial tiles may clip at the edges).
                //
                // `round`: scale each tile so a whole number of tiles fills the
                // edge exactly — no clipping, no gaps. CSS Backgrounds 3 §6.2:
                //   tileCount = max(1, round(edgeLength / naturalTileLength))
                //   tileLen   = edgeLength / tileCount
                // This preserves the tile's source-pixel content but scales its
                // rendered size so tiles pack exactly into the dest edge.
                double naturalLen = System.Math.Max(1, sourceTileLength);
                double tileLen;
                if (repeat == BackgroundRepeatMode.Round) {
                    double edgeLen = isHorizontal ? destRect.Width : destRect.Height;
                    int tileCount = System.Math.Max(1,
                        (int)System.Math.Round(edgeLen / naturalLen));
                    tileLen = edgeLen / tileCount;
                } else {
                    tileLen = naturalLen;
                }
                if (isHorizontal) {
                    tile = new BackgroundTile(
                        tileLen, destRect.Height,
                        0, 0,
                        BackgroundRepeatMode.Repeat,
                        BackgroundRepeatMode.NoRepeat);
                } else {
                    tile = new BackgroundTile(
                        destRect.Width, tileLen,
                        0, 0,
                        BackgroundRepeatMode.NoRepeat,
                        BackgroundRepeatMode.Repeat);
                }
            } else {
                // Stretch: full-bleed.
                tile = null;
            }
            output.Add(new BorderImagePart(handle, destRect, sourceRect, tile));
        }

        // CSS Backgrounds 3 §6.1 — parses
        // `border-image-slice: <number-or-percent>{1,4} [fill]?`. Numbers
        // without units are interpreted as source pixels per spec.
        // Returns the optional `fill` flag through the `fill` out-param so
        // the caller can emit the center patch when present.
        static void ParseSliceFour(CssValue parsed, int srcW, int srcH,
            out double top, out double right, out double bottom, out double left, out bool fill) {
            top = 0; right = 0; bottom = 0; left = 0; fill = false;
            if (parsed == null) return;

            // Collect 1-4 value components, capturing the `fill` keyword
            // (which can appear in any position per spec, though it's
            // conventionally placed last). The parser may give us:
            //   * "8"          → single CssNumber
            //   * "25%"        → single CssPercentage
            //   * "8 16"       → CssValueList(space, [Number, Number])
            //   * "8 fill"     → CssValueList(space, [Number, Keyword "fill"])
            //   * "fill 8"     → CssValueList(space, [Keyword "fill", Number])
            CssValue v0 = null, v1 = null, v2 = null, v3 = null;
            int n = 0;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                // Scan all items in the list regardless of how many numeric
                // components we've seen. The `fill` keyword may appear AFTER
                // all four numeric components ("10 20 30 40 fill") — the old
                // `n < 4` loop-exit condition would stop before reaching it.
                // Non-fill items beyond index 3 are silently ignored (the spec
                // only allows up to 4 numeric components).
                for (int i = 0; i < list.Items.Count; i++) {
                    var item = list.Items[i];
                    if (IsFillKeyword(item)) { fill = true; continue; }
                    // Accept up to 4 numeric components; any surplus non-fill
                    // items are ignored per spec.
                    if (n < 4) {
                        switch (n) {
                            case 0: v0 = item; break;
                            case 1: v1 = item; break;
                            case 2: v2 = item; break;
                            case 3: v3 = item; break;
                        }
                        n++;
                    }
                }
            } else if (IsFillKeyword(parsed)) {
                fill = true;
            } else {
                v0 = parsed; n = 1;
            }
            if (n == 0) return;

            // CSS shorthand expansion: 1 → all four; 2 → vertical/horizontal;
            // 3 → top, horizontal, bottom; 4 → top, right, bottom, left.
            // Each component is resolved against its axis dimension: top/
            // bottom use srcH, left/right use srcW (percent only).
            switch (n) {
                case 1: {
                    double a = ResolveSliceAxis(v0, srcH); // any axis ok for square slice
                    double b = ResolveSliceAxis(v0, srcW);
                    top = a; bottom = a;
                    left = b; right = b;
                    break;
                }
                case 2: {
                    top = bottom = ResolveSliceAxis(v0, srcH);
                    left = right = ResolveSliceAxis(v1, srcW);
                    break;
                }
                case 3: {
                    top = ResolveSliceAxis(v0, srcH);
                    left = right = ResolveSliceAxis(v1, srcW);
                    bottom = ResolveSliceAxis(v2, srcH);
                    break;
                }
                default: {
                    top = ResolveSliceAxis(v0, srcH);
                    right = ResolveSliceAxis(v1, srcW);
                    bottom = ResolveSliceAxis(v2, srcH);
                    left = ResolveSliceAxis(v3, srcW);
                    break;
                }
            }
            // Clamp so opposite slices don't overlap.
            if (top + bottom > srcH) {
                double scale = srcH / (top + bottom);
                top *= scale; bottom *= scale;
            }
            if (left + right > srcW) {
                double scale = srcW / (left + right);
                left *= scale; right *= scale;
            }
        }

        // Resolves a single slice component to source-pixel units.
        // Numbers are raw source pixels per spec; percentages resolve
        // against the axis dimension.
        static double ResolveSliceAxis(CssValue v, double axisDimension) {
            if (v == null) return 0;
            if (v is CssNumber n) return n.Value;
            if (v is CssPercentage p) return axisDimension * p.Value * 0.01;
            // CssLength surfaces if an author writes "8px" — spec disallows
            // length units here, but the previous parser silently accepted
            // them via double.TryParse on the bare number, so keep that
            // tolerance (only the numeric magnitude matters in source-pixel
            // units anyway).
            if (v is CssLength len) return len.Value;
            return 0;
        }

        static bool IsFillKeyword(CssValue v) {
            if (v is CssKeyword k) return CssStringUtil.EqualsIgnoreCase(k.Identifier, "fill");
            if (v is CssIdentifier id) return CssStringUtil.EqualsIgnoreCase(id.Name, "fill");
            return false;
        }

        static bool IsAutoKeyword(CssValue v) {
            if (v is CssKeyword k) return CssStringUtil.EqualsIgnoreCase(k.Identifier, "auto");
            if (v is CssIdentifier id) return CssStringUtil.EqualsIgnoreCase(id.Name, "auto");
            return false;
        }

        // CSS Backgrounds 3 §6.3 — `border-image-outset` accepts 1-4
        // values that expand per the standard CSS four-side shorthand.
        // Each component is <length> or <number>; bare numbers multiply
        // the corresponding border-width (per spec). Percentages are
        // not allowed by the spec and silently resolve to zero.
        static void ParseOutsetFour(CssValue parsed, LengthContext ctx,
            double bTop, double bRight, double bBottom, double bLeft,
            out double top, out double right, out double bottom, out double left) {
            top = 0; right = 0; bottom = 0; left = 0;
            if (parsed == null) return;

            CssValue v0 = null, v1 = null, v2 = null, v3 = null;
            int n = 0;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                for (int i = 0; i < list.Items.Count && n < 4; i++) {
                    var item = list.Items[i];
                    switch (n) {
                        case 0: v0 = item; break;
                        case 1: v1 = item; break;
                        case 2: v2 = item; break;
                        case 3: v3 = item; break;
                    }
                    n++;
                }
            } else {
                v0 = parsed; n = 1;
            }
            if (n == 0) return;

            // Standard 1/2/3/4-value CSS shorthand expansion.
            CssValue cTop, cRight, cBottom, cLeft;
            switch (n) {
                case 1: cTop = cRight = cBottom = cLeft = v0; break;
                case 2: cTop = cBottom = v0; cLeft = cRight = v1; break;
                case 3: cTop = v0; cLeft = cRight = v1; cBottom = v2; break;
                default: cTop = v0; cRight = v1; cBottom = v2; cLeft = v3; break;
            }
            top = ResolveOutsetComponent(cTop, ctx, bTop);
            right = ResolveOutsetComponent(cRight, ctx, bRight);
            bottom = ResolveOutsetComponent(cBottom, ctx, bBottom);
            left = ResolveOutsetComponent(cLeft, ctx, bLeft);
        }

        // Single border-image-outset component. <length> resolves
        // through the LengthContext; bare numbers (per spec) multiply
        // the corresponding border-width.
        static double ResolveOutsetComponent(CssValue v, LengthContext ctx, double borderWidth) {
            if (v == null) return 0;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber num) return num.Value * borderWidth;
            return 0;
        }

        // CSS Backgrounds 3 §6.4 — border-image-width:
        //   <length-percentage> | <number> | auto
        // per side. `<number>` multiplies the corresponding border-width;
        // `auto` falls back to the slice value (in source pixels);
        // `<percentage>` resolves against the border-image area dimension.
        static void ParseBorderImageWidth(CssValue parsed, LengthContext ctx,
            double bTop, double bRight, double bBottom, double bLeft,
            double sTop, double sRight, double sBottom, double sLeft,
            double areaW, double areaH,
            out double wTop, out double wRight, out double wBottom, out double wLeft) {
            // Initial values default to the corresponding border widths
            // (matches the regular border rendering when the property is
            // unset — keeps single-property `border-image-source: url(...)`
            // working without forcing authors to also write border-image-
            // width).
            wTop = bTop; wRight = bRight; wBottom = bBottom; wLeft = bLeft;
            if (parsed == null) return;

            CssValue v0 = null, v1 = null, v2 = null, v3 = null;
            int n = 0;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                for (int i = 0; i < list.Items.Count && n < 4; i++) {
                    var item = list.Items[i];
                    switch (n) {
                        case 0: v0 = item; break;
                        case 1: v1 = item; break;
                        case 2: v2 = item; break;
                        case 3: v3 = item; break;
                    }
                    n++;
                }
            } else {
                v0 = parsed; n = 1;
            }
            if (n == 0) return;

            CssValue cTop, cRight, cBottom, cLeft;
            switch (n) {
                case 1: cTop = cRight = cBottom = cLeft = v0; break;
                case 2: cTop = cBottom = v0; cLeft = cRight = v1; break;
                case 3: cTop = v0; cLeft = cRight = v1; cBottom = v2; break;
                default: cTop = v0; cRight = v1; cBottom = v2; cLeft = v3; break;
            }
            wTop = ResolveWidthComponent(cTop, ctx, bTop, sTop, areaH);
            wRight = ResolveWidthComponent(cRight, ctx, bRight, sRight, areaW);
            wBottom = ResolveWidthComponent(cBottom, ctx, bBottom, sBottom, areaH);
            wLeft = ResolveWidthComponent(cLeft, ctx, bLeft, sLeft, areaW);
        }

        // border-image-width component. Percentages resolve against the
        // border-image area dimension on the relevant axis (height for
        // top/bottom, width for left/right). Lengths via LengthContext.
        // Bare numbers multiply the corresponding border-width. `auto`
        // falls back to the slice value (source pixels).
        static double ResolveWidthComponent(CssValue v, LengthContext ctx,
            double borderWidth, double sliceSourcePx, double axisDimension) {
            if (v == null) return borderWidth;
            if (IsAutoKeyword(v)) return sliceSourcePx;
            if (v is CssLength len) {
                if (len.Unit == CssLengthUnit.Percent) {
                    return axisDimension * len.Value * 0.01;
                }
                return len.ToPixels(ctx);
            }
            if (v is CssPercentage p) return axisDimension * p.Value * 0.01;
            if (v is CssNumber num) return num.Value * borderWidth;
            return borderWidth;
        }

        // CSS Backgrounds 3 §6.5 — border-image-repeat: 1-2 keywords
        // (stretch/repeat/round/space). Walks the parsed tree directly —
        // the parser maps each keyword to CssKeyword (or CssIdentifier
        // for the unknown-name fallback path).
        static void ParseRepeatPair(CssValue parsed, out BackgroundRepeatMode x, out BackgroundRepeatMode y) {
            x = BackgroundRepeatMode.NoRepeat;
            y = BackgroundRepeatMode.NoRepeat;
            if (parsed == null) return;
            CssValue first = parsed;
            CssValue second = null;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space && list.Items.Count > 0) {
                first = list.Items[0];
                if (list.Items.Count > 1) second = list.Items[1];
            }
            x = ParseSingle(first);
            y = second != null ? ParseSingle(second) : x;
        }

        static BackgroundRepeatMode ParseSingle(CssValue v) {
            string name = null;
            if (v is CssKeyword k) name = k.Identifier;
            else if (v is CssIdentifier id) name = id.Name;
            if (string.IsNullOrEmpty(name)) return BackgroundRepeatMode.NoRepeat;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "stretch")) return BackgroundRepeatMode.NoRepeat;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "repeat")) return BackgroundRepeatMode.Repeat;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "round")) return BackgroundRepeatMode.Round;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "space")) return BackgroundRepeatMode.Space;
            return BackgroundRepeatMode.NoRepeat;
        }
    }
}
