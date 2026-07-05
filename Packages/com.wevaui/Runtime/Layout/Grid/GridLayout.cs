// GridLayout — formatting context for `display: grid` / `display: inline-grid`.
//
// Dispatch site: BoxBuilder.cs constructs a `GridBox` for elements whose
// computed display is grid / inline-grid. LayoutEngine.cs runs a post-pass
// after BlockLayout (RunGridPasses) that walks the box tree depth-first and
// invokes GridLayout.Layout on every GridBox.
//
// Algorithm follows CSS Grid Layout Module Level 1 (simplified for v1).
//
// v1 simplifications (also documented in PLAN.md):
//   - `min-content` track keyword resolved as 0 (no per-item content
//     introspection beyond BlockLayout's pre-grid Width/Height).
//   - `max-content` track keyword resolved by inspecting the spanning items'
//     pre-grid Width/Height (the BlockLayout result).
//   - `auto` for an item axis defaults to `span 1` and falls back to
//     auto-placement.
//   - Track-sizing distributes intrinsic sizes evenly across the item's spanned
//     intrinsic tracks (no min-content/max-content distinction inside spans).
//   - When grid sets a child's size different from its style.width/height, the
//     box's Width/Height is overwritten but the child's interior is not
//     re-flowed (mirrors the flex behaviour).
//   - Both axes are sized independently: column tracks first (using
//     containerWidth), then row tracks. Items contribute their post-block
//     intrinsic block size to row sizing.
//   - `auto-fill` / `auto-fit` count uses the *fixed* part of the pattern and
//     the resolved gap (not min content of items inside the pattern).
//   - Item min-width / max-width / min-height / max-height clamp the final
//     placed size; aspect-ratio is not honoured.

using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout.Grid {
    internal sealed class GridLayout {
        // Pass-reuse invariant: constructed once per LayoutEngine. The
        // LayoutScratch.GridRawItems is the only shared scratch we touch and
        // it follows stack discipline within a single Layout call. ctx is
        // refreshed via Reset() per pass.
        LayoutContext ctx;
        readonly LayoutScratch scratch;
        // Optional re-flow hook: when grid resizes a child to its cell size,
        // invoking blockLayout.RelayoutContentAt(child, w) re-flows the
        // child's interior against the new width. Without this, the child's
        // descendants keep the (typically wider) widths BlockLayout assigned
        // during the initial pre-grid pass — the bug behind .panel.actionbar
        // and .actionbar-wrap rendering at viewport width despite their
        // grid cell being far smaller.
        BlockLayout blockLayout;
        // After RelayoutContentAt re-stacks the subtree, flex-positioned
        // descendants (e.g. xp-footer inside an .actionbar-wrap inside the
        // shrunk grid item) are at BlockLayout positions and need their
        // flex layout re-applied so their cross-axis Y values match the
        // container's flex algorithm rather than the post-block stack.
        Weva.Layout.Flex.FlexLayout flexLayout;
        readonly Dictionary<GridPropsCacheKey, GridContainerProperties> containerPropsCache = new(32);
        readonly Dictionary<GridPropsCacheKey, GridItemProperties> itemPropsCache = new(64);

        public GridLayout(LayoutScratch scratch) {
            this.scratch = scratch;
        }

        internal void SetBlockLayout(BlockLayout bl) {
            this.blockLayout = bl;
        }

        internal void SetFlexLayout(Weva.Layout.Flex.FlexLayout fl) {
            this.flexLayout = fl;
        }

        public void Reset(LayoutContext ctx) {
            this.ctx = ctx;
        }

        GridContainerProperties GetContainerProperties(Weva.Css.Cascade.ComputedStyle style, Weva.Css.Values.LengthContext lengthCtx) {
            return GetContainerProperties(style, lengthCtx, lengthCtx.BasisPixels, blockBasisPx: null);
        }

        // Block-basis-aware lookup so row-gap percentages can resolve against
        // the grid container's height (E2 / CSS Box Alignment L3 §8.3).
        // inlineBasisPx is the inline-axis container size (width in
        // horizontal-tb) — used for column-gap percentage resolution;
        // blockBasisPx is the block-axis size (height) for row-gap.
        GridContainerProperties GetContainerProperties(
            Weva.Css.Cascade.ComputedStyle style,
            Weva.Css.Values.LengthContext lengthCtx,
            double? inlineBasisPx,
            double? blockBasisPx) {
            if (style == null) return GridContainerProperties.From(style, lengthCtx, inlineBasisPx, blockBasisPx);
            var key = new GridPropsCacheKey(style, style.Version, lengthCtx, inlineBasisPx, blockBasisPx);
            if (containerPropsCache.TryGetValue(key, out var cached)) return cached;
            var props = GridContainerProperties.From(style, lengthCtx, inlineBasisPx, blockBasisPx);
            if (containerPropsCache.Count >= 256) containerPropsCache.Clear();
            containerPropsCache[key] = props;
            return props;
        }

        GridItemProperties GetItemProperties(Weva.Css.Cascade.ComputedStyle style, Weva.Css.Values.LengthContext lengthCtx) {
            if (style == null) return GridItemProperties.From(style, lengthCtx);
            var key = new GridPropsCacheKey(style, style.Version, lengthCtx);
            if (itemPropsCache.TryGetValue(key, out var cached)) return cached;
            var props = GridItemProperties.From(style, lengthCtx);
            if (itemPropsCache.Count >= 512) itemPropsCache.Clear();
            itemPropsCache[key] = props;
            return props;
        }

        readonly struct GridPropsCacheKey : System.IEquatable<GridPropsCacheKey> {
            readonly Weva.Css.Cascade.ComputedStyle style;
            readonly long version;
            readonly double baseFontSize;
            readonly double rootFontSize;
            readonly double viewportWidth;
            readonly double viewportHeight;
            readonly double dpi;
            readonly double basis;
            readonly bool hasBasis;
            readonly double inlineBasis;
            readonly bool hasInlineBasis;
            readonly double blockBasis;
            readonly bool hasBlockBasis;

            public GridPropsCacheKey(Weva.Css.Cascade.ComputedStyle style, long version, Weva.Css.Values.LengthContext ctx)
                : this(style, version, ctx, ctx.BasisPixels, blockBasisPx: null) { }

            public GridPropsCacheKey(
                Weva.Css.Cascade.ComputedStyle style,
                long version,
                Weva.Css.Values.LengthContext ctx,
                double? inlineBasisPx,
                double? blockBasisPx) {
                this.style = style;
                this.version = version;
                baseFontSize = ctx.BaseFontSizePx;
                rootFontSize = ctx.RootFontSizePx;
                viewportWidth = ctx.ViewportWidthPx;
                viewportHeight = ctx.ViewportHeightPx;
                dpi = ctx.DpiPixelsPerInch;
                hasBasis = ctx.BasisPixels.HasValue;
                basis = ctx.BasisPixels.GetValueOrDefault();
                hasInlineBasis = inlineBasisPx.HasValue;
                inlineBasis = inlineBasisPx.GetValueOrDefault();
                hasBlockBasis = blockBasisPx.HasValue;
                blockBasis = blockBasisPx.GetValueOrDefault();
            }

            public bool Equals(GridPropsCacheKey other) {
                return ReferenceEquals(style, other.style)
                    && version == other.version
                    && baseFontSize == other.baseFontSize
                    && rootFontSize == other.rootFontSize
                    && viewportWidth == other.viewportWidth
                    && viewportHeight == other.viewportHeight
                    && dpi == other.dpi
                    && hasBasis == other.hasBasis
                    && basis == other.basis
                    && hasInlineBasis == other.hasInlineBasis
                    && inlineBasis == other.inlineBasis
                    && hasBlockBasis == other.hasBlockBasis
                    && blockBasis == other.blockBasis;
            }

            public override bool Equals(object obj) => obj is GridPropsCacheKey other && Equals(other);

            public override int GetHashCode() {
                unchecked {
                    int h = style != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(style) : 0;
                    h = (h * 397) ^ version.GetHashCode();
                    h = (h * 397) ^ baseFontSize.GetHashCode();
                    h = (h * 397) ^ rootFontSize.GetHashCode();
                    h = (h * 397) ^ viewportWidth.GetHashCode();
                    h = (h * 397) ^ viewportHeight.GetHashCode();
                    h = (h * 397) ^ dpi.GetHashCode();
                    h = (h * 397) ^ hasBasis.GetHashCode();
                    h = (h * 397) ^ basis.GetHashCode();
                    h = (h * 397) ^ hasInlineBasis.GetHashCode();
                    h = (h * 397) ^ inlineBasis.GetHashCode();
                    h = (h * 397) ^ hasBlockBasis.GetHashCode();
                    h = (h * 397) ^ blockBasis.GetHashCode();
                    return h;
                }
            }
        }

        public void Layout(GridBox container) {
            LayoutInternal(container, measureOnly: false, out _);
        }

        // Non-destructive max-content BLOCK size (border-box height) of a grid:
        // resolves row tracks against ZERO available space so `1fr` rows size as
        // content, NOT to any flex/grid-assigned Height. Used by
        // FlexLayout.ComputeBaseSize as the column-flex base size of a grid item
        // — BlockLayout's pre-grid pass block-stacks the grid's children
        // vertically (a `repeat(3,1fr)` 6-item grid stacks to ~2269 instead of
        // its real 2-row ~646), and reading that as the flex base balloons a
        // `min-height:100vh` column. Runs the full sizing prologue in
        // `measureOnly` mode and returns before positioning any item, so it
        // mutates neither the container nor its children. Returns 0 for a null
        // box. See FLEX-MINHEIGHT-FILL in CSS_OPEN_GAPS.md.
        internal double MaxContentBlockSize(GridBox container) {
            LayoutInternal(container, measureOnly: true, out double blockSize);
            return blockSize;
        }

        void LayoutInternal(GridBox container, bool measureOnly, out double measuredBlockSize) {
            measuredBlockSize = 0;
            if (container == null) return;
            double preGridHeight = container.Height;

            double fs = StyleResolver.FontSizePx(container.Style, container.Parent?.Style, ctx);
            double containerWidth = container.ContentWidth;
            double containerHeight = container.ContentHeight;
            bool containerHeightDefinite = containerHeight > 0;
            bool containerHeightDefiniteForAspectOverflow = containerHeight > 0;
            // CSS Grid §11.5: when the container's height is indefinite (style
            // `height: auto` and not pinned by positioning / aspect-ratio),
            // intrinsic-row sizing must NOT use BlockLayout's pre-grid
            // content-stack height as the available height — that height was
            // computed by stacking children as block flow and is unrelated to
            // the eventual grid row tracks. Feeding it back into
            // GridTrackSizing.Resolve causes the "stretch auto tracks to fill
            // remaining space" v1 simplification to inflate the single auto
            // row track to BlockLayout's stacked content height (e.g.
            // .panel.unit's block-flow 169px instead of max(portrait,unit-meta)
            // = 88px). Treat as indefinite (0) for sizing in that case.
            if (container.Style != null) {
                string hRaw = container.Style.Get("height");
                bool heightIsAuto = string.IsNullOrEmpty(hRaw) || hRaw == "auto";
                bool pinnedByPositioning =
                    (container.Position == PositionType.Fixed || container.Position == PositionType.Absolute)
                    && container.OffsetTop.HasValue && container.OffsetBottom.HasValue
                    && container.Height > 0;
                bool aspectDerived =
                    StyleResolver.TryResolveAspectRatio(container.Style, out double _ar)
                    && _ar > 0 && container.Width > 0 && container.Height > 0;
                // If a parent grid container stretched us to fill our row,
                // the assigned Height IS our definite container size for the
                // recursive sizing pass — even though the style declares no
                // explicit height. Without this, e.g. `.thread { display:grid;
                // grid-template-rows: auto 1fr auto }` placed in a parent grid
                // with `height: 100vh` and no template rows (so the implicit
                // row fills the viewport) would treat its own height as
                // indefinite, collapse the 1fr middle row, and overflow the
                // cell. Mirrors how pinnedByPositioning preserves a definite
                // height set by PositioningPass.
                bool gridStretched = container.GridStretchedHeight && container.Height > 0;
                // Mirror of the grid-stretched carve-out: a flex parent that
                // assigned this grid container a definite TargetMainSize
                // (e.g. `.hero-picker-body { flex: 1 }` inside a definite-
                // height column-flex modal) makes container.Height a real
                // constraint, not a stale BlockLayout stack-sum value the
                // §195 comment warns about. Without this, `.hero-picker-body`'s
                // implicit auto row can't stretch against any container size
                // (containerHeight gets zeroed), so the scroll-container fix
                // for grid items collapses the row to 0 instead of 552.
                bool flexParentStretched = container.Parent is Weva.Layout.Flex.FlexBox flexParent
                    && container.Height > 0
                    && IsFlexParentDefiniteMainColumn(flexParent);
                containerHeightDefinite = !heightIsAuto || pinnedByPositioning || aspectDerived || gridStretched || flexParentStretched;
                containerHeightDefiniteForAspectOverflow = !heightIsAuto || pinnedByPositioning || aspectDerived;
                if (heightIsAuto && !pinnedByPositioning && !aspectDerived && !gridStretched && !flexParentStretched) {
                    containerHeight = 0;
                }
                // A definite min-height floors the row-axis available size even
                // when `height` is auto, so align-content (space-between/center/
                // end/stretch) has real free space to distribute — Chrome spreads
                // grid rows to fill a min-height container. Mirrors FlexLayout's
                // min-height handling in HasDefiniteMain.
                double minHBB = ResolveContainerMinHeightBorderBox(container, fs);
                if (minHBB > 0) {
                    double frameV = container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;
                    double minContent = System.Math.Max(0, minHBB - frameV);
                    if (minContent > containerHeight) {
                        containerHeight = minContent;
                        containerHeightDefinite = true;
                    }
                }
            }
            // H5b: populate lineHeightPx so `lh`-typed grid-template / gap /
            // padding values on the container resolve against the cascaded
            // line-height instead of the 1.2 * fontSize fallback.
            double containerLh = StyleResolver.LineHeightPx(container.Style, fs, ctx, ctx.GetMetrics(container.Style?.Get(Weva.Css.Cascade.CssProperties.FontFamilyId)));
            var lengthCtx = ctx.ToLengthContext(fs, containerWidth, containerLh);

            // CSS Box Alignment L3 §8.3 (E2): row-gap percentages resolve
            // against the block-axis container size (height in horizontal
            // writing modes); column-gap against the inline-axis size
            // (width). Pass both explicitly so the resolver doesn't conflate
            // the two. When the height is indefinite (containerHeight zeroed
            // out above) we pass null — the spec collapses percentage gap
            // to 0 in that case, matched by ResolveGap's no-basis branch.
            double? inlineBasis = containerWidth > 0 ? containerWidth : (double?)null;
            double? blockBasis = (containerHeightDefinite && containerHeight > 0) ? containerHeight : (double?)null;
            var props = GetContainerProperties(container.Style, lengthCtx, inlineBasis, blockBasis);

            // Subgrid resolution (CSS Grid Level 2): if the child declared
            // `subgrid` for an axis AND its parent is itself a GridBox whose
            // tracks have already been resolved, slice the parent's tracks
            // against the child's grid placement. Otherwise the keyword
            // degrades to `none` per spec — Columns/Rows stay at their
            // initial empty template.
            //
            // CSS Grid L2 §6 extension: `grid-auto-rows/columns: subgrid`
            // additionally marks implicit tracks (beyond the explicit subgrid
            // template) as inheriting parent sizing. When AutoRowsSubgrid /
            // AutoColumnsSubgrid is set, we synthesise the autoTracks array
            // by rotating the parent track list so that position 0 corresponds
            // to the first parent track AFTER the child's explicitly-covered
            // span. GridTrackSizing.Resolve then cycles that array for implicit
            // tracks exactly as it does for the normal auto-track pattern.
            if (props.ColumnsSubgrid || props.RowsSubgrid
                    || props.AutoColumnsSubgrid || props.AutoRowsSubgrid) {
                var parentGrid = container.Parent as GridBox;
                if (parentGrid != null && container.HasParentPlacement) {
                    var parentProps = parentGrid.ResolvedProperties;
                    var subgridArea = container.ParentPlacement;
                    if (props.ColumnsSubgrid && parentProps.Columns != null && parentProps.Columns.Tracks != null) {
                        props.Columns = Weva.Layout.Subgrid.SubgridTrackResolver.ResolveAxis(
                            parentProps.Columns.Tracks,
                            subgridArea.ColumnStart, subgridArea.ColumnEnd);
                    }
                    if (props.RowsSubgrid && parentProps.Rows != null && parentProps.Rows.Tracks != null) {
                        props.Rows = Weva.Layout.Subgrid.SubgridTrackResolver.ResolveAxis(
                            parentProps.Rows.Tracks,
                            subgridArea.RowStart, subgridArea.RowEnd);
                    }
                    // CSS Grid L2 §6: grid-auto-columns: subgrid — implicit
                    // column tracks beyond the explicit template inherit sizing
                    // from the parent. The start index into the parent track
                    // list for the first implicit column track is the child's
                    // explicit column end (1-based). When the child also has
                    // grid-template-columns: subgrid the explicit end is the
                    // parent area end; otherwise it is the child's resolved
                    // column end from its own explicit template.
                    if (props.AutoColumnsSubgrid && parentProps.Columns != null && parentProps.Columns.Tracks != null) {
                        // Implicit tracks start at the parent track position
                        // immediately after the child's explicitly-covered
                        // range. For a column-subgrid child that's subgridArea.ColumnEnd;
                        // for a non-subgrid child the explicit template ends at
                        // props.Columns.Tracks.Count tracks from ColumnStart.
                        int explicitColEnd = props.ColumnsSubgrid
                            ? subgridArea.ColumnEnd
                            : subgridArea.ColumnStart + (props.Columns?.Tracks?.Count ?? 0);
                        props.AutoColumns = Weva.Layout.Subgrid.SubgridTrackResolver.BuildAutoTracksFromParent(
                            parentProps.Columns.Tracks, explicitColEnd);
                    }
                    // CSS Grid L2 §6: grid-auto-rows: subgrid — implicit row
                    // tracks beyond the explicit template inherit parent sizing.
                    if (props.AutoRowsSubgrid && parentProps.Rows != null && parentProps.Rows.Tracks != null) {
                        int explicitRowEnd = props.RowsSubgrid
                            ? subgridArea.RowEnd
                            : subgridArea.RowStart + (props.Rows?.Tracks?.Count ?? 0);
                        props.AutoRows = Weva.Layout.Subgrid.SubgridTrackResolver.BuildAutoTracksFromParent(
                            parentProps.Rows.Tracks, explicitRowEnd);
                    }
                }
                // Non-grid parent: both auto-subgrid flags degrade to `auto`
                // per spec (same fallback as template subgrid → none). The
                // initial value (single auto track) was set in From(); no
                // change needed here for that case.
            }

            container.ResolvedProperties = props;

            // Borrow the shared rawItems scratch with stack-discipline. Layout is
            // recursive (nested grid items can themselves be GridBoxes — see the
            // `if (box is GridBox gb) Layout(gb)` tail below), so each frame
            // appends to and pops from the same list.
            var rawItems = scratch.GridRawItems;
            int rawStart = rawItems.Count;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (c is BlockBox bb) {
                    // Out-of-flow children are placed by PositioningPass; they
                    // must not contribute to track intrinsic sizing or be
                    // stretched into a grid cell. Without this filter, e.g.
                    // `.slot > .cd-shade { position: absolute; inset: 0 }`
                    // pre-sized against the body height feeds back as the
                    // slot's row-track cross-axis hint, inflating the cell
                    // from 37px to ~386px and pushing the bottom HUD row off
                    // screen. Mirrors PositioningPass.CompressOutOfFlow.
                    if (bb.Position == PositionType.Absolute || bb.Position == PositionType.Fixed) {
                        // CSS Grid L1 §9 (E7): pre-clear any grid-area CB
                        // stamp from a prior pass so a restyle that drops
                        // the definite placement reverts to the padding-edge
                        // fallback. The stamp is re-applied below once track
                        // sizes are known.
                        bb.HasGridAreaContainingBlock = false;
                        continue;
                    }
                    rawItems.Add(bb);
                }
            }
            int rawCount = rawItems.Count - rawStart;
            if (measureOnly && rawCount == 0) {
                // No in-flow items → intrinsic block size is just the frame.
                measuredBlockSize = container.PaddingTop + container.PaddingBottom
                    + container.BorderTop + container.BorderBottom;
                rawItems.RemoveRange(rawStart, rawItems.Count - rawStart);
                return;
            }
            if (rawCount == 0) {
                // CSS Grid L1 §9 (E7): even when no in-flow items exist, an
                // abs-pos child with definite grid placement still needs its
                // grid-area CB stamped. Resolve tracks against an empty
                // sizing-item set (the template's explicit sizes still
                // produce concrete Positions/Sizes for fixed-length tracks)
                // and run the stamp pass before bailing out.
                StampAbsPosForEmptyInFlow(container, props, containerWidth, containerHeight, fs);
                rawItems.RemoveRange(rawStart, rawItems.Count - rawStart);
                FinalizeContainerSize(container, props, 0, 0, fs);
                return;
            }

            // Compute per-item resolved properties.
            // itemProps / docOrder arrays are still per-pass allocations; their
            // sizes vary by container, so pooling them would require an array-pool
            // keyed by length. Deferred to a follow-up; for v1 we keep these as
            // per-call allocations alongside the partials/sizingItems lists below.
            var itemProps = scratch.RentGridItemPropsView();
            var docOrder = scratch.RentIntView();
            var partials = scratch.RentGridPartialView();
            var sizingItems = scratch.RentGridSizingItemView();
            for (int i = 0; i < rawCount; i++) {
                var bb = rawItems[rawStart + i];
                double itemFs = bb.Style != null ? StyleResolver.FontSizePx(bb.Style, container.Style, ctx) : fs;
                // H5b: thread per-item line-height through the LengthContext
                // so `lh` lengths on the item resolve against its own
                // cascaded line-height, not the 1.2 * fs fallback.
                double itemLh = bb.Style != null
                    ? StyleResolver.LineHeightPx(bb.Style, itemFs, ctx, ctx.GetMetrics(bb.Style.Get(Weva.Css.Cascade.CssProperties.FontFamilyId)))
                    : 0;
                var itemLengthCtx = ctx.ToLengthContext(itemFs, containerWidth, itemLh);
                itemProps.Add(GetItemProperties(bb.Style, itemLengthCtx));
            }

            // Sort by order (then doc order for ties) — auto-placement order.
            for (int i = 0; i < rawCount; i++) docOrder.Add(i);
            for (int i = 1; i < rawCount; i++) {
                int j = i;
                while (j > 0) {
                    int a = docOrder[j - 1];
                    int b = docOrder[j];
                    if (itemProps[a].Order > itemProps[b].Order) {
                        docOrder[j - 1] = b; docOrder[j] = a;
                        j--;
                    } else break;
                }
            }

            // Materialize auto-fill / auto-fit before computing sizes (count depends on the
            // resolved column-axis size, which is the container's content width for the major
            // axis when AutoFlow is row-based; row auto-fill/fit uses the height).
            (var colTrackArr, var colLineNames) = GridTrackSizing.MaterializeAutoRepeat(props.Columns, containerWidth, props.ColumnGap);
            (var rowTrackArr, var rowLineNames) = GridTrackSizing.MaterializeAutoRepeat(props.Rows, containerHeight, props.RowGap);

            // If grid-template-areas defines a region, but no template-rows/columns is
            // declared, derive the explicit grid extents from the areas map so named-area
            // resolution lines up with the auto grid.
            int explicitRows = rowTrackArr.Length;
            int explicitColumns = colTrackArr.Length;
            if (explicitRows == 0 && props.Areas != null && props.Areas.Rows > 0) {
                explicitRows = props.Areas.Rows;
                rowTrackArr = MakeAutoTracks(explicitRows);
                rowLineNames = MakeEmptyLineNames(explicitRows);
            }
            if (explicitColumns == 0 && props.Areas != null && props.Areas.Columns > 0) {
                explicitColumns = props.Areas.Columns;
                colTrackArr = MakeAutoTracks(explicitColumns);
                colLineNames = MakeEmptyLineNames(explicitColumns);
            }

            var resolvedProps = props;
            resolvedProps.Columns = new GridTemplate(colTrackArr, colLineNames, props.Columns.IsAutoFill, props.Columns.IsAutoFit, props.Columns.AutoRepeatPattern, props.Columns.AutoRepeatLineNames);
            resolvedProps.Rows = new GridTemplate(rowTrackArr, rowLineNames, props.Rows.IsAutoFill, props.Rows.IsAutoFit, props.Rows.AutoRepeatPattern, props.Rows.AutoRepeatLineNames);

            for (int idx = 0; idx < rawCount; idx++) {
                int original = docOrder[idx];
                var partial = GridPlacementResolver.Resolve(itemProps[original].Placement, resolvedProps,
                    explicitRows + 1, explicitColumns + 1);
                partials.Add(partial);
            }

            // Auto-place all items.
            var placement = GridAutoPlacement.Place(partials, props, explicitRows, explicitColumns);

            // Build sizing items and resolve track sizes for both axes.
            // v1: only items with explicitly-styled width / height contribute their
            // pre-grid width / height as an intrinsic-size hint to track sizing.
            // Items without explicit dimensions inherit the container width during
            // BlockLayout, which would otherwise inflate auto/min-content tracks
            // to the full container width when used as intrinsic input.
            for (int idx = 0; idx < rawCount; idx++) {
                int original = docOrder[idx];
                var bb = rawItems[rawStart + original];
                bool hasExplicitW = HasExplicitDimension(bb, isHeight: false);
                bool hasExplicitH = HasExplicitDimension(bb, isHeight: true);
                // For the cross axis (height), the item's post-BlockLayout
                // Height is its content's natural height, not an inflated
                // container-width-derived value, so we can safely contribute
                // it as the intrinsic cross-axis hint even without explicit
                // height. Without this, `grid-template-rows: auto 1fr auto`
                // collapses the auto rows to 0 whenever items rely on content
                // sizing (the common HUD layout pattern).
                // For a flex-container grid item with no explicit height, derive
                // the cross-axis intrinsic from the flex algorithm instead of
                // bb.Height. Pre-flex BlockLayout stacks ALL children as block
                // flow (sum-of-heights), which is correct for column-flex but
                // hugely inflates row-flex containers (e.g. chat.html composer:
                // 4 buttons stack to ~168 vs the real row-flex height ~40).
                // Using the inflated value as an auto-row hint makes the grid
                // row way too tall.
                double bbIntrinsicCross;
                if (hasExplicitH) {
                    bbIntrinsicCross = bb.Height;
                } else if (HasScrollableOverflowOnGridRowAxis(bb)) {
                    // CSS Grid L1 §6.6 + Flexbox L1 §4.5 — Automatic Minimum
                    // Size: a scroll container on the relevant axis
                    // contributes 0, not its descendants' intrinsic extent,
                    // so an auto row containing a `flex:1; overflow-y:auto`
                    // grid item (e.g. a `.hero-picker-detail`) doesn't
                    // inflate the row to the content sum and then trap the
                    // user-visible scroll viewport with zero range. Pinned
                    // by HeroPickerScrollReproTests.
                    bbIntrinsicCross = 0;
                } else if (bb is Weva.Layout.Flex.FlexBox flexBb) {
                    // FlexIntrinsicCross returns content-only inner; add the
                    // container's own vertical frame so an auto-sized grid row
                    // actually fits the flex box (e.g. `.sb-head { display:flex;
                    // padding:16px }` needs intrinsic = max(child H) + 32).
                    double vFrame = flexBb.PaddingTop + flexBb.PaddingBottom + flexBb.BorderTop + flexBb.BorderBottom;
                    bbIntrinsicCross = Weva.Layout.Positioning.PositioningPass.FlexIntrinsicCross(flexBb) + vFrame;
                } else if (bb is Weva.Layout.Grid.GridBox gridBb) {
                    // For a nested grid item without an explicit height, use
                    // its already-positioned children's bottom extent rather
                    // than `bb.Height` — which on multi-pass converges retains
                    // a stale value from an early pass (canonical case:
                    // inventory's outer `.inv-body` grid sizing `.grid`'s
                    // row track from a 6-row early-pass Height instead of
                    // the converged 5-row content, pushing the details
                    // panel 420px off-screen). Falls back to `bb.Height` on
                    // the first pass when no children are positioned yet.
                    double cross = Weva.Layout.Positioning.PositioningPass.GridIntrinsicCross(gridBb);
                    double vFrameG = gridBb.PaddingTop + gridBb.PaddingBottom + gridBb.BorderTop + gridBb.BorderBottom;
                    bbIntrinsicCross = cross > 0 ? cross + vFrameG : (bb.Height > 0 ? bb.Height : 0);
                } else {
                    // Same stale-Height hazard as the nested-grid branch
                    // above, but for PLAIN BLOCK items: bb.Height can hold
                    //   (a) the pre-grid BlockLayout value measured at the
                    //       CONTAINER's full content width — wrong whenever
                    //       the item's height is width-derived (descendant
                    //       aspect-ratio cards, wrapping text). Canonical:
                    //       advanced-dashboard `.stats` pre-laid at 1378px →
                    //       its 2-col aspect-ratio stat cards measured ~670
                    //       tall each → hint 1625 vs real content 527; or
                    //   (b) the PREVIOUS pass's STRETCHED height (align
                    //       stretch wrote row height into bb.Height), which
                    //       feeds the stretch back into the row's intrinsic
                    //       input and locks the inflated row in (the page sat
                    //       at 1820 vs Chrome's 781 across converge passes).
                    // The children's bottom extent is the content's actual
                    // height; later converge passes re-lay the item at its
                    // resolved cell width, so the hint self-corrects to the
                    // column-width measure per CSS Grid §12 ordering.
                    double extent = PlainBlockContentExtent(bb);
                    double vFrameB = bb.PaddingTop + bb.PaddingBottom + bb.BorderTop + bb.BorderBottom;
                    bbIntrinsicCross = extent > 0
                        ? extent + vFrameB
                        : (bb.Height > 0 ? bb.Height : 0);
                }
                // For the inline axis (width), explicit width takes priority,
                // otherwise probe max-content so auto / min-content tracks see
                // the item's intrinsic preferred width (e.g. `.pu-label` text
                // "Hammer" contributes ~50px to an `auto` column instead of
                // collapsing to 0). For flex/grid descendants use their own
                // intrinsic helpers (already account for items + frame).
                double bbIntrinsicMain;
                double bbIntrinsicMainMin;
                if (hasExplicitW) {
                    bbIntrinsicMain = bb.Width;
                    bbIntrinsicMainMin = bb.Width;
                } else if (bb is Weva.Layout.Flex.FlexBox flexBbM) {
                    // MaxContentWidth(FlexBox) already returns the flex
                    // container's outer max-content width, including its
                    // own horizontal padding/border.
                    bbIntrinsicMain = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(flexBbM);
                    // s6.6 automatic minimum (floors bare-fr tracks in
                    // definite containers — Phase 1c in GridTrackSizing).
                    // MinContentWidth(FlexBox) returns BORDER-box (it adds
                    // the container frame itself) — no frame add here. The
                    // measurer gives text runs their longest-word width
                    // (true min-content) instead of the laid-out line.
                    bbIntrinsicMainMin = Weva.Layout.Positioning.PositioningPass.MinContentWidth(flexBbM, MinTextMeasure);
                } else if (bb is Weva.Layout.Grid.GridBox gridBbM) {
                    // Same for nested grids: MaxContentWidth(GridBox)
                    // includes the grid container frame.
                    bbIntrinsicMain = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(gridBbM);
                    // CSS Grid L1 §5.2/§12.4 grid min-content: size the nested
                    // grid's column tracks under available=0. Fixed tracks use
                    // their fixed size; auto/fr tracks floor at items' min-content
                    // contributions. Sum + gaps + frame = the nested grid's true
                    // min-content inline size. This replaces the prior 0-stub
                    // (which under-floored — squashing was safe but not correct).
                    // The implementation is non-destructive (reads ResolvedProperties
                    // + positioned children from the just-completed nested Layout).
                    bbIntrinsicMainMin = Weva.Layout.Positioning.PositioningPass.GridMinContentWidth(gridBbM, MinTextMeasure);
                } else {
                    double hFrame = bb.PaddingLeft + bb.PaddingRight + bb.BorderLeft + bb.BorderRight;
                    bbIntrinsicMain = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bb) + hFrame;
                    bbIntrinsicMainMin = Weva.Layout.Positioning.PositioningPass.MinContentWidth(bb, MinTextMeasure) + hFrame;
                }
                // CSS Grid §6.6 / Sizing §5: an item's min-height / min-width
                // floors its contribution to an auto / content-sized track.
                // Without this, a content-or-flex item whose natural size is
                // small but whose min size is large (css-effects `.shape /
                // .mask-card { min-height: 150px }`) collapses its row track to
                // the content height — the clip-path / mask boxes render as
                // squashed slivers instead of 150px tall. Floor the intrinsic
                // hints by the resolved min sizes (px / em; percentages resolve
                // against the container content box). Skip `auto` and explicit
                // dims (already handled above).
                if (bb.Style != null) {
                    double bbFs = StyleResolver.FontSizePx(bb.Style, container.Style, ctx);
                    if (!hasExplicitH) {
                        string minHRaw = bb.Style.Get(Weva.Css.Cascade.CssProperties.MinHeightId);
                        if (!string.IsNullOrEmpty(minHRaw) && minHRaw != "auto") {
                            var mr = StyleResolver.ResolveLength(minHRaw, bb.Style, ctx, bbFs, container.ContentHeight);
                            if (mr.Kind == StyleResolver.LengthKind.Length && mr.Pixels > bbIntrinsicCross) {
                                bbIntrinsicCross = mr.Pixels;
                            }
                        }
                    }
                    if (!hasExplicitW) {
                        string minWRaw = bb.Style.Get(Weva.Css.Cascade.CssProperties.MinWidthId);
                        // K8-OPT: the zero check must run FIRST — "0"/"0px"
                        // satisfy the non-empty/!auto test below, and a
                        // resolved 0px never beats the intrinsic min, so an
                        // else-branch zero-out was dead code and
                        // `min-width: 0` silently failed to opt out of the
                        // automatic minimum (s6.6).
                        if (minWRaw == "0" || minWRaw == "0px") {
                            // explicit min-width: 0 opts OUT of the automatic
                            // minimum (s6.6) — the author asked to squash.
                            bbIntrinsicMainMin = 0;
                        } else if (!string.IsNullOrEmpty(minWRaw) && minWRaw != "auto") {
                            var mr = StyleResolver.ResolveLength(minWRaw, bb.Style, ctx, bbFs, container.ContentWidth);
                            if (mr.Kind == StyleResolver.LengthKind.Length) {
                                if (mr.Pixels > bbIntrinsicMain) bbIntrinsicMain = mr.Pixels;
                                if (mr.Pixels > bbIntrinsicMainMin) bbIntrinsicMainMin = mr.Pixels;
                            }
                        }
                    }
                }
                sizingItems.Add(new GridTrackSizing.SizingItem {
                    Box = bb,
                    Area = placement.ItemAreas[idx],
                    IntrinsicMain = bbIntrinsicMain,
                    IntrinsicMainMin = bbIntrinsicMainMin,
                    IntrinsicCross = bbIntrinsicCross
                });
            }

            // Column sizing first.
            // Per CSS Grid §11: `justify-content: normal` (the unset default)
            // behaves as `stretch` for auto-track distribution — single-auto-
            // column patterns like `display: grid; place-items: center;`
            // depend on the lone auto column stretching to fill the container
            // so place-items can center the item inside it. Explicit `start`/
            // `end`/etc. opt OUT of stretch. We distinguish "default" from
            // "explicit start" by re-reading the raw style here: an empty
            // value or `normal` is the default; anything else is explicit.
            string jcRaw = container.Style?.Get(Weva.Css.Cascade.CssProperties.JustifyContentId);
            bool jcIsDefault = string.IsNullOrEmpty(jcRaw) || jcRaw == "normal";
            bool stretchCols = jcIsDefault || props.JustifyContent == JustifyContent.Stretch;
            var colSized = GridTrackSizing.Resolve(
                colTrackArr,
                props.AutoColumns,
                placement.Columns,
                containerWidth,
                props.ColumnGap,
                sizingItems,
                isColumnAxis: true,
                autoFitCollapseEmpty: props.Columns.IsAutoFit,
                stretchAutoTracksOnRemainder: stretchCols);

            // Apply justify-content track distribution along the inline axis.
            DistributeTracks(colSized, containerWidth, props.ColumnGap, props.JustifyContent);

            // CSS Sizing L4 §5 — for grid items with `aspect-ratio` set and
            // no explicit height, the cross-axis intrinsic for ROW track
            // sizing is the now-resolved column-track width divided by the
            // ratio. Without this fixup row tracks would size to the item's
            // pre-aspect-ratio block height (often ~40 for a flex container
            // with one text child), and a 4-col grid of `aspect-ratio:1/1`
            // slots would have rows ~40-tall instead of ~400-tall — the
            // slot boxes then overflow the cell into the next row. Apply
            // BEFORE row sizing so the row track absorbs the ratio-derived
            // height as part of its intrinsic input.
            for (int idx = 0; idx < sizingItems.Count; idx++) {
                var it = sizingItems[idx];
                var bb = it.Box;
                if (bb?.Style == null) continue;
                bool hasExplicitH = HasExplicitDimension(bb, isHeight: true);
                if (hasExplicitH) continue;
                if (!StyleResolver.TryResolveAspectRatio(bb.Style, out double ratio) || ratio <= 0) continue;
                int colStart = it.Area.ColumnStart - 1;
                int colEnd = it.Area.ColumnEnd - 1;
                if (colStart < 0) colStart = 0;
                if (colEnd > colSized.Sizes.Length) colEnd = colSized.Sizes.Length;
                double colSpanWidth = 0;
                int colSpan = 0;
                for (int k = colStart; k < colEnd; k++) {
                    if (colSized.Collapsed[k]) continue;
                    colSpanWidth += colSized.Sizes[k];
                    colSpan++;
                }
                if (colSpan > 1) colSpanWidth += props.ColumnGap * (colSpan - 1);
                if (colSpanWidth <= 0) continue;
                double frameH = bb.PaddingTop + bb.PaddingBottom + bb.BorderTop + bb.BorderBottom;
                double frameW = bb.PaddingLeft + bb.PaddingRight + bb.BorderLeft + bb.BorderRight;
                // Spec default for box-sizing is content-box; route through
                // the typed parsed value so a null/unset slot maps correctly.
                // Per CSS Sizing L4 §5.4, aspect-ratio applies to the box's
                // CONTENT box when content-box and to its BORDER box when
                // border-box — different formulas:
                //   content-box: outerH = (colSpanWidth - frameW)/ratio + frameH
                //   border-box:  outerH = colSpanWidth / ratio
                // The prior code had two bugs that mostly cancelled:
                //   (1) inverted default (null→border-box, wrong);
                //   (2) tautology — both branches of `totalH` added frameH,
                //       missing the border-box "frame already inside" case.
                var bsParsed = bb.Style.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
                bool borderBox = false;
                if (bsParsed is Weva.Css.Values.CssKeyword bsK) borderBox = bsK.Identifier == "border-box";
                else if (bsParsed is Weva.Css.Values.CssIdentifier bsId) borderBox = bsId.Name == "border-box";
                else borderBox = bb.Style.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
                double totalH;
                if (borderBox) {
                    totalH = colSpanWidth / ratio;
                } else {
                    double contentW = colSpanWidth - frameW;
                    if (contentW <= 0) continue;
                    totalH = contentW / ratio + frameH;
                }
                if (totalH > it.IntrinsicCross) {
                    it.IntrinsicCross = totalH;
                    sizingItems[idx] = it;
                }
            }

            // CSS Grid §11.5 (content-based row sizing): an auto/content row's
            // height is the item's height when laid out at its RESOLVED column
            // width — not at the wider container width BlockLayout gave it
            // pre-grid. Pre-grid, a grid item with no explicit width inherits
            // the container's content width, so its text wraps at the full grid
            // width (often a single line); the row track then sizes to that
            // short height. When the item is finally placed in its (narrower)
            // column it re-flows to more lines and OVERFLOWS the under-sized row
            // (weva-landing `.feat` cards: a 3-line body painted past the card's
            // bottom border). The placement-phase re-flow at line ~1307 already
            // exists but runs AFTER row sizing — too late. Re-flow plain block
            // items at their column width HERE so the corrected height feeds the
            // row track. Gated to plain BlockBoxes with no explicit width/height
            // (flex/grid items derive their cross intrinsic via dedicated
            // helpers above) and only when the column actually narrows the item.
            if (blockLayout != null && !reflowing && !measureOnly) {
                for (int idx = 0; idx < sizingItems.Count; idx++) {
                    var it = sizingItems[idx];
                    var bb = it.Box;
                    if (bb?.Style == null) continue;
                    if (bb is Weva.Layout.Flex.FlexBox || bb is Weva.Layout.Grid.GridBox) continue;
                    if (HasExplicitDimension(bb, isHeight: true)) continue;
                    if (HasExplicitDimension(bb, isHeight: false)) continue;
                    int colStart = it.Area.ColumnStart - 1;
                    int colEnd = it.Area.ColumnEnd - 1;
                    if (colStart < 0) colStart = 0;
                    if (colEnd > colSized.Sizes.Length) colEnd = colSized.Sizes.Length;
                    double colSpanWidth = 0; int colSpan = 0;
                    for (int k = colStart; k < colEnd; k++) {
                        if (colSized.Collapsed[k]) continue;
                        colSpanWidth += colSized.Sizes[k];
                        colSpan++;
                    }
                    if (colSpan > 1) colSpanWidth += props.ColumnGap * (colSpan - 1);
                    // The item fills its column (justify default = stretch);
                    // its border-box width is the span minus its own margins.
                    double itemW = colSpanWidth - bb.MarginLeft - bb.MarginRight;
                    if (itemW <= 0) continue;
                    // Only when the column meaningfully NARROWS the item — the
                    // same one-sided threshold the placement-phase re-flow uses.
                    if (itemW >= bb.Width - LayoutEpsilons.HalfPixelEqual) continue;
                    reflowing = true;
                    try {
                        blockLayout.RelayoutContentAt(bb, itemW);
                        if (flexLayout != null) ReflowFlexDescendants(bb);
                        for (int i = 0; i < bb.Children.Count; i++) ReflowGridDescendants(bb.Children[i]);
                    } finally { reflowing = false; }
                    // bb.Height is now the content height at the column width.
                    if (bb.Height > it.IntrinsicCross) {
                        it.IntrinsicCross = bb.Height;
                        sizingItems[idx] = it;
                    }
                }
            }

            // Row sizing — uses container's content height when present, else expands to
            // fit content. We resolve initial sizes against an unconstrained container
            // (containerHeight may be 0 if height is auto). For v1, when containerHeight
            // is 0, we still compute fr distribution using 0 free space.
            // measureOnly: force the row axis indefinite so `1fr` rows size to
            // their content (the stretch-free max-content block size), never to
            // a flex/grid-assigned Height.
            double rowAxisAvailable = measureOnly ? 0 : containerHeight;
            // Mirror of justify-content above: `align-content: normal` (unset
            // default) behaves as stretch for row auto-track distribution;
            // explicit `start` / `end` / `space-*` / `center` keep tracks at
            // their intrinsic size and leave the leftover as whitespace.
            string acRaw = container.Style?.Get(Weva.Css.Cascade.CssProperties.AlignContentId);
            bool acIsDefault = string.IsNullOrEmpty(acRaw) || acRaw == "normal";
            bool stretchRows = acIsDefault || props.AlignContent == AlignContent.Stretch;
            var rowSized = GridTrackSizing.Resolve(
                rowTrackArr,
                props.AutoRows,
                placement.Rows,
                rowAxisAvailable,
                props.RowGap,
                sizingItems,
                isColumnAxis: false,
                autoFitCollapseEmpty: props.Rows.IsAutoFit,
                stretchAutoTracksOnRemainder: stretchRows);
            // Compute total row track height to drive container auto-height.
            double rowsTotal = 0;
            int activeRows = 0;
            for (int i = 0; i < rowSized.Sizes.Length; i++) {
                if (rowSized.Collapsed[i]) continue;
                rowsTotal += rowSized.Sizes[i];
                activeRows++;
            }
            double rowGaps = activeRows > 1 ? props.RowGap * (activeRows - 1) : 0;
            double rowsTotalSize = rowsTotal + rowGaps;

            if (measureOnly) {
                // Stretch-free intrinsic block size = content row tracks + frame.
                // Return before positioning any item; release the scratch slices
                // we borrowed (mirrors the cleanup tail) so the call is fully
                // non-destructive and recursion/reentrancy safe.
                double frameV = container.PaddingTop + container.PaddingBottom
                    + container.BorderTop + container.BorderBottom;
                measuredBlockSize = rowsTotalSize + frameV;
                scratch.ReturnGridSizingItemView(sizingItems);
                scratch.ReturnGridPartialView(partials);
                scratch.ReturnIntView(docOrder);
                scratch.ReturnGridItemPropsView(itemProps);
                rawItems.RemoveRange(rawStart, rawItems.Count - rawStart);
                return;
            }

            // If container height is auto, height = sum of row tracks + gaps.
            // align-content also distributes rows when there's leftover height.
            if (containerHeight <= 0) {
                rowAxisAvailable = rowsTotalSize;
            }
            DistributeTracks(rowSized, rowAxisAvailable, props.RowGap, JustifyContentFromAlign(props.AlignContent));


            // Position and size each item.
            double padLeft = container.PaddingLeft + container.BorderLeft;
            double padTop = container.PaddingTop + container.BorderTop;

            // CSS Grid L1 §9 (E7): abs-pos grid children with a definite
            // grid-placement use their resolved grid AREA as their containing
            // block — not the grid container's padding edge. Walk the
            // container's abs-pos children now that tracks are sized, resolve
            // their placement, and stamp the area rect onto the box so
            // ContainingBlockResolver.ResolveAbsolute picks it up during the
            // upcoming PositioningPass. Coordinates are LOCAL to the grid
            // container's border-box, matching cellX / cellY above.
            StampAbsPosGridAreaContainingBlocks(
                container, resolvedProps, explicitRows, explicitColumns,
                colSized, rowSized, padLeft, padTop, fs);

            for (int idx = 0; idx < rawCount; idx++) {
                int original = docOrder[idx];
                var box = rawItems[rawStart + original];
                var area = placement.ItemAreas[idx];

                // Clamp to track count.
                int cs = area.ColumnStart - 1;
                int ce = area.ColumnEnd - 1;
                int rs = area.RowStart - 1;
                int re = area.RowEnd - 1;
                if (cs < 0) cs = 0; if (rs < 0) rs = 0;
                if (ce > colSized.Sizes.Length) ce = colSized.Sizes.Length;
                if (re > rowSized.Sizes.Length) re = rowSized.Sizes.Length;

                double cellX = padLeft + (cs < colSized.Positions.Length ? colSized.Positions[cs] : 0);
                double cellY = padTop + (rs < rowSized.Positions.Length ? rowSized.Positions[rs] : 0);
                double cellW = 0, cellH = 0;
                for (int k = cs; k < ce; k++) {
                    if (colSized.Collapsed != null && colSized.Collapsed[k]) continue;
                    cellW += colSized.Sizes[k];
                    if (k > cs) cellW += props.ColumnGap;
                }
                for (int k = rs; k < re; k++) {
                    if (rowSized.Collapsed != null && rowSized.Collapsed[k]) continue;
                    cellH += rowSized.Sizes[k];
                    if (k > rs) cellH += props.RowGap;
                }

                ApplyItemAlignment(box, cellX, cellY, cellW, cellH, itemProps[original], props, containerHeightDefiniteForAspectOverflow);
                if (box is GridBox gb) {
                    // Stamp the area so a subgrid child can slice parent tracks.
                    gb.ParentPlacement = area;
                    gb.HasParentPlacement = true;
                    Layout(gb);
                }
            }

            FinalizeContainerSize(container, props, ColumnsTotalSize(colSized, props.ColumnGap), rowsTotalSize, fs);
            ShiftFollowingSiblingsIfHeightChanged(container, preGridHeight);

            // Pop our slice off the shared rawItems stack so a recursive parent
            // GridLayout frame sees its range intact.
            scratch.ReturnGridSizingItemView(sizingItems);
            scratch.ReturnGridPartialView(partials);
            scratch.ReturnIntView(docOrder);
            scratch.ReturnGridItemPropsView(itemProps);
            rawItems.RemoveRange(rawStart, rawItems.Count - rawStart);
        }

        void ShiftFollowingSiblingsIfHeightChanged(GridBox container, double preGridHeight) {
            double delta = container.Height - preGridHeight;
            BlockFlowAdjuster.PropagateHeightDelta(container, delta, ctx);
        }

        // E7 — Stamp the grid-area containing-block rect onto any abs-pos
        // child whose grid-placement properties resolve to a definite area
        // (CSS Grid L1 §9). Items WITHOUT definite placement preserve the
        // pre-cleared `HasGridAreaContainingBlock = false` so PositioningPass
        // falls through to the grid container's padding-edge CB — that
        // matches the spec for "grid placement is `auto` on at least one
        // axis". Run AFTER track sizing + DistributeTracks so positions and
        // sizes are final.
        void StampAbsPosGridAreaContainingBlocks(
            GridBox container,
            GridContainerProperties resolvedProps,
            int explicitRows,
            int explicitColumns,
            GridTrackSizing.SizedTracks colSized,
            GridTrackSizing.SizedTracks rowSized,
            double padLeft, double padTop,
            double containerFontSize) {
            double containerWidth = container.ContentWidth;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (!(c is BlockBox bb)) continue;
                if (bb.Position != PositionType.Absolute && bb.Position != PositionType.Fixed) continue;
                // Read the item's placement using the same property
                // pipeline as in-flow grid items.
                double itemFs = bb.Style != null ? StyleResolver.FontSizePx(bb.Style, container.Style, ctx) : containerFontSize;
                // H5b: thread per-item line-height (see Layout(...) for the
                // in-flow item case — out-of-flow stamping mirrors it).
                double itemLh = bb.Style != null
                    ? StyleResolver.LineHeightPx(bb.Style, itemFs, ctx, ctx.GetMetrics(bb.Style.Get(Weva.Css.Cascade.CssProperties.FontFamilyId)))
                    : 0;
                var itemLengthCtx = ctx.ToLengthContext(itemFs, containerWidth, itemLh);
                var ip = GetItemProperties(bb.Style, itemLengthCtx);
                if (!HasDefiniteGridPlacement(ip.Placement)) continue;

                var partial = GridPlacementResolver.Resolve(ip.Placement, resolvedProps,
                    explicitRows + 1, explicitColumns + 1);
                // Per CSS Grid L1 §9: a placement edge that resolves to
                // `auto` snaps to the appropriate side of the explicit grid
                // (the start line for the start edge, the end line for the
                // end edge). v1 simplification: span/auto edges fall back
                // to the explicit grid bounds. If BOTH edges on an axis are
                // unresolved we skip stamping (treated as no definite
                // placement on that axis — spec says fall back to padding
                // edge); HasDefiniteGridPlacement already filtered that.
                int rs = partial.RowStart > 0 ? partial.RowStart : 1;
                int re = partial.RowEnd > 0 ? partial.RowEnd : explicitRows + 1;
                int cs = partial.ColumnStart > 0 ? partial.ColumnStart : 1;
                int ce = partial.ColumnEnd > 0 ? partial.ColumnEnd : explicitColumns + 1;
                if (re <= rs) re = rs + 1;
                if (ce <= cs) ce = cs + 1;
                // Convert to 0-based track indices and clamp.
                int cs0 = cs - 1; int ce0 = ce - 1;
                int rs0 = rs - 1; int re0 = re - 1;
                if (cs0 < 0) cs0 = 0;
                if (rs0 < 0) rs0 = 0;
                if (ce0 > colSized.Sizes.Length) ce0 = colSized.Sizes.Length;
                if (re0 > rowSized.Sizes.Length) re0 = rowSized.Sizes.Length;
                if (cs0 >= ce0 || rs0 >= re0) continue;

                double cellX = padLeft + (cs0 < colSized.Positions.Length ? colSized.Positions[cs0] : 0);
                double cellY = padTop + (rs0 < rowSized.Positions.Length ? rowSized.Positions[rs0] : 0);
                double cellW = 0, cellH = 0;
                for (int k = cs0; k < ce0; k++) {
                    if (colSized.Collapsed != null && colSized.Collapsed[k]) continue;
                    cellW += colSized.Sizes[k];
                    if (k > cs0) cellW += resolvedProps.ColumnGap;
                }
                for (int k = rs0; k < re0; k++) {
                    if (rowSized.Collapsed != null && rowSized.Collapsed[k]) continue;
                    cellH += rowSized.Sizes[k];
                    if (k > rs0) cellH += resolvedProps.RowGap;
                }
                if (cellW < 0) cellW = 0;
                if (cellH < 0) cellH = 0;

                bb.HasGridAreaContainingBlock = true;
                bb.GridAreaContainingBlockOffsetX = cellX;
                bb.GridAreaContainingBlockOffsetY = cellY;
                bb.GridAreaContainingBlockWidth = cellW;
                bb.GridAreaContainingBlockHeight = cellH;
            }
        }

        // E7 — empty in-flow path. Mirrors the prefix of Layout (template
        // materialization + track sizing) but skips intrinsic-input gathering
        // since there are no in-flow items. Used when the grid contains only
        // abs-pos children so they still get a definite grid-area CB stamp.
        void StampAbsPosForEmptyInFlow(GridBox container, GridContainerProperties props,
                                       double containerWidth, double containerHeight, double fs) {
            (var colTrackArr, var colLineNames) = GridTrackSizing.MaterializeAutoRepeat(props.Columns, containerWidth, props.ColumnGap);
            (var rowTrackArr, var rowLineNames) = GridTrackSizing.MaterializeAutoRepeat(props.Rows, containerHeight, props.RowGap);
            int explicitRows = rowTrackArr.Length;
            int explicitColumns = colTrackArr.Length;
            if (explicitRows == 0 && props.Areas != null && props.Areas.Rows > 0) {
                explicitRows = props.Areas.Rows;
                rowTrackArr = MakeAutoTracks(explicitRows);
                rowLineNames = MakeEmptyLineNames(explicitRows);
            }
            if (explicitColumns == 0 && props.Areas != null && props.Areas.Columns > 0) {
                explicitColumns = props.Areas.Columns;
                colTrackArr = MakeAutoTracks(explicitColumns);
                colLineNames = MakeEmptyLineNames(explicitColumns);
            }
            var resolvedProps = props;
            resolvedProps.Columns = new GridTemplate(colTrackArr, colLineNames, props.Columns.IsAutoFill, props.Columns.IsAutoFit, props.Columns.AutoRepeatPattern, props.Columns.AutoRepeatLineNames);
            resolvedProps.Rows = new GridTemplate(rowTrackArr, rowLineNames, props.Rows.IsAutoFill, props.Rows.IsAutoFit, props.Rows.AutoRepeatPattern, props.Rows.AutoRepeatLineNames);

            var emptyItems = new System.Collections.Generic.List<GridTrackSizing.SizingItem>();
            var colSized = GridTrackSizing.Resolve(
                colTrackArr, props.AutoColumns, explicitColumns,
                containerWidth, props.ColumnGap, emptyItems,
                isColumnAxis: true, autoFitCollapseEmpty: false,
                stretchAutoTracksOnRemainder: false);
            DistributeTracks(colSized, containerWidth, props.ColumnGap, props.JustifyContent);
            double rowAxisAvailable = containerHeight;
            var rowSized = GridTrackSizing.Resolve(
                rowTrackArr, props.AutoRows, explicitRows,
                rowAxisAvailable, props.RowGap, emptyItems,
                isColumnAxis: false, autoFitCollapseEmpty: false,
                stretchAutoTracksOnRemainder: false);
            DistributeTracks(rowSized, rowAxisAvailable, props.RowGap, JustifyContentFromAlign(props.AlignContent));

            double padLeft = container.PaddingLeft + container.BorderLeft;
            double padTop = container.PaddingTop + container.BorderTop;
            StampAbsPosGridAreaContainingBlocks(
                container, resolvedProps, explicitRows, explicitColumns,
                colSized, rowSized, padLeft, padTop, fs);
        }

        static bool HasDefiniteGridPlacement(GridItemPlacement p) {
            if (!string.IsNullOrEmpty(p.AreaName)) return true;
            // Per CSS Grid L1 §9 the rule applies "if the grid-placement
            // properties resolve to a grid area"; in practice any explicit
            // edge (integer line / named line / span) suffices because the
            // opposite-edge `auto` snaps to the explicit-grid boundary.
            if (!p.RowStart.IsAuto) return true;
            if (!p.RowEnd.IsAuto) return true;
            if (!p.ColumnStart.IsAuto) return true;
            if (!p.ColumnEnd.IsAuto) return true;
            return false;
        }

        static GridTrackSize[] MakeAutoTracks(int n) {
            var arr = new GridTrackSize[n];
            for (int i = 0; i < n; i++) arr[i] = GridTrackSize.Auto;
            return arr;
        }

        static IReadOnlyList<IReadOnlyList<string>> MakeEmptyLineNames(int n) {
            var l = new List<IReadOnlyList<string>>(n + 1);
            for (int i = 0; i <= n; i++) l.Add(new string[0]);
            return l;
        }

        static double ColumnsTotalSize(GridTrackSizing.SizedTracks t, double gap) {
            double total = 0;
            int active = 0;
            for (int i = 0; i < t.Sizes.Length; i++) {
                if (t.Collapsed[i]) continue;
                total += t.Sizes[i];
                active++;
            }
            if (active > 1) total += gap * (active - 1);
            return total;
        }

        static JustifyContent JustifyContentFromAlign(AlignContent a) {
            switch (a) {
                case AlignContent.Start: return JustifyContent.Start;
                case AlignContent.End: return JustifyContent.End;
                case AlignContent.Center: return JustifyContent.Center;
                case AlignContent.Stretch: return JustifyContent.Stretch;
                case AlignContent.SpaceBetween: return JustifyContent.SpaceBetween;
                case AlignContent.SpaceAround: return JustifyContent.SpaceAround;
                case AlignContent.SpaceEvenly: return JustifyContent.SpaceEvenly;
            }
            return JustifyContent.Start;
        }

        static void DistributeTracks(GridTrackSizing.SizedTracks t, double available, double gap, JustifyContent mode) {
            int n = t.Sizes.Length;
            if (n == 0) return;
            int active = 0;
            double used = 0;
            for (int i = 0; i < n; i++) {
                if (t.Collapsed[i]) continue;
                used += t.Sizes[i];
                active++;
            }
            double gapsTotal = active > 1 ? gap * (active - 1) : 0;
            double total = used + gapsTotal;
            double extra = available - total;
            if (extra < 0) extra = 0;

            double offset = 0;
            double extraBetween = 0;

            if (mode == JustifyContent.Stretch && extra > 0) {
                // Distribute extra equally across tracks. Skip collapsed.
                int growable = active;
                if (growable > 0) {
                    double add = extra / growable;
                    for (int i = 0; i < n; i++) {
                        if (t.Collapsed[i]) continue;
                        t.Sizes[i] += add;
                    }
                }
            } else {
                switch (mode) {
                    case JustifyContent.Start:
                        offset = 0; break;
                    case JustifyContent.End:
                        offset = extra; break;
                    case JustifyContent.Center:
                        offset = extra * 0.5; break; // 0.5 = centering factor (NOT the HalfPixelEqual epsilon — see MN3)
                    case JustifyContent.SpaceBetween:
                        if (active > 1) extraBetween = extra / (active - 1);
                        else offset = 0;
                        break;
                    case JustifyContent.SpaceAround:
                        if (active > 0) {
                            extraBetween = extra / active;
                            offset = extraBetween * 0.5; // 0.5 = centering factor (NOT the HalfPixelEqual epsilon — see MN3)
                        }
                        break;
                    case JustifyContent.SpaceEvenly:
                        if (active >= 1) {
                            extraBetween = extra / (active + 1);
                            offset = extraBetween;
                        }
                        break;
                }
            }

            // Recompute positions.
            double cursor = offset;
            bool first = true;
            for (int i = 0; i < n; i++) {
                if (t.Collapsed[i]) {
                    t.Positions[i] = cursor;
                    continue;
                }
                if (!first) cursor += gap + extraBetween;
                t.Positions[i] = cursor;
                cursor += t.Sizes[i];
                first = false;
            }
        }

        void ApplyItemAlignment(BlockBox box, double cellX, double cellY, double cellW, double cellH,
                                GridItemProperties itemProps, GridContainerProperties containerProps,
                                bool containerHeightDefiniteForAspectOverflow) {
            JustifySelf js = itemProps.JustifySelf;
            AlignSelf als = itemProps.AlignSelf;
            if (js == JustifySelf.Auto) js = JustifySelfFromItems(containerProps.JustifyItems);
            if (als == AlignSelf.Auto) als = AlignSelfFromItems(containerProps.AlignItems);

            // Determine each axis target size. If item has no explicit width/height,
            // and the alignment is stretch, fill the cell. Otherwise honour intrinsic.
            bool widthAuto = !HasExplicitDimension(box, isHeight: false);
            bool heightAuto = !HasExplicitDimension(box, isHeight: true);

            double w = box.Width;
            double h = box.Height;

            // Track stretch decisions; flags are stamped AFTER the optional
            // reflow block below so RelayoutContentAt + ReflowFlexDescendants
            // observe flag=false and compute natural content extents (the
            // flag would otherwise short-circuit FlexLayout's collapse and
            // leave the box at the BlockLayout-stack height). Reset here
            // so a restyle that drops `align-self: stretch` clears stale
            // state from a prior layout pass.
            box.GridStretchedWidth = false;
            box.GridStretchedHeight = false;
            bool willStretchWidth = false;
            bool willStretchHeight = false;

            if (js == JustifySelf.Stretch && widthAuto) {
                w = cellW;
                willStretchWidth = true;
            } else if (js != JustifySelf.Stretch && widthAuto) {
                // CSS Grid §10.2: a non-stretch `justify-self` shrinks the item
                // to its max-content size on the inline axis (no longer fills
                // the cell). Without this branch the item kept its pre-grid
                // BlockLayout Width — typically the cell or viewport width —
                // and the alignment offset below resolved to 0, leaving e.g.
                // `.hud-br { justify-self: end }` aligned to the cell start
                // instead of the cell end.
                if (blockLayout != null && !reflowing) {
                    reflowing = true;
                    try {
                        if (LayoutEpsilons.IsHalfPixelEqual(box.ShrinkFitCachedAvail, cellW)
                            && box.ShrinkFitCachedWidth >= 0) {
                            w = box.ShrinkFitCachedWidth;
                        } else if (box is Weva.Layout.Flex.FlexBox || box is GridBox) {
                            // Flex/grid containers: MaxContentWidth already derives
                            // the intrinsic border-box width from the current child
                            // layout without calling RelayoutContentAt. Probing via
                            // RelayoutContentAt(1e6) + restore would stomp the flex
                            // positions with a block-stacked layout, and if the item
                            // width hasn't changed (w ≈ preWidth) the follow-up
                            // ReflowFlexDescendants at line 1198+ never fires to
                            // restore them — symptoms: flex items inside a
                            // justify-self:end grid cell appear in block order
                            // (stacked vertically) rather than flex order.
                            // PositioningPass.MaxContentWidth has dedicated
                            // FlexIntrinsicInline / GridIntrinsicInline branches
                            // that read child widths non-destructively.
                            double mc = PositioningPass.MaxContentWidth(box);
                            w = System.Math.Min(mc, cellW);
                            box.ShrinkFitCachedAvail = cellW;
                            box.ShrinkFitCachedWidth = w;
                        } else {
                            // Snapshot pre-probe geometry, then re-flow at
                            // snapshot width on the way out so descendants
                            // don't keep the 1e6 layout. See FlexLayout's
                            // matching probe site for the full explanation.
                            double snapshotWidth = box.Width;
                            double snapshotX = box.X;
                            try {
                                blockLayout.RelayoutContentAt(box, 1e6);
                                double frame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
                                double mc = PositioningPass.MaxContentWidth(box) + frame;
                                if (mc < frame) mc = frame;
                                w = System.Math.Min(mc, cellW);
                                box.ShrinkFitCachedAvail = cellW;
                                box.ShrinkFitCachedWidth = w;
                            } finally {
                                if (snapshotWidth > 0 && snapshotWidth < 1e5) {
                                    blockLayout.RelayoutContentAt(box, snapshotWidth);
                                } else {
                                    box.Width = snapshotWidth;
                                }
                                box.X = snapshotX;
                            }
                        }
                    } finally { reflowing = false; }
                } else {
                    // Fallback if BlockLayout isn't wired: at least clamp to the
                    // cell so we don't overflow.
                    if (w > cellW) w = cellW;
                }
            }
            // CSS Sizing L4 §5: when the item has aspect-ratio set and one
            // axis has a definite (cell-stretched or author-explicit) size,
            // the OTHER axis is fully determined by the ratio. Don't
            // additionally stretch it — that would force a non-ratio-
            // honoring box shape and (worse) cause flex children to be
            // centered relative to the cell-stretched dim BEFORE
            // ApplyAspectRatioFixupVisit shrinks the box back to the
            // ratio-derived dim, freezing them at the wrong offset.
            // Canonical case: vendor.html `.item-frame { aspect-ratio:1/1;
            // display:flex }` in an 80-px-wide grid track inside an article
            // whose row height stretches to ~224. Stretching frame.Height
            // to 224 centers the glyph at y=112, then aspect-ratio fixup
            // shrinks frame to 80 but glyph stays at 112 — visible as the
            // 72px-below-center icon offset in the diff.
            bool aspectRatioOverridesStretch = false;
            if (StyleResolver.TryResolveAspectRatio(box.Style, out double itemAspectRatio)
                && itemAspectRatio > 0) {
                // We're about to set Width via justify-self:stretch (above), or
                // width is author-explicit. Either way the inline axis will be
                // definite by the time alignment finishes. Aspect-ratio then
                // pins height; don't grid-stretch it.
                bool willHaveDefiniteWidth = willStretchWidth
                    || !widthAuto;
                if (willHaveDefiniteWidth && heightAuto) {
                    h = HeightFromAspectWidth(box, w, itemAspectRatio);
                    aspectRatioOverridesStretch = true;
                }
            }
            // CSS Sizing L4 §5.4 — inverse direction of the column→row fixup
            // that runs pre-row-sizing. When both axes are auto, aspect-ratio
            // is set, and the row track is taller than what the column-derived
            // width would yield via the ratio, derive width from the
            // row-track height. Per CSS Grid §11.7 the item is allowed to
            // overflow its column track. Canonical case: stats.html
            // `.gear-grid { grid-template-columns: repeat(4,1fr); height: 340 }`
            // with `.slot { aspect-ratio: 1/1 }` — Chrome sizes slots
            // 167×167 (row-height-derived) instead of 124×124 (column-derived).
            // Engine bug #21 tracked the missing height→width direction.
            if (containerHeightDefiniteForAspectOverflow
                && widthAuto && heightAuto && itemAspectRatio > 0
                && als == AlignSelf.Stretch && cellH > 0) {
                double arNewW = WidthFromAspectHeight(box, cellH, itemAspectRatio);
                if (arNewW > w) {
                    w = arNewW;
                    // Width is now derived from definite-height + aspect-ratio
                    // (no longer "auto"). Suppress the cell-overflow clamp
                    // below; per Grid §11.7 the item paints wider than its
                    // column track.
                    widthAuto = false;
                    willStretchWidth = false;
                    // Now that width is definite, the height should follow the
                    // stretch path (cellH) so the item fills the row track.
                    aspectRatioOverridesStretch = false;
                }
            }
            if (als == AlignSelf.Stretch && heightAuto && !aspectRatioOverridesStretch) {
                h = cellH;
                willStretchHeight = true;
            // D10 (CODE_AUDIT_FINDINGS.md): one-sided "did BlockLayout's
            // pre-grid stack already reach the cell height?" — not an equality
            // test, so it doesn't fit IsHalfPixelEqual. HalfPixelEqual is used
            // here as a directional tolerance, not a symmetric bucket.
            } else if (heightAuto && box.Height >= cellH - LayoutEpsilons.HalfPixelEqual) {
                // CSS Grid §10: non-stretch align-self + auto height should
                // size to max-content, not fill the cell. BlockLayout's
                // pre-grid pass stacked children as block flow (e.g. 5 row-
                // flex slots vertically = 195px), and FlexLayout fixed the
                // child heights afterward but didn't propagate up to this
                // ancestor. Walk children, take their POST-flex max bottom
                // edge as the intrinsic content height.
                double maxBottom = 0;
                for (int i = 0; i < box.Children.Count; i++) {
                    var c = box.Children[i];
                    // Skip out-of-flow — their height is positioned relative
                    // to ancestors, not contributing to in-flow height.
                    if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                    double cb = c.Y + c.Height + (c is BlockBox cbb ? cbb.MarginBottom : 0);
                    if (cb > maxBottom) maxBottom = cb;
                }
                double frame = box.PaddingBottom + box.BorderBottom;
                double intrinsic = maxBottom + frame;
                if (intrinsic > 0 && intrinsic < box.Height) {
                    h = intrinsic;
                }
            }

            // Constrain to cell ONLY when the size came from auto-sizing
            // (stretch / max-content). Items with an explicit author
            // `width` / `height` are allowed to overflow per CSS Grid §11.7
            // ("items that overflow their grid area are clipped by their
            // grid container's overflow"). The clamp previously fired
            // unconditionally and truncated, for example, .avatar
            // { width:96px; border:2px } from its content-box outer of
            // 100 down to the column track's 96 — visible as a 4px width
            // delta vs Chrome on stats.html.
            if (widthAuto && w > cellW) w = cellW;
            if (heightAuto && h > cellH) h = cellH;

            double offsetX = 0, offsetY = 0;
            switch (js) {
                case JustifySelf.Start: offsetX = 0; break;
                case JustifySelf.End: offsetX = cellW - w; break;
                case JustifySelf.Center: offsetX = (cellW - w) * 0.5; break; // 0.5 = centering factor (NOT the HalfPixelEqual epsilon — see MN3)
                case JustifySelf.Stretch: offsetX = 0; break;
            }
            switch (als) {
                case AlignSelf.Start: offsetY = 0; break;
                case AlignSelf.End: offsetY = cellH - h; break;
                case AlignSelf.Center: offsetY = (cellH - h) * 0.5; break; // 0.5 = centering factor (NOT the HalfPixelEqual epsilon — see MN3)
                case AlignSelf.Stretch: offsetY = 0; break;
            }
            if (offsetX < 0) offsetX = 0;
            if (offsetY < 0) offsetY = 0;

            // CSS Grid L1 §11.3 / §11.4: after alignment positions the item
            // within the cell, the item's own margins offset it from that
            // position.  Negative margins are explicitly allowed and may move
            // the item outside the cell boundary (CSS Box Model L3 §3.1).
            box.X = cellX + offsetX + box.MarginLeft;
            box.Y = cellY + offsetY + box.MarginTop;
            double preWidth = box.Width;
            box.Width = w;
            box.Height = h;
            // Re-flow whenever the cell actually shrinks the box below its
            // pre-grid width — including on the second pass after
            // PositioningPass, where the grid container has its final size
            // and cells like .hud-br (240) finally constrain children that
            // were laid out against a 1434-wide first-pass container. The
            // recursion guard (not a pass flag) prevents the hang we hit
            // earlier: nested grids could otherwise loop here through
            // RelayoutContentAt → grid → ApplyItemAlignment → RelayoutContentAt.
            // D10 (CODE_AUDIT_FINDINGS.md): one-sided "did width shrink
            // meaningfully?" test, not an equality test — doesn't fit
            // IsHalfPixelEqual. HalfPixelEqual is used here as a one-sided
            // change-threshold rather than a symmetric equality bucket.
            if (blockLayout != null && w < preWidth - LayoutEpsilons.HalfPixelEqual && !reflowing) {
                reflowing = true;
                try {
                    blockLayout.RelayoutContentAt(box, w);
                    // Re-run flex/grid on descendants so flex-positioned
                    // children (xp-footer inside .actionbar-wrap inside
                    // .hud-bot grid item) end at flex Y, not block-stack Y.
                    // Grid descendants matter too: RelayoutContentAt invokes
                    // BlockLayout on the subtree, which re-stacks any nested
                    // grid container's children as block flow and resets its
                    // Height to the BlockLayout content height. Without a grid
                    // re-layout here, e.g. `panel.unit { display: grid }` (a
                    // grandchild of `.hud-tl` flex column) keeps the BlockLayout
                    // 187px stack instead of the post-grid 106px row-track sum,
                    // and the next flex pass over `.hud-tl` reads the inflated
                    // value when summing item heights — so `.hud-tl.Height`
                    // never collapses, and the parent grid `.hud` stretches the
                    // whole row 1 to the inflated value.
                    if (flexLayout != null) ReflowFlexDescendants(box);
                    // Only re-flow grid containers strictly NESTED below
                    // `box` — the outer Layout iteration calls Layout on box
                    // itself if it's a grid, so re-running here would be
                    // redundant.
                    for (int i = 0; i < box.Children.Count; i++) ReflowGridDescendants(box.Children[i]);
                    // Re-derive intrinsic height for non-stretch align-self.
                    // RelayoutContentAt above reflowed via BlockLayout, which
                    // STACKS flex/grid children as block flow and overwrote
                    // box.Height with that inflated value (e.g. .hud-br is a
                    // plain block whose only child is `.panel.actionbar`
                    // (display:flex). BlockLayout re-stacked the 5 slots
                    // vertically and set hud-br.Height ≈ 198. The subsequent
                    // ReflowFlexDescendants fixed .panel.actionbar.Height back
                    // to one row (49) but did NOT propagate up to hud-br,
                    // because hud-br is not a flex/grid container itself).
                    // Without this re-derivation, hud-br kept the 198 height
                    // and fed it back into pass-2 row-track sizing as the
                    // intrinsic cross-axis hint, inflating row 3 from 125 to
                    // 199 and dragging hud-bot up by 74px.
                    if (heightAuto && als != AlignSelf.Stretch) {
                        double maxBottomPost = 0;
                        for (int i = 0; i < box.Children.Count; i++) {
                            var c = box.Children[i];
                            if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                            double cb = c.Y + c.Height + (c is BlockBox cbb ? cbb.MarginBottom : 0);
                            if (cb > maxBottomPost) maxBottomPost = cb;
                        }
                        double framePost = box.PaddingBottom + box.BorderBottom;
                        double intrinsicPost = maxBottomPost + framePost;
                        if (intrinsicPost > 0 && intrinsicPost < box.Height) {
                            box.Height = intrinsicPost;
                            // Re-resolve the align-self offset against the new
                            // height so end/center alignment lands at the cell
                            // edge after the shrink.
                            double newOffsetY = 0;
                            switch (als) {
                                case AlignSelf.Start: newOffsetY = 0; break;
                                case AlignSelf.End: newOffsetY = cellH - box.Height; break;
                                case AlignSelf.Center: newOffsetY = (cellH - box.Height) * 0.5; break; // 0.5 = centering factor (NOT the HalfPixelEqual epsilon — see MN3)
                            }
                            if (newOffsetY < 0) newOffsetY = 0;
                            box.Y = cellY + newOffsetY + box.MarginTop;
                        }
                    }
                }
                finally { reflowing = false; }
                // RelayoutContentAt re-stacks children via BlockLayout and
                // overwrites box.Height with the content-stack height. When
                // align-self is stretch and we explicitly assigned the cell
                // height above, that assignment is the authoritative size —
                // restore it so the nested Layout(gb) call below (and any
                // downstream sizing reads) sees the stretched height, and so
                // the GridStretchedHeight flag stamp at the bottom of this
                // method actually fires. Without this restore, a grid item
                // with no explicit height (e.g. `.thread { display:grid;
                // grid-template-rows: auto 1fr auto }` inside a `display:grid;
                // height:100vh` parent with no template rows) ends up at its
                // pre-grid BlockLayout stack height instead of filling the
                // implicit row, and its inner 1fr row collapses.
                if (willStretchHeight && !LayoutEpsilons.IsHalfPixelEqual(box.Height, h)) {
                    box.Height = h;
                }
                // Mirror of the height restore above. ReflowFlexDescendants
                // runs FlexLayout on a column-flex stretch-aligned descendant
                // (e.g. match3 .objectives, an `aside` with no explicit width
                // inside a 220px grid track). Its FinalizeContainerCrossSize
                // collapses Width to max(child width) because the
                // GridStretchedWidth flag is still false at that point (the
                // flag stamp at the end of THIS method hasn't run yet). The
                // post-reflow Width drift then prevents the flag stamp from
                // firing too (cellW=220 vs shrunk Width=186 fail the within-0.5
                // check), and every subsequent flex pass re-shrinks the cross
                // axis — observed: .objectives goes 220→186, .goal child fills
                // to 152, .goal-bar flex:1 free space drops from 103 to 69.
                if (willStretchWidth && !LayoutEpsilons.IsHalfPixelEqual(box.Width, w)) {
                    box.Width = w;
                }
            }

            // Stamp stretch flags AFTER any reflow (which may have re-set
            // box.Width/Height via BlockLayout/FlexLayout). FlexLayout's
            // FinalizeContainerCrossSize consults these to suppress the
            // "shrink to widest child" collapse for flex containers that
            // sit inside a grid cell with stretch alignment — e.g.
            // `.hud-tr { display:flex; flex-direction:column;
            // align-items:flex-end }` would otherwise re-shrink from the
            // 311px cell width back to its widest child's 200px every
            // RunFlexPasses. Only set the flag when this finalize actually
            // produced the cell-sized box (post-reflow box.Width/Height
            // matches cellW/H within tolerance). If the reflow's
            // intrinsic-height re-derivation shrunk the box below cellH,
            // the box is no longer cell-sized and the flag stays clear.
            if (willStretchWidth && LayoutEpsilons.IsHalfPixelEqual(box.Width, cellW)) {
                box.GridStretchedWidth = true;
                box.GridStretchedCellWidth = cellW;
            }
            if (willStretchHeight && LayoutEpsilons.IsHalfPixelEqual(box.Height, cellH)) {
                box.GridStretchedHeight = true;
                box.GridStretchedCellHeight = cellH;
            }
        }

        // CSS Grid L1 §6.6 + Flexbox L1 §4.5 — when a grid item is a scroll
        // container on the row axis (vertical), its automatic minimum
        // contribution to row sizing is zero. The grid auto-track sizing
        // would otherwise inflate the row to fit the item's intrinsic
        // descendant extent, defeating the scroll. v1: only treat the row
        // axis here; the column-axis dual is left for a follow-up because
        // intrinsic width derivation is already capped by `MaxContentWidth`
        // which doesn't recurse into scrollable subtrees the same way.
        // True when the given flex parent's main axis is column AND the
        // parent itself has a definite main-axis size (explicit height,
        // pinned by positioning, or aspect-derived). Used by the
        // containerHeightDefinite check above to recognise a grid
        // container that's a flex item whose Height was assigned by the
        // parent flex's `flex: <n>` distribution rather than by author CSS.
        static bool IsFlexParentDefiniteMainColumn(Weva.Layout.Flex.FlexBox flexParent) {
            if (flexParent?.Style == null) return false;
            string fd = flexParent.Style.Get(CssProperties.GetId("flex-direction"));
            bool isColumn = fd == "column" || fd == "column-reverse";
            if (!isColumn) return false;
            string h = flexParent.Style.Get("height");
            if (!string.IsNullOrEmpty(h) && h != "auto") return true;
            // A definite min-height makes the column-flex parent's main size
            // definite too (it floors the container and flex-grow distributes
            // the floored space — mirrors FlexLayout.HasDefiniteMain). Without
            // this, a `min-height:100vh` shop's `flex:1` grid child treats its
            // flex-assigned Height as indefinite and its `grid-auto-rows:1fr`
            // rows collapse to content instead of filling the viewport.
            string mh = flexParent.Style.Get("min-height");
            if (!string.IsNullOrEmpty(mh) && mh != "auto" && mh != "0" && mh != "0px") return true;
            if (flexParent.Position == PositionType.Fixed || flexParent.Position == PositionType.Absolute) {
                if (flexParent.OffsetTop.HasValue && flexParent.OffsetBottom.HasValue) return true;
            }
            return false;
        }

        // Returns true when the box is a SCROLL CONTAINER on the row axis
        // (overflow-y or shorthand `overflow` is `auto`, `scroll`, or `overlay`).
        // CSS Grid L1 §6.6 + Flexbox L1 §4.5: scroll containers contribute 0
        // to auto-row intrinsic sizing so content inside a scrollable cell
        // does not inflate the row track (the user scrolls instead).
        //
        // NOTE: `overflow:hidden` is deliberately EXCLUDED. Hidden overflow
        // creates a clipping container, NOT a scroll container — the content
        // is simply clipped, not scrollable, so its intrinsic height is still
        // the correct auto-row hint. Erroneously including `hidden` here caused
        // row-flex grid items with `overflow:hidden` (e.g. a segmented-control
        // border-radius clip) to contribute IntrinsicCross=0 and have the row
        // track sized only by sibling items — collapsing the track to the
        // shorter sibling and visually clipping the flex children.
        static bool HasScrollableOverflowOnGridRowAxis(BlockBox bb) {
            if (bb?.Style == null) return false;
            string oy = bb.Style.Get(CssProperties.OverflowYId);
            if (string.IsNullOrEmpty(oy) || oy == "visible") oy = bb.Style.Get(CssProperties.OverflowId);
            return oy == "auto" || oy == "scroll" || oy == "overlay";
        }

        static double HeightFromAspectWidth(BlockBox box, double width, double ratio) {
            if (box == null || ratio <= 0 || width <= 0) return 0;
            if (UsesBorderBoxForAspectRatio(box)) return width / ratio;
            double frameW = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            double frameH = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            double contentW = width - frameW;
            return contentW > 0 ? contentW / ratio + frameH : 0;
        }

        static double WidthFromAspectHeight(BlockBox box, double height, double ratio) {
            if (box == null || ratio <= 0 || height <= 0) return 0;
            if (UsesBorderBoxForAspectRatio(box)) return height * ratio;
            double frameW = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            double frameH = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            double contentH = height - frameH;
            return contentH > 0 ? contentH * ratio + frameW : 0;
        }

        // A definite min-height floors the grid container's block size. Returns
        // the BORDER-BOX min-height px (0 when none / auto / 0 / indefinite),
        // honouring box-sizing for the content-box case. Percentage min-height
        // is treated as indefinite here (v1); fixed lengths cover the common
        // case (e.g. `.g.tall { min-height: 150px }`). Used so align-content has
        // free space and FinalizeContainerSize doesn't collapse below min-height
        // when `height` is auto.
        double ResolveContainerMinHeightBorderBox(GridBox container, double fs) {
            if (container.Style == null) return 0;
            string raw = container.Style.Get(Weva.Css.Cascade.CssProperties.MinHeightId);
            if (string.IsNullOrEmpty(raw) || raw == "auto" || raw == "0" || raw == "0px") return 0;
            var r = StyleResolver.ResolveLength(raw, container.Style, ctx, fs, null);
            if (r.Kind != StyleResolver.LengthKind.Length || r.Pixels <= 0) return 0;
            if (UsesBorderBoxForAspectRatio(container)) return r.Pixels;
            double frameV = container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;
            return r.Pixels + frameV;
        }

        static bool UsesBorderBoxForAspectRatio(BlockBox box) {
            var parsed = box.Style?.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
            if (parsed is Weva.Css.Values.CssKeyword k) return k.Identifier == "border-box";
            if (parsed is Weva.Css.Values.CssIdentifier id) return id.Name == "border-box";
            return box.Style?.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
        }

        void ReflowFlexDescendants(Box parent) {
            if (parent == null) return;
            // Depth-first — children before container, mirroring RunFlexPasses.
            for (int i = 0; i < parent.Children.Count; i++) ReflowFlexDescendants(parent.Children[i]);
            if (parent is Weva.Layout.Flex.FlexBox fb) flexLayout.Layout(fb);
        }

        // Mirror of ReflowFlexDescendants but for nested grid containers.
        // Depth-first (children first) so a grid descendant's child grid is
        // settled before this grid's algorithm consults its size, matching
        // RunGridPasses. The `reflowing` recursion guard set by the caller
        // (ApplyItemAlignment) protects against ApplyItemAlignment → Layout →
        // ApplyItemAlignment → … infinite descent because every nested Layout
        // re-uses the same instance and `reflowing` is sticky for the duration
        // of the outer ApplyItemAlignment.
        void ReflowGridDescendants(Box parent) {
            if (parent == null) return;
            for (int i = 0; i < parent.Children.Count; i++) ReflowGridDescendants(parent.Children[i]);
            if (parent is GridBox gb) {
                bool savedReflowing = reflowing;
                reflowing = false;
                try { Layout(gb); }
                finally { reflowing = savedReflowing; }
            }
        }

        // Re-flow recursion guard — see ApplyItemAlignment.
        bool reflowing;
        // Retained for callers that still set the flag; no longer consulted
        // in the re-flow gate. Safe to remove once LayoutEngine stops calling.
        bool inSecondPass;
        public void SetInSecondPass(bool v) { inSecondPass = v; }

        static JustifySelf JustifySelfFromItems(JustifyItems ji) {
            switch (ji) {
                case JustifyItems.Start: return JustifySelf.Start;
                case JustifyItems.End: return JustifySelf.End;
                case JustifyItems.Center: return JustifySelf.Center;
                case JustifyItems.Stretch: return JustifySelf.Stretch;
            }
            return JustifySelf.Stretch;
        }
        static AlignSelf AlignSelfFromItems(AlignItems ai) {
            switch (ai) {
                case AlignItems.Start: return AlignSelf.Start;
                case AlignItems.End: return AlignSelf.End;
                case AlignItems.Center: return AlignSelf.Center;
                case AlignItems.Stretch: return AlignSelf.Stretch;
            }
            return AlignSelf.Stretch;
        }

        // Longest-word width of a text run — the run's true MIN-content
        // inline size (CSS Sizing 3 s5.1; soft-wrap opportunities at spaces;
        // CJK per-char breaking is approximated by the space rule here, an
        // accepted under-floor). Uses the same metrics/font-size the run was
        // measured with at layout time.
        double MinTextMeasure(TextRun run) {
            string text = run?.Text;
            if (string.IsNullOrEmpty(text)) return 0;
            var metrics = ctx.GetMetrics(run.Style?.Get(Weva.Css.Cascade.CssProperties.FontFamilyId));
            if (metrics == null) return run.Width;
            double fs = run.FontSize > 0 ? run.FontSize : 16;
            double best = 0;
            int wordStart = -1;
            for (int i = 0; i <= text.Length; i++) {
                bool atSpace = i == text.Length || char.IsWhiteSpace(text[i]);
                if (!atSpace) { if (wordStart < 0) wordStart = i; continue; }
                if (wordStart >= 0) {
                    double w = metrics.Measure(text, wordStart, i - wordStart, fs);
                    if (w > best) best = w;
                    wordStart = -1;
                }
            }
            return best;
        }

        static bool HasExplicitDimension(BlockBox box, bool isHeight) {
            if (box.Style == null) return false;
            string raw = box.Style.Get(isHeight ? "height" : "width");
            if (string.IsNullOrEmpty(raw)) return false;
            if (raw == "auto") return false;
            return true;
        }

        // Children's vertical content span of a plain block grid item — the
        // mirror of PositioningPass.GridIntrinsicCross for non-flex/grid
        // boxes. Used as the row-track intrinsic hint instead of bb.Height,
        // which can hold a width-stale or stretch-stale value (see the call
        // site). Skips out-of-flow children; includes in-flow margins.
        static double PlainBlockContentExtent(BlockBox box) {
            double minT = double.PositiveInfinity, maxB = double.NegativeInfinity;
            int n = 0;
            for (int i = 0; i < box.Children.Count; i++) {
                var c = box.Children[i];
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

        void FinalizeContainerSize(GridBox container, GridContainerProperties props, double colsTotal, double rowsTotal, double fs) {
            if (container.Style == null) return;

            string heightRaw = container.Style.Get("height");
            var heightR = StyleResolver.ResolveLength(heightRaw, container.Style, ctx, fs, null);
            if (heightR.Kind != StyleResolver.LengthKind.Length) {
                // Don't clobber a definite height that PositioningPass already
                // wrote (e.g. `.hud { position: fixed; inset: 0 }` is sized to
                // viewport between the first and second flex/grid passes).
                // Without this guard the second-pass GridLayout.Layout reads
                // ContentHeight from a content-derived value and `1fr` rows
                // collapse to 0, so the bottom row of a `auto 1fr auto`
                // template ends up immediately below the top row instead of
                // pinned to the viewport bottom.
                bool pinnedByPositioning =
                    (container.Position == PositionType.Fixed || container.Position == PositionType.Absolute)
                    && container.OffsetTop.HasValue && container.OffsetBottom.HasValue
                    && container.Height > 0;
                // CSS Sizing L4 §5: aspect-ratio + definite width gives a
                // definite height. BlockLayout.FinalizeBlockSize already
                // derived it correctly. Don't clobber that with grid's
                // content-derived rowsTotal — `.portrait { width: 37px;
                // aspect-ratio: 1; display: grid }` was collapsing to h=2
                // because grid found only the icon glyph as content.
                bool aspectDerived =
                    StyleResolver.TryResolveAspectRatio(container.Style, out double ar)
                    && ar > 0 && container.Width > 0 && container.Height > 0;
                // Mirror of the heightIsAuto guard at the top of Layout: when
                // the parent grid stretched us to fill its cell, the assigned
                // Height is the authoritative box size — don't overwrite it
                // with rowsTotal + frame (which would either drift below the
                // cell or, when the box has padding/border, add the frame on
                // TOP of the cell-sized rowsTotal and overflow the cell).
                bool gridStretched = container.GridStretchedHeight && container.Height > 0;
                // Mirror the row-sizing carve-out at the top of Layout: a grid
                // stretched by a definite-main column-flex parent (explicit
                // height OR min-height) keeps its flex-assigned Height — its 1fr
                // rows were already sized to fill it. Recomputing rowsTotal+frame
                // here would collapse it back to content (gold-shop packs).
                bool flexParentStretched = container.Parent is Weva.Layout.Flex.FlexBox flexParent2
                    && container.Height > 0
                    && IsFlexParentDefiniteMainColumn(flexParent2);
                if (!pinnedByPositioning && !aspectDerived && !gridStretched && !flexParentStretched) {
                    // Container.Height is the border-box; add the frame on top of
                    // the row-track sum regardless of `box-sizing` (the author's
                    // `height` declaration, when present, is handled by the
                    // sibling Length branch above where box-sizing is honored).
                    double frame = container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;
                    container.Height = rowsTotal + frame;
                    // Don't collapse below a definite min-height (border-box) —
                    // align-content distributed the rows within the min-height
                    // floor above, so the container must keep that height.
                    double minHBB = ResolveContainerMinHeightBorderBox(container, fs);
                    if (minHBB > container.Height) container.Height = minHBB;
                }
            }
        }
    }
}
