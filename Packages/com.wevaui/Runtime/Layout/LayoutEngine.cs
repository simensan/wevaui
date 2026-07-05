using System;
using System.Collections.Generic;
using Weva.Compiled;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using Weva.Layout.Incremental;
using Weva.Layout.Multicol;
using Weva.Layout.Positioning;
using Weva.Layout.Scrolling;
using Weva.Layout.Tables;
using Weva.Layout.Text;
using Weva.Profiling;
using Weva.Reactive;

namespace Weva.Layout {
    public sealed partial class LayoutEngine {
        readonly IFontMetrics defaultMetrics;
        readonly Dictionary<Element, LayoutCacheEntry> cache = new();
        // Per-engine box pool and per-pass scratch. Mirror CascadePools (v0.2.2)
        // and PaintConverterPools (v0.2.3): one shared instance that gets reset at
        // the start of each Layout pass. A single LayoutEngine assumes
        // single-threaded use; concurrent layouts must use distinct engines.
        readonly BoxPool boxPool = new();
        readonly LayoutScratch scratch = new();
        // PositioningPass holds no per-call state; reuse the same instance every
        // Layout call to skip the constructor allocation entirely.
        readonly PositioningPass positioningPass = new();
        // The four formatting-context passes are stateless across calls modulo
        // their ctx field (refreshed via Reset). Holding them as engine-level
        // fields kills four `new` allocations per Layout pass — together with
        // AnchorSizePass.resolvedSizes reuse this is the bulk of the v0.7 ->
        // v0.8 alloc-floor reduction.
        readonly InlineLayout inlineLayout;
        readonly BlockLayout blockLayout;
        readonly FlexLayout flexLayout;
        readonly GridLayout gridLayout;
        readonly TableLayout tableLayout;
        readonly MulticolLayout multicolLayout;
        readonly List<FlexBox> flexPassBoxes = new(64);
        readonly List<FlexBox> thirdFlexPassBoxes = new(8);
        readonly List<GridBox> gridPassBoxes = new(16);
        readonly List<TableBox> tablePassBoxes = new(4);
        readonly List<MulticolBox> multicolPassBoxes = new(8);
        readonly Weva.Layout.AnchorPositioning.AnchorSizePass anchorSizePass = new();
        readonly ScrollContainer scrollContainer = new();
        // P5: per-engine scratch list for Apply(tracker)'s "elements to drop
        // from cache" sweep. Pre-fix each Apply call lazily allocated `new
        // List<Element>()` whenever any dirty entry was layout/style relevant
        // — once per UIDocumentLifecycle Update that had dirty entries.
        // Hoisted to an instance field; cleared at the top of Apply, filled
        // during, never reallocated. Typical dirty sets are small (a few
        // elements per frame from hover / focus / class toggles), so 16
        // entries pre-sizes the common case without forcing a resize.
        readonly List<Element> scratchToDrop = new(16);
        readonly ScrollLayout scrollLayout;
        readonly StickyResolver stickyResolver;
        // When true, Layout reads a DomSnapshot off LayoutContext.Snapshot (or
        // the cascade-built one supplied by UIDocumentLifecycle) and walks the
        // NodeId arrays to build the box tree instead of dereferencing the
        // managed Element tree. Default true; falls back to managed automatically
        // when no snapshot is supplied. Mirrors CascadeEngine.useSnapshot.
        readonly bool useSnapshot;
        // Reusable SymbolTable for the snapshot path when the cascade hasn't
        // already built one. Held on the engine so ComputeAll-less callers
        // (layout-only test harnesses, paths that run Compute() per element)
        // still get the snapshot speedup without paying for a fresh table per
        // Layout call. Reset is implicit: SymbolTable.Intern is idempotent so
        // reusing the table across calls is safe.
        readonly SymbolTable layoutSymbols;
        // Per-engine reusable SnapshotStyleArray so the NodeId-indexed
        // ComputedStyle buffer is recycled across Layout calls instead of
        // allocating a fresh ComputedStyle[NodeCount] per pass.
        readonly SnapshotStyleArray snapshotStyles = new();
        // Cached delegate over snapshotStyles.At so each Layout call doesn't
        // pay a fresh method-group->Func allocation when constructing the
        // SnapshotBoxBuilder.
        readonly Func<int, ComputedStyle> snapshotStylesAt;
        StyleArray activeSnapshotStyles;
        // Pooled SnapshotBoxBuilder reused across Layout calls. The builder
        // holds no per-pass mutable state; the BoxPool + LayoutScratch it
        // closes over already get reset per pass.
        SnapshotBoxBuilder pooledSnapBuilder;
        // Pooled BoxBuilder reused across Layout / RelayoutOneSubtree
        // calls. Allocating a fresh BoxBuilder per warm flip cost ~500B
        // each in the previous design — measurable GC pressure on a
        // text-heavy fixture at 60Hz. Rebinding the readonly fields
        // (styleOf / backdropStyleOf / imageRegistry) on the existing
        // instance keeps the per-pass state (liOrdinals dict, scratch
        // buffers) warm and skips the allocation.
        BoxBuilder pooledBoxBuilder;

        BoxBuilder GetOrCreatePooledBoxBuilder(Func<Element, ComputedStyle> styleOf) {
            if (pooledBoxBuilder == null) {
                pooledBoxBuilder = new BoxBuilder(styleOf, BackdropStyleOf, ImageRegistry, boxPool, scratch);
            } else {
                pooledBoxBuilder.Rebind(styleOf, BackdropStyleOf, ImageRegistry);
            }
            pooledBoxBuilder.BeforeStyleOf = BeforeStyleOf;
            pooledBoxBuilder.AfterStyleOf = AfterStyleOf;
            pooledBoxBuilder.MarkerStyleOf = MarkerStyleOf;
            // Wire font metrics so field-sizing: content can measure value-text
            // width using the same metrics the rest of the layout pass uses.
            pooledBoxBuilder.FieldSizingMetrics = defaultMetrics;
            return pooledBoxBuilder;
        }
        // Reusable HashSet for PruneScrollState's live-box walk. Without this
        // we'd allocate a fresh HashSet<Box> with O(NodeCount) capacity per
        // Layout call; reusing it is zero-alloc after the first growth.
        readonly HashSet<Box> liveBoxesScratch = new();
        // Owned-snapshot pool for the path where ctx.Snapshot is null (layout-
        // only test harnesses, callers that drive Layout without ComputeAll).
        // Kept across calls and refilled in place so steady-state stays
        // zero-alloc on a stable tree.
        DomSnapshot ownSnapshot;
        long layoutContextVersion = 1;
        long viewportLayoutContextVersion = 1;
        double lastViewportWidth = double.NaN;
        double lastViewportHeight = double.NaN;
        long cacheHits;
        long cacheMisses;
        System.Diagnostics.Stopwatch stageWatch;

        // Survivor from the previous Layout pass. Returned wholesale when an
        // InvalidationTracker reports zero Layout/Structure flags via
        // IncrementalLayoutGate. Null until the first full pass completes.
        Box lastRoot;
        long skipCount;

        // Optional resolver for `::backdrop` pseudo-element styles, normally
        // wired by the document lifecycle to `CascadeEngine.ComputeBackdrop`.
        // When non-null, BoxBuilder/SnapshotBoxBuilder query it for every
        // top-layer host (open modal dialog, open popover) and inject a
        // synthetic backdrop sibling box. Null disables backdrop synthesis;
        // tests that don't exercise dialogs leave it null.
        public Func<Element, ComputedStyle> BackdropStyleOf { get; set; }

        // Optional resolvers for `::before` / `::after` pseudo-element
        // styles. Wired by the document lifecycle to
        // `CascadeEngine.ComputeBefore` / `ComputeAfter`. When non-null,
        // BoxBuilder/SnapshotBoxBuilder injects an anonymous child box at
        // the front (::before) or end (::after) of every element whose
        // pseudo style has a non-default `content`.
        public Func<Element, ComputedStyle> BeforeStyleOf { get; set; }
        public Func<Element, ComputedStyle> AfterStyleOf { get; set; }
        public Func<Element, ComputedStyle> MarkerStyleOf { get; set; }

        // Optional registry for `<img>` natural-size resolution. BoxBuilder
        // queries it when an `<img>` has neither CSS width/height nor HTML
        // width/height attributes; the source's intrinsic Width/Height
        // becomes the default box size. Null disables natural-size and
        // authors must set explicit dimensions.
        public Weva.Paint.Images.IImageRegistry ImageRegistry { get; set; }

        public LayoutEngine(IFontMetrics defaultMetrics) : this(defaultMetrics, true) { }

        public LayoutEngine(IFontMetrics defaultMetrics, bool useSnapshot) {
            // Anchor positioning is gated by additive property registration so
            // it never edits the shared CssProperties switch. Touching the
            // registrar here guarantees `anchor-name` / `position-anchor` are
            // known to the cascade before the first Layout call.
            Weva.Layout.AnchorPositioning.AnchorPositioningProperties.EnsureRegistered();
            this.defaultMetrics = defaultMetrics;
            this.useSnapshot = useSnapshot;
            this.layoutSymbols = useSnapshot ? new SymbolTable() : null;
            this.snapshotStylesAt = SnapshotStyleAt;
            this.scrollLayout = new ScrollLayout(scrollContainer);
            this.stickyResolver = new StickyResolver(scrollContainer);
            // Construct the four formatting-context passes once and wire the
            // BlockLayout <-> InlineLayout cycle. ctx is set per-Layout-call via
            // Reset(); pool/scratch references are stable for the engine's
            // lifetime. The cycle is intentional: InlineLayout calls
            // BlockLayout.LayoutBlock when it encounters an inline-block atom.
            this.inlineLayout = new InlineLayout(boxPool, scratch);
            this.blockLayout = new BlockLayout(inlineLayout, scratch);
            this.inlineLayout.BlockLayout = blockLayout;
            this.flexLayout = new FlexLayout(scratch);
            this.gridLayout = new GridLayout(scratch);
            // Grid items frequently inherit a too-wide box from BlockLayout's
            // pre-grid pass; pass blockLayout in so GridLayout can re-flow
            // the item's interior at the cell width (see GridLayout.cs:439).
            this.gridLayout.SetBlockLayout(blockLayout);
            // After grid's RelayoutContentAt re-stacks a subtree, descendants
            // that are flex-positioned (e.g. xp-footer inside .actionbar-wrap
            // inside a shrunk .hud-bot grid item) need their flex layout
            // re-applied. Inject flexLayout so GridLayout can call it.
            this.gridLayout.SetFlexLayout(flexLayout);
            // Reverse wiring: ComputeBaseSize asks a grid child for its
            // stretch-free max-content block size (column-flex base of a grid).
            this.flexLayout.SetGridLayout(gridLayout);
            // FlexLayout cross-axis clamp/stretch on column flexes can
            // shrink an item's width below its pre-flex BlockLayout result;
            // the re-flow path mirrors GridLayout's recursion-guarded
            // RelayoutContentAt invocation.
            this.flexLayout.SetBlockLayout(blockLayout);
            // PositioningPass needs BlockLayout for shrink-to-fit on
            // absolute/fixed boxes with width:auto and ≤1 horizontal pin
            // (CSS Positioned Layout L3 §10.3.7).
            this.positioningPass.SetBlockLayout(blockLayout);
            this.tableLayout = new TableLayout(blockLayout);
            this.multicolLayout = new MulticolLayout();
            this.multicolLayout.SetBlockLayout(blockLayout);
        }

        public ScrollContainer ScrollContainer => scrollContainer;

        // CSS Position L3 §6.3: sticky offsets must be recomputed from the
        // absolute scroll position on every frame where scroll position changes,
        // even when no layout-affecting property changed (i.e. the layout-skip
        // path). Called by UIDocumentLifecycle.Update in the scroll-only path so
        // sticky elements don't hold stale offsets from the previous layout pass.
        // No-op when no scroll containers exist or the box tree is not yet built.
        // Also gated on LastStickyCount: the viewport scroll container makes
        // scrollContainer.Count >= 1 on nearly every document, so without the
        // count gate every wheel/drag frame paid a whole-tree resolver walk
        // even with zero sticky elements (the incremental path has had this
        // gate all along — see LayoutEngine.Incremental.cs).
        public void RefreshStickyOffsets() {
            if (lastRoot == null || scrollContainer.Count == 0) return;
            if (positioningPass.LastStickyCount == 0) return;
            stickyResolver.Resolve(lastRoot);
        }

        public bool UseSnapshot => useSnapshot;

        internal BoxPool DiagnosticBoxPool => boxPool;

        internal int CacheSize => cache.Count;
        internal long CacheHits => cacheHits;
        internal long CacheMisses => cacheMisses;

        // Latest box tree returned from a Layout call. Same instance returned to
        // callers when a tracker-driven skip occurs. Null until the first full
        // pass completes. Exposed for diagnostics + the incremental-paint sister
        // task which keys off Box.LayoutVersion / persistence.
        public Box LastRoot => lastRoot;

        // Incremented every time the IncrementalLayoutGate short-circuits a
        // Layout call. Read by tests + perf assertions to verify the gate fired.
        public long SkipCount => skipCount;

        internal bool CollectStageTimings { get; set; }

        // Scroll-boundary content reuse (staged, default OFF). When on, a full
        // layout reuses the prior frame's laid-out subtree for a scroll
        // container whose content-box width and subtree fingerprint are
        // unchanged, skipping the (expensive) block/flex/grid re-layout of its
        // contents. Targets the dominant layout-animation waste: a small
        // property change high in the tree forcing a full relayout that
        // needlessly re-lays a large, unchanged scrollable region below it
        // (layout-stress's 96-cell grid-wrap). Default off until proven on the
        // live sample; flip via UIDocumentLifecycle or a debug menu.
        // Default ON: validated live (fast + correct on the propagating
        // animation case) and guarded by two reuse==no-reuse equivalence tests.
        public static bool EnableScrollBoundaryReuse = true;

        internal double LastBuildMs { get; private set; }
        internal double LastBlockMs { get; private set; }
        internal double LastAnalyzeMs { get; private set; }
        internal double LastFlexMs { get; private set; }
        internal double LastGridMs { get; private set; }
        internal double LastPositioningMs { get; private set; }
        internal double LastRepairMs { get; private set; }
        internal double LastReconcileMs { get; private set; }

        public void ResetCacheStats() {
            cacheHits = 0;
            cacheMisses = 0;
            skipCount = 0;
            ResetSubtreeSkipStats();
        }

        public void Invalidate(Element e) {
            if (e == null) return;
            cache.Remove(e);
        }

        public void InvalidateSubtree(Element root) {
            if (root == null) return;
            cache.Remove(root);
            for (int i = 0; i < root.Children.Count; i++) {
                var c = root.Children[i];
                if (c is Element ce) InvalidateSubtree(ce);
            }
        }

        public void InvalidateAll() {
            cache.Clear();
            // We deliberately keep the BoxPool free lists across InvalidateAll: the
            // common pattern is "invalidate then re-layout", and resurrecting the
            // same boxes from the pool for the next pass is exactly the win we
            // want. Cached boxes that were holding pooled instances alive are now
            // unreachable; on the next Layout pass they're not in `allocated` so
            // EndPass simply doesn't see them — they will be GC'd. The pool itself
            // only holds references to the boxes we actively recycled.
            lastRoot = null;
        }

        public void Apply(InvalidationTracker tracker) {
            if (tracker == null) return;
            var kind = InvalidationKind.Layout | InvalidationKind.Structure | InvalidationKind.Style;
            // P5: reuse the per-engine scratch instead of lazy-allocating
            // `new List<Element>()` per dirty frame.
            var toDrop = scratchToDrop;
            toDrop.Clear();
            foreach (var kv in tracker.DirtyEntries) {
                if ((kv.Value & kind) == 0) continue;
                if (kv.Key is Element e) {
                    toDrop.Add(e);
                }
            }
            if (toDrop.Count == 0) return;
            for (int i = 0; i < toDrop.Count; i++) {
                var e = toDrop[i];
                cache.Remove(e);
                if (tracker.IsDirty(e, InvalidationKind.Structure)) {
                    var p = e.Parent as Element;
                    while (p != null) {
                        cache.Remove(p);
                        p = p.Parent as Element;
                    }
                }
            }
            toDrop.Clear();
        }

        public Box Layout(Document doc, Func<Element, ComputedStyle> styleOf, LayoutContext ctx) {
            return Layout(doc, styleOf, ctx, null);
        }

        // Tracker-aware overload. When the IncrementalLayoutGate reports the
        // dirty set has zero Layout|Structure flags, we return the surviving box
        // tree from the previous pass without rebuilding. Style-only and
        // PseudoClassState-only mutations land here as paint-only and skip
        // layout entirely. The caller is responsible for clearing the tracker
        // after the frame.
        //
        // Viewport changes still force a full pass when the tracker is empty:
        // root constraints can change even with no DOM/style dirtiness. The
        // cache invalidation is narrower than a full context bump: only the
        // root and boxes whose computed values contain viewport units key on
        // the viewport-specific version; fixed descendants keep their cache
        // entries when their containing block dimensions are unchanged.
        public Box Layout(Document doc, Func<Element, ComputedStyle> styleOf, LayoutContext ctx, InvalidationTracker tracker) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (styleOf == null) throw new ArgumentNullException(nameof(styleOf));
            if (ctx == null) ctx = new LayoutContext(defaultMetrics);
            if (ctx.DefaultFontMetrics == null) ctx.DefaultFontMetrics = defaultMetrics;
            // L2: open a new shrink-to-fit intrinsic-cache generation for this
            // layout so intrinsics cached during a prior layout (content may
            // have changed) never satisfy a hit in this one. Within this call
            // every positioning pass shares the generation, so a box probed by
            // one pass is reused by the rest.
            PositioningPass.BumpLayoutGeneration();
            // Wire the tracker for the scroll-boundary reuse pre-pass (which needs
            // to know which subtrees are dirty). Set even when reuse is off.
            scrollReuseTracker = tracker;

            // CSS Values L4 §6.2 — the `rem` unit resolves against the cascaded
            // font-size of the document root (`<html>`), not a fixed default.
            // We do this before any layout work runs so every length resolved
            // downstream sees the propagated root size.
            UpdateRootFontSizeFromHtml(doc, styleOf, ctx);

            // Skip path: tracker reports nothing layout-affecting AND a viewport
            // change has not flagged the context as dirty AND we have a survivor
            // from a prior pass.
            if (lastRoot != null
                && tracker != null
                && IncrementalLayoutGate.ShouldSkipLayout(tracker)
                && !ViewportChanged(ctx)) {
                skipCount++;
                // Always record the path (not just under CollectStageTimings):
                // the lifecycle reads LastPath to skip the post-layout
                // ElementToBoxIndex rebuild on a Skip, where the box tree is
                // returned unchanged so the existing map is still valid.
                LastPath = LayoutPath.Skip;
                lastRoot.IsCachedFromLastFrame = true;
                return lastRoot;
            }

            // v0.7: subtree-only relayout when the dirty Layout-flagged set is
            // narrow AND each dirty element sits inside a stable formatting
            // context. The inner method walks the dirty set and rebuilds only
            // the affected subtrees, splicing into lastRoot's box graph in
            // place. Returns false on any predicate miss → fall through to
            // full layout below.
            if (lastRoot != null
                && tracker != null
                && tracker.HasAny(InvalidationKind.Layout)
                && !tracker.HasAny(InvalidationKind.Structure)
                && !ViewportChanged(ctx)) {
                if (TryLayoutSubtree(doc, styleOf, ctx, tracker)) {
                    // Subtree path actually ran the layout algorithm on the
                    // dirty subtree — the root's geometry may have shifted
                    // (positioning pass re-ran), so it's NOT a pure cached
                    // return. Clear the flag so paint-side incremental logic
                    // treats this as a fresh pass.
                    lastRoot.IsCachedFromLastFrame = false;
                    return lastRoot;
                }
            }

            // Capture viewport-change BEFORE the bump syncs lastViewport* — the
            // scroll-boundary reuse pre-pass runs after the bump, so it can't
            // detect the change via ViewportChanged() afterwards. A viewport
            // resize must disable reuse: a scroll container's content is only
            // width/content-invariant, but the ROOT scroll containers (html/body,
            // which carry the UA overflow) have viewport-height-dependent content
            // (vh / height:% chains), so grafting them across a resize freezes
            // the page height ("UI doesn't resize in Y unless width also changes").
            scrollReusePassViewportChanged = ViewportChanged(ctx);
            BumpContextVersionIfChanged(ctx);
            boxPool.BeginPass();
            ResetStageTimings();
            LastPath = LayoutPath.Full;

            // Open a CssValuePool scope so every CssLength/CssNumber/CssPercentage
            // parsed during this pass is recycled at the end of the call. The
            // box tree we return holds Box instances (which keep numeric values
            // already extracted via ToPixels), not CssValue references.
            using var cssScope = CssValuePool.PassScope();

            BlockBox rootBox;
            BeginStage();
            using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutBuild)) {
                if (useSnapshot) {
                    // Prefer the cascade-supplied snapshot if the lifecycle wired one onto
                    // the context; otherwise refill our own persistent snapshot in place.
                    // Either way the builder walks NodeId arrays, never the managed Element
                    // tree, and the typed-array allocations from a fresh DomSnapshot.Build
                    // are amortized to zero after warmup.
                    DomSnapshot snap;
                    StyleArray snapStyles = null;
                    if (ctx.Snapshot != null) {
                        snap = ctx.Snapshot;
                        snapStyles = ctx.SnapshotStyles;
                    } else if (ownSnapshot == null) {
                        snap = ownSnapshot = DomSnapshot.Build(doc, layoutSymbols);
                    } else {
                        snap = ownSnapshot;
                        snap.Refill(doc, layoutSymbols);
                    }
                    // Build-time scroll-boundary skip: sever reuse-eligible
                    // scroll containers' children in the snapshot so the build
                    // doesn't construct subtrees we're about to graft. Restored
                    // immediately after (finally) so the snapshot stays intact.
                    PrepareScrollBoundaryReuseSever(snap, ctx);
                    try {
                        rootBox = (BlockBox)BuildBoxTreeFromSnapshot(snap, styleOf, snapStyles);
                    } finally {
                        RestoreScrollBoundaryReuseSever(snap);
                    }
                } else {
                    var builder = GetOrCreatePooledBoxBuilder(styleOf);
                    rootBox = (BlockBox)builder.BuildDocument(doc);
                }
            }
            LastBuildMs = EndStage();

            // Scroll-boundary reuse: graft prior frame's laid-out subtree onto
            // clean scroll containers BEFORE the block/flex/grid passes so they
            // skip those (expensive) subtrees entirely. Validated + corrected
            // after the pipeline. No-op unless EnableScrollBoundaryReuse.
            ApplyScrollBoundaryReuse(rootBox, ctx, styleOf);

            // Refresh per-pass ctx; pool/scratch wiring is engine-stable.
            inlineLayout.Reset(ctx);
            blockLayout.Reset(ctx);
            flexLayout.Reset(ctx);
            gridLayout.Reset(ctx);
            tableLayout.Reset(ctx);
            multicolLayout.Reset(ctx);
            // Anchor positioning v2: rewrite `anchor-size(...)` consumers in place
            // before BlockLayout reads width/height. Additive no-op when no
            // declared anchor uses the function. The pass holds a persistent
            // dictionary cleared at the start of each ApplyInstance call.
            anchorSizePass.ApplyInstance(rootBox);
            // Position-stamp now lives inside BlockLayout.LayoutRoot so all
            // entry points (main path here + incremental re-layout at the
            // second entry point below) share a single source of truth.
            BeginStage();
            using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutBlock)) {
                blockLayout.LayoutRoot(rootBox, ctx.ViewportWidthPx, ctx.ViewportHeightPx);
            }
            LastBlockMs = EndStage();
            BeginStage();
            var features = AnalyzeLayoutFeatures(rootBox);
            LastAnalyzeMs = EndStage();
            BeginStage();
            using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutFlex)) {
                if (features.HasFlex) RunFlexPassesToConvergence(flexPassBoxes, flexLayout);
            }
            LastFlexMs += EndStage();
            BeginStage();
            using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutGrid)) {
                if (features.HasGrid) RunGridPasses(gridPassBoxes, gridLayout);
            }
            LastGridMs += EndStage();
            if (features.HasTable) RunTablePasses(tablePassBoxes, tableLayout);
            if (features.HasMulticol) RunMulticolPasses(multicolPassBoxes, multicolLayout);
            if (features.HasPositioningWork) {
                BeginStage();
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutPositioning)) {
                    positioningPass.Run(rootBox, ctx);
                }
                LastPositioningMs += EndStage();
            }
            // Re-run flex/grid after PositioningPass: boxes pinned via inset
            // (e.g. `.hud { position: fixed; inset: 0 }`) get their final size
            // from the positioning pass, AFTER the initial flex/grid pass
            // already laid out their children against the BlockLayout
            // content-based size. Without this second pass the children
            // overflow or collapse — visible as bottom-row HUD panels
            // rendering outside the viewport on Ravenmoor demo. Re-running
            // is idempotent on boxes that were not resized.
            bool needsPostPositionFlexGrid = features.HasGrid || features.HasOutOfFlow;
            if (needsPostPositionFlexGrid) {
                // CSS Sizing L3 §5.3 — Percentage Resolution: a `height: <%>`
                // declaration requires the containing block's height to be
                // definite. When the CB itself is sized only by inset/transform
                // (e.g. `.modal-overlay { position: fixed; inset: 0 }`), its
                // definite height is established by PositioningPass — too late
                // for the BlockLayout pre-pass that originally tried to resolve
                // the percent. The percent fell back to auto, and the height-%
                // descendant (`.hero-picker { height: 100% }`) stacked its
                // children at intrinsic content height instead, defeating any
                // downstream flex/grid sizing that relied on the percent value.
                //
                // Repair the percent heights here, between PositioningPass and
                // the post-positioning flex/grid pass, so the second flex/grid
                // iteration sees the correct heights. v1: only handles
                // `height: <number>%` whose CB.Height was set by inset/pinning.
                // calc(), nested percents, min/max-height clamps, and box-sizing
                // are followups. Pinned by HeroPickerScrollReproTests
                // .Hero_picker_detail_clips_when_outer_is_fixed_overlay_with_percent_height.
                if (features.HasOutOfFlow) {
                    // Only walk subtrees of out-of-flow boxes whose Height was
                    // set by PositioningPass — that's where the indefinite-CB
                    // → percent-fallback problem lives. Plain in-flow trees
                    // resolved heights correctly during the first BlockLayout
                    // pass and don't need repair.
                    RepairPercentHeightsUnderOutOfFlow(rootBox, ctx);
                }
                gridLayout.SetInSecondPass(true);
                BeginStage();
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutFlex)) {
                    if (features.HasFlex) RunFlexPasses(flexPassBoxes, flexLayout);
                }
                LastFlexMs += EndStage();
                BeginStage();
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutGrid)) {
                    if (features.HasGrid) RunGridPasses(gridPassBoxes, gridLayout);
                }
                LastGridMs += EndStage();
            }
            // Final flex pass: GridLayout's recursion guard (`reflowing`)
            // can swallow a nested ApplyItemAlignment shrink — when an
            // outer grid's RelayoutContentAt recurses into an inner grid
            // that shrinks a flex item, the inner Reflow{Flex,Grid}Descendants
            // chain is gated off. The container's Width gets the new value,
            // but its flex descendants' children keep pre-shrink sizes.
            // The canonical case: blockified `<span>` children of
            // `.bar > .label` (display:flex; row; justify-content:space-between)
            // end at full label-content width instead of their max-content
            // widths because Reflow never re-runs FlexLayout on `.label`.
            // Running a third RunFlexPasses here is idempotent for boxes
            // that were not resized — it walks the same FlexBox set as the
            // 2nd pass and skips re-shrink for items whose Width already
            // matches the shrunk container. See randhtml `.bar.hp .label`.
            // Third RunFlexPasses runs AFTER the second grid pass has
            // written final cell sizes onto stretch-aligned grid items.
            // Toggle InThirdPass so FinalizeContainerMainSize honors
            // GridStretchedHeight on column-flex items (e.g. `.hud-tr`)
            // and preserves the grid-cell Height instead of collapsing
            // back to the sum of intrinsic item heights. Earlier flex
            // passes must still collapse to feed grid an accurate
            // intrinsic for auto/min-content row-track sizing — see
            // FlexLayout.inThirdPass for the regression detail.
            if (features.HasFlex && CollectThirdFlexPassBoxes()) {
                flexLayout.SetInThirdPass(true);
                BeginStage();
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutFlex)) {
                    RunFlexPasses(thirdFlexPassBoxes, flexLayout);
                }
                LastFlexMs += EndStage();
                flexLayout.SetInThirdPass(false);
            }
            gridLayout.SetInSecondPass(false);
            BeginStage();
            // aspect-ratio fixup: boxes whose width is content/flex-derived
            // (not from an explicit length resolved in BlockLayout's initial
            // pass) miss the FinalizeBlockSize aspect-ratio branch — that
            // branch fires when `width:100%` resolves to 0 because the
            // parent's width isn't known yet. Walk the post-flex tree and
            // re-derive `height = width / aspect-ratio` for any box with
            // definite width and auto height. Only the in-flow tree;
            // abs/fixed boxes get their height from inset or explicit length,
            // not from auto-content sizing.
            if (features.HasAspectRatioWork) ApplyAspectRatioFixup(rootBox);
            // Stale inline-layout fixup: a flex item whose width was shrunk by
            // the parent's flex algorithm has its inline content (LineBoxes /
            // TextRuns) laid out at the parent's content width — the FIRST
            // ReflowIfShrunk call sets Width correctly, but a subsequent
            // outer reflow re-cascades BlockLayout (which re-assigns Width
            // to the parent's content width) and re-runs FlexLayout via
            // ReflowFlexDescendants. The inner ReflowIfShrunk is gated off
            // by the global `reflowing` flag, so the LineBox keeps the
            // pre-shrink width and right-aligned text escapes the box. This
            // walk relayouts any block-with-inline content whose LineBox is
            // wider than the box's content area. Canonical case: `.bar-value
            // { text-align: right; min-width: 60px }` inside a row-flex bar
            // — the bar-value's LineBox stays at the bar's pre-flex 965px and
            // the "84 / 120" text shifts to x=951.
            if (features.HasInlineFixupWork
                && (features.HasFlex || features.HasGrid || features.HasOutOfFlow)) {
                ApplyInlineLayoutFixup(rootBox);
            }
            // Abs/fixed boxes resolved their percent offsets against their CB
            // size during the initial positioningPass.Run, but flex pass 2/3
            // may have grown the CB (e.g. `.minimap-frame { flex:1 }` containing
            // `.dot { position:absolute; top:50% }` — the frame's height comes
            // from flex stretch, not BlockLayout intrinsic, so the dots
            // resolved `top:50%` against H=0 the first time). Re-run a
            // targeted abs/fixed reposition so their final coordinates reflect
            // the post-stretch CB. Skips the destructive parts of Run
            // (CompressOutOfFlow / ApplyRelative) which are not idempotent.
            if (features.HasOutOfFlow) {
                LastRepairMs += EndStage();
                BeginStage();
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutPositioning)) {
                    positioningPass.RepositionAbsolutes(rootBox, ctx);
                }
                LastPositioningMs += EndStage();
                // RepositionAbsolutes re-runs ApplyAbsoluteAgainst's cache-hit
                // path, which calls BlockLayout.RelayoutContentAt(absBox,
                // cachedWidth) so the abs-pos box's inline content re-aligns
                // against the post-shrink width (PositioningPass.cs:404-429
                // — fixes the .skill-slot-key "E" text drift). The re-layout
                // re-stacks the abs box's BLOCK children as block-flow, so a
                // flex container inside an abs-pos wrapper (canonical case:
                // `.play-action { position:absolute } > .play-btn { display:flex }`)
                // loses its flex positioning — children stack from the top at
                // X=0,Y=0 instead of being centred. Re-run flex/grid here so
                // their item placement is restored on top of the corrected
                // inline-content layout. Idempotent for unaffected boxes.
                if (features.HasFlex) {
                    BeginStage();
                    // Toggle the InThirdPass flag for this restoration pass.
                    // Semantically it's the same as the post-grid third flex
                    // pass: FinalizeContainerMainSize must HONOR a grid-stamped
                    // GridStretchedHeight on column-flex items (line 2260
                    // there) so a column flex sitting inside a grid cell
                    // (e.g. `.right-col` in a `grid-template-rows: 1fr` cell)
                    // doesn't get its Height re-inflated to the content sum.
                    // Without this toggle, the .right-col regression: window
                    // shrinks but .right-col keeps its full content height
                    // and overflows. Pinned by `RightColGridStretchHeight_*`
                    // tests; user-reported against a real play-grid.
                    flexLayout.SetInThirdPass(true);
                    using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutFlex)) {
                        RunFlexPasses(flexPassBoxes, flexLayout);
                    }
                    flexLayout.SetInThirdPass(false);
                    LastFlexMs += EndStage();
                }
                if (features.HasGrid) {
                    BeginStage();
                    using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutGrid)) {
                        RunGridPasses(gridPassBoxes, gridLayout);
                    }
                    LastGridMs += EndStage();
                }
                // The flex/grid restoration above re-collapses any aspect-ratio
                // box whose height was derived by the ApplyAspectRatioFixup at
                // line ~534: a column-flex grid item like stats.html
                // `.slot { aspect-ratio:1/1 }` reverts to its content height when
                // RunFlexPasses re-runs FinalizeContainerMainSize (which sums the
                // children for the main/height axis). Without re-deriving, the
                // square slots render squashed (e.g. 125x92 instead of 125x125).
                // Re-apply the aspect-ratio fixup so the ratio-derived height
                // survives the restoration; the re-pin below then sees the
                // corrected geometry. Scoped to grid items — see the
                // gridItemsOnly note in ApplyAspectRatioFixupVisit.
                if (features.HasAspectRatioWork) ApplyAspectRatioFixup(rootBox, gridItemsOnly: true);
                // The flex/grid restoration above re-ran RelayoutContentAt on
                // abs-pos containers, which re-stacks their content as block
                // flow and can disturb abs-pos descendants that RepositionAbsolutes
                // pinned at line 566 — e.g. an `inset:0` overlay inside a
                // flex-item bar re-stamped at the bar's content-bottom (combat-hud
                // hero HP/MP/XP labels clipped), or an `inset:0` full-viewport
                // panel re-shrunk by a content extent (map.html .atlas losing
                // its bottom strip). Re-pin once more AFTER the restoration so
                // the final geometry reflects the restored flex/grid sizes.
                // Idempotent (clean assignment, no accumulation), and a no-op
                // for abs boxes whose containing block didn't change.
                BeginStage();
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutPositioning)) {
                    positioningPass.RepositionAbsolutes(rootBox, ctx, pinOnly: true);
                }
                LastPositioningMs += EndStage();
                BeginStage();
            }
            // Scroll-boundary reuse validation: every grafted container's final
            // content-box width is now known. Confirm it matches the width the
            // grafted children were laid out for; re-lay any that drifted so the
            // result is identical to a no-reuse full layout. No-op unless reuse
            // grafted anything this pass.
            ValidateScrollBoundaryReuse(ctx, styleOf);
            // Late flex/grid/positioning repair passes can change scroll
            // content extents after the first ScrollLayout.Run. Refresh the
            // sidecar state from final geometry so overflow:auto tracks do
            // not stay visible from stale pre-reflow sizes.
            if (features.HasScrollWork) {
                using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutPositioning)) {
                    scrollLayout.Run(rootBox);
                    stickyResolver.Resolve(rootBox);
                }
            }
            LastRepairMs += EndStage();
            BeginStage();
            // (Pixel-snap intentionally lives at the paint layer only — see
            // UIBatcher.SubmitStrokeBorder. Snapping every box's geometry at
            // layout time produces cumulative rounding drift in flex/grid
            // fr distribution and breaks sub-pixel aspect-ratio derivation,
            // and matches Chrome's split between sub-pixel-aware layout +
            // pixel-snapped composite paint.)
            var survivor = Reconcile(rootBox);
            // Viewport scroll (CSS Overflow §3.3) lives on the anonymous root
            // box, which has NO Element — invisible to every element-keyed
            // preservation (layout-cache transfer, the prune re-anchor, the
            // rebuild capture). Hand it to the new root explicitly when the
            // root instance changes, BEFORE EndPass recycles the old root and
            // bumps its PoolGeneration — after that the transfer's phantom
            // check (correctly) refuses the stale entry and the scroll dies
            // (live find: the user's page scrolling rode the viewport root
            // and died on the keystroke's root replacement).
            if (lastRoot != null && survivor != null && !ReferenceEquals(lastRoot, survivor)) {
                scrollContainer.TransferScrollPosition(lastRoot, survivor);
            }
            boxPool.EndPass(survivor);
            PruneScrollState(survivor);
            // Establish viewport-level scrolling per CSS Overflow §3.3 so
            // documents taller than the viewport scroll without any explicit
            // overflow on html/body. Runs AFTER Reconcile so the viewport
            // scroll state is keyed on the survivor box (the same instance
            // that PruneScrollState just retained in CollectLive).
            scrollLayout.RunViewportScroll(survivor, ctx.ViewportWidthPx, ctx.ViewportHeightPx);
            LastReconcileMs = EndStage();
            if (survivor != null) survivor.IsCachedFromLastFrame = false;
            lastRoot = survivor;
            return survivor;
        }

        void ResetStageTimings() {
            if (!CollectStageTimings) return;
            LastBuildMs = 0;
            LastBlockMs = 0;
            LastAnalyzeMs = 0;
            LastFlexMs = 0;
            LastGridMs = 0;
            LastPositioningMs = 0;
            LastRepairMs = 0;
            LastReconcileMs = 0;
        }

        void BeginStage() {
            if (!CollectStageTimings) return;
            stageWatch ??= new System.Diagnostics.Stopwatch();
            stageWatch.Restart();
        }

        double EndStage() {
            if (!CollectStageTimings) return 0;
            stageWatch.Stop();
            return stageWatch.Elapsed.TotalMilliseconds;
        }

        bool ViewportChanged(LayoutContext ctx) {
            if (double.IsNaN(lastViewportWidth) && double.IsNaN(lastViewportHeight)) return false;
            return ctx.ViewportWidthPx != lastViewportWidth || ctx.ViewportHeightPx != lastViewportHeight;
        }

        // Walks the post-flex/grid box tree and applies aspect-ratio
        // derivation for any box with definite width and `height:auto`
        // (i.e. height came from intrinsic content stacking). The path that
        // catches this case in BlockLayout.FinalizeBlockSize only fires
        // when width is already resolved at that point — for boxes whose
        // width comes from a flex/grid container's stretch or from a
        // `width:100%` declaration resolved against a parent whose width
        // wasn't known yet, the ratio branch is skipped and height stays
        // content-derived. This walk repairs the symptom for the canonical
        // case `.portrait-frame { width: 100%; aspect-ratio: 3/4 }` inside
        // a column flex with align-items: center, where neither the
        // stretch path nor the initial BlockLayout pass applied the ratio.
        //
        // Idempotent: a box whose height was already derived from the ratio
        // (or explicitly set) is detected via the `height:auto` check and
        // skipped on subsequent walks.
        static void ApplyAspectRatioFixup(Box root, bool gridItemsOnly = false) {
            if (root == null) return;
            ApplyAspectRatioFixupVisit(root, gridItemsOnly);
        }


        static void ApplyAspectRatioFixupVisit(Box box, bool gridItemsOnly = false) {
            // gridItemsOnly: when this fixup is re-run AFTER the out-of-flow
            // flex/grid restoration, restrict it to GRID ITEMS (boxes whose
            // parent establishes a grid formatting context). The restoration's
            // flex pass only re-collapses aspect-ratio COLUMN-FLEX GRID ITEMS
            // (e.g. stats.html `.slot`); re-deriving every aspect box would
            // re-propagate the restoration's sub-pixel drift into flex-context
            // aspect boxes that were already correct after the first fixup
            // (dialogue `.portrait` regressed 292 → 296 that way).
            bool applyHere = !gridItemsOnly
                || (box.Parent is Weva.Layout.Grid.GridBox);
            if (applyHere && box is Weva.Layout.Boxes.BlockBox bb && bb.Style != null && bb.Width > 0) {
                string heightRaw = bb.Style.Get(Weva.Css.Cascade.CssProperties.HeightId);
                bool heightAuto = string.IsNullOrEmpty(heightRaw) || heightRaw == "auto";
                if (heightAuto
                    && StyleResolver.TryResolveAspectRatio(bb.Style, out double ratio)
                    && ratio > 0) {
                    double derived = bb.Width / ratio;
                    bool borderBox = IsBorderBoxLocal(bb.Style);
                    double frame = bb.PaddingTop + bb.PaddingBottom + bb.BorderTop + bb.BorderBottom;
                    double newHeight = borderBox ? derived : derived + frame;
                    // CSS Sizing L3 §5.2: clamp the aspect-ratio-derived height
                    // by min-height / max-height. Without this, `width:100px;
                    // aspect-ratio:2/1; min-height:80px` ended up at 50 (ratio
                    // result) instead of 80 (#245). Apply max first, then min,
                    // so min wins when conflicting (mirrors the BlockLayout
                    // fix in #244). v1 simplification: this fixup pass has no
                    // LayoutContext, so percent / em / vw min-height clamps
                    // can't be resolved here — only literal pixel values.
                    // The %/em/vw case is still handled by FinalizeBlockSize's
                    // own clamp earlier in the layout pipeline.
                    TryClampPixelHeightMinMax(bb, borderBox, frame, ref newHeight);
                    if (System.Math.Abs(bb.Height - newHeight) > LayoutEpsilons.HalfPixelEqual) {
                        double delta = newHeight - bb.Height;
                        bb.Height = newHeight;
                        // Propagate the size change upward through any
                        // content-sized ancestors. A block-flow parent that
                        // computed its own Height as `contentBottomY +
                        // padding + border` becomes a stale sum the moment
                        // we grow a child. Walk up adding the delta until
                        // we hit an ancestor with an explicit height
                        // (`heightAuto == false`), one that's flex- or
                        // grid-sized (Height was set by flex/grid stretch,
                        // not by child sum — those stand), or one already
                        // larger than the child + frame demand. Without
                        // this, the canonical case (portrait-frame inside
                        // a flex-column .portrait that's auto-height) keeps
                        // its parent at the pre-fixup intrinsic, so the
                        // next-sibling ident stacks over the now-taller
                        // portrait box.
                        PropagateHeightDeltaUp(bb, delta);
                        // Also restack later in-flow siblings of bb whose
                        // parent is a block-flow container — their Y was
                        // computed from the pre-fixup child heights.
                        ShiftFollowingSiblingsDown(bb, delta);
                    }
                }
            }
            for (int i = 0; i < box.Children.Count; i++) {
                ApplyAspectRatioFixupVisit(box.Children[i], gridItemsOnly);
            }
        }

        static void PropagateHeightDeltaUp(Box origin, double delta) {
            if (delta <= 0) return;
            Box child = origin;
            var p = origin.Parent;
            while (p != null) {
                if (!(p is Weva.Layout.Boxes.BlockBox pbb)) break;
                if (pbb.Style == null) break;
                string raw = pbb.Style.Get(Weva.Css.Cascade.CssProperties.HeightId);
                bool autoHeight = string.IsNullOrEmpty(raw) || raw == "auto";
                if (!autoHeight) break;
                // Stop at any ancestor whose height was externally set by
                // a grid parent (GridStretchedHeight) or by absolute-pos
                // edge pinning (vertical pin both edges via inset). Without
                // this guard, an aspect-ratio fixup on a grandchild
                // propagates past a grid-row-stretched container and over-
                // grows it beyond its grid cell — e.g. `.portrait-frame
                // { aspect-ratio: 3/4 }` inside `.character { display:flex }`
                // inside a CSS grid row. The grid row's row track is
                // already sized; growing `.character` makes it overflow
                // its grid cell silently.
                if (pbb.GridStretchedHeight) break;
                bool grew = false;
                if (p is Weva.Layout.Flex.FlexBox fb) {
                    // Column flex: main axis is height — items stack
                    // vertically, container height = sum of items + gaps.
                    // Growing one item grows the container by the same delta
                    // (subject to the autoHeight gate above). Row flex: items
                    // share a horizontal line, container height = max child
                    // height — only propagate if the new origin height
                    // exceeds the previous max (which we approximate by the
                    // simple "is delta positive and origin now the tallest"
                    // check using current parent height).
                    string dir = fb.Style?.Get("flex-direction");
                    bool isColumn = dir == "column" || dir == "column-reverse";
                    if (isColumn) {
                        pbb.Height += delta;
                        grew = true;
                    } else {
                        // Row flex auto height: parent height matches the
                        // tallest item's outer height. The just-grown origin
                        // may now be the tallest; if so, raise the parent
                        // to fit.
                        double childOuter = child.Height
                            + ((child is Weva.Layout.Boxes.BlockBox cbb)
                                ? cbb.MarginTop + cbb.MarginBottom : 0);
                        double need = childOuter + pbb.PaddingTop + pbb.PaddingBottom
                                      + pbb.BorderTop + pbb.BorderBottom;
                        if (pbb.Height < need - LayoutEpsilons.HalfPixelEqual) {
                            pbb.Height = need;
                            grew = true;
                        }
                    }
                } else if (p is Weva.Layout.Grid.GridBox) {
                    // Grid containers: row track sizing already accommodated
                    // the original child intrinsic; we don't have a cheap
                    // way to re-resolve track sizes here. Leave grid
                    // containers alone — overflow is the price until a more
                    // thorough re-track pass is wired.
                    break;
                } else {
                    pbb.Height += delta;
                    grew = true;
                }
                if (!grew) break;
                // Propagate the Y shift to siblings of `p` whose Y was
                // computed from a pre-fixup sum-of-children stack. Stops at
                // the same boundaries as the height propagation: explicit
                // height, flex/grid container, or row-flex container.
                ShiftFollowingSiblingsDown(p, delta);
                child = p;
                p = p.Parent;
            }
        }

        static void ShiftFollowingSiblingsDown(Box origin, double delta) {
            if (delta <= 0) return;
            var parent = origin.Parent;
            if (parent == null) return;
            bool past = false;
            if (parent is Weva.Layout.Flex.FlexBox fb) {
                // Only column flex stacks siblings vertically by Y. For row
                // flex, growing a child's height doesn't push its siblings
                // (they're side-by-side on the main axis). Grid skipped for
                // the same reason as PropagateHeightDeltaUp.
                string dir = fb.Style?.Get("flex-direction");
                bool isColumn = dir == "column" || dir == "column-reverse";
                if (!isColumn) return;
                for (int i = 0; i < parent.Children.Count; i++) {
                    var c = parent.Children[i];
                    if (c == origin) { past = true; continue; }
                    if (!past) continue;
                    c.Y += delta;
                }
                return;
            }
            if (parent is Weva.Layout.Grid.GridBox) return;
            for (int i = 0; i < parent.Children.Count; i++) {
                var c = parent.Children[i];
                if (c == origin) { past = true; continue; }
                if (!past) continue;
                c.Y += delta;
            }
        }

        // Pixel-only min/max-height clamp for the aspect-ratio fixup. The
        // fixup runs without a LayoutContext, so percent / em / vw / vh
        // values can't be resolved here — those still go through the
        // FinalizeBlockSize clamp earlier in the pass. The common author
        // case (`aspect-ratio: X; min-height: Npx`) is what's covered.
        static void TryClampPixelHeightMinMax(Weva.Layout.Boxes.BlockBox bb, bool borderBox, double frame, ref double newHeight) {
            if (bb.Style == null) return;
            var minParsed = bb.Style.GetParsed(Weva.Css.Cascade.CssProperties.MinHeightId);
            var maxParsed = bb.Style.GetParsed(Weva.Css.Cascade.CssProperties.MaxHeightId);
            // CssLength is the parsed pixel-length representation. Other
            // length kinds (percent, em, calc) are skipped here.
            if (maxParsed is Weva.Css.Values.CssLength maxL && maxL.Unit == Weva.Css.Values.CssLengthUnit.Px) {
                double maxPx = borderBox ? maxL.Value : maxL.Value + frame;
                if (newHeight > maxPx) newHeight = maxPx;
            }
            if (minParsed is Weva.Css.Values.CssLength minL && minL.Unit == Weva.Css.Values.CssLengthUnit.Px) {
                double minPx = borderBox ? minL.Value : minL.Value + frame;
                if (newHeight < minPx) newHeight = minPx;
            }
        }

        static bool IsBorderBoxLocal(Weva.Css.Cascade.ComputedStyle style) {
            if (style == null) return false;
            var v = style.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
            if (v is Weva.Css.Values.CssKeyword k) return k.Identifier == "border-box";
            if (v is Weva.Css.Values.CssIdentifier id) return id.Name == "border-box";
            return style.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
        }

        void ApplyInlineLayoutFixup(Box root) {
            if (root == null || blockLayout == null) return;
            ApplyInlineLayoutFixupVisit(root);
        }

        void ApplyInlineLayoutFixupVisit(Box box) {
            // Visit children first so a stale ancestor doesn't get re-shrunk
            // before its descendants are healed.
            for (int i = 0; i < box.Children.Count; i++) {
                ApplyInlineLayoutFixupVisit(box.Children[i]);
            }
            if (!(box is Weva.Layout.Boxes.BlockBox bb)) return;
            if (!bb.ContainsInlines) return;
            if (bb.Children.Count == 0) return;
            double maxLineW = 0;
            for (int i = 0; i < bb.Children.Count; i++) {
                var c = bb.Children[i];
                if (c is Weva.Layout.Boxes.LineBox lb && lb.Width > maxLineW) maxLineW = lb.Width;
            }
            // A LineBox wider than the box's content area + tolerance means
            // ApplyTextAlign ran with a stale (pre-shrink) contentW. Relayout
            // at the current width so text-align uses the right basis.
            if (maxLineW > bb.ContentWidth + LayoutEpsilons.HalfPixelEqual) {
                blockLayout.RelayoutContentAt(bb, bb.Width);
            }
        }

        ComputedStyle SnapshotStyleAt(int nodeId) {
            if (activeSnapshotStyles != null) return activeSnapshotStyles.Get(nodeId);
            return snapshotStyles.At(nodeId);
        }

        Box BuildBoxTreeFromSnapshot(DomSnapshot snap, Func<Element, ComputedStyle> styleOf, StyleArray precomputedStyles = null) {
            // Pre-build a NodeId-indexed style array so the snapshot walk hits a raw
            // array indexer instead of routing each lookup back through the managed
            // Element handle. Cost: O(N) per Layout, identical to the cost the cascade
            // already paid filling its own per-Element style cache. Refill into the
            // engine-owned SnapshotStyleArray so the ComputedStyle[NodeCount] backing
            // buffer is recycled across passes.
            activeSnapshotStyles = precomputedStyles != null && precomputedStyles.Count >= snap.NodeCount
                ? precomputedStyles
                : null;
            if (activeSnapshotStyles == null) snapshotStyles.Refill(snap, styleOf);
            if (pooledSnapBuilder == null) {
                pooledSnapBuilder = new SnapshotBoxBuilder(snapshotStylesAt, boxPool, scratch);
            }
            pooledSnapBuilder.BackdropStyleOf = BackdropStyleOf;
            pooledSnapBuilder.BeforeStyleOf = BeforeStyleOf;
            pooledSnapBuilder.AfterStyleOf = AfterStyleOf;
            pooledSnapBuilder.MarkerStyleOf = MarkerStyleOf;
            // Wire font metrics for field-sizing: content text-width measurement.
            pooledSnapBuilder.FieldSizingMetrics = defaultMetrics;
            // CounterContext.BuildFor walks the Element tree (target.Parent
            // up to Document) calling styleOf(Element) at each step to read
            // counter-reset / counter-increment / counter-set. The snapshot
            // path's int-keyed styleOf can't satisfy that signature, so we
            // pass the original Element-keyed styleOf alongside; without
            // this counter() in ::before / ::after content silently
            // resolves to "" through the snapshot build path.
            pooledSnapBuilder.ElementStyleOf = styleOf;
            try {
                return pooledSnapBuilder.BuildFromSnapshot(snap);
            } finally {
                activeSnapshotStyles = null;
            }
        }

        public Box Layout(Element root, Func<Element, ComputedStyle> styleOf, LayoutContext ctx) {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (styleOf == null) throw new ArgumentNullException(nameof(styleOf));
            if (ctx == null) ctx = new LayoutContext(defaultMetrics);
            if (ctx.DefaultFontMetrics == null) ctx.DefaultFontMetrics = defaultMetrics;

            // Mirror the Document-overload: if the supplied root is the
            // document's `<html>` element, propagate its cascaded font-size
            // into ctx.RootFontSizePx so descendants' `rem` lengths resolve
            // against the author-overridden root.
            if (root.TagName == "html") {
                var rs = styleOf(root);
                if (rs != null) {
                    double fs = StyleResolver.FontSizePx(rs, null, ctx);
                    if (fs > 0) ctx.RootFontSizePx = fs;
                    // Mirror RootFontSizePx: propagate the cascaded line-height
                    // of `<html>` into ctx.RootLineHeightPx so `rlh` units in
                    // descendants resolve against the author root, not the
                    // 1.2× root-font-size fallback. CSS Values L4 §6.2.
                    var fam = rs.Get(CssProperties.FontFamilyId);
                    double lh = StyleResolver.LineHeightPx(rs, fs > 0 ? fs : ctx.RootFontSizePx, ctx, ctx.GetMetrics(fam));
                    if (lh > 0) ctx.RootLineHeightPx = lh;
                }
            }

            BumpContextVersionIfChanged(ctx);
            boxPool.BeginPass();
            using var cssScope = CssValuePool.PassScope();

            var builder = GetOrCreatePooledBoxBuilder(styleOf);
            var rootStyle = styleOf(root);
            var rootBox = builder.Build(root, rootStyle);
            if (rootBox == null) {
                boxPool.EndPass(null);
                return null;
            }

            inlineLayout.Reset(ctx);
            blockLayout.Reset(ctx);
            flexLayout.Reset(ctx);
            gridLayout.Reset(ctx);
            tableLayout.Reset(ctx);
            anchorSizePass.ApplyInstance(rootBox);
            if (rootBox is BlockBox bb) {
                blockLayout.LayoutRoot(bb, ctx.ViewportWidthPx, ctx.ViewportHeightPx);
                RunFlexPasses(bb, flexLayout);
                RunGridPasses(bb, gridLayout);
                RunTablePasses(bb, tableLayout);
            }
            positioningPass.Run(rootBox, ctx);
            scrollLayout.Run(rootBox);
            stickyResolver.Resolve(rootBox);
            var survivor = Reconcile(rootBox);
            // Viewport scroll hand-off across root replacement, before
            // EndPass bumps the old root's generation — see the matching
            // comment on the other Reconcile site.
            if (lastRoot != null && survivor != null && !ReferenceEquals(lastRoot, survivor)) {
                scrollContainer.TransferScrollPosition(lastRoot, survivor);
            }
            boxPool.EndPass(survivor);
            PruneScrollState(survivor);
            scrollLayout.RunViewportScroll(survivor, ctx.ViewportWidthPx, ctx.ViewportHeightPx);
            if (survivor != null) survivor.IsCachedFromLastFrame = false;
            lastRoot = survivor;
            return survivor;
        }

        void PruneScrollState(Box survivor) {
            if (survivor == null) { scrollContainer.Clear(); return; }
            if (scrollContainer.Count == 0) return;
            liveBoxesScratch.Clear();
            CollectLive(survivor, liveBoxesScratch);
            // Scroll re-anchoring safety net (in-editor find: typing scrolled
            // the page to the top). The states are keyed by BOX INSTANCE; the
            // cache-replacement path transfers explicitly
            // (PreserveScrollStateForReplacement), but any OTHER path that
            // replaces a scroll container's box would silently orphan its
            // scrolled state — the painter's Get(newBox) misses and the
            // content renders untranslated. ScrollContainer.ReanchorOrphans
            // resolves each orphan's live box by the ELEMENT captured at link
            // time — a liveness check on the box alone is not enough, because
            // a recycled instance can be re-rented as a DIFFERENT box within
            // the same pass (live find: .page's scrolled box came back as an
            // anonymous wrapper — alive, so the old skip-if-live check left
            // the fresh .page box at 0).
            reanchorSurvivor = survivor;
            reanchorResolver ??= el => FindLiveBoxFor(reanchorSurvivor, el);
            scrollContainer.ReanchorOrphans(liveBoxesScratch, reanchorResolver);
            reanchorSurvivor = null;
            scrollContainer.RetainOnly(liveBoxesScratch);
        }

        // Cached resolver for ReanchorOrphans (avoids a closure allocation
        // per layout pass); reanchorSurvivor is set for the duration of the
        // PruneScrollState call only.
        Box reanchorSurvivor;
        System.Func<Weva.Dom.Element, Box> reanchorResolver;

        static Box FindLiveBoxFor(Box root, Weva.Dom.Element el) {
            if (root == null) return null;
            if (ReferenceEquals(root.Element, el)) return root;
            foreach (var c in root.Children) {
                var hit = FindLiveBoxFor(c, el);
                if (hit != null) return hit;
            }
            return null;
        }

        static void CollectLive(Box b, HashSet<Box> live) {
            if (b == null) return;
            live.Add(b);
            foreach (var c in b.Children) CollectLive(c, live);
        }

        // Locates the document's `<html>` element (the first root-level element
        // whose tag name is `html`) and copies its cascaded font-size into
        // ctx.RootFontSizePx. Without this, an author rule like
        // `html { font-size: 20px }` would correctly affect the html element's
        // own em-relative computations but every descendant's `rem` would still
        // resolve against the static construction-time default. Per CSS Values
        // L4 §6.2 the `rem` unit is defined as the computed font-size of the
        // document root; we propagate it here so CssLength.ToPixels sees the
        // right value.
        static void UpdateRootFontSizeFromHtml(Document doc, Func<Element, ComputedStyle> styleOf, LayoutContext ctx) {
            Element html = null;
            foreach (var c in doc.Children) {
                if (c is Element e && e.TagName == "html") { html = e; break; }
            }
            if (html == null) return;
            // Prefer ctx.SnapshotStyles when the lifecycle has wired both the
            // snapshot and its parallel StyleArray onto the context — that's
            // the zero-styleOf-call path LayoutSnapshotReuseTests pins.
            // HtmlParser now always synthesises an `<html>` wrapper for
            // fragments, so this code path is hit on every Layout call; in
            // the snapshot pipeline we must not fall back to the legacy
            // styleOf delegate just to read the root font-size.
            ComputedStyle style = null;
            if (ctx.Snapshot != null && ctx.SnapshotStyles != null) {
                int htmlNodeId = FindHtmlNodeId(ctx.Snapshot, html);
                if (htmlNodeId >= 0) style = ctx.SnapshotStyles.Get(htmlNodeId);
            }
            if (style == null) style = styleOf(html);
            if (style == null) return;
            // FontSizePx with parentStyle == null falls back to ctx.RootFontSizePx
            // for the inherited computation, which is the spec-correct seed for
            // the document root (the initial containing block's font-size is
            // implementation-defined and `medium` keyword resolves against it).
            double fs = StyleResolver.FontSizePx(style, null, ctx);
            if (fs > 0) ctx.RootFontSizePx = fs;
            // H5b: also seed ctx.RootLineHeightPx so `rlh` lengths in
            // descendants resolve against the cascaded root line-height
            // rather than the RootFontSizePx * 1.2 fallback.
            var fam = style.Get(CssProperties.FontFamilyId);
            double lh = StyleResolver.LineHeightPx(style, fs > 0 ? fs : ctx.RootFontSizePx, ctx, ctx.GetMetrics(fam));
            if (lh > 0) ctx.RootLineHeightPx = lh;
        }

        // Look up the NodeId of the document's `<html>` element in the
        // snapshot's ManagedNodes sidecar so we can read its style from
        // ctx.SnapshotStyles without falling back to the styleOf delegate.
        // Returns -1 if not found (which shouldn't happen now that
        // HtmlParser always synthesises an `<html>`, but the snapshot may
        // be stale relative to the managed doc during mutations).
        static int FindHtmlNodeId(Weva.Compiled.DomSnapshot snap, Element html) {
            if (snap == null || html == null) return -1;
            var managed = snap.ManagedNodes;
            int n = snap.NodeCount;
            for (int i = 0; i < n; i++) {
                if (ReferenceEquals(managed[i], html)) return i;
            }
            return -1;
        }

        void BumpContextVersionIfChanged(LayoutContext ctx) {
            if (ctx.ViewportWidthPx != lastViewportWidth || ctx.ViewportHeightPx != lastViewportHeight) {
                if (!double.IsNaN(lastViewportWidth) || !double.IsNaN(lastViewportHeight)) {
                    viewportLayoutContextVersion++;
                }
                lastViewportWidth = ctx.ViewportWidthPx;
                lastViewportHeight = ctx.ViewportHeightPx;
            }
        }

        Box Reconcile(Box root) {
            var survivor = ReconcileBox(root, null);
            // After Reconcile, re-stamp Parent pointers across the survivor
            // tree. ReconcileBox can return cached boxes via the per-Element
            // cache; those replace freshly-built children via ReplaceChild,
            // which correctly sets the cached child's Parent at the moment
            // of replacement. But the freshly-built PARENT may itself then
            // be replaced by ITS cached version a level up, leaving the
            // cached child with a Parent pointer aimed at the now-orphaned
            // fresh box (which is moments away from EndPass / ResetForPool).
            // ContainingBlockResolver walks Parent pointers to find the
            // nearest positioned ancestor — an orphaned Parent yields a
            // walk that falls through to the viewport (or, worse, lands on
            // a box that the pool has already reallocated into something
            // unrelated like a tooltip — observed as cbW=1800 on the
            // skill-slot-icon img with a positioned tooltip nearby). The
            // walk below makes one O(n) pass over the survivor tree and
            // restores the invariant that every box's Parent points to
            // its actual structural parent.
            if (survivor != null) RestampParents(survivor);
            return survivor;
        }

        static void RestampParents(Box box) {
            for (int i = 0; i < box.Children.Count; i++) {
                var child = box.Children[i];
                if (child == null) continue;
                child.Parent = box;
                RestampParents(child);
            }
        }

        Box ReconcileBox(Box box, Box parent) {
            // Scroll-boundary reuse: a ReuseContent container's children ARE the
            // prior frame's already-reconciled cached boxes (grafted in verbatim).
            // Re-descending would re-stamp / risk swapping them against their own
            // cache entries; skip straight to reconciling the container box itself
            // so its (possibly moved) frame updates while the frozen subtree is
            // left intact.
            if (!box.ReuseContent) {
                for (int i = 0; i < box.Children.Count; i++) {
                    var child = box.Children[i];
                    var replacement = ReconcileBox(child, box);
                    if (!ReferenceEquals(replacement, child)) {
                        box.ReplaceChild(i, replacement);
                    }
                }
            }

            if (box.Element == null || !IsCacheable(box)) {
                box.Version = StableAnonymousVersion(box);
                return box;
            }

            long childAgg = ChildAggregate(box);
            long containerW = QuantizeContainer(parent != null ? parent.ContentWidth : lastViewportWidth);
            long containerH = QuantizeContainer(parent != null ? parent.ContentHeight : lastViewportHeight);
            long styleVer = box.Style != null ? box.Style.Version : 0;
            long contextVer = LayoutContextVersionFor(box, parent);
            var key = new LayoutCacheKey(
                box.Element.Version,
                styleVer,
                containerW,
                containerH,
                contextVer,
                childAgg
            );
            box.CachedDigest = new LayoutDigestKey(
                box.Element.Version,
                styleVer,
                containerW,
                containerH,
                contextVer,
                childAgg
            );

            bool hadEntry = cache.TryGetValue(box.Element, out var entry);
            if (hadEntry
                && entry.Key.Equals(key)
                && SameAssignedGeometry(entry.BoxResult, box)) {
                cacheHits++;
                return entry.BoxResult;
            }

            cacheMisses++;
            box.Version = BoxVersion.Next();
            PreserveScrollStateForReplacement(
                hadEntry ? entry.BoxResult : (lastRoot != null ? FindBoxFor(lastRoot, box.Element) : null),
                box);
            cache[box.Element] = new LayoutCacheEntry(key, box, box.Version);
            return box;
        }

        void PreserveScrollStateForReplacement(Box oldBox, Box newBox) {
            if (oldBox == null || newBox == null || ReferenceEquals(oldBox, newBox)) return;
            scrollContainer.TransferScrollPosition(oldBox, newBox);
        }

        long LayoutContextVersionFor(Box box, Box parent) {
            if (parent == null) return viewportLayoutContextVersion;
            return box?.Style != null && box.Style.HasViewportRelativeValues
                ? viewportLayoutContextVersion
                : layoutContextVersion;
        }

        static bool IsCacheable(Box box) {
            if (box is TextRun) return false;
            if (box is LineBox) return false;
            if (box is AnonymousBlockBox) return false;
            // Multi-line span fragments (CSS 2.1 §9.4.2) are pool-allocated
            // clones that share their originating span's Element. Caching
            // them by Element would collapse every fragment onto the
            // line-1 instance — see InlineBox.IsLineFragment.
            if (box is InlineBox ib && ib.IsLineFragment) return false;
            return true;
        }

        static long QuantizeContainer(double v) {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return (long)(v * 1000);
        }

        static long ChildAggregate(Box box) {
            unchecked {
                long acc = 0;
                for (int i = 0; i < box.Children.Count; i++) {
                    var c = box.Children[i];
                    acc = acc * 1315423911L + c.Version;
                }
                return acc;
            }
        }

        static long StableAnonymousVersion(Box box) {
            unchecked {
                long acc = unchecked((long)0x9E3779B97F4A7C15UL);
                acc = acc * 31 + box.GetType().GetHashCode();
                for (int i = 0; i < box.Children.Count; i++) {
                    var c = box.Children[i];
                    acc = acc * 1315423911L + c.Version;
                }
                return acc;
            }
        }

        static bool SameAssignedGeometry(Box cached, Box fresh) {
            if (cached == null || fresh == null) return false;
            if (cached.GetType() != fresh.GetType()) return false;
            return NearlySame(cached.X, fresh.X)
                && NearlySame(cached.Y, fresh.Y)
                && NearlySame(cached.Width, fresh.Width)
                && NearlySame(cached.Height, fresh.Height)
                && NearlySame(cached.MarginTop, fresh.MarginTop)
                && NearlySame(cached.MarginRight, fresh.MarginRight)
                && NearlySame(cached.MarginBottom, fresh.MarginBottom)
                && NearlySame(cached.MarginLeft, fresh.MarginLeft);
        }

        // NearlySame: sub-pixel equality — `<= LayoutEpsilons.SubPixelEqual`.
        // Use when comparing cached vs fresh layout positions where any
        // sub-pixel difference must invalidate the cache.
        //
        // Counterpart to NearlyEqual in LayoutEngine.Incremental.cs which
        // uses HalfPixelEqual for paint-equivalence checks. The signatures
        // are intentionally identical; the threshold split is intentional.
        // Do NOT consolidate the two methods — see the doc-block on
        // LayoutEpsilons.HalfPixelEqual for the rationale.
        static bool NearlySame(double a, double b) {
            return System.Math.Abs(a - b) <= LayoutEpsilons.SubPixelEqual;
        }

        struct LayoutFeatureFlags {
            public bool HasFlex;
            public bool HasGrid;
            public bool HasTable;
            public bool HasMulticol;
            public bool HasOutOfFlow;
            public bool HasPositioningWork;
            public bool HasScrollWork;
            public bool HasAspectRatioWork;
            public bool HasInlineFixupWork;
        }

        LayoutFeatureFlags AnalyzeLayoutFeatures(Box root) {
            flexPassBoxes.Clear();
            thirdFlexPassBoxes.Clear();
            gridPassBoxes.Clear();
            tablePassBoxes.Clear();
            multicolPassBoxes.Clear();
            var flags = new LayoutFeatureFlags();
            AnalyzeLayoutFeatures(root, ref flags);
            return flags;
        }

        void AnalyzeLayoutFeatures(Box box, ref LayoutFeatureFlags flags) {
            if (box == null) return;
            if (box.Position == PositionType.Absolute || box.Position == PositionType.Fixed) {
                flags.HasOutOfFlow = true;
            }
            if (box.Position != PositionType.Static) {
                flags.HasPositioningWork = true;
                if (box.Position == PositionType.Sticky) flags.HasScrollWork = true;
            }
            if (box.Style != null) {
                if (HasAnchorPositioning(box.Style)) flags.HasPositioningWork = true;
                if (HasScrollableOverflow(box.Style)) flags.HasScrollWork = true;
                if (HasAspectRatio(box.Style)) flags.HasAspectRatioWork = true;
            }
            if (box is BlockBox blockWithInline && blockWithInline.ContainsInlines) {
                flags.HasInlineFixupWork = true;
            }
            // Scroll-boundary reuse: the box's OWN flags are recorded above (it
            // is a scroll container, so HasScrollWork is already set), but its
            // content was carried over verbatim — do NOT descend, so the
            // grafted subtree's flex/grid/table boxes are neither collected nor
            // re-run. BlockLayout.LayoutContent skips it on the block side; this
            // is the matching skip for the post-block flex/grid/table passes.
            if (box.ReuseContent) return;
            for (int i = 0; i < box.Children.Count; i++) {
                AnalyzeLayoutFeatures(box.Children[i], ref flags);
            }
            if (box is FlexBox flex) {
                flags.HasFlex = true;
                flexPassBoxes.Add(flex);
            }
            if (box is GridBox grid) {
                flags.HasGrid = true;
                gridPassBoxes.Add(grid);
            }
            if (box is TableBox table) {
                flags.HasTable = true;
                tablePassBoxes.Add(table);
            }
            if (box is MulticolBox mc) {
                flags.HasMulticol = true;
                multicolPassBoxes.Add(mc);
            }
        }

        static bool HasAnchorPositioning(ComputedStyle style) {
            string anchorName = style.Get(CssProperties.AnchorNameId);
            if (!string.IsNullOrEmpty(anchorName) && anchorName != "none") return true;
            string positionAnchor = style.Get(CssProperties.PositionAnchorId);
            return !string.IsNullOrEmpty(positionAnchor) && positionAnchor != "auto";
        }

        static bool HasScrollableOverflow(ComputedStyle style) {
            string ox = style.Get(CssProperties.OverflowXId);
            string oy = style.Get(CssProperties.OverflowYId);
            string o = style.Get(CssProperties.OverflowId);
            return IsNonVisibleOverflow(ox) || IsNonVisibleOverflow(oy) || IsNonVisibleOverflow(o);
        }

        static bool HasAspectRatio(ComputedStyle style) {
            if (style == null) return false;
            string raw = style.Get(CssProperties.AspectRatioId);
            return !string.IsNullOrWhiteSpace(raw) && !CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "auto");
        }

        static bool IsNonVisibleOverflow(string raw) {
            return raw == "auto" || raw == "scroll" || raw == "hidden" || raw == "clip" || raw == "overlay";
        }

        bool CollectThirdFlexPassBoxes() {
            thirdFlexPassBoxes.Clear();
            for (int i = 0; i < flexPassBoxes.Count; i++) {
                var flex = flexPassBoxes[i];
                if (flex.Style == null) continue;
                bool isRow = IsFlexRow(flex);
                if (flex is BlockBox block) {
                    // FlexLayout's third-pass guard only affects containers
                    // whose main-axis auto size would otherwise collapse after
                    // GridLayout/Positioning stamped an external allocation.
                    // Block row-flex containers do not collapse their main
                    // axis, so a grid-stretched header/control row should not
                    // force a third whole-tree flex walk.
                    if (!isRow && block.GridStretchedHeight) thirdFlexPassBoxes.Add(flex);
                    else if (isRow && flex.IsInlineBlock && block.GridStretchedWidth) thirdFlexPassBoxes.Add(flex);
                    else if (HasDirectInFlowGridChild(flex)) thirdFlexPassBoxes.Add(flex);
                }
            }
            return thirdFlexPassBoxes.Count > 0;
        }

        static bool HasDirectInFlowGridChild(FlexBox flex) {
            if (flex == null) return false;
            for (int i = 0; i < flex.Children.Count; i++) {
                if (flex.Children[i] is GridBox grid
                    && grid.Position != PositionType.Absolute
                    && grid.Position != PositionType.Fixed) {
                    return true;
                }
            }
            return false;
        }

        static bool IsFlexRow(FlexBox container) {
            if (container.Style == null) return true;
            string dir = container.Style.Get(CssProperties.FlexDirectionId);
            if (!string.IsNullOrEmpty(dir)) {
                return CssStringUtil.EqualsIgnoreCaseTrimmed(dir, "row")
                    || CssStringUtil.EqualsIgnoreCaseTrimmed(dir, "row-reverse");
            }
            string flow = container.Style.Get(CssProperties.FlexFlowId);
            if (!string.IsNullOrEmpty(flow)) {
                if (HasWhitespaceToken(flow, "column") || HasWhitespaceToken(flow, "column-reverse")) return false;
                if (HasWhitespaceToken(flow, "row") || HasWhitespaceToken(flow, "row-reverse")) return true;
            }
            return true;
        }

        static bool HasWhitespaceToken(string value, string token) {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token)) return false;
            int i = 0;
            while (i < value.Length) {
                while (i < value.Length && IsCssWhitespace(value[i])) i++;
                int start = i;
                while (i < value.Length && !IsCssWhitespace(value[i])) i++;
                int len = i - start;
                if (len == token.Length && TokenEqualsIgnoreCase(value, start, token)) return true;
            }
            return false;
        }

        static bool TokenEqualsIgnoreCase(string value, int start, string token) {
            for (int i = 0; i < token.Length; i++) {
                char a = value[start + i];
                char b = token[i];
                if (a == b) continue;
                if (char.ToLowerInvariant(a) != b) return false;
            }
            return true;
        }

        static bool IsCssWhitespace(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        // Post-block-layout pass: walk the tree depth-first and invoke FlexLayout
        // on every FlexBox so its children are repositioned and resized according to
        // the flex algorithm. Deepest-first so nested flex containers' intrinsic
        // sizes are settled before their ancestor lays them out. The FlexLayout
        // instance is engine-cached and pre-Reset by the caller.
        static void RunFlexPasses(List<FlexBox> boxes, FlexLayout flex) {
            for (int i = 0; i < boxes.Count; i++) {
                flex.Layout(boxes[i]);
            }
        }

        // A cross-stretch / flex-grow chain propagates ONE nesting level per
        // pass (a parent must settle its size before a child can stretch into
        // it). A pure-flex tree only gets this single RunFlexPasses (the post-
        // position and third passes are gated on grid / out-of-flow), so a
        // deeply nested chain — e.g. grid▸card▸nest-root▸nest-col▸nest-mid▸
        // np.right▸np-foot — was left under-converged after one pass. The
        // per-frame incremental gate then skipped re-layout, so the result
        // depended on history and flickered (FLEX-DEEP-CROSS-STRETCH-INCREMENTAL:
        // the flex-playground footer rendered at height 0 on some frames).
        // Iterate until the flex containers' sizes stop changing (capped), so
        // ONE Layout reaches the fixed-point. Alloc-free: a scalar size
        // signature, no per-box snapshot array. Shallow trees settle after the
        // 2nd pass and break immediately.
        static void RunFlexPassesToConvergence(List<FlexBox> boxes, FlexLayout flex) {
            if (boxes.Count == 0) return;
            RunFlexPasses(boxes, flex);
            const int maxExtraIters = 5;
            double prev = FlexSizeSignature(boxes);
            for (int iter = 0; iter < maxExtraIters; iter++) {
                RunFlexPasses(boxes, flex);
                double cur = FlexSizeSignature(boxes);
                if (System.Math.Abs(cur - prev) <= LayoutEpsilons.HalfPixelEqual) break;
                prev = cur;
            }
        }

        // Order-sensitive scalar fingerprint of the flex containers' resolved
        // box sizes — used only to detect a stable fixed-point between passes.
        // Width / Height carry different weights so a compensating swap (one box
        // grows by the same amount another shrinks) doesn't read as "stable".
        static double FlexSizeSignature(List<FlexBox> boxes) {
            double s = 0;
            for (int i = 0; i < boxes.Count; i++) {
                s += (boxes[i].Width * 2.0 + boxes[i].Height * 3.0) * (i + 1);
            }
            return s;
        }

        static void RunFlexPasses(Box root, FlexLayout flex) {
            VisitFlex(root, flex);
        }

        static void VisitFlex(Box box, FlexLayout flex) {
            for (int i = 0; i < box.Children.Count; i++) {
                VisitFlex(box.Children[i], flex);
            }
            if (box is FlexBox fb) flex.Layout(fb);
        }

        // Post-block-layout pass: walk the tree depth-first and invoke GridLayout on
        // every GridBox so its children are repositioned and resized according to the
        // grid algorithm. Mirrors RunFlexPasses; runs after flex so that flex
        // containers inside grid items have settled their main-axis size before grid
        // sizes the cells around them.
        static void RunGridPasses(List<GridBox> boxes, GridLayout grid) {
            for (int i = 0; i < boxes.Count; i++) {
                grid.Layout(boxes[i]);
            }
        }

        static void RunGridPasses(Box root, GridLayout grid) {
            VisitGrid(root, grid);
        }

        // Repair `height: <%>` values whose containing block was sized by
        // PositioningPass (inset, sticky pin, etc.) and was therefore
        // indefinite when BlockLayout originally ran. Walks ONLY subtrees
        // rooted at position: fixed / absolute boxes — that's where the
        // indefinite-CB → percent-fallback problem lives. Plain in-flow
        // trees resolved heights correctly during the first BlockLayout
        // pass and don't need repair, so they short-circuit at the top.
        //
        // v1 only handles plain `<number>%` height values on BlockBoxes
        // whose immediate Parent is a BlockBox with a now-definite
        // Height. calc()/nested-percent/box-sizing border-box explicit
        // subtraction/min-max clamps are deliberate follow-ups.
        static void RepairPercentHeightsUnderOutOfFlow(Box root, LayoutContext ctx) {
            if (root is BlockBox bb && bb.Position != PositionType.Static
                && (bb.Position == PositionType.Fixed || bb.Position == PositionType.Absolute)
                && bb.Height > 0) {
                RepairPercentHeightsSubtree(bb);
            }
            for (int i = 0; i < root.Children.Count; i++) {
                RepairPercentHeightsUnderOutOfFlow(root.Children[i], ctx);
            }
        }

        static void RepairPercentHeightsSubtree(Box root) {
            for (int i = 0; i < root.Children.Count; i++) {
                var child = root.Children[i];
                if (child is BlockBox bb && bb.Style != null && bb.Parent is BlockBox parent && parent.ContentHeight > 0) {
                    string raw = bb.Style.Get("height");
                    if (!string.IsNullOrEmpty(raw) && raw.EndsWith("%")) {
                        string numPart = raw.Substring(0, raw.Length - 1).Trim();
                        if (double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double pct)) {
                            // CSS Sizing L3 §5.3 — % heights resolve against
                            // the containing block's CONTENT height, not its
                            // outer (border-box) height. parent.ContentHeight
                            // == parent.Height - parent's vertical
                            // padding+border (computed lazily by BlockBox).
                            double resolved = parent.ContentHeight * pct * 0.01;
                            string boxSizing = bb.Style.Get("box-sizing");
                            if (boxSizing != "border-box") {
                                // Content-box: resolved is the content area
                                // height; the engine stores OUTER Height in
                                // box.Height, so add own padding+border.
                                resolved += bb.PaddingTop + bb.PaddingBottom + bb.BorderTop + bb.BorderBottom;
                            }
                            if (resolved < 0) resolved = 0;
                            if (System.Math.Abs(bb.Height - resolved) > LayoutEpsilons.HalfPixelEqual) {
                                bb.Height = resolved;
                            }
                        }
                    }
                }
                RepairPercentHeightsSubtree(child);
            }
        }

        static void VisitGrid(Box box, GridLayout grid) {
            for (int i = 0; i < box.Children.Count; i++) {
                VisitGrid(box.Children[i], grid);
            }
            if (box is GridBox gb) grid.Layout(gb);
        }

        // Post-block-layout pass for table layout. Walks depth-first and
        // invokes TableLayout on every TableBox so rows/cells get sized
        // and positioned per CSS 2.1 §17. Runs after flex/grid because
        // a flex/grid container inside a table cell needs to settle its
        // intrinsic size before the table column algorithm consults it.
        static void RunTablePasses(List<TableBox> boxes, Weva.Layout.Tables.TableLayout table) {
            for (int i = 0; i < boxes.Count; i++) {
                table.Layout(boxes[i]);
            }
        }

        static void RunTablePasses(Box root, Weva.Layout.Tables.TableLayout table) {
            VisitTable(root, table);
        }

        static void VisitTable(Box box, Weva.Layout.Tables.TableLayout table) {
            for (int i = 0; i < box.Children.Count; i++) {
                VisitTable(box.Children[i], table);
            }
            if (box is Weva.Layout.Tables.TableBox tb) table.Layout(tb);
        }

        // Post-block-layout pass for multi-column layout.  Iterates the
        // pre-collected list of MulticolBox instances (depth-first post-order,
        // innermost first so nested multicol containers are resolved before
        // their ancestors).  Runs after table passes because a table inside a
        // multicol column needs its row/cell geometry settled before multicol
        // measures child heights for balanced distribution.
        static void RunMulticolPasses(List<MulticolBox> boxes, MulticolLayout multicol) {
            for (int i = 0; i < boxes.Count; i++) {
                multicol.Layout(boxes[i]);
            }
        }
    }
}
