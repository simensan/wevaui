using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using Weva.Layout.Incremental;
using Weva.Reactive;

namespace Weva.Layout {
    // Per-element layout-subtree-skip path. Co-located here (not in
    // LayoutEngine.cs) so sister tasks editing LayoutEngine.cs (pass pooling
    // additions) don't merge-conflict.
    //
    // Why is this even possible? CSS layout's containing-block model defines
    // each box's outer size and position as a function of:
    //   (a) its computed style,
    //   (b) the inline-axis size of its containing block, and
    //   (c) for some sizing models, the intrinsic sizes of its descendants
    //       (shrink-to-fit, flex item content min, grid-track auto, etc.).
    // When (b) and (c) are stable for a subtree across two frames, only the
    // dirty subtree's INTERNAL geometry needs to be recomputed. The siblings
    // outside the subtree retain their X/Y/Width/Height from the prior frame
    // verbatim, by the determinism of the algorithm: same inputs -> same
    // output.
    //
    // The v1 conservative predicate for "stable parent" is:
    //   - The element's parent box exists in lastRoot.
    //   - The parent box's CachedDigest is unchanged (no Layout or Style flag
    //     on the parent in the tracker, parent's element/style versions match
    //     the prior frame).
    //   - The element's own computed style does NOT have % width/height
    //     against the parent — already handled by digest mismatch when it
    //     does, but we additionally require the parent's content-box
    //     dimensions to match the digest's QuantizeContainer values.
    //   - Flex / grid items may use the subtree path only when the rebuilt
    //     item keeps the exact same outer geometry. Any size/margin change
    //     falls back to full layout so the parent can redistribute tracks or
    //     flex lines.
    //   - The dirty element does NOT contain inline content that would
    //     re-flow into different line-box arrangements at a different width.
    //     Conservative: bail when the box has any inline children.
    //
    // When the predicate fails we fall through to the existing full Layout
    // pass. The bench's hover-with-layout-prop case is exactly the case the
    // v1 predicate handles: a button that's a block-flow descendant of a
    // form-row, whose hover changes border-width by 4px without touching the
    // parent's width.
    public sealed partial class LayoutEngine {
        // A/B toggle for the pure-bubble-ancestor skip (the layout-stress 20x
        // speedup). Default ON. Flip to false at runtime
        // (Weva.Layout.LayoutEngine.EnableBubbleSkip = false) to fall back to the
        // pre-fix behaviour (bubbled ancestors collapse the dirty set to the
        // root → whole-tree rebuild) for isolating whether a visual issue comes
        // from this optimisation.
        public static bool EnableBubbleSkip = true;

        // Reusable scratch buffers for the subtree-skip walk. Cleared per call.
        readonly List<Element> subtreeDirtyScratch = new();
        // L8: dirty-Layout elements resolved once per TryLayoutSubtree call so
        // IsBubbledLayoutAncestor doesn't re-walk + re-cast tracker.DirtyEntries
        // on every candidate (that was O(D) dictionary iteration with per-entry
        // casts on each of the D calls, before the >16 cap even runs).
        readonly List<Element> dirtyLayoutScratch = new();

        long subtreeSkipHits;

        public long SubtreeSkipHits => subtreeSkipHits;

        // Incremental-path stage timings, populated only when CollectStageTimings
        // is on (the diagnostic logger flips it). The full-layout Last*Ms in
        // LayoutEngine.cs stay frozen across incremental flips because this path
        // returns before ResetStageTimings — so these separate fields are the
        // only window into where a slow warm flip spends its time.
        internal enum LayoutPath { None, Skip, Subtree, Full }
        internal LayoutPath LastPath { get; private set; }
        internal int LastDirtyCount { get; private set; }
        internal double LastIncrNormalizeMs { get; private set; }
        internal double LastIncrCommitMs { get; private set; }
        internal double LastIncrOofMs { get; private set; }
        internal double LastIncrScrollMs { get; private set; }
        internal double LastIncrViewportScrollMs { get; private set; }
        internal string LastDirtyLabel { get; private set; }
        internal int LastSubtreeNodeCount { get; private set; }

        static string DescribeElement(Element e) {
            if (e == null) return "<null>";
            string cls = e.GetAttribute("class");
            return string.IsNullOrEmpty(cls) ? e.TagName : e.TagName + "." + cls;
        }

        static int CountDomDescendants(Element e) {
            if (e == null) return 0;
            int n = 1;
            foreach (var child in e.Children) {
                if (child is Element ce) n += CountDomDescendants(ce);
            }
            return n;
        }

        static double TicksToMs(long startTimestamp) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Attempts a subtree-only relayout. Returns true if the subtree path
        // was taken AND succeeded; false to fall through to full layout. The
        // caller (Layout) owns the surrounding bookkeeping (BumpContextVersion,
        // boxPool BeginPass/EndPass, scrollContainer prune). When this method
        // returns true, lastRoot is unchanged (same instance) and its dirty
        // subtrees have been replaced in place with freshly-laid-out boxes.
        bool TryLayoutSubtree(Document doc, System.Func<Element, ComputedStyle> styleOf, LayoutContext ctx, InvalidationTracker tracker) {
            if (lastRoot == null || tracker == null) return false;
            if (ViewportChanged(ctx)) return false;
            if (tracker.HasAny(InvalidationKind.Structure)) return false;
            long tNormalize = System.Diagnostics.Stopwatch.GetTimestamp();
            subtreeDirtyScratch.Clear();
            // L8: resolve the dirty-Layout elements once, up front, so the
            // bubble-ancestor test below is a tight list walk instead of a
            // fresh DirtyEntries dictionary scan (with per-entry kind check +
            // cast) on every candidate.
            dirtyLayoutScratch.Clear();
            foreach (var kv in tracker.DirtyEntries) {
                if ((kv.Value & InvalidationKind.Layout) == 0) continue;
                Element el = kv.Key as Element ?? (kv.Key as TextNode)?.Parent as Element;
                if (el != null) dirtyLayoutScratch.Add(el);
            }
            foreach (var kv in tracker.DirtyEntries) {
                if ((kv.Value & InvalidationKind.Layout) == 0) continue;
                // Skip pure-bubble ancestors. MarkLayoutForElement marks the
                // change ORIGIN plus every ancestor up to the nearest layout
                // boundary (root, when no ancestor pins width+height) with
                // InvalidationKind.Layout. Those ancestor marks are conservative
                // "might-need-relayout" hints — NOT independent change sites. If
                // we seed the subtree candidate set from them, AddSubtreeDirty-
                // Candidate keeps the OUTERMOST (it drops descendants under an
                // ancestor), collapsing every animation to <html> and turning the
                // "incremental" path into a whole-tree rebuild every frame. Seed
                // only from the actual origins; the origin's own RelayoutOneSubtree
                // + outer-geometry bubble re-derives exactly which ancestors must
                // move, so a contained change (clipped progress bar) stays local
                // even when its bubble marks reach the root.
                if (EnableBubbleSkip && IsBubbledLayoutAncestor(kv.Key, tracker, dirtyLayoutScratch)) {
                    continue;
                }
                var normalized = NormalizeSubtreeDirty(kv.Key, styleOf);
                AddSubtreeDirtyCandidate(normalized);
            }
            if (subtreeDirtyScratch.Count == 0) return false;
            // v1 cap: if too many distinct subtrees are dirty, the splice
            // overhead approaches a full re-layout's cost. Threshold is
            // empirical; a single hover toggle bubble-marks 1-3 elements.
            if (subtreeDirtyScratch.Count > 16) return false;

            // Pre-flight: every dirty element must meet the per-element
            // predicate. Only on full success do we commit. This avoids
            // partial splices that would leave lastRoot inconsistent.
            for (int i = 0; i < subtreeDirtyScratch.Count; i++) {
                if (!CanSubtreeRelayout(subtreeDirtyScratch[i], styleOf)) {
                    InvalidateSubtree(subtreeDirtyScratch[i]);
                    return false;
                }
            }

            // Commit: scoped BoxPool pass for any box allocations from the
            // splice. Pre-existing boxes in lastRoot are NOT in `allocated`
            // for this pass, so EndPass leaves them alone. Freshly-built
            // boxes that ended up inside lastRoot are kept (survivor walk
            // sees them); ones that didn't (extremely defensive — we always
            // splice on success) get recycled.
            bool timing = CollectStageTimings;
            long tCommit = System.Diagnostics.Stopwatch.GetTimestamp();
            if (timing) { LastIncrNormalizeMs = TicksToMs(tNormalize); }
            boxPool.BeginPass();
            using var cssScope = CssValuePool.PassScope();
            for (int i = 0; i < subtreeDirtyScratch.Count; i++) {
                var elem = subtreeDirtyScratch[i];
                if (!RelayoutOneSubtree(elem, styleOf, ctx)
                    && !RelayoutPromotedStableAncestor(elem, styleOf, ctx)
                    && !RelayoutBubbleToStableAncestor(elem, styleOf, ctx)) {
                    InvalidateFallbackAncestors(elem);
                    boxPool.EndPass(lastRoot);
                    return false;
                }
            }
            if (timing) { LastIncrCommitMs = TicksToMs(tCommit); }
            // Whole-tree reposition of out-of-flow boxes. RelayoutOneSubtree
            // already repositioned the spliced subtree's own OOF descendants,
            // and the subtree path guarantees the subtree's outer geometry is
            // unchanged — so OOF boxes OUTSIDE it can't have moved. The only
            // reason to walk lastRoot is OOF boxes elsewhere; when the document
            // has none, skip the O(tree) walk entirely (it was a measured
            // ~120µs/flip on a 1500-box OOF-free tree). The count is from the
            // last full positioning Run and stays valid (a structure change
            // forces a full layout, which re-runs it).
            long tOof = System.Diagnostics.Stopwatch.GetTimestamp();
            if (positioningPass.LastOutOfFlowCount > 0) {
                // pinOnly (audit LY2): the incremental path runs NO flex/grid
                // restoration after this call, and the full reposition's
                // shrink-to-fit RelayoutContentAt re-stacks a flex/grid abs
                // container's children as block flow (the pass header
                // documents the contract). A warm flip elsewhere in the tree
                // was corrupting every `position:absolute > display:flex`
                // subtree until the next full layout. Re-pinning positions
                // against the current CBs is all this site needs; content
                // widths were computed by the last full tower.
                positioningPass.RepositionAbsolutes(lastRoot, ctx, pinOnly: true);
            }
            if (timing) { LastIncrOofMs = TicksToMs(tOof); }
            // Scroll + sticky. The previous code ran the WHOLE-TREE
            // scrollLayout.Run(lastRoot) + stickyResolver.Resolve(lastRoot)
            // whenever ANY scroll container existed — and a document that only
            // overflows the VIEWPORT has one (the root box, created by
            // RunViewportScroll). That made every warm flip walk all ~1500
            // boxes hunting for element-level scroll containers (measured
            // ~445µs/flip — the dominant incremental-layout cost on glass.html
            // and any page with a scroll area).
            //
            // The subtree-path invariant (the dirty subtree's OUTER geometry is
            // unchanged — RelayoutOneSubtree bails to full layout otherwise)
            // means scroll containers OUTSIDE the dirty subtree cannot have
            // changed their content extent, and the viewport is handled
            // separately by RunViewportScroll below. So:
            //   - scroll extent / new-container detection only needs the dirty
            //     subtree(s) — Visit each instead of the whole tree.
            //   - sticky positions are a pure function of unchanged geometry +
            //     scroll offset, so they don't move on a flip; skip the
            //     whole-tree sticky walk when the document has no sticky boxes,
            //     and otherwise re-resolve from lastRoot (correct, rare).
            long tScroll = System.Diagnostics.Stopwatch.GetTimestamp();
            if (scrollContainer.Count > 0) {
                for (int i = 0; i < subtreeDirtyScratch.Count; i++) {
                    var dirtyBox = FindBoxFor(lastRoot, subtreeDirtyScratch[i]);
                    if (dirtyBox != null) scrollLayout.Run(dirtyBox);
                }
                if (positioningPass.LastStickyCount > 0) stickyResolver.Resolve(lastRoot);
            }
            if (timing) { LastIncrScrollMs = TicksToMs(tScroll); }
            // Subtree path uses the lighter EndPass: every box this pass
            // allocated either ended up inside the spliced subtree (in
            // which case its Parent is set) or was discarded mid-build
            // (Parent stays null). Walking allocated[] avoids the
            // O(tree size) MarkSurvivors pass that the full-Layout path
            // needs to reconcile cache/fresh swaps.
            boxPool.EndPassByAllocatedParent();
            // Rescue scrolled state off boxes RecycleSubtree returned this
            // pass. PreserveScrollStateForReplacement covers only the splice
            // ROOT; a scroll container that is a DESCENDANT of the spliced
            // subtree is recycled with no transfer — and if the instance is
            // re-rented within the same pass, the entry is simultaneously
            // alive-by-instance and dead-by-generation, which no full-layout
            // prune ever sees because this path returns before them. The
            // generation-only sweep is O(#states) when nothing is stale.
            if (scrollContainer.Count > 0) {
                reanchorSurvivor = lastRoot;
                reanchorResolver ??= el => FindLiveBoxFor(reanchorSurvivor, el);
                scrollContainer.ReanchorStaleGenerations(reanchorResolver);
                reanchorSurvivor = null;
            }
            long tVp = System.Diagnostics.Stopwatch.GetTimestamp();
            // lastRoot IS the survivor on the incremental path. Update the
            // viewport (root-level) scroll state against it directly. Runs
            // outside the scrollContainer.Count gate above: viewport scroll
            // is keyed on the root box and can appear even when the document
            // has no element-level overflow:scroll/auto container.
            scrollLayout.RunViewportScroll(lastRoot, ctx.ViewportWidthPx, ctx.ViewportHeightPx);
            // Always record the path (not just under CollectStageTimings) —
            // the lifecycle keys the post-layout ElementToBoxIndex rebuild on
            // `LastPath != Skip` (P4). Leaving a stale Skip here after a
            // successful splice made the lifecycle keep an Element->Box map
            // full of boxes RecycleSubtree had just pooled (audit LY1).
            LastPath = LayoutPath.Subtree;
            if (timing) {
                LastIncrViewportScrollMs = TicksToMs(tVp);
                LastDirtyCount = subtreeDirtyScratch.Count;
                var first = subtreeDirtyScratch.Count > 0 ? subtreeDirtyScratch[0] : null;
                LastDirtyLabel = DescribeElement(first);
                LastSubtreeNodeCount = CountDomDescendants(first);
            }
            subtreeSkipHits++;
            return true;
        }

        Element NormalizeSubtreeDirty(Node dirty, System.Func<Element, ComputedStyle> styleOf) {
            Element elem = dirty as Element;
            bool fromTextNode = false;
            if (elem == null && dirty is TextNode text) {
                elem = text.Parent as Element;
                fromTextNode = true;
            }
            if (elem == null) return null;
            var box = FindBoxFor(lastRoot, elem);
            while (box == null && elem.Parent is Element parent) {
                elem = parent;
                box = FindBoxFor(lastRoot, elem);
            }
            if (box == null) return elem;
            if (!fromTextNode && (box.Parent is FlexBox || box.Parent is GridBox)) {
                var parentElement = box.Parent.Element;
                if (parentElement != null && CanSubtreeRelayout(parentElement, styleOf)) {
                    return parentElement;
                }
            }
            for (var cursor = box; cursor != null; cursor = cursor.Parent) {
                if (cursor.Element == null) continue;
                var formattingParent = cursor.Parent;
                while (formattingParent != null && formattingParent.Element == null) {
                    formattingParent = formattingParent.Parent;
                }
                if ((formattingParent is FlexBox || formattingParent is GridBox)
                    && formattingParent.Element != null
                    && CanSubtreeRelayout(formattingParent.Element, styleOf)) {
                    return formattingParent.Element;
                }
                if (cursor is BlockBox && !(cursor is AnonymousBlockBox)
                    && CanSubtreeRelayout(cursor.Element, styleOf)) {
                    return cursor.Element;
                }
            }
            return elem;
        }

        void AddSubtreeDirtyCandidate(Element elem) {
            if (elem == null) return;
            for (int i = 0; i < subtreeDirtyScratch.Count; i++) {
                var existing = subtreeDirtyScratch[i];
                if (ReferenceEquals(existing, elem) || IsAncestor(existing, elem)) {
                    return;
                }
                if (IsAncestor(elem, existing)) {
                    subtreeDirtyScratch.RemoveAt(i);
                    i--;
                }
            }
            subtreeDirtyScratch.Add(elem);
        }

        void InvalidateFallbackAncestors(Element elem) {
            if (elem == null) return;
            InvalidateSubtree(elem);
            var box = FindBoxFor(lastRoot, elem);
            for (var cursor = box?.Parent; cursor != null; cursor = cursor.Parent) {
                if (cursor.Element != null) {
                    // A failed subtree probe means the dirty box's outer
                    // footprint changed. Any formatting parent may reposition
                    // siblings from that footprint, so cached descendants under
                    // that parent are no longer safe to resurrect during the
                    // fallback full layout.
                    InvalidateSubtree(cursor.Element);
                    return;
                }
                if (cursor.Element != null) cache.Remove(cursor.Element);
                if (cursor is FlexBox || cursor is GridBox) break;
            }
        }

        // True when `node`'s layout dirtiness is purely the result of the
        // MarkLayoutForElement ancestor-walk bubbling up from a descendant —
        // i.e. it is NOT an independent change origin. Signature: the bubble
        // walk marks ancestors with EXACTLY InvalidationKind.Layout (origins
        // additionally carry Paint/Style/Structure, or — for the text-binding
        // case — have no Layout-dirty descendant), and such a node has at least
        // one OTHER Layout-dirty node beneath it in the tree (the origin).
        // Skipping these as subtree seeds prevents the collapse-to-root that
        // turns the incremental path into a full rebuild.
        static bool IsBubbledLayoutAncestor(Node node, InvalidationTracker tracker, List<Element> dirtyLayoutElements) {
            if (!(node is Element elem)) return false;
            // Exactly Layout (no Paint/Style/Structure/Composite/PseudoClass) is
            // the ancestor-walk signature. A real origin carries extra bits, or
            // (text binding) is the deepest mark with no dirty descendant below.
            if (tracker.GetKinds(node) != InvalidationKind.Layout) return false;
            // Walk the precomputed dirty-Layout elements (resolved once by the
            // caller). The self entry is skipped by the ReferenceEquals guard —
            // identical to the old `kv.Key == node` + `other == elem` pair.
            for (int i = 0; i < dirtyLayoutElements.Count; i++) {
                var other = dirtyLayoutElements[i];
                if (ReferenceEquals(other, elem)) continue;
                if (IsAncestor(elem, other)) return true;
            }
            return false;
        }

        static bool IsAncestor(Element ancestor, Element descendant) {
            if (ancestor == null || descendant == null) return false;
            for (var cursor = descendant.Parent as Element; cursor != null; cursor = cursor.Parent as Element) {
                if (ReferenceEquals(cursor, ancestor)) return true;
            }
            return false;
        }

        // Pre-flight predicate: returns true iff a subtree relayout is safe
        // for `elem`. Mirrors the checks in RelayoutOneSubtree but without
        // allocating.
        bool CanSubtreeRelayout(Element elem, System.Func<Element, ComputedStyle> styleOf) {
            if (elem == null) return false;
            var oldBox = FindBoxFor(lastRoot, elem);
            if (oldBox == null) return false;
            if (!(oldBox is BlockBox)) return false;
            var parentBox = oldBox.Parent;
            if (parentBox == null) return false;
            // Out-of-flow elements (position: absolute / fixed) don't
            // participate in the parent's flex/grid distribution — they
            // size against the containing block, not the parent's
            // formatting context. Permit subtree relayout for them even
            // when the parent is flex / grid / inline-formatting. Without
            // this carve-out a single `position: absolute` element
            // animating its padding (combo-banner in match3) falls back
            // to the full O(N) layout pass every frame.
            if (IsOutOfFlow(oldBox.Style)) return true;
            if (!(parentBox is FlexBox) && !(parentBox is GridBox)
                && parentBox is BlockBox pbb && pbb.ContainsInlines) return false;
            return true;
        }

        static bool IsOutOfFlow(ComputedStyle s) {
            if (s == null) return false;
            string pos = s.Get("position");
            return pos == "absolute" || pos == "fixed";
        }

        // Relays out the box for `elem` while keeping lastRoot's surrounding
        // boxes in place. Returns false if the subtree predicate is not met
        // (caller falls back to full layout).
        bool RelayoutOneSubtree(Element elem, System.Func<Element, ComputedStyle> styleOf, LayoutContext ctx, bool preserveStableAllocation = false) {
            // Locate the existing box in lastRoot.
            var oldBox = FindBoxFor(lastRoot, elem);
            if (oldBox == null) return false;
            var parentBox = oldBox.Parent;
            if (parentBox == null) return false; // root box: full layout
            bool outOfFlow = IsOutOfFlow(oldBox.Style);
            if (!outOfFlow) {
                // v1: skip when the parent contains anonymous inline content —
                // line-box arrangements depend on every inline's intrinsic width.
                // Same carve-out logic for out-of-flow elements (they don't
                // participate in inline formatting either).
                if (!(parentBox is FlexBox) && !(parentBox is GridBox)
                    && parentBox is BlockBox pbb && pbb.ContainsInlines) return false;
            }
            // v1: also skip when the dirty box itself participates in inline
            // formatting (text-runs / inline boxes don't participate in the
            // per-Element cache anyway).
            if (oldBox is TextRun || oldBox is LineBox || oldBox is AnonymousBlockBox) return false;
            // We need the dirty box to be a BlockBox (block-level) for v1:
            // its outer Width is determined by the parent's content width and
            // its own margins, both of which we have on the prior parentBox.
            if (!(oldBox is BlockBox oldBlock)) return false;
            if (outOfFlow && HasAuthorControlledOutOfFlowSize(oldBlock.Style)) {
                // Absolute/fixed boxes with author-controlled size (including
                // viewport units) need the full positioning/grid context to
                // preserve their allocation. A local block-layout splice can
                // relayout inner grid rows against content height instead.
                return false;
            }

            // Build a fresh box for the dirty element subtree only. The outer
            // CssValuePool scope is owned by TryLayoutSubtree; we don't open
            // a nested one here.
            var builder = GetOrCreatePooledBoxBuilder(styleOf);
            var elemStyle = styleOf(elem);
            if (elemStyle == null) return false;
            var newBox = builder.Build(elem, elemStyle);
            if (!(newBox is BlockBox newBlock)) return false;
            newBlock.Parent = parentBox;

            // Run BlockLayout on the subtree using the parent's content width
            // as the containing-block width. The parent's box is unchanged so
            // its X/Y are still valid. Reuses the engine-cached pass instances;
            // their ctx is refreshed via Reset before we call into them.
            inlineLayout.Reset(ctx);
            blockLayout.Reset(ctx);
            flexLayout.Reset(ctx);
            gridLayout.Reset(ctx);
            bool parentDistributesItems = parentBox is FlexBox || parentBox is GridBox;
            double containingWidth = parentDistributesItems && !outOfFlow
                ? oldBlock.Width + oldBlock.MarginLeft + oldBlock.MarginRight
                : parentBox.ContentWidth;
            Weva.Layout.Positioning.PositioningPass.Stamp(newBlock);
            // NOTE: do NOT call positioningPass.RepositionAbsolutes here. On a
            // freshly-built subtree the boxes have not been through ApplyBoxModel
            // yet, so OOF descendants have padding=0. PositioningPass's shrink-
            // to-fit branch (ApplyAbsoluteAgainst) measures intrinsic width using
            // PaddingLeft+PaddingRight+BorderLeft+BorderRight as the "frame", then
            // stamps the result on ShrinkFitCachedWidth. With frame=0 the cached
            // width is wrong, and worse, BlockLayout's pre-collect loop then sees
            // shrinkApplied=true and SKIPS the LayoutBlock(oof) call — so
            // ApplyBoxModel is never invoked on the OOF box and its padding stays
            // at 0 for the rest of its life. The RepositionAbsolutes calls below
            // (after LayoutBlock has run ApplyBoxModel on every box in the subtree)
            // produce the correct shrink-to-fit width.
            // Containment boundary (see LayoutContext.HeightPropagationBoundary):
            // FlexLayout's block-flow height-delta propagation must not walk
            // out of the spliced subtree into the stale ancestor chain — the
            // splice contract already guarantees newBlock's outer geometry is
            // unchanged (SameOuterGeometry bail / pure-height propagation
            // above), so any pre-flex → post-flex delta is INTERNAL.
            ctx.HeightPropagationBoundary = newBlock;
            try {
            blockLayout.LayoutBlock(newBlock, containingWidth, parentBox.Style);
            // Run flex/grid passes only over the new subtree (a no-op when
            // newBlock isn't a flex/grid container).
            VisitFlexInScope(newBlock, flexLayout);
            VisitGridInScope(newBlock, gridLayout);
            // Full scoped positioning, NOT bare RepositionAbsolutes (in-editor
            // find, audit-validation §6): this subtree is freshly built, so
            // BlockLayout gave out-of-flow children provisional in-flow slots
            // and relative children sit at their static positions — neither
            // has been compressed/offset ONCE yet. RepositionAbsolutes
            // deliberately skips CompressOutOfFlow and ApplyRelative (they
            // are accumulative on RE-run, but this is the first run), so a
            // spliced scope containing `position:absolute` left a badge-sized
            // hole in the flow — the gap between the scroll list's items
            // visibly changed with the animating flex sibling, because only
            // the splice path (not the full tower) laid the list out that
            // way. Same stamp/compress/apply shape as the LY6 scroll-width
            // correction (LayoutEngine.ScrollReuse.RelayoutScrollContentFresh).
            positioningPass.RunScopedForCorrection(newBlock, ctx);
            VisitFlexInScope(newBlock, flexLayout);
            VisitGridInScope(newBlock, gridLayout);
            VisitPostGridFlexInScope(newBlock, flexLayout);
            // pinOnly (audit LY2): this is the FINAL reposition of the splice
            // — no flex/grid restoration runs after it, so the destructive
            // shrink-to-fit relayout is forbidden here (the pass header
            // documents the contract; same rule as the full tower's trailing
            // re-pin at LayoutEngine.Layout). The reposition above (followed
            // by the flex/grid visitors) already computed shrink widths.
            positioningPass.RepositionAbsolutes(newBlock, ctx, pinOnly: true);
            } finally {
                ctx.HeightPropagationBoundary = null;
            }
            if (preserveStableAllocation
                && oldBlock.GridStretchedHeight
                && !HasScrollableOverflow(oldBlock.Style)
                && !NearlyEqual(oldBlock.Height, newBlock.Height)) {
                // A grid-stretched item's assigned height can be reused only
                // when its intrinsic block contribution stayed stable. If a
                // descendant animation changes padding/font/height, preserving
                // the old stretched height hides that contribution from the
                // owning grid/flex container, so siblings and the parent never
                // move. Scroll containers are the exception: their stable
                // viewport height is the contract, while their internal
                // content can still restack inside that viewport.
                return false;
            }
            if (preserveStableAllocation) PreserveStableAllocation(oldBlock, newBlock);

            // If a normal-flow subtree's outer footprint changed, siblings and
            // ancestor content sizes may need to move. Normally we decline the
            // local splice and let the full layout path restack. The exception:
            // a PURE HEIGHT delta in a vertical stacking chain can be propagated
            // incrementally — shift the trailing in-flow siblings by the delta
            // and let a definite-height column-flex's flex-grow child absorb it
            // (its scroll subtree is reused, not re-laid). That keeps a
            // genuinely-propagating animation (font-size on an auto-height flex
            // item) off the full-layout path entirely. Feasibility is checked
            // BEFORE the splice so we never leave a partial state.
            double heightPropagationDelta = 0;
            if (!outOfFlow && !SameOuterGeometry(oldBlock, newBlock)) {
                if (!EnableIncrementalHeightPropagation
                    || !IsPureHeightDelta(oldBlock, newBlock)
                    || positioningPass.LastOutOfFlowCount > 0
                    || positioningPass.LastStickyCount > 0
                    || !CanPropagateHeightDelta(oldBox, newBlock.Height - oldBlock.Height)) {
                    return false;
                }
                heightPropagationDelta = newBlock.Height - oldBlock.Height;
            }

            // The new box keeps the old box's position. Block-level boxes
            // are positioned by the parent's BlockLayout cursor; on a
            // subtree-only relayout the parent's cursor advanced to the SAME
            // place the prior pass left it (since prior siblings are
            // unchanged), so the new box's X/Y carry over from the old box.
            newBlock.X = oldBlock.X;
            newBlock.Y = oldBlock.Y;

            // Splice the new box into the parent's children list, replacing
            // the old box at the same index.
            int idx = IndexInParent(parentBox, oldBox);
            if (idx < 0) return false;
            parentBox.ReplaceChild(idx, newBlock);
            PreserveScrollStateForReplacement(oldBox, newBlock);
            // Return the displaced subtree's boxes to the box pool so the
            // next warm flip can reuse them instead of allocating fresh.
            // Without this the old box graph just becomes GC garbage —
            // ~6 boxes × ~200B = ~1.2KB per warm flip, the bulk of the
            // measured LAYOUT-WARM-ALLOCS budget. Walk post-order so
            // children are recycled before their parent (matches the
            // natural reverse-construction order).
            RecycleSubtree(oldBlock);

            // Stamp JUST the new subtree without substituting cached boxes.
            // Full-tree reconciliation can safely replace children because
            // the whole parent chain is being rebuilt. A subtree splice is
            // different: reusing a cached descendant from the old subtree can
            // leave Parent links pointing at the detached box we just replaced.
            StampFreshSubtree(newBlock, parentBox);
            // Propagate a pure-height delta up the vertical stacking chain
            // (feasibility already confirmed above), now that newBlock is spliced
            // at oldBox's position.
            if (heightPropagationDelta != 0) {
                    ApplyHeightDeltaPropagation(newBlock, heightPropagationDelta, ctx);
            }
            return true;
        }

        void RecycleSubtree(Box box) {
            if (box == null) return;
            var children = box.Children;
            for (int i = children.Count - 1; i >= 0; i--) {
                RecycleSubtree(children[i]);
            }
            boxPool.Recycle(box);
        }

        // ── Incremental height-delta propagation (EnableIncrementalHeightPropagation) ──
        // The genuine fix for a PROPAGATING animation (font-size on an auto-height
        // flex item): instead of falling back to a full layout when the dirty
        // subtree's HEIGHT changes, splice it and push the delta up the vertical
        // stacking chain — shift trailing in-flow siblings, grow auto-height
        // ancestors, and let a definite-height column-flex's flex-grow child
        // absorb the delta (shrink + reposition), reusing that child's subtree
        // (it's a scroll container → its content is intrinsic/clipped and never
        // reflows from the height it's given). Stays entirely off the full-layout
        // path: no whole-tree rebuild, no flex re-run, only the changed subtree +
        // O(depth) sibling shifts. Conservative: only fires for a vertical
        // stacking chain that ends in a definite-height column-flex's flex-grow
        // scroll-container absorber; every other shape falls back to full layout
        // (correct, just not accelerated). Guarded by an equivalence test
        // (Incremental_height_propagation_matches_full_layout).
        public static bool EnableIncrementalHeightPropagation = true;

        static bool IsPureHeightDelta(BlockBox a, BlockBox b) {
            // The "height changed" test MUST mirror SameOuterGeometry's STRICT
            // (sub-pixel) threshold, not the half-pixel NearlyEqual used for the
            // width/margin "unchanged" tests. SameOuterGeometry sends a box here
            // the instant its height differs by > SubPixelEqual; if this method
            // then demanded a > half-pixel change to call it "pure height", every
            // delta in the (SubPixelEqual, HalfPixelEqual] band — e.g. the
            // sub-pixel height jitter a digit-changing counter produces every
            // frame (dh≈0.08–0.17px) — would satisfy neither path and fall through
            // to a full relayout on EVERY frame. Width/margins stay on NearlyEqual:
            // a sub-half-pixel change in those is visually irrelevant and safely
            // treated as "unchanged" (the propagated height delta is exact).
            return NearlyEqual(a.Width, b.Width)
                && NearlyEqual(a.MarginTop, b.MarginTop)
                && NearlyEqual(a.MarginRight, b.MarginRight)
                && NearlyEqual(a.MarginBottom, b.MarginBottom)
                && NearlyEqual(a.MarginLeft, b.MarginLeft)
                && System.Math.Abs(a.Height - b.Height) > LayoutEpsilons.SubPixelEqual;
        }

        // Dry-run feasibility: walk up from the changed box's position confirming
        // every container is a vertical stacking context that can either GROW
        // (auto height) or ABSORB (definite-height column flex whose last in-flow
        // child is a flex-grow scroll container). No mutation.
        bool CanPropagateHeightDelta(Box changedPos, double delta) {
            var cur = changedPos;
            for (int guard = 0; guard < 64; guard++) {
                var parent = cur.Parent;
                if (parent == null) return false;          // reached root → let full layout handle
                if (!IsVerticalStackContainer(parent)) return false;
                var absorber = FindColumnFlexGrowAbsorber(parent, cur);
                if (absorber != null) {
                    return HasScrollableOverflow(absorber.Style); // reusable boundary
                }
                if (!HasAutoHeight(parent)) return false;  // definite container, no absorber → can't grow cleanly
                cur = parent;
            }
            return false;
        }

        void ApplyHeightDeltaPropagation(Box changed, double delta, LayoutContext ctx) {
            var cur = changed;
            for (int guard = 0; guard < 64; guard++) {
                var parent = cur.Parent;
                if (parent == null) return;
                ShiftTrailingInFlowSiblings(parent, cur, delta);
                var absorber = FindColumnFlexGrowAbsorber(parent, cur);
                if (absorber != null) {
                    // The absorber was just shifted by +delta (trailing sibling);
                    // shrink it by delta so its bottom stays put — the flex-grow
                    // child consuming the space the grown sibling took.
                    absorber.Height = System.Math.Max(0, absorber.Height - delta);
                    if (scrollContainer.Count > 0) scrollLayout.Run(absorber);
                    return;
                }
                if (parent is BlockBox pbb) pbb.Height += delta; // grow the auto-height container
                cur = parent;
            }
        }

        bool IsVerticalStackContainer(Box parent) {
            if (parent is FlexBox fb) return IsColumnFlex(fb);
            if (parent is GridBox || parent is Weva.Layout.Tables.TableBox) return false;
            if (parent is BlockBox bb) return !bb.ContainsInlines; // block flow stacks vertically
            return false;
        }

        static bool IsColumnFlex(FlexBox fb) {
            string d = fb.Style?.Get(CssProperties.FlexDirectionId);
            return d == "column" || d == "column-reverse";
        }

        static bool HasAutoHeight(Box box) {
            string h = box.Style?.Get(CssProperties.HeightId);
            return string.IsNullOrEmpty(h) || h == "auto";
        }

        // The last in-flow child of a definite-height column flex, IF it is a
        // flex-grow item distinct from `cur` — the box that absorbs a sibling's
        // height growth. Returns null when the container isn't an absorbing
        // column flex.
        BlockBox FindColumnFlexGrowAbsorber(Box parent, Box cur) {
            if (!(parent is FlexBox fb) || !IsColumnFlex(fb)) return null;
            if (HasAutoHeight(fb)) return null; // auto-height column grows instead of absorbing
            BlockBox last = null;
            var kids = parent.ChildList;
            for (int i = kids.Count - 1; i >= 0; i--) {
                var c = kids[i];
                if (c is TextRun || c is LineBox || c is AnonymousBlockBox) continue;
                if (!(c is BlockBox b)) continue;
                if (b.Element != null && IsOutOfFlow(b.Style)) continue;
                last = b; break;
            }
            if (last == null || ReferenceEquals(last, cur)) return null;
            var lc = ctxForFlexItem;
            var ip = Weva.Layout.Flex.FlexItemProperties.From(last.Style, lc);
            return ip.Grow > 0 ? last : null;
        }

        // Minimal LengthContext for reading flex-item grow (a unitless number —
        // the length basis is irrelevant). Rebuilt lazily per engine.
        Weva.Css.Values.LengthContext ctxForFlexItem =>
            new Weva.Css.Values.LengthContext { BaseFontSizePx = 16, RootFontSizePx = 16, DpiPixelsPerInch = 96 };

        void ShiftTrailingInFlowSiblings(Box parent, Box cur, double delta) {
            var kids = parent.ChildList;
            int idx = IndexInParent(parent, cur);
            if (idx < 0) return;
            for (int i = idx + 1; i < kids.Count; i++) {
                var c = kids[i];
                if (c is TextRun || c is LineBox) continue;
                if (c is BlockBox b && b.Element != null && IsOutOfFlow(b.Style)) continue;
                c.Y += delta; // parent-relative coords → whole subtree moves with it
            }
        }

        static bool HasAuthorControlledOutOfFlowSize(ComputedStyle style) {
            if (style == null) return false;
            string width = style.Get(CssProperties.WidthId);
            string height = style.Get(CssProperties.HeightId);
            return IsNonAutoSize(width) || IsNonAutoSize(height);
        }

        static bool IsNonAutoSize(string raw) {
            return !string.IsNullOrWhiteSpace(raw) && raw.Trim() != "auto";
        }

        bool RelayoutPromotedStableAncestor(Element elem, System.Func<Element, ComputedStyle> styleOf, LayoutContext ctx) {
            var box = FindBoxFor(lastRoot, elem);
            for (var cursor = box; cursor != null; cursor = cursor.Parent) {
                if (cursor.Parent == null) break;
                if (!(cursor is BlockBox block)) continue;
                if (cursor.Element == null) continue;
                if (!HasStableExternalAllocation(block)) continue;
                if (!CanSubtreeRelayout(cursor.Element, styleOf)) continue;
                if (RelayoutOneSubtree(cursor.Element, styleOf, ctx, preserveStableAllocation: true)) {
                    return true;
                }
            }
            return false;
        }

        // Bounded relayout bubble. RelayoutOneSubtree(elem) failed because the
        // element's OWN outer geometry changed, so the change must be absorbed
        // by an ancestor. We try EXACTLY the immediate formatting parent, and
        // ONLY when that parent CLIPS overflow (overflow != visible). A clipping
        // parent contains the change — a child that grows is clipped, never
        // propagated to siblings/ancestors — so relaying out just that parent is
        // correct and (when its own outer size is definite, the common case) its
        // geometry stays stable and the splice succeeds. This is the extremely
        // common progress-bar / fill / scroll-area pattern: a clipped element
        // animating its width/height. RelayoutOneSubtree still gates the splice
        // on the parent's SameOuterGeometry, so an auto-sized clip parent that
        // actually grows correctly falls through to full layout.
        //
        // Why only clipping parents (not a general multi-level bubble): a change
        // that genuinely propagates (font-size on inline content reflowing a
        // flex/grid line) is NOT contained, so bubbling just wastes an expensive
        // rebuild-then-bail before the inevitable full layout (a font flip went
        // 1.8ms → 6.7ms attempting it). Restricting to clipping parents skips
        // that waste: non-clipping parents go straight to full layout.
        bool RelayoutBubbleToStableAncestor(Element elem, System.Func<Element, ComputedStyle> styleOf, LayoutContext ctx) {
            var parentElem = FormattingParentElement(elem);
            if (parentElem == null) return false;
            var parentBox = FindBoxFor(lastRoot, parentElem);
            if (parentBox == null) return false;
            if (!Weva.Layout.Scrolling.ScrollContainerLookup.HasNonVisibleOverflow(parentBox)) return false;
            return CanSubtreeRelayout(parentElem, styleOf)
                && RelayoutOneSubtree(parentElem, styleOf, ctx);
        }

        // The element owning the nearest enclosing box up the parent chain
        // (skips anonymous boxes, which have no Element). Used to walk the
        // relayout bubble one formatting context at a time.
        Element FormattingParentElement(Element elem) {
            var box = FindBoxFor(lastRoot, elem);
            if (box == null) return null;
            for (var p = box.Parent; p != null; p = p.Parent) {
                if (p.Element != null) return p.Element;
            }
            return null;
        }

        static bool HasStableExternalAllocation(BlockBox box) {
            return box != null && (box.GridStretchedWidth || box.GridStretchedHeight);
        }

        static void PreserveStableAllocation(BlockBox oldBlock, BlockBox newBlock) {
            if (oldBlock == null || newBlock == null) return;
            if (oldBlock.GridStretchedWidth) {
                newBlock.Width = oldBlock.Width;
                newBlock.GridStretchedWidth = true;
            }
            if (oldBlock.GridStretchedHeight) {
                newBlock.Height = oldBlock.Height;
                newBlock.GridStretchedHeight = true;
            }
        }

        void StampFreshSubtree(Box box, Box parent) {
            if (box == null) return;
            for (int i = 0; i < box.Children.Count; i++) {
                var child = box.Children[i];
                child.Parent = box;
                StampFreshSubtree(child, box);
            }

            if (box.Element == null || !IsCacheable(box)) {
                box.Version = StableAnonymousVersion(box);
                return;
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

            cacheMisses++;
            box.Version = BoxVersion.Next();
            cache[box.Element] = new LayoutCacheEntry(key, box, box.Version);
        }

        static bool SameOuterGeometry(BlockBox a, BlockBox b) {
            if (a == null || b == null) return false;
            // STRICT (sub-pixel) equality, NOT the half-pixel tolerance used for
            // "would this round to the same pixel". A half-pixel tolerance is
            // unsafe here because a CONTINUOUS animation changes a box's outer
            // size by <0.5px PER FRAME, so every frame passes the tolerance and
            // the splice keeps the box's siblings frozen — yet the change
            // ACCUMULATES (16px→25px over a second) and the siblings never move.
            // That froze the rows below a font-size-animating flex item
            // (layout-stress counters: "bottom rows almost never move"). Requiring
            // genuinely-unchanged outer geometry sends any real propagating change
            // to the full-layout path (correct, smooth) while contained
            // animations — clipped bars, fixed-height padding cards — have ZERO
            // outer-geometry delta and still take the fast splice.
            return Strict(a.Width, b.Width)
                && Strict(a.Height, b.Height)
                && Strict(a.MarginTop, b.MarginTop)
                && Strict(a.MarginRight, b.MarginRight)
                && Strict(a.MarginBottom, b.MarginBottom)
                && Strict(a.MarginLeft, b.MarginLeft);

            static bool Strict(double x, double y) =>
                System.Math.Abs(x - y) <= LayoutEpsilons.SubPixelEqual;
        }

        // NearlyEqual: half-pixel equality — `<= LayoutEpsilons.HalfPixelEqual`.
        // Use when comparing rasterization-relevant values where sub-half-
        // pixel differences would not produce visibly distinct output.
        //
        // Two values within ±0.5 round to the same rasterised pixel, so
        // skipping the incremental re-layout is visually undetectable.
        // Counterpart to NearlySame in LayoutEngine.cs (SubPixelEqual /
        // strict sub-px) — that one answers "did we produce the same
        // number", which is a stricter question. The two methods share a
        // signature deliberately; do NOT consolidate.
        static bool NearlyEqual(double a, double b) {
            return System.Math.Abs(a - b) <= LayoutEpsilons.HalfPixelEqual;
        }

        static int IndexInParent(Box parent, Box child) {
            for (int i = 0; i < parent.Children.Count; i++) {
                if (ReferenceEquals(parent.Children[i], child)) return i;
            }
            return -1;
        }

        static Box FindBoxFor(Box root, Element target) {
            if (root.Element == target) return root;
            for (int i = 0; i < root.Children.Count; i++) {
                var f = FindBoxFor(root.Children[i], target);
                if (f != null) return f;
            }
            return null;
        }

        // Subtree-scoped FlexLayout / GridLayout walkers. Identical to the
        // engine's RunFlexPasses / RunGridPasses helpers in shape; replicated
        // here to keep LayoutEngine.cs unchanged. Walks deepest-first so
        // nested flex/grid containers settle before their ancestors.
        static void VisitFlexInScope(Box box, FlexLayout flex) {
            for (int i = 0; i < box.Children.Count; i++) {
                VisitFlexInScope(box.Children[i], flex);
            }
            if (box is FlexBox fb) flex.Layout(fb);
        }

        static void VisitGridInScope(Box box, GridLayout grid) {
            for (int i = 0; i < box.Children.Count; i++) {
                VisitGridInScope(box.Children[i], grid);
            }
            if (box is GridBox gb) grid.Layout(gb);
        }

        static void VisitPostGridFlexInScope(Box box, FlexLayout flex) {
            for (int i = 0; i < box.Children.Count; i++) {
                VisitPostGridFlexInScope(box.Children[i], flex);
            }
            if (box is FlexBox fb && HasDirectInFlowGridChild(fb)) {
                flex.Layout(fb);
            }
        }

        public void ResetSubtreeSkipStats() {
            subtreeSkipHits = 0;
        }
    }
}
