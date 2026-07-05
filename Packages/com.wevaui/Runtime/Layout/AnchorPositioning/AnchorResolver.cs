using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout.AnchorPositioning {
    // AnchorResolver — converts an anchor() function call plus a registry into
    // an absolute pixel value for a top/right/bottom/left offset.
    //
    // Algorithm:
    //   1. Look up the anchor box by name from the registry. If absent the
    //      result is 0 (per the brief; the spec says `auto`, which our
    //      Offsets layer treats as null — 0 is acceptable v1).
    //   2. Compute the anchor box's absolute rect via ContainingBlockResolver.
    //   3. Translate the requested edge to an absolute pixel value:
    //        top    -> rect.Y
    //        bottom -> rect.Y + rect.Height
    //        left   -> rect.X
    //        right  -> rect.X + rect.Width
    //        center -> midpoint of the relevant axis
    //        start/end map to left/right (LTR-only v1)
    //   4. Convert from absolute to value-relative-to-containing-block by
    //      subtracting the positioned-anchored box's containing-block origin.
    //   5. Add the parsed offset.
    public static class AnchorResolver {
        public static bool TryResolveSide(string side, string raw, AnchorRegistry registry,
                                          string fallbackAnchorName, Box positionedBox,
                                          LayoutContext ctx, out double pixels) {
            pixels = 0;
            if (registry == null || positionedBox == null) return false;
            if (!AnchorFunctionParser.TryParse(raw, out var call)) return false;
            string name = call.AnchorName ?? fallbackAnchorName;
            if (string.IsNullOrEmpty(name)) {
                // No anchor name → 0 (per v1 simplification).
                pixels = call.OffsetPx;
                return true;
            }
            if (!registry.TryResolve(name, out var entry) || entry.Anchor == null) {
                pixels = call.OffsetPx;
                return true;
            }
            var (anchorAbsX, anchorAbsY) = ContainingBlockResolver.AbsolutePosition(entry.Anchor);
            double absEdge = EdgeAbsolute(side, call.Edge, anchorAbsX, anchorAbsY,
                                          entry.Anchor.Width, entry.Anchor.Height);

            // Convert absolute pixel to relative-to-containing-block. We need the
            // CB origin of `positionedBox`. PositioningPass uses
            // ContainingBlockResolver.ResolveAbsolute for absolute boxes, the
            // viewport for fixed boxes, and the parent for relative/sticky.
            ContainingBlockResolver.ContainingBlock cb;
            switch (positionedBox.Position) {
                case PositionType.Fixed:
                    cb = ContainingBlockResolver.ResolveFixed(positionedBox, ctx);
                    break;
                case PositionType.Absolute:
                    cb = ContainingBlockResolver.ResolveAbsolute(positionedBox, ctx);
                    break;
                default: {
                    var (px, py) = ContainingBlockResolver.AbsolutePositionOfParent(positionedBox);
                    cb = new ContainingBlockResolver.ContainingBlock(positionedBox.Parent,
                                                                      px, py,
                                                                      positionedBox.Parent?.Width ?? ctx.ViewportWidthPx,
                                                                      positionedBox.Parent?.Height ?? ctx.ViewportHeightPx,
                                                                      false);
                    break;
                }
            }

            switch (side) {
                case "top": pixels = absEdge - cb.Y + call.OffsetPx; return true;
                case "bottom": pixels = (cb.Y + cb.Height) - absEdge + call.OffsetPx; return true;
                case "left": pixels = absEdge - cb.X + call.OffsetPx; return true;
                case "right": pixels = (cb.X + cb.Width) - absEdge + call.OffsetPx; return true;
            }
            return false;
        }

        // Resolves anchor-size(<name>? <axis>?) to a pixel size of the anchor
        // box. Used by width/height/min-/max- resolution. Returns false if the
        // function fails to parse, or if neither an explicit name nor a fallback
        // (position-anchor) is available; returns true with `pixels = 0` when
        // the named anchor cannot be resolved (matches the v1 anchor() behavior).
        // `propertyName` lets the caller hint which axis to use when the
        // function omits an explicit axis ("width"/"min-width"/"max-width" →
        // width; "height"/"min-height"/"max-height" → height).
        public static bool TryResolveSize(string propertyName, string raw,
                                          AnchorRegistry registry, string fallbackAnchorName,
                                          out double pixels) {
            pixels = 0;
            if (registry == null) return false;
            if (!AnchorFunctionParser.TryParseSize(raw, out var call)) return false;
            string name = call.AnchorName ?? fallbackAnchorName;
            if (string.IsNullOrEmpty(name)) {
                pixels = 0;
                return true;
            }
            if (!registry.TryResolve(name, out var entry) || entry.Anchor == null) {
                pixels = 0;
                return true;
            }
            var axis = call.Axis;
            if (axis == AnchorSizeAxis.Inferred) {
                axis = AxisForProperty(propertyName);
            }
            pixels = axis == AnchorSizeAxis.Height ? entry.Anchor.Height : entry.Anchor.Width;
            return true;
        }

        static AnchorSizeAxis AxisForProperty(string property) {
            if (string.IsNullOrEmpty(property)) return AnchorSizeAxis.Width;
            switch (property) {
                case "height":
                case "min-height":
                case "max-height":
                    return AnchorSizeAxis.Height;
                default:
                    return AnchorSizeAxis.Width;
            }
        }

        // Returns the absolute (root-relative) pixel value of the requested
        // edge of an anchor rect. `side` is the offset property being computed
        // (top/right/bottom/left) — used to disambiguate axis for `center`,
        // `start`, `end`.
        public static double EdgeAbsolute(string side, AnchorEdge edge,
                                          double rectX, double rectY,
                                          double rectW, double rectH) {
            bool isVertical = side == "top" || side == "bottom";
            switch (edge) {
                case AnchorEdge.Top: return rectY;
                case AnchorEdge.Bottom: return rectY + rectH;
                case AnchorEdge.Left: return rectX;
                case AnchorEdge.Right: return rectX + rectW;
                case AnchorEdge.Start:
                case AnchorEdge.SelfStart:
                    return isVertical ? rectY : rectX;
                case AnchorEdge.End:
                case AnchorEdge.SelfEnd:
                    return isVertical ? rectY + rectH : rectX + rectW;
                case AnchorEdge.Center:
                    return isVertical ? rectY + rectH * 0.5 : rectX + rectW * 0.5;
            }
            return 0;
        }
    }
}
