using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Scrolling.Snap {
    public readonly struct SnapPoint {
        public readonly double Position;
        public readonly Box Source;
        public readonly SnapStop Stop;

        public SnapPoint(double position, Box source, SnapStop stop) {
            Position = position;
            Source = source;
            Stop = stop;
        }
    }

    public sealed class SnapResolver {
        readonly ScrollContainer container;

        public SnapResolver(ScrollContainer container) {
            this.container = container;
        }

        // CSS Scroll Snap Module Level 1, §6 — for each snap-area child the
        // browser computes a snap position by aligning that area with the
        // alignment edge of the snapport (the container's viewport reduced by
        // scroll-padding). For start: child.top - scroll-padding-top;
        // for end: child.bottom - viewport.height + scroll-padding-bottom;
        // for center: child.middle - viewport.middle.
        public List<SnapPoint> CollectSnapPointsY(Box scrollContainer) {
            var result = new List<SnapPoint>();
            if (scrollContainer?.Style == null) return result;
            var state = container?.Get(scrollContainer);
            if (state == null) return result;

            double padTop = ResolveLonghandOrShorthand(scrollContainer.Style, "scroll-padding-top", "scroll-padding");
            double padBottom = ResolveLonghandOrShorthand(scrollContainer.Style, "scroll-padding-bottom", "scroll-padding");

            double viewportH = state.ViewportHeight;

            CollectDescendants(scrollContainer, (child, localY, _) => {
                if (child.Style == null) return;
                var align = SnapParser.ParseAlign(child.Style.Get("scroll-snap-align"));
                if (!align.IsActive) return;
                var stop = SnapParser.ParseStop(child.Style.Get("scroll-snap-stop"));

                double childTop = localY;
                double childBottom = childTop + child.Height;

                // scroll-margin extends the snap area
                double mTop = ResolveLonghandOrShorthand(child.Style, "scroll-margin-top", "scroll-margin");
                double mBottom = ResolveLonghandOrShorthand(child.Style, "scroll-margin-bottom", "scroll-margin");
                childTop -= mTop;
                childBottom += mBottom;

                double pos;
                switch (align.Block) {
                    case SnapAlign.Start:
                        pos = childTop - padTop;
                        break;
                    case SnapAlign.End:
                        pos = childBottom - viewportH + padBottom;
                        break;
                    case SnapAlign.Center:
                        pos = ((childTop + childBottom) * 0.5) - (viewportH * 0.5);
                        break;
                    default: return;
                }
                pos = ScrollMath.Clamp(pos, 0, state.MaxScrollY);
                result.Add(new SnapPoint(pos, child, stop));
            });
            return result;
        }

        public List<SnapPoint> CollectSnapPointsX(Box scrollContainer) {
            var result = new List<SnapPoint>();
            if (scrollContainer?.Style == null) return result;
            var state = container?.Get(scrollContainer);
            if (state == null) return result;

            double padLeft = ResolveLonghandOrShorthand(scrollContainer.Style, "scroll-padding-left", "scroll-padding");
            double padRight = ResolveLonghandOrShorthand(scrollContainer.Style, "scroll-padding-right", "scroll-padding");

            double viewportW = state.ViewportWidth;

            CollectDescendants(scrollContainer, (child, _, localX) => {
                if (child.Style == null) return;
                var align = SnapParser.ParseAlign(child.Style.Get("scroll-snap-align"));
                if (!align.IsActive) return;
                var stop = SnapParser.ParseStop(child.Style.Get("scroll-snap-stop"));

                double childLeft = localX;
                double childRight = childLeft + child.Width;

                double mLeft = ResolveLonghandOrShorthand(child.Style, "scroll-margin-left", "scroll-margin");
                double mRight = ResolveLonghandOrShorthand(child.Style, "scroll-margin-right", "scroll-margin");
                childLeft -= mLeft;
                childRight += mRight;

                double pos;
                switch (align.Inline) {
                    case SnapAlign.Start:
                        pos = childLeft - padLeft;
                        break;
                    case SnapAlign.End:
                        pos = childRight - viewportW + padRight;
                        break;
                    case SnapAlign.Center:
                        pos = ((childLeft + childRight) * 0.5) - (viewportW * 0.5);
                        break;
                    default: return;
                }
                pos = ScrollMath.Clamp(pos, 0, state.MaxScrollX);
                result.Add(new SnapPoint(pos, child, stop));
            });
            return result;
        }

        // Pick the longhand value if the author actually set it (even to 0);
        // only fall back to the shorthand otherwise. Plain `if (longhand==0)
        // fallback` is wrong: an intentional `scroll-margin-top: 0` is
        // indistinguishable from "unset" once it's parsed to a number, and
        // the shorthand (typically non-zero) would silently override it.
        // CSS cascade resolution distinguishes by string presence, not by
        // resolved-zero, so we check the raw Get(...) result first.
        //
        // The shorthand may carry 1–4 space-separated values (CSS box-edge
        // shorthand grammar — top, right, bottom, left). Decompose to pick
        // the side matching `longhand`; without this, the shorthand path
        // used SnapParser.ParsePadding on the full string and silently kept
        // only the first value, so `scroll-padding: 10px 20px 30px 40px`
        // resolved every side to 10.
        static double ResolveLonghandOrShorthand(
            Weva.Css.Cascade.ComputedStyle style, string longhand, string shorthand) {
            string raw = style.Get(longhand);
            if (!string.IsNullOrEmpty(raw)) return SnapParser.ParsePadding(raw);
            string sh = style.Get(shorthand);
            if (string.IsNullOrEmpty(sh)) return 0;
            return ParseBoxEdgeFromShorthand(sh, longhand);
        }

        // Box-edge shorthand: 1 value → all sides, 2 → t/b r/l, 3 → t r/l b,
        // 4 → t r b l. Split on whitespace and pick the slot for `longhand`.
        static double ParseBoxEdgeFromShorthand(string shorthand, string longhand) {
            int slot = SlotForLonghand(longhand);
            if (slot < 0) return SnapParser.ParsePadding(shorthand);
            var parts = SplitOnAsciiSpace(shorthand);
            if (parts.Count == 0) return 0;
            int idx;
            switch (parts.Count) {
                case 1: idx = 0; break;
                case 2: idx = (slot == 0 || slot == 2) ? 0 : 1; break;        // top/bottom = 0; right/left = 1
                case 3: idx = slot == 0 ? 0 : slot == 2 ? 2 : 1; break;        // top = 0; bottom = 2; right/left = 1
                default: idx = slot < parts.Count ? slot : parts.Count - 1; break;
            }
            return SnapParser.ParsePadding(parts[idx]);
        }

        static int SlotForLonghand(string longhand) {
            if (longhand.EndsWith("-top")) return 0;
            if (longhand.EndsWith("-right")) return 1;
            if (longhand.EndsWith("-bottom")) return 2;
            if (longhand.EndsWith("-left")) return 3;
            return -1;
        }

        static List<string> SplitOnAsciiSpace(string s) {
            var parts = new List<string>(4);
            int i = 0;
            while (i < s.Length) {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                int start = i;
                while (i < s.Length && s[i] != ' ' && s[i] != '\t') i++;
                if (i > start) parts.Add(s.Substring(start, i - start));
            }
            return parts;
        }

        // For a single-axis resolver. axis: false=>Y, true=>X. v1 simplification:
        // we don't need the cross-axis selection logic.
        public bool TryFindSnapTargetY(Box scrollContainer, double currentY, SnapType type, out double snappedY) {
            return TryFindSnapTargetY(scrollContainer, currentY, currentY, type, out snappedY);
        }

        // CSS Scroll Snap L1 §5.2: a snap area with `scroll-snap-stop: always`
        // must not be skipped during a multi-snap pan. If the scroll movement
        // from `startY` to `currentY` passes over such an area, it becomes the
        // target instead of the nearest snap point.
        public bool TryFindSnapTargetY(Box scrollContainer, double startY, double currentY, SnapType type, out double snappedY) {
            snappedY = currentY;
            if (!type.IsActive) return false;
            if (type.Axis == SnapAxis.X) return false;
            var points = CollectSnapPointsY(scrollContainer);
            if (points.Count == 0) return false;

            if (startY != currentY) {
                double lo = System.Math.Min(startY, currentY);
                double hi = System.Math.Max(startY, currentY);
                int alwaysIdx = -1;
                double alwaysDistFromStart = double.PositiveInfinity;
                for (int i = 0; i < points.Count; i++) {
                    if (points[i].Stop != SnapStop.Always) continue;
                    double p = points[i].Position;
                    if (p <= lo || p >= hi) continue;
                    double d = System.Math.Abs(p - startY);
                    if (d < alwaysDistFromStart) {
                        alwaysDistFromStart = d;
                        alwaysIdx = i;
                    }
                }
                if (alwaysIdx >= 0) {
                    snappedY = points[alwaysIdx].Position;
                    return true;
                }
            }

            int nearest = -1;
            double bestDelta = double.PositiveInfinity;
            for (int i = 0; i < points.Count; i++) {
                double d = System.Math.Abs(points[i].Position - currentY);
                if (d < bestDelta) {
                    bestDelta = d;
                    nearest = i;
                }
            }
            if (nearest < 0) return false;
            if (type.Strictness == SnapStrictness.Proximity) {
                var state = container.Get(scrollContainer);
                double viewport = state?.ViewportHeight ?? 0;
                double threshold = viewport * 0.5;
                if (threshold <= 0) return false;
                if (bestDelta > threshold) return false;
            }
            snappedY = points[nearest].Position;
            return true;
        }

        public bool TryFindSnapTargetX(Box scrollContainer, double currentX, SnapType type, out double snappedX) {
            return TryFindSnapTargetX(scrollContainer, currentX, currentX, type, out snappedX);
        }

        public bool TryFindSnapTargetX(Box scrollContainer, double startX, double currentX, SnapType type, out double snappedX) {
            snappedX = currentX;
            if (!type.IsActive) return false;
            if (type.Axis == SnapAxis.Y) return false;
            var points = CollectSnapPointsX(scrollContainer);
            if (points.Count == 0) return false;

            if (startX != currentX) {
                double lo = System.Math.Min(startX, currentX);
                double hi = System.Math.Max(startX, currentX);
                int alwaysIdx = -1;
                double alwaysDistFromStart = double.PositiveInfinity;
                for (int i = 0; i < points.Count; i++) {
                    if (points[i].Stop != SnapStop.Always) continue;
                    double p = points[i].Position;
                    if (p <= lo || p >= hi) continue;
                    double d = System.Math.Abs(p - startX);
                    if (d < alwaysDistFromStart) {
                        alwaysDistFromStart = d;
                        alwaysIdx = i;
                    }
                }
                if (alwaysIdx >= 0) {
                    snappedX = points[alwaysIdx].Position;
                    return true;
                }
            }

            int nearest = -1;
            double bestDelta = double.PositiveInfinity;
            for (int i = 0; i < points.Count; i++) {
                double d = System.Math.Abs(points[i].Position - currentX);
                if (d < bestDelta) {
                    bestDelta = d;
                    nearest = i;
                }
            }
            if (nearest < 0) return false;
            if (type.Strictness == SnapStrictness.Proximity) {
                var state = container.Get(scrollContainer);
                double viewport = state?.ViewportWidth ?? 0;
                double threshold = viewport * 0.5;
                if (threshold <= 0) return false;
                if (bestDelta > threshold) return false;
            }
            snappedX = points[nearest].Position;
            return true;
        }

        public static SnapType ResolveType(Box scrollContainer) {
            if (scrollContainer?.Style == null) return SnapType.None;
            string raw = scrollContainer.Style.Get("scroll-snap-type");
            return SnapParser.ParseType(raw);
        }

        // CSS Scroll Snap L1 §6: a scroll container's snap areas are its
        // descendants whose nearest scroll-container ancestor is itself.
        // Walk the subtree but stop descending into any nested scroll
        // container — its descendants belong to that container's snap pool.
        // The nested container's own box is still visited (it can carry
        // scroll-snap-align and act as a snap area in the outer).
        static void CollectDescendants(Box scrollContainer, System.Action<Box, double, double> visit) {
            double interiorOriginY = scrollContainer.PaddingTop + scrollContainer.BorderTop;
            double interiorOriginX = scrollContainer.PaddingLeft + scrollContainer.BorderLeft;
            for (int i = 0; i < scrollContainer.Children.Count; i++) {
                var child = scrollContainer.Children[i];
                Recurse(child, child.X - interiorOriginX, child.Y - interiorOriginY, visit);
            }
        }

        static void Recurse(Box node, double localX, double localY, System.Action<Box, double, double> visit) {
            visit(node, localY, localX);
            if (ScrollContainerLookup.HasNonVisibleOverflow(node)) return;
            for (int i = 0; i < node.Children.Count; i++) {
                var c = node.Children[i];
                Recurse(c, localX + c.X, localY + c.Y, visit);
            }
        }
    }
}
