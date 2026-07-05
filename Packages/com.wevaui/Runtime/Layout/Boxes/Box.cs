using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Positioning;
using Weva.Layout.Scrolling;
using Weva.Paint.Conversion;

namespace Weva.Layout.Boxes {
    public abstract class Box {
        public Element Element { get; internal set; }
        public ComputedStyle Style { get; internal set; }

        // Per-box paint command cache (set by BoxToPaintConverter on miss; consulted
        // on subsequent Convert passes). Stored as a plain field so the converter
        // hot path can hit/miss without a Dictionary lookup. Cleared by ResetForPool
        // when a Box instance is recycled, and by the converter's invalidation paths
        // when the layout/style version changes. See PaintBoxCache for the box-local-
        // coords contract.
        public PaintBoxCache PaintCache;

        // BoxToPaintConverter caches per-subtree predicates here so the
        // O(N²) recursive walks in SubtreeContainsTextRun /
        // SubtreeContainsScrollState collapse to O(1) lookups during the
        // hot VisitBox loop. Populated by PrecomputeSubtreeFlags at the
        // top of every Convert; bumped when boxes are recycled.
        public bool SubtreeHasTextRun;
        public bool SubtreeHasScrollState;

        // Per-box cache for the resolved INPUTS to EmitWrappersFresh's RentPushXxx
        // calls (filter / transform / mask / opacity / mix-blend-mode). Separate
        // from PaintCache because the wrapper inputs are keyed on the FULL
        // style.Version + abs position (PaintBoxCache uses DecorationVersion + box-
        // local coords so it deliberately ignores wrapper-only ticks and ancestor
        // moves). See WrapperEmitCache for the hit-condition contract. Stays null
        // until the first EmitWrappersFresh call that touches a box with at least
        // one non-default wrapper property — the fast `!HasWrapperProperties`
        // early-out never allocates a cache.
        public WrapperEmitCache WrapperCache;

        // Mirrors ComputedStyle.Version. Initialized to 0 on a freshly-constructed
        // Box; LayoutEngine bumps this via BoxVersion.Next() each time it freshly
        // computes the box's layout. A cached box that survives a re-layout pass
        // keeps its existing Version. Used by the incremental layout cache as part
        // of the key for parent boxes (ChildAggregateVersion) so a parent reruns
        // when any descendant's box changes.
        public long Version { get; internal set; }
        internal int PoolSurvivorMark { get; set; }

        // True while this instance sits on a BoxPool free list. Set by
        // BoxPool.PushToFree, cleared on allocation; guards against the same
        // box being pushed twice (mid-pass Recycle + EndPass) and then handed
        // out to two tree positions. Not touched by ResetForPool — the pool
        // owns this flag's lifecycle.
        internal bool InFreeList;

        // Diagnostic: true when this box instance was returned to the caller as
        // the survivor from a previous Layout pass that the IncrementalLayoutGate
        // skipped. Reset to false at the start of every full Layout pass.
        public bool IsCachedFromLastFrame { get; internal set; }

        // Scroll-boundary content reuse (LayoutEngine.EnableScrollBoundaryReuse).
        // Set on a scroll-container box whose CONTENT layout was carried over
        // verbatim from the previous frame because its content-box width and
        // subtree fingerprint are unchanged. The layout passes honour it as a
        // skip: BlockLayout.LayoutContent leaves the (already-laid-out, parent-
        // relative) children in place, and AnalyzeLayoutFeatures does not descend
        // into the subtree (so its flex/grid/table boxes are not re-collected /
        // re-run). The container box's OWN outer geometry (X/Y/Width/Height) is
        // still assigned by its parent; only the internal content is frozen.
        // Cleared by ResetForPool. A scroll container is a natural boundary for
        // this: its content is laid out at intrinsic size and clipped/scrolled,
        // so a change to the height it is GIVEN never reflows what is inside it.
        public bool ReuseContent { get; internal set; }

        // Per-box layout-input fingerprint set by Reconcile when the box is
        // fully laid out. The subtree-skip path consults this digest directly
        // (no Dictionary lookup) to decide whether the box's geometry from
        // the previous pass can be reused. Empty (default) on a freshly-pooled
        // box; ResetForPool clears it. Mirrors the field-set the engine's
        // per-Element LayoutCacheKey uses, exposed at Box-granularity.
        public LayoutDigestKey CachedDigest { get; internal set; }

        public double X { get; internal set; }
        public double Y { get; internal set; }
        public double Width { get; internal set; }
        public double Height { get; internal set; }

        // First-pass content-derived cross size. Stamped ONCE by BlockLayout
        // on the very first FinalizeBlockSize call (when this field is still
        // 0); subsequent re-layouts via RelayoutContentAt skip the stamp so
        // a flex-stretched Height doesn't masquerade as "pre-flex". Used by
        // PositioningPass.FlexIntrinsicCross when an outer pass asks
        // "what's this row-flex item's intrinsic cross?" — reading the
        // first-pass value defeats the multi-pass convergence bug (audit
        // #19b / #280) where a post-stretch Height feeds back into the
        // next pass's intrinsic computation. 0 means "not yet stamped"
        // (fresh-from-pool or width changed since last stamp).
        public double PreFlexCrossHeight { get; internal set; }

        // Populated by PositioningPass. Defaults: Static / nulls (auto) / null (auto).
        public PositionType Position { get; internal set; }
        public double? OffsetTop { get; internal set; }
        public double? OffsetRight { get; internal set; }
        public double? OffsetBottom { get; internal set; }
        public double? OffsetLeft { get; internal set; }
        public int? ZIndex { get; internal set; }

        public double MarginTop { get; internal set; }
        public double MarginRight { get; internal set; }
        public double MarginBottom { get; internal set; }
        public double MarginLeft { get; internal set; }

        public double PaddingTop { get; internal set; }
        public double PaddingRight { get; internal set; }
        public double PaddingBottom { get; internal set; }
        public double PaddingLeft { get; internal set; }

        public double BorderTop { get; internal set; }
        public double BorderRight { get; internal set; }
        public double BorderBottom { get; internal set; }
        public double BorderLeft { get; internal set; }

        // Scroll offset for boxes that act as scroll containers (overflow != visible).
        // Mutated by ScrollEventHandler / programmatic ScrollTo. The paint converter
        // applies (-ScrollX, -ScrollY) to descendant emit positions; the hit tester
        // does the inverse when descending into this box. ScrollLayout writes
        // ScrollWidth/ScrollHeight onto the linked ScrollState, not onto Box itself.
        public double ScrollX { get; internal set; }
        public double ScrollY { get; internal set; }

        // CSS Overflow L3 §3 — direct per-box scroll state reference.
        // Lazily allocated: null for boxes where overflow is visible (the
        // common case). Set by ScrollLayout.Run after layout; cleared in
        // ResetForPool so pooled boxes never carry stale scroll state into
        // a future layout pass. Read by game code via box.ScrollState.ScrollLeft/Top.
        //
        // Design: stored directly on Box (O(1) field access, no Dictionary lookup)
        // rather than through ScrollContainer so game code at arbitrary call sites
        // can access the state without referencing the engine's ScrollContainer.
        // ScrollContainer remains the authoritative dictionary used by
        // ScrollLayout / ScrollEventHandler / BoxToPaintConverter; this field
        // is the per-box "fast path" accessor (always kept in sync by ScrollLayout).
        public ScrollState ScrollState { get; internal set; }

        // CSS Overflow L3 §3 — true when this box is a scroll container.
        // Per spec: overflow auto / scroll / hidden all establish a scroll
        // container (hidden clips without scrollbar UI but is still scrollable
        // programmatically). overflow clip and overflow visible do NOT.
        //
        // This is computed lazily from the style on each call — no cached field —
        // because it is consulted rarely (typically only by game code or tests).
        // Hot paths (ScrollLayout, ScrollEventHandler) use ScrollContainerLookup
        // or per-style checks directly and are unaffected.
        public bool IsScrollContainer {
            get {
                if (Style == null) return false;
                // Per CSS Overflow L3 §3: longhands preferred; fall back to shorthand.
                var xv = Style.GetParsed(CssProperties.OverflowXId);
                var yv = Style.GetParsed(CssProperties.OverflowYId);
                if (xv == null && yv == null) {
                    var sv = Style.GetParsed(CssProperties.OverflowId);
                    return IsScrollContainerOverflow(sv);
                }
                return IsScrollContainerOverflow(xv) || IsScrollContainerOverflow(yv);
            }
        }

        static bool IsScrollContainerOverflow(Weva.Css.Values.CssValue v) {
            string name = null;
            if (v is Weva.Css.Values.CssKeyword k)    name = k.Identifier;
            else if (v is Weva.Css.Values.CssIdentifier id) name = id.Name;
            else return false;
            // hidden/scroll/auto → scroll container; clip/visible → not.
            switch (name) {
                case "hidden":
                case "scroll":
                case "auto":
                    return true;
                default:
                    return false;
            }
        }

        public double IntrinsicWidth { get; internal set; }
        public double IntrinsicHeight { get; internal set; }

        // Sticky offset written by StickyResolver each time scroll position changes.
        // The paint converter applies (StickyOffsetX, StickyOffsetY) on top of the
        // box's natural origin; layout coordinates (X/Y) remain at the in-flow
        // position so other layout passes still reason about static placement.
        public double StickyOffsetX { get; internal set; }
        public double StickyOffsetY { get; internal set; }

        protected readonly List<Box> children = new();
        public IReadOnlyList<Box> Children => children;
        internal List<Box> ChildList => children;

        public Box Parent { get; internal set; }

        public void AddChild(Box child) {
            if (child == null) return;
            child.Parent = this;
            children.Add(child);
        }

        // Inserts `child` at position 0, pushing existing children to higher
        // indices. Used by AttachInlineFragmentsToLines so InlineBox fragments
        // appear BEFORE the TextRuns they cover in the walker's DFS order —
        // the first box to claim an Element is the principal box, so the
        // InlineBox (which carries the correct full-span width including
        // pseudo-element runs) wins over any same-element TextRun.
        internal void InsertChildFirst(Box child) {
            if (child == null) return;
            child.Parent = this;
            children.Insert(0, child);
        }

        internal void ClearChildren() {
            foreach (var c in children) c.Parent = null;
            children.Clear();
        }

        internal void ReplaceChild(int index, Box replacement) {
            if (index < 0 || index >= children.Count) return;
            var old = children[index];
            if (old == replacement) return;
            old.Parent = null;
            replacement.Parent = this;
            children[index] = replacement;
        }

        internal void RemoveChildAt(int index) {
            if (index < 0 || index >= children.Count) return;
            var c = children[index];
            c.Parent = null;
            children.RemoveAt(index);
        }

        public double ContentWidth => Width - PaddingLeft - PaddingRight - BorderLeft - BorderRight;
        public double ContentHeight => Height - PaddingTop - PaddingBottom - BorderTop - BorderBottom;

        // Wipes every field back to its construction-default so the instance can be
        // handed back to a free-list and resurrected by a future Build* call. Keeps
        // the children List<Box> backing array allocated (Clear is O(count) but does
        // not free buckets) — that's the whole point of pooling. Subclasses override
        // to additionally reset their own fields.
        //
        // We deliberately DON'T touch each child's Parent pointer: the layout
        // incremental cache resurrects cached child boxes via Reconcile.ReplaceChild,
        // which transiently re-parents them onto a freshly-built box that we are
        // about to recycle. The cached child still belongs to its original cached
        // parent in the survivor tree; clearing its Parent here would zero out the
        // pointer the survivor tree expects to see.
        // Bumped on every pool recycle. Instance-keyed caches (ScrollContainer)
        // validate this against the generation they recorded at link time: a
        // recycled instance re-rented as a DIFFERENT box otherwise resurrects
        // the dead entry — live find: a wheel-scrolled offset (1480px)
        // reappeared on a random filler div's state after its original box was
        // recycled mid-hunt for the typing-scrolls-to-top bug.
        internal int PoolGeneration;

        internal virtual void ResetForPool() {
            // Notify subscribers BEFORE we null Element — consumers (e.g.
            // BoxToPaintConverter's subtree-snapshot dictionary) key off the
            // Box instance and need a chance to drop their entries while the
            // Box is still identifiable.
            Recycled?.Invoke(this);
            PoolGeneration++;
            Element = null;
            Style = null;
            Version = 0;
            PoolSurvivorMark = 0;
            IsCachedFromLastFrame = false;
            ReuseContent = false;
            CachedDigest = LayoutDigestKey.Empty;
            PaintCache = null;
            WrapperCache = null;
            SubtreeHasTextRun = false;
            SubtreeHasScrollState = false;
            X = 0; Y = 0; Width = 0; Height = 0;
            PreFlexCrossHeight = 0;
            Position = default;
            OffsetTop = null; OffsetRight = null; OffsetBottom = null; OffsetLeft = null;
            ZIndex = null;
            MarginTop = 0; MarginRight = 0; MarginBottom = 0; MarginLeft = 0;
            PaddingTop = 0; PaddingRight = 0; PaddingBottom = 0; PaddingLeft = 0;
            BorderTop = 0; BorderRight = 0; BorderBottom = 0; BorderLeft = 0;
            ScrollX = 0; ScrollY = 0;
            // Clear the per-box ScrollState so a recycled box doesn't present
            // stale overflow metrics to game code. ScrollLayout re-assigns this
            // after each layout pass for boxes that remain scroll containers.
            if (ScrollState != null) { ScrollState.OwnerBox = null; ScrollState = null; }
            StickyOffsetX = 0; StickyOffsetY = 0;
            IntrinsicWidth = 0; IntrinsicHeight = 0;
            children.Clear();
            Parent = null;
        }

        // Process-global "this Box was just recycled to its pool" notification.
        // Consumed by BoxToPaintConverter to drop any cached BoxBatchSnapshot
        // keyed on the recycled instance — without this, a re-allocated Box
        // (different Element, different subtree) would replay the OLD
        // snapshot. Subscribers must keep their handler closure cheap;
        // this fires on every layout teardown for every retired box.
        internal static System.Action<Box> Recycled;
    }
}
