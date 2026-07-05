using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Positioning {
    public static class PaintOrderTraversal {
        public static IEnumerable<Box> Enumerate(StackingContext root) {
            if (root == null) yield break;
            foreach (var b in EnumerateContext(root)) yield return b;
        }

        static IEnumerable<Box> EnumerateContext(StackingContext ctx) {
            // 1) the context's own root box (background/border)
            yield return ctx.Root;

            // 2) child contexts with negative z (asc by z, doc order tiebreak)
            foreach (var child in NegativeChildContexts(ctx)) {
                foreach (var b in EnumerateContext(child)) yield return b;
            }

            // 3) non-positioned in-flow descendants in doc order, split per
            //    CSS 2.1 Appendix E z-order paint steps 3a / 3b / 3c so that
            //    floats sit on top of block backgrounds but inline content
            //    paints over floats:
            //      3a. block-level non-positioned descendants (backgrounds + borders)
            //      3b. non-positioned floats (in tree order) — full painting
            //      3c. in-flow inline-level non-positioned descendants — full painting
            //    Each sub-pass walks the flat NonPositionedDescendants list in
            //    document order and emits only the boxes whose type matches its
            //    bucket. A box is a float when it's a BlockBox with IsFloat; it
            //    is inline-level when it's an InlineBox / TextRun / LineBox or a
            //    BlockBox with IsInlineBlock; otherwise it's block-level. Float
            //    classification wins over inline-block so a floated inline-block
            //    box paints in the float pass, not the inline pass.
            // 3a — block-level
            for (int i = 0; i < ctx.NonPositionedDescendants.Count; i++) {
                var b = ctx.NonPositionedDescendants[i];
                if (b is BlockBox bbA && bbA.IsFloat) continue;
                if (b is InlineBox || b is TextRun || b is LineBox) continue;
                if (b is BlockBox bbInl && bbInl.IsInlineBlock) continue;
                yield return b;
            }
            // 3b — floats
            for (int i = 0; i < ctx.NonPositionedDescendants.Count; i++) {
                var b = ctx.NonPositionedDescendants[i];
                if (b is BlockBox bbF && bbF.IsFloat) yield return b;
            }
            // 3c — inline-level
            for (int i = 0; i < ctx.NonPositionedDescendants.Count; i++) {
                var b = ctx.NonPositionedDescendants[i];
                if (b is BlockBox bbC && bbC.IsFloat) continue;
                if (b is InlineBox || b is TextRun || b is LineBox) { yield return b; continue; }
                if (b is BlockBox bbIB && bbIB.IsInlineBlock) yield return b;
            }

            // 4) positioned descendants with z=auto, including z=0 child contexts (recurse those)
            //    Doc order across the merged set.
            foreach (var b in ctx.PositionedDescendantsZAuto) {
                var sub = FindContext(ctx, b);
                if (sub != null) {
                    foreach (var inner in EnumerateContext(sub)) yield return inner;
                } else {
                    yield return b;
                }
            }

            // 5) child contexts with positive z (asc by z, doc order tiebreak)
            foreach (var child in PositiveChildContexts(ctx)) {
                foreach (var b in EnumerateContext(child)) yield return b;
            }
        }

        static IEnumerable<StackingContext> NegativeChildContexts(StackingContext ctx) {
            for (int i = 0; i < ctx.ChildContexts.Count; i++) {
                var c = ctx.ChildContexts[i];
                if (c.ZIndex < 0) yield return c;
            }
        }

        static IEnumerable<StackingContext> PositiveChildContexts(StackingContext ctx) {
            for (int i = 0; i < ctx.ChildContexts.Count; i++) {
                var c = ctx.ChildContexts[i];
                if (c.ZIndex > 0) yield return c;
            }
        }

        static StackingContext FindContext(StackingContext parent, Box box) {
            for (int i = 0; i < parent.ChildContexts.Count; i++) {
                if (parent.ChildContexts[i].Root == box) return parent.ChildContexts[i];
            }
            return null;
        }
    }
}
