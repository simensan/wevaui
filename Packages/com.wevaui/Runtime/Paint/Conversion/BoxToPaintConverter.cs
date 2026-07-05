using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using Weva.Layout.Positioning;
using Weva.Layout.Scrolling;
using Weva.Layout.Multicol;
using Weva.Layout.Tables;
using Weva.Paint.Filters;
using Weva.Paint.Images;
using Weva.Profiling;
using Weva.Reactive;
using CssValuePool = Weva.Css.Values.CssValuePool;

namespace Weva.Paint.Conversion {
    // Walks a laid-out Box tree in document order (which is also paint order in v1 — no z-index,
    // no positioned, no stacking contexts yet) and emits PaintCommands. The layout engine writes
    // Box.X/Y/Width/Height as the BORDER-BOX rect regardless of the author's `box-sizing` (the
    // box-sizing distinction lives entirely in ApplyBoxModel / FinalizeBlockSize when converting
    // the author's `width` / `height` declarations into outer dimensions). So the outer rect a
    // painter consumes is simply Rect(X, Y, W, H) and already includes padding + border.
    //
    // Per-box paint cache (PLAN §12): each non-text, non-scroll Box gets a PaintBoxCache holding
    // ITS OWN decoration commands (NOT inlined descendants) in BOX-LOCAL coordinates. Children
    // are recursed independently every Convert. On a hit we rent translated copies of the cached
    // commands; on a miss we recompute via the from-scratch path, build a fresh cache, and rent
    // translated copies. This makes a parent's layout move free for descendants whose own state
    // is unchanged: only the parent's own cache misses; every descendant hits and just retranslates.
    public sealed class BoxToPaintConverter {
        readonly LengthContext baseLengthContext;
        readonly Func<ComputedStyle, FontHandle> fontResolver;
        readonly PaintConverterPools pools = new();
        readonly PaintCommandPool commandPool = new();
        readonly PaintListPool listPool = new();

        // When set, BackgroundResolver consults the registry for an image's
        // intrinsic size so background-size / background-position / repeat
        // produce a tiled BackgroundTile instead of stretching across the
        // box. Set by host code (WevaDocument or test harness) at startup.
        public Weva.Paint.Images.IImageRegistry ImageRegistry { get; set; }

        // B16 — path-coverage rasterization cache. Lazily populated when the first
        // clip-path: path() element is encountered. Lives for the converter's lifetime
        // (i.e. the document's lifetime), so rasterized coverage images survive across
        // frames. Eviction story: v1 grows per unique (shapeHash, width, height) triple;
        // in practice clip-path shapes are static CSS, so the per-element steady-state
        // is one entry. See PathCoverageCache for details.
        PathCoverageCache _pathCoverageCache;

        // B16 — companion in-memory registry that the GPU path reads coverage images
        // from via the synthetic mask handle. We maintain our own InMemoryImageRegistry
        // so we can Register() synthetic handles without touching the author's registry
        // (which may be a read-only AddressablesImageRegistry or similar). The UIBatcher
        // needs the registry to bind _WevaMaskImage per batch; the converter hands it
        // as SyntheticImageRegistry (distinct from the author's ImageRegistry).
        //
        // This field is only non-null when the document has at least one clip-path: path()
        // element. The GPU path (UIBatcher) checks it at submit time.
        InMemoryImageRegistry _syntheticImageRegistry;

        // B16 — access for UIBatcher / test code.
        public InMemoryImageRegistry SyntheticImageRegistry => _syntheticImageRegistry;

        // B16 — one-time warning gate to prevent per-frame console spam when a
        // document has clip-path: path() alongside 4 author mask-image layers.
        bool _pathMaskOverflowWarned;

        // B16 — ensures the lazy registry and cache are initialized.
        PathCoverageCache EnsurePathCoverageCache() {
            if (_pathCoverageCache == null) {
                _pathCoverageCache = new PathCoverageCache();
                _syntheticImageRegistry = new InMemoryImageRegistry();
            }
            return _pathCoverageCache;
        }

        // Resolves the ::placeholder pseudo-element style for an `<input>` host.
        // UIDocumentBuilder wires this to CascadeEngine.ComputePlaceholder so
        // author rules (color, font, etc.) flow into the placeholder text. When
        // null, the converter still paints placeholder text using a faded
        // currentColor fallback.
        public Func<Element, ComputedStyle> PlaceholderStyleOf { get; set; }

        // Returns the focused input's caret + selection geometry (px offsets from
        // the content-box text start) so EmitInputOverlays can paint a caret bar
        // and a selection highlight. Null when `element` is not the focused
        // editable. Wired by UIDocumentBuilder to the focused InputController's
        // TextEditModel; measured with the layout font metrics so the caret aligns
        // with the painted value text.
        public Func<Element, InputCaretGeometry?> InputCaretOf { get; set; }

        // Resolves the cascaded ::selection style for the host element so the
        // selection highlight honors `::selection { background-color }`. Null →
        // the UA-default highlight color is used. Wired by UIDocumentBuilder.
        public Func<Element, ComputedStyle> SelectionStyleOf { get; set; }

        public readonly struct InputCaretGeometry {
            public readonly double CaretX;          // px from content-box text start
            public readonly bool HasSelection;
            public readonly double SelectionStartX;
            public readonly double SelectionEndX;
            public readonly bool CaretVisible;      // false during the caret's blink-off phase
            // Input/selection audit #7: the controller's PERSISTENT horizontal
            // edit-scroll. NaN = not provided (bare-test callers) → the
            // painter falls back to the old stateless caret-follow derivation.
            public readonly double ScrollX;
            public InputCaretGeometry(double caretX, bool hasSelection, double selStartX, double selEndX,
                                      bool caretVisible = true, double scrollX = double.NaN) {
                CaretX = caretX;
                HasSelection = hasSelection;
                SelectionStartX = selStartX;
                SelectionEndX = selEndX;
                CaretVisible = caretVisible;
                ScrollX = scrollX;
            }
        }

        // Multiline caret/selection geometry for a focused <textarea>
        // (input/selection audit #6). All coordinates border-box-relative to
        // the textarea. Null for unfocused/unmapped textareas. Wired by
        // UIDocumentBuilder off the FormControls registry + TextAreaCaretMap.
        public struct TextAreaOverlayGeometry {
            public List<(double X, double Y, double W, double H)> SelectionRects;
            public double CaretX, CaretY, CaretHeight;
            public bool CaretVisible;
        }
        public Func<Element, TextAreaOverlayGeometry?> TextAreaOverlayOf { get; set; }

        // CascadeEngine that holds WebKit scrollbar pseudo-element rules.
        // When set, ScrollbarPaint.Emit queries ::-webkit-scrollbar(-thumb|-track)
        // for each scrolling box and applies the resolved styles (colors, thickness,
        // border-radius) in place of CSS Scrollbars L1 per Chrome's precedence rule:
        // if ANY webkit scrollbar rule matches, L1 properties are ignored for that box.
        // UIDocumentBuilder wires this to the document's CascadeEngine on startup.
        public Weva.Css.Cascade.CascadeEngine ScrollbarCascade { get; set; }
        // Tracks every Box whose PaintCache field this converter has populated, so
        // InvalidateAll can null the field on each. Without this list we'd leak
        // references from Box → cache → commands across InvalidateAll calls (the
        // per-Box field is the only owner since we dropped the dictionary index).
        readonly HashSet<Box> cachedBoxes = new();
        // Pool of PaintBoxCache wrappers. Invalidate(box) used to NULL out
        // box.PaintCache, forcing the next cache-miss path to `new PaintBoxCache()`
        // every frame for every animating box (style.Version bumped each tick =
        // Apply invalidates the cache = miss = alloc). On the gem-grid scene
        // that was ~65 PaintBoxCache + 130 inner List<> instances per frame,
        // ~5 KB / frame. Returning the wrapper to a pool instead and renting
        // it back on miss keeps the steady state alloc-free.
        readonly Stack<PaintBoxCache> paintBoxCachePool = new(64);
        // Pool of WrapperEmitCache wrappers — see PaintBoxCache pool comment
        // above for the same alloc-shaving rationale. Wrappers are reused
        // across boxes when a Box's WrapperCache is dropped (cascade marks
        // the style without wrapper props, or Box ResetForPool).
        readonly Stack<WrapperEmitCache> wrapperCachePool = new(64);
        // Mirror of cachedBoxes for the WrapperEmitCache field so
        // InvalidateAll can null the field on every box that this converter
        // ever stamped a wrapper cache onto. Without this, a converter
        // serving a long-lived document leaks WrapperEmitCache references
        // through Box.WrapperCache across InvalidateAll.
        readonly HashSet<Box> wrapperCachedBoxes = new();
        // Test-visible counter: increments every time EmitWrappersFresh
        // actually runs the FilterResolver / TransformResolver / MaskResolver
        // / OpacityResolver / MixBlendModeResolver bundle (i.e. on a wrapper-
        // cache miss). The fast `!HasWrapperProperties` early-out and the
        // wrapper-cache hit path do NOT bump this counter. Tests assert that
        // unchanged frames re-hit instead of re-resolving (P9).
        internal long wrapperResolveCount;
        public long WrapperResolveCount => wrapperResolveCount;
        // Pair-counter for hits — useful in tests to disambiguate "we ran
        // EmitWrappersFresh and hit the cache" from "we never called it".
        internal long wrapperCacheHits;
        public long WrapperCacheHits => wrapperCacheHits;
        long contextVersion = 1;
        // Counts DrawBackdropFilter emissions in the current Convert pass.
        // Used to extend ExactSrgbSourceOver (mode-17) to non-backdrop-filter
        // elements ONLY when the document already has at least one backdrop-filter
        // element (so the backdrop-copy RT is already being allocated). Resets to
        // 0 at the start of each Convert call. See EmitVisibleDecorations.
        int backdropFilterSeenThisConvert;

        // Per-box subtree snapshots used by the batch-replay fast path. When a
        // subtree's PaintBoxCache is valid AND none of its descendants are in
        // this frame's InvalidationTracker.GetDirty set AND the batcher's
        // parent state matches the snapshot's captured parent state, the
        // painter splices the snapshot's instances back into the current
        // batch list instead of walking the subtree and re-Submitting every
        // command. Targets the 7+ ms BatchedURPRenderBackend.Submit cost on
        // active frames where most of the document is paint-stable but a few
        // elements animate.
        readonly Dictionary<Box, IBoxBatchSnapshot> subtreeSnapshots
            = new Dictionary<Box, IBoxBatchSnapshot>();

        // Elements whose subtree contains at least one dirty descendant.
        // Computed once per Convert from the tracker's dirty set by walking
        // each dirty element's parent chain. Replayable subtrees are those
        // whose Box.Element is NOT in this set.
        readonly HashSet<Element> dirtyAncestors = new HashSet<Element>(32);
        readonly List<Box> snapshotEvictScratch = new List<Box>(16);

        // Nesting depth of in-flight BeginSubtreeCaptureCommand emissions.
        // Painter only opens an outer capture per clean subtree — inner
        // clean subtrees within an outer capture become part of that
        // capture's snapshot. This avoids cascading per-box snapshots when
        // the WHOLE document is clean (the root subtree captures everything;
        // no need to ALSO snapshot every descendant). On the next frame the
        // outer snapshot is the largest replayable unit.
        int captureDepth;
        long cacheHits;
        long cacheMisses;

        // Paint-level column fragmentation (MULTICOL-FRAG v1):
        // When VisitChildrenMulticol is painting a block child across multiple
        // columns it sets this flag so VisitBox skips snapshot replay for that
        // child. A snapshot from a prior frame captures the child at its single
        // column anchor; replaying it without the clip+translate wrappers that
        // the multicol slicer emits would paint at the wrong position and skip
        // the per-column clip entirely.  Force a fresh paint every time a child
        // is being column-sliced; the batcher will capture a new snapshot on
        // the column-0 slice if captureDepth == 0.
        bool insideColumnSlice;

        // Scratch buffer reused on every cache miss to materialize the box's own
        // commands at box-local coords. Owned by the converter so steady-state
        // miss paths don't allocate a fresh List per box. Pre-sized to 16 —
        // a fully-decorated box (filter + transform + opacity + 1-3 shadows +
        // 1-3 background layers + border + outline + clip) tops out around
        // a dozen entries; pre-allocating skips the 4→8→16 reallocations on
        // the first miss after a warm-up.
        readonly List<PaintCommand> scratchPre = new(16);

        // Exposed so callers (WevaDocument lifecycle, tests verifying parity) can
        // return commands they consumed back into the converter's pool. Returning
        // a list parks every command's instance for re-use on the next Convert.
        // Skipping the return is safe — the GC will collect — but defeats the
        // pool's purpose.
        public PaintCommandPool CommandPool => commandPool;
        public PaintListPool ListPool => listPool;

        // Opt-in: allow snapshot capture/replay on text-bearing subtrees.
        // Default false — preserves the legacy "text subtrees always
        // re-submit" behavior that PaintIncrementalTests.Text_subtree_
        // never_replays_retained_batch_snapshot pins. Render backends that
        // implement IValidatedBoxBatchSnapshot with text-atlas tracking
        // (URP's BoxBatchSnapshot checks SdfTextRendering.CurrentAtlas
        // Version) flip this to true to unlock the biggest paint-warm-
        // flip win — without it, every card subtree with a label re-walks
        // every frame, blowing the per-frame paint budget on idle UIs.
        public bool AllowTextSubtreeSnapshotReplay { get; set; }

        // Opt-in: allow snapshot capture/replay on subtrees INSIDE a scroll
        // container's content region. Default false — scroll content was
        // historically excluded outright (conservative). It is safe to replay a
        // CLEAN scroll-content subtree because: (a) subtreeIsClean already means
        // no descendant reflowed, and (b) the replay's (absX-anchorX) translate
        // absorbs any scroll-offset change (the snapshot's anchored positions
        // shift with the scroll). The clip is re-pushed by the scroll container
        // before the content paints, so the replay is clipped correctly. This
        // unlocks retained paint for a large static scrollable region (e.g. a
        // grid inside overflow-y:auto) whose siblings animate — the dominant
        // per-frame paint cost on such layouts. Pairs with the layout-side
        // scroll-boundary reuse (LayoutEngine.EnableScrollBoundaryReuse).
        public bool AllowScrollContentSnapshotReplay { get; set; }

        // Optional viewport dimensions for content-visibility:auto off-screen
        // relevance check.  When both are > 0 the painter skips descendant paint
        // for `content-visibility:auto` boxes whose border box is entirely outside
        // the viewport rect (0, 0, ViewportWidth, ViewportHeight) at paint time.
        // Leave at 0 (default) to disable the viewport skip (all auto boxes render
        // their descendants, containment still applies).
        //
        // These also feed LengthContextFor so paint-time vh/vw lengths
        // (border-radius, box-shadow blur, filters…) resolve against the live
        // viewport. The per-box paint cache keys on (box.Version,
        // DecorationVersion, contextVersion) — NOT the viewport — so a box whose
        // size is unchanged by a resize (e.g. a fixed-px box with a vh shadow)
        // would otherwise serve a stale, wrong-sized decoration. Bumping
        // contextVersion on a real change invalidates those caches so the
        // decoration re-resolves. WevaDocument re-pushes the same value every
        // frame, hence the equality guard — only an actual resize pays the cost.
        double viewportWidth, viewportHeight;
        public double ViewportWidth {
            get => viewportWidth;
            set { if (value != viewportWidth) { viewportWidth = value; contextVersion++; } }
        }
        public double ViewportHeight {
            get => viewportHeight;
            set { if (value != viewportHeight) { viewportHeight = value; contextVersion++; } }
        }

        // B3e — exact sRGB source-over for backdrop-filtered translucent
        // background-colors (internal MixBlendMode.ExactSrgbSourceOver, 17).
        //
        // DEFAULT ON — verified game-view-true at a VIEWPORT-MATCHED Chrome
        // reference on glass.html: flag off = mean abs err 7.8 (the 0.16-lift
        // approximation), flag on measured on DEEP-IDLE DrawCache frames =
        // 5.0. The per-batch NeedsBackdropRefresh blits keep _WevaBackdrop
        // fresh on idle replay frames too.
        //
        // History (it matters): this flag was gated off twice on "regression"
        // measurements that were ALL artifacts — (1) a domain reload reverted
        // the scene's documentAsset to a different demo page, (2)+(3) the
        // game view was resized (1279x733 -> 1559x829) so fixed measurement
        // coordinates + the stale Chrome reference sampled page background
        // instead of the glass strip ("flat dark" readings). The one REAL bug
        // was ReplayTranslated handing cache-owned blend pushes to the live
        // list (pool Reset corrupted the cache after frame 1) — fixed and
        // pinned by the cache-replay survival tests. Before trusting any
        // future glass measurement: re-shoot the Chrome reference at the
        // CURRENT game-view resolution and assert capture sizes match.
        public static bool EnableExactSrgbGlassCompositing = true;

        public BoxToPaintConverter() : this(LengthContext.Default, null) { }

        public BoxToPaintConverter(LengthContext lengthContext) : this(lengthContext, null) { }

        public BoxToPaintConverter(LengthContext lengthContext, Func<ComputedStyle, FontHandle> fontResolver) {
            this.baseLengthContext = lengthContext;
            this.fontResolver = fontResolver;
            // Subscribe to the global Box-recycled notification so we can
            // drop snapshot entries when the layout pool retires a box.
            // Without this, the subtreeSnapshots dictionary holds references
            // to recycled Boxes — leaking the snapshot's pooled
            // UIQuadInstance[] arrays AND risking a stale snapshot replay
            // if the Box gets re-allocated for a different element.
            boxRecycledHandler = OnBoxRecycled;
            Box.Recycled += boxRecycledHandler;
        }

        readonly Action<Box> boxRecycledHandler;

        void OnBoxRecycled(Box box) {
            if (box == null) return;
            if (subtreeSnapshots.TryGetValue(box, out var snap)) {
                snap?.Recycle();
                subtreeSnapshots.Remove(box);
            }
            // PA3: drop any per-box subtree-count memo so a re-allocated
            // Box instance (potentially serving a different element later)
            // cannot inherit a stale cached count from its predecessor.
            subtreeCountCache.Remove(box);
            // PaintCache returns to its own pool via Invalidate; the
            // recycle hook is only for state the converter keeps that
            // out-survives a single layout pass.
        }

        ~BoxToPaintConverter() {
            // Best-effort unsubscribe so a never-Dispose'd converter doesn't
            // peg a process-wide event. Most consumers hold the converter
            // for the document's lifetime, but tests construct + drop them.
            if (boxRecycledHandler != null) Box.Recycled -= boxRecycledHandler;
        }

        internal int CacheSize => cachedBoxes.Count;
        internal long CacheHits => cacheHits;
        internal long CacheMisses => cacheMisses;
        public long ContextVersion => contextVersion;

        // Diagnostics for the subtree-snapshot dictionary. The painter's
        // RegisterSubtreeSnapshot deposits one entry per clean subtree it
        // captures; without the recycle hook (OnBoxRecycled, above) the
        // dictionary would accumulate references to retired Boxes
        // indefinitely. Exposed for tests verifying the recycle-eviction
        // contract (MS4).
        public int SubtreeSnapshotCount => subtreeSnapshots.Count;
        public bool HasSubtreeSnapshot(Box box) => box != null && subtreeSnapshots.ContainsKey(box);

        // Memoization for EstimateCommandCount: a full tree walk to size the
        // PaintList capacity is wasted work in the steady state when neither the
        // root box nor the contextVersion has changed since the last Convert.
        // We cache the (root, root.Version, contextVersion) triple plus the
        // resulting estimate. A single struct field — no allocation. The
        // InvalidateAll path bumps contextVersion, which auto-invalidates the
        // memo on the next call.
        struct EstimateCacheEntry {
            public Box Root;
            public long RootVersion;
            public long ContextVersion;
            public int Estimate;
            public bool Valid;
        }
        EstimateCacheEntry estimateCache;
        // PA3 — per-Box subtree-count memo. The top-level estimateCache misses
        // whenever the ROOT's Version bumps; in practice ChildAggregate
        // propagates ANY descendant version-bump up to the root, so the
        // top-level cache invalidates on every layout that touched a single
        // box anywhere in the document. Without per-box memoization,
        // CountWalk then re-visits every node — O(N) per frame on documents
        // with frequent subtree-relayout.
        //
        // The per-box entry caches `(Version, SubtreeCount)`: on a miss
        // CountWalk recursively sums children, but if a child's cached
        // entry's Version matches the live Version we read the cached
        // subtree count and skip the recursion for that branch. Net effect
        // on the typical animation frame (one element bumps, root re-bumps
        // via aggregate, every other subtree's Version is stable): we walk
        // only the dirty path from root to mutated leaves, not the whole
        // tree.
        //
        // Lifetime: entries land here on every walk and live until either
        // (a) the Box is recycled (cleared via OnBoxRecycled), or
        // (b) InvalidateAll wipes the dictionary alongside contextVersion++.
        // Both paths drop stale Box references so the dict cannot leak
        // across document rebuilds.
        readonly Dictionary<Box, (long Version, int SubtreeCount)> subtreeCountCache
            = new Dictionary<Box, (long, int)>(128);
        // Test-visible counter incremented per CountWalk node visit, so tests
        // can assert that a steady-state Convert skipped the tree walk entirely.
        long estimateWalkNodes;
        public long EstimateWalkNodes => estimateWalkNodes;

        public void ResetCacheStats() {
            cacheHits = 0;
            cacheMisses = 0;
            wrapperResolveCount = 0;
            wrapperCacheHits = 0;
        }

        public void Invalidate(Box box) {
            if (box == null) return;
            var existing = box.PaintCache;
            if (existing != null) {
                // Return the cache + its Lists to the pool instead of dropping
                // them to GC. The inner List<PaintCommand>s retain their
                // backing arrays — the next cache miss rents this wrapper
                // and refills the Lists in place.
                ReturnCachedCommands(existing);
                existing.Reset(0, 0, 0);
                if (paintBoxCachePool.Count < 256) paintBoxCachePool.Push(existing);
                box.PaintCache = null;
            }
            cachedBoxes.Remove(box);
            var wrapper = box.WrapperCache;
            if (wrapper != null) {
                wrapper.Invalidate();
                if (wrapperCachePool.Count < 256) wrapperCachePool.Push(wrapper);
                box.WrapperCache = null;
            }
            wrapperCachedBoxes.Remove(box);
        }

        public void InvalidateSubtree(Box root) {
            if (root == null) return;
            Invalidate(root);
            var children = root.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                InvalidateSubtree(children[i]);
            }
        }

        public void InvalidateAll() {
            foreach (var b in cachedBoxes) {
                if (b == null) continue;
                var c = b.PaintCache;
                if (c != null) {
                    ReturnCachedCommands(c);
                    c.Reset(0, 0, 0);
                    if (paintBoxCachePool.Count < 256) paintBoxCachePool.Push(c);
                }
                b.PaintCache = null;
            }
            cachedBoxes.Clear();
            foreach (var b in wrapperCachedBoxes) {
                if (b == null) continue;
                var w = b.WrapperCache;
                if (w != null) {
                    w.Invalidate();
                    if (wrapperCachePool.Count < 256) wrapperCachePool.Push(w);
                }
                b.WrapperCache = null;
            }
            wrapperCachedBoxes.Clear();
            // Also drop the subtree-snapshot cache. Each snapshot owns a
            // UIQuadInstance[] rented from ArrayPool — without Recycle()
            // those rentals leak (see BoxBatchSnapshot pooling contract).
            // InvalidateAll is called on bulk events (theme swap, viewport
            // resize, document rebuild); leaving the dictionary populated
            // also re-introduces the snapshot-staleness problem the rest
            // of the engine is now guarding against.
            if (subtreeSnapshots.Count > 0) {
                foreach (var kv in subtreeSnapshots) kv.Value?.Recycle();
                subtreeSnapshots.Clear();
            }
            // PA3: clear per-box subtree-count memo. The top-level
            // estimateCache is auto-invalidated by the contextVersion++
            // below, but the per-Box dict holds Box references that would
            // otherwise outlive bulk events (theme swap, viewport resize,
            // document rebuild) and risk serving counts against stale
            // Versions. Backing storage is retained.
            subtreeCountCache.Clear();
            contextVersion++;
            pools.Deactivate();
        }

        // Return cached PreChildren commands to the pool. Called before
        // PaintBoxCache.Reset clears the lists, and from InvalidateAll when
        // the painter drops every cache.
        //
        // PostChildren is skipped: under the wrapper/decoration split it
        // only ever contains the PopClip pop-singleton, which ReturnOne
        // would dispatch as a no-op (singletons have no fields to reset and
        // are never sourced from the pool). Iterating it would just walk
        // the list to no effect.
        void ReturnCachedCommands(PaintBoxCache cache) {
            if (cache == null) return;
            var pre = cache.PreChildren;
            for (int i = 0; i < pre.Count; i++) commandPool.ReturnOne(pre[i]);
        }

        // Drops cache entries for any Box whose corresponding Element is marked
        // dirty in the tracker for any kind that influences DECORATION output.
        // Per the box-local-coords contract, ancestor entries are NOT touched.
        //
        // `Paint`-only invalidations are deliberately excluded: the wrapper-
        // decoration split means transform/opacity/filter animations bump
        // ComputedStyle.Version (and hence the tracker's Paint bit) without
        // bumping DecorationVersion. PaintBoxCache.IsValid compares against
        // DecorationVersion, so leaving the cache in place lets the
        // wrapper-only frame stay a HIT — wrappers re-emit fresh at the
        // top of VisitBox, decorations replay from cache. A color or
        // background-color change DOES bump DecorationVersion via the
        // non-wrapper Set path, so cache.IsValid still returns false there
        // and forces a rebuild. The (Layout|Style|Structure) bits remain
        // because they signal changes that fall outside the wrapper set
        // (e.g. width animation, content swap, hover-driven background
        // change always co-occur with Style).
        public void Apply(InvalidationTracker tracker, Func<Element, Box> elementToBox) {
            if (tracker == null || elementToBox == null) return;
            // Paint is the WHOLE POINT of this method — when an element's
            // paint state changes (hover, color animation, gradient stop
            // shift), its cached decoration commands are stale and must be
            // dropped before the next Convert walks the tree. Earlier
            // versions of this mask omitted Paint, which silently broke
            // every hover/transition repaint and was caught only by the
            // PaintIncrementalTests {Apply_drops_entries_for_elements_marked_Paint,
            // Convert_with_tracker_applies_invalidation_before_walking}.
            //
            // PseudoClassState must be in the mask too. The earlier comment
            // claimed "hover-driven background change always co-occurs with
            // Style", but InteractionStateProvider.SetFlag only marks
            // PseudoClassState (it doesn't synthetically also mark Style).
            // Result: the cache survived the hover transition with the
            // pre-hover decoration list — typically NO background commands
            // because the un-hovered `.filter` had background:transparent
            // (an initial value, suppressed by IsNonDefaultDecorationValue).
            // After my UIDocumentLifecycle fix repointed Box.Style at the
            // post-hover ComputedStyle, the painter's NEXT Convert pass
            // read box.Style for text (live, no cache) but replayed cached
            // decoration commands for the background — so the hover color
            // updated and the hover background didn't. Adding
            // PseudoClassState here drops the cache so decorations re-emit
            // fresh against the post-hover style.
            const InvalidationKind kind = InvalidationKind.Paint
                                          | InvalidationKind.Composite
                                          | InvalidationKind.Layout
                                          | InvalidationKind.Style
                                          | InvalidationKind.Structure
                                          | InvalidationKind.PseudoClassState;
            // Composite-ONLY entries (wrapper-only animation ticks —
            // transform / opacity — and the tracker's ancestor propagation)
            // do NOT stale the element's cached DECORATION commands: the
            // wrappers are re-resolved fresh on every VisitBox and the
            // decorations don't read wrapper properties. They still count as
            // dirty for the snapshot machinery below (a subtree snapshot
            // covering the element bakes the OLD transform and must not
            // replay). Dropping the PaintBoxCache for them forced a full
            // EmitDecorations rebuild per animated element per frame —
            // particles.html paid 420 radial-gradient rebuilds every frame.
            const InvalidationKind cacheStaling = InvalidationKind.Paint
                                          | InvalidationKind.Layout
                                          | InvalidationKind.Style
                                          | InvalidationKind.Structure
                                          | InvalidationKind.PseudoClassState;
            bool anyDirty = false;
            foreach (var kv in tracker.DirtyEntries) {
                if ((kv.Value & kind) == 0) continue;
                if (kv.Key is Document) {
                    // Viewport resize and other document-level invalidations
                    // have no Element->Box mapping. Treat them as a global
                    // paint cache boundary; otherwise stale per-box caches and
                    // subtree batch snapshots can survive while layout builds a
                    // fresh box tree for the new viewport.
                    InvalidateAll();
                    return;
                }
                var e = DirtyElementFor(kv.Key);
                if (e != null) {
                    if ((kv.Value & cacheStaling) == 0) {
                        // Composite-only: decorations stay cached; just flag
                        // dirtiness so stale subtree snapshots get evicted.
                        anyDirty = true;
                        continue;
                    }
                    var b = elementToBox(e);
                    if (b == null) continue;
                    // An Element can map to MULTIPLE boxes in the layout tree —
                    // BoxBuilder produces an outer container box (FlexBox /
                    // BlockBox with the decoration rect: padding + border +
                    // background) AND an inner content box (the actual text /
                    // image content rect) for many display modes, sometimes
                    // with one or two ANONYMOUS wrapper boxes (Element=null)
                    // between them. ElementToBox.Lookup returns just one —
                    // typically the inner box — but the OUTER one owns the
                    // background and border cache. Walk up the parent chain,
                    // invalidating every ancestor that points at the SAME
                    // Element (passing transparently through anonymous
                    // wrappers whose Element is null). Stop when we hit a
                    // box whose Element is non-null AND different from `e`
                    // — that's a DOM ancestor, not part of this element's
                    // box group. Without this, hover on a flex-display
                    // button kept the outer FlexBox's cached background —
                    // the text-color refresh fired (TextRun cache cleared)
                    // but the bg never updated.
                    Box scope = b;
                    while (scope != null) {
                        if (scope.Element != null && !ReferenceEquals(scope.Element, e)) break;
                        if (ReferenceEquals(scope.Element, e)) Invalidate(scope);
                        scope = scope.Parent;
                    }
                    anyDirty = true;
                }
            }
            // Subtree snapshots are captured at ancestor levels and include
            // the dirty descendant's drawn output verbatim. Without this
            // eviction, EmitPaint runs after tracker.Clear() with empty
            // dirtyAncestors → every box looks clean → snapshot replay
            // re-draws the pre-dirty state (hover that never visibly
            // applies, value text that's painted but immediately covered by
            // the stale snapshot, etc). Evict only snapshots rooted at dirty
            // elements or their ancestors; unrelated branches keep their
            // retained batch chunks and can still replay on localized updates.
            if (anyDirty && subtreeSnapshots.Count > 0) {
                if (tracker.HasAny(InvalidationKind.Layout | InvalidationKind.Structure)) {
                    ClearSubtreeSnapshots();
                } else {
                    BuildDirtyAncestors(tracker);
                    EvictSnapshotsForDirtyAncestors();
                }
            }
        }

        void ClearSubtreeSnapshots() {
            foreach (var kv in subtreeSnapshots) kv.Value?.Recycle();
            subtreeSnapshots.Clear();
        }

        void EvictSnapshotsForDirtyAncestors() {
            snapshotEvictScratch.Clear();
            foreach (var kv in subtreeSnapshots) {
                var elem = kv.Key != null ? kv.Key.Element : null;
                if (elem != null && dirtyAncestors.Contains(elem)) {
                    snapshotEvictScratch.Add(kv.Key);
                }
            }
            for (int i = 0; i < snapshotEvictScratch.Count; i++) {
                var box = snapshotEvictScratch[i];
                if (!subtreeSnapshots.TryGetValue(box, out var snap)) continue;
                snap?.Recycle();
                subtreeSnapshots.Remove(box);
            }
            snapshotEvictScratch.Clear();
        }

        static Element DirtyElementFor(Node node) {
            if (node is Element e) return e;
            if (node is TextNode t) return t.Parent as Element;
            return null;
        }

        void BuildDirtyAncestors(InvalidationTracker tracker) {
            dirtyAncestors.Clear();
            if (tracker == null) return;
            const InvalidationKind dirtyMask =
                InvalidationKind.Paint
                | InvalidationKind.Composite
                | InvalidationKind.Layout
                | InvalidationKind.Style
                | InvalidationKind.Structure
                | InvalidationKind.PseudoClassState;
            foreach (var kv in tracker.DirtyEntries) {
                if ((kv.Value & dirtyMask) == 0) continue;
                var e = DirtyElementFor(kv.Key);
                if (e == null) continue;
                for (Node a = e; a != null; a = a.Parent) {
                    if (a is Element ae && !dirtyAncestors.Add(ae)) break;
                }
            }
        }

        public PaintList Convert(Box root) {
            return Convert(root, null, null, null, null, null);
        }

        public PaintList Convert(Box root, InvalidationTracker tracker, Func<Element, Box> elementToBox,
                                 ScrollContainer scrollContainer, IElementStateProvider stateProvider) {
            return Convert(root, tracker, elementToBox, scrollContainer, stateProvider, null);
        }

        // The output overload lets the caller own the PaintList lifetime — useful
        // for the document lifecycle which rents one PaintList per frame from
        // PaintListPool and feeds it to Convert each time. When `output` is null
        // the converter rents from its own listPool (caller still owns the
        // returned reference; they must Return() it when done to keep the steady
        // state allocation-free).
        public PaintList Convert(Box root, InvalidationTracker tracker, Func<Element, Box> elementToBox,
                                 ScrollContainer scrollContainer, IElementStateProvider stateProvider,
                                 PaintList output) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.PaintConvert)) {
                using var cssScope = CssValuePool.PassScope();
                // Reset per-pass recursion state. Defensive against an
                // exception mid-walk in a previous frame leaving visitDepth
                // non-zero (the try/finally in VisitBox normally guarantees
                // this, but a managed exception thrown from a deep callback
                // could in theory be swallowed at a different layer).
                visitDepth = 0;
                visitDepthWarned = false;
                // captureDepth has no try/finally around its inc/dec pair; an
                // exception escaping mid-walk left it >0 forever, which made
                // the `captureDepth == 0` gate refuse ALL future snapshot
                // captures — silently disabling the snapshot system for the
                // session. Reset alongside the other per-pass recursion state.
                captureDepth = 0;
                scrollContentDepth = 0;
                backdropFilterSeenThisConvert = 0;
                activeColorBrightness = 1.0;
                viewportScrollX = 0;
                viewportScrollY = 0;
                // Compute the per-Convert dirty-ancestors set: walk parents
                // from each dirty Element. A subtree whose root Element is
                // NOT in this set has no dirty descendants in this frame and
                // is a candidate for the batch-replay fast path. Done once
                // per Convert so the per-VisitBox check is a single HashSet
                // lookup.
                dirtyAncestors.Clear();
                if (tracker != null) {
                    // DirtyEntries returns the underlying Dictionary so the
                    // foreach uses its struct enumerator — no IEnumerable
                    // boxing of a yield-return state machine. The kind filter
                    // moves inline; on the typical frame most entries match.
                    const InvalidationKind dirtyMask =
                        InvalidationKind.Paint
                        | InvalidationKind.Composite
                        | InvalidationKind.Layout
                        | InvalidationKind.Style
                        | InvalidationKind.Structure
                        | InvalidationKind.PseudoClassState;
                    foreach (var kv in tracker.DirtyEntries) {
                        if ((kv.Value & dirtyMask) == 0) continue;
                        var e = DirtyElementFor(kv.Key);
                        if (e != null) {
                            for (Node a = e; a != null; a = a.Parent) {
                                if (a is Element ae && !dirtyAncestors.Add(ae)) break;
                            }
                        }
                    }
                }
                if (tracker != null && elementToBox != null) {
                    Apply(tracker, elementToBox);
                }
                int est = EstimateCommandCountCached(root);
                PaintList list;
                if (output != null) {
                    output.Reset();
                    list = output;
                } else {
                    list = listPool.Rent(est);
                }
                if (root == null) return list;
                this.activeScrollContainer = scrollContainer;
                this.activeStateProvider = stateProvider;
                // O(N) post-order walk to populate Box.SubtreeHasTextRun
                // and Box.SubtreeHasScrollState — replaces the O(N²) hot
                // path inside VisitBox where every box invoked recursive
                // SubtreeContainsTextRun / SubtreeContainsScrollState.
                PrecomputeSubtreeFlags(root);
                VisitBox(root, list, 0, 0);
                activeColorBrightness = 1.0;
                this.activeScrollContainer = null;
                this.activeStateProvider = null;
                return list;
            }
        }

        // Used by the backend to deposit a completed BoxBatchSnapshot into
        // the painter's per-box dictionary. Wired by WevaDocument before each
        // paint pass — the backend (BatchedURPRenderBackend) invokes the
        // sink when its Submit(EndSubtreeCaptureCommand) handler closes a
        // capture window. The painter then has the snapshot available on
        // the NEXT Convert call for the replay decision.
        public void RegisterSubtreeSnapshot(Box box, IBoxBatchSnapshot snapshot) {
            if (box == null || snapshot == null) return;
            // Recycle the displaced snapshot: a re-capture for the same box
            // (typically because the previous frame's snapshot is now stale)
            // means the old wrapper + its rented Instances arrays can go
            // back to their pool. Without this hand-off, every dirty/clean
            // cycle of a box would GC the previous snapshot.
            if (subtreeSnapshots.TryGetValue(box, out var existing)
                && existing != null
                && !ReferenceEquals(existing, snapshot)) {
                existing.Recycle();
            }
            subtreeSnapshots[box] = snapshot;
        }

        // Returns the commands in `list` to the converter's command pool and the
        // PaintList itself to its list pool. The caller hands back ownership; do
        // not retain references to `list` or any commands it contained after
        // calling this. No-op on null.
        public void Return(PaintList list) {
            if (list == null) return;
            commandPool.ReturnAll(list);
            listPool.Return(list);
        }

        public void ReturnCommands(PaintList list) {
            if (list == null) return;
            commandPool.ReturnAll(list);
            list.Reset();
        }

        ScrollContainer activeScrollContainer;
        IElementStateProvider activeStateProvider;
        int scrollContentDepth;
        double activeColorBrightness = 1.0;
        // Viewport-level scroll offset (CSS Overflow §3.3). Populated when the
        // root (anonymous viewport) box has a ScrollState. Fixed-position boxes
        // must not be affected by the viewport scroll, so VisitBox applies a
        // counter-translate when visiting position:fixed boxes while these are
        // non-zero.
        double viewportScrollX;
        double viewportScrollY;

        // Per-frame free-list of bucket triplets used by the stacking-order
        // walk. Stacking-context boxes can nest (e.g. an opacity<1 inside a
        // fixed-position root), and each level needs its own buckets while
        // recursion is in flight. We rent a triplet on entry and return it
        // on exit; the underlying List<T>s keep their capacity so steady-
        // state visits allocate nothing.
        readonly Stack<StackingBuckets> stackingBucketPool = new();

        sealed class StackingBuckets {
            public readonly List<(Box Box, int Z, int DocOrder)> Negative = new();
            // CSS 2.1 Appendix E painting order: in-flow non-positioned
            // descendants paint BEFORE positioned-z-auto descendants. The
            // prior single "Normal" bucket lumped both at z=0 sorted by
            // doc order, which let an earlier-doc-order positioned z:auto
            // box paint UNDER a later non-positioned sibling.
            public readonly List<(Box Box, int Z, int DocOrder)> NormalInFlow = new();
            public readonly List<(Box Box, int Z, int DocOrder)> NormalPositioned = new();
            public readonly List<(Box Box, int Z, int DocOrder)> Positive = new();

            public void Clear() {
                Negative.Clear();
                NormalInFlow.Clear();
                NormalPositioned.Clear();
                Positive.Clear();
            }
        }

        static readonly Comparison<(Box Box, int Z, int DocOrder)> StackingCompare =
            (a, b) => {
                int c = a.Z.CompareTo(b.Z);
                if (c != 0) return c;
                return a.DocOrder.CompareTo(b.DocOrder);
            };

        // Steady-state shortcut: when the same root with the same Box.Version
        // is re-converted under the same contextVersion (no Invalidate*All
        // happened between calls), the previous estimate is still exact. Skip
        // the O(N) CountWalk entirely.
        int EstimateCommandCountCached(Box root) {
            if (root == null) return 0;
            long rootVersion = root.Version;
            if (estimateCache.Valid
                && ReferenceEquals(estimateCache.Root, root)
                && estimateCache.RootVersion == rootVersion
                && estimateCache.ContextVersion == contextVersion) {
                return estimateCache.Estimate;
            }
            int count = CountWalk(root);
            estimateCache.Root = root;
            estimateCache.RootVersion = rootVersion;
            estimateCache.ContextVersion = contextVersion;
            estimateCache.Estimate = count;
            estimateCache.Valid = true;
            return count;
        }

        // Returns the subtree command-count estimate for `box`, consulting the
        // per-Box memo so a stable subtree is a single dictionary probe
        // instead of an O(subtree) walk. Misses descend into children — each
        // child either short-circuits via its own cached entry or recurses.
        // The dirty-path walk down from the root is exactly the chain whose
        // Box.Version bumped this frame; every other subtree replays its
        // cached count.
        int CountWalk(Box box) {
            if (box == null) return 0;
            long version = box.Version;
            if (subtreeCountCache.TryGetValue(box, out var entry) && entry.Version == version) {
                return entry.SubtreeCount;
            }
            estimateWalkNodes++;
            int count = 4;
            var children = box.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) count += CountWalk(children[i]);
            subtreeCountCache[box] = (version, count);
            return count;
        }

        // Per-Convert recursion depth — incremented at the top of VisitBox,
        // decremented in the trailing finally. Browsers cap DOM nesting at
        // ~500-1000 to keep the layout / paint walkers off the stack-overflow
        // cliff; we match Chrome's effective ceiling. Beyond the limit we
        // bail with a one-shot diagnostic instead of crashing.
        const int MaxVisitDepth = 512;
        int visitDepth;
        bool visitDepthWarned;

        void VisitBox(Box box, PaintList list, double parentAbsX, double parentAbsY) {
            using var _vbScope = Weva.Profiling.PerfMarkerScope.Auto(Weva.Profiling.UIProfilerMarkers.PaintVisitBox);
            if (box == null) return;

            // Stack-overflow guard. Author-generated HTML with pathological
            // nesting (or accidental infinite recursion via display:contents
            // or some component-expansion loop) would otherwise crash the
            // app — we'd rather draw a partial tree and log than die.
            if (visitDepth >= MaxVisitDepth) {
                if (!visitDepthWarned) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("BoxToPaintConverter",
                        "max DOM nesting depth (" + MaxVisitDepth + ") exceeded; skipping deeper subtree");
                    visitDepthWarned = true;
                }
                return;
            }
            visitDepth++;
            try {

            // Layout stores X/Y as local-to-direct-parent. Sum down the tree to get the
            // box's absolute (root-relative) origin, which is what paint commands need.
            // Sticky offsets are applied here so descendants inherit the shift; layout
            // X/Y stay at the natural in-flow position so re-laying out is unnecessary
            // when only the scroll position (and thus sticky offset) changes.
            double absX = parentAbsX + box.X + box.StickyOffsetX;
            double absY = parentAbsY + box.Y + box.StickyOffsetY;

            // CSS Overflow §3.3 — fixed-position boxes are anchored to the
            // viewport and must NOT be affected by the viewport scroll translate.
            // When viewportScrollX/Y are non-zero (the viewport is actively
            // scrolled), push a counter-translate before this fixed box's subtree
            // and pop it after so the net transform on fixed descendants is zero.
            // We check Position directly here; fixed boxes are always BlockBoxes
            // so the Position field is always stamped by PositioningPass.
            bool pushedFixedCounterTranslate = false;
            if (box.Position == Weva.Layout.Positioning.PositionType.Fixed
                && (viewportScrollX != 0 || viewportScrollY != 0)) {
                list.Add(commandPool.RentPushTransform(
                    Transform2D.Translate((float)viewportScrollX, (float)viewportScrollY)));
                pushedFixedCounterTranslate = true;
            }

            if (box is TextRun tr) {
                // ExactSrgbSourceOver (mode 17) for text: in glass documents ALL
                // translucent compositing should happen in sRGB space to match Chrome.
                // Translucent text (0 < color.A < 1) composited linearly in Unity
                // appears too bright vs Chrome — e.g. rgba(244,242,255,0.66) over a
                // dark glass panel yields ~(203,205,225) in Unity vs ~(181,190,219) in
                // Chrome, a visually prominent 22-count red-channel gap.
                // Gate: backdropFilterSeenThisConvert > 0 (same glass-document guard as
                // bg-color mode-17) so text on plain non-glass pages pays zero overhead.
                bool textSrgbWrap = false;
                if (EnableExactSrgbGlassCompositing && backdropFilterSeenThisConvert > 0 && tr.Style != null) {
                    var textColor = TextRunResolver.ResolveTextColor(tr.Style);
                    textSrgbWrap = textColor.A > 1e-4f && textColor.A < 1f - 1e-4f;
                }
                if (textSrgbWrap) list.Add(commandPool.RentPushMixBlendMode(MixBlendMode.ExactSrgbSourceOver));
                EmitTextRunCached(tr, list, absX, absY);
                if (textSrgbWrap) list.Add(PaintCommandSingletons.PopMixBlendMode);
                if (pushedFixedCounterTranslate) list.Add(PaintCommandSingletons.PopTransform);
                return;
            }

            ComputedStyle style = box.Style;

            // Defensively skip display:none boxes if encountered (BoxBuilder
            // normally drops them). Done BEFORE the capture/replay dispatch
            // so a hidden subtree never opens a capture window.
            //
            // The per-style parsed cache returns the cascade-resolved keyword
            // as a CssKeyword whose .Identifier is already lowercase, so the
            // common `display: none` case is a single byte-equality compare
            // with no Trim/ToLowerInvariant allocation. Unknown idents
            // (CssIdentifier) fall through to CssStringUtil's case-insensitive
            // trim compare.
            if (style != null && IsDisplayNone(style)) {
                if (pushedFixedCounterTranslate) list.Add(PaintCommandSingletons.PopTransform);
                return;
            }

            ScrollState scrollState = activeScrollContainer != null
                ? activeScrollContainer.Get(box)
                : null;

            // Per-subtree batch-replay fast path. If this box's Element is
            // outside this frame's dirtyAncestors set, the subtree has no
            // dirty descendants and is a candidate for replay/capture.
            // Replay: a previously-captured snapshot exists → emit a
            // ReplaySubtreeSnapshotCommand and skip the walk entirely.
            // Capture: no snapshot yet → wrap the normal walk in Begin/End
            // capture markers so the backend records this frame's
            // contribution into a fresh snapshot for next frame.
            // captureDepth prevents nested captures from cascading into N
            // overlapping snapshots; only the outermost clean subtree's
            // walk drives the capture.
            bool subtreeIsClean = box.Element != null && !dirtyAncestors.Contains(box.Element);
            // Use precomputed subtree flags populated at the top of
            // Convert. The local scrollState check still matters because
            // it represents THIS box's own scroll state (not a descendant
            // — that's what SubtreeHasScrollState captures).
            bool subtreeHasScrollState = scrollState != null || box.SubtreeHasScrollState;
            bool subtreeHasTextRun = box.SubtreeHasTextRun;
            bool insideScrollContent = scrollContentDepth > 0;
            bool didReplay = false;
            bool emittedBeginCapture = false;
            int beginCaptureIndex = -1;
            // !subtreeHasTextRun is load-bearing UNLESS the caller has
            // opted into text-aware snapshot replay (AllowTextSubtree
            // SnapshotReplay = true). Text atlas UVs change between
            // frames (atlas re-pack, fallback glyph resolution)
            // independently of layout/style, so a snapshot captured
            // this frame may reference stale UV coords next frame.
            // URP's BoxBatchSnapshot implements IValidatedBoxBatch
            // Snapshot.IsValid against SdfTextRendering's atlas
            // version, so it's safe to skip the text gate when
            // opt-in. Default off keeps the
            // PaintIncrementalTests.Text_subtree_never_replays_
            // retained_batch_snapshot pin happy with the fake snapshots
            // those tests use.
            bool textGate = subtreeHasTextRun && !AllowTextSubtreeSnapshotReplay;
            // insideColumnSlice: this child is being painted across multiple columns
            // by VisitChildrenMulticol. A prior-frame snapshot cannot be replayed
            // here because the slicer wraps each column in its own clip+translate
            // scope; the snapshot's AnchorX/Y would apply the wrong base-position.
            // Force fresh paint so the correct clip/translate scope is applied.
            // (MULTICOL-FRAG v1 — paint-level slicing.)
            if (activeColorBrightness == 1.0
                && subtreeIsClean
                && (!insideScrollContent || AllowScrollContentSnapshotReplay)
                && !subtreeHasScrollState && !textGate
                && !insideColumnSlice) {
                bool hasReusableSnapshot = false;
                if (subtreeSnapshots.TryGetValue(box, out var snap) && snap != null) {
                    bool snapshotIsValid = !(snap is IValidatedBoxBatchSnapshot validated) || validated.IsValid;
                    if (!snap.ContainsFilterScopes && snapshotIsValid) {
                        double dx = absX - snap.AnchorX;
                        double dy = absY - snap.AnchorY;
                        list.Add(commandPool.RentReplaySubtreeSnapshot(snap, dx, dy));
                        didReplay = true;
                        hasReusableSnapshot = true;
                    } else {
                        snap.Recycle();
                        subtreeSnapshots.Remove(box);
                    }
                }
                if (!hasReusableSnapshot && captureDepth == 0) {
                    beginCaptureIndex = list.Count;
                    list.Add(commandPool.RentBeginSubtreeCapture(box, absX, absY));
                    captureDepth++;
                    emittedBeginCapture = true;
                }
            }
            if (didReplay) {
                if (pushedFixedCounterTranslate) list.Add(PaintCommandSingletons.PopTransform);
                return;
            }

            // Box-local-coords cache lookup: PaintCache holds commands as if the
            // box's origin were (0,0). On a hit we rent translated copies into the
            // active list; on a miss we materialize fresh box-local commands into
            // the cache and replay them translated. Either way, child recursion is
            // independent — the per-child cache decides its own hit/miss.
            //
            // The cache hit path skips the display:none check below: a flip
            // between display:none and a visible value bumps style.Version, which
            // invalidates the cache and forces the miss path where the check runs.
            // Likewise, scroll-state-bearing boxes never populate their PaintCache
            // (VisitWithScroll skips caching), so a cache hit guarantees no scroll
            // state. This cuts a per-visit string-equality branch on the steady-
            // state hot path proportional to tree size.
            // (display:none check moved above the capture/replay dispatch so
            // a hidden subtree never opens a Begin/End capture window.)

            // ScrollState lives in a sidecar (ScrollContainer) that's updated
            // independently of box.Version — wheel events can change
            // ScrollX/Y between frames without bumping the box style. So the
            // SCROLL WRAP commands (PushClip + (-ScrollX,-ScrollY) translate
            // + scrollbar) are NOT cached; they're re-emitted into the live
            // list each frame from the current scroll state. The decoration
            // commands above and the scroll-content (children) are still
            // cached and translated like every other box. Earlier the entire
            // scroll-container path was uncached (VisitWithScroll calling
            // EmitBoxFromScratchAbsolute), which made each scroll container
            // pay full-rebuild cost every frame — 7+ ms in the match3
            // profile across 4 overflow:hidden boxes.
            // Emit the wrapper push commands FRESH every frame, regardless
            // of cache state. Wrappers (filter / transform / opacity) animate
            // independently of the cached decorations — a transform-only
            // tick bumps style.Version but leaves DecorationVersion stable,
            // so the cache stays valid below. The 3 wrapper pool-rents per
            // box are alloc-free and effectively free.
            FilterChain filters = EmitWrappersFresh(box, style, list, absX, absY,
                out int pushedFilter, out int pushedTransform, out int pushedMask, out int pushedOpacity,
                out int pushedMixBlendMode,
                out double foldedBrightness);
            double previousColorBrightness = activeColorBrightness;
            if (foldedBrightness != 1.0) activeColorBrightness *= foldedBrightness;

            var cache = box.PaintCache;
            bool cacheHit = cache != null && cache.IsValid(box, style, contextVersion);

            if (cacheHit) {
                cacheHits++;
                // Skip the call entirely when there are no decoration
                // commands to replay. Anonymous wrappers and layout-only
                // containers cache an empty PreChildren list — saves the
                // call frame + profiler scope per such box (audit: ~half
                // the tree on the demo).
                if (cache.PreChildren.Count > 0) ReplayTranslated(cache.PreChildren, list, absX, absY);
            } else {
                cacheMisses++;
                if (cache == null) {
                    cache = paintBoxCachePool.Count > 0 ? paintBoxCachePool.Pop() : new PaintBoxCache();
                    box.PaintCache = cache;
                    cachedBoxes.Add(box);
                } else {
                    // Cache miss with an existing entry: rented PaintCommands
                    // from the previous fill are still in PreChildren. Return
                    // them to the pool before Reset clears the list.
                    ReturnCachedCommands(cache);
                }
                cache.Reset(box.Version, style != null ? style.DecorationVersion : 0, contextVersion);

                // Build the decoration commands at box-local origin. Wrappers
                // were already emitted into `list` above; the cache holds
                // only the decoration + overflow-clip-push commands.
                scratchPre.Clear();
                EmitDecorationsLocal(box, style, filters, scratchPre, out int popClip, out int popClipPath);
                for (int i = 0; i < scratchPre.Count; i++) cache.PreChildren.Add(scratchPre[i]);
                scratchPre.Clear();
                // Replay into the active list (translated to absolute coords).
                ReplayTranslated(cache.PreChildren, list, absX, absY);

                // Only the overflow-clip pop lives in PostChildren. Wrapper
                // pops are emitted fresh per frame at the bottom of this
                // method, mirroring the wrapper pushes above.
                for (int i = 0; i < popClip; i++) cache.PostChildren.Add(PaintCommandSingletons.PopClip);
                for (int i = 0; i < popClipPath; i++) cache.PostChildren.Add(PaintCommandSingletons.PopClipPath);
            }

            // Scroll wrap: NOT cached. The clip rect uses the current
            // (possibly track-thinned) scrollport; the translate uses the
            // current ScrollX/Y. Both can change per frame on user input.
            int pushedScrollClip = 0;
            int pushedScrollTransform = 0;
            // True when this box IS the viewport scroll root (anonymous box,
            // no Element, no parent). Fixed-position descendants must not be
            // offset by the viewport scroll transform, so we track the active
            // viewport scroll and emit a counter-translate for them.
            bool isViewportScrollRoot = scrollState != null
                && box.Element == null && box.Parent == null;
            if (scrollState != null) {
                // CSS Scrollbars 1 §3.3 — clip rect honors the per-box
                // scrollbar-width so a `thin`/`none`/explicit-thickness
                // scroller doesn't leave the default 12px gap when the
                // gutter is narrower or zero.
                double resolvedThickness = Weva.Layout.Scrolling.ScrollMath.ResolveScrollbarThickness(box);
                double trackThickX = scrollState.ShowsTrackY ? resolvedThickness : 0;
                double trackThickY = scrollState.ShowsTrackX ? resolvedThickness : 0;
                var scrollportRect = new Rect(
                    absX + box.BorderLeft,
                    absY + box.BorderTop,
                    Math.Max(0, box.Width - box.BorderLeft - box.BorderRight - trackThickX),
                    Math.Max(0, box.Height - box.BorderTop - box.BorderBottom - trackThickY)
                );
                bool overflowClipAlreadyCoversScrollport =
                    trackThickX <= 0.0001
                    && trackThickY <= 0.0001
                    && OverflowResolver.ShouldClip(style);
                if (!overflowClipAlreadyCoversScrollport && scrollportRect.Width > 0 && scrollportRect.Height > 0) {
                    BorderRadii scrollportRadii = style != null
                        ? BorderRadiiResolver.ResolveBorderRadii(style, LengthContextFor(style), new Rect(0, 0, box.Width, box.Height))
                        : BorderRadii.Zero;
                    list.Add(commandPool.RentPushClip(scrollportRect, scrollportRadii));
                    pushedScrollClip = 1;
                }
                if (scrollState.ScrollX != 0 || scrollState.ScrollY != 0) {
                    list.Add(commandPool.RentPushTransform(
                        Transform2D.Translate((float)(-scrollState.ScrollX), (float)(-scrollState.ScrollY))));
                    pushedScrollTransform = 1;
                }
                // Record the active viewport scroll so VisitBox can counter-
                // translate position:fixed descendants.
                if (isViewportScrollRoot) {
                    viewportScrollX = scrollState.ScrollX;
                    viewportScrollY = scrollState.ScrollY;
                }
            }

            // CSS Containment L2 §4 — content-visibility: hidden / auto.
            //
            // `hidden`: the element's own box (background, border) has already
            //   been emitted above by EmitDecorationsLocal.  Descendant paint
            //   and the form-control overlay are suppressed entirely — the
            //   spec says "as if [the contents] had no boxes" for paint.
            //   Size containment (height collapses) is handled by BlockLayout
            //   via ContainmentResolver.HasSize.  Hit testing exclusion is
            //   handled by BoxTreeHitTester.
            //
            // `auto` + off-viewport: same descendant-paint skip applies when
            //   the border box is entirely outside the viewport (0,0,VW,VH).
            //   On-screen boxes are painted normally — only the containment
            //   side-effects (layout barrier, paint clip) remain active.
            //   The viewport check is skipped when ViewportWidth/Height == 0.
            bool skipDescendants = false;
            if (style != null) {
                if (Weva.Layout.Containment.ContainmentResolver.IsContentVisibilityHidden(style)) {
                    skipDescendants = true;
                } else if (Weva.Layout.Containment.ContainmentResolver.IsContentVisibilityAuto(style)
                           && ViewportWidth > 0 && ViewportHeight > 0) {
                    // Off-viewport when the border box is entirely outside [0, VW] x [0, VH].
                    bool offScreen = absX + box.Width <= 0
                        || absY + box.Height <= 0
                        || absX >= ViewportWidth
                        || absY >= ViewportHeight;
                    if (offScreen) skipDescendants = true;
                }
            }

            if (!skipDescendants) {
                if (scrollState != null) scrollContentDepth++;
                VisitChildren(box, list, absX, absY);
                if (scrollState != null) scrollContentDepth--;
                if (isViewportScrollRoot) {
                    viewportScrollX = 0;
                    viewportScrollY = 0;
                }

                // Form-control overlays (placeholder text) are emitted UNCACHED
                // so attribute mutations on `value` / `placeholder` flow through
                // without depending on a style.Version bump. See EmitInputOverlays.
                EmitInputOverlays(box, list, absX, absY);
            } else {
                // Even when skipping descendants reset viewport-scroll tracking
                // in case this box is the viewport scroll root.
                if (isViewportScrollRoot) {
                    viewportScrollX = 0;
                    viewportScrollY = 0;
                }
            }

            if (scrollState != null) {
                for (int i = 0; i < pushedScrollTransform; i++) list.Add(PaintCommandSingletons.PopTransform);
                for (int i = 0; i < pushedScrollClip; i++) list.Add(PaintCommandSingletons.PopClip);
                ScrollbarPaint.Emit(box, scrollState, absX, absY, list, activeStateProvider, ScrollbarCascade);
            }

            if (cache.PostChildren.Count > 0) ReplayTranslated(cache.PostChildren, list, absX, absY);

            // Emit wrapper pops fresh in reverse-push order — matching the
            // EmitWrappersFresh calls at the top of this method. Pop counts
            // are derived from how many wrappers we pushed this frame, not
            // from the cache (which doesn't store wrappers any more).
            // Pop wrappers in reverse-push order; mix-blend-mode was pushed
            // AFTER opacity so it pops FIRST here (LIFO).
            for (int i = 0; i < pushedMixBlendMode; i++) list.Add(PaintCommandSingletons.PopMixBlendMode);
            for (int i = 0; i < pushedOpacity; i++) list.Add(PaintCommandSingletons.PopOpacity);
            for (int i = 0; i < pushedMask; i++) list.Add(PaintCommandSingletons.PopMask);
            for (int i = 0; i < pushedTransform; i++) list.Add(PaintCommandSingletons.PopTransform);
            for (int i = 0; i < pushedFilter; i++) list.Add(PaintCommandSingletons.PopFilter);
            activeColorBrightness = previousColorBrightness;

            // Close the snapshot-capture window opened at the top of this
            // VisitBox call (if any). Backend's Submit handler for the End
            // command materialises the BoxBatchSnapshot from the marker
            // recorded at Begin time.
            //
            // Empty-subtree elision: when nothing was emitted between Begin
            // and End the markers carry no payload and would only inflate
            // the command list. Drop the Begin we recorded and skip the End
            // entirely so PaintCorrectnessTests.Empty_tree_produces_no_commands
            // stays at zero commands. Snapshotting an empty subtree has no
            // visible cost in the renderer either.
            if (emittedBeginCapture) {
                if (beginCaptureIndex >= 0 && list.Count == beginCaptureIndex + 1) {
                    // Empty subtree: pull the pool-rented Begin back out and
                    // return it; otherwise it'd leak (the list owned the
                    // reference and we're removing it from the list).
                    var begin = list.Commands[beginCaptureIndex];
                    list.RemoveAt(beginCaptureIndex);
                    commandPool.ReturnOne(begin);
                } else {
                    list.Add(commandPool.RentEndSubtreeCapture(box));
                }
                captureDepth--;
            }

            // Pop the fixed-position counter-translate (viewport scroll
            // cancellation) after all of this box's contents have been emitted.
            if (pushedFixedCounterTranslate) list.Add(PaintCommandSingletons.PopTransform);

            } finally {
                visitDepth--;
            }
        }

        void VisitChildren(Box box, PaintList list, double absX, double absY) {
            var children = box.Children;
            int n = children.Count;
            if (n == 0) return;

            // MULTICOL-FRAG v1: paint-level column fragmentation.
            // When the parent is a MulticolBox, direct in-flow block children
            // that overflow their assigned column are sliced across columns.
            // Children that fit in their column paint normally (single visit,
            // no extra clip/transform — byte-identical to the pre-slicing path).
            if (box is Weva.Layout.Multicol.MulticolBox mcb) {
                VisitChildrenMulticol(mcb, list, absX, absY);
                return;
            }

            // Fast path: if NO child has a non-zero effective z-index the
            // three sub-loops would all collapse into the doc-order middle
            // bucket, so skip the bucket allocation and walk children
            // directly — but still apply CSS 2.1 Appendix E step 3's
            // 3a→3b→3c split (block-level → floats → inline-level) so
            // that floated siblings paint over block backgrounds but
            // under inline content, regardless of document order. This
            // matches PaintOrderTraversal.EnumerateContext's interpretation
            // of step 3, which B4 already adopted for the enumeration
            // surface; without rewiring the converter, only the
            // enumeration changed, not the paint output (B4b).
            if (!AnyChildHasExplicitZ(children, n)) {
                VisitChildrenInSpecOrder(children, n, list, absX, absY);
                return;
            }

            // CSS Painting Order: positioned descendants with z-index!=auto
            // paint in three buckets — negative-z first, then in-flow plus
            // z-auto/0 in doc order, then positive-z. The PaintCache
            // contract is unaffected: per-box caches still hold box-local
            // commands; only the visit order over Children changes.
            VisitChildrenInStackingOrder(children, n, list, absX, absY);
        }

        // MULTICOL-FRAG v1: Paint direct children of a MulticolBox.
        //
        // For each in-flow block child whose laid-out height exceeds the column
        // content height the child is "fragmented" across columns at paint time.
        // Layout is unchanged — the child retains its single whole-child geometry
        // (X/Y/Width/Height set by TryDistribute).  Only the paint step changes:
        //
        //   For each column k that the child spans (from startCol onward):
        //     1. PushClip to column k's content rect (absolute world coords).
        //     2. PushTransform: translate child UP by (k - startCol) * colHeight
        //        so the next "slice" of content aligns with the column top.
        //     3. VisitBox(child, list, absX, absY) — paint the full child subtree
        //        inside the clip+translate scope.
        //     4. PopTransform / PopClip.
        //
        // A child that fits in its column paints normally — single VisitBox call,
        // no clip or transform — preserving byte-identical output for the fits case.
        //
        // When a child is taller than the total column span (endColWasCapped) the
        // child's last-column tail is clipped to colHeight, matching Chrome behaviour
        // (Chrome does not grow the multicol box; content beyond the last column is
        // not shown).  See C2 divergence #1 fix.
        //
        // Column rules are unaffected — they are emitted as part of the MulticolBox's
        // own decoration (EmitDecorationsLocal), not in this child-visitation pass.
        //
        // Snapshot replay: insideColumnSlice suppresses snapshot replay inside
        // VisitBox for any sliced child.  See the field comment for rationale.
        void VisitChildrenMulticol(Weva.Layout.Multicol.MulticolBox mcb,
                                   PaintList list, double absX, double absY) {
            int N = mcb.UsedColumnCount;
            double colWidth = mcb.UsedColumnWidth;
            double gap = mcb.UsedGap;
            double contentLeft = mcb.PaddingLeft + mcb.BorderLeft;
            double contentTop  = mcb.PaddingTop  + mcb.BorderTop;

            // Column content height: the usable height limit per column.
            // UsedColumnHeight is set by MulticolLayout to the colHeight parameter
            // passed to TryDistribute: the explicit content height (sequential path)
            // or the converged balanced height (auto-height path). This is the
            // correct limit for fragmentation — ColumnHeights[] stores actual filled
            // heights that can EXCEED the limit when a child overflows.
            double colHeight = mcb.UsedColumnHeight;
            if (colHeight <= 0) {
                // Fallback: if MulticolLayout hasn't run, derive from container.
                colHeight = mcb.Height - mcb.PaddingTop - mcb.PaddingBottom
                            - mcb.BorderTop - mcb.BorderBottom;
                if (colHeight <= 0) colHeight = mcb.Height;
            }

            var children = mcb.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;

                // Only in-flow block children are candidates for fragmentation.
                // OOF / float / inline children fall through to VisitBox normally.
                bool isInFlowBlock = child is Weva.Layout.Boxes.BlockBox cb
                    && !cb.IsFloat
                    && cb.Position != Weva.Layout.Positioning.PositionType.Absolute
                    && cb.Position != Weva.Layout.Positioning.PositionType.Fixed;

                if (!isInFlowBlock) {
                    VisitBox(child, list, absX, absY);
                    continue;
                }

                // Determine the child's starting column from its X offset
                // relative to the container content left edge.  TryDistribute
                // sets child.X = contentLeft + col*(colWidth+gap) + marginLeft.
                double childLocalX = child.X - contentLeft;
                int startCol = 0;
                if (colWidth + gap > 0) {
                    startCol = (int)Math.Round(childLocalX / (colWidth + gap));
                    if (startCol < 0) startCol = 0;
                    if (startCol >= N) startCol = N - 1;
                }

                // Child height including its top margin offset from column top.
                // child.Y is the absolute Y inside the container (content-box-
                // relative top, set by TryDistribute as: top + cursor + marginTop).
                double childLocalY = child.Y - contentTop;   // offset from content top
                double childTotalHeight = child.Height;       // border-box height only

                // How many columns does this child span?
                // Column k can hold content from Y = (k - startCol)*colHeight
                // to Y = (k - startCol + 1)*colHeight.
                // sliceStartY is the Y within the child (relative to child top)
                // that falls in column k.  For startCol the child always starts
                // at its top, offset downward by childLocalY within the column.
                //
                // Simple span count: span = ceil(childTotalHeight / colHeight)
                // but capped to N - startCol (remaining columns).
                double heightAboveColumnTop = childLocalY;  // portion of col above child top
                // Full occupied height in the column dimension:
                double occupiedHeight = heightAboveColumnTop + childTotalHeight;
                int spanCols = 1;
                if (colHeight > 0 && occupiedHeight > colHeight + 1e-3) {
                    spanCols = (int)Math.Ceiling(occupiedHeight / colHeight);
                    if (spanCols < 1) spanCols = 1;
                }
                int endCol = startCol + spanCols - 1;
                bool endColWasCapped = endCol >= N;
                if (endCol >= N) endCol = N - 1;

                bool needsSlicing = (endCol > startCol);

                if (!needsSlicing) {
                    // Child fits in its column OR is in the last column with no further
                    // columns to slice into.
                    //
                    // Chrome-parity fix (C2 divergence #1): when the child is taller than
                    // the total column span (endColWasCapped) the child's last column is
                    // also the LAST column of the container.  Without a clip the content
                    // below colAbsY+colHeight paints past the column box — Chrome does NOT
                    // grow; it simply does not show content beyond the last column.
                    // Apply a one-column clip so the tail is hidden, matching Chrome.
                    //
                    // A child that genuinely fits (spanCols==1, endColWasCapped==false)
                    // takes the fast no-clip path unchanged — byte-identical to before.
                    if (endColWasCapped) {
                        // Last-column tail clip: clip to column startCol's content rect.
                        double colAbsXlast = absX + contentLeft + startCol * (colWidth + gap);
                        double colAbsYlast = absY + contentTop;
                        var lastColClip = new Rect(colAbsXlast, colAbsYlast, colWidth, colHeight);
                        list.Add(commandPool.RentPushClip(lastColClip, BorderRadii.Zero));
                        VisitBox(child, list, absX, absY);
                        list.Add(PaintCommandSingletons.PopClip);
                    } else {
                        // Child fits in its column — paint normally, no clip/transform.
                        VisitBox(child, list, absX, absY);
                    }
                    continue;
                }

                // Child overflows its column: paint once per spanned column.
                // Each iteration:
                //   1. Clip to column k's content rect (absolute coords).
                //   2. Translate child UP by (k - startCol) * colHeight so the
                //      next slice of content aligns with the column top.
                //   3. VisitBox (snapshot replay suppressed via insideColumnSlice).
                //   4. PopTransform / PopClip.
                insideColumnSlice = true;
                try {
                    for (int k = startCol; k <= endCol; k++) {
                        // Column k's content rect in absolute (world) coordinates.
                        double colAbsX = absX + contentLeft + k * (colWidth + gap);
                        double colAbsY = absY + contentTop;
                        var clipRect = new Rect(colAbsX, colAbsY, colWidth, colHeight);

                        // The child is laid out at its START column's X (VisitBox
                        // paints it at absX + child.X). To move the kth slice into
                        // column k we shift RIGHT by (k-startCol)*(colWidth+gap) AND
                        // UP by (k-startCol)*colHeight so that slice aligns to the
                        // top of column k. The earlier Y-only transform left every
                        // slice at the start column's X, where column k's clip rect
                        // rejected it entirely (columns 2+ rendered empty).
                        int slice = k - startCol;
                        float translateX = (float)(slice * (colWidth + gap));
                        float translateY = -(float)(slice * colHeight);
                        var sliceTransform = Transform2D.Translate(translateX, translateY);

                        list.Add(commandPool.RentPushClip(clipRect, BorderRadii.Zero));
                        list.Add(commandPool.RentPushTransform(sliceTransform));

                        // VisitBox paints the entire child subtree; the clip
                        // restricts visible output to this column's vertical band.
                        VisitBox(child, list, absX, absY);

                        list.Add(PaintCommandSingletons.PopTransform);
                        list.Add(PaintCommandSingletons.PopClip);
                    }
                } finally {
                    insideColumnSlice = false;
                }
            }
        }

        // Walk direct children in CSS 2.1 Appendix E step 3 sub-pass order:
        //   3a — block-level non-positioned non-float non-inline-block
        //   3b — floats (BlockBox.IsFloat)
        //   3c — inline-level (InlineBox / TextRun / LineBox / inline-block)
        // Predicates mirror PaintOrderTraversal.EnumerateContext exactly so
        // both surfaces classify identically. Doc-order is preserved within
        // each sub-pass via the index-based scan. The three passes share a
        // single loop body via a discriminator switch — no per-pass
        // List<> allocation, alloc-free for any child count.
        //
        // This is used by the fast path in VisitChildren (when no child
        // creates a z-index!=0 stacking context) AND by the NormalInFlow
        // bucket of VisitChildrenInStackingOrder.
        void VisitChildrenInSpecOrder(IReadOnlyList<Box> children, int n,
                                      PaintList list, double absX, double absY) {
            // 3a — block-level (default bucket; floats and inline-level fall through).
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                if (PaintOrderBucket(child) != 0) continue;
                VisitBox(child, list, absX, absY);
            }
            // 3b — floats.
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                if (PaintOrderBucket(child) != 1) continue;
                VisitBox(child, list, absX, absY);
            }
            // 3c — inline-level, split into two sub-passes so inline-box
            // backgrounds/borders paint BEHIND the line's text. Inline layout
            // flattens an inline element's text out into sibling TextRuns
            // under the LineBox and appends the element's decoration shell
            // (InlineBox / AnonymousInlineBox, child-count 0) as a trailing
            // sibling. In document order that shell paints LAST, covering its
            // own text — visible as a `<code>`/`<mark>`/highlighted-`<span>`
            // pill that blanks the glyphs underneath. CSS 2.1 Appendix E puts
            // "backgrounds and borders of inline boxes" before the inline
            // content, so emit the shells first, then the text/atomics.
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                if (PaintOrderBucket(child) != 2) continue;
                if (!IsInlineDecorationShell(child)) continue;
                VisitBox(child, list, absX, absY);
            }
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                if (PaintOrderBucket(child) != 2) continue;
                if (IsInlineDecorationShell(child)) continue;
                VisitBox(child, list, absX, absY);
            }
        }

        // Inline-box decoration shells (InlineBox / AnonymousInlineBox). These
        // carry an inline element's background/border/etc. but NOT its text —
        // line layout flattens the text into sibling TextRuns. They paint in
        // the inline-level bucket BEFORE the text (see VisitChildrenInSpecOrder
        // and the NormalInFlow loop in VisitChildrenInStackingOrder).
        static bool IsInlineDecorationShell(Box b) {
            return b is InlineBox || b is AnonymousInlineBox;
        }

        // 0 = block-level (3a), 1 = float (3b), 2 = inline-level (3c).
        // Float classification wins over inline-block so a floated
        // inline-block paints in 3b, not 3c — matches
        // PaintOrderTraversal.EnumerateContext.
        static int PaintOrderBucket(Box b) {
            if (b is BlockBox bb && bb.IsFloat) return 1;
            if (b is InlineBox || b is TextRun || b is LineBox) return 2;
            if (b is BlockBox bbIB && bbIB.IsInlineBlock) return 2;
            return 0;
        }

        // Post-order walk that populates Box.SubtreeHasTextRun and
        // Box.SubtreeHasScrollState before VisitBox starts. Without this,
        // every VisitBox call invoked the recursive predicate functions
        // — an O(N²) hot path that dominated PAINT-WARM-NOOP at ~900µs
        // on a 1500-box fixture. After: O(N) precompute + O(1) lookup
        // during the walk, cutting the time to roughly the inherent
        // cost of visiting each box.
        void PrecomputeSubtreeFlags(Box box) {
            if (box == null) return;
            bool hasText = false;
            bool hasScroll = activeScrollContainer != null && activeScrollContainer.Get(box) != null;
            var children = box.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child is TextRun) {
                    hasText = true;
                    child.SubtreeHasTextRun = false;
                    child.SubtreeHasScrollState = false;
                    continue;
                }
                PrecomputeSubtreeFlags(child);
                hasText = hasText || child.SubtreeHasTextRun;
                hasScroll = hasScroll || child.SubtreeHasScrollState;
            }
            box.SubtreeHasTextRun = hasText;
            box.SubtreeHasScrollState = hasScroll;
        }

        bool TryFoldBrightnessFilterIntoPaintColors(Box box, FilterChain filters, out double brightness) {
            brightness = 1.0;
            if (box == null || filters == null || filters.Functions.Count != 1) return false;
            if (!(filters.Functions[0] is BrightnessFilter bf)) return false;
            bool sawPaint = false;
            if (!SubtreePaintsOnlyBrightnessFoldablePaint(box, box, ref sawPaint)) return false;
            if (!sawPaint) return false;
            brightness = bf.Amount;
            return true;
        }

        bool SubtreePaintsOnlyBrightnessFoldablePaint(Box box, Box filterOwner, ref bool sawPaint) {
            if (box == null) return true;
            if (box is TextRun) {
                sawPaint = true;
                return true;
            }

            ComputedStyle style = box.Style;
            if (IsDecoratable(box, style)) {
                if (box.Element != null && box.Element.TagName == "img") return false;
                if (StyleHasUnsupportedBrightnessFoldPaint(style)) return false;
                if (style.HasDecorationProperties) sawPaint = true;
                if (!ReferenceEquals(box, filterOwner) && style.HasWrapperProperties) {
                    var filters = FilterResolver.Resolve(style, LengthContextFor(style));
                    if (!filters.IsEmpty && !IsSingleBrightnessFilter(filters)) return false;
                }
            }

            var children = box.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                if (!SubtreePaintsOnlyBrightnessFoldablePaint(children[i], filterOwner, ref sawPaint)) return false;
            }
            return true;
        }

        static bool IsSingleBrightnessFilter(FilterChain filters) {
            return filters != null
                   && filters.Functions.Count == 1
                   && filters.Functions[0] is BrightnessFilter;
        }

        static bool StyleHasUnsupportedBrightnessFoldPaint(ComputedStyle style) {
            if (style == null) return false;
            if (HasNonNoneValue(style, CssProperties.BackdropFilterId)) return true;
            if (HasNonNoneValue(style, CssProperties.BorderImageSourceId)) return true;
            if (HasNonNoneValue(style, CssProperties.MaskId)
                || HasNonNoneValue(style, CssProperties.MaskImageId)) return true;

            string backgroundImage = style.Get(CssProperties.BackgroundImageId);
            string background = style.Get(CssProperties.BackgroundId);
            return ContainsExternalImage(backgroundImage) || ContainsExternalImage(background);
        }

        static bool HasNonNoneValue(ComputedStyle style, int propertyId) {
            string raw = style.Get(propertyId);
            return !string.IsNullOrWhiteSpace(raw) && !CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none");
        }

        static bool ContainsExternalImage(string raw) {
            if (string.IsNullOrEmpty(raw)) return false;
            return raw.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0
                   || raw.IndexOf("image(", StringComparison.OrdinalIgnoreCase) >= 0
                   || raw.IndexOf("image-set(", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool AnyChildHasExplicitZ(IReadOnlyList<Box> children, int n) {
            for (int i = 0; i < n; i++) {
                var c = children[i];
                if (c == null) continue;
                // Non-zero z only matters if the child itself creates a
                // stacking context — z-index on a non-positioned, non-
                // context box has no painting effect per CSS.
                if (c.ZIndex.HasValue && c.ZIndex.Value != 0
                    && c.CreatesStackingContext()) {
                    return true;
                }
            }
            return false;
        }

        void VisitChildrenInStackingOrder(IReadOnlyList<Box> children, int n,
                                           PaintList list, double absX, double absY) {
            var buckets = stackingBucketPool.Count > 0 ? stackingBucketPool.Pop() : new StackingBuckets();

            // Effective z-index for ordering: a child only contributes a
            // non-zero z to the bucket choice if it itself creates a
            // stacking context AND has an explicit ZIndex (per CSS, z-index
            // is ignored on non-positioned / non-context boxes). Everything
            // else falls into the middle bucket at z=0, where doc-order
            // tiebreak preserves the existing in-flow paint sequence.
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                int z = 0;
                if (child.ZIndex.HasValue && child.CreatesStackingContext()) {
                    z = child.ZIndex.Value;
                }
                var entry = (Box: child, Z: z, DocOrder: i);
                if (z < 0) buckets.Negative.Add(entry);
                else if (z > 0) buckets.Positive.Add(entry);
                else {
                    // Split z=0 into in-flow and positioned. Per CSS 2.1
                    // Appendix E (painting order), positioned z:auto/z:0
                    // descendants paint AFTER in-flow non-positioned
                    // descendants, regardless of document order.
                    if (child.IsPositioned()) buckets.NormalPositioned.Add(entry);
                    else buckets.NormalInFlow.Add(entry);
                }
            }

            // List<T>.Sort is not guaranteed stable, so we tiebreak on
            // DocOrder explicitly to preserve source ordering for equal z.
            if (buckets.Negative.Count > 1) buckets.Negative.Sort(StackingCompare);
            if (buckets.Positive.Count > 1) buckets.Positive.Sort(StackingCompare);

            // Snapshot counts before recursing — VisitBox may itself rent
            // and return buckets from the same pool, but each rental gets
            // its own StackingBuckets instance, so iterating our own lists
            // by index here is safe.
            int negCount = buckets.Negative.Count;
            for (int i = 0; i < negCount; i++) {
                VisitBox(buckets.Negative[i].Box, list, absX, absY);
            }
            // In-flow non-positioned z=0 descendants paint first within
            // the middle layer (CSS painting order step 3), with step 3
            // itself sub-divided into 3a (block-level) → 3b (floats) →
            // 3c (inline-level). Mirrors VisitChildrenInSpecOrder's
            // predicate so the fast path and the stacking-bucket path
            // agree bit-for-bit on the in-flow paint sequence.
            // Pass 0 = block-level (3a), 1 = floats (3b), then the inline-level
            // bucket (3c) is split across passes 2 and 3: inline-box decoration
            // shells (backgrounds/borders) first, then the line's text/atomics.
            // See IsInlineDecorationShell / VisitChildrenInSpecOrder for why the
            // shells must paint behind the text they were flattened out of.
            int normInFlowCount = buckets.NormalInFlow.Count;
            for (int pass = 0; pass < 4; pass++) {
                for (int i = 0; i < normInFlowCount; i++) {
                    var entry = buckets.NormalInFlow[i];
                    int bkt = PaintOrderBucket(entry.Box);
                    if (pass < 2) {
                        if (bkt != pass) continue;
                    } else if (pass == 2) {
                        if (bkt != 2 || !IsInlineDecorationShell(entry.Box)) continue;
                    } else {
                        if (bkt != 2 || IsInlineDecorationShell(entry.Box)) continue;
                    }
                    VisitBox(entry.Box, list, absX, absY);
                }
            }
            // Then positioned z:auto/z:0 descendants (step 4).
            int normPosCount = buckets.NormalPositioned.Count;
            for (int i = 0; i < normPosCount; i++) {
                VisitBox(buckets.NormalPositioned[i].Box, list, absX, absY);
            }
            int posCount = buckets.Positive.Count;
            for (int i = 0; i < posCount; i++) {
                VisitBox(buckets.Positive[i].Box, list, absX, absY);
            }

            buckets.Clear();
            stackingBucketPool.Push(buckets);
        }

        // True when the box can carry its own decorations (background,
        // border, filter, transform, ...). Anonymous block / inline boxes
        // are layout-internal wrappers that the cascade never assigns a
        // user-authored style to — their decoration output is always
        // empty, so EmitWrappersFresh / EmitDecorationsLocal short-circuit
        // on a single `IsDecoratable` probe instead of duplicating the
        // three-part check.
        static bool IsDecoratable(Box box, ComputedStyle style) {
            return style != null && !(box is AnonymousBlockBox) && !(box is AnonymousInlineBox);
        }

        // Emits the box's BOX-LOCAL decoration commands (shadow / background
        // / image / border-image / border / outline / inset-shadow / overflow-
        // clip-push) into `output`. Returns the overflow-clip-push count via
        // out param so the caller can pair it with a matching pop after
        // recursing into children.
        //
        // The filter / transform / opacity WRAPPER push commands are NOT
        // emitted here — VisitBox calls EmitWrappersFresh separately for
        // those because they animate independently of the decorations. The
        // split lets the cache hold only the (stable-across-animation-ticks)
        // decoration commands so a transform-only frame stays a cache hit.
        //
        // box-local contract: bounds rects are (0, 0, Width, Height);
        // contentRect is (BorderLeft, BorderTop, ...). The replay path
        // translates by the current absolute origin when emitting into the
        // active list.
        void EmitDecorationsLocal(Box box, ComputedStyle style, FilterChain filters,
                                  List<PaintCommand> output, out int pushedClip, out int pushedClipPath) {
            using var _edScope = Weva.Profiling.PerfMarkerScope.Auto(Weva.Profiling.UIProfilerMarkers.PaintEmitDecorations);
            pushedClip = 0;
            pushedClipPath = 0;
            if (!IsDecoratable(box, style)) return;

            var bounds = new Rect(0, 0, box.Width, box.Height);
            LengthContext ctx = LengthContextFor(style);
            var clipPath = ClipPathResolver.Resolve(style, ctx, bounds);
            if (clipPath != null) {
                output.Add(commandPool.RentPushClipPath(clipPath));
                pushedClipPath++;
            }

            // Visible-only fills: shadow / background / image / border /
            // outline. `visibility: hidden` and `collapse` (treated as
            // hidden on non-flex/grid contexts per CSS Box 3 §3.2) skip
            // this block.
            BorderRadii radii = BorderRadiiResolver.ResolveBorderRadii(style, ctx, bounds);
            if (!IsVisibilityHidden(style) && !IsHiddenEmptyTableCell(box, style)) {
                EmitVisibleDecorations(box, style, bounds, ctx, filters, radii, output);
            }

            // CSS Containment L2 §3.2: `contain: paint` clips descendants to
            // the PADDING box (same clip region as overflow:hidden) even when
            // overflow is `visible`.  Only needed when overflow doesn't already
            // supply an equal-or-tighter clip — if overflow:hidden is also set,
            // the clip below handles it, so we avoid a redundant push.
            if (ContainmentResolver.HasPaint(style) && !OverflowResolver.ShouldClip(style)) {
                var paddingRect = new Rect(
                    box.BorderLeft,
                    box.BorderTop,
                    Math.Max(0, box.Width - box.BorderLeft - box.BorderRight),
                    Math.Max(0, box.Height - box.BorderTop - box.BorderBottom)
                );
                output.Add(commandPool.RentPushClip(paddingRect, radii));
                pushedClip++;
            }

            // Overflow clip — pushed last so its scope wraps the children
            // (the matching pop is appended via PostChildren after the
            // VisitChildren recursion).
            if (OverflowResolver.ShouldClip(style)) {
                var contentRect = new Rect(
                    box.BorderLeft,
                    box.BorderTop,
                    Math.Max(0, box.Width - box.BorderLeft - box.BorderRight),
                    Math.Max(0, box.Height - box.BorderTop - box.BorderBottom)
                );
                BorderRadii clipRadii = radii;
                if (OverflowResolver.IsOverflowClip(style)) {
                    double top = OverflowResolver.ResolveClipMarginTop(style, ctx);
                    double right = OverflowResolver.ResolveClipMarginRight(style, ctx);
                    double bottom = OverflowResolver.ResolveClipMarginBottom(style, ctx);
                    double left = OverflowResolver.ResolveClipMarginLeft(style, ctx);
                    if (top > 0 || right > 0 || bottom > 0 || left > 0) {
                        // CSS Overflow L4 §6: the `<visual-box>` keyword on each
                        // side longhand selects which box edge the clip-margin
                        // length inflates from. Per-side base offsets are
                        // measured outward from the border-box (negative shrinks
                        // toward content, positive grows beyond border).
                        var boxTop = OverflowResolver.ResolveClipMarginVisualBoxTop(style);
                        var boxRight = OverflowResolver.ResolveClipMarginVisualBoxRight(style);
                        var boxBottom = OverflowResolver.ResolveClipMarginVisualBoxBottom(style);
                        var boxLeft = OverflowResolver.ResolveClipMarginVisualBoxLeft(style);
                        // Per-side inset measured from the border-box edge into
                        // the box: 0 for border-box, border for padding-box
                        // (default), border + padding for content-box.
                        double baseTop = boxTop == OverflowClipMarginBox.BorderBox ? 0
                            : boxTop == OverflowClipMarginBox.ContentBox ? box.BorderTop + box.PaddingTop
                            : box.BorderTop;
                        double baseRight = boxRight == OverflowClipMarginBox.BorderBox ? 0
                            : boxRight == OverflowClipMarginBox.ContentBox ? box.BorderRight + box.PaddingRight
                            : box.BorderRight;
                        double baseBottom = boxBottom == OverflowClipMarginBox.BorderBox ? 0
                            : boxBottom == OverflowClipMarginBox.ContentBox ? box.BorderBottom + box.PaddingBottom
                            : box.BorderBottom;
                        double baseLeft = boxLeft == OverflowClipMarginBox.BorderBox ? 0
                            : boxLeft == OverflowClipMarginBox.ContentBox ? box.BorderLeft + box.PaddingLeft
                            : box.BorderLeft;
                        double rectLeft = baseLeft - left;
                        double rectTop = baseTop - top;
                        double rectRight = box.Width - baseRight + right;
                        double rectBottom = box.Height - baseBottom + bottom;
                        contentRect = new Rect(
                            rectLeft,
                            rectTop,
                            Math.Max(0, rectRight - rectLeft),
                            Math.Max(0, rectBottom - rectTop)
                        );
                        // Radii are anchored to the border edge; pairing them
                        // with the inflated rect would round the wrong
                        // rectangle. Drop them so the clip-margin halo clips
                        // as a rectangle.
                        clipRadii = BorderRadii.Zero;
                    }
                }
                output.Add(commandPool.RentPushClip(contentRect, clipRadii));
                pushedClip++;
            }
        }

        // Resolves the filter / transform / opacity wrapper state for `box`
        // and appends the matching push commands to `output` (the active
        // PaintList, NOT a cache). Returns the resolved FilterChain so the
        // decoration path can take the drop-shadow synthetic-shadow short-
        // circuit. The pushed* out params let VisitBox emit matching pops
        // after recursing into children.
        //
        // CSS Filter Effects L1: filter applies BEFORE transform on the
        // owning element. We honour that by folding the box's transform
        // into the PushFilter scope (ScopeBoxTransform) — the filter scope
        // rasterises with identity transform, then composite applies the
        // box transform. Without this split, the per-scope blur cache
        // would invalidate every frame whenever the filtered element's
        // transform animates (aurora-drift in match3 is the canonical case).
        FilterChain EmitWrappersFresh(Box box, ComputedStyle style, PaintList output,
                                      double absX, double absY,
                                      out int pushedFilter, out int pushedTransform, out int pushedMask, out int pushedOpacity,
                                      out int pushedMixBlendMode,
                                      out double foldedBrightness) {
            pushedFilter = 0;
            pushedTransform = 0;
            pushedMask = 0;
            pushedOpacity = 0;
            pushedMixBlendMode = 0;
            foldedBrightness = 1.0;
            if (!IsDecoratable(box, style)) return FilterChain.Empty;
            // O(1) bypass for plain boxes — no transform / filter / opacity
            // / transform-origin set on this style. The cascade flips
            // HasWrapperProperties to true the first time any wrapper prop
            // is written, so this skips three resolver calls per non-
            // wrapper box per frame. Single most-frequent fast path on
            // a deep tree (audit: ~half of 269 boxes per frame had no
            // wrapper props at all).
            if (!style.HasWrapperProperties) return FilterChain.Empty;

            // P9 fast-path: a Box that survived the previous Convert with the
            // same (style.Version, box.Version, abs position, contextVersion)
            // has wrapper inputs that haven't shifted — skip the five resolver
            // calls and re-emit the push commands directly from the cached
            // resolved outputs. ANY property mutation (cascade, transition
            // tick, animation tick) bumps style.Version, so this fast-path
            // never returns stale state for an animated transform / opacity /
            // filter — those bypass it every frame the animator runs.
            var wrapperCache = box.WrapperCache;
            long styleVersion = style.Version;
            long boxVersion = box.Version;
            if (wrapperCache != null
                && wrapperCache.Matches(styleVersion, boxVersion, absX, absY,
                                        box.Width, box.Height, contextVersion)) {
                wrapperCacheHits++;
                EmitWrappersFromCache(wrapperCache, output,
                                      out pushedFilter, out pushedTransform, out pushedMask,
                                      out pushedOpacity, out pushedMixBlendMode);
                foldedBrightness = wrapperCache.FoldedBrightness;
                return wrapperCache.Filters;
            }
            wrapperResolveCount++;

            // PushFilter bounds must be in WORLD/screen coordinates, not box-
            // local. The dispatcher's `ComputeRtRect` applies the (outer)
            // transform to the corners of these bounds to produce the
            // filter-scope RT's screen-space rect — without the box's own
            // (absX, absY) baked in, every box whose layout position is
            // resolved through flex/grid (not via a CSS `transform`) ends
            // up with a filter-scope RT placed at (0, 0): the RT viewport
            // origin is wrong, the box's quads at world coords fall
            // outside the RT, and the resulting brightness/contrast/etc.
            // composite either draws nothing or — when a sibling scope's
            // cache state is also in flight — leaks whole-viewport
            // garbage. Threading absX/absY through here makes
            // ComputeRtRect produce the correct screen rect for any box,
            // not just position:fixed ones whose layout coords already
            // were viewport-relative.
            var bounds = new Rect(absX, absY, box.Width, box.Height);
            LengthContext ctx = LengthContextFor(style);
            var filters = FilterResolver.Resolve(style, ctx);
            Transform2D xf = TransformResolver.ResolveTransform(style, bounds.Width, bounds.Height);
            bool foldFilterIntoPaintColors = TryFoldBrightnessFilterIntoPaintColors(box, filters, out foldedBrightness);
            // A lone `filter: drop-shadow(...)` is rendered by the pragmatic
            // synthetic-shadow path in EmitDecorations (a DrawShadow emitted
            // inline), NOT by the offscreen GPU filter scope. Pushing a filter
            // scope here too is redundant AND wrong: the box's own content gets
            // wrapped in a scope whose composite paints OVER later siblings —
            // e.g. story-bubble's silver `.frame` (drop-shadow) ended up covering
            // the dark `.inner` that's painted after it, so the speech box read
            // as a split silver band instead of a dark box with a thin rim.
            // Combined chains (drop-shadow + blur, color-matrix, …) still need
            // the real filter scope, so only the single-drop-shadow case opts out.
            bool isLoneDropShadow = filters.Functions.Count == 1
                                    && filters.Functions[0] is DropShadowFilter;
            bool hasFilter = !filters.IsEmpty && !foldFilterIntoPaintColors && !isLoneDropShadow;
            bool hasTransform = !xf.Equals(Transform2D.Identity);
            if (hasTransform) {
                // Bake the author's transform-origin into the matrix as
                // `Translate(pivot) · M · Translate(-pivot)`. Pivot resolves
                // against the reference box selected by `transform-box`
                // (CSS Transforms L1 §6.2). HTML default is `view-box`
                // which collapses to border-box for non-SVG elements;
                // `content-box` shifts the origin inward by (border + padding).
                // Default origin is (50%, 50%) per CSS Transforms L1 §3.
                double basisX = box.Width;
                double basisY = box.Height;
                double basisOriginX = 0;
                double basisOriginY = 0;
                string transformBox = style.Get(CssProperties.TransformBoxId);
                if (transformBox == "content-box") {
                    double insetLeft = box.BorderLeft + box.PaddingLeft;
                    double insetTop = box.BorderTop + box.PaddingTop;
                    double insetRight = box.BorderRight + box.PaddingRight;
                    double insetBottom = box.BorderBottom + box.PaddingBottom;
                    basisX = System.Math.Max(0, box.Width - insetLeft - insetRight);
                    basisY = System.Math.Max(0, box.Height - insetTop - insetBottom);
                    basisOriginX = insetLeft;
                    basisOriginY = insetTop;
                }
                (double originX, double originY) = ResolveTransformOrigin(style, basisX, basisY, ctx);
                float pivotX = (float)(absX + basisOriginX + originX);
                float pivotY = (float)(absY + basisOriginY + originY);
                float tx = xf.Tx + pivotX - (xf.A * pivotX + xf.C * pivotY);
                float ty = xf.Ty + pivotY - (xf.B * pivotX + xf.D * pivotY);
                xf = new Transform2D(xf.A, xf.B, xf.C, xf.D, tx, ty);
            }
            Rect filterBounds = default;
            if (hasFilter) {
                filterBounds = ComputeFilterScopeBounds(box, absX, absY, bounds);
                output.Add(commandPool.RentPushFilter(filterBounds, filters, hasTransform ? xf : Transform2D.Identity));
                pushedFilter++;
            } else if (hasTransform) {
                output.Add(commandPool.RentPushTransform(xf));
                pushedTransform++;
            }
            var mask = MaskResolver.Resolve(style, box, absX, absY, ctx, ImageRegistry);
            // B16 — if clip-path: path(...) is active, inject a synthetic image-mask
            // layer so the GPU clip path is applied via the coverage rasterizer.
            mask = InjectPathCoverageMaskLayer(style, ctx, bounds, mask);
            if (mask != null) {
                output.Add(commandPool.RentPushMask(bounds, mask));
                pushedMask++;
            }
            float opacity = OpacityResolver.ResolveOpacity(style);
            if (opacity < 1f) {
                output.Add(commandPool.RentPushOpacity(opacity));
                pushedOpacity++;
            }
            // CSS Compositing 1 §6 — mix-blend-mode wraps the entire element
            // (decorations + descendants) so it composites against the
            // backdrop with the named formula. Pushed AFTER opacity so the
            // opacity layer is part of the source the blend formula sees,
            // matching Chrome's "mix-blend-mode composites the source
            // including its own opacity" behaviour.
            MixBlendMode blend = MixBlendModeResolver.Resolve(style);
            if (blend != MixBlendMode.Normal) {
                output.Add(commandPool.RentPushMixBlendMode(blend));
                pushedMixBlendMode++;
            }

            // Subtree-dependency opt-outs: two cases below resolve against
            // DESCENDANT state that doesn't bump this box's style/box
            // Version, so caching would serve stale output on a descendant
            // mutation.
            //   1. Single-brightness filter — TryFoldBrightnessFilterIntoPaintColors
            //      walks the subtree to decide whether to fold the
            //      brightness into the per-paint color (vs. push a filter
            //      scope). A descendant adding an `<img>` or a mask flips
            //      the decision.
            //   2. Non-empty filter chain that's actually pushed (hasFilter)
            //      — ComputeFilterScopeBounds walks the subtree to union
            //      descendant box-shadow / text-shadow extents into the
            //      filter scope rect. A descendant's shadow change shifts
            //      that rect.
            // Both paths are uncommon — most styled boxes set opacity /
            // transform / mask / mix-blend-mode without a filter, and
            // those resolutions are dependency-local to this box. Skipping
            // the stamp for the filter cases is safe and keeps the fast
            // path correct.
            if (IsSingleBrightnessFilter(filters) || hasFilter) {
                if (wrapperCache != null) wrapperCache.Valid = false;
                return filters;
            }

            // Stamp the wrapper cache with this frame's inputs + resolved
            // outputs. The next Convert that sees the same fingerprint hits
            // the fast path above and skips every resolver call.
            if (wrapperCache == null) {
                wrapperCache = wrapperCachePool.Count > 0
                    ? wrapperCachePool.Pop()
                    : new WrapperEmitCache();
                box.WrapperCache = wrapperCache;
                wrapperCachedBoxes.Add(box);
            }
            wrapperCache.Filters = filters;
            wrapperCache.Xf = xf;
            wrapperCache.HasFilter = hasFilter;
            wrapperCache.HasTransform = hasTransform;
            wrapperCache.FoldFilterIntoPaintColors = foldFilterIntoPaintColors;
            wrapperCache.FoldedBrightness = foldedBrightness;
            wrapperCache.FilterBounds = filterBounds;
            wrapperCache.BorderBounds = bounds;
            wrapperCache.Mask = mask;
            wrapperCache.Opacity = opacity;
            wrapperCache.Blend = blend;
            wrapperCache.Stamp(styleVersion, boxVersion, absX, absY, box.Width, box.Height, contextVersion);
            return filters;
        }

        // B16 — if clip-path: path(...) is present, prepend a synthetic image-mask
        // layer carrying the rasterized path coverage so the GPU applies the correct
        // shape clip via the _WevaMaskImage sampler. Returns the combined MaskDefinition
        // (or null if there is no path clip and no author mask).
        //
        // Interplay with author masks:
        //   - If author masks are present, the synthetic layer is prepended so it
        //     composites (Add, multiply) over the author layers. CSS mask layers are
        //     applied bottom-to-top: the LAST entry is the bottom layer and the FIRST
        //     entry composites last. Prepending the synthetic layer makes it the topmost
        //     mask, restricting pixels that author masks already accepted — correct
        //     semantics (clip-path further clips what the mask reveals).
        //   - If there are already MaxRenderedLayers (4) author layers, we drop the
        //     LAST author layer (lowest visual priority) to make room and emit a
        //     one-time Debug.LogWarning.
        //   - Non-PathClipPathShape clips emit NO synthetic layer — polygon/inset/
        //     circle/ellipse shapes are handled by the GPU clip path directly.
        //   - The software path is unaffected: MaskLayer.IsSyntheticClipMask=true causes
        //     SoftwareRasterizer.SampleMaskLayerAlpha to return 1.0 for this layer (the
        //     software path already clips via PathClipPathShape.Contains).
        MaskDefinition InjectPathCoverageMaskLayer(ComputedStyle style, LengthContext ctx,
                                                   Rect bounds, MaskDefinition authorMask) {
            var clipShape = ClipPathResolver.Resolve(style, ctx,
                new Rect(0, 0, bounds.Width, bounds.Height));
            if (!(clipShape is PathClipPathShape pathShape)) return authorMask;

            // Rasterize (or return cached) coverage image and register it.
            var cache = EnsurePathCoverageCache();
            string handle = cache.EnsureRegistered(pathShape, _syntheticImageRegistry);
            if (handle == null) return authorMask;

            // Build the synthetic layer: alpha-mode (use alpha channel of coverage image),
            // Add composite (multiply with below), no-repeat (the image covers exactly the
            // path bounds), tile fills the full path bounds.
            //
            // Coordinate space: pathShape was resolved with a box-local Rect (0,0 origin),
            // so pathShape.Bounds is in box-local coordinates (relative to the element's
            // top-left). Author mask bounds from MaskResolver.Resolve are in world-space
            // (absX/absY baked in). We translate pathBounds to world-space by adding the
            // element's absolute position (bounds.X, bounds.Y from EmitWrappersFresh).
            var localPathBounds = pathShape.Bounds;
            var worldPathBounds = new Rect(
                localPathBounds.X + bounds.X,
                localPathBounds.Y + bounds.Y,
                localPathBounds.Width,
                localPathBounds.Height);
            var synthTile = new BackgroundTile(
                worldPathBounds.Width,
                worldPathBounds.Height,
                originX: 0,
                originY: 0,
                BackgroundRepeatMode.NoRepeat,
                BackgroundRepeatMode.NoRepeat);
            var synthBrush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var synthLayer = new MaskLayer(
                worldPathBounds,
                synthBrush,
                MaskMode.Alpha,
                MaskComposite.Add,
                synthTile,
                isSyntheticClipMask: true);

            // Combine with author mask layers.
            if (authorMask == null || authorMask.IsEmpty) {
                return MaskDefinition.Single(synthLayer);
            }

            var existingLayers = authorMask.Layers;
            int authorCount = existingLayers.Count;
            int maxAuthor = MaskDefinition.MaxRenderedLayers - 1; // 3 slots for authors
            var combined = new List<MaskLayer>(Math.Min(authorCount, maxAuthor) + 1);
            combined.Add(synthLayer); // topmost = composited last = restricts beneath

            if (authorCount > maxAuthor) {
                // Drop the last author layer (lowest visual priority) to stay within 4 total.
                // Log only once per document lifetime to avoid per-frame console spam.
                if (!_pathMaskOverflowWarned) {
                    _pathMaskOverflowWarned = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || TESTVERIFY
                    UnityEngine.Debug.LogWarning(
                        "[Weva/B16] clip-path: path() with 4 author mask-image layers: " +
                        "dropping last author mask layer to make room for the path coverage mask. " +
                        "Reduce mask-image layers to 3 or fewer to keep all layers.");
#endif
                }
                for (int i = 0; i < maxAuthor; i++) combined.Add(existingLayers[i]);
            } else {
                for (int i = 0; i < authorCount; i++) combined.Add(existingLayers[i]);
            }

            return new MaskDefinition(combined);
        }

        // Hit path for the WrapperEmitCache. Mirrors the push-emit half of
        // EmitWrappersFresh one-for-one: same per-output Rent calls, same
        // pushed-* increments, same LIFO push order. The pop counts in
        // VisitBox derive from `pushed*` so this must agree exactly with
        // the miss path to keep the stack balanced.
        void EmitWrappersFromCache(WrapperEmitCache cache, PaintList output,
                                   out int pushedFilter, out int pushedTransform, out int pushedMask,
                                   out int pushedOpacity, out int pushedMixBlendMode) {
            pushedFilter = 0;
            pushedTransform = 0;
            pushedMask = 0;
            pushedOpacity = 0;
            pushedMixBlendMode = 0;
            if (cache.HasFilter) {
                output.Add(commandPool.RentPushFilter(cache.FilterBounds, cache.Filters,
                    cache.HasTransform ? cache.Xf : Transform2D.Identity));
                pushedFilter++;
            } else if (cache.HasTransform) {
                output.Add(commandPool.RentPushTransform(cache.Xf));
                pushedTransform++;
            }
            if (cache.Mask != null) {
                output.Add(commandPool.RentPushMask(cache.BorderBounds, cache.Mask));
                pushedMask++;
            }
            if (cache.Opacity < 1f) {
                output.Add(commandPool.RentPushOpacity(cache.Opacity));
                pushedOpacity++;
            }
            if (cache.Blend != MixBlendMode.Normal) {
                output.Add(commandPool.RentPushMixBlendMode(cache.Blend));
                pushedMixBlendMode++;
            }
        }

        Rect ComputeFilterScopeBounds(Box box, double absX, double absY, Rect borderBounds) {
            var scope = ExpandFilterScopeForSubtree(box, absX, absY, borderBounds, 0);
            return scope.IsEmpty ? borderBounds : scope;
        }

        Rect ExpandFilterScopeForSubtree(Box box, double absX, double absY, Rect scope, int depth) {
            if (box == null || depth >= MaxVisitDepth) return scope;

            var borderBounds = new Rect(absX, absY, box.Width, box.Height);
            scope = Union(scope, borderBounds);

            ComputedStyle style = box.Style;
            LengthContext ctx = LengthContextFor(style);
            if (box is TextRun run) {
                scope = ExpandFilterScopeForTextRun(run, style, ctx, borderBounds, scope);
                return scope;
            }

            scope = ExpandFilterScopeForBoxShadows(style, ctx, borderBounds, scope);

            // Overflow clipping (or contain:paint) constrains descendants before
            // the element's filter is applied, so descendant visual overflow
            // cannot expand the filter source beyond the clipped border box.
            if (OverflowResolver.ShouldClip(style) || ContainmentResolver.HasPaint(style)) return scope;

            var children = box.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                double childAbsX = absX + child.X + child.StickyOffsetX;
                double childAbsY = absY + child.Y + child.StickyOffsetY;
                scope = ExpandFilterScopeForSubtree(child, childAbsX, childAbsY, scope, depth + 1);
            }
            return scope;
        }

        Rect ExpandFilterScopeForTextRun(TextRun run, ComputedStyle style, LengthContext ctx,
                                         Rect runBounds, Rect scope) {
            filterBoundsShadowScratch.Clear();
            if (TextShadowResolver.ResolveTextShadowInto(style, ctx, filterBoundsShadowScratch)) {
                for (int i = 0; i < filterBoundsShadowScratch.Count; i++) {
                    var sh = filterBoundsShadowScratch[i];
                    var shadowBounds = new Rect(
                        runBounds.X + sh.OffsetX,
                        runBounds.Y + sh.OffsetY,
                        runBounds.Width,
                        runBounds.Height);
                    if (sh.BlurRadius > 0) {
                        shadowBounds = Inflate(shadowBounds, Math.Ceiling(sh.BlurRadius * 2.0));
                    }
                    scope = Union(scope, shadowBounds);
                }
            }

            var stroke = TextRunResolver.ResolveTextStroke(style, ctx);
            if (stroke.hasStroke) {
                scope = Union(scope, Inflate(runBounds, stroke.widthPx * 0.5));
            }
            return scope;
        }

        Rect ExpandFilterScopeForBoxShadows(ComputedStyle style, LengthContext ctx,
                                            Rect borderBounds, Rect scope) {
            if (style == null || !style.HasDecorationProperties) return scope;
            pools.ShadowBuffer.Clear();
            BoxShadowResolver.ResolveBoxShadowInto(style, ctx, pools.ShadowBuffer);
            for (int i = 0; i < pools.ShadowBuffer.Count; i++) {
                var sh = pools.ShadowBuffer[i];
                if (sh.Inset) continue;
                double pad = sh.BlurRadius * 2.0 + Math.Abs(sh.SpreadRadius);
                var shadowBounds = new Rect(
                    borderBounds.X + sh.OffsetX - pad,
                    borderBounds.Y + sh.OffsetY - pad,
                    borderBounds.Width + pad * 2.0,
                    borderBounds.Height + pad * 2.0);
                scope = Union(scope, shadowBounds);
            }
            return scope;
        }

        static Rect Inflate(Rect rect, double pad) {
            if (pad <= 0) return rect;
            return new Rect(rect.X - pad, rect.Y - pad, rect.Width + pad * 2.0, rect.Height + pad * 2.0);
        }

        static Rect Union(Rect a, Rect b) {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            double x1 = Math.Min(a.X, b.X);
            double y1 = Math.Min(a.Y, b.Y);
            double x2 = Math.Max(a.Right, b.Right);
            double y2 = Math.Max(a.Bottom, b.Bottom);
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        // Paint phase 2: shadow → background → image → border-image → border →
        // outline → inset-shadow. Only runs when the box is visible (skipped
        // for visibility:hidden / collapse).
        void EmitVisibleDecorations(Box box, ComputedStyle style, Rect bounds, LengthContext ctx,
                                    FilterChain filters, BorderRadii radii, List<PaintCommand> output) {
            // O(1) bypass: cascade marks the style on first non-default
            // decoration-property write. Layout-only containers (flex
            // parents, anonymous wrappers, plain divs used purely for
            // structure) never set any decoration property and skip the
            // five resolver calls below entirely.
            //
            // Caveat: `<img>` content is emitted from
            // EmitImageContent which doesn't look at the flag (no
            // decoration property is set on a bare img). Inline below
            // so we don't lose img coverage on plain images.
            bool isImg = box.Element != null && box.Element.TagName == "img";
            // Synthetic drop-shadow (from `filter: drop-shadow(...)`) is
            // emitted even when the style has no decoration property —
            // it comes from the filter chain, not from box-shadow.
            bool hasSyntheticShadow = filters.Functions.Count == 1
                                      && filters.Functions[0] is DropShadowFilter;
            if (!style.HasDecorationProperties && !isImg && !hasSyntheticShadow) return;
            var backdropFilters = FilterResolver.ResolveBackdrop(style, ctx);
            // CSS Filter Effects 1 §4 + CSS Color L3 §3 — `opacity: 0` makes
            // the element fully transparent including its backdrop-filter
            // output. The wrapper PushOpacity around this decoration block
            // is *supposed* to modulate the composited result, but the
            // backdrop-filter pass renders into the camera target outside
            // the opacity stack (the filter runtime owns its own RT and
            // composites back directly), so a 0-opacity element with
            // backdrop-filter would visibly paint the blurred backdrop
            // anyway. Diagnosed in a real `.discovery-toast`
            // (opacity: 0 default + backdrop-filter: blur(8px) — rendered
            // a visible rectangular blur band at top-center even when the
            // toast was idle). The cheapest spec-correct fix is to skip
            // emission when the effective opacity rounds to zero — the
            // GPU work is wasted anyway and the visible bug disappears.
            // Partial opacity (0 < α < 1) still emits and gets modulated
            // by the wrapper PushOpacity through the normal compositor
            // path; only the exact-zero case short-circuits.
            float backdropOpacity = OpacityResolver.ResolveOpacity(style);
            if (!backdropFilters.IsEmpty && backdropOpacity > 1e-4f) {
                output.Add(commandPool.RentDrawBackdropFilter(bounds, radii, backdropFilters));
                backdropFilterSeenThisConvert++;
            }
            // -- Box shadows (outset pass; inset pass runs at the end so it
            //    paints over the border).
            pools.ShadowBuffer.Clear();
            BoxShadowResolver.ResolveBoxShadowInto(style, ctx, pools.ShadowBuffer);
            // Pragmatic drop-shadow: a single `filter: drop-shadow()` with
            // nothing else emits as a synthetic outset BoxShadow — visually
            // identical for opaque rectangles and avoids the offscreen-RT
            // composite. Combined chains (drop-shadow + blur, ...) still flow
            // through UIRenderGraphFilterRuntime via PushFilter.
            if (filters.Functions.Count == 1 && filters.Functions[0] is DropShadowFilter ds) {
                output.Add(commandPool.RentDrawShadow(bounds, radii,
                    new BoxShadow(ds.OffsetX, ds.OffsetY, ds.BlurRadius, 0, ds.Color, false)));
            }
            for (int si = 0; si < pools.ShadowBuffer.Count; si++) {
                var sh = pools.ShadowBuffer[si];
                if (!sh.Inset) output.Add(commandPool.RentDrawShadow(bounds, radii, sh));
            }

            // -- Background layers. background-clip → paintRect; background-
            //    origin → originRect. Layers are emitted bottom-to-top per
            //    CSS Backgrounds 3 §3.10.
            pools.BackgroundLayers.Clear();
            BackgroundClipOrigin.Resolve(style, box, out var paintRect, out var originRect);
            // CSS Backgrounds 4 `background-clip: text`: the background is
            // painted ONLY where the element's text glyphs are, so it never
            // shows as a box. Full glyph-clipped gradient fill is a future
            // feature (needs the SDF text path to source a gradient brush);
            // until then the correct-direction behaviour is to NOT paint the
            // background box at all and let the text render in its `color`
            // fallback (the standard `-webkit-background-clip:text` fallback
            // authors set, e.g. weva-landing's `.grad { color:#8fb8ff }`).
            // Without this guard a `.num.grad` stat painted the full gradient
            // as an opaque rectangle behind the digits.
            if (!IsBackgroundClipText(style)) {
                BackgroundResolver.ResolveBackgroundLayersInto(style, paintRect, originRect,
                    pools.BackgroundLayers, pools.BrushCache, ImageRegistry, ctx);
            }
            // Occlusion skip: layers are ordered top-first (index 0 paints
            // last); a fully-opaque layer that covers the whole paint rect
            // hides every layer beneath it. Emitting those hidden layers is
            // not just wasted fills — each one carries the box's rounded-rect
            // AA, and a lower layer's AA edge bleeds the backdrop through the
            // opaque layer's own AA edge (coverage compounds as (1-α)²),
            // producing a gray corner fringe on multi-layer backgrounds
            // (menu.html `.card-gradient`: an opaque gradient over a `white`
            // background-color). Find the topmost opaque-covering layer and
            // skip everything below it — fewer draws AND the single remaining
            // silhouette AAs cleanly, exactly once.
            // Guard: `background-blend-mode` (other than normal) blends each
            // layer with the ones beneath it, so a lower layer is NOT hidden
            // even under an opaque upper layer — it contributes to the blend.
            // Disable the occlusion skip whenever any non-normal blend mode is
            // declared (e.g. the radar minimap-bg's `background-blend-mode:
            // overlay` over two gradients).
            //
            // CSS Compositing 1 §9 — background-blend-mode per-layer modes.
            // Resolved once here; null means all-normal (fast path, no wrapping).
            // The mode list applies to IMAGE layers (indices 0..imageLayerCount-1)
            // in declaration order with cycling. background-color is the fixed
            // compositing base and is NEVER blended with a mode; its entry in
            // pools.BackgroundLayers (last index if present) is always Normal.
            var bgBlendModes = BackgroundBlendModeResolver.Resolve(style);
            bool blendsLayers = bgBlendModes != null;
            // Count image layers (distinct from background-color) so the mode
            // list indexes correctly. BackgroundResolver appends background-color
            // as the last entry after all image layers, so imageLayerCount is the
            // total count minus 1 when background-color is present. We detect
            // background-color presence by checking whether the last brush is a
            // SolidColor that was added for background-color. The reliable check:
            // image layers come from background-image (non-null parsed value or
            // non-"none" raw string); we ask BackgroundResolver via the raw value.
            // Simpler: count declared image layers from the raw background-image.
            int imageLayerCount = BackgroundBlendModeResolver.CountImageLayers(style);
            // CSS Compositing 1 §9: element-local blending uses background-color
            // as the compositing base. Resolve it once here so all image layers
            // that need a PushBackgroundBlend carry the same base color.
            // V1 limitation: when multiple image layers stack with non-Normal
            // modes, each blends against ONLY the background-color base — inter-
            // layer blending (image N against image N-1) is not composited at
            // this level. A fuller compositor that accumulates lower layers into
            // a temp surface is a future improvement; for v1 background-color is
            // the correct base for the bottommost image layer and an approximation
            // for layers above it.
            LinearColor bgBlendBase = LinearColor.Transparent;
            if (blendsLayers) {
                var currentColorForBase = ColorResolver.ResolveCurrentColor(style);
                var bgColorParsed = style.GetParsed(CssProperties.BackgroundColorId);
                if (bgColorParsed != null) {
                    ColorResolver.TryResolveParsed(bgColorParsed, currentColorForBase, style, out bgBlendBase);
                }
            }
            int occluderIdx = -1;
            if (!blendsLayers) {
                for (int li = 0; li < pools.BackgroundLayers.Count; li++) {
                    if (IsOpaqueCoveringLayer(pools.BackgroundLayers[li], paintRect)) { occluderIdx = li; break; }
                }
            }
            // ExactSrgbSourceOver (mode 17): wrap the background-color fill
            // in a PushMixBlendMode(ExactSrgbSourceOver) / PopMixBlendMode scope
            // for translucent background-colors (0 < alpha < 1).
            // Chrome composites translucent fills in sRGB; Unity renders in linear;
            // the per-pixel backdrop read that B24 provides lets the GPU perform
            // exact sRGB source-over, matching Chrome exactly.
            //
            // Scope: backdrop-filter elements always get mode-17. Non-backdrop-
            // filter elements also get mode-17 WHEN the document has already
            // emitted at least one DrawBackdropFilter earlier in this Convert pass
            // (backdropFilterSeenThisConvert > 0). This gate avoids adding the
            // backdrop RT + blit overhead to non-glass pages (a game HUD with
            // rgba() buttons but no blur panels pays zero extra cost). When glass
            // IS present the RT is already allocated; the extra per-batch blits
            // (≤6 on a typical glass UI) are the only new cost.
            // Fixes the "Home button white frame too dim" divergence: .nav-link-on
            // (rgba(255,255,255,0.14), no backdrop-filter) previously used the
            // 0.16-lift approximation (~11 sRGB counts of error vs Chrome).
            //
            // DEFAULT ON (EnableExactSrgbGlassCompositing): pool-ownership bug
            // in ReplayTranslated is fixed (PushMixBlendMode re-rents). Flag kept
            // as emergency opt-out (AuthoringGuide §16).
            //
            // Background-color alpha: resolve once here. The layer loop identifies
            // the bg-color fill by li >= imageLayerCount (the same predicate that
            // suppresses mode-cycling on the compositing base).
            bool needsExactSrgbBgColor = false;
            bool inGlassDocument = !backdropFilters.IsEmpty || backdropFilterSeenThisConvert > 0;
            if (EnableExactSrgbGlassCompositing && inGlassDocument && backdropOpacity > 1e-4f) {
                var currentColorForAlpha = ColorResolver.ResolveCurrentColor(style);
                var bgColorParsedForAlpha = style.GetParsed(CssProperties.BackgroundColorId);
                if (bgColorParsedForAlpha != null &&
                    ColorResolver.TryResolveParsed(bgColorParsedForAlpha, currentColorForAlpha, style, out var resolvedBg)) {
                    // Translucent: 0 < alpha < 1. Fully opaque bg is unaffected by
                    // the linear→sRGB compositing gap (opaque src-over is exact in
                    // both spaces). Fully transparent bg emits no layer at all.
                    needsExactSrgbBgColor = resolvedBg.A > 1e-4f && resolvedBg.A < 1f - 1e-4f;
                }
            }
            // When mode-17 wrapping is needed, suppress the occlusion skip: an
            // opaque image layer sitting above the background-color fill would
            // otherwise cause the bg-color layer to be culled before the loop
            // reaches it, and the sRGB wrap would never fire. Like background-
            // blend-mode, the bg-color fill is load-bearing (it's the value we
            // must composite in sRGB), so every layer including the ones "under"
            // an opaque upper layer must be visited.
            if (needsExactSrgbBgColor) occluderIdx = -1;
            int lowestVisible = occluderIdx >= 0 ? occluderIdx : pools.BackgroundLayers.Count - 1;
            for (int li = lowestVisible; li >= 0; li--) {
                var layer = pools.BackgroundLayers[li];
                if (layer == null) continue;
                // CSS Compositing 1 §9 — background-blend-mode: wrap image layers
                // with PushBackgroundBlend/PopBackgroundBlend (element-local blend)
                // when the mode is non-Normal. This is spec-correct: blending is
                // ELEMENT-LOCAL against the background-color base, NOT against the
                // page backdrop (_WevaBackdrop). Using PushMixBlendMode here was
                // spec-wrong — it blended gradient tiles against the camera clear
                // color, producing incorrect results vs Chrome.
                //
                // Only image layers (li < imageLayerCount) participate in mode
                // cycling. The background-color layer (li >= imageLayerCount) is
                // always Normal (it IS the compositing base, never blended).
                // Normal layers are emitted without wrapping (zero overhead on the
                // common all-normal path — byte-identical to the pre-feature path).
                MixBlendMode layerBlend = (li < imageLayerCount)
                    ? BackgroundBlendModeResolver.LayerAt(bgBlendModes, li)
                    : MixBlendMode.Normal;
                bool hasLayerBlend = layerBlend != MixBlendMode.Normal;
                if (hasLayerBlend) output.Add(commandPool.RentPushBackgroundBlend(layerBlend, bgBlendBase));
                // ExactSrgbSourceOver: wrap the background-color fill (li >= imageLayerCount)
                // in the mode-17 page-backdrop scope so the shader performs the exact
                // sRGB source-over. Gradient/image layers (li < imageLayerCount) are
                // intentionally NOT wrapped — v1 targets the bg-color-only glass pattern.
                bool hasSrgbWrap = needsExactSrgbBgColor && li >= imageLayerCount;
                if (hasSrgbWrap) output.Add(commandPool.RentPushMixBlendMode(MixBlendMode.ExactSrgbSourceOver));
                // cross-fade() sub-layers carry a per-layer alpha weight < 1.
                // Wrap in PushOpacity/PopOpacity so the renderer modulates the
                // fill. The push/pop are emitted around just this one FillRect
                // (not the whole background block) so adjacent layers are
                // unaffected. LayerAlpha == 1 is the common case — no extra
                // commands emitted.
                bool hasLayerAlpha = layer.LayerAlpha < 0.9999f;
                if (hasLayerAlpha) output.Add(commandPool.RentPushOpacity(layer.LayerAlpha));
                output.Add(commandPool.RentFillRect(paintRect, layer, radii));
                if (hasLayerAlpha) output.Add(PaintCommandSingletons.PopOpacity);
                if (hasSrgbWrap) output.Add(PaintCommandSingletons.PopMixBlendMode);
                if (hasLayerBlend) output.Add(PaintCommandSingletons.PopBackgroundBlend);
            }

            // -- <img> content (atop the background, beneath the border).
            //    object-fit decides the destination rect; needs natural size
            //    from the registry for everything except `fill`.
            if (box.Element != null && box.Element.TagName == "img") {
                EmitImageContent(box, style, bounds, radii, output);
            }

            // -- border-image (CSS Backgrounds 3 §6): 9-slice frame between
            //    the background and the regular per-side border stroke.
            //    Brush.ImageTiled caches by (handle, sourceRect, rendering,
            //    tile) so the ~9 parts share Brush instances across boxes
            //    and frames — without the cache each part allocated a fresh
            //    Brush per cache miss.
            pools.BorderImageBuffer.Clear();
            BorderImageResolver.Resolve(style, box, ImageRegistry, ctx, pools.BorderImageBuffer);
            if (pools.BorderImageBuffer.Count > 0) {
                var biRendering = ImageRenderingResolver.Resolve(style);
                // Seam overlap + layering. The parts are separate quads; where
                // two abut, each quad's edge anti-aliases independently and
                // the backend's SnapSampledFillToPixels rounds strip lengths,
                // so a fractional shared boundary leaves a sub-pixel gap —
                // a visible dark seam line (9slice-demo Method B). Inflate
                // every part's dest by 1px clamped to the parts' union AND
                // paint in REVERSE resolver order (center → edges → corners)
                // so each part's bleed tucks UNDER its neighbours: the top
                // quad at any boundary is the one whose content owns it
                // (corners over edges over center). Painting the center last
                // instead would cut a 1px line of stretched interior into the
                // edge strips' artwork.
                double ux0 = double.MaxValue, uy0 = double.MaxValue, ux1 = double.MinValue, uy1 = double.MinValue;
                for (int bi = 0; bi < pools.BorderImageBuffer.Count; bi++) {
                    var d = pools.BorderImageBuffer[bi].DestRect;
                    if (d.X < ux0) ux0 = d.X;
                    if (d.Y < uy0) uy0 = d.Y;
                    if (d.X + d.Width > ux1) ux1 = d.X + d.Width;
                    if (d.Y + d.Height > uy1) uy1 = d.Y + d.Height;
                }
                const double biBleed = 1.0;
                for (int bi = pools.BorderImageBuffer.Count - 1; bi >= 0; bi--) {
                    var part = pools.BorderImageBuffer[bi];
                    if (part.Handle == null) continue;
                    var d = part.DestRect;
                    double x0 = System.Math.Max(ux0, d.X - biBleed);
                    double y0 = System.Math.Max(uy0, d.Y - biBleed);
                    double x1 = System.Math.Min(ux1, d.X + d.Width + biBleed);
                    double y1 = System.Math.Min(uy1, d.Y + d.Height + biBleed);
                    var bled = new Rect(x0, y0, System.Math.Max(0, x1 - x0), System.Math.Max(0, y1 - y0));
                    // Keep a tiled part's pattern intact under the bleed. On
                    // the REPEAT axis the phase is anchored to the strip
                    // start, so shift the tile origin by however far the
                    // bleed moved the quad's origin. On the NO-REPEAT (cross)
                    // axis the tile spans the whole strip thickness — it must
                    // grow with the bled quad or the strip's artwork shifts
                    // and the bleed sliver renders empty (the round panel's
                    // doubled-corner notch).
                    var tile = part.Tile;
                    if (tile.HasValue && (bled.X != d.X || bled.Y != d.Y
                                          || bled.Width != d.Width || bled.Height != d.Height)) {
                        var t = tile.Value;
                        bool repX = t.RepeatX != BackgroundRepeatMode.NoRepeat;
                        bool repY = t.RepeatY != BackgroundRepeatMode.NoRepeat;
                        tile = new BackgroundTile(
                            repX ? t.TileWidth : bled.Width,
                            repY ? t.TileHeight : bled.Height,
                            repX ? t.OriginX + (d.X - bled.X) : 0,
                            repY ? t.OriginY + (d.Y - bled.Y) : 0,
                            t.RepeatX, t.RepeatY, t.GapX, t.GapY);
                    }
                    // Tiled parts take the batcher's floor/ceil snap (Tile
                    // branch) — already edge-aligned. STRETCH parts flag the
                    // backend to round all four dest edges (B-9SLICE-SNAP) so
                    // abutting parts never AA at a fractional shared boundary.
                    var brush = tile.HasValue
                        ? Brush.BorderImageTiledPart(part.Handle, part.SourceRect, biRendering, tile)
                        : Brush.ImageSlicePart(part.Handle, part.SourceRect, biRendering);
                    output.Add(commandPool.RentFillRect(bled, brush, BorderRadii.Zero));
                }
            }

            // -- Border stroke.
            Borders borders = BorderResolver.ResolveBorders(style, ctx);
            // CSS 2.2 §17.6.2.1: in the collapsed-borders model each shared
            // edge is won by the "stronger" declaration (hidden > wider > style
            // priority > cell > side). Apply the winner rule so the correct
            // border draws on each of the four sides.
            if (box is TableCellBox tcb && CollapsedBorderWinnerResolver.IsCollapsed(tcb)) {
                borders = CollapsedBorderWinnerResolver.Resolve(tcb, borders, ctx);
            }
            // CSS Fragmentation L3 §6.1 — box-decoration-break.
            //
            // clone: each fragment is an independent decoration box with full
            //   borders on all four sides. This is the paint behaviour already
            //   produced by EmitVisibleDecorations for every InlineBox fragment,
            //   so no adjustment is needed under clone.
            //
            // slice (initial): the element is rendered as one unbroken box and
            //   then sliced at the break edges. For LTR inline fragmentation
            //   across line boxes that means:
            //     - the LEFT border of every NON-FIRST fragment is invisible
            //       (the left edge is an internal break, not a visual edge)
            //     - the RIGHT border of every NON-LAST fragment is invisible
            //   We detect fragment position via IsLineFragment (non-first) and
            //   IsLastFragment (set by InlineLayout on the last fragment). A
            //   single-line span has IsLineFragment=false AND IsLastFragment=true
            //   so it receives all four borders — correct.
            //
            //   Layout-side: under slice, Chrome also removes inline-axis
            //   padding/border/margin from the LAYOUT width of break edges so the
            //   line-breaking algorithm sees a wider available run. That requires
            //   changes to InlineLayout and is not yet implemented. The gap is
            //   documented in CSS_OPEN_GAPS.md B22.
            if (box is InlineBox ib) {
                bool isClone = IsBoxDecorationBreakClone(style);
                if (!isClone) {
                    // slice: suppress break-edge borders.
                    bool suppressLeft  = ib.IsLineFragment;        // non-first fragment
                    bool suppressRight = !ib.IsLastFragment;        // non-last fragment
                    if (suppressLeft || suppressRight) {
                        borders = new Borders(
                            borders.Top,
                            suppressRight ? BorderEdge.None : borders.Right,
                            borders.Bottom,
                            suppressLeft  ? BorderEdge.None : borders.Left);
                    }
                }
            }
            if (!borders.IsNone) {
                // ExactSrgbSourceOver for borders: translucent borders on glass elements
                // (e.g. rgba(255,255,255,0.26)) are too bright in Unity's linear
                // compositing vs Chrome's sRGB compositing. Wrap the StrokeBorder in
                // mode-17 when the document already has a glass element (same
                // inGlassDocument guard as bg-color) and at least one border edge is
                // translucent. Single StrokeBorder command covers all four edges.
                bool borderSrgbWrap = EnableExactSrgbGlassCompositing && inGlassDocument
                    && BorderHasTranslucentEdge(borders);
                if (borderSrgbWrap) output.Add(commandPool.RentPushMixBlendMode(MixBlendMode.ExactSrgbSourceOver));
                output.Add(commandPool.RentStrokeBorder(bounds, borders, radii));
                if (borderSrgbWrap) output.Add(PaintCommandSingletons.PopMixBlendMode);
            }

            // -- Column rules (CSS Multicol §4): paint a vertical line centred in
            //    each inter-column gap. column-rule is painted "in the middle of
            //    the gap between two columns" (spec) and does NOT take up space —
            //    the gap's width is fixed regardless of the rule's width.
            if (box is MulticolBox mcb && mcb.UsedColumnCount > 1) {
                EmitColumnRules(mcb, style, ctx, output);
            }

            // -- Outline (CSS UI 4 §3.5): paints around the border edge offset
            //    by outline-offset. The outline corner radius follows the
            //    border-radius: each corner's radius on the outline path equals
            //    the corresponding border-radius corner expanded by outline-offset
            //    (clamped to zero so a large negative offset never produces
            //    negative radii). Per spec the visual shape of the outline is the
            //    same as the border-box path offset outward by outline-offset —
            //    i.e. a rounded outline tracks the rounded border edge.
            OutlineResolver.TryResolve(style, ctx, out var outlineEdge, out double outlineOffset);
            if (outlineEdge.Style != BorderStyle.None && outlineEdge.Width > 0) {
                double oo = outlineOffset + outlineEdge.Width;
                var outlineBounds = new Rect(-oo, -oo, bounds.Width + 2 * oo, bounds.Height + 2 * oo);
                BorderRadii outlineRadii = radii.IsZero
                    ? BorderRadii.Zero
                    : new BorderRadii(
                        ExpandCornerByOffset(radii.TopLeft,     outlineOffset),
                        ExpandCornerByOffset(radii.TopRight,    outlineOffset),
                        ExpandCornerByOffset(radii.BottomRight, outlineOffset),
                        ExpandCornerByOffset(radii.BottomLeft,  outlineOffset));
                output.Add(commandPool.RentStrokeBorder(outlineBounds, Borders.Uniform(outlineEdge), outlineRadii));
            }

            // -- Inset shadows, painted last so they sit on top of the
            //    border + outline (matches every browser).
            for (int si = 0; si < pools.ShadowBuffer.Count; si++) {
                var sh = pools.ShadowBuffer[si];
                if (!sh.Inset) continue;
                // ExactSrgbSourceOver for inset shadows: translucent inset highlights
                // (e.g. inset 0 1px 0 rgba(255,255,255,0.32)) are too bright in linear
                // compositing vs Chrome. Wrap per-shadow so each reads the up-to-date
                // backdrop after prior shadows have composited.
                bool insetShadowSrgbWrap = EnableExactSrgbGlassCompositing && inGlassDocument
                    && sh.Color.A > 1e-4f && sh.Color.A < 1f - 1e-4f;
                if (insetShadowSrgbWrap) output.Add(commandPool.RentPushMixBlendMode(MixBlendMode.ExactSrgbSourceOver));
                output.Add(commandPool.RentDrawShadow(bounds, radii, sh));
                if (insetShadowSrgbWrap) output.Add(PaintCommandSingletons.PopMixBlendMode);
            }
        }

        // CSS Multicol §4: emit a vertical rule centred in each inter-column gap.
        // The rule uses StrokeBorder with only a left-edge set so the backend
        // renders a single vertical line per gap.  rule-width "medium" = 3px per
        // CSS Values §6; rule-style "none" produces no paint (fast exit).
        void EmitColumnRules(MulticolBox mcb, ComputedStyle style, LengthContext ctx,
                             List<PaintCommand> output) {
            // Resolve column-rule-style. Default "none" = no rule drawn.
            var ruleStyleParsed = style.GetParsed(CssProperties.ColumnRuleStyleId);
            string ruleStyleStr = ruleStyleParsed != null
                ? (ruleStyleParsed is CssKeyword kw ? kw.Identifier
                    : ruleStyleParsed is CssIdentifier id ? id.Name
                    : style.Get(CssProperties.ColumnRuleStyleId))
                : style.Get(CssProperties.ColumnRuleStyleId);
            if (string.IsNullOrEmpty(ruleStyleStr) || ruleStyleStr == "none") return;

            // Resolve column-rule-width.
            double ruleWidth;
            var ruleWidthParsed = style.GetParsed(CssProperties.ColumnRuleWidthId);
            if (ruleWidthParsed is CssLength rlen) {
                ruleWidth = rlen.ToPixels(ctx);
            } else {
                // Keyword widths: thin=1, medium=3, thick=5 (CSS Values §6).
                string rawWidth = style.Get(CssProperties.ColumnRuleWidthId);
                if (string.IsNullOrEmpty(rawWidth) || rawWidth == "medium") ruleWidth = 3;
                else if (rawWidth == "thin") ruleWidth = 1;
                else if (rawWidth == "thick") ruleWidth = 5;
                else if (double.TryParse(rawWidth.TrimEnd('p', 'x'), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double rw)) {
                    ruleWidth = rw;
                } else {
                    ruleWidth = 3;
                }
            }
            if (ruleWidth <= 0) return;

            // Resolve column-rule-color (defaults to the element's color = currentcolor).
            LinearColor currentColor = ColorResolver.ResolveCurrentColor(style);
            LinearColor ruleColor = currentColor;
            var ruleColorParsed = style.GetParsed(CssProperties.ColumnRuleColorId);
            if (ruleColorParsed != null) {
                ColorResolver.TryResolveParsed(ruleColorParsed, currentColor, style, out ruleColor);
            } else {
                string rawColor = style.Get(CssProperties.ColumnRuleColorId);
                if (!string.IsNullOrEmpty(rawColor) && rawColor != "currentcolor") {
                    if (CssValue.TryParse(rawColor, out var parsedColor) && parsedColor != null) {
                        ColorResolver.TryResolveParsed(parsedColor, currentColor, style, out ruleColor);
                    }
                }
            }

            // Map border-style keyword to BorderStyle enum.
            BorderStyle ruleStyle = BorderResolver.ParseBorderStyle(ruleStyleStr);
            if (ruleStyle == BorderStyle.None) return;

            // Emit one StrokeBorder per gap (N-1 rules for N columns).
            int N = mcb.UsedColumnCount;
            double colWidth = mcb.UsedColumnWidth;
            double gap = mcb.UsedGap;
            double paddingLeft = mcb.PaddingLeft + mcb.BorderLeft;
            double paddingTop = mcb.PaddingTop + mcb.BorderTop;
            double containerHeight = mcb.Height - mcb.PaddingTop - mcb.PaddingBottom - mcb.BorderTop - mcb.BorderBottom;
            if (containerHeight <= 0) containerHeight = mcb.Height;

            for (int g = 0; g < N - 1; g++) {
                // Centre of the gap in box-local X.
                double gapCenterX = paddingLeft + (g + 1) * colWidth + g * gap + gap * 0.5;
                double ruleLeft = gapCenterX - ruleWidth * 0.5;
                var ruleBounds = new Rect(ruleLeft, paddingTop, ruleWidth, containerHeight);
                var ruleEdge = new BorderEdge(ruleStyle, ruleWidth, ruleColor);
                // Render as a uniform border on a zero-height rect so only the
                // vertical sides are visible — but the StrokeBorder API draws
                // the rectangle edges. Instead we use a 1-sided Borders (left
                // only) on a rect whose width equals ruleWidth, making only the
                // left edge render at full height. Actually it's cleaner to emit
                // as a FillRect using a solid brush, which avoids the border
                // corner cap artefacts from StrokeBorder on a thin rect.
                // Use a FillRect: spec says the rule fills a rectangle ruleWidth
                // wide, centred in the gap, spanning the full column height.
                var brush = Brush.SolidColor(ruleColor);
                // Apply the border-style pattern: only "solid" and the other dash
                // patterns make visual sense here; we emit solid for all non-none.
                // Full dashed/dotted rendering via the StrokeBorder pipeline would
                // require a StrokeBorder with a single left-only edge.
                output.Add(commandPool.RentFillRect(ruleBounds, brush, BorderRadii.Zero));
            }
        }

        // CSS Fragmentation L3 §6.1: returns true when box-decoration-break is
        // `clone` for the given style. The property is non-inherited; its initial
        // value is `slice`. Fast path: no string parse needed — the cascade stores
        // the resolved keyword verbatim.
        static bool IsBoxDecorationBreakClone(ComputedStyle style) {
            if (style == null) return false;
            var raw = style.Get(CssProperties.BoxDecorationBreakId);
            return CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "clone");
        }

        // True when the Borders struct has at least one visible edge whose color
        // is translucent (0 < A < 1). Used by the ExactSrgbSourceOver border
        // wrap gate to skip the mode-17 push/pop overhead on fully-opaque borders.
        static bool BorderHasTranslucentEdge(Borders b) {
            return IsTranslucentBorderEdge(b.Top)
                || IsTranslucentBorderEdge(b.Right)
                || IsTranslucentBorderEdge(b.Bottom)
                || IsTranslucentBorderEdge(b.Left);
        }
        static bool IsTranslucentBorderEdge(BorderEdge e)
            => e.Style != BorderStyle.None && e.Width > 0
               && e.Color.A > 1e-4f && e.Color.A < 1f - 1e-4f;

        // True when a background layer is fully opaque AND covers the entire
        // paint rect, so every layer beneath it is invisible. Conservative by
        // design — only solid colors and gradients whose stops are ALL opaque
        // qualify, and only when the brush isn't tiled (a tile can leave gaps
        // between repeats that reveal lower layers). Images never qualify: we
        // don't know their alpha without decoding. A non-tiled linear/conic/
        // radial gradient always fills the whole rect, so opaque stops ⇒ full
        // opaque coverage.
        static bool IsOpaqueCoveringLayer(Brush b, Rect paintRect) {
            if (b == null) return false;
            // cross-fade() sub-layers carry a per-layer alpha weight; even an
            // all-opaque gradient at LayerAlpha=0.5 is translucent overall.
            if (b.LayerAlpha < 0.9999f) return false;
            // 1) Fully opaque paint? Solid colors and gradients whose stops are
            //    ALL opaque qualify; images never do (alpha unknown without
            //    decoding).
            bool opaque;
            switch (b.Kind) {
                case BrushKind.SolidColor:
                    opaque = b.Color.A >= 0.999f;
                    break;
                case BrushKind.Gradient:
                    var g = b.GradientValue;
                    if (g?.Stops == null || g.Stops.Count == 0) return false;
                    opaque = true;
                    for (int i = 0; i < g.Stops.Count; i++) {
                        if (g.Stops[i].Color.A < 0.999f) { opaque = false; break; }
                    }
                    break;
                default:
                    return false;
            }
            if (!opaque) return false;
            // 2) Covers the whole paint rect? A non-tiled brush stretches to
            //    fill it. A tiled brush covers iff it leaves no gaps — either it
            //    repeats with zero gap on an axis, or a single tile already
            //    spans that axis (origin ≤ 0 and tile ≥ paint extent). A
            //    no-repeat tile smaller than the box (or a non-zero gap) can
            //    reveal lower layers, so it does NOT occlude.
            if (!b.Tile.HasValue) return true;
            var t = b.Tile.Value;
            bool coversX = (t.RepeatX == BackgroundRepeatMode.Repeat && t.GapX <= 0.001)
                           || (t.OriginX <= 0.001 && t.OriginX + t.TileWidth >= paintRect.Width - 0.001);
            bool coversY = (t.RepeatY == BackgroundRepeatMode.Repeat && t.GapY <= 0.001)
                           || (t.OriginY <= 0.001 && t.OriginY + t.TileHeight >= paintRect.Height - 0.001);
            return coversX && coversY;
        }

        // <img>-element content fill. CSS Images 3 §5 object-fit decides the
        // destination rect. Default `fill` paints stretched to bounds; the
        // other keywords need the natural size from the IImageRegistry.
        // CSS Images 3 §6 object-position then shifts the produced rect
        // inside the box. With the default `object-position: 50% 50%`
        // this is a no-op (centered placement matches the legacy code).
        void EmitImageContent(Box box, ComputedStyle style, Rect bounds, BorderRadii radii, List<PaintCommand> output) {
            string src = box.Element.GetAttribute("src");
            if (string.IsNullOrEmpty(src)) return;
            var rendering = ImageRenderingResolver.Resolve(style);

            // 9-slice fast path: when the source carries native nine-slice
            // metadata (Unity sprite.border or similar) and the author hasn't
            // opted out via `object-fit: fill`, paint the image as 9 parts
            // (corners + edges + center) using the source's pixel slice
            // values. Corners stay unstretched; edges and center stretch to
            // fill the box. This matches Unity UGUI's Image with Sliced
            // sprite, so a `<img src="frame.png">` whose sprite was
            // configured with borders in the importer Just Works without any
            // CSS.
            if (ImageRegistry != null
                && ImageRegistry.TryResolve(src, out var imgSource) && imgSource != null
                && imgSource.Width > 0 && imgSource.Height > 0
                && imgSource is Weva.Paint.Images.IImageNineSliceSource ns
                && ns.TryGetNineSlice(out var slice) && !slice.IsEmpty) {
                EmitImageNineSlice(src, bounds, slice, imgSource.Width, imgSource.Height, rendering, output);
                return;
            }

            string fit = ResolveObjectFitKeyword(style);
            Rect destRect = bounds;
            if (!string.IsNullOrEmpty(fit) && fit != "fill" && ImageRegistry != null
                && ImageRegistry.TryResolve(src, out var imgSource2) && imgSource2 != null
                && imgSource2.Width > 0 && imgSource2.Height > 0) {
                destRect = ApplyObjectFit(fit, bounds, imgSource2.Width, imgSource2.Height, style, LengthContextFor(style));
            }
            // Brush.ImageFullRect caches the (src, rendering) pair — every
            // <img> with the same source returns the same Brush instance.
            output.Add(commandPool.RentFillRect(destRect, Brush.ImageFullRect(src, rendering), radii));
        }

        // Paints a 9-slice source into `bounds` as 9 FillRect commands. The
        // source's pixel slice insets define both source UV ranges (corners
        // sample exactly the corner pixels of the source) and dest sizes
        // (corners paint at source-pixel size, scaled proportionally only if
        // the box is too small to fit both opposite borders). Edges and
        // center stretch to fill the remaining area. Mirrors the geometry
        // in BorderImageResolver but without border-image-outset / border-
        // width complications — the `<img>` box is itself the dest area.
        void EmitImageNineSlice(string src, Rect bounds, Weva.Paint.Images.ImageNineSlice slice,
                                int srcW, int srcH, Weva.Paint.ImageRenderingMode rendering,
                                List<PaintCommand> output) {
            double wLeft = slice.Left;
            double wRight = slice.Right;
            double wTop = slice.Top;
            double wBottom = slice.Bottom;

            // Clamp opposite borders if the box is narrower / shorter than
            // the source's combined corner pixels — keep the proportion so
            // the corner aspect doesn't shear.
            if (wLeft + wRight > bounds.Width && wLeft + wRight > 0) {
                double scale = bounds.Width / (wLeft + wRight);
                wLeft *= scale; wRight *= scale;
            }
            if (wTop + wBottom > bounds.Height && wTop + wBottom > 0) {
                double scale = bounds.Height / (wTop + wBottom);
                wTop *= scale; wBottom *= scale;
            }

            double dCenterW = System.Math.Max(0, bounds.Width - wLeft - wRight);
            double dCenterH = System.Math.Max(0, bounds.Height - wTop - wBottom);

            float u0 = (float)(slice.Left / (double)srcW);
            float u1 = (float)((srcW - slice.Right) / (double)srcW);
            float v0 = (float)(slice.Top / (double)srcH);
            float v1 = (float)((srcH - slice.Bottom) / (double)srcH);
            float wL = (float)(slice.Left / (double)srcW);
            float wR = (float)(slice.Right / (double)srcW);
            float wT = (float)(slice.Top / (double)srcH);
            float wB = (float)(slice.Bottom / (double)srcH);

            double ax = bounds.X;
            double ay = bounds.Y;
            double aw = bounds.Width;
            double ah = bounds.Height;

            // Source-rect V is BOTTOM-UP. The image sampler maps a quad's UV.y
            // through `lerp(v1, v0, uv.y)` to compensate for Unity textures'
            // bottom-left origin, so a full <img> (src.Y 0..1) is upright with
            // src.Y measured from the bottom. The 9-slice slices are computed
            // top-down (slice.Top is the TOP inset), so each part's source Y
            // must be flipped into bottom-up space — otherwise the top and
            // bottom edges/corners sample the opposite half of the sprite and
            // render swapped. Bottom-up anchors:
            //   top border rows    → [1 - wT, 1]   (V start = 1 - wT)
            //   bottom border rows → [0, wB]       (V start = 0)
            //   center rows        → [wB, 1 - wT]  (V start = wB)
            float topV = 1f - wT;     // V start of the top-edge source band
            float botV = 0f;          // V start of the bottom-edge source band
            float midV = wB;          // V start of the center source band
            float midVH = v1 - v0;    // center source height (unchanged)

            // Half-texel inset to kill seam bleed. Bilinear filtering at a
            // slice boundary samples HALF A TEXEL past it into the neighbouring
            // slice — at the border↔center seam that neighbour is the
            // transparent/dark interior, so a thin dark line appears between
            // the parts. Pulling each source sub-rect inward by half a texel
            // keeps the bilinear footprint inside the slice's own texels.
            float tu = 0.5f / srcW;
            float tv = 0.5f / srcH;
            Rect SI(float x, float y, float w, float h) =>
                new Rect(x + tu, y + tv, System.Math.Max(0f, w - 2f * tu), System.Math.Max(0f, h - 2f * tv));

            // Seam overlap. The 9 parts are separate quads; where two abut, each
            // quad's edge anti-aliases independently and the source-over
            // compositing leaves a (1-a) backdrop term at the shared pixel — a
            // thin dark seam between the border and the center. Inflate every
            // part's DEST by half a pixel on all sides and clamp to the overall
            // box, so adjacent parts overlap by ~1px and each seam pixel is
            // fully covered by at least one opaque part. The source UV is
            // unchanged (a sub-pixel dest stretch, invisible).
            double bx0 = ax, by0 = ay, bx1 = ax + aw, by1 = ay + ah;
            // 1px overlap so adjacent parts cover each other's edge-AA band
            // (the source-over (1-a) backdrop bleed at a shared seam). Survives
            // the backend's SnapSampledFillToPixels rounding on most edges.
            const double bleed = 1.0;
            Rect DI(double x, double y, double w, double h) {
                double x0 = System.Math.Max(bx0, x - bleed);
                double y0 = System.Math.Max(by0, y - bleed);
                double x1 = System.Math.Min(bx1, x + w + bleed);
                double y1 = System.Math.Min(by1, y + h + bleed);
                return new Rect(x0, y0, System.Math.Max(0, x1 - x0), System.Math.Max(0, y1 - y0));
            }

            // Paint order: center FIRST, then edges, then corners — each
            // part's 1px bleed must tuck UNDER its neighbours so the top
            // quad at any shared boundary is the one whose content owns it.
            // The old order (corners, edges, center-on-top) made the
            // center's bleed paint 1px of stretched interior OVER the edge
            // strips' inner border artwork — a visible line cut into the
            // frame on every side (9slice-demo Method A).
            // Center
            EmitSlicePart(src, rendering, DI(ax + wLeft, ay + wTop, dCenterW, dCenterH), SI(u0, midV, u1 - u0, midVH), output);
            // Edges (4) — stretched to fill the gap between corners
            EmitSlicePart(src, rendering, DI(ax + wLeft, ay, dCenterW, wTop), SI(u0, topV, u1 - u0, wT), output);
            EmitSlicePart(src, rendering, DI(ax + aw - wRight, ay + wTop, wRight, dCenterH), SI(u1, midV, wR, midVH), output);
            EmitSlicePart(src, rendering, DI(ax + wLeft, ay + ah - wBottom, dCenterW, wBottom), SI(u0, botV, u1 - u0, wB), output);
            EmitSlicePart(src, rendering, DI(ax, ay + wTop, wLeft, dCenterH), SI(0, midV, wL, midVH), output);
            // Corners (4) — painted last, on top of the edge bleeds
            EmitSlicePart(src, rendering, DI(ax, ay, wLeft, wTop), SI(0, topV, wL, wT), output);
            EmitSlicePart(src, rendering, DI(ax + aw - wRight, ay, wRight, wTop), SI(u1, topV, wR, wT), output);
            EmitSlicePart(src, rendering, DI(ax + aw - wRight, ay + ah - wBottom, wRight, wBottom), SI(u1, botV, wR, wB), output);
            EmitSlicePart(src, rendering, DI(ax, ay + ah - wBottom, wLeft, wBottom), SI(0, botV, wL, wB), output);
        }

        void EmitSlicePart(string src, Weva.Paint.ImageRenderingMode rendering, Rect dest, Rect source, List<PaintCommand> output) {
            if (dest.Width <= 0 || dest.Height <= 0) return;
            // ImageSlicePart, not Image: flags the backend to snap all four
            // dest edges to device pixels (B-9SLICE-SNAP) so abutting parts
            // share exact integer boundaries instead of AA-ing independently
            // at a fractional seam (the residual ~1px dim band the 1px bleed
            // couldn't fully hide).
            output.Add(commandPool.RentFillRect(dest, Brush.ImageSlicePart(src, source, rendering), BorderRadii.Zero));
        }

        // Reads box-local cached commands out of `cached` and appends pool-rented
        // copies with bounds translated by (dx, dy) to the active list. Pop
        // singletons (stateless) and PushTransform / PushOpacity (no bounds) pass
        // through without translation. Steady-state hot path: a single command
        // costs one pool stack pop + one Set() + one List<>.Add — no allocation.
        //
        // Dispatch is via the PaintCommandKind discriminator on the base class so
        // the JIT compiles this to a single int-switch jump table instead of an
        // 8-arm `isinst` cascade. FillRect dominates by frequency on typical UI
        // (background per box) so it's listed first; DrawText / StrokeBorder /
        // DrawShadow follow before the rare push/pop wrappers.
        void ReplayTranslated(List<PaintCommand> cached, PaintList list, double dx, double dy) {
            using var _rtScope = Weva.Profiling.PerfMarkerScope.Auto(Weva.Profiling.UIProfilerMarkers.PaintReplayTranslated);
            int n = cached.Count;
            var listCommands = list.Commands;
            for (int i = 0; i < n; i++) {
                var c = cached[i];
                switch (c.Kind) {
                    case PaintCommandKind.FillRect: {
                        var fr = (FillRectCommand)c;
                        listCommands.Add(commandPool.RentFillRect(
                            Translate(fr.Bounds, dx, dy),
                            ApplyActiveColorBrightness(fr.Brush),
                            fr.Radii));
                        break;
                    }
                    case PaintCommandKind.DrawText: {
                        var dt = (DrawTextCommand)c;
                        var translatedBounds = Translate(dt.Bounds, dx, dy);
                        var color = ApplyActiveColorBrightness(dt.Color);
                        LinearColor? decorationColor = dt.HasDecorationColor
                            ? ApplyActiveColorBrightness(dt.DecorationColor)
                            : (LinearColor?)null;
                        // Stroke phantoms carry BlurRadius via the 7-arg
                        // overload; main glyph commands carry decoration via
                        // the 10-arg overload. The two overload shapes are
                        // mutually exclusive in production, so dispatch here
                        // lossless.
                        DrawTextCommand replayed;
                        if (dt.BlurRadius > 0) {
                            replayed = commandPool.RentDrawText(
                                translatedBounds, dt.Text, dt.Font, color, dt.Decoration, dt.LetterSpacingPx, dt.BlurRadius);
                        } else {
                            replayed = commandPool.RentDrawText(
                                translatedBounds, dt.Text, dt.Font, color, dt.Decoration, dt.LetterSpacingPx,
                                decorationColor,
                                dt.DecorationStyle, dt.DecorationThickness, dt.DecorationOffset);
                        }
                        // Set()-style overloads reset the flag/extension fields,
                        // so late-added state must be copied explicitly or the
                        // replayed command silently loses it (the original
                        // background-clip:text gradient vanished exactly here).
                        replayed.SetKerningEnabled(dt.KerningEnabled);
                        replayed.SetTextFillGradient(dt.TextFillGradient);
                        replayed.SetLayoutBaseline(dt.LayoutBaseline);
                        listCommands.Add(replayed);
                        break;
                    }
                    case PaintCommandKind.StrokeBorder: {
                        var sb = (StrokeBorderCommand)c;
                        listCommands.Add(commandPool.RentStrokeBorder(
                            Translate(sb.Bounds, dx, dy),
                            ApplyActiveColorBrightness(sb.Borders),
                            sb.Radii));
                        break;
                    }
                    case PaintCommandKind.DrawShadow: {
                        var ds = (DrawShadowCommand)c;
                        listCommands.Add(commandPool.RentDrawShadow(
                            Translate(ds.Bounds, dx, dy),
                            ds.Radii,
                            ApplyActiveColorBrightness(ds.Shadow)));
                        break;
                    }
                    case PaintCommandKind.DrawBackdropFilter: {
                        var db = (DrawBackdropFilterCommand)c;
                        listCommands.Add(commandPool.RentDrawBackdropFilter(Translate(db.Bounds, dx, dy), db.Radii, db.Filters));
                        // Mirror fresh-emission counter: a cache-miss child processed
                        // after this cache-hit replay still sees the flag > 0.
                        backdropFilterSeenThisConvert++;
                        break;
                    }
                    case PaintCommandKind.PushClip: {
                        var pc = (PushClipCommand)c;
                        listCommands.Add(commandPool.RentPushClip(Translate(pc.Bounds, dx, dy), pc.Radii));
                        break;
                    }
                    case PaintCommandKind.PushClipPath: {
                        var pc = (PushClipPathCommand)c;
                        listCommands.Add(commandPool.RentPushClipPath(pc.Shape.Translate(dx, dy)));
                        break;
                    }
                    case PaintCommandKind.PushMask: {
                        var pm = (PushMaskCommand)c;
                        listCommands.Add(commandPool.RentPushMask(Translate(pm.Bounds, dx, dy), pm.Mask.Translate(dx, dy)));
                        break;
                    }
                    case PaintCommandKind.PushFilter: {
                        var pf = (PushFilterCommand)c;
                        listCommands.Add(commandPool.RentPushFilter(Translate(pf.Bounds, dx, dy), pf.Filters, pf.ScopeBoxTransform));
                        break;
                    }
                    case PaintCommandKind.PushOpacity: {
                        var po = (PushOpacityCommand)c;
                        listCommands.Add(commandPool.RentPushOpacity(po.Opacity));
                        break;
                    }
                    case PaintCommandKind.PushTransform: {
                        var pt = (PushTransformCommand)c;
                        listCommands.Add(commandPool.RentPushTransform(pt.Transform));
                        break;
                    }
                    case PaintCommandKind.PushMixBlendMode: {
                        // MUST re-rent, never pass through to the default arm:
                        // the cached instance is pool-owned; appending it to
                        // the live list lets Painter.Return(list) Reset() it
                        // INSIDE the cache — the mode silently degrades to
                        // Normal from the second frame on. This is exactly how
                        // the B3e mode-17 glass wrap (and B25's bg-blend wrap
                        // below) went inert on steady-state frames while the
                        // fresh-converter probe looked correct. No geometry —
                        // mode payload only, no translation needed.
                        var pmb = (PushMixBlendModeCommand)c;
                        listCommands.Add(commandPool.RentPushMixBlendMode(pmb.Mode));
                        break;
                    }
                    case PaintCommandKind.PushBackgroundBlend: {
                        // Same pool-ownership hazard as PushMixBlendMode above
                        // (CSS Compositing 1 §9 element-local blend; mode +
                        // base color payload, position-independent).
                        var pbb = (PushBackgroundBlendCommand)c;
                        listCommands.Add(commandPool.RentPushBackgroundBlend(pbb.Mode, pbb.BaseColor));
                        break;
                    }
                    default:
                        // Pop singletons, or anything stateless — append directly.
                        // NOTE: this arm is ONLY safe for PaintCommandSingletons
                        // (stateless shared instances). Any pool-RENTED command
                        // kind that lands here gets corrupted by the frame-end
                        // Return (see PushMixBlendMode above) — add an explicit
                        // re-rent case when introducing new stateful kinds.
                        listCommands.Add(c);
                        break;
                }
            }
        }

        static Rect Translate(Rect r, double dx, double dy) {
            return new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
        }

        LinearColor ApplyActiveColorBrightness(LinearColor color) {
            if (activeColorBrightness == 1.0) return color;
            float b = (float)activeColorBrightness;
            return new LinearColor(color.R * b, color.G * b, color.B * b, color.A);
        }

        Brush ApplyActiveColorBrightness(Brush brush) {
            if (brush == null || activeColorBrightness == 1.0) return brush;
            switch (brush.Kind) {
                case BrushKind.SolidColor:
                    return Brush.SolidColor(ApplyActiveColorBrightness(brush.Color));
                case BrushKind.Gradient:
                    return Brush.Gradient(ApplyActiveColorBrightness(brush.GradientValue), brush.Tile);
                default:
                    return brush;
            }
        }

        Gradient ApplyActiveColorBrightness(Gradient gradient) {
            if (gradient == null || activeColorBrightness == 1.0) return gradient;
            var sourceStops = gradient.Stops;
            var stops = new GradientStop[sourceStops.Count];
            for (int i = 0; i < sourceStops.Count; i++) {
                var s = sourceStops[i];
                stops[i] = new GradientStop(ApplyActiveColorBrightness(s.Color), s.Position);
            }

            switch (gradient) {
                case LinearGradient lg:
                    return new LinearGradient(lg.AngleDegrees, stops, lg.InterpolationSpace, lg.IsRepeating);
                case RadialGradient rg:
                    return new RadialGradient(rg.CenterX, rg.CenterY, rg.RadiusX, rg.RadiusY,
                        rg.Shape, stops, rg.InterpolationSpace, rg.HueMethod, rg.IsRepeating);
                case ConicGradient cg:
                    return new ConicGradient(cg.FromAngleDegrees, cg.CenterX, cg.CenterY,
                        stops, cg.InterpolationSpace, cg.HueMethod, cg.IsRepeating);
                default:
                    return gradient;
            }
        }

        Borders ApplyActiveColorBrightness(Borders borders) {
            if (activeColorBrightness == 1.0) return borders;
            return new Borders(
                ApplyActiveColorBrightness(borders.Top),
                ApplyActiveColorBrightness(borders.Right),
                ApplyActiveColorBrightness(borders.Bottom),
                ApplyActiveColorBrightness(borders.Left));
        }

        BorderEdge ApplyActiveColorBrightness(BorderEdge edge) {
            if (activeColorBrightness == 1.0) return edge;
            return new BorderEdge(edge.Style, edge.Width, ApplyActiveColorBrightness(edge.Color));
        }

        BoxShadow ApplyActiveColorBrightness(BoxShadow shadow) {
            if (activeColorBrightness == 1.0) return shadow;
            return new BoxShadow(shadow.OffsetX, shadow.OffsetY, shadow.BlurRadius,
                shadow.SpreadRadius, ApplyActiveColorBrightness(shadow.Color), shadow.Inset);
        }

        // Reads object-fit from the per-style parsed cache and returns the
        // already-lowercased keyword identifier so ApplyObjectFit can switch
        // on it. The parser emits CssKeyword for spec values and
        // CssIdentifier for any unrecognised ident (which we forward
        // verbatim so the fallthrough in ApplyObjectFit treats it as `fill`).
        // Falls back to the raw string read when neither branch matches —
        // covers null slots and any future grammar additions before this
        // helper learns to recognise them.
        static string ResolveObjectFitKeyword(ComputedStyle style) {
            var parsed = style.GetParsed(CssProperties.ObjectFitId);
            if (parsed is CssKeyword k) return k.Identifier;
            if (parsed is CssIdentifier id) return id.Name;
            return style.Get(CssProperties.ObjectFitId);
        }

        // CSS Images 3 §5: compute the painted destination rect for a
        // replaced element given its `object-fit` keyword and the natural
        // image dimensions, then shift the rect inside the box per CSS
        // Images 3 §6 `object-position`. Unknown fit keywords fall back
        // to `fill` (i.e. the original `bounds`). For `cover` the image
        // is intentionally larger than the box on at least one axis; the
        // resulting rect's overflow direction is selected by position
        // (e.g. `object-position: top` for a `cover` portrait crops the
        // bottom rather than the top). object-position reuses the
        // background-position resolver — same <position> grammar.
        static Rect ApplyObjectFit(string fit, Rect bounds, double natW, double natH, ComputedStyle style, LengthContext ctx) {
            if (natW <= 0 || natH <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return bounds;
            double w = bounds.Width, h = bounds.Height;
            double targetW = w, targetH = h;
            if (fit == "none") {
                targetW = natW;
                targetH = natH;
            } else if (fit == "contain" || fit == "scale-down") {
                double scale = System.Math.Min(w / natW, h / natH);
                if (fit == "scale-down" && scale > 1.0) scale = 1.0;
                targetW = natW * scale;
                targetH = natH * scale;
            } else if (fit == "cover") {
                double scale = System.Math.Max(w / natW, h / natH);
                targetW = natW * scale;
                targetH = natH * scale;
            } else {
                return bounds; // fill (or unrecognized) — paint to bounds.
            }
            // Resolve object-position. The resolver computes an offset in
            // [0, box - tile] for non-negative residuals, so a missing /
            // default value (50% 50%) reproduces the legacy centered
            // placement. For `cover` the residual is negative (tile is
            // bigger than box); the resolver handles negative ranges by
            // multiplying the percentage normally, which produces the
            // correct overflow direction.
            double posX = 0.5 * (w - targetW);
            double posY = 0.5 * (h - targetH);
            if (style != null) {
                var posParsed = style.GetParsed(CssProperties.ObjectPositionId);
                if (posParsed != null) {
                    BackgroundLayoutResolver.ResolvePosition(
                        posParsed, w, h, targetW, targetH, ctx, style,
                        out posX, out posY);
                }
            }
            double offsetX = bounds.X + posX;
            double offsetY = bounds.Y + posY;
            return new Rect(offsetX, offsetY, targetW, targetH);
        }


        // Emits form-control overlay text (currently just placeholder) for an
        // `<input>` / `<textarea>` box at absolute coordinates `absX`/`absY`.
        // BoxBuilder never produces children for these elements (their content
        // is the `value` attribute, not DOM children), so without this hook the
        // input renders as an empty styled rectangle. We render placeholder
        // text when the value is empty and a `placeholder` attribute is set,
        // using the cascaded ::placeholder style for color when one is wired
        // via PlaceholderStyleOf, else a faded currentColor fallback.
        //
        // Paints the input's value text, and — for the focused control — a
        // selection highlight + caret bar. The caret/selection geometry comes
        // from InputCaretOf (wired to the focused InputController's TextEditModel),
        // since the converter has no direct handle on the edit model.
        void EmitInputOverlays(Box box, PaintList list, double absX, double absY) {
            var element = box.Element;
            if (element == null) return;
            // A closed <select> renders the selected option's label as its own
            // text. The <option> elements are display:none (UA sheet), so no box
            // carries that text — paint it here, like the <input> value overlay.
            if (element.TagName == "select") { EmitSelectLabel(box, list, absX, absY); return; }
            // <textarea>: the VALUE renders through the normal text pipeline
            // (child TextNode → LineBox/TextRun, painted before this overlay),
            // so the overlay adds only the selection highlight + caret bar
            // (audit #6). Painted AFTER the glyphs: the caret sits on top like
            // Chrome's; the highlight relies on a translucent color (the UA
            // default is 40% alpha; authored ::selection colors are clamped —
            // glyph-recolor-inside-selection is not implemented).
            if (element.TagName == "textarea") {
                var overlay = TextAreaOverlayOf?.Invoke(element);
                if (overlay.HasValue) EmitTextAreaOverlay(box, list, absX, absY, overlay.Value);
                return;
            }
            if (element.TagName != "input") return;
            string type = element.GetAttribute("type");
            // Native <input type=range>: a thin groove + accent fill + round knob.
            if (type == "range") { EmitRangeTrack(box, list, absX, absY); return; }
            // Native <input type=checkbox|radio>: accent-colored tick / dot when
            // checked (the element's border/background paints the empty box).
            if (type == "checkbox") { EmitCheckboxGlyph(box, list, absX, absY); return; }
            if (type == "radio") { EmitRadioGlyph(box, list, absX, absY); return; }
            // Render value/placeholder as text for every type EXCEPT the ones with
            // distinct or no rendering. range/checkbox/radio/select handled above;
            // hidden/file/submit/reset/button/image don't show editable text.
            // date/time/color/datetime-local/month/week + unknown types fall back to
            // a text box (matching the browser's unknown-type behavior).
            if (type == "hidden" || type == "file" || type == "submit" ||
                type == "reset" || type == "button" || type == "image") return;

            string value = element.GetAttribute("value") ?? "";

            ComputedStyle style = box.Style;
            LengthContext ctx = LengthContextFor(style);
            double fontSize = ResolveFontSize(style, ctx);
            double padLeft = box.PaddingLeft + box.BorderLeft;
            double padTop = box.PaddingTop + box.BorderTop;
            double padBottom = box.PaddingBottom + box.BorderBottom;
            double padRight = box.PaddingRight + box.BorderRight;
            double availW = Math.Max(0, box.Width - padLeft - padRight);
            // CSS UI: single-line <input> vertically centers content within the
            // content box; use the line-height: normal glyph-span factor (shared
            // via StyleResolver.DefaultLineHeightFactor so this can't drift).
            double availH = Math.Max(0, box.Height - padTop - padBottom);
            double glyphSpan = fontSize * Weva.Layout.StyleResolver.DefaultLineHeightFactor;
            double textY = absY + padTop + Math.Max(0, (availH - glyphSpan) * 0.5);
            double contentX = absX + padLeft;

            // Caret + selection geometry for the focused control (px offsets from
            // contentX). Null when this element isn't the focused editable.
            InputCaretGeometry? caret = InputCaretOf?.Invoke(element);

            // TX4/audit #7: clip the editable overlay to the content box and
            // scroll the text horizontally. The offset is the controller's
            // PERSISTENT EditScrollX (Chrome model: the window moves only
            // when the caret would leave it — navigating left reveals
            // context without boundary jumps, and text after the caret stays
            // visible). Bare callers constructing the geometry without a
            // scroll (NaN) keep the old stateless caret-follow derivation.
            double scrollX = 0;
            if (caret.HasValue) {
                if (!double.IsNaN(caret.Value.ScrollX)) {
                    scrollX = Math.Max(0, caret.Value.ScrollX);
                } else {
                    const double caretSlack = 2.0; // the 1px bar + a hair of context
                    double cxRel = caret.Value.CaretX;
                    if (cxRel + caretSlack > availW) scrollX = cxRel + caretSlack - availW;
                }
            }
            bool clipped = availW > 0 && availH > 0;
            if (clipped) {
                list.Add(commandPool.RentPushClip(
                    new Rect(contentX, absY + padTop, availW, availH), BorderRadii.Zero));
            }

            // Selection highlight — painted BEHIND the text so glyphs sit on top.
            // Height = the glyph span (line box), not bare fontSize — Chrome's
            // highlight covers ascent+descent; fontSize undershot descenders
            // (input/selection audit #8).
            if (caret.HasValue && caret.Value.HasSelection) {
                double xs = contentX - scrollX + caret.Value.SelectionStartX;
                double xe = contentX - scrollX + caret.Value.SelectionEndX;
                if (xe > xs) {
                    list.Add(commandPool.RentFillRect(new Rect(xs, textY, xe - xs, glyphSpan),
                        Brush.SolidColor(ResolveSelectionColor(style, element)), BorderRadii.Zero));
                }
            }

            if (value.Length != 0) {
                // Real value: host color; password masks to bullets so the
                // attribute's plain-text never renders on screen. PF2: the
                // mask is cached (was minted per repaint, per blink flip).
                string text = type == "password" ? Weva.Forms.InputRenderer.BulletMask(value.Length) : value;
                LinearColor valueColor = style != null
                    ? ColorResolver.ResolveCurrentColor(style)
                    : LinearColor.Black;
                valueColor = ApplyActiveColorBrightness(valueColor);
                FontHandle valueFont = fontResolver != null
                    ? fontResolver(style)
                    : TextRunResolver.BuildFont(style, ctx, pools.FontCache, fontSize);
                // TX4: shift by the edit-scroll; widen the rect by the scroll
                // so the visible window always has glyph coverage (the clip
                // trims anything outside the content box).
                list.Add(commandPool.RentDrawText(new Rect(contentX - scrollX, textY, availW + scrollX, fontSize),
                    text, valueFont, valueColor, default, 0));
            } else {
                // Empty value — paint the placeholder (if any). ::placeholder
                // cascaded color, else a faded host currentColor.
                string placeholderText = element.GetAttribute("placeholder");
                if (!string.IsNullOrEmpty(placeholderText)) {
                    ComputedStyle placeholderStyle = PlaceholderStyleOf != null
                        ? PlaceholderStyleOf(element)
                        : null;
                    LinearColor color;
                    if (placeholderStyle != null) {
                        string raw = placeholderStyle.Get("color");
                        if (!string.IsNullOrEmpty(raw)) {
                            var hostCurrent = style != null
                                ? ColorResolver.ResolveCurrentColor(style)
                                : LinearColor.Black;
                            if (!ColorResolver.TryResolve(raw, hostCurrent, placeholderStyle, out color)) {
                                color = FadedCurrentColor(style);
                            }
                        } else {
                            color = FadedCurrentColor(style);
                        }
                    } else {
                        color = FadedCurrentColor(style);
                    }
                    color = ApplyActiveColorBrightness(color);
                    FontHandle font = fontResolver != null
                        ? fontResolver(placeholderStyle ?? style)
                        : TextRunResolver.BuildFont(placeholderStyle ?? style, ctx, pools.FontCache, fontSize);
                    list.Add(commandPool.RentDrawText(new Rect(contentX, textY, availW, fontSize),
                        placeholderText, font, color, default, 0));
                }
            }

            // Caret bar — a 1px vertical, painted ON TOP of the text at the caret
            // index. Hidden during the blink-off phase (caret.CaretVisible) AND
            // while a selection is non-collapsed — Chrome shows the highlight
            // OR the caret, never both (input/selection audit #3). Height =
            // glyph span, matching the selection rect (audit #8).
            if (caret.HasValue && caret.Value.CaretVisible && !caret.Value.HasSelection) {
                double cx = contentX - scrollX + caret.Value.CaretX;
                list.Add(commandPool.RentFillRect(new Rect(cx, textY, 1.0, glyphSpan),
                    Brush.SolidColor(ResolveCaretColor(style)), BorderRadii.Zero));
            }
            if (clipped) list.Add(PaintCommandSingletons.PopClip);
        }

        void EmitTextAreaOverlay(Box box, PaintList list, double absX, double absY,
                                 TextAreaOverlayGeometry overlay) {
            ComputedStyle style = box.Style;
            // Clip to the content box so hung trailing spaces / the caret at a
            // hanging position don't paint outside the control.
            double padLeft = box.PaddingLeft + box.BorderLeft;
            double padTop = box.PaddingTop + box.BorderTop;
            double availW = Math.Max(0, box.Width - padLeft - box.PaddingRight - box.BorderRight);
            double availH = Math.Max(0, box.Height - padTop - box.PaddingBottom - box.BorderBottom);
            bool clipped = availW > 0 && availH > 0;
            if (clipped) {
                list.Add(commandPool.RentPushClip(
                    new Rect(absX + padLeft, absY + padTop, availW, availH), BorderRadii.Zero));
            }
            if (overlay.SelectionRects != null && overlay.SelectionRects.Count > 0) {
                var color = ResolveSelectionColor(style, box.Element);
                // The glyphs are already painted UNDER this highlight (no
                // glyph recolor pass) — clamp opaque authored ::selection
                // colors so the selected text stays readable.
                if (color.A > 0.5f) color = new LinearColor(color.R, color.G, color.B, 0.5f);
                foreach (var r in overlay.SelectionRects) {
                    if (r.W <= 0 || r.H <= 0) continue;
                    list.Add(commandPool.RentFillRect(new Rect(absX + r.X, absY + r.Y, r.W, r.H),
                        Brush.SolidColor(color), BorderRadii.Zero));
                }
            }
            if (overlay.CaretVisible && overlay.CaretHeight > 0) {
                list.Add(commandPool.RentFillRect(
                    new Rect(absX + overlay.CaretX, absY + overlay.CaretY, 1.0, overlay.CaretHeight),
                    Brush.SolidColor(ResolveCaretColor(style)), BorderRadii.Zero));
            }
            if (clipped) list.Add(PaintCommandSingletons.PopClip);
        }

        // UA-default text selection highlight (translucent blue so glyphs read
        // through), used when no ::selection { background-color } is authored.
        static readonly LinearColor InputSelectionColor = new LinearColor(0.20f, 0.45f, 0.95f, 0.40f);

        // CSS Pseudo-Elements 4 §8: ::selection background-color tints the
        // highlight. Falls back to the UA default when no rule set a background.
        LinearColor ResolveSelectionColor(ComputedStyle hostStyle, Element element) {
            ComputedStyle sel = SelectionStyleOf != null && element != null ? SelectionStyleOf(element) : null;
            if (sel == null) return InputSelectionColor;
            string raw = sel.Get(CssProperties.BackgroundColorId);
            if (string.IsNullOrEmpty(raw) || raw == "transparent") return InputSelectionColor;
            var hostCurrent = hostStyle != null ? ColorResolver.ResolveCurrentColor(hostStyle) : LinearColor.Black;
            return ColorResolver.TryResolve(raw, hostCurrent, sel, out var c) ? c : InputSelectionColor;
        }

        // CSS UI 4 §5.4: caret-color; `auto`/unset → currentColor.
        LinearColor ResolveCaretColor(ComputedStyle style) {
            if (style == null) return LinearColor.Black;
            LinearColor current = ColorResolver.ResolveCurrentColor(style);
            string raw = style.Get(CssProperties.CaretColorId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return current;
            return ColorResolver.TryResolve(raw, current, style, out var c) ? c : current;
        }

        // Native <input type=range>: a thin centred groove (unfilled rail) + an
        // accent fill (rail start → knob centre) + a round knob sized to FIT the
        // content box so it never clips. Geometry mirrors
        // InputRenderer.DrawRangeTrack (pinned by InputRangeRenderTests); this is
        // the LIVE converter path, emitting in absolute paint coords.
        void EmitRangeTrack(Box box, PaintList list, double absX, double absY) {
            var element = box.Element;
            double min = ParseRangeAttr(element, "min", 0.0);
            double max = ParseRangeAttr(element, "max", 100.0);
            if (max <= min) max = min + 1.0;
            double value = ParseRangeAttr(element, "value", (min + max) * 0.5);
            double frac = (value - min) / (max - min);
            if (!(frac >= 0)) frac = 0; else if (frac > 1) frac = 1;

            double left = absX + box.PaddingLeft + box.BorderLeft;
            double trackW = box.Width - box.PaddingLeft - box.PaddingRight - box.BorderLeft - box.BorderRight;
            if (trackW <= 0) return;
            double contentH = box.Height - box.PaddingTop - box.PaddingBottom - box.BorderTop - box.BorderBottom;
            if (contentH <= 0) contentH = box.Height;
            double cy = absY + box.PaddingTop + box.BorderTop + contentH * 0.5;

            LinearColor accent = ResolveRangeAccent(box.Style);
            double railH = Math.Min(contentH, 6.0);
            double thumbD = Math.Max(railH, Math.Min(contentH, 14.0));
            double railTop = cy - railH * 0.5;
            double usable = Math.Max(0.0, trackW - thumbD);
            double cx = left + thumbD * 0.5 + frac * usable;

            var groove = new LinearColor(accent.R, accent.G, accent.B, accent.A * 0.3f);
            list.Add(commandPool.RentFillRect(new Rect(left, railTop, trackW, railH),
                Brush.SolidColor(groove), BorderRadii.Uniform(railH * 0.5)));
            double fillW = cx - left;
            if (fillW > 0) {
                list.Add(commandPool.RentFillRect(new Rect(left, railTop, fillW, railH),
                    Brush.SolidColor(accent), BorderRadii.Uniform(railH * 0.5)));
            }
            list.Add(commandPool.RentFillRect(new Rect(cx - thumbD * 0.5, cy - thumbD * 0.5, thumbD, thumbD),
                Brush.SolidColor(accent), BorderRadii.Uniform(thumbD * 0.5)));
        }

        // Native <input type=checkbox> tick: an accent-filled inset rounded rect
        // when checked. Mirrors InputRenderer.DrawCheckboxGlyph in absolute coords
        // (the dead test-only path); this is the LIVE converter path.
        void EmitCheckboxGlyph(Box box, PaintList list, double absX, double absY) {
            var element = box.Element;
            if (element == null || !element.HasAttribute("checked")) return;
            const double inset = 2.0;
            double w = box.Width - inset * 2, h = box.Height - inset * 2;
            if (w <= 0 || h <= 0) return;
            list.Add(commandPool.RentFillRect(new Rect(absX + inset, absY + inset, w, h),
                Brush.SolidColor(ResolveRangeAccent(box.Style)), BorderRadii.Uniform(1)));
        }

        // Native <input type=radio> dot: a centred accent circle when checked.
        // Mirrors InputRenderer.DrawRadioGlyph in absolute coords.
        void EmitRadioGlyph(Box box, PaintList list, double absX, double absY) {
            var element = box.Element;
            if (element == null || !element.HasAttribute("checked")) return;
            double inset = box.Width * 0.25;
            double w = box.Width - inset * 2, h = box.Height - inset * 2;
            if (w <= 0 || h <= 0) return;
            list.Add(commandPool.RentFillRect(new Rect(absX + inset, absY + inset, w, h),
                Brush.SolidColor(ResolveRangeAccent(box.Style)), BorderRadii.Uniform(w * 0.5)));
        }

        static double ParseRangeAttr(Element element, string attr, double fallback) {
            string raw = element?.GetAttribute(attr);
            if (!string.IsNullOrEmpty(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)
                && !double.IsNaN(v) && !double.IsInfinity(v)) {
                return v;
            }
            return fallback;
        }

        // `accent-color` for the range fill/knob; `auto`/unset → indigo (matches
        // InputRenderer.CheckColor).
        static readonly LinearColor RangeAccentDefault = new LinearColor(0.090f, 0.196f, 0.671f, 1f);
        LinearColor ResolveRangeAccent(ComputedStyle style) {
            if (style == null) return RangeAccentDefault;
            string raw = style.Get(CssProperties.AccentColorId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return RangeAccentDefault;
            var current = ColorResolver.ResolveCurrentColor(style);
            if (ColorResolver.TryResolve(raw, current, style, out var c)) return c;
            return RangeAccentDefault;
        }

        // Paints the closed <select>'s selected-option label, mirroring the
        // <input> value overlay (vertically centered, host color/font). Skips
        // multiple/size selects (those expose a listbox where options DO lay
        // out as blocks). A right gutter reserves room for the dropdown arrow.
        void EmitSelectLabel(Box box, PaintList list, double absX, double absY) {
            var element = box.Element;
            if (element.HasAttribute("multiple") || element.HasAttribute("size")) return;
            var selected = new Weva.Forms.SelectElement(element).SelectedOption;
            string text = selected != null ? selected.Label : "";
            if (string.IsNullOrEmpty(text)) return;

            ComputedStyle style = box.Style;
            LengthContext ctx = LengthContextFor(style);
            double fontSize = ResolveFontSize(style, ctx);
            double padLeft = box.PaddingLeft + box.BorderLeft;
            double padTop = box.PaddingTop + box.BorderTop;
            double padBottom = box.PaddingBottom + box.BorderBottom;
            double padRight = box.PaddingRight + box.BorderRight;
            const double arrowGutter = 16.0;
            double availW = Math.Max(0, box.Width - padLeft - padRight - arrowGutter);
            double availH = Math.Max(0, box.Height - padTop - padBottom);
            double glyphSpan = fontSize * Weva.Layout.StyleResolver.DefaultLineHeightFactor;
            double textY = absY + padTop + Math.Max(0, (availH - glyphSpan) * 0.5);

            LinearColor color = style != null
                ? ColorResolver.ResolveCurrentColor(style)
                : LinearColor.Black;
            color = ApplyActiveColorBrightness(color);
            FontHandle font = fontResolver != null
                ? fontResolver(style)
                : TextRunResolver.BuildFont(style, ctx, pools.FontCache, fontSize);
            var bounds = new Rect(absX + padLeft, textY, availW, fontSize);
            list.Add(commandPool.RentDrawText(bounds, text, font, color, default, 0));
        }

        static LinearColor FadedCurrentColor(ComputedStyle style) {
            var host = style != null
                ? ColorResolver.ResolveCurrentColor(style)
                : LinearColor.Black;
            return new LinearColor(host.R * 0.5f, host.G * 0.5f, host.B * 0.5f, host.A * 0.5f);
        }

        // Scratch buffer reused for each EmitTextRun call so a paragraph
        // with N text-shadow declarations doesn't pay N fresh List<TextShadow>
        // allocations. Cleared on entry; returned-to-pool reuse not needed
        // since the buffer never escapes the call.
        readonly List<TextShadow> textShadowScratch = new(4);
        readonly List<TextShadow> filterBoundsShadowScratch = new(4);

        // Cache-aware entry point used by VisitBox. Mirrors the decoration
        // cache flow: validate against (run.Version, style.DecorationVersion,
        // contextVersion); on hit, replay translated commands; on miss,
        // re-emit at box-local origin into the cache and replay.
        //
        // Why this works: a transform/opacity-only animation tick bumps
        // ComputedStyle.Version but NOT DecorationVersion, so the cached
        // text-run commands stay valid across every animation frame. Without
        // this lift, every text run paid the full font / decoration / shadow
        // resolution and N-shadows-plus-glyph rents on every frame.
        void EmitTextRunCached(TextRun run, PaintList list, double absX, double absY) {
            if (string.IsNullOrEmpty(run.Text)) return;
            ComputedStyle style = run.Style;
            if (IsVisibilityHidden(style)) return;
            // PAINT-1 diagnostic — fires only when UILayoutDiagnostics.Enabled
            // and the run's owning element (or its parent's element) class
            // matches MatchClassContains. Reports the absolute origin paint
            // will pass to the SDF baker as DrawTextCommand.Bounds.(X,Y).
            var diagElem = run.Element ?? run.Parent?.Element ?? run.Parent?.Parent?.Element;
            if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(diagElem)) {
                Weva.Diagnostics.UILayoutDiagnostics.TraceFor(diagElem, "Paint.EmitTextRun",
                    $"text='{run.Text}' run.X={run.X} run.Y={run.Y} " +
                    $"run.W={run.Width} run.H={run.Height} fontSize={run.FontSize} " +
                    $"absX={absX} absY={absY} (=Bounds.X/Y passed to SDF baker)");
            }

            var cache = run.PaintCache;
            bool cacheHit = cache != null && cache.IsValid(run, style, contextVersion);

            if (cacheHit) {
                cacheHits++;
                ReplayTranslated(cache.PreChildren, list, absX, absY);
                return;
            }

            cacheMisses++;
            if (cache == null) {
                cache = paintBoxCachePool.Count > 0 ? paintBoxCachePool.Pop() : new PaintBoxCache();
                run.PaintCache = cache;
                cachedBoxes.Add(run);
            } else {
                // Stale entry: rented PaintCommands in PreChildren must
                // return to the pool before Reset clears the list.
                ReturnCachedCommands(cache);
            }
            cache.Reset(run.Version, style != null ? style.DecorationVersion : 0, contextVersion);

            scratchPre.Clear();
            EmitTextRunLocal(run, scratchPre);
            for (int i = 0; i < scratchPre.Count; i++) cache.PreChildren.Add(scratchPre[i]);
            scratchPre.Clear();

            ReplayTranslated(cache.PreChildren, list, absX, absY);
        }

        // True when the element's first background-clip layer is `text`
        // (CSS Backgrounds 4). The cascade stores the keyword verbatim;
        // BackgroundClipOrigin.ParseBox maps the box keywords and falls back to
        // border-box for `text`, so this is the only place that recognises it.
        static bool IsBackgroundClipText(ComputedStyle style) {
            string raw = style?.Get(CssProperties.BackgroundClipId);
            if (string.IsNullOrEmpty(raw)) return false;
            int comma = raw.IndexOf(',');
            string first = comma >= 0 ? raw.Substring(0, comma) : raw;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(first, "text");
        }

        // Builds the DrawTextCommands for a TextRun at LOCAL origin (0,0),
        // appending into `output`. The cache stores these; ReplayTranslated
        // rents fresh copies with bounds shifted by (absX, absY) for the
        // live PaintList. Returning a local-origin form is what makes the
        // cache survive parent-transform animations: only the translate
        // changes per frame, never the cached payload.
        void EmitTextRunLocal(TextRun run, List<PaintCommand> output) {
            ComputedStyle style = run.Style;
            LengthContext ctx = LengthContextFor(style);
            // Use the layout-resolved font-size verbatim. The ctx returned by
            // LengthContextFor has BaseFontSizePx = THIS box's fs, so re-resolving
            // an em-relative font-size value against it would compound the relative
            // factor twice. run.FontSize is the value StyleResolver.FontSizePx
            // computed at layout time against the proper parent base.
            FontHandle font = fontResolver != null
                ? fontResolver(style)
                : TextRunResolver.BuildFont(style, ctx, pools.FontCache, run.FontSize);
            LinearColor color = TextRunResolver.ResolveTextColor(style);
            var (decoration, decoColor, decoStyle, decoThickness, decoOffset) =
                TextRunResolver.ResolveDecorationStyle(style, ctx);
            // letter-spacing is resolved at paint time (the value the agent draws with
            // must match the value the line-breaker measured with — see InlineLayout).
            // CSS Text L3 §7.3 inter-character justify: JustifyLineInterCharacter
            // stores an additional per-run spacing increment in run.JustifyLetterSpacingPx.
            // Add it here so the glyph baker spreads characters to match the layout width.
            double letterSpacingPx = TextRunResolver.ResolveLetterSpacingPx(style, ctx)
                                     + run.JustifyLetterSpacingPx;
            bool kerningEnabled = TextRunResolver.ResolveKerningEnabled(style);
            var bounds = new Rect(0, 0, run.Width, run.Height);

            // CSS Inline Layout §3: the run's alphabetic baseline within its
            // line box. The glyph baker otherwise derives the baseline from the
            // text shaper's own box layout (TextCore bottom-aligns it), which
            // pushes glyphs to the box bottom and mis-centres text in tight
            // line boxes. Passing layout's baseline keeps paint aligned with
            // layout. Measured relative to the run's local box top (Bounds.Y=0
            // here); run.Y is the run's offset inside the line box, so the
            // run-box-relative baseline is lineBox.Baseline - run.Y.
            double layoutBaseline = run.Parent is Weva.Layout.Boxes.LineBox lineBox
                ? lineBox.Baseline - run.Y
                : double.NaN;

            // text-shadow paints UNDER the glyph layer. CSS Text Decoration §6
            // specifies shadows are listed front-to-back: the FIRST listed
            // shadow paints on top of subsequent ones. Emit in reverse so the
            // first listed ends up nearest the glyph (drawn last, on top).
            //
            // Blur handling:
            // non-zero blur is rendered in the SDF text shader. Routing every
            // text-shadow through a filter scope makes common UI patterns such
            // as glowing stars pay one offscreen render target per shadow; the
            // glyph path can carry the same CSS blur radius as instance data
            // and batch with regular text instead.
            //
            // Decorations (underline / line-through) are NOT emitted on the
            // shadow phantom — only the glyph silhouette is shadowed.
            textShadowScratch.Clear();
            if (TextShadowResolver.ResolveTextShadowInto(style, ctx, textShadowScratch)) {
                for (int i = textShadowScratch.Count - 1; i >= 0; i--) {
                    var sh = textShadowScratch[i];
                    var shBounds = new Rect(sh.OffsetX, sh.OffsetY, run.Width, run.Height);
                    if (sh.BlurRadius > 0) {
                        double pad = Math.Ceiling(sh.BlurRadius * 3.0);
                        var filterBounds = new Rect(
                            shBounds.X - pad,
                            shBounds.Y - pad,
                            shBounds.Width + pad * 2.0,
                            shBounds.Height + pad * 2.0);
                        // The shadow glyph renders into the blur filter's OFFSCREEN
                        // scope RT, which has no page backdrop. If an ancestor
                        // pushed a page-backdrop mix-blend mode (e.g. the glass
                        // ExactSrgbSourceOver / mode-17 wrap applied to all text in
                        // a backdrop-filter document), the shader's mode>0 path would
                        // sample _WevaBackdrop and force alpha=1 across the whole
                        // quad — turning the shadow into an opaque backdrop-colored
                        // square (the glass.html .art-note "black square" ghost; the
                        // FP16 scope RT let the forced alpha accumulate past 1).
                        // Reset to Normal inside the scope so the shadow glyph
                        // composites as a plain premultiplied glyph; the scope's
                        // composite-back already handles sRGB-correct blending.
                        output.Add(commandPool.RentPushMixBlendMode(MixBlendMode.Normal));
                        // CSS Backgrounds 3 §6.2 (referenced by Text Decoration §6
                        // for text-shadow): the shadow is blurred by a Gaussian with
                        // standard deviation = HALF the blur radius. BlurFilter's
                        // argument is σ directly (CSS Filter Effects 1 §6.1 / A3b),
                        // so a text-shadow `blur(r)` maps to σ = r/2. Passing the
                        // full radius blurred 2× too wide (visibly softer than Chrome).
                        output.Add(commandPool.RentPushFilter(
                            filterBounds,
                            new FilterChain(new FilterFunction[] { new BlurFilter(sh.BlurRadius * 0.5) })));
                        var shadowCmd = commandPool.RentDrawText(shBounds, run.Text, font, sh.Color, decoration, letterSpacingPx);
                        shadowCmd.SetKerningEnabled(kerningEnabled);
                        shadowCmd.SetLayoutBaseline(layoutBaseline);
                        output.Add(shadowCmd);
                        output.Add(PaintCommandSingletons.PopFilter);
                        output.Add(PaintCommandSingletons.PopMixBlendMode);
                    } else {
                        var shadowCmd = commandPool.RentDrawText(shBounds, run.Text, font, sh.Color, decoration, letterSpacingPx);
                        shadowCmd.SetKerningEnabled(kerningEnabled);
                        shadowCmd.SetLayoutBaseline(layoutBaseline);
                        output.Add(shadowCmd);
                    }
                }
            }

            // CSS Text Decoration L4 §10 `-webkit-text-stroke`: paint a phantom
            // glyph silhouette under the fill with the stroke color and an SDF
            // dilation equal to half the stroke width. The fill on top then
            // covers the inner portion, leaving a `width`-pixel outline visible
            // around the glyph. The dilation still uses the DrawTextCommand
            // BlurRadius field to widen the SDF AA band outward —
            // v1 caveat: the result is a softly-feathered outline rather than
            // a hard-edged stroke. A follow-up that adds a dedicated stroke
            // brushIndex to Weva-Quad.shader and samples `abs(distance) <
            // halfWidth` would give true hard edges; the C# surface here is
            // the same either way.
            var stroke = TextRunResolver.ResolveTextStroke(style, ctx);
            if (stroke.hasStroke) {
                // halfWidth is the SDF dilation amount — the visible outline
                // is twice this (extends symmetrically outward from the glyph
                // silhouette, with the inner half covered by the fill).
                double halfWidth = stroke.widthPx * 0.5;
                var strokeCmd = commandPool.RentDrawText(bounds, run.Text, font, stroke.color, decoration, letterSpacingPx, halfWidth);
                strokeCmd.SetKerningEnabled(kerningEnabled);
                strokeCmd.SetLayoutBaseline(layoutBaseline);
                output.Add(strokeCmd);
            }

            var glyphCmd = commandPool.RentDrawText(bounds, run.Text, font, color, decoration, letterSpacingPx,
                decoColor, decoStyle, decoThickness, decoOffset);
            glyphCmd.SetKerningEnabled(kerningEnabled);
            glyphCmd.SetLayoutBaseline(layoutBaseline);
            // CSS Backgrounds 4 `background-clip: text`: the gradient shows
            // through the text fill, so it only matters when the resolved
            // `color` is not fully opaque (the canonical pattern is
            // `color: transparent`). An opaque color paints over the clipped
            // background in Chrome too — skip the gradient then so those runs
            // keep the cached fast path. The background box is suppressed
            // elsewhere; here we hand the gradient to the SDF backend, which
            // samples it per shaped glyph over the run's bounds and composites
            // the text color over it.
            if (style != null && color.A < 0.999f && IsBackgroundClipText(style)) {
                var grad = ResolveFirstBackgroundGradient(style, ctx);
                if (grad != null) glyphCmd.SetTextFillGradient(grad);
            }
            output.Add(glyphCmd);
        }

        // Resolves the topmost gradient layer of the element's `background` (for
        // `background-clip: text` glyph fill). Returns null when no background
        // layer is a gradient. A LinearGradient carries only angle + stops, so
        // the unit rect passed here doesn't bind it to a box — the backend maps
        // it onto each run's bounds at sample time.
        Gradient ResolveFirstBackgroundGradient(ComputedStyle style, LengthContext lengthCtx) {
            var layers = pools.BackgroundLayers;
            layers.Clear();
            var unit = new Rect(0, 0, 1, 1);
            BackgroundResolver.ResolveBackgroundLayersInto(style, unit, unit,
                layers, pools.BrushCache, ImageRegistry, lengthCtx);
            Gradient found = null;
            for (int i = 0; i < layers.Count; i++) {
                if (layers[i] != null && layers[i].Kind == BrushKind.Gradient) {
                    found = layers[i].GradientValue;
                    break;
                }
            }
            layers.Clear();
            return found;
        }

        LengthContext LengthContextFor(ComputedStyle style) {
            var ctx = baseLengthContext;
            // baseLengthContext is frozen at construction with the BUILD-TIME
            // viewport. Paint-time viewport-relative lengths (vh/vw in
            // border-radius, box-shadow blur, filters, outline…) must resolve
            // against the LIVE viewport or they stay sized for the resolution the
            // document was first built at — corners/shadows then look wrong at any
            // other resolution while layout (which reads the live LayoutContext)
            // scales correctly. WevaDocument.EmitPaint pushes the current viewport
            // into ViewportWidth/Height every frame; mirror it here. Guarded on
            //  > 0 so headless Convert() callers that never set it keep the
            // explicit LengthContext they constructed the converter with.
            if (ViewportWidth > 0) ctx.ViewportWidthPx = ViewportWidth;
            if (ViewportHeight > 0) ctx.ViewportHeightPx = ViewportHeight;
            double fs = ResolveFontSize(style, ctx);
            ctx.BaseFontSizePx = fs;
            if (ctx.RootFontSizePx <= 0) ctx.RootFontSizePx = fs;
            return ctx;
        }

        // CSS Transforms L1 §3 — resolve `transform-origin` to box-local
        // (x, y) pixels. Default `50% 50%` returns box center. Accepts
        // keywords (`top`, `right`, `bottom`, `left`, `center`), lengths,
        // percentages, and `calc()`. A third (Z) component is parsed but
        // ignored for 2D paint (PLAN: engine is 2D-only). When both tokens
        // are axis keywords the pair `<vertical> <horizontal>` is accepted
        // in either order per the spec.
        internal static (double, double) ResolveTransformOrigin(ComputedStyle style, double w, double h, LengthContext ctx) {
            double ox = w * 0.5;
            double oy = h * 0.5;
            if (style == null) return (ox, oy);
            string raw = style.Get(CssProperties.TransformOriginId);
            if (string.IsNullOrEmpty(raw)) return (ox, oy);
            int n = raw.Length;
            int i = 0;
            // Paren-aware token scan so `calc(50% + 10px)` is a single token.
            // Author values are typically `50% 50%` / `top left` / `0 0` which
            // never enter the depth>0 branch.
            int t1Start, t1End, t2Start, t2End;
            if (!ScanToken(raw, ref i, n, out t1Start, out t1End)) return (ox, oy);
            bool has2 = ScanToken(raw, ref i, n, out t2Start, out t2End);
            // Third (Z) token is consumed for spec round-trip but ignored at
            // paint time — 2D engine has no Z axis (PLAN: 2D-only).
            if (has2) ScanToken(raw, ref i, n, out _, out _);
            // Two-token swap: <vertical> <horizontal> -> assign by keyword.
            if (has2) {
                int t1Axis = KeywordAxis(raw, t1Start, t1End);
                int t2Axis = KeywordAxis(raw, t2Start, t2End);
                if (t1Axis == 2 && t2Axis == 1) {
                    int sStart = t1Start, sEnd = t1End;
                    t1Start = t2Start; t1End = t2End;
                    t2Start = sStart; t2End = sEnd;
                }
            }
            double? xResolved = ParseOriginAxisSlice(raw, t1Start, t1End, w, ctx, isX: true);
            if (xResolved.HasValue) ox = xResolved.Value;
            if (!has2) return (ox, h * 0.5);
            double? yResolved = ParseOriginAxisSlice(raw, t2Start, t2End, h, ctx, isX: false);
            if (yResolved.HasValue) oy = yResolved.Value;
            return (ox, oy);
        }

        static bool ScanToken(string raw, ref int i, int n, out int tStart, out int tEnd) {
            while (i < n && (raw[i] == ' ' || raw[i] == '\t')) i++;
            tStart = i;
            int depth = 0;
            while (i < n) {
                char c = raw[i];
                if (depth == 0 && (c == ' ' || c == '\t')) break;
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                i++;
            }
            tEnd = i;
            return tEnd > tStart;
        }

        // 1 = horizontal-axis keyword (left/right), 2 = vertical-axis keyword
        // (top/bottom), 0 = not a single-axis keyword (center, length, calc).
        static int KeywordAxis(string raw, int start, int end) {
            if (SliceEquals(raw, start, end, "left") || SliceEquals(raw, start, end, "right")) return 1;
            if (SliceEquals(raw, start, end, "top") || SliceEquals(raw, start, end, "bottom")) return 2;
            return 0;
        }

        static double? ParseOriginAxisSlice(string raw, int start, int end, double basis, LengthContext ctx, bool isX) {
            int len = end - start;
            if (len <= 0) return null;
            // Keyword fast path — compare without allocating substrings.
            if (SliceEquals(raw, start, end, "left")) return isX ? 0 : (double?)null;
            if (SliceEquals(raw, start, end, "right")) return isX ? basis : (double?)null;
            if (SliceEquals(raw, start, end, "top")) return !isX ? 0 : (double?)null;
            if (SliceEquals(raw, start, end, "bottom")) return !isX ? basis : (double?)null;
            if (SliceEquals(raw, start, end, "center")) return basis * 0.5;
            // calc(...) — route through the full parser with the axis as the
            // percentage basis. Allocates the substring once per calc token.
            //
            // DD5 — every calc() failure path returns null (caller treats
            // null as "use default" and silently drops the malformed
            // background-position component). Behaviour is preserved, but
            // we now route through UICssDiagnostics so authors see that
            // their malformed `calc(...)` was rejected. Warn is deduped per
            // offending token by UICssDiagnostics's (source, detail) key.
            if (len > 5
                && (raw[start] == 'c' || raw[start] == 'C')
                && (raw[start + 1] == 'a' || raw[start + 1] == 'A')
                && (raw[start + 2] == 'l' || raw[start + 2] == 'L')
                && (raw[start + 3] == 'c' || raw[start + 3] == 'C')
                && raw[start + 4] == '(' && raw[end - 1] == ')') {
                string calcToken = raw.Substring(start, len);
                try {
                    var parsed = CssValueParser.Parse(calcToken);
                    if (parsed is CssCalc calc) {
                        return calc.Evaluate(ctx.WithBasis(basis));
                    }
                } catch (CssValueParseException) {
                    WarnParseOriginAxisSliceFailure(calcToken);
                    return null;
                }
                WarnParseOriginAxisSliceFailure(calcToken);
                return null;
            }
            // %, px, or bare number.
            int numEnd = end;
            bool isPct = raw[end - 1] == '%';
            if (isPct) numEnd = end - 1;
            else if (len >= 2 && raw[end - 2] == 'p' && raw[end - 1] == 'x') numEnd = end - 2;
            if (numEnd <= start) return null;
            // Allocates one substring per axis — unavoidable for double.TryParse
            // without span overloads on this target framework. Author values
            // are dominated by `50% 50%` / `top left` keywords which take the
            // keyword path above and skip this allocation entirely.
            if (double.TryParse(raw.Substring(start, numEnd - start),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) {
                return isPct ? basis * v * 0.01 : v;
            }
            return null;
        }

        // DD5 — emit a deduped diagnostic when ParseOriginAxisSlice's
        // defensive null fallback fires on a malformed calc() token.
        // Behaviour (return null → caller uses default) is preserved; the
        // warn surfaces the malformed background-position to authors.
        // UICssDiagnostics dedupes on the (source, detail) pair, so passing
        // the offending token as the detail gives us per-token dedupe.
        static void WarnParseOriginAxisSliceFailure(string token) {
            Weva.Diagnostics.UICssDiagnostics.Warn("paint", "ParseOriginAxisSlice failed for: " + token);
        }

        static bool SliceEquals(string raw, int start, int end, string kw) {
            int len = end - start;
            if (len != kw.Length) return false;
            for (int i = 0; i < len; i++) if (raw[start + i] != kw[i]) return false;
            return true;
        }

        // Typed display-keyword dispatch backed by the per-style parsed cache.
        // CssKeyword.Identifier is constructed lowercased, so the `none`
        // match is a single string-equality compare on the cached interned
        // string. CssIdentifier falls back to the case-insensitive trim
        // comparator. Returns false when the slot was unset or didn't parse
        // (treat as visible) — matches the prior code path's behaviour.
        static bool IsDisplayNone(ComputedStyle style) {
            var parsed = style.GetParsed(CssProperties.DisplayId);
            if (parsed is CssKeyword k) return k.Identifier == "none";
            if (parsed is CssIdentifier id) return CssStringUtil.EqualsIgnoreCaseTrimmed(id.Name, "none");
            // Slot missing / not a keyword (e.g. malformed value the parser
            // returned as something else) — fall through to the raw read so
            // hot reloads / inline-style edge cases stay accurate. The exact
            // `none` check up front is the steady-state fast path (parser
            // outputs lower-cased canonical form); the
            // EqualsIgnoreCaseTrimmed call only fires for whitespace-padded
            // / mixed-case raw values, which the typed path above already
            // handles for parsable styles.
            string raw = style.Get(CssProperties.DisplayId);
            if (raw == null) return false;
            if (raw == "none") return true;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none");
        }

        // True when the computed `visibility` is `hidden` or `collapse`. Per CSS
        // Box 3 §3.2 / CSS UI 4 §9, both keywords suppress the box's own paint
        // (decorations + text) but preserve the layout slot. `collapse` only
        // affects layout for flex / grid / table-row items — outside those
        // contexts it behaves as `hidden`.
        //
        // The per-style parsed cache returns visibility as a CssKeyword whose
        // .Identifier is already lowercase, so the hot path is two
        // string-equality compares (`hidden` / `collapse`) on an interned
        // value. CssIdentifier handles the unusual `Visibility` /
        // ` hidden ` shapes via the case-insensitive trim comparator, which
        // covers everything the prior hand-rolled byte loop did.
        static bool IsVisibilityHidden(ComputedStyle style) {
            if (style == null) return false;
            var parsed = style.GetParsed(CssProperties.VisibilityId);
            if (parsed is CssKeyword k) {
                string id = k.Identifier;
                return id == "hidden" || id == "collapse";
            }
            if (parsed is CssIdentifier i) {
                string name = i.Name;
                return CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hidden")
                    || CssStringUtil.EqualsIgnoreCaseTrimmed(name, "collapse");
            }
            return false;
        }

        // CSS 2.1 §17.6.1.1 / CSS Tables L3 §11: in the separate-borders model,
        // `empty-cells: hide` suppresses the borders and backgrounds of cells
        // with no in-flow content. The collapsed-borders model ignores the
        // property (collapsed borders belong to the table, not the cell).
        // empty-cells is inherited, so the cell's own style carries the
        // value resolved at the table.
        static bool IsHiddenEmptyTableCell(Box box, ComputedStyle style) {
            if (!(box is TableCellBox) || style == null) return false;
            string emptyCells = style.Get(CssProperties.EmptyCellsId);
            if (string.IsNullOrEmpty(emptyCells)
                || !CssStringUtil.EqualsIgnoreCaseTrimmed(emptyCells, "hide")) return false;
            string borderCollapse = style.Get(CssProperties.BorderCollapseId);
            if (!string.IsNullOrEmpty(borderCollapse)
                && CssStringUtil.EqualsIgnoreCaseTrimmed(borderCollapse, "collapse")) return false;
            return SubtreeHasNoCellContent(box);
        }

        // Conservative empty-cell probe: empty iff the only descendants are
        // layout-internal containers (LineBox, AnonymousBlockBox /
        // AnonymousInlineBox, InlineBox) or whitespace-only TextRuns.
        // Anything else — a child element box, an <img>, a non-whitespace
        // TextRun, generated ::before/::after content — counts as content
        // and keeps the cell non-empty.
        static bool SubtreeHasNoCellContent(Box node) {
            var children = node.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                var child = children[i];
                if (child == null) continue;
                if (child is TextRun tr) {
                    string t = tr.Text;
                    if (string.IsNullOrEmpty(t)) continue;
                    for (int j = 0; j < t.Length; j++) {
                        if (!char.IsWhiteSpace(t[j])) return false;
                    }
                    continue;
                }
                if (child is LineBox
                    || child is AnonymousBlockBox
                    || child is AnonymousInlineBox
                    || child is InlineBox) {
                    if (!SubtreeHasNoCellContent(child)) return false;
                    continue;
                }
                return false;
            }
            return true;
        }

        static double ResolveFontSize(ComputedStyle style, LengthContext ctx) {
            if (style == null) return ctx.BaseFontSizePx > 0 ? ctx.BaseFontSizePx : 16;
            // The per-style parsed cache lets the typed dispatch (CssLength /
            // CssNumber / CssPercentage / CssCalc) skip the
            // CssValue.TryParse hop on every paint frame — `font-size` is
            // read for every box and every text run. The absolute-size
            // keyword shortcuts (`small` / `large` / etc) are matched on
            // CssKeyword.Identifier which is already lowercase, so no
            // Trim/ToLowerInvariant allocation either.
            var parsed = style.GetParsed(CssProperties.FontSizeId);
            if (parsed is CssKeyword k) {
                switch (k.Identifier) {
                    case "medium": return ctx.BaseFontSizePx > 0 ? ctx.BaseFontSizePx : 16;
                    case "small": return ctx.BaseFontSizePx * 0.85;
                    case "large": return ctx.BaseFontSizePx * 1.2;
                    case "x-small": return ctx.BaseFontSizePx * 0.75;
                    case "x-large": return ctx.BaseFontSizePx * 1.5;
                    case "xx-small": return ctx.BaseFontSizePx * 0.6;
                    case "xx-large": return ctx.BaseFontSizePx * 2.0;
                }
                // Unrecognised keyword — drop through to the raw fallback.
            }
            if (parsed is CssLength len) return len.ToPixels(ctx);
            if (parsed is CssNumber num) return num.Value;
            if (parsed is CssPercentage p) return ctx.BaseFontSizePx * p.Value * 0.01;
            // CSS Values L4 §10: clamp/min/max/calc resolve to a length
            // when their inputs do. Layout's StyleResolver.FontSizePx
            // already handles this; we have to mirror it here so the
            // paint pass measures + shapes glyphs at the SAME font-size
            // layout did. Without this branch, every clamp()'d font-size
            // fell through to ctx.BaseFontSizePx (typically 16) — every
            // run rendered ~14% wider than it was measured at, causing
            // adjacent runs to visibly overlap (Aerith→Stormborn run-
            // overlap, chat lines packed together, quest title bleed).
            if (parsed is CssCalc c) {
                try { return c.Evaluate(ctx); } catch { /* fall through */ }
            }
            // Slot is empty / parsed-failed / has a shape we don't recognise
            // — the prior code returned ctx.BaseFontSizePx for the
            // empty/medium cases and after a CssValue.TryParse miss, so
            // mirror that here. Also returns medium-default 16 when both
            // the slot AND the inherited base size are unset.
            string raw = style.Get(CssProperties.FontSizeId);
            if (string.IsNullOrEmpty(raw) || raw == "medium") {
                return ctx.BaseFontSizePx > 0 ? ctx.BaseFontSizePx : 16;
            }
            return ctx.BaseFontSizePx;
        }

        // CSS UI 4 §3.5 — expand a border-radius corner outward by outline-offset.
        // Each component of the corner (XRadius / YRadius) grows by the offset;
        // the result is clamped to zero so a large negative offset never yields a
        // negative radius. A zero corner stays at zero (zero + negative clamps to 0).
        static CornerRadius ExpandCornerByOffset(CornerRadius corner, double offset) {
            double rx = Math.Max(0, corner.XRadius + offset);
            double ry = Math.Max(0, corner.YRadius + offset);
            return new CornerRadius(rx, ry);
        }
    }
}
