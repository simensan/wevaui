using System.Collections.Generic;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using Weva.Layout.Multicol;
using Weva.Layout.Tables;

namespace Weva.Layout {
    // Per-LayoutEngine pools mirroring CascadePools / PaintConverterPools (v0.2.2 /
    // v0.2.3). Layout's allocation hotspots:
    //   - One Box subtype per Element on every fresh Layout call (BoxBuilder).
    //   - Per-pass List<> scratch in InlineLayout / LineBreaker / Flex / Grid.
    //   - LineBox / TextRun / AnonymousBlockBox aren't cacheable so they churn even
    //     when the rest of the tree hits the layout cache.
    //
    // Lifetime: pools live on the LayoutEngine instance; concurrent layouts on
    // different engines are isolated by construction. A single Layout call walks
    // the box tree single-threaded so one shared scratch instance is enough.
    //
    // Pool size is unbounded — there is no soft cap. The high-water mark is
    // bounded by the total number of boxes the tallest tree ever produces, which
    // is the same upper bound the GC would have seen pre-pooling. If memory
    // pressure becomes an issue, callers can call DropAll() to drop the free
    // lists; in steady state all Allocate calls hit the existing free list.
    internal sealed class BoxPool {
        // Free lists per concrete Box subtype. We key by exact runtime type rather
        // than a single Stack<Box>: BoxBuilder picks the subtype based on the
        // computed display, so AllocateXxx must hand out the correct kind. Mixing
        // types would force every rent site to pay a runtime type-check / cast.
        readonly Stack<BlockBox> blockBoxFree = new(64);
        readonly Stack<InlineBox> inlineBoxFree = new(64);
        readonly Stack<FlexBox> flexBoxFree = new(16);
        readonly Stack<GridBox> gridBoxFree = new(16);
        readonly Stack<MulticolBox> multicolBoxFree = new(8);
        readonly Stack<AnonymousBlockBox> anonymousBlockBoxFree = new(16);
        readonly Stack<TextRun> textRunFree = new(64);
        readonly Stack<LineBox> lineBoxFree = new(32);
        readonly Stack<TableBox> tableBoxFree = new(8);
        readonly Stack<TableRowGroupBox> tableRowGroupBoxFree = new(8);
        readonly Stack<TableRowBox> tableRowBoxFree = new(16);
        readonly Stack<TableCellBox> tableCellBoxFree = new(32);
        readonly Stack<TableCaptionBox> tableCaptionBoxFree = new(4);

        // Live set: every Box returned by an Allocate* call this pass. After
        // reconcile completes, LayoutEngine walks the surviving box tree to mark
        // the kept boxes; the rest get ResetForPool()-ed and pushed back onto
        // their free list so the next Layout call can reuse them.
        readonly List<Box> allocated = new(256);
        int survivorMark;

        // Diagnostic toggle: when DisablePooling is true, AllocateXxx always
        // builds a fresh instance and EndPass does not push back onto the free
        // lists. Used by the BaselineGen alloc check to measure the
        // box-allocation savings of pooling vs. always-fresh.
        public bool DisablePooling;

        // Marks a box live and adds it to this pass's live set. Clears
        // InFreeList so the PushToFree double-push guard re-arms — see
        // PushToFree for why the guard exists.
        void Track(Box b) {
            b.InFreeList = false;
            allocated.Add(b);
        }

        public BlockBox AllocateBlockBox() {
            BlockBox b = (!DisablePooling && blockBoxFree.Count > 0) ? blockBoxFree.Pop() : new BlockBox();
            Track(b);
            return b;
        }

        public InlineBox AllocateInlineBox() {
            InlineBox b = (!DisablePooling && inlineBoxFree.Count > 0) ? inlineBoxFree.Pop() : new InlineBox();
            Track(b);
            return b;
        }

        public FlexBox AllocateFlexBox() {
            FlexBox b = (!DisablePooling && flexBoxFree.Count > 0) ? flexBoxFree.Pop() : new FlexBox();
            Track(b);
            return b;
        }

        public GridBox AllocateGridBox() {
            GridBox b = (!DisablePooling && gridBoxFree.Count > 0) ? gridBoxFree.Pop() : new GridBox();
            Track(b);
            return b;
        }

        public MulticolBox AllocateMulticolBox() {
            MulticolBox b = (!DisablePooling && multicolBoxFree.Count > 0) ? multicolBoxFree.Pop() : new MulticolBox();
            Track(b);
            return b;
        }

        public AnonymousBlockBox AllocateAnonymousBlockBox() {
            AnonymousBlockBox b = (!DisablePooling && anonymousBlockBoxFree.Count > 0) ? anonymousBlockBoxFree.Pop() : new AnonymousBlockBox();
            Track(b);
            return b;
        }

        public TextRun AllocateTextRun() {
            TextRun r = (!DisablePooling && textRunFree.Count > 0) ? textRunFree.Pop() : new TextRun();
            Track(r);
            return r;
        }

        public LineBox AllocateLineBox() {
            LineBox b = (!DisablePooling && lineBoxFree.Count > 0) ? lineBoxFree.Pop() : new LineBox();
            Track(b);
            return b;
        }

        public TableBox AllocateTableBox() {
            TableBox b = (!DisablePooling && tableBoxFree.Count > 0) ? tableBoxFree.Pop() : new TableBox();
            Track(b);
            return b;
        }

        public TableRowGroupBox AllocateTableRowGroupBox() {
            TableRowGroupBox b = (!DisablePooling && tableRowGroupBoxFree.Count > 0) ? tableRowGroupBoxFree.Pop() : new TableRowGroupBox();
            Track(b);
            return b;
        }

        public TableRowBox AllocateTableRowBox() {
            TableRowBox b = (!DisablePooling && tableRowBoxFree.Count > 0) ? tableRowBoxFree.Pop() : new TableRowBox();
            Track(b);
            return b;
        }

        public TableCellBox AllocateTableCellBox() {
            TableCellBox b = (!DisablePooling && tableCellBoxFree.Count > 0) ? tableCellBoxFree.Pop() : new TableCellBox();
            Track(b);
            return b;
        }

        public TableCaptionBox AllocateTableCaptionBox() {
            TableCaptionBox b = (!DisablePooling && tableCaptionBoxFree.Count > 0) ? tableCaptionBoxFree.Pop() : new TableCaptionBox();
            Track(b);
            return b;
        }

        // Returns a single box to its appropriate free list. Used by InlineLayout
        // when it discards the previous IFC's lines/runs before re-laying-out.
        public void Recycle(Box b) {
            if (b == null) return;
            b.ResetForPool();
            PushToFree(b);
        }

        void PushToFree(Box b) {
            // Double-push guard: a box that was Recycle()d mid-pass is still in
            // allocated[], so EndPass would push it onto its free list a SECOND
            // time — two future Allocate* calls would then hand out the same
            // instance for two tree positions (silent shared-state corruption;
            // trigger sites: LineClampHelper mid-pass recycles, the scroll-
            // boundary graft). InFreeList is cleared by Track on allocation.
            if (b.InFreeList) return;
            b.InFreeList = true;
            switch (b) {
                case AnonymousBlockBox a: anonymousBlockBoxFree.Push(a); break;
                case FlexBox f: flexBoxFree.Push(f); break;
                case GridBox g: gridBoxFree.Push(g); break;
                case MulticolBox mc: multicolBoxFree.Push(mc); break;
                case TableBox t: tableBoxFree.Push(t); break;
                case TableRowGroupBox trg: tableRowGroupBoxFree.Push(trg); break;
                case TableRowBox tr2: tableRowBoxFree.Push(tr2); break;
                case TableCellBox tc: tableCellBoxFree.Push(tc); break;
                case TableCaptionBox tcap: tableCaptionBoxFree.Push(tcap); break;
                case BlockBox bb: blockBoxFree.Push(bb); break;
                case InlineBox ib: inlineBoxFree.Push(ib); break;
                case TextRun tr: textRunFree.Push(tr); break;
                case LineBox lb: lineBoxFree.Push(lb); break;
            }
        }

        // Called at the very start of a Layout pass to prepare the live-set list
        // for tracking this pass's outputs.
        public void BeginPass() {
            allocated.Clear();
        }

        // Walks the box tree that survived Reconcile, marking which boxes the
        // engine intends to keep. Recycles every other box this pass allocated
        // (these are the freshly-built boxes that Reconcile replaced with cache
        // hits, plus any boxes structurally discarded mid-pass).
        public void EndPass(Box survivedRoot) {
            unchecked {
                survivorMark++;
                if (survivorMark == 0) survivorMark = 1;
            }
            if (survivedRoot != null) MarkSurvivors(survivedRoot, survivorMark);
            for (int i = 0; i < allocated.Count; i++) {
                var b = allocated[i];
                if (b.PoolSurvivorMark == survivorMark) continue;
                b.ResetForPool();
                PushToFree(b);
            }
            allocated.Clear();
        }

        // Subtree-path EndPass — walks allocated[] instead of the survivor
        // tree and uses Box.Parent as the in-tree witness. A box that was
        // built but never spliced has Parent==null (mid-build orphan or
        // discarded pseudo-element); a box that ended up inside the
        // spliced subtree has Parent set to its surviving parent.
        //
        // This is correct ONLY when the caller can guarantee that every
        // allocated[] box this pass either (a) ended up inside the
        // spliced subtree (Parent != null), or (b) was discarded
        // mid-build (Parent == null). The TryLayoutSubtree path satisfies
        // both: BoxBuilder.Build owns all allocations and either splices
        // them into the new subtree or leaves them orphaned. Tree-wide
        // reconciliation paths (full Layout) can shuffle boxes between
        // cached and fresh in ways that this faster check can't see, so
        // they keep using EndPass(lastRoot).
        //
        // Cost: O(allocated.Count) instead of O(tree size). On a
        // 1500-box tree with 6 allocated boxes per warm flip this is
        // ~30µs saved per flip.
        public void EndPassByAllocatedParent() {
            unchecked {
                survivorMark++;
                if (survivorMark == 0) survivorMark = 1;
            }
            for (int i = 0; i < allocated.Count; i++) {
                var b = allocated[i];
                if (b.Parent != null) {
                    b.PoolSurvivorMark = survivorMark;
                    continue;
                }
                b.ResetForPool();
                PushToFree(b);
            }
            allocated.Clear();
        }

        void MarkSurvivors(Box b, int mark) {
            b.PoolSurvivorMark = mark;
            var children = b.ChildList;
            for (int i = 0; i < children.Count; i++) MarkSurvivors(children[i], mark);
        }

        // Resets ALL free lists. Used by LayoutEngine.InvalidateAll so a cache
        // wipe doesn't leave stale boxes lingering past a viewport change.
        public void DropAll() {
            blockBoxFree.Clear();
            inlineBoxFree.Clear();
            flexBoxFree.Clear();
            gridBoxFree.Clear();
            anonymousBlockBoxFree.Clear();
            textRunFree.Clear();
            lineBoxFree.Clear();
            tableBoxFree.Clear();
            tableRowGroupBoxFree.Clear();
            tableRowBoxFree.Clear();
            tableCellBoxFree.Clear();
            tableCaptionBoxFree.Clear();
            allocated.Clear();
        }

        // Diagnostics for tests.
        public int FreeCountFor<T>() where T : Box {
            if (typeof(T) == typeof(BlockBox)) return blockBoxFree.Count;
            if (typeof(T) == typeof(InlineBox)) return inlineBoxFree.Count;
            if (typeof(T) == typeof(FlexBox)) return flexBoxFree.Count;
            if (typeof(T) == typeof(GridBox)) return gridBoxFree.Count;
            if (typeof(T) == typeof(AnonymousBlockBox)) return anonymousBlockBoxFree.Count;
            if (typeof(T) == typeof(TextRun)) return textRunFree.Count;
            if (typeof(T) == typeof(LineBox)) return lineBoxFree.Count;
            if (typeof(T) == typeof(TableBox)) return tableBoxFree.Count;
            if (typeof(T) == typeof(TableRowGroupBox)) return tableRowGroupBoxFree.Count;
            if (typeof(T) == typeof(TableRowBox)) return tableRowBoxFree.Count;
            if (typeof(T) == typeof(TableCellBox)) return tableCellBoxFree.Count;
            if (typeof(T) == typeof(TableCaptionBox)) return tableCaptionBoxFree.Count;
            return 0;
        }
    }

    // Per-pass List<> scratch shared across the layout passes (block / inline /
    // flex / grid). Reset() at the start of every Layout call, then handed to
    // whichever pass needs it. Single-threaded: the pass owns the scratch
    // end-to-end. Each list is .Clear()-ed on rent so the previous pass's payload
    // does not leak; List<T>.Clear is O(count) and does not free the backing
    // array, which is exactly what we want.
    internal sealed class LayoutScratch {
        // BlockLayout.LayoutContent groups in-flow block children for margin
        // collapsing. Pre-pooling, this was a `new List<BlockBox>()` per box.
        public readonly List<BlockBox> BlockInflow = new(32);

        // FlexLayout per-Layout buffers. Reused across every FlexLayout.Layout
        // call within a single Layout pass.
        public readonly List<BlockBox> FlexRawChildren = new(16);
        public readonly List<FlexLayoutItem> FlexItems = new(16);

        // Pool of pre-allocated List<FlexLayoutItem> views for the per-frame
        // items handed to the flex algorithm helpers. FlexLayout.Layout is
        // recursive, so each frame rents its own view.
        readonly Stack<List<FlexLayoutItem>> flexItemsViewPool = new(4);
        public List<FlexLayoutItem> RentFlexItemsView() {
            var v = flexItemsViewPool.Count > 0 ? flexItemsViewPool.Pop() : new List<FlexLayoutItem>(16);
            v.Clear();
            return v;
        }
        public void ReturnFlexItemsView(List<FlexLayoutItem> v) {
            v.Clear();
            flexItemsViewPool.Push(v);
        }
        // Per-frame line buffer. Same stack-discipline pattern as FlexItems —
        // snapshot count on entry, pop slice on exit. We allocate the FlexLine
        // wrappers themselves once per high-water mark too via FlexLinePool.
        public readonly List<FlexLine> FlexLines = new(4);
        public readonly Stack<FlexLine> FlexLinePool = new(4);

        public FlexLine RentFlexLine() {
            FlexLine l = FlexLinePool.Count > 0 ? FlexLinePool.Pop() : new FlexLine();
            l.ItemIndices.Clear();
            l.MainSize = 0; l.CrossSize = 0; l.CrossOffset = 0;
            return l;
        }

        public void ReturnFlexLine(FlexLine l) {
            l.ItemIndices.Clear();
            FlexLinePool.Push(l);
        }

        // Pool of pre-allocated List<FlexLine> wrappers used as the per-frame
        // "lines" view that FlexLayout's helpers iterate over. Each Layout()
        // frame rents one; recursive calls each rent their own.
        readonly Stack<List<FlexLine>> lineViewPool = new(4);
        public List<FlexLine> RentLineView() {
            var v = lineViewPool.Count > 0 ? lineViewPool.Pop() : new List<FlexLine>(4);
            v.Clear();
            return v;
        }
        public void ReturnLineView(List<FlexLine> v) {
            v.Clear();
            lineViewPool.Push(v);
        }

        // GridLayout per-Layout buffers.
        public readonly List<BlockBox> GridRawItems = new(16);

        readonly Stack<List<GridItemProperties>> gridItemPropsViewPool = new(4);
        public List<GridItemProperties> RentGridItemPropsView() {
            var v = gridItemPropsViewPool.Count > 0 ? gridItemPropsViewPool.Pop() : new List<GridItemProperties>(16);
            v.Clear();
            return v;
        }
        public void ReturnGridItemPropsView(List<GridItemProperties> v) {
            v.Clear();
            gridItemPropsViewPool.Push(v);
        }

        readonly Stack<List<int>> intViewPool = new(4);
        public List<int> RentIntView() {
            var v = intViewPool.Count > 0 ? intViewPool.Pop() : new List<int>(16);
            v.Clear();
            return v;
        }
        public void ReturnIntView(List<int> v) {
            v.Clear();
            intViewPool.Push(v);
        }

        readonly Stack<List<GridPlacementResolver.PartialPlacement>> gridPartialViewPool = new(4);
        public List<GridPlacementResolver.PartialPlacement> RentGridPartialView() {
            var v = gridPartialViewPool.Count > 0 ? gridPartialViewPool.Pop() : new List<GridPlacementResolver.PartialPlacement>(16);
            v.Clear();
            return v;
        }
        public void ReturnGridPartialView(List<GridPlacementResolver.PartialPlacement> v) {
            v.Clear();
            gridPartialViewPool.Push(v);
        }

        readonly Stack<List<GridTrackSizing.SizingItem>> gridSizingItemViewPool = new(4);
        public List<GridTrackSizing.SizingItem> RentGridSizingItemView() {
            var v = gridSizingItemViewPool.Count > 0 ? gridSizingItemViewPool.Pop() : new List<GridTrackSizing.SizingItem>(16);
            v.Clear();
            return v;
        }
        public void ReturnGridSizingItemView(List<GridTrackSizing.SizingItem> v) {
            v.Clear();
            gridSizingItemViewPool.Push(v);
        }

        // BoxBuilder.FinalizeBlockChildren snapshots the current children before
        // it inserts anonymous wrappers. Two slots: one for the snapshot (Existing)
        // and one for the in-progress inline run (Buffer).
        public readonly List<Box> AnonymousFlushBuffer = new(16);
        public readonly List<Box> AnonymousFlushExisting = new(16);

        // InlineLayout.MakeAtomItem (shrink-to-fit inline-block) snapshots the
        // atom's raw children before laying it out the first time. The first
        // LayoutBlock pass replaces the atom's Children with LineBox-es; to do
        // a second pass at the fitted width we have to restore the originals
        // so CollectInline can re-walk the real TextRun/InlineBox children.
        // Stack-discipline: each MakeAtomItem call snapshots a slice, restores
        // it, and pops back to its entry count so nested inline-block atoms
        // (atom-inside-atom) don't clobber each other's snapshots.
        public readonly List<Box> AtomShrinkToFitSnapshot = new(16);

        readonly Stack<List<Box>> boxViewPool = new(8);
        public List<Box> RentBoxView() {
            var v = boxViewPool.Count > 0 ? boxViewPool.Pop() : new List<Box>(16);
            v.Clear();
            return v;
        }
        public void ReturnBoxView(List<Box> v) {
            v.Clear();
            boxViewPool.Push(v);
        }

        readonly Stack<List<BlockBox>> blockBoxViewPool = new(8);
        public List<BlockBox> RentBlockBoxView() {
            var v = blockBoxViewPool.Count > 0 ? blockBoxViewPool.Pop() : new List<BlockBox>(8);
            v.Clear();
            return v;
        }
        public void ReturnBlockBoxView(List<BlockBox> v) {
            v.Clear();
            blockBoxViewPool.Push(v);
        }

        // FlexLayoutItem free list to avoid `new FlexLayoutItem()` per child per
        // FlexBox. A FlexBox's items are exactly one-to-one with its block-level
        // children; rented at the start of FlexLayout and returned at the end.
        public readonly Stack<FlexLayoutItem> FlexItemPool = new(16);

        public FlexLayoutItem RentFlexItem() {
            return FlexItemPool.Count > 0 ? FlexItemPool.Pop() : new FlexLayoutItem();
        }

        public void ReturnFlexItem(FlexLayoutItem it) {
            it.Reset();
            FlexItemPool.Push(it);
        }
    }

    // Lifted out of FlexLayout's nested type so LayoutScratch can hold a typed
    // list of these without FlexLayout having to expose its private nested type.
    // Same fields as the old private FlexLayout.Item.
    internal sealed class FlexLayoutItem {
        public BlockBox Box;
        public FlexItemProperties Props;
        public int DocOrder;

        public double FlexBaseSize;
        public double HypotheticalMainSize;
        public double TargetMainSize;
        public double OuterMainMarginSum;
        public double LeadMainMargin;
        public double LeadCrossMargin;
        public double TrailCrossMargin;
        // CSS Flexbox §8.1: items can declare `margin-*: auto` on either
        // main-axis edge to absorb free space ahead of justify-content.
        // BlockLayout resolves the keyword to 0px on the Box (so the rest
        // of the engine sees a concrete margin), which means FlexLayout
        // must remember the keyword separately to distribute free space
        // correctly. Without these flags `margin-top: auto` on a column-
        // flex item is silently dropped — e.g. `.primary-row { margin-top:
        // auto }` in the HUD failed to pin Strike/Block to the bottom of
        // `.actions`.
        public bool LeadMainMarginAuto;
        public bool TrailMainMarginAuto;
        // CSS Flexbox §8.1/§8.2: same "auto" tracking for the cross axis.
        // `margin-top: auto` on a row-flex item absorbs cross free space
        // and pins the item to the bottom; `margin-bottom: auto` pins it
        // to the top; both auto center the item. Cross auto margins take
        // effect BEFORE align-self, making align-self a no-op when any
        // cross auto margin consumed free space.
        public bool LeadCrossMarginAuto;
        public bool TrailCrossMarginAuto;
        public double CrossSize;
        public double MainPos;
        public double CrossPos;

        public void Reset() {
            Box = null;
            Props = default;
            DocOrder = 0;
            FlexBaseSize = 0; HypotheticalMainSize = 0; TargetMainSize = 0;
            OuterMainMarginSum = 0; LeadMainMargin = 0;
            LeadCrossMargin = 0; TrailCrossMargin = 0;
            LeadMainMarginAuto = false; TrailMainMarginAuto = false;
            LeadCrossMarginAuto = false; TrailCrossMarginAuto = false;
            CrossSize = 0; MainPos = 0; CrossPos = 0;
        }
    }
}
