using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Reactive;

namespace Weva.Layout {
    // Scroll-boundary content reuse (LayoutEngine.EnableScrollBoundaryReuse).
    //
    // Problem this solves: a genuinely-propagating animation (a font-size change
    // on an auto-height flex item) correctly forces a FULL layout every frame —
    // and that full layout re-runs block/flex/grid over the ENTIRE document,
    // including a large scrollable region (e.g. a 96-cell grid inside an
    // overflow-y:auto wrapper) whose contents did not change. That re-layout is
    // pure waste: a scroll container is a natural layout boundary — its content
    // is laid out at intrinsic size and clipped/scrolled, so the height it is
    // GIVEN never reflows what is inside it, and its child boxes are positioned
    // parent-relative so the container can be moved/resized without touching
    // them. The ONLY input that can invalidate the contents is the container's
    // own content-box WIDTH (line wrapping / track sizing) or a change inside
    // the subtree.
    //
    // Strategy (optimistic + self-correcting):
    //   1. After the fresh box tree is built (BEFORE block/flex/grid), graft the
    //      PRIOR frame's laid-out subtree onto each scroll-container box whose
    //      element subtree has NO tracker-dirty node, and flag it ReuseContent.
    //      BlockLayout.LayoutContent + AnalyzeLayoutFeatures (see Box.ReuseContent)
    //      then skip the subtree, so the expensive passes never touch it.
    //   2. After the full pipeline has assigned the container's final geometry,
    //      VALIDATE that its content-box width matches the width the grafted
    //      children were laid out for. If it does (the common case — the change
    //      was elsewhere and the container's width is unchanged), we are done. If
    //      not (rare: a width-affecting change actually reached the container),
    //      RE-LAY that subtree fresh in place — so the result is always identical
    //      to a no-reuse full layout.
    //
    // Gated behind EnableScrollBoundaryReuse + only when a tracker and a prior
    // tree exist and the viewport is unchanged.
    public sealed partial class LayoutEngine {
        InvalidationTracker scrollReuseTracker;
        // Set by Layout() BEFORE BumpContextVersionIfChanged syncs lastViewport*,
        // so the reuse pre-pass can tell a viewport resize happened this pass
        // (ViewportChanged() reads false after the bump). Reuse is disabled on a
        // viewport change — see the gate sites.
        bool scrollReusePassViewportChanged;
        readonly List<(BlockBox container, double priorContentWidth, double priorContentHeight, bool heightSensitive)> scrollReuseGrafted = new();
        readonly List<(int nodeId, int savedFirstChild)> scrollReuseSevered = new();

        // BUILD-TIME skip: before BuildBoxTreeFromSnapshot, sever the snapshot
        // FirstChild link of every reuse-eligible scroll container so the build
        // never constructs its (about-to-be-grafted) subtree — eliminating the
        // wasteful per-frame rebuild of a large unchanged scrollable region.
        // Eligibility mirrors the post-build graft (a cached prior box with
        // children + a clean element subtree), so a severed container is always
        // one ApplyScrollBoundaryReuse will fill. The links are restored
        // immediately after the build. Snapshot path only.
        void PrepareScrollBoundaryReuseSever(Weva.Compiled.DomSnapshot snap, LayoutContext ctx) {
            scrollReuseSevered.Clear();
            if (!EnableScrollBoundaryReuse) return;
            if (scrollReuseTracker == null || lastRoot == null || snap == null) return;
            if (scrollReusePassViewportChanged) return;
            var map = snap.NodeToIdMap;
            if (map == null) return;
            ScrollReuseSeverWalk(lastRoot, snap, map);
        }

        void ScrollReuseSeverWalk(Box box, Weva.Compiled.DomSnapshot snap, System.Collections.Generic.IReadOnlyDictionary<Node, int> map) {
            if (box is BlockBox bb && bb.Element != null && bb.ChildList.Count > 0
                && HasScrollableOverflow(bb.Style)
                && cache.TryGetValue(bb.Element, out var entry)
                && entry.BoxResult is BlockBox prior && prior.ChildList.Count > 0
                && !SubtreeHasDirtyLayout(bb.Element)
                && map.TryGetValue(bb.Element, out int nodeId)
                && nodeId >= 0 && nodeId < snap.FirstChild.Length) {
                scrollReuseSevered.Add((nodeId, snap.FirstChild[nodeId]));
                snap.FirstChild[nodeId] = -1; // build sees no children → skips subtree
                return; // do not descend into a severed (nested) subtree
            }
            var kids = box.ChildList;
            for (int i = 0; i < kids.Count; i++) ScrollReuseSeverWalk(kids[i], snap, map);
        }

        void RestoreScrollBoundaryReuseSever(Weva.Compiled.DomSnapshot snap) {
            for (int i = 0; i < scrollReuseSevered.Count; i++) {
                var (nodeId, saved) = scrollReuseSevered[i];
                snap.FirstChild[nodeId] = saved;
            }
            scrollReuseSevered.Clear();
        }

        // Diagnostics: how many scroll containers were grafted on the last pass,
        // and how many of those required a width-mismatch correction.
        internal int LastScrollReuseGraftCount { get; private set; }
        internal int LastScrollReuseCorrectCount { get; private set; }

        // Called from the tracker-aware Layout right after the fresh tree is
        // built. Walks the tree and grafts reusable scroll-container subtrees.
        void ApplyScrollBoundaryReuse(Box root, LayoutContext ctx, System.Func<Element, ComputedStyle> styleOf) {
            scrollReuseGrafted.Clear();
            LastScrollReuseGraftCount = 0;
            LastScrollReuseCorrectCount = 0;
            if (!EnableScrollBoundaryReuse) return;
            if (scrollReuseTracker == null || lastRoot == null) return;
            if (scrollReusePassViewportChanged) return;
            ScrollReuseWalk(root, styleOf);
            LastScrollReuseGraftCount = scrollReuseGrafted.Count;
        }

        void ScrollReuseWalk(Box box, System.Func<Element, ComputedStyle> styleOf) {
            // A scroll container with an Element + a clean subtree + an available
            // prior laid-out box is a graft candidate. On a successful graft we do
            // NOT recurse (its contents are frozen for this pass).
            if (box is BlockBox bb && bb.Element != null && bb.Parent != null
                && HasScrollableOverflow(bb.Style)
                && TryGraftScrollSubtree(bb, styleOf)) {
                return;
            }
            var kids = box.ChildList;
            for (int i = 0; i < kids.Count; i++) ScrollReuseWalk(kids[i], styleOf);
        }

        bool TryGraftScrollSubtree(BlockBox fresh, System.Func<Element, ComputedStyle> styleOf) {
            var el = fresh.Element;
            if (!cache.TryGetValue(el, out var entry)) return false;
            if (!(entry.BoxResult is BlockBox prior) || ReferenceEquals(prior, fresh)) return false;
            if (prior.ChildList.Count == 0) return false;
            // The container subtree must be clean: a dirty descendant means its
            // own layout could differ, so it must not be reused.
            if (SubtreeHasDirtyLayout(el)) return false;
            // Detach the freshly-built children (they stay in boxPool.allocated[]
            // with no surviving parent → EndPass recycles them) and adopt the
            // prior laid-out children. Their positions are parent-relative and
            // were computed at prior.ContentWidth, so they remain valid as long
            // as `fresh` ends up the same content width (validated post-layout).
            fresh.ClearChildren();
            var priorKids = prior.ChildList;
            for (int i = 0; i < priorKids.Count; i++) {
                var c = priorKids[i];
                c.Parent = fresh;
                fresh.AddChild(c);
            }
            fresh.ContainsInlines = prior.ContainsInlines;
            fresh.ReuseContent = true;
            // LY5: "content is independent of the height it is given" holds
            // for auto-sized content but NOT for %-height descendants (they
            // resolve against the container's content-box height). Record
            // whether the subtree contains any, so validation can check the
            // height too when it matters. (vh/vmin/vmax resolve against the
            // viewport, and reuse is already disabled on viewport changes.)
            scrollReuseGrafted.Add((fresh, prior.ContentWidth, prior.ContentHeight,
                SubtreeHasHeightRelativeContent(el, styleOf)));
            return true;
        }

        // True if any descendant element's height/min-height/max-height/
        // flex-basis uses a percentage — the unit class whose resolved value
        // depends on the height the scroll container is GIVEN.
        static bool SubtreeHasHeightRelativeContent(Element el, System.Func<Element, ComputedStyle> styleOf) {
            var kids = el.Children;
            for (int i = 0; i < kids.Count; i++) {
                if (!(kids[i] is Element c)) continue;
                var st = styleOf(c);
                if (st != null
                    && (ValueUsesPercent(st.Get("height"))
                        || ValueUsesPercent(st.Get("min-height"))
                        || ValueUsesPercent(st.Get("max-height"))
                        || ValueUsesPercent(st.Get("flex-basis")))) {
                    return true;
                }
                if (SubtreeHasHeightRelativeContent(c, styleOf)) return true;
            }
            return false;
        }

        static bool ValueUsesPercent(string v) => v != null && v.IndexOf('%') >= 0;

        // True if any tracker dirty node carrying a Layout/Structure/Style mark is
        // `el` itself or a descendant of `el`. Such a change means the subtree's
        // own layout could differ, so it must not be reused.
        bool SubtreeHasDirtyLayout(Element el) {
            const InvalidationKind affecting =
                InvalidationKind.Layout | InvalidationKind.Structure | InvalidationKind.Style | InvalidationKind.PseudoClassState;
            foreach (var kv in scrollReuseTracker.DirtyEntries) {
                if ((kv.Value & affecting) == 0) continue;
                for (Node c = kv.Key; c != null; c = c.Parent) {
                    if (ReferenceEquals(c, el)) return true;
                }
            }
            return false;
        }

        // Post-pipeline validation. For each grafted container, confirm its final
        // content-box width equals the width the grafted children were laid out
        // for. On mismatch, re-lay that subtree fresh so the output matches a
        // no-reuse full layout exactly. Returns true if any correction ran (the
        // caller may want to re-run scroll/positioning sweeps).
        bool ValidateScrollBoundaryReuse(LayoutContext ctx, System.Func<Element, ComputedStyle> styleOf) {
            if (scrollReuseGrafted.Count == 0) return false;
            bool corrected = false;
            for (int i = 0; i < scrollReuseGrafted.Count; i++) {
                var (container, priorWidth, priorHeight, heightSensitive) = scrollReuseGrafted[i];
                bool widthOk = System.Math.Abs(container.ContentWidth - priorWidth) <= LayoutEpsilons.SubPixelEqual;
                // LY5: %-height descendants resolved against the height the
                // container HAD when the grafted content was laid out — a
                // height change at constant width silently froze them before.
                bool heightOk = !heightSensitive
                    || System.Math.Abs(container.ContentHeight - priorHeight) <= LayoutEpsilons.SubPixelEqual;
                if (widthOk && heightOk) {
                    continue; // grafted layout is valid
                }
                RelayoutScrollContentFresh(container, styleOf, ctx);
                LastScrollReuseCorrectCount++;
                corrected = true;
            }
            scrollReuseGrafted.Clear();
            return corrected;
        }

        // Rare path: the container's width DID change, so the grafted (prior)
        // children are stale. Rebuild the element's content and lay it out at the
        // container's now-final width, keeping the container's own frame.
        void RelayoutScrollContentFresh(BlockBox container, System.Func<Element, ComputedStyle> styleOf, LayoutContext ctx) {
            container.ReuseContent = false;
            // The grafted children belong to the prior tree; just detach them
            // (do NOT recycle — the prior tree still owns them until it is
            // replaced as lastRoot at the end of this pass).
            container.ClearChildren();

            var builder = GetOrCreatePooledBoxBuilder(styleOf);
            var st = styleOf(container.Element);
            if (st == null) return;
            if (builder.Build(container.Element, st) is BlockBox rebuilt) {
                var kids = rebuilt.ChildList;
                while (kids.Count > 0) {
                    var c = kids[0];
                    rebuilt.RemoveChildAt(0);
                    c.Parent = container;
                    container.AddChild(c);
                }
                container.ContainsInlines = rebuilt.ContainsInlines;
                boxPool.Recycle(rebuilt);
            }

            inlineLayout.Reset(ctx);
            blockLayout.Reset(ctx);
            flexLayout.Reset(ctx);
            gridLayout.Reset(ctx);
            // LY6: run positioning over the corrected subtree — the full-tower
            // shape, scoped. Freshly-built boxes default Position=Static, so
            // without this abs/rel/sticky content inside a width-corrected
            // scroll subtree laid out as plain in-flow blocks (taking flow
            // space, never positioned/compressed) — and stayed that way every
            // frame the width kept animating. Stamp first (so the in-flow
            // pass sees the right Position values), lay out, then the scoped
            // stamp/compress/apply, flex/grid restoration (the apply's
            // shrink-to-fit is destructive), and the pinOnly re-pin — same
            // sequence the full tower runs.
            // Same containment boundary as the subtree splice (see
            // LayoutContext.HeightPropagationBoundary): the corrected
            // container's OUTER geometry is owned by the full pass that just
            // ran — internal pre-flex → post-flex deltas must not leak into
            // the (already-correct) ancestor chain.
            Weva.Layout.Positioning.PositioningPass.Stamp(container);
            ctx.HeightPropagationBoundary = container;
            try {
                blockLayout.RelayoutContentAt(container, container.Width);
                VisitFlexInScope(container, flexLayout);
                VisitGridInScope(container, gridLayout);
                positioningPass.RunScopedForCorrection(container, ctx);
                VisitFlexInScope(container, flexLayout);
                VisitGridInScope(container, gridLayout);
                VisitPostGridFlexInScope(container, flexLayout);
                positioningPass.RepositionAbsolutes(container, ctx, pinOnly: true);
            } finally {
                ctx.HeightPropagationBoundary = null;
            }
        }
    }
}
