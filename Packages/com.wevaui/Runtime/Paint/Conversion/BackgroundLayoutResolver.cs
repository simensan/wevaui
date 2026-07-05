using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // Resolves background-position / background-size / background-repeat
    // into a `BackgroundTile`. Inputs:
    //   * Box bounds (where the image paints; v1 = border-box)
    //   * Intrinsic image size (from the registered IImageSource)
    //   * The CSS computed values (raw strings out of ComputedStyle)
    //
    // Output: a BackgroundTile with absolute pixel dimensions / origin
    // and the parsed RepeatMode pair, ready for the renderer to consume.
    //
    // Why this lives next to BackgroundResolver: same hot path — every
    // background-image:url() needs all three resolved together.
    internal static class BackgroundLayoutResolver {
        // Resolves the trio. `intrinsicWidth`/`intrinsicHeight` come from
        // the IImageRegistry's IImageSource. Pass 0 if unknown — `auto`
        // sizes degrade to (0,0) which makes layout authors notice
        // (matches CSS for an unloaded image).
        public static BackgroundTile Resolve(
            ComputedStyle style,
            Rect bounds,
            double intrinsicWidth,
            double intrinsicHeight,
            LengthContext lengthCtx
        ) {
            // Single-layer entry point: read the typed parse tree out of the
            // per-style cache (ComputedStyle.GetParsed) so we skip the
            // global string→CssValue probe and dispatch entirely on the
            // typed tree below. Each Get is an O(1) array lookup.
            var sizeParsed = style != null ? style.GetParsed(CssProperties.BackgroundSizeId) : null;
            var posParsed = style != null ? style.GetParsed(CssProperties.BackgroundPositionId) : null;
            var repeatParsed = style != null ? style.GetParsed(CssProperties.BackgroundRepeatId) : null;
            return ResolveTyped(style, bounds, intrinsicWidth, intrinsicHeight, lengthCtx,
                posParsed, sizeParsed, repeatParsed);
        }

        // Per-layer overload. Callers that have already split the multi-layer
        // longhand strings on top-level commas pass the layer-specific values
        // directly so `background-position: 0 0, 30vmin 0, ...` resolves the
        // correct origin per layer instead of treating the whole comma-list
        // as one position string. Used by BackgroundResolver's multi-layer
        // walker.
        public static BackgroundTile Resolve(
            ComputedStyle style,
            Rect bounds,
            double intrinsicWidth,
            double intrinsicHeight,
            LengthContext lengthCtx,
            string posRaw,
            string sizeRaw,
            string repeatRaw
        ) {
            // Convert the per-layer raw text into a typed CssValue once and
            // dispatch via the typed path. Single TryParse per longhand per
            // layer — the multi-layer caller (BackgroundResolver) already
            // split on top-level commas, so this is the only re-parse needed.
            CssValue size = TryParseValue(sizeRaw);
            CssValue pos = TryParseValue(posRaw);
            CssValue repeat = TryParseValue(repeatRaw);
            return ResolveTyped(style, bounds, intrinsicWidth, intrinsicHeight, lengthCtx,
                pos, size, repeat);
        }

        static CssValue TryParseValue(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            return CssValue.TryParse(raw, out var v) ? v : null;
        }

        static BackgroundTile ResolveTyped(
            ComputedStyle style,
            Rect bounds,
            double intrinsicWidth,
            double intrinsicHeight,
            LengthContext lengthCtx,
            CssValue posParsed,
            CssValue sizeParsed,
            CssValue repeatParsed
        ) {
            ResolveSize(sizeParsed, bounds.Width, bounds.Height, intrinsicWidth, intrinsicHeight, lengthCtx, style,
                out double tileW, out double tileH);
            ResolveRepeat(repeatParsed, out var repeatX, out var repeatY);

            // CSS Backgrounds 3 §3.6: `round` adjusts tile size so a whole
            // number of repetitions fits the box exactly. Apply BEFORE
            // computing position so origin resolves against the rounded tile.
            double gapX = 0, gapY = 0;
            if (repeatX == BackgroundRepeatMode.Round) {
                tileW = ApplyRound(bounds.Width, tileW);
            }
            if (repeatY == BackgroundRepeatMode.Round) {
                tileH = ApplyRound(bounds.Height, tileH);
            }

            // Position resolves first so we know where the FIRST tile sits.
            // `space` overrides position to 0 (per spec, first tile is flush
            // with the container edge), but we record the parsed position in
            // case the gap computation degenerates (single tile fits).
            ResolvePosition(posParsed, bounds.Width, bounds.Height, tileW, tileH, lengthCtx, style,
                out double originX, out double originY);

            // `space`: distribute leftover width/height evenly as gaps
            // between tiles, with the first tile flush at the container's
            // origin and the last tile flush at the far edge.
            if (repeatX == BackgroundRepeatMode.Space) {
                ApplySpace(bounds.Width, tileW, ref originX, out gapX);
            }
            if (repeatY == BackgroundRepeatMode.Space) {
                ApplySpace(bounds.Height, tileH, ref originY, out gapY);
            }

            return new BackgroundTile(tileW, tileH, originX, originY, repeatX, repeatY, gapX, gapY);
        }

        // Round: pick the tile size that lets a whole number of reps fit
        // the container exactly. count = round(box / tile); tile = box / count.
        // Falls back to the original tile when box or tile is 0 (avoids
        // degenerate division — unloaded image / collapsed box).
        static double ApplyRound(double box, double tile) {
            if (box <= 0 || tile <= 0) return tile;
            double rawCount = box / tile;
            int count = (int)System.Math.Round(rawCount);
            if (count <= 0) count = 1;
            return box / count;
        }

        // Space: compute even gap between tiles. count = floor(box / tile);
        // gap = (box - count*tile) / max(count - 1, 1). When the tile is
        // larger than the box (count == 0 → clamp to 1), no gap; the tile
        // is positioned per the parsed background-position.
        static void ApplySpace(double box, double tile, ref double origin, out double gap) {
            gap = 0;
            if (box <= 0 || tile <= 0) return;
            int count = (int)System.Math.Floor(box / tile);
            if (count <= 1) {
                // Single tile (or zero fits) — no gap; keep parsed origin.
                return;
            }
            double leftover = box - count * tile;
            gap = leftover / (count - 1);
            // Per CSS spec, the first tile aligns with the container origin
            // when `space` resolves; the position keyword is ignored on this
            // axis. Setting origin = 0 makes that explicit.
            origin = 0;
        }

        // ---------- size ----------

        // String entry point kept for callers outside this file. Parses
        // through CssValue.TryParse and forwards to the typed path.
        public static void ResolveSize(
            string raw, double boxW, double boxH,
            double intrinsicW, double intrinsicH,
            LengthContext ctx, ComputedStyle style,
            out double tileW, out double tileH
        ) {
            CssValue parsed = TryParseValue(raw);
            ResolveSize(parsed, boxW, boxH, intrinsicW, intrinsicH, ctx, style, out tileW, out tileH);
        }

        // Typed entry point. The parsed tree is one of:
        //   * null / unparseable                  → intrinsic (treated as "auto")
        //   * CssKeyword "auto" / "cover" / "contain"
        //   * single <length-percent>             → applied to both axes
        //   * CssValueList(space) of two values   → per-axis x/y
        public static void ResolveSize(
            CssValue parsed, double boxW, double boxH,
            double intrinsicW, double intrinsicH,
            LengthContext ctx, ComputedStyle style,
            out double tileW, out double tileH
        ) {
            tileW = 0;
            tileH = 0;
            if (parsed == null || IsKeyword(parsed, "auto")) {
                tileW = intrinsicW;
                tileH = intrinsicH;
                return;
            }
            if (IsKeyword(parsed, "cover")) {
                FitCover(boxW, boxH, intrinsicW, intrinsicH, out tileW, out tileH);
                return;
            }
            if (IsKeyword(parsed, "contain")) {
                FitContain(boxW, boxH, intrinsicW, intrinsicH, out tileW, out tileH);
                return;
            }

            CssValue xPart = parsed;
            CssValue yPart = null;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space && list.Items.Count >= 1) {
                xPart = list.Items[0];
                if (list.Items.Count >= 2) yPart = list.Items[1];
            }

            tileW = ResolveSingleSize(xPart, boxW, intrinsicW, ctx);
            tileH = yPart != null
                ? ResolveSingleSize(yPart, boxH, intrinsicH, ctx)
                : intrinsicH; // missing yPart implies auto

            // CSS Backgrounds 3 §3.9: when one axis is `auto` and the other
            // is a length/percent, the auto axis derives from the intrinsic
            // aspect ratio.
            bool xAuto = xPart == null || IsKeyword(xPart, "auto");
            bool yAuto = yPart == null || IsKeyword(yPart, "auto");
            if (intrinsicW > 0 && intrinsicH > 0) {
                if (xAuto && !yAuto) tileW = tileH * (intrinsicW / intrinsicH);
                else if (yAuto && !xAuto) tileH = tileW * (intrinsicH / intrinsicW);
                else if (xAuto && yAuto) { tileW = intrinsicW; tileH = intrinsicH; }
            }
        }

        static double ResolveSingleSize(CssValue v, double box, double intrinsic, LengthContext ctx) {
            if (v == null || IsKeyword(v, "auto")) return intrinsic;
            if (v is CssPercentage p) return box * p.Value * 0.01;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber n) return n.Value;
            if (v is CssCalc calc) {
                try {
                    var c = ctx;
                    c.BasisPixels = box;
                    return calc.Evaluate(c);
                } catch { return intrinsic; }
            }
            return intrinsic;
        }

        static void FitCover(double boxW, double boxH, double intrinsicW, double intrinsicH,
                             out double tileW, out double tileH) {
            if (intrinsicW <= 0 || intrinsicH <= 0) {
                tileW = boxW; tileH = boxH;
                return;
            }
            double ratio = intrinsicW / intrinsicH;
            double byHeight = boxH * ratio;
            if (byHeight >= boxW) {
                tileW = byHeight;
                tileH = boxH;
            } else {
                tileW = boxW;
                tileH = boxW / ratio;
            }
        }

        static void FitContain(double boxW, double boxH, double intrinsicW, double intrinsicH,
                               out double tileW, out double tileH) {
            if (intrinsicW <= 0 || intrinsicH <= 0) {
                tileW = boxW; tileH = boxH;
                return;
            }
            double ratio = intrinsicW / intrinsicH;
            double byHeight = boxH * ratio;
            if (byHeight <= boxW) {
                tileW = byHeight;
                tileH = boxH;
            } else {
                tileW = boxW;
                tileH = boxW / ratio;
            }
        }

        // ---------- position ----------

        public static void ResolvePosition(
            string raw, double boxW, double boxH,
            double tileW, double tileH,
            LengthContext ctx, ComputedStyle style,
            out double originX, out double originY
        ) {
            CssValue parsed = TryParseValue(raw);
            ResolvePosition(parsed, boxW, boxH, tileW, tileH, ctx, style, out originX, out originY);
        }

        // Typed entry point. Position is 1-2 length-or-percent-or-keyword
        // values per layer; the parser produces a space-separated CssValueList
        // for the two-token form, a bare CssValue otherwise.
        public static void ResolvePosition(
            CssValue parsed, double boxW, double boxH,
            double tileW, double tileH,
            LengthContext ctx, ComputedStyle style,
            out double originX, out double originY
        ) {
            // CSS positions are computed against (box - tile) — the maximum
            // offset that keeps the tile inside the container. A 0%
            // position puts the tile at the box's origin; 100% flush right.
            originX = 0;
            originY = 0;
            if (parsed == null) return;

            CssValue xToken = parsed;
            CssValue yToken = null;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space && list.Items.Count >= 1) {
                // CSS Backgrounds 3 §3.7 edge-offset form (3-4 tokens):
                //   <edge-x> <offset>? || <edge-y> <offset>? — each axis is
                //   an edge keyword optionally followed by a length/percent;
                //   the offset is measured FROM the named edge (so `right N`
                //   anchors N px in from the right side, not N px from the
                //   left). Axes can appear in either order. Routed before
                //   the 1/2-token paths so the keyword-+-offset pairs aren't
                //   silently truncated to the first two tokens.
                if (list.Items.Count >= 3 && list.Items.Count <= 4
                    && TryResolveEdgeOffset(list.Items, boxW, boxH, tileW, tileH, ctx, out originX, out originY)) {
                    return;
                }
                xToken = list.Items[0];
                if (list.Items.Count >= 2) yToken = list.Items[1];
            }
            // CSS Backgrounds 3 §3.7 disambiguation rules:
            //   1 token  → apply to its named axis (vertical keyword to Y,
            //              horizontal keyword to X); other axis = center.
            //   2 tokens → (xToken, yToken), with swap when first is vertical
            //              keyword and second is horizontal keyword
            //              ("top left" → x=left, y=top).
            // The 1-token case previously left a vertical keyword in the X slot:
            // `background-position: top` resolved as x=0, y=center → top-left
            // instead of top-center.
            if (yToken == null && IsVerticalKeyword(xToken)) {
                yToken = xToken;
                xToken = null;
            } else if (IsVerticalKeyword(xToken) && IsHorizontalKeyword(yToken)) {
                var swap = xToken; xToken = yToken; yToken = swap;
            }

            double maxX = boxW - tileW;
            double maxY = boxH - tileH;
            originX = ResolvePositionSingle(xToken, true, maxX, boxW, ctx);
            originY = ResolvePositionSingle(yToken, false, maxY, boxH, ctx);
        }

        // CSS Backgrounds 3 §3.7 edge-offset form. Walks the 3-4 token list
        // as pairs of (edge-keyword, optional offset) and folds each pair
        // into the axis the keyword names. `right`/`bottom` invert the
        // offset (measured FROM the far edge); `left`/`top` use it directly;
        // `center` ignores the offset slot since it has no edge to anchor to.
        // Returns false when the shape doesn't fit the grammar so the caller
        // can fall back to the 1/2-token paths.
        static bool TryResolveEdgeOffset(
            System.Collections.Generic.IReadOnlyList<CssValue> items,
            double boxW, double boxH, double tileW, double tileH, LengthContext ctx,
            out double originX, out double originY
        ) {
            originX = 0; originY = 0;
            double maxX = boxW - tileW;
            double maxY = boxH - tileH;
            bool xSet = false, ySet = false;
            int i = 0;
            while (i < items.Count) {
                CssValue edge = items[i];
                string kw = TryGetKeyword(edge);
                if (kw == null) return false;
                bool horizontal = IsHorizontalKeyword(edge);
                bool vertical = IsVerticalKeyword(edge);
                bool center = CssStringUtil.EqualsIgnoreCaseTrimmed(kw, "center");
                if (!horizontal && !vertical && !center) return false;

                CssValue offset = null;
                if (i + 1 < items.Count && !IsAnyPositionKeyword(items[i + 1])) {
                    offset = items[i + 1];
                    i += 2;
                } else {
                    i += 1;
                }

                // `center` has no edge to anchor an offset to; per spec the
                // offset slot is disallowed there. Reject so callers fall
                // back to the 2-token path rather than silently consuming
                // the next number.
                if (center && offset != null) return false;

                double axisOrigin;
                bool isHorizontalAxis;
                if (horizontal) {
                    isHorizontalAxis = true;
                    bool isFarEdge = CssStringUtil.EqualsIgnoreCaseTrimmed(kw, "right");
                    double offsetPx = ResolveOffsetLength(offset, maxX, ctx);
                    axisOrigin = isFarEdge ? maxX - offsetPx : offsetPx;
                } else if (vertical) {
                    isHorizontalAxis = false;
                    bool isFarEdge = CssStringUtil.EqualsIgnoreCaseTrimmed(kw, "bottom");
                    double offsetPx = ResolveOffsetLength(offset, maxY, ctx);
                    axisOrigin = isFarEdge ? maxY - offsetPx : offsetPx;
                } else {
                    // center — assign to whichever axis is still open.
                    if (!xSet) { isHorizontalAxis = true; axisOrigin = maxX * 0.5; }
                    else if (!ySet) { isHorizontalAxis = false; axisOrigin = maxY * 0.5; }
                    else return false;
                }

                if (isHorizontalAxis) {
                    if (xSet) return false;
                    originX = axisOrigin; xSet = true;
                } else {
                    if (ySet) return false;
                    originY = axisOrigin; ySet = true;
                }
            }
            if (!xSet) originX = maxX * 0.5;
            if (!ySet) originY = maxY * 0.5;
            return xSet || ySet;
        }

        static bool IsAnyPositionKeyword(CssValue v) {
            string k = TryGetKeyword(v);
            if (k == null) return false;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(k, "left")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(k, "right")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(k, "top")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(k, "bottom")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(k, "center");
        }

        static double ResolveOffsetLength(CssValue v, double rangeBase, LengthContext ctx) {
            if (v == null) return 0;
            if (v is CssPercentage p) return rangeBase * p.Value * 0.01;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber n) return n.Value;
            if (v is CssCalc calc) {
                try {
                    var c = ctx;
                    c.BasisPixels = rangeBase;
                    return calc.Evaluate(c);
                } catch { return 0; }
            }
            return 0;
        }

        static double ResolvePositionSingle(CssValue v, bool horizontal, double rangeBase, double boxAxis, LengthContext ctx) {
            if (v == null) {
                // Missing token on either axis defaults to "center" per
                // CSS Backgrounds 3 §3.7. The prior `horizontal ? 0 : ...`
                // branch dead-coded the horizontal case (xToken was never
                // null pre-fix) and would have produced left-alignment if
                // ever reached.
                return rangeBase * 0.5;
            }
            string keyword = TryGetKeyword(v);
            if (keyword != null) {
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "left")) return 0;
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "top")) return 0;
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "right")) return horizontal ? rangeBase : 0;
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "bottom")) return horizontal ? 0 : rangeBase;
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "center")) return rangeBase * 0.5;
                return 0;
            }
            if (v is CssPercentage p) return rangeBase * p.Value * 0.01;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber n) return n.Value;
            if (v is CssCalc calc) {
                try {
                    var c = ctx;
                    c.BasisPixels = rangeBase;
                    return calc.Evaluate(c);
                } catch { return 0; }
            }
            return 0;
        }

        static bool IsHorizontalKeyword(CssValue v) {
            string k = TryGetKeyword(v);
            return k != null && (CssStringUtil.EqualsIgnoreCaseTrimmed(k, "left")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(k, "right"));
        }
        static bool IsVerticalKeyword(CssValue v) {
            string k = TryGetKeyword(v);
            return k != null && (CssStringUtil.EqualsIgnoreCaseTrimmed(k, "top")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(k, "bottom"));
        }

        static string TryGetKeyword(CssValue v) {
            if (v is CssKeyword k) return k.Identifier;
            if (v is CssIdentifier id) return id.Name;
            return null;
        }

        static bool IsKeyword(CssValue v, string name) {
            string k = TryGetKeyword(v);
            return k != null && CssStringUtil.EqualsIgnoreCaseTrimmed(k, name);
        }

        // ---------- repeat ----------

        public static void ResolveRepeat(string raw, out BackgroundRepeatMode repeatX, out BackgroundRepeatMode repeatY) {
            CssValue parsed = TryParseValue(raw);
            ResolveRepeat(parsed, out repeatX, out repeatY);
        }

        // Typed entry point. Single-keyword shorthands per CSS Backgrounds 3
        // §3.6: `no-repeat`, `repeat-x`, `repeat-y`, `repeat`, `space`,
        // `round`. Two-value form: `<x-keyword> <y-keyword>`.
        public static void ResolveRepeat(CssValue parsed, out BackgroundRepeatMode repeatX, out BackgroundRepeatMode repeatY) {
            repeatX = BackgroundRepeatMode.Repeat;
            repeatY = BackgroundRepeatMode.Repeat;
            if (parsed == null) return;

            // Two-value form first.
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space && list.Items.Count >= 2) {
                repeatX = ParseRepeatKeyword(list.Items[0]);
                repeatY = ParseRepeatKeyword(list.Items[1]);
                return;
            }
            string keyword = TryGetKeyword(parsed);
            if (keyword == null) return;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "no-repeat")) {
                repeatX = BackgroundRepeatMode.NoRepeat;
                repeatY = BackgroundRepeatMode.NoRepeat;
                return;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "repeat-x")) {
                repeatX = BackgroundRepeatMode.Repeat;
                repeatY = BackgroundRepeatMode.NoRepeat;
                return;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "repeat-y")) {
                repeatX = BackgroundRepeatMode.NoRepeat;
                repeatY = BackgroundRepeatMode.Repeat;
                return;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "repeat")) {
                repeatX = BackgroundRepeatMode.Repeat;
                repeatY = BackgroundRepeatMode.Repeat;
                return;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "space")) {
                repeatX = BackgroundRepeatMode.Space;
                repeatY = BackgroundRepeatMode.Space;
                return;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(keyword, "round")) {
                repeatX = BackgroundRepeatMode.Round;
                repeatY = BackgroundRepeatMode.Round;
                return;
            }
        }

        static BackgroundRepeatMode ParseRepeatKeyword(CssValue v) {
            string k = TryGetKeyword(v);
            if (k == null) return BackgroundRepeatMode.Repeat;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(k, "no-repeat")) return BackgroundRepeatMode.NoRepeat;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(k, "space")) return BackgroundRepeatMode.Space;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(k, "round")) return BackgroundRepeatMode.Round;
            return BackgroundRepeatMode.Repeat;
        }
    }
}
