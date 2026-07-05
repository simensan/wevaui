using System;
using System.Collections.Generic;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.Events {
    // Direction enum for SpatialNavigator. Named SpatialDirection (not NavDirection)
    // to avoid collision with the existing DirectionalNavigation.NavDirection.
    // Both enums carry the same four cardinal values; callers use whichever API
    // they need.
    public enum SpatialDirection {
        Up,
        Down,
        Left,
        Right,
    }

    // CSS Spatial Navigation heuristics for box-tree focus movement.
    //
    // Reference: WICG Spatial Navigation spec
    // https://drafts.csswg.org/css-nav-1/ (spatnav)
    //
    // This class operates directly on the laid-out Box tree (no external
    // rect-callback indirection). It is pure geometry logic — no Unity input
    // wiring, no EventDispatcher coupling. Unity wiring is a separate W3 phase-2
    // increment.
    //
    // Typical call-site (once W3 phase 2 lands):
    //   var next = SpatialNavigator.FindNext(layoutRoot, focusedBox, SpatialDirection.Down);
    //   if (next != null) focusManager.Focus(next.Element);
    //
    // Algorithm overview (WICG spatnav §6.5 simplified):
    //   1. Absolute positions: walk each box's Parent chain summing X/Y.
    //   2. Candidate filter (half-plane): a candidate qualifies if its leading
    //      edge in the navigation direction lies at or beyond the current box's
    //      trailing edge minus a small epsilon (allows a few-pixel overlap so
    //      buttons that don't align to a grid are still navigable). For example,
    //      for Right: candidate.absLeft >= current.absRight − Epsilon.
    //   3. Score = rectDistance(current, candidate) × (1 + perp × PerpPenalty)
    //      where rectDistance is the Euclidean distance between the closest
    //      points of the two border-box rects (0 when they touch/overlap), and
    //      perp is the normalised orthogonal misalignment. This matches the
    //      spatnav spec's preference for elements that are both close AND
    //      axis-aligned with the current focus.
    //   4. Overlap bonus: when the two rects share a span on the orthogonal axis
    //      (e.g. for Up/Down navigation: both share a horizontal strip), reduce
    //      the score by OverlapBonus so row/column peers beat distant aligned
    //      strangers.
    //   5. Tiebreak: document order (earlier in depth-first traversal wins).
    //   6. Filtering: skip TextRun boxes (they share their parent Element's
    //      pointer and would cause double-counting), zero-size boxes (display:none
    //      or collapsed), and boxes with no owning Element (anonymous wrappers).
    //      Also skip boxes whose element is disabled (tabindex="-1" or `disabled`
    //      attribute present).
    public static class SpatialNavigator {
        // Half-plane allowance: a candidate whose leading edge overlaps the
        // current box's trailing edge by up to this many CSS px is still
        // considered "in the navigation direction". Prevents the "two buttons
        // that perfectly touch" edge case from excluding either candidate.
        public const double Epsilon = 2.0;

        // Weight applied to the orthogonal misalignment term. >1 penalises
        // off-axis candidates more than raw distance alone. 2.5 was chosen
        // empirically to make a 3×3 grid navigate correctly in all four
        // directions while still picking the closer of two column-misaligned
        // candidates when the second is far enough away. (Tests confirm 2.0
        // would also work for the grid cases but fails the
        // Staggered_rows_orthogonal_penalty test.)
        public const double PerpPenalty = 2.5;

        // Flat score reduction applied to candidates that share an orthogonal-
        // axis span with the current element (e.g. same row for Left/Right
        // movement). Sized to defeat candidates that are slightly closer in raw
        // rect distance but completely misaligned. Set to 80 px worth of
        // equivalent parallel distance, which comfortably separates row-peers
        // from off-row elements in layouts with ≥20 px row gaps.
        public const double OverlapBonus = 80.0;

        // Find the best next focusable box from `current` in `dir`.
        //
        // Returns null when:
        //   - the tree has no focusable candidates at all,
        //   - every focusable lies in the wrong half-plane, or
        //   - `current` is null (call PickEntryFocus instead when you want the
        //     topmost-leftmost initial focus).
        //
        // The returned Box always has a non-null Element.
        public static Box FindNext(Box root, Box current, SpatialDirection dir) {
            if (root == null) throw new ArgumentNullException(nameof(root));

            // Compute absolute rect for the current box. If current is null or
            // has no valid geometry we fall through to null (caller should
            // use PickEntryFocus for first-press behaviour).
            if (current == null) return null;
            var curAbs = AbsRect(current);
            if (IsZeroSize(curAbs)) return null;

            Box best = null;
            double bestScore = double.PositiveInfinity;
            int bestOrder = int.MaxValue;

            int docOrder = 0;
            CollectCandidates(root, current, curAbs, dir, ref best, ref bestScore, ref bestOrder, ref docOrder);
            return best;
        }

        // Walk the box tree and score every qualifying candidate against the
        // current element, updating (best, bestScore, bestOrder) in place.
        // `docOrder` is incremented for every non-TextRun, element-owning box
        // visited so that the order reflects the full tree walk, not just
        // focusable boxes (prevents reordering across invisible siblings).
        static void CollectCandidates(
            Box node,
            Box current,
            in AbsoluteRect curAbs,
            SpatialDirection dir,
            ref Box best,
            ref double bestScore,
            ref int bestOrder,
            ref int docOrder
        ) {
            // Skip TextRun: shares parent's Element pointer; must NOT be treated
            // as a focusable candidate (spec: only element boxes, not anonymous
            // runs, participate in spatial navigation).
            if (node is TextRun) return;

            // Count document order for all non-TextRun boxes.
            int myOrder = docOrder++;

            if (node != current && node.Element != null && !IsZeroSize(AbsRect(node))) {
                if (IsFocusableBox(node)) {
                    var cAbs = AbsRect(node);
                    if (InHalfPlane(curAbs, cAbs, dir)) {
                        double score = Score(curAbs, cAbs, dir);
                        if (score < bestScore || (score == bestScore && myOrder < bestOrder)) {
                            best = node;
                            bestScore = score;
                            bestOrder = myOrder;
                        }
                    }
                }
            }

            foreach (var child in node.Children) {
                CollectCandidates(child, current, curAbs, dir, ref best, ref bestScore, ref bestOrder, ref docOrder);
            }
        }

        // Half-plane test (WICG spatnav §6.4.1 "B is in the direction D of A").
        // We use border-box edges with a small Epsilon to handle touching rects.
        // For each direction the candidate's "incoming" edge must clear the
        // current element's "outgoing" edge.
        static bool InHalfPlane(in AbsoluteRect cur, in AbsoluteRect cand, SpatialDirection dir) {
            switch (dir) {
                case SpatialDirection.Right:  return cand.Left   >= cur.Right  - Epsilon;
                case SpatialDirection.Left:   return cand.Right  <= cur.Left   + Epsilon;
                case SpatialDirection.Down:   return cand.Top    >= cur.Bottom - Epsilon;
                case SpatialDirection.Up:     return cand.Bottom <= cur.Top    + Epsilon;
                default: return false;
            }
        }

        // Scoring function.
        //
        // dist   = Euclidean distance between the nearest points of the two rects
        //          (0 when they are touching or overlapping; handles the case where
        //          a candidate is axis-aligned and adjacent — its border-box edge
        //          touches the current box's edge, giving dist=0, so score=0 minus
        //          any overlap bonus → extremely low → wins over everything else).
        //
        // perp   = orthogonal misalignment normalised to [0,1] relative to the
        //          parallel distance; grows as the candidate drifts sideways. We
        //          cap the ratio at 1 so it doesn't dominate when parallel is tiny.
        //
        // bonus  = OverlapBonus when the two rects share a span on the orthogonal
        //          axis (row peers for Left/Right, column peers for Up/Down).
        //
        // score  = dist * (1 + perp * PerpPenalty) - bonus
        //
        // This mirrors the spatnav recommendation to weight orthogonal displacement
        // more heavily than direct distance while still favouring close elements
        // over distant but perfectly-aligned ones when no overlap exists.
        static double Score(in AbsoluteRect cur, in AbsoluteRect cand, SpatialDirection dir) {
            // Closest-point distance between the two rects.
            double dx = RectAxisDist(cur.Left, cur.Right, cand.Left, cand.Right);
            double dy = RectAxisDist(cur.Top, cur.Bottom, cand.Top, cand.Bottom);
            double dist = Math.Sqrt(dx * dx + dy * dy);

            // Parallel and perpendicular components relative to the nav direction.
            double parallel, perpendicular;
            switch (dir) {
                case SpatialDirection.Left:
                case SpatialDirection.Right:
                    parallel = dx; perpendicular = dy;
                    break;
                default: // Up / Down
                    parallel = dy; perpendicular = dx;
                    break;
            }

            // Normalised perpendicular ratio. Cap at 1 to prevent domination when
            // parallel distance is near-zero (two boxes on the same column edge).
            double ratio = parallel > 0.0001 ? Math.Min(perpendicular / parallel, 1.0) : 0;

            // Overlap bonus: candidate shares a cross-axis span with current.
            bool overlaps = dir == SpatialDirection.Left || dir == SpatialDirection.Right
                ? RangesOverlap(cur.Top, cur.Bottom, cand.Top, cand.Bottom)
                : RangesOverlap(cur.Left, cur.Right, cand.Left, cand.Right);

            double score = dist * (1.0 + ratio * PerpPenalty) - (overlaps ? OverlapBonus : 0.0);
            return score;
        }

        // 1-D gap between two axis ranges [a1,a2] and [b1,b2]; 0 when they overlap.
        static double RectAxisDist(double a1, double a2, double b1, double b2) {
            if (b1 > a2) return b1 - a2;   // b is to the right of / below a
            if (a1 > b2) return a1 - b2;   // a is to the right of / below b
            return 0;                       // ranges overlap
        }

        static bool RangesOverlap(double a1, double a2, double b1, double b2) {
            return a1 < b2 && b1 < a2;
        }

        // Focusability predicate mirroring FocusManager.IsFocusable but applied
        // to a Box (where we have an Element reference but no Document/FocusManager
        // instance handy). We replicate the same rules:
        //   - element must not be disabled
        //   - tabindex >= 0, OR naturally focusable tag (button/input/select/textarea/a[href])
        // We intentionally do NOT check IsHidden here — the caller already skips
        // zero-size boxes, which is the primary observable effect of display:none.
        // A separate phase-2 integration layer can layer IsHidden on top.
        static bool IsFocusableBox(Box box) {
            var e = box.Element;
            if (e == null) return false;
            if (FocusManager.IsDisabled(e)) return false;
            if (e.HasAttribute("tabindex")) {
                var raw = e.GetAttribute("tabindex");
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var ti)) {
                    return ti >= 0;
                }
                // tabindex="" or non-integer: treat as tabindex=0, which is focusable.
                return true;
            }
            return IsNaturallyFocusable(e);
        }

        static bool IsNaturallyFocusable(Element e) {
            switch (e.TagName) {
                case "button":
                case "input":
                case "select":
                case "textarea":
                    return true;
                case "a":
                    return e.HasAttribute("href");
                default:
                    return false;
            }
        }

        // Compute the absolute border-box rect for a box by summing parent offsets.
        // Matches LayoutTestHelpers.AbsoluteOrigin and BoxTreeHitTester's descent.
        // Does NOT account for CSS transforms or sticky offsets — this is the pure
        // layout-space position, consistent with how spatnav reasons about geometry.
        static AbsoluteRect AbsRect(Box b) {
            double x = 0, y = 0;
            for (var n = b; n != null; n = n.Parent) {
                x += n.X;
                y += n.Y;
            }
            return new AbsoluteRect(x, y, b.Width, b.Height);
        }

        static bool IsZeroSize(in AbsoluteRect r) {
            return r.Width <= 0 || r.Height <= 0;
        }

        // Absolute border-box rect in document space. Private to this file; callers
        // outside SpatialNavigator work with Box directly. Using a struct (not NavRect)
        // keeps the Events layer free of a Layout dependency for its own internal use.
        readonly struct AbsoluteRect {
            public readonly double Left;
            public readonly double Top;
            public readonly double Width;
            public readonly double Height;

            public AbsoluteRect(double left, double top, double width, double height) {
                Left   = left;
                Top    = top;
                Width  = width;
                Height = height;
            }

            public double Right  => Left + Width;
            public double Bottom => Top  + Height;
        }
    }
}
