using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Layout.Positioning {
    public static class PositionedExtensions {
        public static PositionType ReadPositionType(this Box box) {
            return ParsePositionType(box?.Style?.Get(CssProperties.PositionId));
        }

        public static PositionType ParsePositionType(string raw) {
            if (string.IsNullOrEmpty(raw)) return PositionType.Static;
            switch (CssStringUtil.ToLowerInvariantOrSame(raw.Trim())) {
                case "relative": return PositionType.Relative;
                case "absolute": return PositionType.Absolute;
                case "fixed": return PositionType.Fixed;
                case "sticky": return PositionType.Sticky;
                case "static": return PositionType.Static;
                default: return PositionType.Static;
            }
        }

        public static int? ReadZIndex(this Box box) {
            return ParseZIndex(box?.Style?.Get(CssProperties.ZIndexId));
        }

        public static int? ParseZIndex(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            if (s == "auto") return null;
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssNumber n) {
                    return ClampToInt(n.Value);
                }
                if (v is CssKeyword k && k.Identifier == "auto") return null;
            }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) {
                return parsed;
            }
            return null;
        }

        public static Offsets ReadOffsets(this Box box, LayoutContext ctx, double containingBlockWidth, double containingBlockHeight) {
            if (box?.Style == null) return Offsets.AllAuto;
            double fs = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
            string positionAnchor = box.Style.Get(CssProperties.PositionAnchorId);
            double? top = ResolveSide("top", box.Style.Get(CssProperties.TopId), box, ctx, fs, containingBlockHeight, positionAnchor);
            double? right = ResolveSide("right", box.Style.Get(CssProperties.RightId), box, ctx, fs, containingBlockWidth, positionAnchor);
            double? bottom = ResolveSide("bottom", box.Style.Get(CssProperties.BottomId), box, ctx, fs, containingBlockHeight, positionAnchor);
            double? left = ResolveSide("left", box.Style.Get(CssProperties.LeftId), box, ctx, fs, containingBlockWidth, positionAnchor);
            return new Offsets(top, right, bottom, left);
        }

        static double? ResolveSide(string sideName, string raw, Box box, LayoutContext ctx,
                                   double fontSize, double basisPx, string positionAnchor) {
            if (string.IsNullOrEmpty(raw) || raw == "auto") return null;
            // Intercept `anchor(...)` function before falling through to the
            // length parser. The CSS Anchor Positioning module overlays this
            // at the value level — only valid for top/right/bottom/left.
            if (raw.TrimStart().StartsWith("anchor(", System.StringComparison.OrdinalIgnoreCase)) {
                if (Weva.Layout.AnchorPositioning.AnchorResolver.TryResolveSide(
                        sideName, raw, ctx?.Anchors, positionAnchor, box, ctx, out double px)) {
                    return px;
                }
                return null;
            }
            var r = StyleResolver.ResolveLength(raw, box.Style, ctx, fontSize, basisPx);
            switch (r.Kind) {
                case StyleResolver.LengthKind.Length: return r.Pixels;
                case StyleResolver.LengthKind.Percent: return r.Percent * 0.01 * basisPx;
                case StyleResolver.LengthKind.Auto:
                case StyleResolver.LengthKind.None:
                default:
                    return null;
            }
        }

        public static bool IsPositioned(this Box box) {
            var p = box.Position;
            return p == PositionType.Relative || p == PositionType.Absolute || p == PositionType.Fixed || p == PositionType.Sticky;
        }

        public static bool CreatesStackingContext(this Box box) {
            if (box?.Style == null) return false;
            var p = box.Position;
            if (p == PositionType.Fixed || p == PositionType.Sticky) return true;
            if ((p == PositionType.Relative || p == PositionType.Absolute) && box.ZIndex.HasValue) return true;
            string opacity = box.Style.Get(CssProperties.OpacityId);
            if (!string.IsNullOrEmpty(opacity)) {
                if (CssValue.TryParse(opacity, out var ov)) {
                    if (ov is CssNumber on && on.Value < 1.0) return true;
                    if (ov is CssPercentage op && op.Value < 100.0) return true;
                }
            }
            string transform = box.Style.Get(CssProperties.TransformId);
            // Allocation-free case-insensitive compare via CssStringUtil —
            // was `.Trim().ToLowerInvariant() != "none"` which allocated two
            // fresh strings per probe (~25 B / call × N stacking-context
            // probes / frame).
            if (!string.IsNullOrEmpty(transform) && !CssStringUtil.EqualsIgnoreCaseTrimmed(transform, "none")) return true;
            string isolation = box.Style.Get("isolation");
            if (!string.IsNullOrEmpty(isolation) && CssStringUtil.EqualsIgnoreCaseTrimmed(isolation, "isolate")) return true;
            // CSS Filter Effects 1 §2.1: any filter value other than `none`
            // creates a stacking context (and a containing block for fixed
            // descendants). Spec-required even though our paint pipeline may
            // not yet rasterise filters — the box-tree topology must match.
            string filter = box.Style.Get("filter");
            if (!string.IsNullOrEmpty(filter) && !CssStringUtil.EqualsIgnoreCaseTrimmed(filter, "none")) return true;
            string backdropFilter = box.Style.Get("backdrop-filter");
            if (!string.IsNullOrEmpty(backdropFilter) && !CssStringUtil.EqualsIgnoreCaseTrimmed(backdropFilter, "none")) return true;
            // CSS Compositing 1 §5.1: any non-`normal` `mix-blend-mode` creates
            // a stacking context. Required even though the paint pipeline does
            // not yet composite blend modes — the box-tree topology must match
            // spec so descendants group correctly.
            string mixBlendMode = box.Style.Get("mix-blend-mode");
            if (!string.IsNullOrEmpty(mixBlendMode) && !CssStringUtil.EqualsIgnoreCaseTrimmed(mixBlendMode, "normal")) return true;
            // CSS Will Change 1 §3: `will-change` creates a stacking context
            // when one of its tokens names a property that itself would
            // create one. The token set must mirror the SC-creating
            // properties this engine actually promotes above — adding a
            // token whose property the engine ignores would manufacture
            // phantom contexts. HasTokenIgnoreCase scans the original
            // string without lowercasing it.
            string willChange = box.Style.Get("will-change");
            if (!string.IsNullOrEmpty(willChange)) {
                if (HasTokenIgnoreCase(willChange, "transform")
                    || HasTokenIgnoreCase(willChange, "opacity")
                    || HasTokenIgnoreCase(willChange, "filter")
                    || HasTokenIgnoreCase(willChange, "backdrop-filter")
                    || HasTokenIgnoreCase(willChange, "isolation")
                    || HasTokenIgnoreCase(willChange, "contain")
                    || HasTokenIgnoreCase(willChange, "position")
                    || HasTokenIgnoreCase(willChange, "z-index")) return true;
            }
            // CSS Containment 2 §3: `contain: layout`, `contain: paint`,
            // `contain: strict` (= layout + paint + size + style), and
            // `contain: content` (= layout + paint + style) all establish
            // a stacking context on the contained element.
            string contain = box.Style.Get("contain");
            if (!string.IsNullOrEmpty(contain)) {
                if (HasTokenIgnoreCase(contain, "layout") || HasTokenIgnoreCase(contain, "paint")
                    || HasTokenIgnoreCase(contain, "strict") || HasTokenIgnoreCase(contain, "content")) return true;
            }
            return false;
        }

        // Whitespace-token match without lowercasing the input. `will-change:
        // transform, opacity` and `contain: layout paint` are both valid —
        // the spec allows commas or whitespace as separators. We split on
        // either and compare each token ordinal-ignore-case against the
        // target.
        static bool HasTokenIgnoreCase(string value, string token) {
            int idx = 0;
            while (idx < value.Length) {
                while (idx < value.Length && (value[idx] == ' ' || value[idx] == ',' || value[idx] == '\t')) idx++;
                int start = idx;
                while (idx < value.Length && value[idx] != ' ' && value[idx] != ',' && value[idx] != '\t') idx++;
                int len = idx - start;
                if (len != token.Length) continue;
                bool eq = true;
                for (int j = 0; j < len; j++) {
                    char a = value[start + j];
                    if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                    if (a != token[j]) { eq = false; break; }
                }
                if (eq) return true;
            }
            return false;
        }

        static int ClampToInt(double v) {
            if (v >= int.MaxValue) return int.MaxValue;
            if (v <= int.MinValue) return int.MinValue;
            return (int)v;
        }
    }
}
