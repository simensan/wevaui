using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Floats {
    // CSS 2.1 §9.5 float-context tracker. One instance per block-formatting
    // context (BFC) — floats are scoped to the BFC that contains them and
    // do not escape across BFC boundaries. BlockLayout creates a new
    // FloatContext when it enters a new BFC (the document root, or any
    // box for which MarginCollapsing.EstablishesNewBfc returns true,
    // including floats themselves).
    //
    // Coordinates are LOCAL to the BFC's content-area origin. Each entry
    // is the float's outer rect (Top, Bottom, Left, Right edges including
    // margins). Left floats stack left-to-right; right floats stack
    // right-to-left.
    //
    // The context exposes two query primitives:
    //   - LeftExtentAt(y), RightExtentAt(y): how far inward each side
    //     intrudes at vertical position `y`. Returns 0 when nothing is
    //     active. Callers narrow line boxes / clear targets accordingly.
    //   - HighestBottom*: convenience getters for `clear` resolution.
    //
    // Stack-discipline: BlockLayout snapshots Floats.Count when descending
    // into a non-BFC child container and trims back to the snapshot on
    // exit, ensuring a parent's floats don't leak into nested siblings'
    // queries and vice-versa.
    internal sealed class FloatContext {
        public readonly struct Entry {
            public readonly BlockBox Box;
            public readonly FloatType Side;
            // Outer rect (margin box) of the float, in BFC-local coords.
            public readonly double Top;
            public readonly double Bottom;
            public readonly double Left;
            public readonly double Right;

            public Entry(BlockBox box, FloatType side, double top, double bottom, double left, double right) {
                Box = box;
                Side = side;
                Top = top;
                Bottom = bottom;
                Left = left;
                Right = right;
            }
        }

        readonly List<Entry> floats = new List<Entry>(4);

        public IReadOnlyList<Entry> Floats => floats;
        public int Count => floats.Count;

        public void Clear() {
            floats.Clear();
        }

        public void Add(Entry e) {
            floats.Add(e);
        }

        // Returns the distance from the BFC's left content edge to the
        // rightmost edge of any active left float at vertical position
        // `y`. Used to narrow line boxes and shift right-side floats.
        public double LeftExtentAt(double y) {
            double max = 0;
            for (int i = 0; i < floats.Count; i++) {
                var f = floats[i];
                if (f.Side != FloatType.Left) continue;
                if (y < f.Top) continue;
                if (y >= f.Bottom) continue;
                if (f.Right > max) max = f.Right;
            }
            return max;
        }

        // Returns the distance from the BFC's RIGHT content edge to the
        // leftmost edge of any active right float at vertical position
        // `y`. Use with care: the caller's reference frame matters —
        // we return the inward intrusion (e.g. cbWidth - leftmost-right-
        // float-edge), which is what line-box narrowing needs.
        public double RightExtentAt(double y, double cbWidth) {
            double max = 0;
            for (int i = 0; i < floats.Count; i++) {
                var f = floats[i];
                if (f.Side != FloatType.Right) continue;
                if (y < f.Top) continue;
                if (y >= f.Bottom) continue;
                double inward = cbWidth - f.Left;
                if (inward > max) max = inward;
            }
            return max;
        }

        // Lowest Y at which `width` pixels of horizontal space are free
        // on the given side, starting from the search `y`. Used to place
        // a new float: floats must not overlap each other on the same
        // side, and a new float that doesn't fit at the requested `y`
        // moves down to the first row where it does.
        //
        // `cbWidth` is the available width of the BFC's content edge.
        // Returns y unchanged when the float fits at the initial query.
        public double FindFloatPlacementY(double y, double width, FloatType side, double cbWidth) {
            if (width <= 0) return y;
            // Iterate over the bottoms of all currently-active floats in
            // ascending order so we step down to each row where availability
            // could increase. Worst case is O(N^2) over the float count,
            // which is fine for the v1 use case (page-scale float count
            // is in the single digits).
            double current = y;
            for (;;) {
                double leftIn = LeftExtentAt(current);
                double rightIn = RightExtentAt(current, cbWidth);
                double avail = cbWidth - leftIn - rightIn;
                if (avail >= width) return current;
                // Advance to the smallest bottom that's still above `current`
                // — that's the next row where one of the intruding floats
                // ends and frees space.
                double nextStep = double.PositiveInfinity;
                for (int i = 0; i < floats.Count; i++) {
                    var f = floats[i];
                    if (f.Bottom > current && f.Bottom < nextStep) nextStep = f.Bottom;
                }
                if (double.IsInfinity(nextStep)) return current; // unreachable: nothing to wait for
                current = nextStep;
            }
        }

        // Highest bottom edge across all floats matching `clear`. CSS 2.1
        // §9.5.2: the box's top margin-edge is pushed below this value.
        // Returns 0 when no matching float exists.
        public double ClearBottom(ClearType clear) {
            if (clear == ClearType.None) return 0;
            double max = 0;
            for (int i = 0; i < floats.Count; i++) {
                var f = floats[i];
                bool match = clear == ClearType.Both
                    || (clear == ClearType.Left && f.Side == FloatType.Left)
                    || (clear == ClearType.Right && f.Side == FloatType.Right);
                if (!match) continue;
                if (f.Bottom > max) max = f.Bottom;
            }
            return max;
        }

        // Per CSS 2.1 §10.6.7: a BFC's height grows to enclose any floats
        // it contains. Returns the maximum float bottom; the caller adds
        // it into the content-bottom calculation.
        public double MaxBottom() {
            double max = 0;
            for (int i = 0; i < floats.Count; i++) {
                if (floats[i].Bottom > max) max = floats[i].Bottom;
            }
            return max;
        }
    }
}
