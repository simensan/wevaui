using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout.AnchorPositioning {
    // PositionTryFallbacks — implements the v2 `position-try-fallbacks` (a.k.a.
    // `position-try-options` in earlier drafts) keyword set:
    //   flip-block:  swap top<->bottom edges (vertical axis)
    //   flip-inline: swap left<->right edges (horizontal axis)
    //   flip-block flip-inline: both
    //
    // CSS spec: when an anchor-positioned box would overflow the viewport, the
    // browser tries the listed fallbacks in order and applies the first that
    // fits. We approximate "fits" with a strict "fully inside the viewport"
    // test against the box's offset+size after the fallback rewrites top/right/
    // bottom/left.
    //
    // v1 simplifications:
    //   - Only the keyword fallbacks listed above; no `position-try-options`
    //     custom @position-try blocks.
    //   - "Inside" is computed against the viewport rect; we don't honor
    //     scroll containers (the spec's `most-recent-relevant-anchor` scope).
    //   - The check happens after PositioningPass has applied the original
    //     position. If no fallback fits, the original position is retained.
    public static class PositionTryFallbacks {
        public enum Strategy {
            None,
            FlipBlock,
            FlipInline,
            FlipBlockInline
        }

        public static List<Strategy> Parse(string raw) {
            var list = new List<Strategy>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var token in SplitTopLevel(raw, ',')) {
                var s = ParseSingle(token.Trim());
                if (s != Strategy.None) list.Add(s);
            }
            return list;
        }

        // Shared so each ParseSingle call doesn't allocate a fresh delimiter array.
        static readonly char[] s_TokenSeparators = { ' ', '\t' };

        public static Strategy ParseSingle(string raw) {
            if (string.IsNullOrEmpty(raw)) return Strategy.None;
            var parts = raw.Split(s_TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            bool flipB = false, flipI = false;
            for (int i = 0; i < parts.Length; i++) {
                switch (CssStringUtil.ToLowerInvariantOrSame(parts[i])) {
                    case "flip-block": flipB = true; break;
                    case "flip-inline": flipI = true; break;
                    default: return Strategy.None;
                }
            }
            if (flipB && flipI) return Strategy.FlipBlockInline;
            if (flipB) return Strategy.FlipBlock;
            if (flipI) return Strategy.FlipInline;
            return Strategy.None;
        }

        // Applies fallbacks to a positioned box. Returns the strategy that won
        // (Strategy.None when the original fits, no list is set, or none fit).
        public static Strategy Apply(Box positionedBox, LayoutContext ctx, IList<Strategy> fallbacks) {
            if (positionedBox == null || ctx == null || fallbacks == null || fallbacks.Count == 0) {
                return Strategy.None;
            }
            if (FitsViewport(positionedBox, ctx)) return Strategy.None;
            // Snapshot the original side raws + applied X/Y so we can revert on
            // a no-fit outcome without re-running the entire positioning pipeline.
            //
            // Per-style parsed-cache migration: the four side slots are read via
            // GetParsed(int id) — the parse tree is already in hand from the
            // cascade and we only need the .Raw form for the downstream
            // anchor(...) substring rewrites. Falling back to style.Get(id)
            // when GetParsed returns null preserves the legacy auto/initial
            // path (an unset slot still serialises to null and SetSide will
            // write "auto" on restore).
            var style = positionedBox.Style;
            if (style == null) return Strategy.None;
            string origTop = ReadSideRaw(style, CssProperties.TopId);
            string origRight = ReadSideRaw(style, CssProperties.RightId);
            string origBottom = ReadSideRaw(style, CssProperties.BottomId);
            string origLeft = ReadSideRaw(style, CssProperties.LeftId);
            double origX = positionedBox.X;
            double origY = positionedBox.Y;
            // Snapshot the offset fields too so a no-fit outcome rebuilds the
            // exact original placement.
            var origOT = positionedBox.OffsetTop;
            var origOR = positionedBox.OffsetRight;
            var origOB = positionedBox.OffsetBottom;
            var origOL = positionedBox.OffsetLeft;
            for (int i = 0; i < fallbacks.Count; i++) {
                var s = fallbacks[i];
                ApplyStrategy(style, s, origTop, origRight, origBottom, origLeft);
                RecomputePosition(positionedBox, ctx);
                if (FitsViewport(positionedBox, ctx)) return s;
            }
            // No fallback fit — restore the original.
            RestoreSides(style, origTop, origRight, origBottom, origLeft);
            positionedBox.X = origX;
            positionedBox.Y = origY;
            positionedBox.OffsetTop = origOT;
            positionedBox.OffsetRight = origOR;
            positionedBox.OffsetBottom = origOB;
            positionedBox.OffsetLeft = origOL;
            return Strategy.None;
        }

        static void ApplyStrategy(Weva.Css.Cascade.ComputedStyle style, Strategy s,
                                  string top, string right, string bottom, string left) {
            // Swapping the property values alone (e.g. moving the original `left`
            // raw onto `right`) is not enough: an `anchor(<edge>)` call inside
            // that raw still references its original anchor edge. The CSS spec
            // for flip-block / flip-inline also flips anchor-edge keywords on
            // the relevant axis, so `anchor(right)` on the original `left`
            // becomes `anchor(left)` on the swapped `right`. Without this,
            // flip-inline of `left: anchor(right)` would yield `right:
            // anchor(right)` and silently leave the box overflowing.
            bool flipBlock = s == Strategy.FlipBlock || s == Strategy.FlipBlockInline;
            bool flipInline = s == Strategy.FlipInline || s == Strategy.FlipBlockInline;
            string nt = FlipAnchorEdges(top, flipBlock, flipInline);
            string nr = FlipAnchorEdges(right, flipBlock, flipInline);
            string nb = FlipAnchorEdges(bottom, flipBlock, flipInline);
            string nl = FlipAnchorEdges(left, flipBlock, flipInline);
            switch (s) {
                case Strategy.FlipBlock:
                    SetSide(style, "top", nb);
                    SetSide(style, "bottom", nt);
                    SetSide(style, "left", nl);
                    SetSide(style, "right", nr);
                    break;
                case Strategy.FlipInline:
                    SetSide(style, "top", nt);
                    SetSide(style, "bottom", nb);
                    SetSide(style, "left", nr);
                    SetSide(style, "right", nl);
                    break;
                case Strategy.FlipBlockInline:
                    SetSide(style, "top", nb);
                    SetSide(style, "bottom", nt);
                    SetSide(style, "left", nr);
                    SetSide(style, "right", nl);
                    break;
            }
        }

        // Rewrites any `anchor(<edge> ...)` token in `raw` so that vertical
        // edges (top/bottom) swap when flipBlock is set and horizontal edges
        // (left/right) swap when flipInline is set. start/end/self-start/
        // self-end/center are LTR-equivalent to the physical edges in v1 and
        // get the same treatment. Non-anchor() values pass through unchanged.
        static string FlipAnchorEdges(string raw, bool flipBlock, bool flipInline) {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (!flipBlock && !flipInline) return raw;
            int idx = raw.IndexOf("anchor(", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return raw;
            int open = idx + "anchor(".Length;
            int close = raw.IndexOf(')', open);
            if (close < 0) return raw;
            string inner = raw.Substring(open, close - open);
            string flipped = FlipEdgeTokensInInner(inner, flipBlock, flipInline);
            if (flipped == inner) return raw;
            return raw.Substring(0, open) + flipped + raw.Substring(close);
        }

        static string FlipEdgeTokensInInner(string inner, bool flipBlock, bool flipInline) {
            // Inner is "<name>? <edge> [+/- <offset>]?". We only need to flip the
            // edge token; the name (starts with --) and offset trailer are left
            // alone. Walk space-delimited parts and rewrite the first edge match.
            var sb = new System.Text.StringBuilder(inner.Length);
            int i = 0;
            bool rewroteEdge = false;
            while (i < inner.Length) {
                while (i < inner.Length && (inner[i] == ' ' || inner[i] == '\t')) {
                    sb.Append(inner[i]); i++;
                }
                int start = i;
                while (i < inner.Length && inner[i] != ' ' && inner[i] != '\t') i++;
                if (i == start) break;
                string tok = inner.Substring(start, i - start);
                if (!rewroteEdge) {
                    string lower = CssStringUtil.ToLowerInvariantOrSame(tok);
                    string swapped = SwapEdge(lower, flipBlock, flipInline);
                    if (swapped != null) {
                        sb.Append(swapped);
                        rewroteEdge = true;
                        continue;
                    }
                }
                sb.Append(tok);
            }
            return sb.ToString();
        }

        static string SwapEdge(string edge, bool flipBlock, bool flipInline) {
            if (flipBlock) {
                if (edge == "top") return "bottom";
                if (edge == "bottom") return "top";
            }
            if (flipInline) {
                if (edge == "left") return "right";
                if (edge == "right") return "left";
                if (edge == "start") return "end";
                if (edge == "end") return "start";
                if (edge == "self-start") return "self-end";
                if (edge == "self-end") return "self-start";
            }
            return null;
        }

        static void RestoreSides(Weva.Css.Cascade.ComputedStyle style,
                                 string top, string right, string bottom, string left) {
            SetSide(style, "top", top);
            SetSide(style, "right", right);
            SetSide(style, "bottom", bottom);
            SetSide(style, "left", left);
        }

        static void SetSide(Weva.Css.Cascade.ComputedStyle style, string prop, string value) {
            style.Set(prop, value ?? "auto");
        }

        // Reads a side property via the per-style parsed cache and returns the
        // .Raw form so the existing string-based FlipAnchorEdges / Set(prop,
        // raw) pipeline can be reused. The parsed value is typically a
        // CssFunctionCall("anchor", …), CssLength, CssPercentage, CssCalc, or
        // CssKeyword "auto". For any cached parse tree we return parsed.Raw —
        // CssValue.Raw is the round-trip form of the original declaration.
        // Falls back to style.Get(id) when GetParsed yields null (slot unset
        // or parse failed) so unparseable values still flow through unchanged.
        static string ReadSideRaw(Weva.Css.Cascade.ComputedStyle style, int propertyId) {
            var parsed = style.GetParsed(propertyId);
            if (parsed != null) return parsed.Raw ?? style.Get(propertyId);
            return style.Get(propertyId);
        }

        // Re-resolve offsets and re-apply position for a single box. Mirrors a
        // narrowed slice of PositioningPass.ApplyAbsolute / ApplyFixed without
        // touching the registry or stacking-context logic.
        static void RecomputePosition(Box box, LayoutContext ctx) {
            var style = box.Style;
            if (style == null) return;
            double cbW, cbH;
            ContainingBlockResolver.ContainingBlock cb;
            switch (box.Position) {
                case PositionType.Fixed:
                    cb = ContainingBlockResolver.ResolveFixed(box, ctx);
                    cbW = cb.Width; cbH = cb.Height;
                    break;
                case PositionType.Absolute:
                    cb = ContainingBlockResolver.ResolveAbsolute(box, ctx);
                    cbW = cb.Width; cbH = cb.Height;
                    break;
                default:
                    return;
            }
            var off = box.ReadOffsets(ctx, cbW, cbH);
            box.OffsetTop = off.Top;
            box.OffsetRight = off.Right;
            box.OffsetBottom = off.Bottom;
            box.OffsetLeft = off.Left;

            double absX, absY;
            if (box.OffsetLeft.HasValue) {
                absX = cb.X + box.OffsetLeft.Value;
            } else if (box.OffsetRight.HasValue) {
                absX = cb.X + cb.Width - box.OffsetRight.Value - box.Width;
            } else {
                var (parentAbsX0, _) = ContainingBlockResolver.AbsolutePositionOfParent(box);
                absX = parentAbsX0 + box.X;
            }
            if (box.OffsetTop.HasValue) {
                absY = cb.Y + box.OffsetTop.Value;
            } else if (box.OffsetBottom.HasValue) {
                absY = cb.Y + cb.Height - box.OffsetBottom.Value - box.Height;
            } else {
                var (_, parentAbsY0) = ContainingBlockResolver.AbsolutePositionOfParent(box);
                absY = parentAbsY0 + box.Y;
            }
            var (parentAbsX, parentAbsY) = ContainingBlockResolver.AbsolutePositionOfParent(box);
            box.X = absX - parentAbsX;
            box.Y = absY - parentAbsY;
        }

        public static bool FitsViewport(Box box, LayoutContext ctx) {
            double absX = 0, absY = 0;
            for (var p = box; p != null; p = p.Parent) {
                absX += p.X;
                absY += p.Y;
            }
            if (absX < 0) return false;
            if (absY < 0) return false;
            if (absX + box.Width > ctx.ViewportWidthPx) return false;
            if (absY + box.Height > ctx.ViewportHeightPx) return false;
            return true;
        }

        static IEnumerable<string> SplitTopLevel(string s, char sep) {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == sep && depth == 0) {
                    yield return s.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (start < s.Length) yield return s.Substring(start);
        }
    }
}
