using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Positioning {
    public sealed class StackingContextBuilder {
        public StackingContext Build(Box root) {
            if (root == null) return null;
            var ctx = new StackingContext { Root = root, ZIndex = 0 };
            int order = 0;
            foreach (var c in root.Children) {
                CollectInto(c, ctx, ctx, ref order);
            }
            SortContextChildren(ctx);
            return ctx;
        }

        // current = the stacking context we're collecting bucket entries into.
        // owner   = the same as current at recursion start, but when descending into a non-
        //           context positioned box we still keep buckets in `current`. We track
        //           document order via a single counter to break ties.
        static void CollectInto(Box box, StackingContext current, StackingContext owner, ref int order) {
            int docOrder = order++;

            bool createsContext = box.CreatesStackingContext();

            if (createsContext) {
                int z = box.ZIndex.GetValueOrDefault(0);
                var child = new StackingContext { Root = box, ZIndex = z };
                BuildInto(box, child, ref order);
                child.SortKey = docOrder;
                current.ChildContexts.Add(child);
                if (z < 0) current.PositionedDescendantsZNegative.Add(box);
                else if (z > 0) current.PositionedDescendantsZPositive.Add(box);
                else current.PositionedDescendantsZAuto.Add(box);
                return;
            }

            if (box.IsPositioned()) {
                current.PositionedDescendantsZAuto.Add(box);
                foreach (var c in box.Children) CollectInto(c, current, owner, ref order);
                return;
            }

            current.NonPositionedDescendants.Add(box);
            foreach (var c in box.Children) CollectInto(c, current, owner, ref order);
        }

        static void BuildInto(Box root, StackingContext ctx, ref int order) {
            foreach (var c in root.Children) {
                CollectInto(c, ctx, ctx, ref order);
            }
            SortContextChildren(ctx);
        }

        static void SortContextChildren(StackingContext ctx) {
            ctx.ChildContexts.Sort((a, b) => {
                int c = a.ZIndex.CompareTo(b.ZIndex);
                if (c != 0) return c;
                return a.SortKey.CompareTo(b.SortKey);
            });
        }
    }
}
