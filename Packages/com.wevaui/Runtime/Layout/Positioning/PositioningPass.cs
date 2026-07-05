// PositioningPass — applies CSS `position` semantics after BlockLayout / FlexLayout.
//
// Dispatch site: LayoutEngine.Layout calls PositioningPass.Run(root, ctx) AFTER
// BlockLayout.LayoutRoot and the flex post-pass. Box X/Y/Width/Height set by
// BlockLayout/FlexLayout are in coordinates LOCAL to each box's direct parent in
// the box tree. We preserve that convention: for `relative` the offsets become a
// delta on top of the existing local X/Y; for `absolute` and `fixed` we resolve
// against the containing block in absolute (root-relative) coordinates and then
// subtract the direct parent's absolute origin so the rewritten X/Y stays local.
//
// v1 simplifications:
//   - `sticky` no longer aliases `relative`. PositioningPass leaves the natural
//     in-flow X/Y untouched; Weva.Layout.Scrolling.StickyResolver runs after
//     ScrollLayout and writes Box.StickyOffsetX/Y, which the paint converter
//     applies as a translation at emit time.
//   - The containing block for absolute is the nearest positioned ancestor's
//     padding-box (per CSS Positioned Layout L3 §4.3 — ContainingBlockResolver
//     subtracts the ancestor's border widths from the border-box rect).
//   - Absolute box auto sizing with both top/bottom (or left/right) pinned uses
//     the containing block's full extent minus the offsets; we don't iterate to
//     reconcile with intrinsic sizes.
//   - Absolute boxes do NOT get their interior re-flowed when stretched by both
//     pinned edges; their children stay sized at the BlockLayout-computed width.
//   - Per the modern spec, `position: fixed/sticky` always creates a stacking
//     context (z-index aside). For `relative/absolute`, a stacking context is
//     created only when z-index is an integer (not auto).

using System;
using Weva.Layout.AnchorPositioning;
using Weva.Layout.Boxes;

namespace Weva.Layout.Positioning {
    public sealed class PositioningPass {
        // L2: monotone per-layout generation stamp for the shrink-to-fit
        // intrinsic cache (BlockBox.ShrinkFitCached{Max,Min}Content). Bumped
        // once at the top of every LayoutEngine.Layout call so intrinsics
        // cached in one layout never satisfy a hit in the next (content may
        // have changed). Static is safe: layout is single-threaded, and a
        // cross-engine generation bump only ever causes an extra recompute
        // (a missed cache hit), never a stale read. Resets to 0 on domain
        // reload, which is correct (every box's stamp starts at -1).
        internal static long LayoutGeneration;
        internal static void BumpLayoutGeneration() => LayoutGeneration++;

        // Wired by LayoutEngine right after constructing the pass. Used only
        // by the absolute/fixed shrink-to-fit branch in ApplyAbsoluteAgainst
        // (CSS Positioned Layout L3 §10.3.7: width:auto with at most one
        // horizontal pin → shrink-to-fit). Null is tolerated; the shrink path
        // silently no-ops, preserving pre-patch behaviour.
        BlockLayout block;
        internal void SetBlockLayout(BlockLayout block) { this.block = block; }

        // Count of absolute/fixed boxes seen by the last full Run. The
        // incremental relayout path gates its whole-tree RepositionAbsolutes
        // walk on this: a document with zero out-of-flow boxes has nothing to
        // reposition, so the O(tree) walk is pure overhead (it was ~86% of a
        // warm flip's cost on a 1500-box OOF-free tree). Stays valid across
        // incremental flips because a structure change forces a full layout
        // (which re-runs this), and non-structural flips can't add/remove
        // positioned boxes.
        int outOfFlowCount;
        public int LastOutOfFlowCount => outOfFlowCount;
        // Count of sticky boxes seen by the last full Run. Lets the incremental
        // path skip the whole-tree sticky resolve when there are none (sticky
        // positions don't change on a geometry-stable flip anyway).
        int stickyCount;
        public int LastStickyCount => stickyCount;

        public void Run(Box root, LayoutContext ctx) {
            if (root == null || ctx == null) return;
            // Anchor positioning (CSS 2024): build the registry before reading
            // offsets so anchor() function values can resolve immediately.
            ctx.Anchors.Clear();
            CollectAnchors(root, ctx.Anchors);
            // Phase 1: stamp Position + ZIndex on every box (no cb required).
            // Required before CompressOutOfFlow walks the tree to pull in-flow
            // siblings up over removed absolute/fixed boxes.
            StampPositionTypes(root);
            outOfFlowCount = 0;
            stickyCount = 0;
            CompressOutOfFlow(root, ctx);
            // Phase 2: combined populate-and-apply in pre-order. Reading a box's
            // offsets BEFORE its positioned ancestor's Apply has finalized W/H
            // would resolve `top: 35%` against the ancestor's pre-Apply height
            // (which for absolute ancestors with `inset: 0` is the BlockLayout
            // intrinsic, not the post-pin filled extent). Pre-order guarantees
            // that when we descend into children, their containing-block
            // ancestor has already had its W/H pinned by ApplyAbsoluteAgainst.
            PopulateAndApply(root, ctx);
            // Anchor positioning v2 — `position-try-fallbacks`. Walks the tree
            // again and re-positions any anchored box that overflows the
            // viewport using the listed flip strategies. Additive: a no-op when
            // no box declares the property.
            ApplyTryFallbacks(root, ctx);
        }

        // LY6: scoped variant of Run for the scroll-graft width-correction
        // path — the same stamp/compress/apply sequence, but it leaves the
        // anchor registry (populated by the full pass over the whole tree)
        // intact, and doesn't reset the sticky/OOF counters (the corrected
        // subtree's boxes were already counted during the full Run via the
        // graft's count-only walk; the gates only test > 0, so the overcount
        // is harmless).
        public void RunScopedForCorrection(Box root, LayoutContext ctx) {
            if (root == null || ctx == null) return;
            StampPositionTypes(root);
            CompressOutOfFlow(root, ctx);
            PopulateAndApply(root, ctx);
        }

        // Re-resolves abs/fixed positions only, against current parent/CB
        // dimensions. Called by LayoutEngine after the second/third flex+grid
        // passes have finalized container sizes — abs-pos children whose CB is
        // a flex-stretched container (e.g. `.minimap-frame { flex:1 }` housing
        // `.dot { position:absolute; top:50% }`) resolved their percent offsets
        // against the pre-stretch height during the initial Run, so without
        // this fixup their final position is computed from a stale basis.
        //
        // Safe to call multiple times: skips CompressOutOfFlow (which is
        // accumulative on Y shifts) and ApplyRelative (which adds offsets onto
        // the current X/Y). For abs/fixed boxes, ApplyAbsoluteAgainst writes
        // box.X/Y as `absX - parentAbsX` — a clean assignment that produces the
        // same value when re-run against unchanged input, and the correct value
        // when the CB has grown.
        // pinOnly: re-resolve abs/fixed POSITION (and inset-pinned width/height)
        // against the current containing block WITHOUT re-running the shrink-to-
        // fit content relayout (RelayoutContentAt). The destructive shrink-to-fit
        // probe re-stacks a flex/grid abs container's children as block flow; a
        // restoration flex/grid pass normally repairs that, so re-running it in a
        // LATER reposition (with no restoration after) re-breaks those containers
        // (canonical: `.play-action{position:absolute} > .play-btn{display:flex}`).
        // The trailing re-pin LayoutEngine runs after the restoration only needs
        // to fix inset-pinned boxes disturbed by the restoration (combat-hud
        // bar-label inset:0 overlay, map .atlas inset:0 panel) — those take the
        // pinned branch and never relayout content, so pinOnly is safe + correct.
        public void RepositionAbsolutes(Box root, LayoutContext ctx, bool pinOnly = false) {
            if (root == null || ctx == null) return;
            VisitReposition(root, ctx, pinOnly);
        }

        void VisitReposition(Box box, LayoutContext ctx, bool pinOnly) {
            if (box is Weva.Layout.Boxes.BlockBox) {
                if (box.Position == PositionType.Absolute) {
                    PopulateOffsets(box, ctx);
                    ApplyAbsolute(box, ctx, pinOnly);
                } else if (box.Position == PositionType.Fixed) {
                    PopulateOffsets(box, ctx);
                    ApplyFixed(box, ctx, pinOnly);
                }
            }
            // LY3: same graft boundary as PopulateAndApply — an abs box inside
            // frozen content keeps its parent-relative position, and the
            // non-pinOnly shrink branch would run RelayoutContentAt over
            // content no restoration pass will repair.
            if (box.ReuseContent) return;
            for (int i = 0; i < box.Children.Count; i++) VisitReposition(box.Children[i], ctx, pinOnly);
        }

        static void StampPositionTypes(Box box) {
            // CSS Positioned Layout L3: only block-level boxes (BlockBox and its
            // subclasses) can participate in positioned layout. Anonymous inline
            // content (TextRun, LineBox) inherits its parent element's ComputedStyle
            // — including any `position` value — but CSS does not allow text runs
            // or line boxes to be positioned. Stamping `position: absolute` from a
            // parent's style onto a TextRun causes PositioningPass to treat the run
            // as an abs-pos box and overwrite its X/Y/Width/Height with the
            // containing-block geometry (canonical bug: `.abs { position:absolute;
            // inset:0; display:flex } X` — the TextRun "X" gets Width=54, X=-23).
            if (box is Weva.Layout.Boxes.BlockBox) {
                box.Position = box.ReadPositionType();
                box.ZIndex = box.ReadZIndex();
            }
            // else: TextRun / LineBox / InlineBox — keep Position=Static (default).
            for (int i = 0; i < box.Children.Count; i++) StampPositionTypes(box.Children[i]);
        }

        // Public entry so LayoutEngine can stamp Position before BlockLayout
        // runs. Without this, the first BlockLayout sees `box.Position ==
        // Static` on every abs-pos box (the field isn't initialised until
        // PositioningPass.Run, which runs AFTER block/flex/grid). The
        // in-flow height computation then includes abs-pos descendants —
        // e.g. an `::before { position:absolute }` decoration adds its
        // line-height to the parent <li>, doubling the LI's measured
        // height. The second-pass flex/grid run does see the post-stamp
        // values, but flex item cross sizes are already locked from
        // pass 1; the symptom persists as too-tall items.
        public static void Stamp(Box root) {
            StampPositionTypes(root);
        }

        void PopulateAndApply(Box box, LayoutContext ctx) {
            // Only BlockBox instances participate in CSS positioned layout.
            // TextRun / LineBox / InlineBox are never positioned elements and
            // have Position=Static after StampPositionTypes; skip them to
            // avoid any residual Style mismatch triggering a spurious apply.
            if (box is Weva.Layout.Boxes.BlockBox) {
                PopulateOffsets(box, ctx);
                switch (box.Position) {
                    case PositionType.Relative:
                        ApplyRelative(box);
                        break;
                    case PositionType.Sticky:
                        stickyCount++;
                        break;
                    case PositionType.Absolute:
                        outOfFlowCount++;
                        ApplyAbsolute(box, ctx);
                        break;
                    case PositionType.Fixed:
                        outOfFlowCount++;
                        ApplyFixed(box, ctx);
                        break;
                }
            }
            // LY3: don't descend into frozen graft content. Its children keep
            // last frame's post-apply parent-relative values (correct — the
            // container's own outer geometry is re-assigned by its parent
            // each pass): ApplyRelative is `X += dx` (accumulative) and the
            // abs shrink-to-fit branch can destroy the frozen line layout.
            // Do count the frozen subtree's positioned boxes, so the
            // incremental path's LastStickyCount/LastOutOfFlowCount gates
            // stay truthful.
            if (box.ReuseContent) {
                for (int i = 0; i < box.Children.Count; i++) CountPositionedOnly(box.Children[i]);
                return;
            }
            for (int i = 0; i < box.Children.Count; i++) PopulateAndApply(box.Children[i], ctx);
        }

        void CountPositionedOnly(Box box) {
            if (box is Weva.Layout.Boxes.BlockBox) {
                switch (box.Position) {
                    case PositionType.Sticky: stickyCount++; break;
                    case PositionType.Absolute:
                    case PositionType.Fixed: outOfFlowCount++; break;
                }
            }
            for (int i = 0; i < box.Children.Count; i++) CountPositionedOnly(box.Children[i]);
        }

        static void PopulateOffsets(Box box, LayoutContext ctx) {
            double cbW, cbH;
            switch (box.Position) {
                case PositionType.Fixed: {
                    cbW = ctx.ViewportWidthPx;
                    cbH = ctx.ViewportHeightPx;
                    break;
                }
                case PositionType.Absolute: {
                    var cb = ContainingBlockResolver.ResolveAbsolute(box, ctx);
                    cbW = cb.Width;
                    cbH = cb.Height;
                    break;
                }
                case PositionType.Relative:
                case PositionType.Sticky: {
                    // CSS 2.1 §10.6 / Positioned Layout L3 §10.3: containing
                    // block for relative/sticky is the parent's content area,
                    // so percentage offsets resolve against content-box dims.
                    var p = box.Parent;
                    if (p != null) {
                        cbW = p.Width - p.PaddingLeft - p.PaddingRight - p.BorderLeft - p.BorderRight;
                        cbH = p.Height - p.PaddingTop - p.PaddingBottom - p.BorderTop - p.BorderBottom;
                    } else {
                        cbW = ctx.ViewportWidthPx;
                        cbH = ctx.ViewportHeightPx;
                    }
                    break;
                }
                default: {
                    cbW = box.Parent != null ? box.Parent.Width : ctx.ViewportWidthPx;
                    cbH = box.Parent != null ? box.Parent.Height : ctx.ViewportHeightPx;
                    break;
                }
            }

            if (box.Position == PositionType.Static) {
                box.OffsetTop = null;
                box.OffsetRight = null;
                box.OffsetBottom = null;
                box.OffsetLeft = null;
            } else {
                var off = box.ReadOffsets(ctx, cbW, cbH);
                box.OffsetTop = off.Top;
                box.OffsetRight = off.Right;
                box.OffsetBottom = off.Bottom;
                box.OffsetLeft = off.Left;
            }
        }

        static void ApplyTryFallbacks(Box box, LayoutContext ctx) {
            if (box.Style != null
                && (box.Position == PositionType.Absolute || box.Position == PositionType.Fixed)) {
                string raw = box.Style.Get("position-try-fallbacks");
                if (!string.IsNullOrEmpty(raw) && raw != "none") {
                    var list = AnchorPositioning.PositionTryFallbacks.Parse(raw);
                    AnchorPositioning.PositionTryFallbacks.Apply(box, ctx, list);
                }
            }
            for (int i = 0; i < box.Children.Count; i++) ApplyTryFallbacks(box.Children[i], ctx);
        }

        static void CollectAnchors(Box box, AnchorRegistry registry) {
            if (box?.Style != null) {
                string name = box.Style.Get("anchor-name");
                if (!string.IsNullOrEmpty(name) && name.Trim() != "none") {
                    foreach (var n in SplitNames(name)) {
                        // Accept either dashed ("--tip") or stripped ("tip")
                        // forms — the registry normalises both to a common
                        // key so resolution finds the anchor regardless of
                        // upstream tokenisation.
                        if (!string.IsNullOrEmpty(n) && n != "none") {
                            registry.Register(n, box);
                        }
                    }
                }
            }
            for (int i = 0; i < box.Children.Count; i++) CollectAnchors(box.Children[i], registry);
        }

        static System.Collections.Generic.IEnumerable<string> SplitNames(string raw) {
            int start = 0;
            for (int i = 0; i < raw.Length; i++) {
                if (raw[i] == ',') {
                    var seg = raw.Substring(start, i - start).Trim();
                    if (seg.Length > 0) yield return seg;
                    start = i + 1;
                }
            }
            if (start < raw.Length) {
                var tail = raw.Substring(start).Trim();
                if (tail.Length > 0) yield return tail;
            }
        }

        // BlockLayout doesn't yet know about `position`, so absolute and
        // fixed children still contributed flow space when their parent was sized.
        // Walk every parent whose children stack vertically (block flow) and shift
        // later non-out-of-flow siblings up by each removed child's margin-box
        // height. Flex containers are NOT touched here — FlexLayout now skips
        // out-of-flow children at item-collect time, so the in-flow items never
        // get a slot in the first place; walking the flex container and applying
        // a Y-axis shift would WRONGLY move flex children whose positions were
        // already correctly computed without the out-of-flow box. Grid containers
        // are also excluded; out-of-flow inside grid is a v1 simplification.
        static void CompressOutOfFlow(Box root, LayoutContext ctx) {
            VisitCompress(root, ctx);
        }

        static void VisitCompress(Box box, LayoutContext ctx) {
            // LY3: a scroll-grafted subtree (Box.ReuseContent) carries last
            // frame's ALREADY-compressed content verbatim. CompressOutOfFlow
            // is accumulative — it subtracts each OOF child's margin-box from
            // following siblings' Y (and the auto-height parent) every run —
            // so re-walking a frozen subtree shifts its content again on
            // every full layout (content creeps per frame under a
            // propagating animation). BlockLayout/AnalyzeLayoutFeatures
            // already honour the freeze; positioning must too.
            if (box.ReuseContent) return;
            for (int i = 0; i < box.Children.Count; i++) VisitCompress(box.Children[i], ctx);
            if (!IsBlockFlowContainer(box)) return;

            double accumulatedShift = 0;
            for (int i = 0; i < box.Children.Count; i++) {
                var c = box.Children[i];
                if (IsOutOfFlow(c)) {
                    // The space this child took in flow.
                    double removed = c.MarginTop + c.Height + c.MarginBottom;
                    accumulatedShift += removed;
                } else if (IsFloat(c)) {
                    // CSS 2.1 §9.5 floats were placed by BlockLayout's
                    // PlaceFloat against the parent BFC's float context;
                    // their Y is the absolute placement Y, NOT a cumulative
                    // in-flow cursor that should be retro-compressed by
                    // earlier OOF siblings. Leave the float's Y untouched
                    // and don't let it accumulate shift (it didn't take
                    // in-flow space to remove). Without this guard, an
                    // abs-pos sibling that precedes a left float would
                    // pull the float up by the abs-pos margin-box, which
                    // for the canonical `<aside abs-pos> + <img float:left>`
                    // pattern visibly displaces the float off the page.
                    continue;
                } else if (accumulatedShift > 0) {
                    c.Y -= accumulatedShift;
                }
            }

            if (accumulatedShift > 0 && box is BlockBox bb && HasAutoHeightForOutOfFlowCompression(bb)) {
                double minHeight = bb.PaddingTop + bb.PaddingBottom + bb.BorderTop + bb.BorderBottom;
                // Out-of-flow children don't contribute to their container's
                // content height, so we subtract the in-flow space BlockLayout
                // provisionally gave them — but the container's declared
                // min-height is still a hard floor. Without re-applying it here,
                // a box sized by min-height (e.g. min-height:100vh) with only
                // absolutely-positioned children collapsed to (min − oofExtent),
                // pulling a bottom-anchored child up off the bottom edge.
                // CSS Box Sizing L3 §5.
                if (bb.Style != null) {
                    string minRaw = bb.Style.Get("min-height");
                    if (!string.IsNullOrEmpty(minRaw) && minRaw != "auto" && minRaw != "0") {
                        double fs = StyleResolver.FontSizePx(bb.Style, bb.Parent?.Style, ctx);
                        var minR = StyleResolver.ResolveLength(minRaw, bb.Style, ctx, fs, null);
                        if (minR.Kind == StyleResolver.LengthKind.Length) {
                            double minPx = IsBorderBox(bb) ? minR.Pixels : minR.Pixels + minHeight;
                            if (minPx > minHeight) minHeight = minPx;
                        }
                    }
                }
                double h = bb.Height - accumulatedShift;
                bb.Height = h > minHeight ? h : minHeight;
            }
        }

        static bool IsOutOfFlow(Box box) {
            return box.Position == PositionType.Absolute || box.Position == PositionType.Fixed;
        }

        static bool HasAutoHeightForOutOfFlowCompression(BlockBox box) {
            if (box?.Style == null) return false;
            string raw = box.Style.Get(Weva.Css.Cascade.CssProperties.HeightId);
            return string.IsNullOrEmpty(raw) || raw == "auto";
        }

        static bool IsFloat(Box box) {
            return box is BlockBox bb && bb.IsFloat;
        }

        static bool IsBlockFlowContainer(Box box) {
            // Flex and grid containers handle their own out-of-flow exclusion at
            // collect time, so we deliberately exclude them here.
            if (box is Flex.FlexBox) return false;
            if (box is Grid.GridBox) return false;
            if (box is BlockBox bb && !bb.ContainsInlines) return true;
            return false;
        }

        // Sticky leaves the natural in-flow X/Y untouched; the sticky-relative
        // offset is computed by StickyResolver and applied at paint time via
        // Box.StickyOffsetX/Y. This keeps the scroll-aware adjustment out of
        // the layout's reactive surface (re-laying out is unnecessary when
        // scroll changes; only paint and sticky-resolve need to re-run).

        static void ApplyRelative(Box box) {
            double dx = 0, dy = 0;
            if (box.OffsetLeft.HasValue) dx = box.OffsetLeft.Value;
            else if (box.OffsetRight.HasValue) dx = -box.OffsetRight.Value;
            if (box.OffsetTop.HasValue) dy = box.OffsetTop.Value;
            else if (box.OffsetBottom.HasValue) dy = -box.OffsetBottom.Value;
            box.X += dx;
            box.Y += dy;
        }

        void ApplyAbsolute(Box box, LayoutContext ctx, bool pinOnly = false) {
            var cb = ContainingBlockResolver.ResolveAbsolute(box, ctx);
            ApplyAbsoluteAgainst(box, cb, block, ctx, pinOnly);
        }

        void ApplyFixed(Box box, LayoutContext ctx, bool pinOnly = false) {
            var cb = ContainingBlockResolver.ResolveFixed(box, ctx);
            ApplyAbsoluteAgainst(box, cb, block, ctx, pinOnly);
        }

        static void ApplyAbsoluteAgainst(Box box, ContainingBlockResolver.ContainingBlock cb, BlockLayout block, LayoutContext ctx, bool pinOnly = false) {
            // Width/height resolution when both edges are pinned.
            bool horizPinned = box.OffsetLeft.HasValue && box.OffsetRight.HasValue;
            bool vertPinned = box.OffsetTop.HasValue && box.OffsetBottom.HasValue;
            double marginLeft = box is BlockBox mb ? mb.MarginLeft : 0;
            double marginRight = box is BlockBox mb2 ? mb2.MarginRight : 0;
            double marginTop = box is BlockBox mb3 ? mb3.MarginTop : 0;
            double marginBottom = box is BlockBox mb4 ? mb4.MarginBottom : 0;

            if (horizPinned) {
                double w = cb.Width - box.OffsetLeft.Value - box.OffsetRight.Value - marginLeft - marginRight;
                if (w < 0) w = 0;
                if (!HasExplicitDim(box, "width")) {
                    bool widthChanged = System.Math.Abs(box.Width - w) > LayoutEpsilons.HalfPixelEqual;
                    if (widthChanged)
                        box.Version = Weva.Layout.Incremental.BoxVersion.Next();
                    box.Width = w;
                    // Symmetric counterpart of the vertical pin guard below:
                    // tells FlexLayout (when this box is a column-flex
                    // container whose cross axis is width) not to collapse
                    // the pinned width to intrinsic on the second pass.
                    if (box is BlockBox hBlock) hBlock.GridStretchedWidth = true;
                    // CSS2 §10.3.7: the children were laid out by BlockLayout
                    // at the PROVISIONAL width (the CB's content width) before
                    // this pass derived the pinned width. Without a content
                    // relayout they keep the wider measure — canonical
                    // nook-dialogue `.dialog { left:30px; right:30px }`: the
                    // box pinned to 1374 but its `p.say` stayed at 1330
                    // (= 1434 CB − 104 padding), overflowing the dialog and
                    // diverging 60px from Chrome. Plain blocks only:
                    // flex/grid containers re-run their own pass after
                    // RepositionAbsolutes at the pinned width (that is what
                    // GridStretchedWidth above is for), and a block-flow
                    // relayout would re-stack their children destructively
                    // (the pinOnly contract in RepositionAbsolutes' header).
                    if (widthChanged && !pinOnly && block != null
                        && box is BlockBox pinnedBlock
                        && !(pinnedBlock is Weva.Layout.Flex.FlexBox)
                        && !(pinnedBlock is Weva.Layout.Grid.GridBox)) {
                        block.RelayoutContentAt(pinnedBlock, w);
                    }
                }
            } else if (!pinOnly
                       && !HasExplicitDim(box, "width")
                       && box is BlockBox bb
                       && block != null) {
                // Flex/grid containers used to be excluded from this shrink-
                // to-fit branch, but that left abs-pos flex/grid items at
                // their pre-positioning BlockLayout width (typically the
                // containing block's full width). Canonical map.html case:
                // `.marker { position:absolute; display:flex; top:28%;
                // left:42% }` ended up 1434 wide, and `transform:translate(
                // -50%, -50%)` then shifted it 717 px off-screen. MaxContentWidth
                // already has flex/grid branches that walk their intrinsic
                // size correctly, and the trailing RelayoutContentAt is a
                // no-op for flex/grid (their own pass re-runs after this).
                // CSS Positioned Layout L3 §10.3.7: width:auto with at most
                // one horizontal pin → shrink-to-fit width =
                //   min(max-content, max(min-content, available)).
                //
                // `available` per §10.3.7 is the containing-block width minus
                // the resolved values of margin-left + margin-right + left +
                // right (with auto offsets treated as 0). Reading `cb.Width`
                // verbatim — as the code used to — over-counts the available
                // space whenever the box is offset toward one edge (e.g.
                // `left: 50%`), but since maxContent typically clamps the
                // result this only mattered for content that ran past the
                // CB edge. The spec-correct value also makes the upper-bound
                // clamp (`if (fitted > avail) fitted = avail`) behave
                // correctly when a pinned-edge offset is large.
                double marginX = bb.MarginLeft + bb.MarginRight;
                double leftOff = bb.OffsetLeft.GetValueOrDefault();
                double rightOff = bb.OffsetRight.GetValueOrDefault();
                double avail = cb.Width - marginX - leftOff - rightOff;
                if (avail < 0) avail = 0;
                // The RelayoutContentAt probes below re-lay this box's content
                // as BLOCK FLOW (BlockLayout's content pass). For a flex/grid
                // container that re-stacks its children vertically, inflating
                // box.Height to the sum-of-children height (e.g. the combat-HUD
                // ability bar: six 72px hex slots collapse to a single 72px row
                // via FlexLayout, but the probe re-stacks them to 432). The
                // bottom/right-edge pin math further down reads box.Height /
                // box.Width, so a probe-inflated height would mis-place a
                // `bottom:`-anchored flex container (it floated to Y≈344 instead
                // of 704). The Flex/GridLayout pass owns this container's
                // cross-size and re-runs after RepositionAbsolutes, so capture
                // the real pre-probe height here and restore it before the pin
                // math. Plain block boxes legitimately derive height from the
                // relaid content, so they are left untouched.
                bool isFlexGridContainer =
                    bb is Weva.Layout.Flex.FlexBox || bb is Weva.Layout.Grid.GridBox;
                double preProbeHeight = bb.Height;
                if (System.Math.Abs(bb.ShrinkFitCachedAvail - avail) < LayoutEpsilons.HalfPixelEqual && bb.ShrinkFitCachedWidth >= 0) {
                    // Cache-hit fast path. The cache lets us skip the
                    // max-content / min-content probes (two extra
                    // RelayoutContentAt calls below). But we MUST still
                    // re-lay the inline content at the cached width.
                    // Between the previous shrink-to-fit and now,
                    // BlockLayout.ApplyBoxModel re-ran LayoutContent for
                    // this box at `avail` (= the containing block's content
                    // width, not the shrunk width). The TextRun's X was
                    // then recomputed against contentW=avail; for a
                    // centered text-align it sits at `(avail - textW) / 2`
                    // — way off the right edge once we shrink the box.
                    // Canonical case: a `.skill-slot-key` pill (a
                    // 13.42-wide pill containing "E") rendered the "E"
                    // at runX≈15 inside the pill, visibly floating past
                    // the pill's right edge. Only the text shifted because
                    // ApplyAbsoluteAgainst correctly placed the pill via
                    // Offsets+Width. RelayoutContentAt at the cached width
                    // re-runs inline layout so runX matches the shrunk
                    // content box. Always re-lay; cheap and correct beats
                    // a fragile "did Width change" check (the box's Width
                    // can match the cache yet its inline children's
                    // positions can still reflect an earlier wider layout
                    // — e.g., the BoxBuilder fresh-box path zeroes Width
                    // but seeds it to `avail` again via ApplyBoxModel).
                    block.RelayoutContentAt(bb, bb.ShrinkFitCachedWidth);
                } else {
                    // border-box = intrinsic content width + frame on BOTH
                    // sides (PaddingLeft + PaddingRight + BorderLeft +
                    // BorderRight). The previous version only added the
                    // right-side frame, which under-reports the box width
                    // by exactly PaddingLeft + BorderLeft — visibly wrong
                    // for any abs-pos box with non-zero left padding
                    // (canonical case: match3 `.combo-banner` with
                    // `padding: 4px 12px`, where the missing 12px of
                    // PaddingLeft compounded with a downstream issue and
                    // collapsed the box to ~19px instead of its
                    // max-content ~92px width).
                    double frame = bb.PaddingLeft + bb.PaddingRight + bb.BorderLeft + bb.BorderRight;
                    double maxContent, minContent;
                    double fsShrink = StyleResolver.FontSizePx(bb.Style, bb.Parent?.Style, ctx);
                    if (bb is Weva.Layout.Flex.FlexBox || bb is Weva.Layout.Grid.GridBox) {
                        // MaxContentWidth/MinContentWidth ALREADY fold in the
                        // horizontal frame for flex/grid (they derive the
                        // intrinsic from items + HorizontalFrame), so adding
                        // `frame` again double-counts padding — an abs-pos flex
                        // bubble (story-bubble's `.thought`: padding 12px 16px,
                        // 3 dots) came out 32px too wide, leaving dead space
                        // after the dots. Also: RelayoutContentAt(box, 1e6) on a
                        // flex/grid container is destructive (it re-flows and
                        // wrecks the children) — the item-based intrinsic needs
                        // no probe at all.
                        maxContent = MaxContentWidth(bb, ctx, fsShrink);
                        minContent = MinContentWidth(bb, null, ctx, fsShrink);
                    } else if (bb.ShrinkFitIntrinsicGeneration == LayoutGeneration
                               && bb.ShrinkFitCachedMaxContent >= 0) {
                        // L2: intrinsics already computed for this box THIS
                        // layout (a prior pass probed it). max/min-content are
                        // avail-independent, so reuse them and skip the two
                        // destructive RelayoutContentAt probes — only `fitted`
                        // and the final relayout below depend on `avail`.
                        maxContent = bb.ShrinkFitCachedMaxContent;
                        minContent = bb.ShrinkFitCachedMinContent;
                    } else {
                        block.RelayoutContentAt(bb, 1e6);
                        maxContent = MaxContentWidth(bb, ctx, fsShrink) + frame;
                        block.RelayoutContentAt(bb, 1);
                        minContent = MaxContentWidth(bb, ctx, fsShrink) + frame;
                        bb.ShrinkFitCachedMaxContent = maxContent;
                        bb.ShrinkFitCachedMinContent = minContent;
                        bb.ShrinkFitIntrinsicGeneration = LayoutGeneration;
                    }
                    if (maxContent < frame) maxContent = frame;
                    if (minContent < frame) minContent = frame;
                    // CSS Sizing L3 §5.1: fit-content(<arg>) caps the available
                    // space at the argument rather than the CB's full avail.
                    // For plain auto shrink-to-fit, effectiveAvail == avail.
                    double effectiveAvail = avail;
                    if (bb.Style != null) {
                        var wParsed = bb.Style.GetParsed(Weva.Css.Cascade.CssProperties.WidthId);
                        if (wParsed is Weva.Css.Values.CssFunctionCall wfn && wfn.Name == "fit-content") {
                            double fs2 = StyleResolver.FontSizePx(bb.Style, bb.Parent?.Style, ctx);
                            var argR = StyleResolver.ResolveLengthFromParsed(wParsed, ctx, fs2, avail);
                            if (argR.Kind == StyleResolver.LengthKind.FitContent) {
                                effectiveAvail = argR.Pixels;
                            }
                        }
                    }
                    double fitted = System.Math.Min(maxContent, System.Math.Max(minContent, effectiveAvail));
                    if (fitted > avail) fitted = avail;
                    // CSS Sizing L3: clamp shrink-to-fit by min-width / max-width.
                    // Without this, `.hud-top { min-width: 280px }` ended up at
                    // ~218 px (its max-content) in map.html. Honor only definite
                    // pixel/percent values; auto / keyword values are no-ops.
                    if (bb.Style != null) {
                        double fs = StyleResolver.FontSizePx(bb.Style, bb.Parent?.Style, ctx);
                        var minR = StyleResolver.ResolveLength(bb.Style.Get("min-width"), bb.Style, ctx, fs, cb.Width);
                        var maxR = StyleResolver.ResolveLength(bb.Style.Get("max-width"), bb.Style, ctx, fs, cb.Width);
                        if (minR.Kind == StyleResolver.LengthKind.Length && fitted < minR.Pixels) fitted = minR.Pixels;
                        else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                            double mp = cb.Width * minR.Percent * 0.01;
                            if (fitted < mp) fitted = mp;
                        }
                        if (maxR.Kind == StyleResolver.LengthKind.Length && fitted > maxR.Pixels) fitted = maxR.Pixels;
                        else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                            double mp = cb.Width * maxR.Percent * 0.01;
                            if (fitted > mp) fitted = mp;
                        }
                    }
                    block.RelayoutContentAt(bb, fitted);
                    bb.ShrinkFitCachedAvail = avail;
                    bb.ShrinkFitCachedWidth = fitted;
                }
                // Restore the flex/grid cross-size clobbered by the block-flow
                // relayout probes above (see preProbeHeight comment).
                if (isFlexGridContainer) bb.Height = preProbeHeight;
            }
            // E7: when the CB is a grid-area (CSS Grid L1 §9) rather than the
            // grid container's padding edge, BlockLayout's ApplyBoxModel
            // already wrote `width: <percent>` against the WRONG basis (the
            // parent grid container's content width). Re-resolve the explicit
            // percent width against cb.Width — but only for boxes flagged
            // with a grid-area CB to avoid re-touching the existing OOF
            // width pipeline. Symmetric height re-resolution lives below and
            // already handles all OOF percent-height cases.
            if (box.Style != null
                && box is BlockBox bbGridW
                && bbGridW.HasGridAreaContainingBlock
                && cb.Width > 0
                && !horizPinned) {
                string wRaw = box.Style.Get("width");
                if (!string.IsNullOrEmpty(wRaw) && wRaw != "auto") {
                    double fs = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
                    var r = StyleResolver.ResolveLength(wRaw, box.Style, ctx, fs, cb.Width);
                    if (r.Kind == StyleResolver.LengthKind.Length) {
                        double width = r.Pixels;
                        if (!IsBorderBox(box)) {
                            width += box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
                        }
                        box.Width = width < 0 ? 0 : width;
                    }
                }
            }
            // Resolve any explicit height value against the containing block.
            // BlockLayout deferred percent resolution for OOF boxes (it doesn't
            // know cb.Height top-down). For an OOF box with `height: 60%`
            // (e.g. .cd-shade), this is the first chance to compute it
            // correctly. Runs even when vertPinned because explicit `height`
            // takes precedence over the pin-derived size per CSS spec.
            bool didResolveExplicitHeight = false;
            if (box.Style != null && cb.Height > 0 && box.IntrinsicHeight <= 0) {
                string raw = box.Style.Get("height");
                if (!string.IsNullOrEmpty(raw) && raw != "auto") {
                    double fs = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
                    var r = StyleResolver.ResolveLength(raw, box.Style, ctx, fs, cb.Height);
                    if (r.Kind == StyleResolver.LengthKind.Length) {
                        double height = r.Pixels;
                        if (!IsBorderBox(box)) {
                            height += box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
                        }
                        box.Height = height < 0 ? 0 : height;
                        didResolveExplicitHeight = true;
                    }
                }
            }
            if (vertPinned && !didResolveExplicitHeight) {
                double h = cb.Height - box.OffsetTop.Value - box.OffsetBottom.Value - marginTop - marginBottom;
                if (h < 0) h = 0;
                if (!HasExplicitDim(box, "height")) {
                    // CSS §10.6.7 case (e): height:auto with both vertical pins
                    // → stretch the box to fill the space between the two pinned
                    // edges (h = CB.height − top − bottom − margins).
                    //
                    // EXCEPTION — CSS Sizing L3 / fit-content: when the cascaded
                    // `height` value is a shrink-to-fit keyword (fit-content,
                    // min-content, max-content), the box must NOT stretch to fill
                    // the inset space. Instead it sizes to its content (already
                    // computed by BlockLayout), capped at the available inset
                    // space.  Canonical case: the UA stylesheet gives `<dialog>`
                    // both `top:0; bottom:0` (for vertical auto-margin centering
                    // when height is explicit) AND `height:fit-content` (so that
                    // an author-positioned dialog without an explicit height
                    // shrinks to its content). Without this guard the engine
                    // would override BlockLayout's ~86px content height with
                    // cb.Height − top − bottom = 520px (CSS2 §10.6.7 FIXED-
                    // DIALOG-HEIGHT regression; snippet 25).
                    if (IsHeightShrinkToFit(box)) {
                        // Content height is already in box.Height from BlockLayout.
                        // Clamp to the inset-derived available space so it never
                        // overflows the gap between the pinned edges.
                        if (box.Height > h) {
                            if (System.Math.Abs(box.Height - h) > LayoutEpsilons.HalfPixelEqual)
                                box.Version = Weva.Layout.Incremental.BoxVersion.Next();
                            box.Height = h;
                        }
                        // No GridStretchedHeight: height is content-derived,
                        // not externally forced by the inset pins.
                    } else {
                        // height:auto — stretch to fill between the pins.
                        if (System.Math.Abs(box.Height - h) > LayoutEpsilons.HalfPixelEqual)
                            box.Version = Weva.Layout.Incremental.BoxVersion.Next();
                        box.Height = h;
                        // Tell the next FlexLayout pass not to collapse this height
                        // back to its intrinsic content cross-size. Without this
                        // flag, `position: absolute; inset: 0; display: flex` on a
                        // pseudo-element (canonical case:
                        // `.tile::after { inset: 0; display: flex; align-items:
                        // center }`) would have its pinned Height (= cb.Height
                        // here) overwritten by FlexLayout.FinalizeContainerCrossSize
                        // on the second pass — the symptom is a emoji rendering
                        // at the top of the tile instead of centred. Reusing the
                        // GridStretchedHeight flag piggybacks on FlexLayout's
                        // existing guard for externally-allocated cross sizes; the
                        // semantic is identical ("an outer system has decided this
                        // cross-axis size; don't shrink to content").
                        if (box is BlockBox vBlock) vBlock.GridStretchedHeight = true;
                    }
                }
            }

            // CSS Position 3 §10.3.7 / §10.6.4: when an absolutely positioned
            // box has BOTH inline-axis edges pinned, a definite width, and
            // `margin-left: auto` AND `margin-right: auto`, the leftover slack
            // is split evenly between the two margins — centering the box
            // horizontally inside the containing block. Same rule on the
            // block axis (top/bottom + height + vertical margins auto). This
            // is the mechanism the `<dialog>` UA stylesheet uses to centre
            // its modal: `position: absolute; inset: 0; margin: auto`.
            //
            // BlockLayout resolved `auto` margins to 0 when computing
            // box.MarginLeft/Right, so we have to re-inspect the raw style
            // values to detect the auto sentinel. The auto-margin shift is
            // applied ONLY when both margins on that axis are auto AND the
            // box is fully pinned on that axis AND the relevant dimension is
            // a definite length (excluding `auto`/`fit-content`/`min-content`/
            // `max-content`, which per the spec leave the auto margins as 0).
            double extraMarginLeft = 0;
            double extraMarginTop = 0;
            if (box.Style != null) {
                string mlRaw = box.Style.Get("margin-left");
                string mrRaw = box.Style.Get("margin-right");
                bool mlAuto = string.Equals(mlRaw, "auto", System.StringComparison.OrdinalIgnoreCase);
                bool mrAuto = string.Equals(mrRaw, "auto", System.StringComparison.OrdinalIgnoreCase);
                if (horizPinned && mlAuto && mrAuto && IsDefiniteSize(box, "width", ctx, cb.Width)) {
                    double slack = cb.Width - box.OffsetLeft.Value - box.OffsetRight.Value - marginLeft - marginRight - box.Width;
                    if (slack > 0) {
                        extraMarginLeft = slack * 0.5;
                    }
                }
                string mtRaw = box.Style.Get("margin-top");
                string mbRaw = box.Style.Get("margin-bottom");
                bool mtAuto = string.Equals(mtRaw, "auto", System.StringComparison.OrdinalIgnoreCase);
                bool mbAuto = string.Equals(mbRaw, "auto", System.StringComparison.OrdinalIgnoreCase);
                if (vertPinned && mtAuto && mbAuto && IsDefiniteSize(box, "height", ctx, cb.Height)) {
                    double slack = cb.Height - box.OffsetTop.Value - box.OffsetBottom.Value - marginTop - marginBottom - box.Height;
                    if (slack > 0) {
                        extraMarginTop = slack * 0.5;
                    }
                }
            }

            // CSS Flexbox §4.1 / CSS Position §6.3: the STATIC position of an
            // absolutely-positioned child of a FLEX container is its
            // hypothetical position as if it were the sole flex item — i.e.
            // honouring the container's justify-content (main axis) and
            // align-items/align-self (cross axis). The default per-axis static
            // position fallback below uses box.X/box.Y, which FlexLayout left
            // at the content origin (0,0) because it skips out-of-flow
            // children. Canonical map.html `.player { display:flex;
            // justify-content:center; align-items:center } > .player-fov
            // { position:absolute }` — the 80×80 glow sat at the container's
            // top-left and rendered offset down-right of the centred icon
            // instead of behind it. Compute the flex-aligned static offsets so
            // the auto-inset branches below place the box correctly.
            double flexStaticX = box.X, flexStaticY = box.Y;
            bool flexStaticResolved = false;
            if ((!box.OffsetLeft.HasValue && !box.OffsetRight.HasValue)
                || (!box.OffsetTop.HasValue && !box.OffsetBottom.HasValue)) {
                flexStaticResolved = TryFlexStaticPosition(box, ref flexStaticX, ref flexStaticY);
            }

            // Compute desired absolute origin.
            double absX, absY;
            if (box.OffsetLeft.HasValue) {
                absX = cb.X + box.OffsetLeft.Value + marginLeft + extraMarginLeft;
            } else if (box.OffsetRight.HasValue) {
                absX = cb.X + cb.Width - box.OffsetRight.Value - marginRight - box.Width;
            } else {
                // Static-position fallback: where the box would have been laid
                // out by its parent in flow (flex-aligned when the parent is a
                // flex container, otherwise the in-flow X).
                var (parentAbsX0, _) = ContainingBlockResolver.AbsolutePositionOfParent(box);
                absX = parentAbsX0 + (flexStaticResolved ? flexStaticX : box.X);
            }
            if (box.OffsetTop.HasValue) {
                absY = cb.Y + box.OffsetTop.Value + marginTop + extraMarginTop;
            } else if (box.OffsetBottom.HasValue) {
                absY = cb.Y + cb.Height - box.OffsetBottom.Value - marginBottom - box.Height;
            } else {
                var (_, parentAbsY0) = ContainingBlockResolver.AbsolutePositionOfParent(box);
                absY = parentAbsY0 + (flexStaticResolved ? flexStaticY : box.Y);
            }

            // Convert to local-to-parent.
            var (parentAbsX, parentAbsY) = ContainingBlockResolver.AbsolutePositionOfParent(box);
            box.X = absX - parentAbsX;
            box.Y = absY - parentAbsY;
        }

        // CSS Flexbox §4.1: the static position of an out-of-flow child of a
        // flex container is computed as if the child were a flex item, with
        // justify-content distributing it on the main axis and align-self /
        // align-items on the cross axis. Returns the content-box-relative
        // (X, Y) the child would occupy; only meaningful for axes whose inset
        // is auto (the caller gates per-axis). Returns false when the box's
        // PARENT is not a flex container, leaving the in-flow fallback.
        static bool TryFlexStaticPosition(Box box, ref double staticX, ref double staticY) {
            if (!(box.Parent is Weva.Layout.Flex.FlexBox flex)) return false;
            var fs = flex.Style;
            if (fs == null) return false;

            string dir = fs.Get(Weva.Css.Cascade.CssProperties.FlexDirectionId);
            bool column = dir == "column" || dir == "column-reverse";
            bool reverse = dir == "row-reverse" || dir == "column-reverse";

            // Content box of the flex container (padding/border excluded).
            double contentLeft = flex.PaddingLeft + flex.BorderLeft;
            double contentTop = flex.PaddingTop + flex.BorderTop;
            double contentW = System.Math.Max(0, flex.Width - flex.PaddingLeft - flex.PaddingRight - flex.BorderLeft - flex.BorderRight);
            double contentH = System.Math.Max(0, flex.Height - flex.PaddingTop - flex.PaddingBottom - flex.BorderTop - flex.BorderBottom);

            double mainSize = column ? contentH : contentW;
            double crossSize = column ? contentW : contentH;
            double childMain = column ? box.Height : box.Width;
            double childCross = column ? box.Width : box.Height;

            // Main axis: justify-content (initial `normal`/`flex-start`/`start`
            // → start; `center` → centred; `flex-end`/`end` → end). The
            // distribution keywords (space-between/around/evenly) place a sole
            // item at the start per spec.
            string justify = fs.Get(Weva.Css.Cascade.CssProperties.JustifyContentId);
            double mainStart = FlexAlignStart(justify, mainSize, childMain, isJustify: true, reverse: reverse);

            // Cross axis: align-self (falling back to the container's
            // align-items). `stretch`/`normal`/`flex-start`/`start`/`baseline`
            // → start; `center` → centred; `flex-end`/`end` → end.
            string alignSelf = box.Style?.Get(Weva.Css.Cascade.CssProperties.AlignSelfId);
            string align = (string.IsNullOrEmpty(alignSelf) || alignSelf == "auto")
                ? fs.Get(Weva.Css.Cascade.CssProperties.AlignItemsId)
                : alignSelf;
            double crossStart = FlexAlignStart(align, crossSize, childCross, isJustify: false, reverse: false);

            if (column) {
                staticX = contentLeft + crossStart;
                staticY = contentTop + mainStart;
            } else {
                staticX = contentLeft + mainStart;
                staticY = contentTop + crossStart;
            }
            return true;
        }

        static double FlexAlignStart(string keyword, double containerSize, double childSize, bool isJustify, bool reverse) {
            double free = containerSize - childSize;
            string k = keyword;
            if (string.IsNullOrEmpty(k)) k = "normal";
            switch (k) {
                case "center":
                    return free * 0.5;
                case "end":
                case "flex-end":
                    return reverse ? 0 : free;
                case "right":
                    return isJustify ? free : free * 0.5;
                case "left":
                    return isJustify ? 0 : free * 0.5;
                case "start":
                case "flex-start":
                case "normal":
                case "stretch":
                case "baseline":
                case "space-between":
                case "space-around":
                case "space-evenly":
                default:
                    return reverse ? free : 0;
            }
        }

        // Returns the natural intrinsic content width for shrink-to-fit
        // (CSS Sizing 3 — max-content). At a wide probe relayout, no line
        // wraps so each LineBox.Width equals the natural advance of all its
        // text fragments. Block descendants' Width is bogus (they filled the
        // probe), but their inline children's LineBox widths are honest.
        // Inline-block atoms have their final shrink-to-fit width already.
        // Out-of-flow descendants don't constrain the ancestor's box.
        // CSS Sizing L4 §5: when `contain-intrinsic-width` (or the width component
        // of `contain-intrinsic-size`) is set, the contained box contributes that
        // value as its content-width instead of zero.  ctx/fontSize are needed to
        // resolve em-relative lengths; when omitted the contain-intrinsic hint
        // falls back to 0 (same behaviour as before this feature landed).
        internal static double MaxContentWidth(BlockBox box, LayoutContext ctx = null, double fontSize = 0) {
            // CSS Containment L2 §3.3 / L3 inline-size: under inline-size
            // containment the element's intrinsic inline-size contributions
            // (min-content / max-content) are computed as if it were empty —
            // only padding + border count; content contributes zero (or the
            // contain-intrinsic-width hint when specified — CSS Sizing L4 §5).
            //
            // Callers have two conventions for what this function returns:
            //   • For PLAIN BLOCK boxes: MaxContentWidth returns the CONTENT
            //     width only; the caller is responsible for adding the frame
            //     (PaddingLeft+Right+BorderLeft+Right) to reach border-box.
            //   • For FLEX/GRID boxes: MaxContentWidth already returns the
            //     full BORDER-BOX width (frame included); the caller does NOT
            //     add it again.
            // We must respect this split so callers don't double-count.
            if (box.Style != null && Weva.Layout.Containment.ContainmentResolver.HasInlineSize(box.Style)) {
                // Flex/grid: return just the frame (border-box with zero content),
                // plus any contain-intrinsic-width hint.
                if (box is Weva.Layout.Flex.FlexBox || box is Weva.Layout.Grid.GridBox) {
                    double intrinsicW = (ctx != null)
                        ? Weva.Layout.Containment.ContainmentResolver.ResolveContainIntrinsicWidthPx(box.Style, ctx, fontSize)
                        : 0;
                    return ClampByOwnMinMax(box, HorizontalFrame(box) + intrinsicW);
                }
                // Plain block: return contain-intrinsic-width as content (or 0);
                // caller adds frame.  min-width / max-width apply via ClampByOwnMinMax.
                if (ctx != null) {
                    return Weva.Layout.Containment.ContainmentResolver.ResolveContainIntrinsicWidthPx(box.Style, ctx, fontSize);
                }
                return 0;
            }
            // If `box` itself is a flex/grid container, derive intrinsic
            // inline size from items + frame instead of walking descendants
            // (which would only return MAX of items, not SUM for row flex).
            if (box is Weva.Layout.Flex.FlexBox bf) {
                return ClampByOwnMinMax(bf, FlexIntrinsicInline(bf) + HorizontalFrame(bf));
            }
            if (box is Weva.Layout.Grid.GridBox bg) {
                return ClampByOwnMinMax(bg, GridIntrinsicInline(bg) + HorizontalFrame(bg));
            }
            double max = 0;
            WalkContent(box, ref max);
            return max;
        }

        // Returns a conservative CSS min-content inline size. Text line boxes
        // are already split into word/space fragments by LineBreaker, so the
        // largest non-space fragment is the longest unbreakable contribution.
        // px-only gap reader for the min-content hint above. `column-gap`
        // wins over the `gap` shorthand's first component; non-px values
        // (%, em — need a LengthContext this static helper doesn't have)
        // contribute 0, which UNDER-floors and is therefore safe.
        static double ParsePxColumnGap(Weva.Css.Cascade.ComputedStyle style) {
            if (style == null) return 0;
            string raw = style.Get("column-gap");
            if (string.IsNullOrEmpty(raw)) {
                raw = style.Get("gap");
                if (!string.IsNullOrEmpty(raw)) {
                    int sp = raw.IndexOf(' ');
                    // `gap: <row> <column>` — the COLUMN gap is the second
                    // component when two are given; single value covers both.
                    if (sp > 0 && sp + 1 < raw.Length) raw = raw.Substring(sp + 1);
                }
            }
            if (string.IsNullOrEmpty(raw)) return 0;
            raw = raw.Trim();
            if (!raw.EndsWith("px")) return 0;
            return double.TryParse(raw.Substring(0, raw.Length - 2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        // `minTextMeasure` (optional): per-TextRun MIN-content measurer (longest
        // word). Callers with a LayoutContext (the grid fr-floor hint) supply
        // it; null keeps the legacy approximation (the run's laid-out width —
        // adequate for shrink-fit probes, OVER-floors fr tracks since an
        // unwrapped line equals max-content).
        internal static double MinContentWidth(BlockBox box, Func<TextRun, double> minTextMeasure = null, LayoutContext ctx = null, double fontSize = 0) {
            if (box == null) return 0;
            // CSS Containment L2 §3.3 / L3 inline-size: inline-size containment
            // makes the element's min-content width equal to its frame only (zero
            // content contribution), plus any contain-intrinsic-width hint when
            // provided — CSS Sizing L4 §5.  See MaxContentWidth for the full
            // rationale on the plain-block vs flex/grid caller convention split.
            if (box.Style != null && Weva.Layout.Containment.ContainmentResolver.HasInlineSize(box.Style)) {
                if (box is Weva.Layout.Flex.FlexBox || box is Weva.Layout.Grid.GridBox) {
                    double intrinsicW = (ctx != null)
                        ? Weva.Layout.Containment.ContainmentResolver.ResolveContainIntrinsicWidthPx(box.Style, ctx, fontSize)
                        : 0;
                    return ClampByOwnMinMax(box, HorizontalFrame(box) + intrinsicW);
                }
                // Plain block: return contain-intrinsic-width as content (or 0);
                // caller adds frame.
                if (ctx != null) {
                    return Weva.Layout.Containment.ContainmentResolver.ResolveContainIntrinsicWidthPx(box.Style, ctx, fontSize);
                }
                return 0;
            }
            if (box is Weva.Layout.Flex.FlexBox bfx) {
                string dir = bfx.Style?.Get(Weva.Css.Cascade.CssProperties.FlexDirectionId);
                bool column = dir == "column" || dir == "column-reverse";
                if (column) {
                    // Column flex: items stack on the block axis, so the
                    // container's min-content INLINE size is the widest item's
                    // own min-content — NOT the max-content fallback. The old
                    // fallback kept a nested column-flex item (e.g. a card's
                    // text column) from ever shrinking below its longest line,
                    // so its text never wrapped to the available width and the
                    // column overflowed. CSS Sizing 3 §5.1 / Flexbox §9.9.
                    double maxItem = 0;
                    for (int i = 0; i < bfx.Children.Count; i++) {
                        var c = bfx.Children[i];
                        if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                        if (c is BlockBox fcb && fcb.IsFloat) continue;
                        double inner;
                        if (c is BlockBox cb2) {
                            // An item with a DEFINITE inline size contributes that
                            // size (its content-based min-content is irrelevant —
                            // a fixed `width:200px` item floors the column at 200
                            // even when empty). Its laid-out border-box Width is
                            // the resolved definite width (explicit width wins over
                            // the column's align-stretch). Otherwise fall back to
                            // the content min-content (longest word for text).
                            if (HasDefiniteInlineSize(cb2)) {
                                inner = cb2.Width;
                            } else {
                                inner = MinContentWidth(cb2, minTextMeasure);
                                // MinContentWidth of a plain block is content-level;
                                // add its own frame to reach border-box. Flex/grid
                                // children already return border-box.
                                if (!(cb2 is Weva.Layout.Flex.FlexBox) && !(cb2 is Weva.Layout.Grid.GridBox))
                                    inner += cb2.PaddingLeft + cb2.PaddingRight + cb2.BorderLeft + cb2.BorderRight;
                            }
                            inner += cb2.MarginLeft + cb2.MarginRight;
                        } else {
                            double mx = 0;
                            WalkMinContent(c, ref mx, minTextMeasure);
                            inner = mx;
                        }
                        if (inner > maxItem) maxItem = inner;
                    }
                    return ClampByOwnMinMax(bfx, maxItem + HorizontalFrame(bfx));
                }
                // Row flex — CSS Flexbox s9.9.1 simplified. Single-line
                // (nowrap): the container's min-content inline size is the
                // SUM of the items' outer min-content contributions + gaps.
                // flex-wrap: wrap: lines can break between items, so the
                // true minimum is the LARGEST single contribution instead.
                // An item's contribution: a definite inline size wins;
                // otherwise its own min-content (plain blocks add their own
                // frame; nested flex/grid already return border-box) plus
                // margins. Per s9.9.1's clamp semantics `min-width: 0` does
                // NOT reduce a contribution (clamping only raises toward
                // min-width / caps at max-width) — Chrome verified on the
                // glass.html player card (fr-floor regression: art 168 +
                // gap 24 + "Midnight" word + frame ~ 305.6).
                // Simplifications (documented): px gaps only (relative gaps
                // contribute 0 — under-floors, never over-floors);
                // flex-basis ignored.
                {
                    bool wrap = false;
                    string fw = bfx.Style?.Get("flex-wrap");
                    if (fw == "wrap" || fw == "wrap-reverse") wrap = true;
                    double sum = 0, largest = 0;
                    int counted = 0;
                    for (int i = 0; i < bfx.Children.Count; i++) {
                        var c = bfx.Children[i];
                        if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                        if (c is BlockBox fcb2 && fcb2.IsFloat) continue;
                        double inner;
                        if (c is BlockBox cb3) {
                            if (HasDefiniteInlineSize(cb3)) {
                                inner = cb3.Width;
                            } else {
                                inner = MinContentWidth(cb3, minTextMeasure);
                                if (!(cb3 is Weva.Layout.Flex.FlexBox) && !(cb3 is Weva.Layout.Grid.GridBox))
                                    inner += cb3.PaddingLeft + cb3.PaddingRight + cb3.BorderLeft + cb3.BorderRight;
                            }
                            inner += cb3.MarginLeft + cb3.MarginRight;
                        } else {
                            double mx2 = 0;
                            WalkMinContent(c, ref mx2, minTextMeasure);
                            inner = mx2;
                        }
                        sum += inner;
                        if (inner > largest) largest = inner;
                        counted++;
                    }
                    double gapPx = ParsePxColumnGap(bfx.Style);
                    if (!wrap && counted > 1) sum += gapPx * (counted - 1);
                    double content = wrap ? largest : sum;
                    return ClampByOwnMinMax(bfx, content + HorizontalFrame(bfx));
                }
            }
            if (box is Weva.Layout.Grid.GridBox gbMin) {
                // CSS Grid L1 §5.2/§12: grid min-content is track-algorithmic.
                // Run the simplified min-content track sizing (available=0):
                // fixed tracks at their fixed size, auto/fr tracks at items'
                // min-content contributions. Falls back to MaxContentWidth when
                // the grid hasn't been laid out yet (ResolvedProperties absent).
                return GridMinContentWidth(gbMin, minTextMeasure);
            }
            double max = 0;
            WalkMinContent(box, ref max, minTextMeasure);
            return max;
        }

        static void WalkMinContent(Box node, ref double max, Func<TextRun, double> minTextMeasure = null) {
            for (int i = 0; i < node.Children.Count; i++) {
                var c = node.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                // CSS 2.1 §10.3.5 / CSS Sizing 3: floats are taken out of
                // normal flow for intrinsic-size purposes; the containing
                // block flows around them, so they don't contribute to its
                // min/max-content inline size.
                if (c is BlockBox fc && fc.IsFloat) continue;
                if (c is Weva.Layout.Boxes.LineBox lb) {
                    for (int j = 0; j < lb.Children.Count; j++) {
                        var r = lb.Children[j];
                        if (r is Weva.Layout.Boxes.InlineBox) continue;
                        if (IsAllWhitespaceTextRun(r)) continue;
                        double rw = (minTextMeasure != null && r is TextRun trMin)
                            ? minTextMeasure(trMin) : r.Width;
                        if (rw > max) max = rw;
                    }
                    continue;
                }
                if (c is BlockBox cbb && cbb.IsInlineBlock) {
                    if (cbb.Width > max) max = cbb.Width;
                    continue;
                }
                if (c is Weva.Layout.Flex.FlexBox fx) {
                    // Legacy callers (shrink-to-fit clamps) keep the
                    // max-content fallback they were calibrated against;
                    // the fr-floor path (measurer present) needs the REAL
                    // min-content or blocks containing row-flex children
                    // over-floor their grid tracks.
                    double w = minTextMeasure != null
                        ? MinContentWidth(fx, minTextMeasure)
                        : MaxContentWidth(fx);
                    if (w > max) max = w;
                    continue;
                }
                if (c is Weva.Layout.Grid.GridBox gx) {
                    // Use the real grid min-content when a measurer is provided
                    // (fr-floor path); fall back to max-content for legacy callers.
                    double w = minTextMeasure != null
                        ? GridMinContentWidth(gx, minTextMeasure)
                        : MaxContentWidth(gx);
                    if (w > max) max = w;
                    continue;
                }
                WalkMinContent(c, ref max, minTextMeasure);
            }
        }

        static bool IsAllWhitespaceTextRun(Box box) {
            if (box is not Weva.Layout.Boxes.TextRun tr) return false;
            string text = tr.Text;
            if (string.IsNullOrEmpty(text)) return true;
            for (int i = 0; i < text.Length; i++) {
                if (!char.IsWhiteSpace(text[i])) return false;
            }
            return true;
        }

        internal static void WalkContent(Box node, ref double max) {
            for (int i = 0; i < node.Children.Count; i++) {
                var c = node.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                // CSS 2.1 §10.3.5 / CSS Sizing 3: floats are taken out of
                // normal flow for intrinsic-size purposes; the containing
                // block flows around them, so they don't contribute to its
                // min/max-content inline size.
                if (c is BlockBox fc && fc.IsFloat) continue;
                if (c is Weva.Layout.Boxes.LineBox lb) {
                    // line.Width is post-text-align (OffsetLine adds the
                    // alignment dx onto it). Sum the raw fragment widths
                    // instead so max-content reflects the natural text
                    // advance only.
                    double sum = 0;
                    for (int j = 0; j < lb.Children.Count; j++) {
                        var r = lb.Children[j];
                        if (r is Weva.Layout.Boxes.InlineBox) continue;
                        sum += r.Width;
                    }
                    if (sum > max) max = sum;
                    continue;
                }
                if (c is BlockBox cbb && cbb.IsInlineBlock) {
                    if (cbb.Width > max) max = cbb.Width;
                    continue;
                }
                // Flex / grid containers: their Width filled the probe, but
                // their items' Widths are real (laid out by Flex/GridLayout
                // pass). Derive intrinsic inline size from items + frame.
                if (c is Weva.Layout.Flex.FlexBox fx) {
                    double w = FlexIntrinsicInline(fx) + HorizontalFrame(fx);
                    w = ClampByOwnMinMax(fx, w);
                    if (w > max) max = w;
                    continue;
                }
                if (c is Weva.Layout.Grid.GridBox gx) {
                    double w = GridIntrinsicInline(gx) + HorizontalFrame(gx);
                    w = ClampByOwnMinMax(gx, w);
                    if (w > max) max = w;
                    continue;
                }
                WalkContent(c, ref max);
            }
        }

        static double HorizontalFrame(BlockBox b) =>
            b.PaddingLeft + b.PaddingRight + b.BorderLeft + b.BorderRight;

        // True when the box has a DEFINITE inline (width) size — a length or
        // calc, i.e. not auto / percentage / an intrinsic keyword. Used by the
        // column-flex min-content path: a definite-width item contributes that
        // width regardless of its content. (Percentages are indefinite for
        // intrinsic sizing per CSS Sizing 3 §5.)
        static bool HasDefiniteInlineSize(BlockBox box) {
            string w = box.Style?.Get(Weva.Css.Cascade.CssProperties.WidthId);
            if (string.IsNullOrEmpty(w) || w == "auto") return false;
            if (w.EndsWith("%")) return false;
            if (w == "min-content" || w == "max-content" || w == "fit-content") return false;
            return true;
        }

        static double ClampByOwnMinMax(BlockBox box, double w) {
            if (box.Style == null) return w;
            double pxMin = TryReadPxLength(box.Style.Get(Weva.Css.Cascade.CssProperties.MinWidthId));
            double pxMax = TryReadPxLength(box.Style.Get(Weva.Css.Cascade.CssProperties.MaxWidthId));
            if (pxMin <= 0 && pxMax <= 0) return w;
            double frame = HorizontalFrame(box);
            var bsParsed = box.Style.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
            bool borderBox = false;
            if (bsParsed is Weva.Css.Values.CssKeyword bsK) borderBox = bsK.Identifier == "border-box";
            else if (bsParsed is Weva.Css.Values.CssIdentifier bsId) borderBox = bsId.Name == "border-box";
            else borderBox = box.Style.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
            if (pxMax > 0) {
                double maxOuter = borderBox ? pxMax : pxMax + frame;
                if (w > maxOuter) w = maxOuter;
            }
            if (pxMin > 0) {
                double minOuter = borderBox ? pxMin : pxMin + frame;
                if (w < minOuter) w = minOuter;
            }
            return w;
        }

        static double OuterWidth(Box b) {
            double m = (b is BlockBox bb) ? bb.MarginLeft + bb.MarginRight : 0;
            return m + b.Width;
        }

        // Minimal px-only length reader for FlexIntrinsicInline's min-width /
        // max-width clamp. Returns 0 when the value is missing, `auto`,
        // percent/em/rem (which need a containing-block / font-size that the
        // intrinsic-sizing probe doesn't have), or unparseable. A negative or
        // zero px value is treated as "no clamp".
        static double TryReadPxLength(string raw) {
            if (string.IsNullOrEmpty(raw)) return 0;
            string s = raw.Trim();
            if (s == "auto" || s == "none" || s == "0") return 0;
            if (s.EndsWith("px")) {
                if (double.TryParse(s.Substring(0, s.Length - 2),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0) {
                    return v;
                }
                return 0;
            }
            // Bare number → treat as unitless length (px-equivalent), which is
            // CSS-spec-illegal for length properties but the parser sometimes
            // surfaces this for the `0` shorthand.
            if (double.TryParse(s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double bare) && bare > 0) {
                return bare;
            }
            return 0;
        }

        static double FlexIntrinsicInline(Weva.Layout.Flex.FlexBox container) {
            bool isRow = true;
            double gap = 0;
            if (container.Style != null) {
                string dir = container.Style.Get("flex-direction");
                if (dir == "column" || dir == "column-reverse") isRow = false;
                string g = isRow ? container.Style.Get("column-gap") : container.Style.Get("row-gap");
                if (string.IsNullOrEmpty(g) || g == "normal") g = container.Style.Get("gap");
                if (!string.IsNullOrEmpty(g)) {
                    if (TryReadFirstCssNumber(g, out double parsedGap)) gap = parsedGap;
                }
            }
            double sum = 0, maxItem = 0; int n = 0;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                // Use intrinsic max-content rather than current Width: when an
                // outer probe (`RelayoutContentAt(parent, 1e6)`) inflates parent
                // contentW, this container's children get sized to that huge
                // value too unless they have explicit widths. Reading their
                // current Width then gives a probe-inflated answer (e.g. an
                // anonymous text child of a `<button class=btn-send>` flex
                // container ends up at the parent flex's contentW, making this
                // function return the entire parent main-size as the button's
                // intrinsic — which then flex-shrinks every sibling to 0).
                double w;
                if (c is BlockBox cbb) {
                    bool hasExplicitWidth = false;
                    if (cbb.Style != null) {
                        string wRaw = cbb.Style.Get("width");
                        hasExplicitWidth = !string.IsNullOrEmpty(wRaw) && wRaw != "auto";
                    }
                    if (hasExplicitWidth) {
                        w = cbb.MarginLeft + cbb.MarginRight + cbb.Width;
                    } else {
                        // Intrinsic content width + horizontal frame + margins.
                        double inner = MaxContentWidth(cbb);
                        double frame = cbb.PaddingLeft + cbb.PaddingRight + cbb.BorderLeft + cbb.BorderRight;
                        // MaxContentWidth for FlexBox/GridBox already includes
                        // their frame; for plain BlockBox it doesn't.
                        if (cbb is Weva.Layout.Flex.FlexBox || cbb is Weva.Layout.Grid.GridBox) {
                            w = cbb.MarginLeft + cbb.MarginRight + inner;
                        } else {
                            w = cbb.MarginLeft + cbb.MarginRight + inner + frame;
                        }
                    }
                    // Clamp to author-specified min-width / max-width. Without
                    // this, a flex item with `min-width:80px` whose text content
                    // is only 50px wide reports intrinsic = 50, and an outer
                    // grid auto-track sizing this container under-allocates by
                    // (80-50) per item. Canonical case: leaderboard.html
                    // `.stat { min-width:80px }` inside `.hero-stats(flex)`
                    // inside `.hero(grid 1fr auto)` — each stat ended up 75.5
                    // instead of 80, hero-stats track collapsed by ~116, hero-
                    // meta's 1fr absorbed the slack. The outer BlockLayout path
                    // already does this via min-width clamp in FinalizeBlockSize,
                    // but the *intrinsic* probe used by grid/flex track sizing
                    // bypassed it. Only px / unitless lengths are honoured here
                    // — percent / em / rem need a containing-block width that
                    // an intrinsic-sizing probe doesn't have.
                    if (cbb.Style != null) {
                        double pxMin = TryReadPxLength(cbb.Style.Get(Weva.Css.Cascade.CssProperties.MinWidthId));
                        double pxMax = TryReadPxLength(cbb.Style.Get(Weva.Css.Cascade.CssProperties.MaxWidthId));
                        double mFrame = cbb.PaddingLeft + cbb.PaddingRight + cbb.BorderLeft + cbb.BorderRight;
                        // Spec default for box-sizing is content-box; treat a
                        // null / unset slot as content-box so an author
                        // `min-width: 80px; padding: 6px 12px; border: 1px`
                        // (no box-sizing) resolves the outer floor to
                        // 80 + 26 = 106 (matching Chrome) instead of the
                        // border-box reading 80. The prior `!= "content-box"`
                        // test inverted the default.
                        var bsParsed = cbb.Style.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
                        bool borderBox = false;
                        if (bsParsed is Weva.Css.Values.CssKeyword bsK) borderBox = bsK.Identifier == "border-box";
                        else if (bsParsed is Weva.Css.Values.CssIdentifier bsId) borderBox = bsId.Name == "border-box";
                        else borderBox = cbb.Style.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
                        double mhOuter = cbb.MarginLeft + cbb.MarginRight;
                        if (pxMax > 0) {
                            double maxOuter = mhOuter + (borderBox ? pxMax : pxMax + mFrame);
                            if (w > maxOuter) w = maxOuter;
                        }
                        if (pxMin > 0) {
                            double minOuter = mhOuter + (borderBox ? pxMin : pxMin + mFrame);
                            if (w < minOuter) w = minOuter;
                        }
                    }
                } else {
                    w = OuterWidth(c);
                }
                sum += w; if (w > maxItem) maxItem = w; n++;
            }
            if (isRow) return sum + (n > 1 ? gap * (n - 1) : 0);
            return maxItem;
        }

        // Cross-axis intrinsic of a flex container.
        // For row flex: max(child outer height) — items lay out on a single line.
        // For column flex: sum(child outer heights) + gaps — items stack.
        // Used as grid-item IntrinsicCross when the box is a flex container; the
        // pre-flex BlockLayout pass stacks ALL children vertically (sum-of-heights)
        // which inflates auto rows of a parent grid when the flex is row-direction
        // (e.g. chat.html composer: 4 buttons + input pre-flex stack to ~168 but
        // the actual row-flex cross size is ~40).
        internal static double FlexIntrinsicCross(Weva.Layout.Flex.FlexBox container) {
            bool isRow = true;
            double gap = 0;
            if (container.Style != null) {
                string dir = container.Style.Get("flex-direction");
                if (dir == "column" || dir == "column-reverse") isRow = false;
                string g = isRow ? container.Style.Get("row-gap") : container.Style.Get("column-gap");
                if (string.IsNullOrEmpty(g) || g == "normal") g = container.Style.Get("gap");
                if (!string.IsNullOrEmpty(g)) {
                    if (TryReadFirstCssNumber(g, out double parsedGap)) gap = parsedGap;
                }
            }
            double sum = 0, maxItem = 0; int n = 0;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                double mt = (c is BlockBox cbb) ? cbb.MarginTop + cbb.MarginBottom : 0;
                // Multi-pass convergence guard for ROW direction (audit #19b /
                // #280): prefer first-pass `PreFlexCrossHeight` (stamped once
                // by BlockLayout, immune to later flex stretch) over the live
                // `.Height` — the latter may have been stretched by an earlier
                // flex pass, and feeding that back into line.CrossSize causes
                // monotonic cross-axis growth across re-layout passes.
                // Falls through to the narrow LineBox-sum helper for cases
                // where PreFlex wasn't stamped (e.g. flex/grid items whose
                // BlockLayout path was bypassed).
                double crossSize;
                if (c is BlockBox bbCross && bbCross.PreFlexCrossHeight > 0
                    && !(c is Weva.Layout.Flex.FlexBox)
                    && !(c is Weva.Layout.Grid.GridBox)) {
                    // Conservative scope — only plain BlockBox children.
                    // FlexBox/GridBox children were tried but caused regressions
                    // (dialogue portrait, quest body) because the FlexLayout-
                    // stamped value isn't the right "intrinsic" for outer
                    // FlexIntrinsicCross consumers. See #280 notes.
                    //
                    // PreFlexCrossHeight is stamp-ONCE; on a tree that hasn't
                    // converged on the first BlockLayout pass (e.g. a watchlist
                    // whose rows first lay out at body width, then settle once
                    // their flex/grid column shrinks) the stamp captures a
                    // STALE-INFLATED height that never updates. Feeding that up
                    // balloons the ancestor — a `min-height:100vh` dashboard
                    // (flex → grid → flex → watchlist) ballooned 917→1348. The
                    // pre-stretch stamp guards against POST-stretch inflation
                    // (current Height too big); it must never exceed the CURRENT
                    // content intrinsic, which guards against PRE-convergence
                    // inflation. The true stretch-free intrinsic is the smaller.
                    crossSize = System.Math.Min(bbCross.PreFlexCrossHeight, IntrinsicCrossOfPureInlineBlock(c));
                } else {
                    crossSize = IntrinsicCrossOfPureInlineBlock(c);
                }
                double oh = mt + crossSize;
                sum += oh; if (oh > maxItem) maxItem = oh; n++;
            }
            if (isRow) return maxItem;
            return sum + (n > 1 ? gap * (n - 1) : 0);
        }

        static bool TryReadFirstCssNumber(string raw, out double value) {
            value = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            int i = 0;
            while (i < raw.Length) {
                char c = raw[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+') break;
                i++;
            }
            if (i >= raw.Length) return false;

            bool negative = false;
            if (raw[i] == '-' || raw[i] == '+') {
                negative = raw[i] == '-';
                i++;
            }

            double whole = 0;
            bool any = false;
            while (i < raw.Length) {
                char c = raw[i];
                if (c < '0' || c > '9') break;
                any = true;
                whole = whole * 10 + (c - '0');
                i++;
            }

            double frac = 0;
            double scale = 1;
            if (i < raw.Length && raw[i] == '.') {
                i++;
                while (i < raw.Length) {
                    char c = raw[i];
                    if (c < '0' || c > '9') break;
                    any = true;
                    frac = frac * 10 + (c - '0');
                    scale *= 10;
                    i++;
                }
            }

            if (!any) return false;
            value = whole + (scale > 1 ? frac / scale : 0);
            if (negative) value = -value;
            return true;
        }

        // Narrow helper: only returns a synthesized intrinsic for the
        // pure-inline-content BlockBox pattern (LineBox-only children, no
        // block-level descendants, no flex/grid). Anything else returns
        // `.Height` as before. The pure-inline pattern is the one where
        // post-flex-stretch `.Height` leaks back into FlexIntrinsicCross
        // and causes hud's stat-name to snowball 15 → 20 → 35 across passes.
        // Chip-strip-style flex containers with explicit-sized children
        // route through the non-BlockBox or block-child branch and keep
        // `.Height` intact.
        static double IntrinsicCrossOfPureInlineBlock(Box b) {
            if (!(b is BlockBox bb)) return b.Height;
            if (b is Weva.Layout.Flex.FlexBox fbChild) {
                // A flex child that its own container (or a parent grid
                // auto-row) stretched via align-items:stretch reports an
                // INFLATED .Height. Feeding that back into the outer cross-
                // intrinsic causes monotonic growth across passes — settings
                // .panel-foot (row flex in a `grid-template-rows: auto 1fr auto`
                // auto-row) snowballed its inline-flex buttons 35 → … → 356.
                // When the flex child has no explicit height, derive its
                // stretch-immune content cross (FlexIntrinsicCross walks its
                // own children, bottoming out at line boxes) and use it ONLY
                // when it is strictly smaller than the stored Height — the
                // stretched-past-intrinsic case. Equal/greater means the stored
                // Height is the genuine intrinsic (explicit/min-height or
                // content-tall column flex), so we keep it and don't regress
                // dialogue-portrait / quest-body (see #280 notes).
                if (!HasExplicitDim(b, "height")) {
                    double fFrame = fbChild.PaddingTop + fbChild.PaddingBottom + fbChild.BorderTop + fbChild.BorderBottom;
                    double fIntrinsic = FlexIntrinsicCross(fbChild) + fFrame;
                    if (fIntrinsic > 0 && fIntrinsic < b.Height) return fIntrinsic;
                }
                return b.Height;
            }
            if (b is Weva.Layout.Grid.GridBox) return b.Height;
            double sumLines = 0;
            int lineCount = 0;
            int otherCount = 0;
            for (int i = 0; i < b.Children.Count; i++) {
                var c = b.Children[i];
                if (c is LineBox lb) { sumLines += lb.Height; lineCount++; }
                else otherCount++;
            }
            if (lineCount == 0 || otherCount > 0) return b.Height;
            double frame = bb.PaddingTop + bb.PaddingBottom + bb.BorderTop + bb.BorderBottom;
            double intrinsic = sumLines + frame;
            // Only return the synthesized value when it's STRICTLY smaller
            // than the stored Height — that's the stretched-past-intrinsic
            // case. If Height already matches (or is smaller, e.g. due to
            // an explicit min-height), trust it.
            return intrinsic < b.Height ? intrinsic : b.Height;
        }

        // Mirror of GridIntrinsicInline for the cross axis (height). Uses
        // already-positioned items' Y+Height extents — robust when GridLayout
        // has run at least once. The flex/grid INTRINSIC for a row-axis flex
        // parent (computing a grid item's IntrinsicCross) was previously
        // falling back to `bb.Height`, which on multi-pass converges retains
        // STALE values from earlier passes (canonical case: inventory's
        // .grid container, sized to 6 row-tracks worth by an early pass,
        // never shrinking to 5 once placement settled — pushes the details
        // panel's Equip/Drop buttons off-screen). Reading positioned items'
        // bottoms gives a converged-state intrinsic regardless of stale
        // Height. Falls through to bb.Height when no items are positioned
        // yet (first pass).
        internal static double GridIntrinsicCross(Weva.Layout.Grid.GridBox container) {
            double minT = double.PositiveInfinity, maxB = double.NegativeInfinity;
            int n = 0;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                double mt = (c is BlockBox cbb) ? cbb.MarginTop : 0;
                double mb = (c is BlockBox cbb2) ? cbb2.MarginBottom : 0;
                double t = c.Y - mt;
                double b = c.Y + c.Height + mb;
                if (t < minT) minT = t;
                if (b > maxB) maxB = b;
                n++;
            }
            if (n == 0 || double.IsInfinity(minT)) return 0;
            return maxB - minT;
        }

        // CSS Grid L1 §5.2 / §12.4 — grid container min-content inline size.
        //
        // Algorithm (simplified, mirroring GridTrackSizing.Resolve under a
        // min-content / available=0 constraint):
        //   1. Fixed (px) tracks: contribute their fixed size.
        //   2. Auto / fr tracks (fr = minmax(auto,fr) per §7.2.4 §6.6):
        //      contribute the max of their items' min-content widths.
        //   3. min-content / max-content / minmax(intrinsic,...) tracks:
        //      treated as auto for this simplified pass.
        //   4. Sum of all track contributions + column-gap*(n-1) + container
        //      horizontal frame = the min-content border-box width.
        //   5. Clamped by the container's own min-width / max-width.
        //
        // This replaces the prior `MaxContentWidth(box)` fallback which
        // over-floored fr tracks of parent grids containing nested grids
        // (glass.html regression: nested `.tiles` inflated the 1fr board
        // column past the viewport). Non-destructive: reads already-positioned
        // children and the ResolvedProperties set by GridLayout; no re-layout.
        //
        // Falls back to MaxContentWidth when the grid hasn't been laid out yet
        // (ResolvedProperties.Columns == null) — that case only arises during
        // the pre-grid block-layout pass, before GridLayout runs.
        //
        // Multi-span items split their contribution evenly across the spanned
        // auto/fr tracks (same "even split" simplification as Phase 1c).
        internal static double GridMinContentWidth(
            Weva.Layout.Grid.GridBox container,
            Func<TextRun, double> minTextMeasure = null)
        {
            if (container == null) return 0;

            var resolvedProps = container.ResolvedProperties;
            // Columns == null means GridLayout hasn't run for this container yet.
            // Columns == GridTemplate.Empty means an implicit-only grid.
            if (resolvedProps.Columns == null) {
                // Pre-layout: fall back to max-content. Safe: over-floor at
                // worst, but won't happen in the fr-floor Phase 1c path which
                // runs AFTER GridLayout has resolved all nested grids.
                return MaxContentWidth(container);
            }

            var tracks = resolvedProps.Columns.Tracks;
            int nTracks = tracks != null ? tracks.Count : 0;
            double columnGap = resolvedProps.ColumnGap;
            double padLeft = container.PaddingLeft + container.BorderLeft;

            // Gather in-flow children with their left edges relative to the
            // content box. Items have X set by ApplyItemAlignment:
            //   box.X = cellX + offsetX + marginLeft
            //   cellX = padLeft + trackPosition[colStart]
            // So: relativeX ≈ child.X - padLeft - marginLeft = trackPosition[colStart]
            // We build a list of (relX, minContent, relR) for track-span matching.
            // For items with no explicit width (stretch-aligned), Width ≈ cellWidth.

            // Step 1: collect all distinct track-start X positions to build an
            // index map. Positions within HalfPixelEqual are the same track start.
            const double eps = 0.5;
            var trackStarts = new System.Collections.Generic.List<double>(nTracks + 1);
            for (int ci = 0; ci < container.Children.Count; ci++) {
                var c = container.Children[ci];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                if (!(c is BlockBox childBox)) continue;
                double relX = childBox.X - padLeft - childBox.MarginLeft;
                bool found = false;
                for (int k = 0; k < trackStarts.Count; k++) {
                    if (System.Math.Abs(trackStarts[k] - relX) < eps) { found = true; break; }
                }
                if (!found) trackStarts.Add(relX);
            }
            trackStarts.Sort();

            // If we couldn't determine track starts from items, fall back to
            // the max-content path (e.g. empty grid or all abs-pos children).
            if (trackStarts.Count == 0) {
                return ClampByOwnMinMax(container, GridIntrinsicInline(container) + HorizontalFrame(container));
            }

            // Actual number of columns = max of explicit track count and observed
            // distinct item-start positions (implicit grid may have more columns).
            int nCols = System.Math.Max(nTracks, trackStarts.Count);
            double[] colMinContent = new double[nCols];

            // Step 2: for each in-flow child, compute its min-content and
            // distribute it across the track(s) it spans.
            for (int ci = 0; ci < container.Children.Count; ci++) {
                var c = container.Children[ci];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                if (!(c is BlockBox childBox2)) continue;

                // Compute item border-box min-content.
                double itemMin;
                if (HasDefiniteInlineSize(childBox2)) {
                    itemMin = childBox2.Width;
                } else if (c is Weva.Layout.Flex.FlexBox fxChild) {
                    itemMin = MinContentWidth(fxChild, minTextMeasure);
                } else if (c is Weva.Layout.Grid.GridBox gxChild) {
                    // Recursive: nested grid-in-grid.
                    itemMin = GridMinContentWidth(gxChild, minTextMeasure);
                } else {
                    // Plain block: walk content, add frame.
                    double inner = 0;
                    WalkMinContent(c, ref inner, minTextMeasure);
                    itemMin = inner + HorizontalFrame(childBox2);
                }
                // Respect explicit min-width; explicit min-width:0 opts out
                // per CSS Grid §6.6 automatic-minimum opt-out.
                string minWRaw = childBox2.Style?.Get(Weva.Css.Cascade.CssProperties.MinWidthId);
                if (!string.IsNullOrEmpty(minWRaw)) {
                    if (minWRaw == "0" || minWRaw == "0px") {
                        itemMin = 0;
                    } else if (minWRaw != "auto") {
                        double pxMinW = TryReadPxLength(minWRaw);
                        if (pxMinW > itemMin) itemMin = pxMinW;
                    }
                }
                if (itemMin <= 0) continue;

                // Find the track span: start = first trackStarts entry ≈ relX;
                // end = first entry whose position ≥ child's right edge.
                double relX2 = childBox2.X - padLeft - childBox2.MarginLeft;
                double relR2 = relX2 + childBox2.Width + childBox2.MarginRight;

                int spanStart = 0;
                for (int k = 0; k < trackStarts.Count; k++) {
                    if (System.Math.Abs(trackStarts[k] - relX2) < eps) { spanStart = k; break; }
                    if (trackStarts[k] > relX2 + eps) break;
                }
                // Span end: count how many distinct starts fall within the item.
                int spanEnd = spanStart + 1;
                for (int k = spanStart + 1; k < trackStarts.Count; k++) {
                    if (trackStarts[k] < relR2 - eps) spanEnd = k + 1;
                    else break;
                }
                int span = spanEnd - spanStart;
                if (span <= 0) span = 1;
                if (spanEnd > nCols) spanEnd = nCols;
                if (spanStart >= nCols) continue;

                // For single-span items: contribute directly.
                // For multi-span items: subtract any fixed-track contributions
                // first, then split the remainder evenly across the auto/fr
                // tracks in the span (mirroring Phase 1c's even-split).
                double fixedContrib = 0;
                int flexCount = 0;
                for (int k = spanStart; k < spanEnd; k++) {
                    if (k < nTracks) {
                        var tdef = tracks[k];
                        double fixedPx = FixedTrackMinContentSize(tdef);
                        if (fixedPx > 0) {
                            fixedContrib += fixedPx;
                        } else {
                            flexCount++;
                        }
                    } else {
                        // Implicit track — auto.
                        flexCount++;
                    }
                }
                if (flexCount == 0) continue; // entirely fixed span
                double gapInSpan = span > 1 ? columnGap * (span - 1) : 0;
                double rem = itemMin - fixedContrib - gapInSpan;
                if (rem <= 0) continue;
                double perFlex = rem / flexCount;
                for (int k = spanStart; k < spanEnd && k < nCols; k++) {
                    bool isFixed = (k < nTracks) && (FixedTrackMinContentSize(tracks[k]) > 0);
                    if (!isFixed) {
                        if (perFlex > colMinContent[k]) colMinContent[k] = perFlex;
                    }
                }
            }

            // Step 3: sum all track contributions.
            // Fixed tracks: use their min-content size (px/percentage).
            // Auto/fr/intrinsic tracks: use the item-derived colMinContent[k].
            // Percentage tracks under min-content constraint → 0 (indefinite container).
            double sum = 0;
            int activeTracks = 0;
            for (int k = 0; k < nCols; k++) {
                double sz;
                if (k < nTracks) {
                    double fixedPx = FixedTrackMinContentSize(tracks[k]);
                    sz = fixedPx > 0 ? fixedPx : colMinContent[k];
                } else {
                    // Implicit track (auto) — use item contribution.
                    sz = colMinContent[k];
                }
                sum += sz;
                activeTracks++;
            }
            double gaps = activeTracks > 1 ? columnGap * (activeTracks - 1) : 0;
            double result = sum + gaps + HorizontalFrame(container);
            return ClampByOwnMinMax(container, result);
        }

        // Returns the fixed pixel size of a track for min-content purposes.
        // Only px-length and resolved-px percentage tracks have a fixed minimum;
        // all intrinsic and flexible tracks return 0 (they size to content).
        // Percentage tracks return 0 under min-content constraint (available
        // inline size is indefinite / treated as 0 per CSS Sizing §5).
        static double FixedTrackMinContentSize(Weva.Layout.Grid.GridTrackSize t) {
            switch (t.Kind) {
                case Weva.Layout.Grid.GridTrackKind.Length:
                    return t.Value > 0 ? t.Value : 0;
                case Weva.Layout.Grid.GridTrackKind.Percentage:
                    // Percentage under min-content constraint (available=0) → 0.
                    return 0;
                case Weva.Layout.Grid.GridTrackKind.Minmax: {
                    // minmax(len, fr): the minimum is the px-length min floor.
                    var min = t.MinChild();
                    if (min.Kind == Weva.Layout.Grid.GridTrackKind.Length) return min.Value > 0 ? min.Value : 0;
                    // All other minmax forms (auto/intrinsic min) → 0.
                    return 0;
                }
                // Auto, fr, MinContent, MaxContent, FitContent → items size them.
                default:
                    return 0;
            }
        }

        static double GridIntrinsicInline(Weva.Layout.Grid.GridBox container) {
            // Prefer reading the resolved track template directly: when the
            // grid container hasn't been laid out yet (e.g. it's a flex item
            // and the flex base-size probe is asking for max-content width
            // before GridLayout has run), the children have X=0 / Width=0 and
            // the bounding-box approach returns 0, which made the flex
            // algorithm size the grid to the parent's main-size instead of
            // its intrinsic width. The track template is enough to answer
            // the question for the explicit-pixel case (which is the
            // overwhelming majority of grid containers used as flex items —
            // e.g. `grid-template-columns: repeat(8, 56px)` for a fixed-size
            // board widget). Falls through to the bounding-box path for
            // intrinsic-track templates (auto / fr / max-content) which we
            // can't measure without running layout first.
            double trackSum = ResolveExplicitColumnTrackSum(container);
            if (trackSum > 0) {
                return trackSum;
            }
            // Use already-positioned items: rightmost edge minus leftmost edge
            // of in-flow children. Robust to gap & subgrid (grid items are
            // packed at track edges; no equivalent of justify-content:space-
            // between on the column axis by default).
            double minL = double.PositiveInfinity, maxR = double.NegativeInfinity;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                double ml = (c is BlockBox cbb) ? cbb.MarginLeft : 0;
                double mr = (c is BlockBox cbb2) ? cbb2.MarginRight : 0;
                double l = c.X - ml;
                double r = c.X + c.Width + mr;
                if (l < minL) minL = l;
                if (r > maxR) maxR = r;
            }
            if (double.IsInfinity(minL)) return 0;
            return maxR - minL;
        }

        // Sums explicit pixel-valued column tracks + their column-gaps. Returns
        // 0 for templates we can't statically measure (auto / fr / minmax /
        // intrinsic keywords) so the caller falls back to the bounding-box
        // path. Honors `repeat(N, <track>)` shorthand. Does not read the cell
        // count from `grid-auto-flow`-derived implicit tracks; explicit
        // `grid-template-columns` is the only signal here.
        static double ResolveExplicitColumnTrackSum(Weva.Layout.Grid.GridBox container) {
            if (container?.Style == null) return 0;
            string template = container.Style.Get("grid-template-columns");
            if (string.IsNullOrEmpty(template) || template == "none") return 0;
            // We don't have a LayoutContext at this static call site — em/rem
            // tracks fall back to a 16 px default which is the spec-mandated
            // root font size. Authors using `repeat(N, <px>)` (the case we
            // actually need to fix here) don't hit this branch.
            double fs = 16;
            double total = 0;
            int trackCount = 0;
            foreach (var token in TokenizeGridTracks(template)) {
                double trackPx = ParseTrackPixels(token, fs);
                if (trackPx <= 0) return 0; // bail on any non-pixel track
                total += trackPx;
                trackCount++;
            }
            if (trackCount <= 0) return 0;
            // column-gap may be a single length or come from `gap` shorthand.
            string gapRaw = container.Style.Get("column-gap");
            if (string.IsNullOrEmpty(gapRaw)) gapRaw = container.Style.Get("gap");
            double gap = ParseLengthPixels(gapRaw, fs);
            if (gap > 0) total += gap * (trackCount - 1);
            return total;
        }

        static System.Collections.Generic.IEnumerable<string> TokenizeGridTracks(string template) {
            // Hand-rolled tokenizer: split on whitespace at paren-depth zero,
            // expand `repeat(N, <inner>)` inline.
            int n = template.Length;
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            for (int i = 0; i < n; i++) {
                char c = template[i];
                if (c == '(') { depth++; sb.Append(c); continue; }
                if (c == ')') { depth--; sb.Append(c); continue; }
                if (depth == 0 && (c == ' ' || c == '\t')) {
                    if (sb.Length > 0) {
                        foreach (var t in ExpandRepeat(sb.ToString())) yield return t;
                        sb.Length = 0;
                    }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) {
                foreach (var t in ExpandRepeat(sb.ToString())) yield return t;
            }
        }

        static System.Collections.Generic.IEnumerable<string> ExpandRepeat(string token) {
            // Accepts `repeat(N, <track>)` where N is an integer and <track>
            // is a single track expression (no nested repeats). For anything
            // else, yields the token verbatim.
            if (!token.StartsWith("repeat(", System.StringComparison.OrdinalIgnoreCase) || !token.EndsWith(")")) {
                yield return token;
                yield break;
            }
            string inner = token.Substring(7, token.Length - 8);
            int comma = inner.IndexOf(',');
            if (comma < 0) { yield return token; yield break; }
            // countStr only feeds TryParse — slice as a span to skip the
            // substring + trim allocation; trackPart still needs to be a
            // string because it's yielded back to the caller verbatim.
            var countSpan = inner.AsSpan(0, comma).Trim();
            string trackPart = inner.Substring(comma + 1).Trim();
            if (!int.TryParse(countSpan, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int count)) {
                yield return token; yield break;
            }
            for (int i = 0; i < count; i++) yield return trackPart;
        }

        static double ParseTrackPixels(string token, double fontSize) {
            return ParseLengthPixels(token, fontSize);
        }

        static double ParseLengthPixels(string raw, double fontSize) {
            if (string.IsNullOrEmpty(raw)) return 0;
            raw = raw.Trim();
            // The shorthand-derived `gap` may carry two values (row + col); we
            // only want the column one — first whitespace-delimited token.
            int sp = raw.IndexOf(' ');
            if (sp > 0) raw = raw.Substring(0, sp);
            if (raw.EndsWith("px")) {
                if (double.TryParse(raw.AsSpan(0, raw.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)) return px;
            }
            if (raw.EndsWith("em")) {
                if (double.TryParse(raw.AsSpan(0, raw.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var em)) return em * fontSize;
            }
            if (raw.EndsWith("rem")) {
                if (double.TryParse(raw.AsSpan(0, raw.Length - 3), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rem)) return rem * 16;
            }
            return 0;
        }

        static bool HasExplicitDim(Box box, string property) {
            if (box.Style == null) return false;
            string raw = box.Style.Get(property);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return false;
            if (raw == "min-content" || raw == "max-content" || raw == "fit-content") return false;
            if (raw.StartsWith("fit-content(")) return false;
            if (property == "width" && box.IntrinsicWidth > 0) return false;
            if (property == "height" && box.IntrinsicHeight > 0) return false;
            return true;
        }

        // True when the cascaded `height` is a shrink-to-fit keyword (CSS
        // Sizing L3: fit-content, min-content, max-content). These values
        // request content-based sizing and must NOT stretch to fill the inset
        // space when both vertical pins are set (CSS §10.6.7 exception: the
        // "stretch" rule only applies to height:auto, not to explicit
        // sizing keywords). Mirrors HasExplicitDim's keyword list.
        static bool IsHeightShrinkToFit(Box box) {
            if (box?.Style == null) return false;
            string raw = box.Style.Get("height");
            if (string.IsNullOrEmpty(raw) || raw == "auto") return false;
            if (raw == "min-content" || raw == "max-content" || raw == "fit-content") return true;
            if (raw.StartsWith("fit-content(")) return true;
            return false;
        }

        // True when `property` (width / height) resolves to a definite length
        // — i.e. a concrete pixel count we can subtract from cb.Width/Height
        // to compute slack for `margin: auto` centering on an abs-pos box.
        // Returns false for `auto`, `fit-content`, `min-content`, and
        // `max-content`, which per CSS Position 3 §10.3.7 / §10.6.4 leave
        // the auto margins computed to 0 (no centering) — the box's actual
        // size is content-derived and the spec's centering formula does
        // not apply.
        static bool IsDefiniteSize(Box box, string property, LayoutContext ctx, double cbBasis) {
            if (box.Style == null) return false;
            string raw = box.Style.Get(property);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return false;
            double fs = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
            var r = StyleResolver.ResolveLength(raw, box.Style, ctx, fs, cbBasis);
            return r.Kind == StyleResolver.LengthKind.Length
                || r.Kind == StyleResolver.LengthKind.Percent;
        }

        static bool IsBorderBox(Box box) {
            if (box?.Style == null) return false;
            var v = box.Style.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
            if (v is Weva.Css.Values.CssKeyword k) return k.Identifier == "border-box";
            if (v is Weva.Css.Values.CssIdentifier id) return id.Name == "border-box";
            return box.Style.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
        }
    }
}
