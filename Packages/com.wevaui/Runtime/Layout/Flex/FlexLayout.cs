// FlexLayout — formatting context for `display: flex` / `display: inline-flex`.
//
// Dispatch site: BoxBuilder.cs constructs a `FlexBox` for elements whose
// computed display is flex / inline-flex (search BoxBuilder.cs for
// "NewBlockBoxFor"). LayoutEngine.cs runs a post-pass after BlockLayout
// (RunFlexPasses) that walks the box tree depth-first and invokes
// FlexLayout.Layout on every FlexBox.
//
// Algorithm follows CSS Flexible Box Module Level 1 (simplified for v1).
//
// v1 simplifications (documented in PLAN.md):
//   - `min-content` / `max-content` / `fit-content` keyword sizes treated as `auto`.
//   - `aspect-ratio` IS honoured for auto heights (ApplyAspectRatioFromWidth
//     re-derives after stretch/grow); row-flex `baseline` uses real item
//     baselines; column flexes synthesise per spec (cross-start edge).
//   - Item min/max main-size constraints not iteratively resolved during flex
//     (single pass).
//   - Percentage `flex-basis` resolves against the container's resolved main size,
//     no definite/indefinite distinction.
//   - When flex sets a child's main size to a value different from its style.width,
//     the box's Width/Height is overwritten but the child's interior is NOT
//     re-flowed at the new size (text inside flex children stays wrapped at the
//     BlockLayout-computed pre-flex width).
//   - `align-self: stretch` only stretches when the item has no explicit
//     cross-axis dimension set in its style.
//   - Explicit longhand `flex-grow: 0` / `flex-shrink: 1` / `flex-basis: auto`
//     are treated as their initial values and do not override a user-set `flex`
//     shorthand (no per-property explicit-vs-initial tracking in ComputedStyle).
//   - Text directly inside a flex container is wrapped in the normal anonymous-
//     block flow rather than an anonymous flex item.

using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
// `Item` is a using-alias for the per-engine pooled FlexLayoutItem so the
// algorithm body stays readable; behaviour and field-set are identical.
using Item = Weva.Layout.FlexLayoutItem;

namespace Weva.Layout.Flex {
    internal sealed class FlexLayout {
        // Minimum absolute epsilon (in CSS px) for the wrap-boundary tolerance in
        // CollectLines. The wrap threshold is `containerMainSize * LayoutNoise`
        // clamped up to this floor so small containers (and the degenerate
        // 0-size case) still tolerate ordinary layout-jitter without spuriously
        // wrapping. The 0.01px floor is two orders of magnitude below half a
        // CSS px, so an intentional 0.5px overflow still trips the wrap. See
        // K4 in CSS_COMPLIANCE_ISSUES.md for the motivating jitter scenario.
        // Distinct from LayoutEpsilons.LayoutNoise (the relative scale) — this
        // is the absolute floor.
        const double FlexWrapEpsilonMinPx = 0.01;

        // Pass-reuse invariant: constructed once per LayoutEngine. The
        // LayoutScratch holds every per-pass List<>/Stack<> we use; everything
        // is appended-then-popped within a single Layout() call (stack
        // discipline), and the scratch itself is engine-stable. Reset() is the
        // only place ctx changes between calls.
        LayoutContext ctx;
        readonly LayoutScratch scratch;
        // Re-flow hook: when a flex cross-axis clamp or stretch reduces a
        // column-flex item's width below its pre-flex value (BlockLayout
        // sized it against the body width before the flex container shrank),
        // invoke blockLayout.RelayoutContentAt so the item's descendants
        // re-flow at the new width. Same pattern as GridLayout.SetBlockLayout.
        BlockLayout blockLayout;
        // Set by LayoutEngine so ComputeBaseSize can ask a grid child for its
        // stretch-free max-content block size (the correct column-flex base of a
        // grid item — see ComputeBaseSize / GridLayout.MaxContentBlockSize).
        Weva.Layout.Grid.GridLayout gridLayout;
        bool reflowing;
        // Set true by LayoutEngine ONLY before the third RunFlexPasses call
        // (after PositioningPass + the second grid pass have written final
        // grid-cell heights into stretch-aligned column-flex containers).
        // Consulted in FinalizeContainerMainSize so that pass — and only
        // that pass — preserves a grid-stamped Height instead of collapsing
        // it back to the sum of intrinsic item heights. Earlier passes still
        // collapse so grid pass 1/2 receives an accurate intrinsic for row-
        // track sizing (locking the height in pass 1 inflates row 1 to the
        // pre-flex BlockLayout-stack value and regresses overall score).
        bool inThirdPass;
        public void SetInThirdPass(bool v) { inThirdPass = v; }

        public FlexLayout(LayoutScratch scratch) {
            this.scratch = scratch;
        }

        internal void SetBlockLayout(BlockLayout bl) { this.blockLayout = bl; }
        internal void SetGridLayout(Weva.Layout.Grid.GridLayout gl) { this.gridLayout = gl; }

        public void Reset(LayoutContext ctx) {
            this.ctx = ctx;
        }

        // After FlexLayout assigns a new width to a flex item (stretch cross,
        // shrink/grow main), re-derive the item's height from its
        // aspect-ratio if `height` is auto. BlockLayout already does this in
        // FinalizeBlockSize on its initial pass, but that pass runs BEFORE
        // FlexLayout has stretched/shrunk the item — at that point the item's
        // width is content-derived (often 0 for a `width:100%` flex item
        // whose parent's width isn't known yet). Without this fixup, an
        // author writing `.portrait-frame { width: 100%; aspect-ratio: 3/4 }`
        // gets a content-derived height instead of width/ratio.
        static void ApplyAspectRatioFromWidth(BlockBox box) {
            if (box?.Style == null) return;
            string heightRaw = box.Style.Get(CssProperties.HeightId);
            if (!string.IsNullOrEmpty(heightRaw) && heightRaw != "auto") return;
            if (!StyleResolver.TryResolveAspectRatio(box.Style, out double ratio) || ratio <= 0) return;
            if (box.Width <= 0) return;
            double derived = box.Width / ratio;
            // The box's height includes its own padding/border frame (border-box
            // is the rendered geometry). v1 simplification mirrors BlockLayout's
            // FinalizeBlockSize aspect-ratio branch: ratio applies to content
            // area; add the vertical frame so the rendered box is the
            // content-box-derived size + frame.
            bool borderBox = IsBorderBox(box.Style);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            box.Height = borderBox ? derived : derived + frame;
        }

        // Mirror of BlockLayout.IsBorderBox — kept local to avoid widening
        // BlockLayout's surface for a hot-path helper.
        static bool IsBorderBox(Weva.Css.Cascade.ComputedStyle style) {
            if (style == null) return false;
            var v = style.GetParsed(Weva.Css.Cascade.CssProperties.BoxSizingId);
            if (v is Weva.Css.Values.CssKeyword k) return k.Identifier == "border-box";
            if (v is Weva.Css.Values.CssIdentifier id) return id.Name == "border-box";
            return style.Get(Weva.Css.Cascade.CssProperties.BoxSizingId) == "border-box";
        }

        // Symmetric helper for the row-flex stretch path that assigns
        // it.Box.Height = itemCross. Width derived from height * ratio when
        // `width` is auto and aspect-ratio is set.
        static void ApplyAspectRatioFromHeight(BlockBox box) {
            if (box?.Style == null) return;
            string widthRaw = box.Style.Get(CssProperties.WidthId);
            if (!string.IsNullOrEmpty(widthRaw) && widthRaw != "auto") return;
            if (!StyleResolver.TryResolveAspectRatio(box.Style, out double ratio) || ratio <= 0) return;
            if (box.Height <= 0) return;
            double derived = box.Height * ratio;
            bool borderBox = IsBorderBox(box.Style);
            double frame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            box.Width = borderBox ? derived : derived + frame;
        }

        void ReflowIfShrunk(BlockBox box, double newW, double preW) {
            if (blockLayout == null || reflowing) return;
            if (newW >= preW - LayoutEpsilons.HalfPixelEqual) return;
            if (CanKeepCurrentLayoutForShrink(box, newW)) {
                RetargetAnonymousBlockWidths(box);
                // text-align (and inherited variants like the UA `button`
                // rule's `text-align: center`) computes line offsets against
                // the container's ContentWidth at InlineLayout time. When
                // shrink optimisation skips relayout, the container's Width
                // changes (preW → newW) but any previously-stamped
                // OffsetLine delta on the LineBox is now stale. Symptom:
                // `<button display:flex; justify-content:center>` with an
                // inherited `text-align:center` renders its label TEXT at
                // the centre of the PRE-shrink width — visibly past the
                // shrunk label box. Re-apply the alignment now using the
                // new ContentWidth so the deltas match the final geometry.
                ReapplyTextAlignOnLines(box);
                return;
            }
            reflowing = true;
            try {
                blockLayout.RelayoutContentAt(box, newW);
                // BlockLayout re-stacks ALL descendants as block flow, including
                // nested flex containers (e.g. column-flex `unit-meta` shrinks
                // its `.auras` row-flex child to the cell width and triggers
                // this. RelayoutContentAt then re-stacks .auras's icon children
                // vertically, inflating .auras Height to 4× icon height. The
                // outer unit-meta column-flex Layout then sums that inflated
                // child height into its own Height — a 56px overshoot per
                // unit-frame in the randhtml fixture). Walk the subtree
                // depth-first and re-run FlexLayout on every FlexBox so its
                // line layout is restored before the caller reads child
                // dimensions for cross/main-axis sizing. The `reflowing`
                // recursion guard above is sticky for the duration of this
                // method, so a nested ReflowIfShrunk inside the re-run won't
                // re-enter (which would be redundant: BlockLayout already
                // shrunk the subtree against newW).
                ReflowFlexDescendants(box);
            }
            finally { reflowing = false; }
        }

        void ReflowFlexDescendants(Box parent) {
            if (parent == null) return;
            // Depth-first — children before container, mirroring RunFlexPasses
            // and GridLayout.ReflowFlexDescendants.
            for (int i = 0; i < parent.Children.Count; i++) ReflowFlexDescendants(parent.Children[i]);
            if (parent is FlexBox fb) Layout(fb);
        }

        public void Layout(FlexBox container) {
            if (container == null) return;

            // Snapshot pre-layout height. BlockLayout's pre-flex pass sized this
            // FlexBox by stacking its children as block-flow (sum of heights). For
            // a row-flex with multiple items, the actual flex-resolved height is
            // max(item cross-sizes) — strictly less than the stacked sum. The
            // parent BlockLayout already advanced its cursor based on the inflated
            // pre-flex height, so following block-flow siblings sit too far down.
            // After flex sizing finalizes (FinalizeContainerCrossSize /
            // FinalizeContainerMainSize), shift those siblings up by the delta so
            // the parent's block stack collapses around the corrected height.
            // Canonical case: `.panel.quest > .obj` is a row-flex container with a
            // bullet ::before + a `<b>` child; BlockLayout sized .obj at H=30
            // (14+14+pad), flex shrinks it to H=16, but `.obj.done` (next sibling
            // of .quest) was placed at Y=46 based on H=30 — visible as a 14px
            // phantom gap before .obj.done in the headless dump.
            double preFlexHeight = container.Height;

            double fs = StyleResolver.FontSizePx(container.Style, container.Parent?.Style, ctx);
            bool isRow = ContainerIsRow(container);
            double containerMainSize = isRow ? container.ContentWidth : container.ContentHeight;
            // Floor the main size by a definite min-height (column) / min-width
            // (row) so flex-grow distributes the floored free space (Chrome
            // stretches the children to the floor). ContentHeight can lag the
            // container's own min-height clamp — BlockLayout.FinalizeBlockSize
            // applies it AFTER this pass — so derive the floor directly here.
            // `!isRow` selects the MAIN axis in MinCrossFloorContent.
            double mainMinFloor = MinCrossFloorContent(container, fs, !isRow);
            if (mainMinFloor > containerMainSize) containerMainSize = mainMinFloor;
            double containerCrossSize = isRow ? container.ContentHeight : container.ContentWidth;
            // CSS Sizing L4 §5: when the cross axis is `auto` but `aspect-ratio`
            // + a definite main axis are set, the cross size is fully
            // determined by the ratio. The aspect-ratio fixup walk in
            // LayoutEngine.Layout runs AFTER this flex pass, so without
            // deriving the cross here, single-line flex containers like
            // `.portrait-frame { width:100%; aspect-ratio:3/4; display:flex;
            // align-items:center }` distribute lines against an intrinsic
            // (pre-ratio) container cross size — the child glyph then has no
            // free space and `align-items: center` pins it to the top instead
            // of centering it in the eventual aspect-ratio-derived height.
            string crossRaw = isRow ? container.Style?.Get(CssProperties.HeightId) : container.Style?.Get(CssProperties.WidthId);
            // When the cross axis is `min-height` (auto height floored by
            // min-height) the single flex line must fill that floored cross so
            // align-items / align-content have free space to distribute —
            // otherwise the line collapses to content and items pin to the
            // cross-start. Treat the min-floored cross as definite, mirroring
            // the aspect-ratio derivation below. CSS Box Sizing L3 §5.
            bool crossFlooredByMin = false;
            if (string.IsNullOrEmpty(crossRaw) || crossRaw == "auto") {
                double derived = TryDeriveCrossFromAspectRatio(container, isRow);
                if (derived > containerCrossSize) containerCrossSize = derived;

                // A positive min-* is an active definite cross constraint: the
                // line must fill at least it. Trip the flag whenever it's set
                // (not only when it currently exceeds containerCrossSize) — on a
                // re-layout pass ContentHeight already equals the floor, and a
                // strict `>` would drop the flag and re-pin items to the
                // cross-start. Bump the cross up to the floor when needed.
                double minFloor = MinCrossFloorContent(container, fs, isRow);
                if (minFloor > 0) {
                    if (minFloor > containerCrossSize) containerCrossSize = minFloor;
                    crossFlooredByMin = true;
                }
            }

            // H5b: thread the container's resolved line-height so `lh`-typed
            // gap / flex-basis declarations on the flex container resolve
            // against the cascaded line-height rather than the 1.2 * fs
            // fallback.
            double containerLh = StyleResolver.LineHeightPx(container.Style, fs, ctx, ctx.GetMetrics(container.Style?.Get(CssProperties.FontFamilyId)));
            var lengthCtx = ctx.ToLengthContext(fs, containerMainSize, containerLh);
            // CSS Box Alignment L3 §8.3 (E2): row-gap percentages resolve
            // against the container's block-axis size (height in horizontal
            // writing modes); column-gap against the inline-axis size (width).
            // Pass both explicitly — lengthCtx.BasisPixels is the main-axis
            // basis (used for flex-basis / font-relative calc), which is
            // height in column-direction and doesn't match the inline axis.
            // An indefinite axis (zero ContentWidth/ContentHeight) yields a
            // 0 gap per spec — matched by ResolveGap's no-basis fallback.
            double? inlineBasis = container.ContentWidth > 0 ? container.ContentWidth : (double?)null;
            double? blockBasis = container.ContentHeight > 0 ? container.ContentHeight : (double?)null;
            var props = FlexProperties.From(container.Style, lengthCtx, inlineBasis, blockBasis);

            // Native <button> (and button-like <input>) renders its content
            // CENTERED on the main axis as well as the cross axis (Chrome's UA
            // wraps the content in a centered anonymous box). We model the
            // cross-axis centering with the UA `align-items: center`, but the
            // UA deliberately does NOT set `justify-content: center` — doing so
            // bled through to author `display: flex` button overrides and
            // centered icon rows that should pack flex-start (the
            // hero-picker bleed incident; see UserAgentStylesheet.cs). Chrome avoids
            // that because the special centering only applies to the DEFAULT
            // button rendering, not when the author changes `display`.
            //
            // Reproduce that distinction here: when the box is a button-like
            // element STILL at its UA-default `display: inline-flex` and the
            // author left `justify-content` unset (initial `normal`), pack the
            // content to center. A plain `width: 140px` button then centers its
            // label like Chrome, while `button { display: flex }` (raw display
            // != inline-flex) keeps the spec-default flex-start.
            if (props.JustifyContent == JustifyContent.FlexStart && IsButtonLike(container.Element)) {
                string rawDisplay = container.Style.Get(Weva.Css.Cascade.CssProperties.DisplayId);
                string rawJustify = container.Style.Get(Weva.Css.Cascade.CssProperties.JustifyContentId);
                bool jcUnset = string.IsNullOrEmpty(rawJustify) || rawJustify == "normal";
                // Only center when the button has a DEFINITE main-axis size
                // (explicit width / min-width for a row button). An auto-sized
                // button shrinks to fit its content, so there is no free space
                // on the main axis and `center` is visually identical to
                // `flex-start` — but centering against the button's pre-shrink
                // main size leaves a stale line offset that paints the label far
                // to the side of the shrunk button box. That was the map.html
                // `.travel-btn` regression: auto-width buttons whose labels were
                // centered at the ~360px container width then shrank to ~90px,
                // stranding the text ~150px to the right (overlapping neighbours).
                // A fixed-width button (menu.html `button { width: 140px }`) has
                // real free space and centers correctly — no shrink, no stale
                // offset.
                if (rawDisplay == "inline-flex" && jcUnset && HasDefiniteMainSize(container, isRow)) {
                    props.JustifyContent = JustifyContent.Center;
                }
            }

            // Per CSS Flexbox §3: an absolutely-positioned child of a flex container is
            // NOT a flex item.  (IsButtonLike defined below.) It does not contribute to main-axis sizing, doesn't get
            // a slot in any flex line, and is positioned by PositioningPass against its
            // containing block (the flex container, if positioned). Its static-position
            // origin is the would-be flex container content edge — for v1 we leave the
            // out-of-flow box's pre-flex X/Y in place and let PositioningPass overwrite.
            //
            // FlexLayout.Layout is recursive (a flex item that is itself a FlexBox
            // re-enters Layout in ApplyMainSizesAndReflow). We use stack discipline on
            // shared scratch buffers: snapshot count on entry, pop the appended slice on
            // every exit path.
            var rawChildren = scratch.FlexRawChildren;
            int rawStart = rawChildren.Count;
            for (int i = 0; i < container.Children.Count; i++) {
                var c = container.Children[i];
                if (c is BlockBox bb && !IsOutOfFlow(bb)) rawChildren.Add(bb);
            }
            int rawCount = rawChildren.Count - rawStart;

            if (rawCount == 0) {
                rawChildren.RemoveRange(rawStart, rawChildren.Count - rawStart);
                // An empty flex container has zero intrinsic main/cross
                // content. The canonical case `.hud-top` is an empty
                // `display:flex` div whose height should come from its grid
                // cell (row 1 of `.hud`), NOT from BlockLayout's pre-flex
                // pass (which inflates to row1=503 before grid sizing).
                // We therefore collapse it here, but ONLY when the parent
                // hasn't already assigned a definite size (e.g. from grid).
                // The 3rd flex pass runs after grid has settled — at that
                // point grid has written the correct row-1 height into
                // hud-top, and we must not overwrite it back to zero.
                bool parentIsGridWithSize = container.Parent is Weva.Layout.Grid.GridBox && container.Height > 0;
                if (parentIsGridWithSize) return;
                FinalizeContainerCrossSize(container, props, isRow, 0);
                var emptyLines = scratch.RentLineView();
                var emptyItems = scratch.RentFlexItemsView();
                FinalizeContainerMainSize(container, props, isRow, emptyLines, emptyItems);
                scratch.ReturnLineView(emptyLines);
                scratch.ReturnFlexItemsView(emptyItems);
                return;
            }

            // Working buffer over flex items uses a local snapshot of [itemsStart,
            // itemsStart+rawCount). FlexLayoutItem instances themselves are pooled
            // via scratch.RentFlexItem / ReturnFlexItem.
            var allItems = scratch.FlexItems;
            int itemsStart = allItems.Count;
            for (int i = 0; i < rawCount; i++) {
                var bb = rawChildren[rawStart + i];
                var itemFs = bb.Style != null ? StyleResolver.FontSizePx(bb.Style, container.Style, ctx) : fs;
                // H5b: per-item line-height threads through so `lh` lengths on
                // individual flex items resolve against their own cascaded
                // line-height.
                double itemLh = bb.Style != null
                    ? StyleResolver.LineHeightPx(bb.Style, itemFs, ctx, ctx.GetMetrics(bb.Style.Get(CssProperties.FontFamilyId)))
                    : 0;
                var itemLengthCtx = ctx.ToLengthContext(itemFs, containerMainSize, itemLh);
                var itemProps = FlexItemProperties.From(bb.Style, itemLengthCtx);
                var fit = scratch.RentFlexItem();
                fit.Box = bb;
                fit.Props = itemProps;
                fit.DocOrder = i;
                allItems.Add(fit);
            }

            // List<T>.Sort with a Comparison<> would box the lambda capture for `this`;
            // we work over a local range so use an in-place insertion sort which is
            // alloc-free and adequate for typical small flex containers.
            SortItemRange(allItems, itemsStart, rawCount);

            // Build a thin per-frame view onto the slice so existing helpers that
            // accept List<Item> can keep working without changes. Rented from a
            // pool so recursive Layout calls each get their own view list.
            var items = scratch.RentFlexItemsView();
            for (int i = 0; i < rawCount; i++) items.Add(allItems[itemsStart + i]);

            foreach (var it in items) {
                ComputeBaseSize(it, container, containerMainSize, isRow);
                ComputeOuterMargins(it, isRow);
                // CSS Flexbox §9.2: hypothetical main size = flex base size
                // clamped by the item's used min/max main-size properties.
                // Without this, an item with `width: 100%; max-width: 560px`
                // hands FlexBaseSize = container's main size into
                // ResolveFlexibleLengths, and max-width is silently dropped.
                it.HypotheticalMainSize = ClampMainSizeByMinMax(it, container, containerMainSize, isRow, it.FlexBaseSize);
            }

            var lines = CollectLines(items, props, containerMainSize);

            // CSS Flexbox §9.7: shrink/grow only fires when the container
            // has a definite main-axis size that disagrees with the sum of
            // item base sizes. For an indefinite main axis (e.g. column
            // flex with `height: auto` and no grid/positioning override),
            // the container adopts the items' total — no overflow exists,
            // so neither grow nor shrink should run. Without this guard
            // BlockLayout's pre-flex Height (sum of children's stacked
            // heights, NO flex gaps because BlockLayout doesn't apply
            // CSS gap between block children) is fed in as
            // containerMainSize, then ResolveFlexibleLengths subtracts
            // gapSum from it for availableSpace and finds totalBase
            // exceeds it by exactly gapSum — triggering a spurious
            // shrink that scales every item down. Canonical shape: a
            // column flex with gap and definite-height children whose total
            // already fits — without the guard, every child is uniformly
            // scaled down by (containerMainSize - gapSum) / containerMainSize.
            bool definiteMain = HasDefiniteMain(container, isRow);
            foreach (var line in lines) {
                ResolveFlexibleLengths(line, items, props, containerMainSize, definiteMain, isRow, container);
            }

            ApplyMainSizesAndReflow(lines, items, container, isRow);

            // For the cross-axis clamp (see ComputeLineCrossSize), pass the
            // container's cross-size only when the container has a TRULY
            // definite cross size:
            //   - author-set (HasDefiniteCross), or
            //   - imposed by an outer formatting context (grid stretch, or
            //     position-pinned both edges on the cross axis).
            // Pre-flex BlockLayout always writes a positive Height/Width
            // (block stack of children), but that's not a real constraint —
            // capping at it makes auto-height row flex unable to grow when
            // items reflow taller (e.g. `.msg` row-flex whose `.bubble`
            // reflows to 2-line height; without this fix .msg.H stays at
            // pre-reflow value and the bubble lands at negative Y inside
            // .msg due to align-items:flex-end).
            bool gridImposedCross = container is BlockBox bbContainer
                && (isRow ? bbContainer.GridStretchedHeight : bbContainer.GridStretchedWidth);
            bool positionPinnedCross = (container.Position == PositionType.Absolute || container.Position == PositionType.Fixed)
                && (isRow
                    ? (container.OffsetTop.HasValue && container.OffsetBottom.HasValue)
                    : (container.OffsetLeft.HasValue && container.OffsetRight.HasValue));
            // Column flex: the cross axis is WIDTH, which is always assigned by
            // the parent formatting context (block-flow ContentWidth, an outer
            // flex item's main-axis distribution, a grid track size, etc.).
            // Unlike row flex's auto-height case (where line.CrossSize should
            // be free to grow to fit items — see the .msg/.bar comment block
            // above), an "auto width" column flex still has a concrete width
            // that was just written by BlockLayout / the parent flex pass.
            // Treat that assigned width as a real cross-axis constraint so
            // ComputeLineCrossSize's column-shrink branch can fire when
            // BlockLayout's pre-flex pass over-stretched items to the
            // container width with non-stretch align (e.g. match3
            // .hud .center { flex-direction:column; align-items:center }
            // where .score-pill and .stars need to shrink to ~108 / ~56
            // instead of staying at the 136.4 container width).
            bool columnWithAssignedWidth = !isRow && containerCrossSize > 0;
            // A row container whose height was grown by a column parent's
            // flex-grow (FlexParentAssignedCross) has a definite cross.
            bool rowFlexGrownCross = isRow && container is BlockBox bbRowFa && bbRowFa.FlexParentAssignedCross;
            double clampCrossSize = (HasDefiniteCross(container, isRow) || gridImposedCross || positionPinnedCross || columnWithAssignedWidth || crossFlooredByMin || rowFlexGrownCross)
                ? containerCrossSize : 0;
            foreach (var line in lines) {
                ComputeLineCrossSize(line, items, props, isRow, clampCrossSize);
            }

            double linesCrossTotal = 0;
            for (int i = 0; i < lines.Count; i++) {
                linesCrossTotal += lines[i].CrossSize;
                if (i > 0) linesCrossTotal += props.CrossGap;
            }

            // A flex container's cross size is "definite" when the author set
            // it (HasDefiniteCross) OR when an outer formatting context — like
            // a grid cell stretching the item via justify-self:stretch /
            // align-self:stretch — already wrote a non-zero ContentWidth/
            // ContentHeight onto the box before flex pass 2. Without honouring
            // the imposed size here, single-line column flexes shrink to
            // max(child cross-size) for line.CrossSize, and align-items:flex-end
            // pins items to the wrong edge: e.g. `.hud-tr { display:flex;
            // flex-direction:column; align-items:flex-end }` inside a 311-wide
            // grid cell positions a 200-wide `.panel.quest` at offset 0 instead
            // of cell-end (offset 111).
            // Use the same TRULY-definite cross-size predicate as clampCrossSize
            // above. The earlier `|| containerCrossSize > 0` clause treated any
            // pre-flex BlockLayout-stacked Height/Width as "definite", which
            // then routes DistributeLinesAlongCross + AlignContent.Stretch to
            // stretch the single line to the inflated pre-flex value. Canonical
            // bug: .stars row-flex (3 span.star, each ~22 tall) pre-flex H=66
            // (block-stacked sum), then flex pass keeps line.CrossSize=66 and
            // stretches each star to 66 — three times its actual line-height.
            bool definiteCross = HasDefiniteCross(container, isRow)
                || (container is BlockBox bbDef
                    && (isRow ? bbDef.GridStretchedHeight : bbDef.GridStretchedWidth))
                || ((container.Position == PositionType.Absolute || container.Position == PositionType.Fixed)
                    && (isRow
                        ? (container.OffsetTop.HasValue && container.OffsetBottom.HasValue)
                        : (container.OffsetLeft.HasValue && container.OffsetRight.HasValue)))
                // Column flex with an externally-assigned width: cross-axis
                // is WIDTH, always written by the parent formatting context,
                // so containerCrossSize is a real constraint. Without this,
                // single-line column flexes with non-stretch align-items
                // (e.g. `align-items:center`) don't center their items
                // within the container's full width — line.CrossSize collapses
                // to max(item.Width) (the linesCrossTotal fallback) so
                // AlignItemsInLine has no free space to distribute. Mirrors
                // the columnWithAssignedWidth gate used for clampCrossSize.
                || columnWithAssignedWidth
                // min-height (row) / min-width (column) floored the cross above
                // content — the floored value is a real, definite constraint.
                || crossFlooredByMin
                // row container whose height was grown by a column parent's flex-grow
                || rowFlexGrownCross;
            double effectiveCrossSize = definiteCross ? containerCrossSize : linesCrossTotal;
            DistributeLinesAlongCross(lines, props, effectiveCrossSize, linesCrossTotal);

            foreach (var line in lines) {
                AlignItemsInLine(line, items, props, isRow);
            }

            JustifyItemsAlongMain(lines, items, props, containerMainSize, isRow);

            ApplyDirectionReversal(lines, items, props, containerMainSize, isRow);

            ApplyWrapReversal(lines, props, effectiveCrossSize);

            WriteBackPositions(lines, items, container, isRow);

            FinalizeContainerCrossSize(container, props, isRow, linesCrossTotal);
            FinalizeContainerMainSize(container, props, isRow, lines, items);

            // Re-stamp text-align deltas on every flex item now that final
            // sizes are settled. Why this is needed even with the
            // ApplyTextAlign idempotency fix in InlineLayout: that pass uses
            // the item's PROVISIONAL ContentWidth (often inherited from the
            // parent's content area before flex shrink-to-fit kicks in). For
            // a flex item whose final Width ends up much narrower than its
            // provisional Width AND that has `text-align:center` inherited
            // (commonly from the UA button rule), the stamped delta is
            // computed against the wide provisional ContentWidth and ends up
            // shifting the text outside the final box. The shrink-path
            // reapply added to ReflowIfShrunk only covers boxes that go
            // through CanKeepCurrentLayoutForShrink; items that never enter
            // that path (e.g. flex-children that are already content-sized
            // from the start, like a content-sized state-chip
            // span) still need a final pass. Cheap: walks LineBox children
            // only, no relayout.
            for (int i = 0; i < items.Count; i++) {
                var item = items[i];
                if (item?.Box != null) ReapplyTextAlignOnLines(item.Box);
            }

            // Re-stack following block-flow siblings if our height changed. See
            // the preFlexHeight snapshot at Layout entry for the canonical case.
            // Skip when the container is out-of-flow (its size doesn't influence
            // block-flow siblings), or when the parent isn't a plain block-flow
            // container (FlexBox/GridBox parents control their own item
            // positioning). The shift MUST run even when `reflowing` is true:
            // ReflowFlexDescendants walks the subtree AFTER BlockLayout's
            // RelayoutContentAt has re-stacked everything at PRE-flex heights;
            // only the subsequent per-FlexBox Layout calls produce the real
            // (smaller) flex heights, and following block-flow siblings still
            // need to be pulled up by the height delta. Canonical case:
            // `.convo > .convo-body > .convo-row` in chat.html — `.convo`
            // (row-flex) grows `.convo-body` (block) and triggers ReflowIfShrunk
            // → BlockLayout re-stacks the two `.convo-row` children at their
            // pre-flex (stacked-child-sum) heights, then ReflowFlexDescendants
            // shrinks the first row's Height. Without firing the shift here,
            // `.convo-row 2` sits at Y=36 (using row 1's pre-flex H=34) instead
            // of Y=21 (using post-flex H=19).
            ShiftFollowingSiblingsIfHeightChanged(container, preFlexHeight);

            // Tear-down: return all rented borrowables and pop our slices off the
            // shared stacks so a recursive parent Layout() frame sees its own
            // ranges intact. (The recursive ApplyMainSizesAndReflow path completed
            // before this point — its slice was popped by its own tear-down.)
            for (int i = 0; i < lines.Count; i++) scratch.ReturnFlexLine(lines[i]);
            scratch.FlexLines.RemoveRange(scratch.FlexLines.Count - lines.Count, lines.Count);
            scratch.ReturnLineView(lines);

            scratch.ReturnFlexItemsView(items);

            for (int i = 0; i < rawCount; i++) {
                scratch.ReturnFlexItem(allItems[itemsStart + i]);
            }
            allItems.RemoveRange(itemsStart, allItems.Count - itemsStart);
            rawChildren.RemoveRange(rawStart, rawChildren.Count - rawStart);
        }

        // Shifts the parent's following block-flow siblings of `container` up
        // (or down) by the delta between the pre-flex Height (assigned by
        // BlockLayout) and the post-flex Height. Only applies when the parent
        // is a plain block-flow BlockBox (not flex / grid / table — those
        // position their items themselves) and the container is in-flow.
        // Margin-collapsing chains across FlexBox boundaries aren't unwound:
        // BlockLayout's chain logic seeds `chainMaxPos`/`chainMinNeg` from the
        // child's literal margin-bottom (which doesn't change with this shift),
        // so the relative gaps between siblings are preserved — we only
        // translate the entire trailing run by the height delta.
        //
        // Note: there is no `if (reflowing) return;` guard. The shift is
        // needed both during normal flex passes AND during ReflowIfShrunk's
        // ReflowFlexDescendants walk — BlockLayout.RelayoutContentAt has just
        // re-stacked everything at PRE-flex (inflated) heights and only the
        // per-FlexBox Layout calls inside ReflowFlexDescendants resolve the
        // real (smaller) heights, so following block-flow siblings still need
        // to be pulled up here. The parent-is-flex/grid/table guards below
        // already prevent us from stepping on a parent that owns its own
        // positioning.
        void ShiftFollowingSiblingsIfHeightChanged(FlexBox container, double preFlexHeight) {
            double delta = container.Height - preFlexHeight;
            BlockFlowAdjuster.PropagateHeightDelta(container, delta, ctx);
        }

        static void SortItemRange(List<FlexLayoutItem> list, int start, int count) {
            // Stable insertion sort. For small ranges (the typical flex container
            // size) this is faster than List<T>.Sort and avoids any Comparison<>
            // / IComparer<> closure allocation.
            for (int i = 1; i < count; i++) {
                var key = list[start + i];
                int j = i - 1;
                while (j >= 0) {
                    var prev = list[start + j];
                    int cmp = prev.Props.Order.CompareTo(key.Props.Order);
                    if (cmp == 0) cmp = prev.DocOrder.CompareTo(key.DocOrder);
                    if (cmp <= 0) break;
                    list[start + j + 1] = prev;
                    j--;
                }
                list[start + j + 1] = key;
            }
        }

        // Allocation-free direction probe: the prior Trim().ToLowerInvariant()
        // ran twice (once on flex-flow tokens, once on flex-direction) for
        // every flex container per layout pass. CssStringUtil.EqualsIgnoreCaseTrimmed
        // compares against a known lowercase literal without copying.
        bool ContainerIsRow(FlexBox container) {
            if (container.Style == null) return true;
            string dir = container.Style.Get(CssProperties.FlexDirectionId);
            if (string.IsNullOrEmpty(dir)) {
                string ff = container.Style.Get(CssProperties.FlexFlowId);
                if (!string.IsNullOrEmpty(ff)) {
                    if (HasWhitespaceToken(ff, "column")
                        || HasWhitespaceToken(ff, "column-reverse")) return false;
                    if (HasWhitespaceToken(ff, "row")
                        || HasWhitespaceToken(ff, "row-reverse")) return true;
                }
                return true;
            }
            return CssStringUtil.EqualsIgnoreCaseTrimmed(dir, "row")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(dir, "row-reverse");
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

        static bool IsOutOfFlow(BlockBox box) {
            if (box?.Style == null) return false;
            string raw = box.Style.Get(CssProperties.PositionId);
            if (string.IsNullOrEmpty(raw)) return false;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "absolute")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "fixed");
        }

        // <button>, <input type=submit|reset|button>: Chrome centers the
        // button's content box. Used to apply the default main-axis centering
        // (see Layout) only to genuine button controls.
        static bool IsButtonLike(Weva.Dom.Element el) {
            if (el == null) return false;
            string tag = el.TagName;
            if (string.IsNullOrEmpty(tag)) return false;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(tag, "button")) return true;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(tag, "input")) {
                string type = el.GetAttribute("type");
                return CssStringUtil.EqualsIgnoreCaseTrimmed(type, "submit")
                    || CssStringUtil.EqualsIgnoreCaseTrimmed(type, "reset")
                    || CssStringUtil.EqualsIgnoreCaseTrimmed(type, "button");
            }
            return false;
        }

        static bool HasDefiniteCross(FlexBox container, bool isRow) {
            if (container.Style == null) return false;
            string raw = isRow ? container.Style.Get(CssProperties.HeightId) : container.Style.Get(CssProperties.WidthId);
            if (!string.IsNullOrEmpty(raw) && raw != "auto") return true;
            // CSS Sizing L4 §5: when one axis is auto and aspect-ratio is set,
            // the auto axis is derived from the definite axis. The cross size
            // is then deterministic and must be treated as definite for flex
            // line-stretch / align-items purposes.
            if (StyleResolver.TryResolveAspectRatio(container.Style, out double ratio) && ratio > 0) {
                string otherRaw = isRow ? container.Style.Get(CssProperties.WidthId) : container.Style.Get(CssProperties.HeightId);
                if (!string.IsNullOrEmpty(otherRaw) && otherRaw != "auto") return true;
                double otherPx = isRow ? container.Width : container.Height;
                if (otherPx > 0) return true;
            }
            return false;
        }

        // CSS Sizing L4 §5: when the cross axis is auto and aspect-ratio is
        // set, derive the cross size from the (definite) main axis via the
        // ratio. Returns 0 when ratio is not set or the main axis is also
        // indefinite. Used by the flex pass to feed a real `containerCrossSize`
        // into line-stretch / align-items distribution BEFORE the aspect-ratio
        // fixup pass runs at the end of LayoutEngine.Layout — without this,
        // `.portrait-frame { width:100%; aspect-ratio:3/4; display:flex;
        // align-items:center }` lines up its glyph against the box's intrinsic
        // (pre-ratio) content height (≈131 from the glyph itself), so
        // `align-items: center` has no free space and pins the glyph to the
        // top of the eventual 386-px-tall frame.
        static double TryDeriveCrossFromAspectRatio(FlexBox container, bool isRow) {
            if (container.Style == null) return 0;
            if (!StyleResolver.TryResolveAspectRatio(container.Style, out double ratio) || ratio <= 0) return 0;
            double mainPx = isRow ? container.Width : container.Height;
            if (mainPx <= 0) return 0;
            double frameMain = isRow
                ? container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight
                : container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;
            double frameCross = isRow
                ? container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom
                : container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight;
            double contentMain = mainPx - frameMain;
            if (contentMain <= 0) return 0;
            // aspect-ratio means width : height = ratio : 1 → height = width / ratio.
            double contentCross = isRow ? (contentMain / ratio) : (contentMain * ratio);
            return contentCross > 0 ? (contentCross + frameCross) : 0;
        }

        // CSS Sizing L4 §5: derive a flex item's main-axis size from its
        // (definite) cross-axis size via aspect-ratio. Returns 0 when the
        // item has no aspect-ratio set, or its cross axis is not explicitly
        // definite. The flex pass runs BEFORE the aspect-ratio fixup at the
        // end of LayoutEngine.Layout, so for column flex an item like
        // `.portrait { width:100%; aspect-ratio:1/1 }` has box.Height still
        // content-derived (~154 from a giant glyph), not the 292 the ratio
        // would give. Feeding 154 into FlexBaseSize makes
        // `justify-content:flex-end` compute the wrong free space and the
        // speaker-meta block ends up 138 px below its Chrome position.
        // Cross must be EXPLICITLY definite (raw style is a length/percent,
        // not `auto`); otherwise box.Width may itself be content-derived
        // and the derived main would be doubly wrong.
        static double TryDeriveItemMainFromAspectRatio(BlockBox item, bool isRow) {
            if (item.Style == null) return 0;
            if (!StyleResolver.TryResolveAspectRatio(item.Style, out double ratio) || ratio <= 0) return 0;
            string crossProp = isRow ? item.Style.Get(CssProperties.HeightId) : item.Style.Get(CssProperties.WidthId);
            if (string.IsNullOrEmpty(crossProp) || crossProp == "auto") return 0;
            double crossPx = isRow ? item.Height : item.Width;
            if (crossPx <= 0) return 0;
            double frameMain = isRow
                ? item.PaddingLeft + item.PaddingRight + item.BorderLeft + item.BorderRight
                : item.PaddingTop + item.PaddingBottom + item.BorderTop + item.BorderBottom;
            double frameCross = isRow
                ? item.PaddingTop + item.PaddingBottom + item.BorderTop + item.BorderBottom
                : item.PaddingLeft + item.PaddingRight + item.BorderLeft + item.BorderRight;
            double contentCross = crossPx - frameCross;
            if (contentCross <= 0) return 0;
            // aspect-ratio = width:height. Column flex main=height: contentMain = contentCross/ratio.
            // Row flex main=width: contentMain = contentCross * ratio.
            double contentMain = isRow ? (contentCross * ratio) : (contentCross / ratio);
            return contentMain > 0 ? (contentMain + frameMain) : 0;
        }

        // True when the flex container's main-axis size is definite:
        // either the author set a concrete length/percent on the main axis,
        // OR an outer formatting context (grid stretch, positioning inset
        // top+bottom for column flex / left+right for row flex) imposed a
        // size. Used to gate ResolveFlexibleLengths' grow/shrink branches:
        // when main is indefinite, the container adopts totalBase and
        // there is no meaningful free space to distribute.
        //
        // Conservative: ROW flex defaults to definite (a flex item's width
        // is typically allocated by its parent container even with
        // `width:auto`, and existing row-flex behavior depends on the
        // shrink/grow algorithm running). The fix targets the canonical
        // column-flex regression: BlockLayout's pre-flex Height = sum of
        // children stacked block-style with NO flex gap, which the flex
        // pass then reads as a "definite" main size that disagrees with
        // child total + gaps by exactly gapSum, triggering a spurious
        // shrink. Restricting the indefinite branch to column flex with
        // an auto height keeps row-flex semantics unchanged.
        static bool HasDefiniteMain(FlexBox container, bool isRow) {
            if (isRow) return true;
            if (container.Style == null) return true;
            string raw = container.Style.Get(CssProperties.HeightId);
            if (!string.IsNullOrEmpty(raw) && raw != "auto") return true;
            // A definite min-height floors the column main size, so there IS
            // free space for flex-grow to distribute (Chrome stretches `flex:1`
            // children of a `min-height:100vh` column). Mirrors HasDefiniteMainSize,
            // which already honours min-height for the button-justify default.
            // The flex base sizes feeding FinalizeContainerMainSize are
            // stretch-free (ComputeBaseSize uses MaxContentBlockSize for grid
            // children), so the container settles at its min-height rather than
            // ballooning from the grown children.
            string minRaw = container.Style.Get(CssProperties.MinHeightId);
            if (!string.IsNullOrEmpty(minRaw) && minRaw != "auto" && minRaw != "0" && minRaw != "0px") return true;
            // Grid stretched the main axis to the cell.
            if (container is BlockBox bb && bb.GridStretchedHeight) return true;
            // A row-flex parent cross-stretched this column's height (= its main).
            if (container is BlockBox bbf && bbf.FlexCrossStretchedMain) return true;
            // Pinned by positioning (top+bottom both set).
            if (container.Position == PositionType.Fixed || container.Position == PositionType.Absolute) {
                if (container.OffsetTop.HasValue && container.OffsetBottom.HasValue) return true;
            }
            return false;
        }

        // True when the (button-like) container has an explicitly authored
        // main-axis size — width for a row container, height for a column —
        // or a non-trivial min main-axis size. Such a container does NOT
        // shrink-to-fit its content, so there is genuine free space for the
        // UA button `justify-content: center` default to act on. Auto-sized
        // buttons fill their content box exactly (center == flex-start
        // visually) and centering them against the pre-shrink main size
        // strands the label off to the side after shrink — see the call site.
        static bool HasDefiniteMainSize(FlexBox container, bool isRow) {
            if (container.Style == null) return false;
            string raw = container.Style.Get(isRow ? CssProperties.WidthId : CssProperties.HeightId);
            if (!string.IsNullOrEmpty(raw) && raw != "auto") return true;
            string minRaw = container.Style.Get(isRow ? CssProperties.MinWidthId : CssProperties.MinHeightId);
            if (!string.IsNullOrEmpty(minRaw) && minRaw != "auto" && minRaw != "0" && minRaw != "0px") return true;
            return false;
        }

        void ComputeBaseSize(Item it, FlexBox container, double containerMainSize, bool isRow) {
            var box = it.Box;
            double fs = box.Style != null ? StyleResolver.FontSizePx(box.Style, container.Style, ctx) : ctx.RootFontSizePx;

            double natural = isRow ? box.Width : box.Height;
            // Prefer aspect-ratio-derived main when cross is explicitly definite
            // — overrides the stale BlockLayout-computed main BEFORE either the
            // FlexBasisKind.Content branch or the default `auto` fallback reads
            // `natural`. Probe paths in the default branch read `natural` for
            // their inflation heuristic; a ratio-derived value is by definition
            // not inflated content (it's geometric), so the probe never fires.
            double aspectMain = (box is BlockBox bbForRatio)
                ? TryDeriveItemMainFromAspectRatio(bbForRatio, isRow)
                : 0;
            if (aspectMain > 0) natural = aspectMain;
            double frameMain = isRow
                ? box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight
                : box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;

            switch (it.Props.Basis.Kind) {
                case FlexBasisKind.Length:
                    it.FlexBaseSize = it.Props.Basis.Value;
                    break;
                case FlexBasisKind.Percentage:
                    it.FlexBaseSize = it.Props.Basis.Value * 0.01 * containerMainSize;
                    break;
                case FlexBasisKind.Content:
                    // CSS Flexbox L1 §7.2.1: `content` sizes the item as
                    // `max-content`. Reuse the elaborate max-content probe
                    // the `auto` path runs by falling through with a flag
                    // that (a) skips the width/height resolve (Content
                    // ignores the main-size property entirely) and
                    // (b) forces the probe to run unconditionally.
                    goto default;
                default: {
                    bool forceContent = it.Props.Basis.Kind == FlexBasisKind.Content;
                    string raw = isRow ? box.Style?.Get(CssProperties.WidthId) : box.Style?.Get(CssProperties.HeightId);
                    var resolved = forceContent
                        ? StyleResolver.ResolvedLength.Auto()
                        : StyleResolver.ResolveLength(raw, box.Style, ctx, fs, containerMainSize);
                    if (resolved.Kind == StyleResolver.LengthKind.Length) {
                        // Spec default for box-sizing is `content-box` — author
                        // width is content; outer = content + frame. The prior
                        // `!= "content-box"` test inverted the default: a null
                        // / unset slot read as border-box and `width: 96px`
                        // landed at outer=96 instead of 100 (96 + 2*2 border).
                        // IsBorderBox already has the spec-correct default.
                        bool borderBox = IsBorderBox(box.Style);
                        it.FlexBaseSize = borderBox ? resolved.Pixels : resolved.Pixels + frameMain;
                    } else if (resolved.Kind == StyleResolver.LengthKind.Percent) {
                        bool borderBox = IsBorderBox(box.Style);
                        double basePct = containerMainSize * (resolved.Percent * 0.01);
                        it.FlexBaseSize = borderBox ? basePct : basePct + frameMain;
                    } else {
                        // CSS Flexbox §9.2: when flex-basis is auto and the
                        // item's main-size property is auto, the flex base
                        // size is the item's max-content. Use the recursive
                        // MaxContentWidth helper for row flex (it handles
                        // flex/grid descendants correctly). Falls back to
                        // pre-flex Width on column flex (where natural =
                        // box.Height which is content-derived from BlockLayout).
                        // Probe path runs when EITHER:
                        //   (a) BlockLayout's pre-flex Width was inflated to >=
                        //       containerMainSize (the canonical "pre-flex
                        //       BlockLayout sized me to parent" case the comment
                        //       below describes), OR
                        //   (b) the item's pre-flex Width is SMALLER than its
                        //       content's max-content. Canonical case: a
                        //       blockified `<span>142</span>` inside a sub-
                        //       flex `.weight { display:flex }` whose pre-flex
                        //       Width gets set to a half-of-line value (~14.7)
                        //       by BlockLayout's inline-fragment fitting,
                        //       while the actual text content needs 29.6 px.
                        //       Without (b) the flex base size is the wrong
                        //       half-width, the span renders truncated, and
                        //       siblings overlap (visible as "Knapsack 142"
                        //       collision in the inventory header).
                        // BlockLayout's pre-flex pass fills a block item to the
                        // container's content width MINUS the item's own margins
                        // (CSS 2.1 §10.3.3). So a blockified inline flex item with
                        // a margin (e.g. a footer `.kbd { margin-left:16 }`) lands
                        // at containerMainSize - margin, NOT >= containerMainSize —
                        // the bare `>=` test then misses it, the flex base stays
                        // the inflated fill-width, and flex-shrink scales the item
                        // to a fraction of the row instead of its content width.
                        // Subtract the item's main-axis margin so the inflation is
                        // still detected and we probe max-content.
                        double infMargin = box is BlockBox bbInf
                            ? (isRow ? bbInf.MarginLeft + bbInf.MarginRight : bbInf.MarginTop + bbInf.MarginBottom)
                            : 0;
                        bool probeWideInflation = natural >= containerMainSize - infMargin - LayoutEpsilons.HalfPixelEqual;
                        bool probeContentExceedsNatural = false;
                        if (!probeWideInflation && box is BlockBox bbForContent) {
                            double contentMax = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bbForContent);
                            if (contentMax > natural + LayoutEpsilons.HalfPixelEqual) probeContentExceedsNatural = true;
                        }
                        // Aspect-ratio gave us a geometric natural; skip probe
                        // (it would replace the geometric size with whatever
                        // descendants want, defeating the ratio).
                        if (aspectMain > 0) { probeWideInflation = false; probeContentExceedsNatural = false; }
                        // CSS Flexbox L1 §7.2.1: `flex-basis: content` is
                        // sized as max-content. The probe heuristics above
                        // are tuned to detect when the pre-flex `natural`
                        // disagrees with max-content; for explicit `content`
                        // the spec demands max-content even when natural
                        // already equals it (so the fallback to `natural`
                        // would still be spec-correct, but we want the
                        // probe path to be taken so callers can rely on it).
                        // Force the probe on unless aspect-ratio claimed
                        // the geometric value (mirrors the carve-out above).
                        if (forceContent && aspectMain <= 0) probeContentExceedsNatural = true;
                        if (isRow && box is BlockBox bbItem && blockLayout != null
                            && (probeWideInflation || probeContentExceedsNatural)) {
                            // Item Width was inflated by BlockLayout to
                            // parent.contentWidth (or larger — in nested
                            // flex, BlockLayout sized the item against the
                            // BODY's width before the outer flex container
                            // shrank, so `natural` can be much larger than
                            // this container's resolved main size). Re-flow
                            // at probe and measure max-content. Cache on the
                            // box. Canonical nested-flex case: match3 HUD
                            // `.left { display:flex }` contains a `.level
                            // { display:flex; flex-direction:column }`. By
                            // the time `.left`'s own Layout runs, .left's
                            // ContentWidth has been shrunk to ~89px (via the
                            // outer `.hud` row-flex distribution), but
                            // .level.Width is still ~1410 (body width from
                            // the pre-flex BlockLayout pass). Without the
                            // relaxed bound, the probe path is skipped and
                            // FlexBaseSize falls back to 1410, which
                            // ResolveFlexibleLengths then over-shrinks both
                            // siblings (icon-btn collapses to ~2.4 instead
                            // of its explicit 44).
                            if (System.Math.Abs(bbItem.ShrinkFitCachedAvail - containerMainSize) < LayoutEpsilons.HalfPixelEqual
                                && bbItem.ShrinkFitCachedWidth >= 0) {
                                it.FlexBaseSize = bbItem.ShrinkFitCachedWidth;
                            } else if (probeWideInflation
                                && TryCurrentUnwrappedPlainBlockBase(bbItem, frameMain, containerMainSize, out double unwrappedBase)) {
                                it.FlexBaseSize = unwrappedBase;
                                bbItem.ShrinkFitCachedAvail = containerMainSize;
                                bbItem.ShrinkFitCachedWidth = unwrappedBase;
                            } else if (reflowing) {
                                // Re-entered FlexLayout during a ReflowIfShrunk
                                // RelayoutContentAt. We can't recursively
                                // RelayoutContentAt the item (would re-enter the
                                // outer reflow). Fortunately the outer
                                // RelayoutContentAt already laid out the item's
                                // descendants at the (wide) parent content width
                                // — wider than any text fragment, so no wrap —
                                // so its existing LineBoxes carry honest
                                // max-content fragment widths. Walk them
                                // directly. Without this, FlexBaseSize falls
                                // back to `natural` (= containerMainSize), and
                                // ResolveFlexibleLengths shrinks both items to
                                // half the line — the canonical `<span>HP</span>
                                // <span>4,480 / 7,000</span>` inside
                                // `.bar > .label { display:flex; justify-content:
                                // space-between }` symptom: both spans rendered
                                // at containerMainSize/2 instead of their text
                                // intrinsic widths.
                                // MaxContentWidth for FlexBox/GridBox already
                                // includes their HorizontalFrame; for plain
                                // BlockBox it returns content-only and the
                                // caller adds the frame.
                                double mc;
                                bool isRigidSubContainer = bbItem is FlexBox || bbItem is Weva.Layout.Grid.GridBox;
                                if (isRigidSubContainer) {
                                    mc = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bbItem);
                                } else {
                                    mc = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bbItem)
                                         + bbItem.PaddingLeft + bbItem.PaddingRight
                                         + bbItem.BorderLeft + bbItem.BorderRight;
                                }
                                if (mc < frameMain) mc = frameMain;
                                // CSS Flexbox §9.2: flex-basis auto on a grid/
                                // flex sub-container resolves to max-content
                                // unclamped — the container's bounded intrinsic
                                // size (sum of tracks/items + frame). Capping at
                                // containerMainSize hides legitimate overflow:
                                // .board (524) inside .board-wrap (503) would
                                // shrink to 503 and tiles would render past the
                                // dark frame. Plain BlockBox keeps the cap
                                // because text max-content can be unbounded.
                                it.FlexBaseSize = isRigidSubContainer
                                    ? mc
                                    : System.Math.Min(mc, containerMainSize);
                                // Don't write the cache during reflow — its
                                // ShrinkFitCachedAvail key matches the post-
                                // shrink width, not containerMainSize.
                            } else {
                                bool isRigid2 = bbItem is FlexBox || bbItem is Weva.Layout.Grid.GridBox;
                                if (isRigid2) {
                                    // Flex/grid sub-containers: MaxContentWidth reads the
                                    // intrinsic border-box width from already-positioned
                                    // items via FlexIntrinsicInline / GridIntrinsicInline —
                                    // no RelayoutContentAt needed. Calling
                                    // RelayoutContentAt(1e6) would stomp the sub-
                                    // container's flex/grid children with a block-stacked
                                    // layout, and since RunFlexPasses processes children
                                    // before parents (depth-first), the inner flex pass
                                    // has already run and won't run again. If the final
                                    // assigned width equals snapshotWidth, ReflowIfShrunk
                                    // would not fire, leaving children block-stacked.
                                    double mc = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bbItem);
                                    if (mc < frameMain) mc = frameMain;
                                    it.FlexBaseSize = mc; // no cap — rigid intrinsic may overflow
                                    bbItem.ShrinkFitCachedAvail = containerMainSize;
                                    bbItem.ShrinkFitCachedWidth = it.FlexBaseSize;
                                } else {
                                    // Snapshot the item's pre-probe geometry. The
                                    // probe writes box.Width = 1e6 and re-lays out
                                    // descendants relative to that giant content
                                    // width. After we measure max-content we have
                                    // to UNDO the descendant changes too, not just
                                    // restore the box's own Width/X — otherwise
                                    // descendants keep W ≈ 1e6 (visible as
                                    // bubble-text W=999972 in the chat demo).
                                    // Re-running BlockLayout at the snapshot width
                                    // restores the entire subtree to its pre-
                                    // probe state. The outer flex algorithm then
                                    // commits its real fitted size on top, and
                                    // ReflowIfShrunk handles any shrink from
                                    // there to the final layout.
                                    //
                                    // SAVE+RESTORE the `reflowing` flag instead of
                                    // unconditional false in `finally`: when this
                                    // probe runs nested inside an outer
                                    // ReflowIfShrunk → ReflowFlexDescendants chain
                                    // (which sets reflowing=true to suppress
                                    // recursive entry), an unconditional reset
                                    // would clobber the outer state — subsequent
                                    // ReflowIfShrunk calls in the outer scope
                                    // would then incorrectly re-enter.
                                    bool wasReflowing = reflowing;
                                    reflowing = true;
                                    double snapshotWidth = bbItem.Width;
                                    double snapshotX = bbItem.X;
                                    try {
                                        blockLayout.RelayoutContentAt(bbItem, 1e6);
                                        double mc = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bbItem)
                                                     + bbItem.PaddingLeft + bbItem.PaddingRight
                                                     + bbItem.BorderLeft + bbItem.BorderRight;
                                        if (mc < frameMain) mc = frameMain;
                                        it.FlexBaseSize = System.Math.Min(mc, containerMainSize);
                                    } finally {
                                        reflowing = wasReflowing;
                                        if (snapshotWidth > 0 && snapshotWidth < 1e5) {
                                            blockLayout.RelayoutContentAt(bbItem, snapshotWidth);
                                        } else {
                                            bbItem.Width = snapshotWidth;
                                        }
                                        bbItem.X = snapshotX;
                                    }
                                    bbItem.ShrinkFitCachedAvail = containerMainSize;
                                    bbItem.ShrinkFitCachedWidth = it.FlexBaseSize;
                                }
                            }
                        } else if (isRow && (box is FlexBox || box is Weva.Layout.Grid.GridBox)
                                   && box is BlockBox bbRigid) {
                            // CSS Flexbox §9.2 — flex-basis: auto on a
                            // FlexBox/GridBox sub-container should resolve to
                            // the sub-container's max-content. Plain
                            // BlockBoxes can fall back to `natural` (their
                            // pre-flex Width) without harm because their
                            // intrinsic max-content needs a RelayoutContentAt
                            // probe (expensive) and `natural` is usually close
                            // enough when no probe is needed. But a
                            // FlexBox/GridBox child's max-content is cheaply
                            // computable via FlexIntrinsicInline /
                            // GridIntrinsicInline — and using `natural`
                            // (= pre-flex Width clamped by BlockLayout's max-
                            // width) instead of the real intrinsic locks the
                            // child to its max-width whenever BlockLayout
                            // sized it that way. Canonical case: a
                            // `.hero-chip { display:flex; min-width:110;
                            // max-width:168 }` in a 1200-wide topbar —
                            // BlockLayout sized chip.Width = 168 (max-width
                            // clamp of 1200 avail), but chip's real intrinsic
                            // is portrait(40)+gap(12)+name(40 min-width)+
                            // 20 frame = 112. With `natural` fallback the
                            // chip locks at 168; with MaxContentWidth it
                            // sits at 112 (and the min-width:110 floor still
                            // applies via ClampMainSizeByMinMax).
                            double mc = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(bbRigid);
                            if (mc < frameMain) mc = frameMain;
                            it.FlexBaseSize = mc;
                        } else if (!isRow && box is Weva.Layout.Grid.GridBox gridChild
                                   && gridLayout != null && !reflowing) {
                            // CSS Flexbox §9.2 — the column-flex base size of a
                            // grid sub-container is its max-content BLOCK size.
                            // `natural` here is `box.Height`, which BlockLayout's
                            // pre-grid pass set by block-STACKING the grid's
                            // children vertically (a `repeat(3,1fr)` 6-item grid
                            // stacks to ~2269 instead of its real 2-row ~646).
                            // Feeding that into the flex base balloons a
                            // `min-height:100vh` column: grow inflates the grid,
                            // its `1fr` rows then FILL the inflated height, and
                            // that filled Height feeds back as the next base —
                            // an unrecoverable loop. MaxContentBlockSize resolves
                            // the grid's row tracks against zero free space
                            // (1fr-as-content), independent of any assigned
                            // Height, so the base is stable and the container
                            // resolves to its min-height. Guarded by `!reflowing`
                            // to avoid re-entering grid sizing during a
                            // BlockLayout reflow probe. See FLEX-MINHEIGHT-FILL.
                            double gmc = gridLayout.MaxContentBlockSize(gridChild);
                            it.FlexBaseSize = gmc > frameMain ? gmc : natural;
                        } else {
                            it.FlexBaseSize = natural;
                        }
                    }
                    break;
                }
            }
            if (it.FlexBaseSize < 0) it.FlexBaseSize = 0;
        }

        // CSS Flexbox §9.2 / §9.7: clamp by used min/max main-size. Mirrors
        // BlockLayout's min/max-width clamp so a flex item with
        // `width: 100%; max-width: 560px` doesn't bypass the cap once flex
        // takes over sizing. Resolves percent min/max against the
        // container's main size (the natural containing block for a flex
        // item's main axis). Returns `value` unchanged when no constraint
        // is set or only `auto`/`none` keywords are present.
        bool TryCurrentUnwrappedPlainBlockBase(BlockBox box, double frameMain, double containerMainSize, out double basis) {
            basis = 0;
            if (box == null) return false;
            if (box is FlexBox || box is Weva.Layout.Grid.GridBox) return false;

            double contentMax = 0;
            if (!TryCurrentUnwrappedMaxContent(box, ref contentMax)) return false;

            double frame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            double candidate = contentMax + frame;
            if (candidate < frameMain) candidate = frameMain;

            // If current lines touch the container edge, they may already be
            // wrapped. Keep the wide probe for that case because it is the only
            // way to see the true max-content width.
            if (candidate >= containerMainSize - LayoutEpsilons.HalfPixelEqual) return false;

            basis = candidate;
            return true;
        }

        static bool TryCurrentUnwrappedMaxContent(Boxes.Box node, ref double max) {
            int directLines = 0;
            for (int i = 0; i < node.Children.Count; i++) {
                var c = node.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) continue;
                if (c is LineBox lb) {
                    directLines++;
                    if (directLines > 1) return false;
                    double sum = 0;
                    for (int j = 0; j < lb.Children.Count; j++) sum += CurrentInlineFragmentWidth(lb.Children[j]);
                    if (sum > max) max = sum;
                    continue;
                }
                if (c is BlockBox cbb && cbb.IsInlineBlock) {
                    if (cbb.Width > max) max = cbb.Width;
                    continue;
                }
                if (c is FlexBox || c is Weva.Layout.Grid.GridBox) return false;
                if (c is BlockBox childBlock) {
                    double childMax = 0;
                    if (!TryCurrentUnwrappedMaxContent(childBlock, ref childMax)) return false;
                    if (childMax > max) max = childMax;
                    continue;
                }
                if (c is InlineBox || c is TextRun) return false;
            }
            return true;
        }

        static double CurrentInlineFragmentWidth(Boxes.Box box) {
            if (box == null) return 0;
            if (box is TextRun) return box.Width;
            if (box is InlineBox) return 0;
            return box.Width;
        }

        bool CanKeepCurrentLayoutForShrink(BlockBox box, double newBorderBoxWidth) {
            if (box == null) return false;
            if (box is FlexBox || box is Weva.Layout.Grid.GridBox || box is Weva.Layout.Tables.TableBox) return false;

            double contentMax = 0;
            if (!TryCurrentUnwrappedMaxContent(box, ref contentMax)) return false;
            double frame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            if (contentMax + frame > newBorderBoxWidth + LayoutEpsilons.HalfPixelEqual) return false;

            return HasOnlyStableWidthDescendants(box);
        }

        static bool HasOnlyStableWidthDescendants(Boxes.Box node) {
            for (int i = 0; i < node.Children.Count; i++) {
                var c = node.Children[i];
                if (c.Position == PositionType.Absolute || c.Position == PositionType.Fixed) return false;
                if (c is LineBox) continue;
                if (c is InlineBox || c is TextRun) return false;
                if (c is FlexBox || c is Weva.Layout.Grid.GridBox || c is Weva.Layout.Tables.TableBox) return false;
                if (c is BlockBox bb) {
                    if (bb.Style != null && !HasStableExplicitWidth(bb.Style)) return false;
                    if (!HasOnlyStableWidthDescendants(bb)) return false;
                    continue;
                }
            }
            return true;
        }

        static bool HasStableExplicitWidth(Weva.Css.Cascade.ComputedStyle style) {
            string raw = style?.Get(CssProperties.WidthId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return false;
            raw = raw.Trim();
            if (raw == "0") return true;
            return raw.EndsWith("px", System.StringComparison.Ordinal);
        }

        static void RetargetAnonymousBlockWidths(BlockBox box) {
            double contentWidth = box.ContentWidth;
            for (int i = 0; i < box.Children.Count; i++) {
                if (box.Children[i] is BlockBox child) {
                    if (child.Style == null) child.Width = contentWidth;
                    RetargetAnonymousBlockWidths(child);
                }
            }
        }

        // Walks `box` and every BlockBox descendant; for each LineBox child
        // recomputes the text-align centring / right offset against the
        // container's current ContentWidth. Mirrors InlineLayout.ApplyTextAlign
        // but in undo-then-reapply form, using the LineBox's
        // AppliedTextAlignDelta to back out the previous shift before stamping
        // the new one. Only touches lines whose container has a non-default
        // text-align — the no-op case (text-align:left) just clears any stale
        // delta.
        void ReapplyTextAlignOnLines(BlockBox box) {
            if (box == null) return;
            ReapplyTextAlignOnContainer(box);
            for (int i = 0; i < box.Children.Count; i++) {
                if (box.Children[i] is BlockBox child) {
                    ReapplyTextAlignOnLines(child);
                }
            }
        }

        static void ReapplyTextAlignOnContainer(BlockBox container) {
            if (container == null) return;
            // CSS 2.1 §9.2.1.1: anonymous block boxes inherit inheritable
            // properties (incl. text-align) from their enclosing non-anonymous
            // box. AnonymousBlockBox has Style=null, so fall back to the
            // parent's Style — mirrors InlineLayout.ApplyTextAlign. Without
            // this, the flex pass reads "left" for the anon block wrapping an
            // inline-flex atom (stock-dashboard `.r-px` price + `.pill`),
            // UNDOES BlockLayout's correct right-offset (step 1) and never
            // re-applies it (step 2 skips on "left") — the pill snaps back to
            // X=0 after the first RunFlexPasses (INLINE-ATOM-TEXTALIGN-RESET).
            var alignStyle = container.Style ?? container.Parent?.Style;
            string align = StyleResolver.TextAlign(alignStyle);
            string alignLast = StyleResolver.TextAlignLast(alignStyle, align);
            double contentW = container.ContentWidth;
            var children = container.ChildList;
            for (int i = 0; i < children.Count; i++) {
                if (!(children[i] is LineBox line)) continue;
                // Step 1: undo any previously applied delta.
                if (line.AppliedTextAlignDelta != 0) {
                    OffsetLineX(line, -line.AppliedTextAlignDelta);
                    line.AppliedTextAlignDelta = 0;
                }
                // Step 2: compute the new delta against the current ContentWidth.
                string lineAlign = line.IsFinalLine ? alignLast : align;
                if (lineAlign == "left" || string.IsNullOrEmpty(lineAlign)) continue;
                double extra = contentW - line.Width;
                if (extra <= 0) continue;
                double newDelta = 0;
                if (lineAlign == "right") newDelta = extra;
                else if (lineAlign == "center") newDelta = extra * 0.5;
                // `justify` mutates child widths; can't be cleanly redone here.
                // Fall back to the existing rebuild path for that case.
                if (newDelta != 0) {
                    OffsetLineX(line, newDelta);
                    line.AppliedTextAlignDelta = newDelta;
                }
            }
        }

        // Same shape as InlineLayout.OffsetLine — duplicated here because the
        // FlexLayout assembly already references Boxes, and InlineLayout's
        // copy is private. Cheap to keep aligned: two callers, both layout
        // internal.
        static void OffsetLineX(LineBox line, double dx) {
            var children = line.ChildList;
            for (int i = 0; i < children.Count; i++) {
                var run = children[i];
                if (run is TextRun tr) tr.X += dx;
                else if (run is BlockBox bb) bb.X += dx;
            }
        }

        double ClampMainSizeByMinMax(Item it, FlexBox container, double containerMainSize, bool isRow, double value) {
            var style = it.Box.Style;
            if (style == null) return value;
            double fs = StyleResolver.FontSizePx(style, container.Style, ctx);
            int minId = isRow ? CssProperties.MinWidthId : CssProperties.MinHeightId;
            int maxId = isRow ? CssProperties.MaxWidthId : CssProperties.MaxHeightId;
            var minParsed = style.GetParsed(minId);
            var maxParsed = style.GetParsed(maxId);
            var minR = StyleResolver.ResolveLengthFromParsed(minParsed, ctx, fs, containerMainSize);
            var maxR = StyleResolver.ResolveLengthFromParsed(maxParsed, ctx, fs, containerMainSize);
            // CSS Sizing L3 §5.2 + Flexbox §9.7: min/max use the same box-
            // sizing basis as the main-size property. `value` is the
            // already-border-box main size; under content-box (spec default)
            // the author's min/max is a CONTENT bound, so add frameMain
            // before comparing. Without this, `.stat { min-width: 80; padding:
            // 6 12; border: 1 }` (no box-sizing) clamped to outer=80 instead
            // of the spec-correct 106.
            bool borderBox = IsBorderBox(style);
            double frameMain = isRow
                ? (it.Box.PaddingLeft + it.Box.PaddingRight + it.Box.BorderLeft + it.Box.BorderRight)
                : (it.Box.PaddingTop + it.Box.PaddingBottom + it.Box.BorderTop + it.Box.BorderBottom);
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frameMain;
                if (value < minPx) value = minPx;
            } else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = containerMainSize * (minR.Percent * 0.01);
                if (!borderBox) mp += frameMain;
                if (value < mp) value = mp;
            } else if (IsAutoMinMainSize(style, minId)) {
                // Auto min main size is content-based unless the item is
                // scrollable. Row min-width:auto needs this too, or cramped
                // headers shrink item boxes below their text and overlap.
                double autoMin = AutomaticMinimumMainSize(it, isRow);
                if (value < autoMin) value = autoMin;
            }
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frameMain;
                if (value > maxPx) value = maxPx;
            } else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = containerMainSize * (maxR.Percent * 0.01);
                if (!borderBox) mp += frameMain;
                if (value > mp) value = mp;
            }
            if (value < 0) value = 0;
            return value;
        }

        static bool IsAutoMinMainSize(Weva.Css.Cascade.ComputedStyle style, int minId) {
            string raw = style?.Get(minId);
            return string.IsNullOrEmpty(raw) || raw == "auto";
        }

        // CSS Flexbox §8.3 / Sizing L3 §5.2: the stretched cross size must be
        // clamped against the item's min/max cross-axis size. Mirror of
        // ClampMainSizeByMinMax but for the cross axis (height in row flex,
        // width in column flex). `auto` and missing values are treated as
        // "no clamp", per spec. min/max are content-box bounds by default and
        // get the item's cross-axis frame added before the comparison unless
        // box-sizing is border-box (`value` is the just-assigned border-box
        // cross size).
        double ClampCrossSizeByMinMax(Item it, double lineCrossSize, bool isRow, double value) {
            var style = it.Box.Style;
            if (style == null) return value;
            double fs = StyleResolver.FontSizePx(style, it.Box.Parent?.Style, ctx);
            int minId = isRow ? CssProperties.MinHeightId : CssProperties.MinWidthId;
            int maxId = isRow ? CssProperties.MaxHeightId : CssProperties.MaxWidthId;
            var minParsed = style.GetParsed(minId);
            var maxParsed = style.GetParsed(maxId);
            // Percentages on the cross-axis min/max resolve against the
            // container's cross-axis size (the line cross size matches that
            // for a single-line flex; multi-line uses the line we're in).
            var minR = StyleResolver.ResolveLengthFromParsed(minParsed, ctx, fs, lineCrossSize);
            var maxR = StyleResolver.ResolveLengthFromParsed(maxParsed, ctx, fs, lineCrossSize);
            bool borderBox = IsBorderBox(style);
            double frameCross = isRow
                ? (it.Box.PaddingTop + it.Box.PaddingBottom + it.Box.BorderTop + it.Box.BorderBottom)
                : (it.Box.PaddingLeft + it.Box.PaddingRight + it.Box.BorderLeft + it.Box.BorderRight);
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frameCross;
                if (value < minPx) value = minPx;
            } else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = lineCrossSize * (minR.Percent * 0.01);
                if (!borderBox) mp += frameCross;
                if (value < mp) value = mp;
            }
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frameCross;
                if (value > maxPx) value = maxPx;
            } else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = lineCrossSize * (maxR.Percent * 0.01);
                if (!borderBox) mp += frameCross;
                if (value > mp) value = mp;
            }
            if (value < 0) value = 0;
            return value;
        }

        static bool HasScrollableOverflowOnMainAxis(BlockBox box, bool isRow) {
            if (box?.Style == null) return false;
            string raw = box.Style.Get(isRow ? CssProperties.OverflowXId : CssProperties.OverflowYId);
            if (string.IsNullOrEmpty(raw) || raw == "visible") raw = box.Style.Get(CssProperties.OverflowId);
            return raw == "auto" || raw == "scroll" || raw == "hidden" || raw == "overlay";
        }

        double AutomaticMinimumMainSize(Item it, bool isRow) {
            var box = it.Box;
            if (box == null || HasScrollableOverflowOnMainAxis(box, isRow)) return 0;

            if (isRow) {
                if (box is Weva.Layout.Grid.GridBox || box is FlexBox) {
                    return PositioningPass.MinContentWidth(box);
                }
                double inlineFrame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
                return PositioningPass.MinContentWidth(box) + inlineFrame;
            }

            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            if (box is Weva.Layout.Grid.GridBox grid) {
                // Stretch-free: GridIntrinsicCross reads the grid's POSITIONED
                // children (their FILLED bottom extent), so for a grid the flex
                // already stretched to fill, it returns the filled Height and
                // floors HypotheticalMainSize back up to it — re-ballooning a
                // `min-height:100vh` column even after ComputeBaseSize gave the
                // stretch-free base. MaxContentBlockSize sizes 1fr rows as
                // content, independent of the assigned Height. Falls back to the
                // positioned-extent path during a reflow probe. See
                // FLEX-MINHEIGHT-FILL.
                if (gridLayout != null && !reflowing) {
                    double mc = gridLayout.MaxContentBlockSize(grid);
                    if (mc > 0) return mc; // already border-box (includes frame)
                }
                double content = PositioningPass.GridIntrinsicCross(grid);
                if (content > 0) return content + frame;
            } else if (box is FlexBox flex) {
                return PositioningPass.FlexIntrinsicCross(flex) + frame;
            }

            return box.Height > 0 ? box.Height : 0;
        }

        static void ComputeOuterMargins(Item it, bool isRow) {
            var b = it.Box;
            if (isRow) {
                it.LeadMainMargin = b.MarginLeft;
                it.OuterMainMarginSum = b.MarginLeft + b.MarginRight;
                it.LeadCrossMargin = b.MarginTop;
                it.TrailCrossMargin = b.MarginBottom;
                it.LeadMainMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginLeftId);
                it.TrailMainMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginRightId);
                // CSS Flexbox §8.1/§8.2: cross-axis auto margins for row flex
                // (top/bottom are cross for row direction).
                it.LeadCrossMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginTopId);
                it.TrailCrossMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginBottomId);
            } else {
                it.LeadMainMargin = b.MarginTop;
                it.OuterMainMarginSum = b.MarginTop + b.MarginBottom;
                it.LeadCrossMargin = b.MarginLeft;
                it.TrailCrossMargin = b.MarginRight;
                it.LeadMainMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginTopId);
                it.TrailMainMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginBottomId);
                // CSS Flexbox §8.1/§8.2: cross-axis auto margins for column flex
                // (left/right are cross for column direction).
                it.LeadCrossMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginLeftId);
                it.TrailCrossMarginAuto = IsMarginAutoRaw(b, CssProperties.MarginRightId);
            }
        }

        // BlockLayout's ResolveLengthPx maps "auto" → 0 for margin sides,
        // so the keyword is lost by the time we reach flex layout. Peek at
        // the raw style string to keep the auto information alive for
        // JustifyItemsAlongMain's free-space distribution.
        static bool IsMarginAutoRaw(BlockBox box, int propId) {
            if (box.Style == null) return false;
            var raw = box.Style.Get(propId);
            return raw == "auto";
        }

        // Returns a slice of scratch.FlexLines that holds [linesStart, linesStart+n)
        // FlexLine instances rented from the pool. Caller pops the slice and
        // returns each line via scratch.ReturnFlexLine when done.
        List<FlexLine> CollectLines(List<Item> items, FlexProperties props, double containerMainSize) {
            // Reuse a thin per-frame view list so the rest of the algorithm can
            // index by line position. The view itself is just a dispatch handle;
            // the FlexLine instances inside are pooled.
            var lines = scratch.FlexLines;
            int linesStart = lines.Count;
            if (items.Count == 0) {
                // Caller treats the [linesStart, linesStart) empty slice as 0
                // lines. Hand back a sub-view via a fresh List<>; alloc-cost is
                // amortised by FlexLinePool / FlexLines retaining backing arrays.
                return EmptyLineView();
            }

            if (props.Wrap == FlexWrap.NoWrap) {
                var line = scratch.RentFlexLine();
                for (int i = 0; i < items.Count; i++) line.ItemIndices.Add(i);
                lines.Add(line);
                return BuildLineView(lines, linesStart);
            }

            var current = scratch.RentFlexLine();
            double currentSum = 0;
            for (int i = 0; i < items.Count; i++) {
                double itemOuter = items[i].HypotheticalMainSize + items[i].OuterMainMarginSum;
                double tentative = currentSum;
                if (current.ItemIndices.Count > 0) tentative += props.MainGap;
                tentative += itemOuter;
                // K4: wrap tolerance is a relative epsilon (~1 part in 100000 of
                // the container's main size) with an absolute floor, so that
                // sub-pixel jitter from FontEngine measurement and accumulated
                // double rounding in base/grow computations can't flip an item
                // across the wrap boundary between re-layouts. The relative
                // scaling stays far below half a CSS px, so an intentional
                // half-pixel overflow still wraps as expected.
                double wrapEpsilon = containerMainSize * LayoutEpsilons.LayoutNoise;
                if (wrapEpsilon < FlexWrapEpsilonMinPx) wrapEpsilon = FlexWrapEpsilonMinPx;
                if (current.ItemIndices.Count > 0 && tentative > containerMainSize + wrapEpsilon) {
                    lines.Add(current);
                    current = scratch.RentFlexLine();
                    current.ItemIndices.Add(i);
                    currentSum = itemOuter;
                } else {
                    if (current.ItemIndices.Count > 0) currentSum += props.MainGap;
                    current.ItemIndices.Add(i);
                    currentSum += itemOuter;
                }
            }
            if (current.ItemIndices.Count > 0) lines.Add(current);
            else scratch.ReturnFlexLine(current);
            return BuildLineView(lines, linesStart);
        }

        // Materialises a List<FlexLine> view spanning [start, lines.Count). The
        // existing helpers (DistributeLinesAlongCross etc.) expect a List<FlexLine>
        // they can mutate (.CrossOffset etc.). The FlexLine instances are shared
        // — mutations land on the pooled FlexLine which we return at tear-down.
        //
        // Because Layout is recursive (a flex item may itself be a FlexBox), each
        // call needs its own view: we rent one from scratch.LineViewPool, which
        // is a stack of pre-allocated List<FlexLine> wrappers so the depth-N
        // recursion costs N rents but zero list-allocations after warmup.
        List<FlexLine> BuildLineView(List<FlexLine> source, int start) {
            var view = scratch.RentLineView();
            for (int i = start; i < source.Count; i++) view.Add(source[i]);
            return view;
        }
        List<FlexLine> EmptyLineView() {
            return scratch.RentLineView();
        }

        void ResolveFlexibleLengths(FlexLine line, List<Item> items, FlexProperties props, double containerMainSize, bool definiteMain, bool isRow, FlexBox container) {
            int n = line.ItemIndices.Count;
            if (n == 0) return;

            double gapSum = props.MainGap * (n - 1);
            double availableSpace = containerMainSize - gapSum;

            double totalBase = 0;
            for (int k = 0; k < n; k++) {
                var it = items[line.ItemIndices[k]];
                totalBase += it.HypotheticalMainSize + it.OuterMainMarginSum;
            }

            for (int k = 0; k < n; k++) {
                items[line.ItemIndices[k]].TargetMainSize = items[line.ItemIndices[k]].HypotheticalMainSize;
            }

            // Indefinite main: container will adopt totalBase (see
            // FinalizeContainerMainSize). No grow/shrink is meaningful
            // because no overflow or free space exists by definition.
            // Skip both branches — TargetMainSize already equals
            // HypotheticalMainSize from the loop above.
            if (!definiteMain) {
                double mainIndef = 0;
                for (int k = 0; k < n; k++) {
                    var it = items[line.ItemIndices[k]];
                    mainIndef += it.TargetMainSize + it.OuterMainMarginSum;
                }
                mainIndef += gapSum;
                line.MainSize = mainIndef;
                return;
            }

            if (totalBase < availableSpace - LayoutEpsilons.SubPixelEqual) {
                // CSS Flexbox §9.7.2 "Resolve Flexible Lengths" — grow branch.
                // Iterative loop per spec: distribute the remaining free space to
                // unfrozen items proportionally by flex-grow factor, clamp each
                // at its min/max main size, freeze items that hit a max constraint,
                // then re-derive the remaining free space for the next round.
                // The unfrozen set shrinks monotonically → always terminates.
                // Cap at 8 iterations as a safety net.
                //
                // Per spec §9.7.2: in each iteration the "remaining free space" is
                // `availableSpace - frozenSum - unfrozenHypotheticalSum`.
                // Frozen items hold their clamped size permanently; unfrozen items
                // always start from their HypotheticalMainSize (the grow factor
                // fraction is on top of the base, not cumulative over iterations).
                bool[] frozen = new bool[n];
                const int maxIter = 8;
                for (int iter = 0; iter < maxIter; iter++) {
                    // Recompute remaining free space: availableSpace minus frozen
                    // items' committed sizes minus unfrozen items' hypothetical sizes.
                    double frozenSum = 0;
                    double unfrozenHypo = 0;
                    double totalGrow = 0;
                    for (int k = 0; k < n; k++) {
                        var it = items[line.ItemIndices[k]];
                        if (frozen[k]) {
                            frozenSum += it.TargetMainSize + it.OuterMainMarginSum;
                        } else {
                            unfrozenHypo += it.HypotheticalMainSize + it.OuterMainMarginSum;
                            totalGrow += it.Props.Grow;
                        }
                    }
                    double freeSpace = availableSpace - frozenSum - unfrozenHypo;
                    if (totalGrow <= 0 || freeSpace <= LayoutEpsilons.SubPixelEqual) break;
                    // Distribute remaining free space proportionally among unfrozen items.
                    bool anyFrozenThisIter = false;
                    for (int k = 0; k < n; k++) {
                        if (frozen[k]) continue;
                        var it = items[line.ItemIndices[k]];
                        double share = it.Props.Grow > 0 ? freeSpace * (it.Props.Grow / totalGrow) : 0;
                        double grown = it.HypotheticalMainSize + share;
                        double clamped = ClampMainSizeByMinMax(it, container, containerMainSize, isRow, grown);
                        it.TargetMainSize = clamped;
                        // Freeze items that hit a max constraint.
                        if (clamped < grown - LayoutEpsilons.SubPixelEqual) {
                            frozen[k] = true;
                            anyFrozenThisIter = true;
                        }
                    }
                    // If no new freezes, all unfrozen items accepted their share → done.
                    if (!anyFrozenThisIter) break;
                }
            } else if (totalBase > availableSpace + LayoutEpsilons.SubPixelEqual) {
                double overflow = totalBase - availableSpace;
                double totalScaledShrink = 0;
                for (int k = 0; k < n; k++) {
                    var it = items[line.ItemIndices[k]];
                    totalScaledShrink += it.Props.Shrink * it.HypotheticalMainSize;
                }
                if (totalScaledShrink > 0) {
                    for (int k = 0; k < n; k++) {
                        var it = items[line.ItemIndices[k]];
                        if (it.Props.Shrink <= 0) continue;
                        double scaled = it.Props.Shrink * it.HypotheticalMainSize;
                        double share = overflow * (scaled / totalScaledShrink);
                        double size = it.HypotheticalMainSize - share;
                        if (size < 0) size = 0;
                        // CSS Flexbox §9.7: flex shrink MUST NOT shrink an
                        // item below its min-content size. The engine's v1
                        // simplifications (line 12) treat the `min-content`
                        // keyword as `auto`, but a grid/flex sub-container
                        // with rigid inner content (fixed-px tracks or
                        // explicit-width items) has a SPEC min-content equal
                        // to its intrinsic inline size — the sum of tracks +
                        // gaps + frame for grid, or sum of items + gaps +
                        // frame for row-flex. Without this clamp, a grid
                        // child like `display: grid; grid-template-columns:
                        // repeat(8, 56px); gap: 6px; padding: 16px` (board
                        // = 524) inside a 503-wide flex parent shrinks to
                        // 503, but its tiles still occupy 8×56+7×6+padding,
                        // so the tiles render past the board's background
                        // (canonical match3 overflow bug). Per spec, the
                        // board should stay at 524 and overflow the parent.
                        //
                        // Restricted to grid/flex item-boxes only: plain
                        // BlockBox children's true min-content (longest
                        // unbreakable word) is not measurable here without
                        // a probe relayout, and using MaxContentWidth as a
                        // proxy would over-clamp shrink-to-fit text bubbles
                        // (chat.html .bubble's text max-content is the
                        // longest message — much wider than the bubble's
                        // shrunk width). MaxContentWidth already returns
                        // border-box for grid/flex (GridIntrinsicInline /
                        // FlexIntrinsicInline + HorizontalFrame), matching
                        // TargetMainSize's border-box semantics.
                        // CSS Flexbox L1 §4.5 — the automatic content-based
                        // minimum is OVERRIDDEN by an authored `min-width` /
                        // `min-height` value (including an explicit `0`). The
                        // rigid-sub-container floor above is the spec's auto-
                        // min for grid/flex children; gate it on the item
                        // actually USING auto min-main so that authored
                        // `min-width: 0` (or any explicit length) lets the
                        // item shrink past its intrinsic max-content. Without
                        // this gate a `.hero-picker-card-info {
                        // display:flex; flex-direction:column; min-width: 0 }`
                        // — sitting next to a 48px icon in a row-flex card —
                        // gets pinned to max(name, status) text width and the
                        // card overflows the 240px hero-picker list track
                        // instead of letting the `white-space:nowrap;
                        // text-overflow:ellipsis` children ellipsise. Match3
                        // board fix preserved: its grid container has no
                        // authored min-width so still hits this floor.
                        if (isRow && IsAutoMinMainSize(it.Box.Style, CssProperties.MinWidthId)) {
                            double minContent = -1;
                            if (it.Box is Weva.Layout.Grid.GridBox gridChild) {
                                // Grid tracks are rigid: min-content == the track
                                // sum (== max-content). Keep the board floor.
                                minContent = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(gridChild);
                            } else if (it.Box is FlexBox flexChild) {
                                // A flex sub-container's true min-content: for a
                                // COLUMN flex this is the widest item's min-content
                                // (so wrappable text can shrink + wrap), for a ROW
                                // flex MinContentWidth still falls back to the safe
                                // max-content floor. Using MaxContentWidth here
                                // unconditionally pinned a column-flex text card to
                                // its longest line and prevented wrapping.
                                minContent = Weva.Layout.Positioning.PositioningPass.MinContentWidth(flexChild);
                            }
                            if (minContent > size) size = minContent;
                        }
                        size = ClampMainSizeByMinMax(it, container, containerMainSize, isRow, size);
                        it.TargetMainSize = size;
                    }
                }
            }

            double main = 0;
            for (int k = 0; k < n; k++) {
                var it = items[line.ItemIndices[k]];
                main += it.TargetMainSize + it.OuterMainMarginSum;
            }
            main += gapSum;
            line.MainSize = main;
        }

        void ApplyMainSizesAndReflow(List<FlexLine> lines, List<Item> items, FlexBox container, bool isRow) {
            foreach (var line in lines) {
                for (int k = 0; k < line.ItemIndices.Count; k++) {
                    var it = items[line.ItemIndices[k]];
                    var box = it.Box;
                    if (isRow) {
                        // Snapshot the pre-flex Width BEFORE overwriting it so a
                        // shrink (e.g. .bubble pre-flex W = .msg.ContentWidth,
                        // post-flex W = max-content) can re-flow descendants at
                        // the new width. Without this, bubble-meta's text-align:
                        // right aligned "11:42" against the bubble-meta's
                        // PRE-flex 484.4px Width (sized by BlockLayout when
                        // .msg's content-width was the only constraint) — the
                        // shrunk 324px bubble around it then rendered the timestamp
                        // far to the right of its visible right padding. Mirrors
                        // the column-flex shrink in ComputeLineCrossSize which
                        // already calls ReflowIfShrunk after over-stretch cap.
                        double preW = box.Width;
                        box.Width = it.TargetMainSize;
                        if (box is FlexBox childFlex) {
                            bool mainChanged = System.Math.Abs(it.TargetMainSize - preW) > LayoutEpsilons.HalfPixelEqual;
                            if (!mainChanged) continue;
                            // Nested flex item that just got main-axis-shrunk
                            // by us: re-flow its descendants (via BlockLayout)
                            // at the new width before its own Layout runs.
                            // Otherwise FinalizeContainerCrossSize inside the
                            // recursive Layout would read children that still
                            // carry the pre-flex (body-wide) Width and stomp
                            // childFlex.Width back to that inflated value —
                            // canonical match3 HUD bug where
                            // `.left { display:flex } > .level
                            // { display:flex; flex-direction:column }` keeps
                            // its 1410px body-wide width inside an 89px parent.
                            // ReflowIfShrunk also re-runs FlexLayout on every
                            // FlexBox in the subtree (childFlex included), so
                            // we skip the explicit Layout call in that branch.
                            // For grow/no-shrink (TargetMainSize >= preW),
                            // ReflowIfShrunk early-returns and we run Layout
                            // directly as before.
                            if (it.TargetMainSize < preW - LayoutEpsilons.HalfPixelEqual) {
                                if (reflowing && blockLayout != null) {
                                    // We're ALREADY inside a parent's reflow (an
                                    // ancestor shrank and is re-flowing its subtree).
                                    // ReflowIfShrunk would no-op on the recursion
                                    // guard, leaving this nested flex child centered
                                    // against the pre-shrink (block-stacked) width —
                                    // the ancestor's RelayoutContentAt stretched it as
                                    // block flow, then ReflowFlexDescendants ran its
                                    // Layout BEFORE we (its flex parent) sized it down,
                                    // so its justify-content offset is stale. Re-stack +
                                    // re-flow it directly at the resolved main size so a
                                    // `justify-content:center` label lands in its own
                                    // box, not the grandparent's (flex-playground: cell
                                    // labels flung to the row centre).
                                    blockLayout.RelayoutContentAt(box, it.TargetMainSize);
                                    ReflowFlexDescendants(box);
                                } else {
                                    ReflowIfShrunk(box, it.TargetMainSize, preW);
                                }
                            } else {
                                Layout(childFlex);
                            }
                        } else if (!(box is Weva.Layout.Grid.GridBox)
                                   && !(box is Weva.Layout.Tables.TableBox)) {
                            ReflowIfShrunk(box, it.TargetMainSize, preW);
                        }
                    } else {
                        double preH = box.Height;
                        box.Height = it.TargetMainSize;
                        if (box is FlexBox childFlex) {
                            // A ROW-flex child of this COLUMN container has its
                            // height (= the child's CROSS axis) assigned by our
                            // main-axis distribution. When that grows the child
                            // past its content, mark the cross definite so the
                            // child's align-items:stretch fills the grown height
                            // instead of collapsing to content (FLEX-GROW-ROW-
                            // CROSS-STRETCH). Cleared otherwise so a column child
                            // (whose height is its own MAIN) is unaffected.
                            bool childRowGrew = ContainerIsRow(childFlex)
                                && it.TargetMainSize > preH + LayoutEpsilons.HalfPixelEqual;
                            childFlex.FlexParentAssignedCross = childRowGrew;
                            if (System.Math.Abs(it.TargetMainSize - preH) <= LayoutEpsilons.HalfPixelEqual) continue;
                            Layout(childFlex);
                            // CSS Flexbox §9: a column-flex child with `height:
                            // auto` re-computes its main-axis size from its own
                            // content sum via FinalizeContainerMainSize during
                            // the recursive Layout above. Propagate the new
                            // size back into TargetMainSize so the parent's
                            // JustifyItemsAlongMain (which runs AFTER this
                            // method) places subsequent siblings against the
                            // post-Layout child extent. Without this, the
                            // parent uses the pre-Layout child height (which
                            // came from BlockLayout's stale pre-flex stack of
                            // grandchildren and is typically smaller than the
                            // flex-resolved sum), and the next sibling lands
                            // ON TOP of this child — visible in settings.html
                            // as `.group` sections overlapping by ~78 px each
                            // inside the column-flex `.panel-body`.
                            //
                            // CARVE-OUT (CSS Flexbox §4.5 — Automatic Minimum
                            // Size): when the child is a scroll container on
                            // the main axis (overflow-y: auto / scroll /
                            // hidden / overlay in column flex), the parent's
                            // assigned TargetMainSize is the binding
                            // constraint — the child's content-driven height
                            // must NOT stomp it. Otherwise a
                            // `.scroll { flex:1; overflow-y:auto }` child of
                            // a height-constrained parent re-inflates to fit
                            // its 800 px of content instead of clipping to
                            // the 300 px allotment, defeating the scroll. The
                            // already-correct AutoMinimumMainSize=0 in
                            // ClampMainSizeByMinMax shrinks the item's
                            // HypotheticalMainSize per spec; this guard keeps
                            // the inner Layout from undoing that decision
                            // post-hoc. Pinned by
                            // FlexOverflowScrollMinSizeTests.
                            if (!HasScrollableOverflowOnMainAxis(box, isRow)
                                && System.Math.Abs(box.Height - it.TargetMainSize) > LayoutEpsilons.HalfPixelEqual) {
                                it.TargetMainSize = box.Height;
                            }
                        }
                    }
                }
            }
        }

        void ComputeLineCrossSize(FlexLine line, List<Item> items, FlexProperties props, bool isRow, double containerCrossSize) {
            double maxCross = 0;
            // Baseline-aligned items contribute (maxAboveBaseline + maxBelowBaseline)
            // to the line's cross-size — see CSS Flexbox §9.4.2.
            double maxAbove = 0;
            double maxBelow = 0;
            bool anyBaseline = false;
            // Clamp item cross-size to the container's content cross-size when
            // the container has a definite size. Without this, BlockLayout's
            // pre-flex pass (which sized children against the body width before
            // grid/positioning shrunk the container) leaves items at e.g. 1434px
            // wide inside a 240px column flex, and FlexEnd alignment then pushes
            // them way off-screen. Stretch alignment is handled later in
            // ApplyCrossAlignmentForLine; here we just cap the over-large
            // intrinsic value so line.CrossSize matches the container.
            for (int k = 0; k < line.ItemIndices.Count; k++) {
                var it = items[line.ItemIndices[k]];
                // Multi-pass convergence guard (audit #280): in row direction,
                // prefer first-pass `PreFlexCrossHeight` over post-stretch
                // `.Box.Height`. On pass 2 the live Height contains the
                // stretched-to-line.CrossSize value from pass 1, which would
                // re-feed line.CrossSize and grow it again. The stamp is
                // first-pass-only (BlockLayout/FlexLayout-stamped with
                // PreFlexCrossHeight==0 sentinel), so it never captures the
                // stretched value itself.
                //
                // EXCEPTION: a column-flex CHILD (FlexBox) and a grid CHILD
                // already had their own Height authoritatively assembled by
                // their inner FinalizeContainerMainSize/FinalizeGridSize —
                // which INCLUDES the inter-row gap. Forcing those to fall
                // back to PreFlexCrossHeight (which was stamped by an outer
                // BlockLayout pre-flex pass that block-stacks children WITHOUT
                // flex gaps) understates the row's cross extent by
                // (n-1) * row-gap and clips descendants via ancestor
                // overflow:hidden. Canonical case: a `.objective-card >
                // .objective-body { display:flex; flex-direction:column;
                // gap:4 } > .objective-reward > .reward-chip` — the body's
                // PreFlexCrossHeight is sum-without-gaps; the inner column
                // flex set body.Height correctly to sum+2*gap, but this
                // guard reverted line.CrossSize to the smaller value and the
                // reward chip got clipped by .objective-card's overflow.
                double itemCross;
                if (isRow && it.Box is BlockBox bbRow && bbRow.PreFlexCrossHeight > 0
                    && bbRow.PreFlexCrossHeight <= it.Box.Height + LayoutEpsilons.HalfPixelEqual
                    && !(it.Box is Weva.Layout.Grid.GridBox)
                    && !(it.Box is FlexBox)) {
                    itemCross = bbRow.PreFlexCrossHeight;
                } else {
                    itemCross = isRow ? it.Box.Height : it.Box.Width;
                }
                // Column flex, non-stretch align: a block child whose Width
                // meets OR EXCEEDS the container's content cross-size is an
                // over-stretch from BlockLayout. Probe max-content (which
                // handles flex/grid descendants via FlexIntrinsicInline and
                // GridIntrinsicInline in PositioningPass) and shrink.
                //
                // Width can EXCEED containerCrossSize when BlockLayout filled
                // the child against an OUTER context's width — e.g. match3
                // .right (89px row-flex) contains .moves (33px column-flex)
                // whose .num/.label block children were stretched to .right's
                // 89 before the inner flex pass sized .moves to 33. With
                // align-items:flex-end the unfit-shrunk 89-wide .num was then
                // placed at x=-56 inside the 33-wide .moves. Strict equality
                // (== 33) missed this entirely.
                // The over-stretch tell is `width >= container width` — but when
                // the item carries a max-width, BlockLayout's fill was already
                // CLAMPED to it, leaving width == resolved max-width and hiding
                // the evidence (chat `.msg { max-width:70%; align-self:flex-end }`
                // stayed at the full 70% instead of hugging its bubble). A width
                // sitting at its max-width clamp is the same fill-then-clamp
                // signature, so treat it as over-stretched too.
                double resolvedMaxCross = -1;
                bool overStretchedCross = !isRow && containerCrossSize > 0
                    && itemCross >= containerCrossSize - LayoutEpsilons.HalfPixelEqual;
                if (!isRow && !overStretchedCross && containerCrossSize > 0 && it.Box.Style != null) {
                    string mwRaw = it.Box.Style.Get(CssProperties.MaxWidthId);
                    if (!string.IsNullOrEmpty(mwRaw) && mwRaw != "none") {
                        double fsi = StyleResolver.FontSizePx(it.Box.Style, null, ctx);
                        var mwR = StyleResolver.ResolveLength(mwRaw, it.Box.Style, ctx, fsi, containerCrossSize);
                        double mwPx = mwR.Kind == StyleResolver.LengthKind.Length ? mwR.Pixels
                            : mwR.Kind == StyleResolver.LengthKind.Percent ? containerCrossSize * mwR.Percent * 0.01
                            : -1;
                        if (mwPx >= 0 && !IsBorderBox(it.Box.Style)) {
                            mwPx += it.Box.PaddingLeft + it.Box.PaddingRight + it.Box.BorderLeft + it.Box.BorderRight;
                        }
                        if (mwPx >= 0) {
                            resolvedMaxCross = mwPx;
                            if (itemCross >= mwPx - LayoutEpsilons.HalfPixelEqual) overStretchedCross = true;
                        }
                    }
                }
                // A fit-content shrink from an earlier pass is ONE-WAY: once
                // this path shrinks an item, later passes see
                // `itemCross < containerCrossSize`, the over-stretch tell never
                // fires again, and the item stays at the stale fit even when
                // the container's cross-size has since GROWN. Canonical
                // inputtest `.tile` (grid item, column flex, align-items:
                // center): an early pass ran before the grid sized its 1fr
                // tracks, fit the anonymous "Crafting" text item against a
                // ~10px container, and the cached 10px width survived into the
                // real 243px layout — the 61px text then painted 26px right of
                // center. fit-content is a function of the available size
                // (CSS Sizing 3 §5.1), so when avail changes for an item we
                // previously fit (ShrinkFitCachedWidth stamp), re-fit in
                // EITHER direction.
                double availCross = containerCrossSize - it.LeadCrossMargin - it.TrailCrossMargin;
                // fit-content never exceeds the max-width clamp.
                if (resolvedMaxCross >= 0 && resolvedMaxCross < availCross) availCross = resolvedMaxCross;
                bool staleShrinkFit = !isRow && !overStretchedCross && containerCrossSize > 0
                    && it.Box.ShrinkFitCachedWidth >= 0
                    && System.Math.Abs(it.Box.ShrinkFitCachedAvail - availCross) > LayoutEpsilons.HalfPixelEqual;
                if ((overStretchedCross || staleShrinkFit)
                    && !HasDefiniteCrossOnItem(it.Box, isRow)) {
                    AlignSelf effShrink = ResolveAlignSelf(it.Props.AlignSelf, props.AlignItems);
                    if (effShrink != AlignSelf.Stretch && effShrink != AlignSelf.Baseline
                        && blockLayout != null && !reflowing) {
                        double avail = availCross;
                        double fitted;
                        if (System.Math.Abs(it.Box.ShrinkFitCachedAvail - avail) < LayoutEpsilons.HalfPixelEqual
                            && it.Box.ShrinkFitCachedWidth >= 0) {
                            fitted = it.Box.ShrinkFitCachedWidth;
                        } else if (it.Box is FlexBox || it.Box is Weva.Layout.Grid.GridBox) {
                            // Flex/grid sub-containers: use MaxContentWidth non-
                            // destructively. Calling RelayoutContentAt(1e6) first
                            // would stomp the sub-container's flex/grid children with
                            // block-stacked positions. If the column-flex container
                            // assigns the same width to this item as snapshotWidth
                            // (no ReflowIfShrunk), the inner flex children remain
                            // block-stacked — same class of bug as b2f0d02.
                            // MaxContentWidth for Flex/Grid already returns the full
                            // border-box width via FlexIntrinsicInline/GridIntrinsicInline.
                            double frame2 = it.Box.PaddingLeft + it.Box.PaddingRight + it.Box.BorderLeft + it.Box.BorderRight;
                            double mc2 = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(it.Box);
                            if (mc2 < frame2) mc2 = frame2;
                            fitted = System.Math.Min(mc2, avail);
                            it.Box.ShrinkFitCachedAvail = avail;
                            it.Box.ShrinkFitCachedWidth = fitted;
                        } else {
                            // SAVE+RESTORE the `reflowing` flag — see the row-
                            // direction probe above for the rationale (avoid
                            // clobbering an outer ReflowIfShrunk's true state).
                            bool wasReflowing = reflowing;
                            reflowing = true;
                            double snapshotWidth = it.Box.Width;
                            double snapshotX = it.Box.X;
                            try {
                                blockLayout.RelayoutContentAt(it.Box, 1e6);
                                double frame = it.Box.PaddingLeft + it.Box.PaddingRight + it.Box.BorderLeft + it.Box.BorderRight;
                                double mc = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(it.Box) + frame;
                                if (mc < frame) mc = frame;
                                fitted = System.Math.Min(mc, avail);
                            } finally {
                                reflowing = wasReflowing;
                                // Re-run BlockLayout at the snapshot width so
                                // descendants don't keep the 1e6 layout — see
                                // the row-direction probe above for the full
                                // explanation. Probe-only widths (≥1e5) skip
                                // the re-flow and just reset the box's own
                                // dimensions; the outer flex pass will assign
                                // a real fitted width and ReflowIfShrunk
                                // handles descendants from there.
                                if (snapshotWidth > 0 && snapshotWidth < 1e5) {
                                    blockLayout.RelayoutContentAt(it.Box, snapshotWidth);
                                } else {
                                    it.Box.Width = snapshotWidth;
                                }
                                it.Box.X = snapshotX;
                            }
                            it.Box.ShrinkFitCachedAvail = avail;
                            it.Box.ShrinkFitCachedWidth = fitted;
                        }
                        if (fitted < itemCross - LayoutEpsilons.HalfPixelEqual) {
                            double preW = it.Box.Width;
                            it.Box.Width = fitted;
                            ReflowIfShrunk(it.Box, fitted, preW);
                            itemCross = fitted;
                        } else if (staleShrinkFit
                                   && fitted > itemCross + LayoutEpsilons.HalfPixelEqual) {
                            // Stale-fit GROWTH: the container widened since the
                            // earlier fit. Re-lay content at the wider fit so
                            // lines re-wrap; flex/grid sub-containers keep their
                            // own inner layout and only the border-box widens.
                            if (it.Box is FlexBox || it.Box is Weva.Layout.Grid.GridBox) {
                                it.Box.Width = fitted;
                            } else {
                                bool wasReflowingGrow = reflowing;
                                reflowing = true;
                                try {
                                    blockLayout.RelayoutContentAt(it.Box, fitted);
                                    ReflowFlexDescendants(it.Box);
                                } finally { reflowing = wasReflowingGrow; }
                            }
                            itemCross = fitted;
                        }
                    }
                }
                AlignSelf eff = ResolveAlignSelf(it.Props.AlignSelf, props.AlignItems);
                if (containerCrossSize > 0 && itemCross > containerCrossSize) {
                    // CSS Flexbox §9.4: with non-stretch cross-axis alignment
                    // (center / start / end / flex-start / flex-end), an item
                    // whose intrinsic cross-size exceeds the container's
                    // cross-size is allowed to OVERFLOW — Chrome reports the
                    // larger size and positions the item accordingly. Only
                    // stretch (and the column-flex over-stretch path above)
                    // should resize the item.
                    //
                    // Canonical row-flex case: `.bar > .label { display:flex;
                    // align-items:center }` is a 4.69px-tall absolute box
                    // containing two `<span>` flex items whose text intrinsic
                    // height is ~12px. Pre-fix the spans were clamped to
                    // 4.69 (the bar's tiny height), wrecking text rendering
                    // and contributing 80-100 to the score deficit.
                    //
                    // We still want line.CrossSize to track the container's
                    // cross-size (so a flex container with `auto` height
                    // doesn't inflate to the overflowing item's size — that
                    // would defeat the author's `inset:0`). Cap the cross
                    // contribution below; leave it.Box dimensions intact.
                    bool allowOverflow = (eff == AlignSelf.Center
                                          || eff == AlignSelf.Start || eff == AlignSelf.End
                                          || eff == AlignSelf.FlexStart || eff == AlignSelf.FlexEnd);
                    if (!allowOverflow) {
                        itemCross = containerCrossSize - it.LeadCrossMargin - it.TrailCrossMargin;
                        if (itemCross < 0) itemCross = 0;
                        if (isRow) {
                            it.Box.Height = itemCross;
                            // aspect-ratio derives width from the just-assigned
                            // height when width is auto. Mirror the column-flex
                            // branch below — both serve `aspect-ratio + cross
                            // axis stretched, other axis auto`.
                            ApplyAspectRatioFromHeight(it.Box);
                        } else {
                            double preW = it.Box.Width;
                            it.Box.Width = itemCross;
                            ReflowIfShrunk(it.Box, itemCross, preW);
                            // After the stretch widens (or leaves unchanged)
                            // the item, derive height from `aspect-ratio` if
                            // height is auto. ReflowIfShrunk only kicks in
                            // when the item SHRINKS — for the common growth
                            // case (`.portrait-frame { width: 100%;
                            // aspect-ratio: 3/4 }` inside a column flex),
                            // BlockLayout's FinalizeBlockSize ran with
                            // width=0 and the ratio branch never fired, so
                            // height was content-derived. Apply it now.
                            ApplyAspectRatioFromWidth(it.Box);
                        }
                    }
                }
                it.CrossSize = itemCross;
                // For line.CrossSize accounting we cap at the container's cross
                // size when the item is over-large with non-stretch alignment —
                // otherwise FinalizeContainerCrossSize would inflate the
                // container's auto-sized cross axis to match the overflowing
                // item, undoing the author's intent (e.g. `.bar > .label`
                // pinned by `inset:0`).
                double accountedCross = itemCross;
                if (containerCrossSize > 0 && accountedCross > containerCrossSize) {
                    accountedCross = containerCrossSize - it.LeadCrossMargin - it.TrailCrossMargin;
                    if (accountedCross < 0) accountedCross = 0;
                }
                if (isRow && eff == AlignSelf.Baseline) {
                    anyBaseline = true;
                    double bl = ComputeItemBaseline(it.Box);
                    double above = bl + it.LeadCrossMargin;
                    double below = (accountedCross - bl) + it.TrailCrossMargin;
                    if (above > maxAbove) maxAbove = above;
                    if (below > maxBelow) maxBelow = below;
                } else {
                    double outer = accountedCross + it.LeadCrossMargin + it.TrailCrossMargin;
                    if (outer > maxCross) maxCross = outer;
                }
            }
            if (anyBaseline) {
                double baselineLine = maxAbove + maxBelow;
                if (baselineLine > maxCross) maxCross = baselineLine;
            }
            line.CrossSize = maxCross;
        }

        static void DistributeLinesAlongCross(List<FlexLine> lines, FlexProperties props, double containerCrossSize, double linesTotal) {
            if (lines.Count == 0) return;
            int n = lines.Count;

            if (n == 1) {
                lines[0].CrossOffset = 0;
                if (props.AlignContent == AlignContent.Stretch && containerCrossSize > lines[0].CrossSize) {
                    lines[0].CrossSize = containerCrossSize;
                }
                return;
            }

            double freeSpace = containerCrossSize - linesTotal;

            double offset = 0;
            double extraBetween = 0;
            switch (props.AlignContent) {
                case AlignContent.Stretch: {
                    double extraEach = freeSpace > 0 ? freeSpace / n : 0;
                    double cursor = 0;
                    for (int i = 0; i < n; i++) {
                        if (extraEach > 0) lines[i].CrossSize += extraEach;
                        lines[i].CrossOffset = cursor;
                        cursor += lines[i].CrossSize;
                        if (i < n - 1) cursor += props.CrossGap;
                    }
                    return;
                }
                case AlignContent.FlexStart:
                    offset = 0; extraBetween = 0; break;
                case AlignContent.FlexEnd:
                    offset = freeSpace; extraBetween = 0; break;
                case AlignContent.Center:
                    offset = freeSpace * 0.5; extraBetween = 0; break;
                case AlignContent.SpaceBetween:
                    offset = 0;
                    extraBetween = n > 1 && freeSpace > 0 ? freeSpace / (n - 1) : 0;
                    break;
                case AlignContent.SpaceAround:
                    // CSS Box Alignment L3 §8: "If the leftover free-space is
                    // negative or there is only a single alignment subject,
                    // [space-around] behaves as center." With the `> 0` guard
                    // alone, an overflowing line stack packed to the cross-start
                    // edge instead of centring around the available area.
                    if (freeSpace > 0) {
                        extraBetween = freeSpace / n;
                        offset = extraBetween * 0.5;
                    } else {
                        extraBetween = 0;
                        offset = freeSpace * 0.5;
                    }
                    break;
                case AlignContent.SpaceEvenly:
                    // CSS Box Alignment L3 §8: space-evenly with negative free
                    // space also falls back to center, same as space-around.
                    if (freeSpace > 0) {
                        extraBetween = freeSpace / (n + 1);
                        offset = extraBetween;
                    } else {
                        extraBetween = 0;
                        offset = freeSpace * 0.5;
                    }
                    break;
            }

            double y = offset;
            for (int i = 0; i < n; i++) {
                lines[i].CrossOffset = y;
                y += lines[i].CrossSize;
                if (i < n - 1) y += props.CrossGap + extraBetween;
            }
        }

        void AlignItemsInLine(FlexLine line, List<Item> items, FlexProperties props, bool isRow) {

            // Baseline alignment only meaningful in row flexes (cross-axis is
            // vertical and baseline is a horizontal line). For column flex the
            // synthesised baseline degenerates to the cross-start edge — see
            // CSS Flexbox §9.4.1 — so we fall back to FlexStart there.
            double maxBaseline = 0;
            bool anyBaseline = false;
            if (isRow) {
                for (int k = 0; k < line.ItemIndices.Count; k++) {
                    var it = items[line.ItemIndices[k]];
                    AlignSelf eff = ResolveAlignSelf(it.Props.AlignSelf, props.AlignItems);
                    if (eff != AlignSelf.Baseline) continue;
                    anyBaseline = true;
                    double itemBaseline = ComputeItemBaseline(it.Box) + it.LeadCrossMargin;
                    if (itemBaseline > maxBaseline) maxBaseline = itemBaseline;
                }
            }

            for (int k = 0; k < line.ItemIndices.Count; k++) {
                var it = items[line.ItemIndices[k]];
                // Re-evaluate the cross-stretch-grow flag each pass: cleared here,
                // re-set below only if we actually grow this column item's height.
                if (it.Box is BlockBox bbClr) { bbClr.FlexCrossStretchedMain = false; bbClr.FlexCrossStretchedMainSize = 0; }
                AlignSelf effective = ResolveAlignSelf(it.Props.AlignSelf, props.AlignItems);
                double available = line.CrossSize - it.LeadCrossMargin - it.TrailCrossMargin;
                double itemCross = it.CrossSize;
                double crossPos = 0;

                // CSS Flexbox §8.1/§8.2: cross-axis auto margins absorb free
                // cross space BEFORE align-self runs. When any cross auto margin
                // is present and free space is positive, distribute the space
                // to the auto margins and skip align-self (it has no effect).
                bool leadAuto = it.LeadCrossMarginAuto;
                bool trailAuto = it.TrailCrossMarginAuto;
                if (leadAuto || trailAuto) {
                    double freeCross = available - itemCross;
                    if (freeCross > 0) {
                        if (leadAuto && trailAuto) {
                            // Both auto: equal distribution → center the item.
                            crossPos = freeCross * 0.5 + it.LeadCrossMargin;
                        } else if (leadAuto) {
                            // Only lead auto: lead absorbs all free space → item
                            // is pinned to the cross-end (bottom for row flex).
                            crossPos = freeCross + it.LeadCrossMargin;
                        } else {
                            // Only trail auto: trail absorbs all free space →
                            // item is pinned to the cross-start (top for row flex).
                            crossPos = it.LeadCrossMargin;
                        }
                        it.CrossPos = crossPos;
                        it.CrossSize = itemCross;
                        continue; // align-self has no effect when auto margin consumed free space
                    }
                    // No free space (or negative): auto margins resolve to 0;
                    // fall through to align-self with the original cross margins.
                }

                if (effective == AlignSelf.Stretch) {
                    bool definite = HasDefiniteCrossOnItem(it.Box, isRow);
                    if (!definite) {
                        itemCross = available;
                        itemCross = ClampCrossSizeByMinMax(it, line.CrossSize, isRow, itemCross);
                        if (isRow) {
                            double preH = it.Box.Height;
                            it.Box.Height = itemCross;
                            ApplyAspectRatioFromHeight(it.Box);
                            // A COLUMN-flex item grown taller on the cross axis has
                            // its MAIN axis (height) enlarged: re-flow so margin:auto
                            // / flex-grow / justify-content distribute into the new
                            // height. Mark it FlexCrossStretchedMain so the recursive
                            // Layout treats the height as definite (HasDefiniteMain)
                            // and FinalizeContainerMainSize preserves it instead of
                            // collapsing back to the content extent. ReflowIfShrunk
                            // only covers shrink. (FLEX-CROSS-STRETCH-GROW-REFLOW.)
                            if (it.Box is FlexBox grownFlex && !reflowing) {
                                if (!ContainerIsRow(grownFlex)) {
                                    // Mark the stretched height definite EVERY pass it
                                    // is stretched (not only when it grew THIS pass):
                                    // otherwise the per-pass reset clears it in steady
                                    // state, FinalizeContainerMainSize collapses the
                                    // height back to content, and the layout flickers
                                    // between filled and empty across passes. Re-flow
                                    // only when it actually grew (a no-op otherwise —
                                    // RunFlexPasses' own Layout(item) preserves it).
                                    grownFlex.FlexCrossStretchedMain = true;
                                    grownFlex.FlexCrossStretchedMainSize = itemCross;
                                    if (itemCross > preH + LayoutEpsilons.HalfPixelEqual) {
                                        Layout(grownFlex);
                                    }
                                } else {
                                    // A ROW-flex item stretched taller by its row
                                    // parent has its CROSS axis enlarged: its own
                                    // align-items / align-content must re-distribute
                                    // against the new height. Mark the parent-assigned
                                    // cross definite (rowFlexGrownCross consumes this
                                    // in clampCrossSize) every stretched pass — same
                                    // steady-state-flicker lesson as the column branch
                                    // — and re-run its layout when it actually grew.
                                    // Without this, `.row { height:52px } .ep {
                                    // display:flex; align-items:center }` kept the
                                    // text centred against the PRE-stretch content
                                    // height: visually top-aligned in the button
                                    // (episode-stats "EPISODE 1").
                                    grownFlex.FlexParentAssignedCross = true;
                                    if (itemCross > preH + LayoutEpsilons.HalfPixelEqual) {
                                        Layout(grownFlex);
                                    }
                                }
                            }
                        } else {
                            double preW = it.Box.Width;
                            it.Box.Width = itemCross;
                                ReflowIfShrunk(it.Box, itemCross, preW);
                            ApplyAspectRatioFromWidth(it.Box);
                        }
                    }
                    crossPos = 0;
                } else if (effective == AlignSelf.Baseline) {
                    if (isRow && anyBaseline) {
                        double itemBl = ComputeItemBaseline(it.Box);
                        crossPos = maxBaseline - itemBl - it.LeadCrossMargin;
                    } else {
                        crossPos = 0;
                    }
                } else if (effective == AlignSelf.FlexStart || effective == AlignSelf.Start) {
                    crossPos = 0;
                } else if (effective == AlignSelf.FlexEnd || effective == AlignSelf.End) {
                    crossPos = available - itemCross;
                } else if (effective == AlignSelf.Center) {
                    crossPos = (available - itemCross) * 0.5;
                }
                crossPos += it.LeadCrossMargin;

                it.CrossPos = crossPos;
                it.CrossSize = itemCross;
            }
        }

        // First-baseline of a flex item used by `align-items: baseline`.
        // Per CSS Flexbox §9.4.1: the first baseline is the baseline of the
        // item's first in-flow line box (recursively). For items without a
        // text baseline (e.g. an image or empty box) the synthesised baseline
        // is the bottom of the content area. For v1 we walk the box subtree
        // looking for the first LineBox; if none, we use the item's height.
        double ComputeItemBaseline(BlockBox box) {
            double y = 0;
            var line = FindFirstLineBox(box, ref y);
            if (line != null) return y + line.Baseline;
            return box.Height;
        }

        static LineBox FindFirstLineBox(Boxes.Box parent, ref double yOffset) {
            for (int i = 0; i < parent.Children.Count; i++) {
                var c = parent.Children[i];
                if (c is LineBox lb) {
                    yOffset += lb.Y;
                    return lb;
                }
                if (c is BlockBox bb) {
                    double saved = yOffset;
                    yOffset += bb.Y;
                    var found = FindFirstLineBox(bb, ref yOffset);
                    if (found != null) return found;
                    yOffset = saved;
                }
            }
            return null;
        }

        static AlignSelf ResolveAlignSelf(AlignSelf self, AlignItems items) {
            if (self != AlignSelf.Auto) return self;
            switch (items) {
                case AlignItems.Stretch: return AlignSelf.Stretch;
                case AlignItems.FlexStart: return AlignSelf.FlexStart;
                case AlignItems.FlexEnd: return AlignSelf.FlexEnd;
                case AlignItems.Center: return AlignSelf.Center;
                case AlignItems.Baseline: return AlignSelf.Baseline;
                case AlignItems.Start: return AlignSelf.Start;
                case AlignItems.End: return AlignSelf.End;
            }
            return AlignSelf.Stretch;
        }

        static bool HasDefiniteCrossOnItem(BlockBox box, bool isRow) {
            if (box.Style == null) return false;
            string raw = isRow ? box.Style.Get(CssProperties.HeightId) : box.Style.Get(CssProperties.WidthId);
            if (!string.IsNullOrEmpty(raw) && raw != "auto") return true;
            // CSS Sizing L4 §5: aspect-ratio + definite main-axis dimension
            // gives a definite cross-axis dimension. Without this, flex items
            // like `.slot { width: 37px; aspect-ratio: 1 }` were getting
            // stretched on the cross axis (height) instead of staying square,
            // inflating action-bar slot heights from 37 → ~386 in the
            // Ravenmoor demo and pushing the bottom row off-screen.
            if (StyleResolver.TryResolveAspectRatio(box.Style, out double ratio) && ratio > 0) {
                string mainRaw = isRow ? box.Style.Get(CssProperties.WidthId) : box.Style.Get(CssProperties.HeightId);
                if (!string.IsNullOrEmpty(mainRaw) && mainRaw != "auto") return true;
            }
            return false;
        }

        static void JustifyItemsAlongMain(List<FlexLine> lines, List<Item> items, FlexProperties props, double containerMainSize, bool isRow) {
            foreach (var line in lines) {
                int n = line.ItemIndices.Count;
                if (n == 0) continue;

                double itemsMain = 0;
                int autoMarginCount = 0;
                for (int k = 0; k < n; k++) {
                    var it = items[line.ItemIndices[k]];
                    itemsMain += it.TargetMainSize + it.OuterMainMarginSum;
                    if (it.LeadMainMarginAuto) autoMarginCount++;
                    if (it.TrailMainMarginAuto) autoMarginCount++;
                }
                double freeSpace = containerMainSize - itemsMain - props.MainGap * (n - 1);

                // CSS Flexbox §8.1: any positive free space is distributed
                // equally to each auto margin on the main axis BEFORE
                // justify-content runs. When auto margins are present and
                // absorb the free space, justify-content has no effect on
                // this line (the spec literally says "any distribution of
                // positive free space is to those auto margins").
                double autoShare = 0;
                if (autoMarginCount > 0 && freeSpace > 0) {
                    autoShare = freeSpace / autoMarginCount;
                    freeSpace = 0;
                }
                // Preserve the signed free space for `center` BEFORE clamping.
                // CSS Box Alignment L3 §5.3: the default overflow alignment is
                // UNSAFE — `justify-content:center` keeps the content centred on
                // the container even when it overflows (the content spills past
                // both edges equally; it does NOT snap to the start edge). The
                // blanket `freeSpace = 0` clamp below is correct for the
                // space-* distributions (which spec-fall-back to packing) but
                // wrong for center: it pinned overflowing content to the
                // content-start, so e.g. a fixed-height flex-column card whose
                // padding animates saw its centred label/value DRIFT toward the
                // padded edge instead of staying put. Chrome keeps it centred.
                double centerFree = freeSpace;
                if (freeSpace < 0) freeSpace = 0;

                double offset = 0;
                double extraBetween = 0;
                if (autoMarginCount == 0) {
                    switch (props.JustifyContent) {
                        case JustifyContent.FlexStart:
                        case JustifyContent.Start:
                            break;
                        case JustifyContent.FlexEnd:
                        case JustifyContent.End:
                            offset = freeSpace; break;
                        case JustifyContent.Center:
                            offset = centerFree * 0.5; break;
                        case JustifyContent.SpaceBetween:
                            extraBetween = n > 1 ? freeSpace / (n - 1) : 0;
                            break;
                        case JustifyContent.SpaceAround:
                            extraBetween = n > 0 ? freeSpace / n : 0;
                            offset = extraBetween * 0.5;
                            break;
                        case JustifyContent.SpaceEvenly:
                            extraBetween = freeSpace / (n + 1);
                            offset = extraBetween;
                            break;
                    }
                }

                double cursor = offset;
                for (int k = 0; k < n; k++) {
                    var it = items[line.ItemIndices[k]];
                    double leadAuto = it.LeadMainMarginAuto ? autoShare : 0;
                    double trailAuto = it.TrailMainMarginAuto ? autoShare : 0;
                    it.MainPos = cursor + it.LeadMainMargin + leadAuto;
                    cursor += it.TargetMainSize + it.OuterMainMarginSum + leadAuto + trailAuto;
                    if (k < n - 1) cursor += props.MainGap + extraBetween;
                }
            }
        }

        static void ApplyDirectionReversal(List<FlexLine> lines, List<Item> items, FlexProperties props, double containerMainSize, bool isRow) {
            if (!props.IsReverse) return;
            foreach (var line in lines) {
                for (int k = 0; k < line.ItemIndices.Count; k++) {
                    var it = items[line.ItemIndices[k]];
                    double leadMargin = it.LeadMainMargin;
                    double outer = it.TargetMainSize + it.OuterMainMarginSum;
                    double startEdge = it.MainPos - leadMargin;
                    double newStart = containerMainSize - (startEdge + outer);
                    it.MainPos = newStart + leadMargin;
                }
            }
        }

        static void ApplyWrapReversal(List<FlexLine> lines, FlexProperties props, double containerCrossSize) {
            if (!props.IsWrapReverse) return;
            foreach (var line in lines) {
                line.CrossOffset = containerCrossSize - (line.CrossOffset + line.CrossSize);
            }
        }

        static void WriteBackPositions(List<FlexLine> lines, List<Item> items, FlexBox container, bool isRow) {
            double padLeft = container.PaddingLeft + container.BorderLeft;
            double padTop = container.PaddingTop + container.BorderTop;
            foreach (var line in lines) {
                for (int k = 0; k < line.ItemIndices.Count; k++) {
                    var it = items[line.ItemIndices[k]];
                    double mainPos = it.MainPos;
                    double crossPos = it.CrossPos + line.CrossOffset;
                    if (isRow) {
                        it.Box.X = padLeft + mainPos;
                        it.Box.Y = padTop + crossPos;
                    } else {
                        it.Box.X = padLeft + crossPos;
                        it.Box.Y = padTop + mainPos;
                    }
                    // PAINT-1 / HEROPICK-1 diagnostic — fires only when
                    // UILayoutDiagnostics.Enabled and the child element class matches.
                    if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(it.Box.Element)) {
                        Weva.Diagnostics.UILayoutDiagnostics.TraceFor(it.Box.Element, "FlexLayout.Place",
                            $"container[{(container.Element?.ClassName ?? container.Element?.TagName ?? "?")}] " +
                            $"isRow={isRow} padTop={padTop} padLeft={padLeft} " +
                            $"container.W={container.Width} container.CW={container.ContentWidth} " +
                            $"container.H={container.Height} container.CH={container.ContentHeight} " +
                            $"mainPos={mainPos} crossPos(it+lineOff)={crossPos} " +
                            $"(item.CrossPos={it.CrossPos} line.CrossOffset={line.CrossOffset}) " +
                            $"line.CrossSize={line.CrossSize} " +
                            $"→ Box.X={it.Box.X} Y={it.Box.Y} W={it.Box.Width} H={it.Box.Height}");
                    }
                }
            }
        }

        // After flex items are laid out and positioned, the container's main-
        // axis size should reflect their cumulative outer extent (sum of
        // hypothetical sizes + gaps for single-line, or wrapped tally for
        // multi-line) — UNLESS the author set a definite main-axis size or
        // the box is pinned by PositioningPass. Without this, BlockLayout's
        // pre-flex content-derived main-axis size sticks: e.g. column flex
        // `actionbar-wrap` gets stuck at 410.5 (10 slots stacked vertically
        // by BlockLayout) when its actual flex layout sums to 72.
        void FinalizeContainerMainSize(FlexBox container, FlexProperties props, bool isRow, List<FlexLine> lines, List<Item> items) {
            if (container.Style == null) return;
            // CSS Flexbox §9.5 distinguishes block-level flex (default `display:
            // flex`) from inline-level flex (`display: inline-flex`):
            //   - Block-level flex: main-axis = parent allocation. For row
            //     flex this is width = parent ContentWidth; collapsing would
            //     override the grid-cell-sized hud-bot pattern. Skip.
            //   - Inline-level flex: main-axis is SHRINK-TO-FIT. For row
            //     inline-flex, width must collapse to the sum of items + gaps.
            //     This is what makes `<button>OK</button>` size to its text
            //     content (~28px) instead of inheriting the parent block's
            //     content width (~800px). Fixed in #254.
            // The column-flex case continues below unchanged.
            if (isRow && !container.IsInlineBlock) return;
            // For row inline-flex we collapse main = WIDTH; for column flex
            // we collapse main = HEIGHT. The size property to read for the
            // explicit-author check below depends on which axis we're on.
            int sizePropId = isRow ? CssProperties.WidthId : CssProperties.HeightId;
            // CSS Containment L2 §3.3: when inline-size containment is active
            // on a row inline-flex container, the element's inline-size is
            // externally constrained to the contain-intrinsic-width placeholder
            // (or zero) by InlineLayout.MakeAtomItem.  Do NOT collapse the
            // Width to the item-sum — that would override the containment-
            // imposed width with the true flex content, defeating containment.
            // This guard fires both during the normal flex passes (which re-run
            // after MakeAtomItem has stamped the contained width) and during the
            // per-RelayoutContentAt call inside MakeAtomItem itself.
            if (isRow && container.IsInlineBlock
                && Weva.Layout.Containment.ContainmentResolver.HasInlineSize(container.Style)) {
                return;
            }
            // Skip when the author set an explicit main-axis size.
            string sizeRaw = container.Style.Get(sizePropId);
            var r = StyleResolver.ResolveLength(sizeRaw, container.Style, ctx, StyleResolver.FontSizePx(container.Style, container.Parent?.Style, ctx), null);
            if (r.Kind == StyleResolver.LengthKind.Length) return;
            // CSS Sizing §5.1: a PERCENTAGE main size whose basis is definite is
            // just as definite as a length. BlockLayout already resolved it
            // (ApplyBoxModel's early percent path + FinalizeBlockSize both use
            // the same parent.Height>0 definiteness proxy) — collapsing here to
            // the item content sum threw that resolution away: `.inventory {
            // height:100% }` (column flex under body{height:100%}) collapsed
            // from the 781px viewport chain to its header's ~120px, and its
            // `flex:1; min-height:0` body then ballooned unconstrained
            // (inventory / advanced-dashboard audit failures). A percent
            // against an indefinite parent still falls through and collapses —
            // §10.5 treats it as auto there.
            if (r.Kind == StyleResolver.LengthKind.Percent
                && container.Parent != null && container.Parent.Height > 0) return;
            // CSS Sizing L4 §5: when the container has aspect-ratio set and
            // its cross axis is explicitly definite, the main axis is fully
            // determined by the ratio. Don't collapse to content sum — that
            // would override the geometric size with whatever descendants
            // happen to need. Canonical case: `.portrait { width:100%;
            // aspect-ratio:1/1; display:flex; align-items:center }`
            // containing a giant glyph. Without this guard the recursive
            // Layout(childFlex) call at the end of the outer column-flex
            // placement collapses portrait.Height back to glyph height +
            // frame (~154 px), losing the 296 px aspect-ratio-derived height
            // that the outer flex already accounted for, and the ShiftFollowing-
            // SiblingsDown pass at fixup time then shifts speaker-meta by
            // ~142 px below its correct row.
            if (StyleResolver.TryResolveAspectRatio(container.Style, out double aspectMainRatio)
                && aspectMainRatio > 0 && container.Width > 0) {
                string crossRaw = container.Style.Get(CssProperties.WidthId);
                bool crossExplicit = !string.IsNullOrEmpty(crossRaw) && crossRaw != "auto";
                bool gridStretchedCross = container is BlockBox bbCrossM
                    && bbCrossM.GridStretchedWidth;
                if (crossExplicit || gridStretchedCross) return;
            }
            // Skip when PositioningPass already pinned both edges — preserves
            // the inset:0 → viewport-sized HUD case.
            bool pinnedByPositioning =
                (container.Position == PositionType.Fixed || container.Position == PositionType.Absolute)
                && container.OffsetTop.HasValue && container.OffsetBottom.HasValue;
            if (pinnedByPositioning) return;
            // Honor a grid-stamped main-axis size — but ONLY in the third
            // flex pass. Mirror of the FinalizeContainerCrossSize guard:
            // GridLayout.ApplyItemAlignment stamped GridStretchedHeight on
            // a column-flex item whose grid cell stretch-aligned its
            // Height to the row-track size (e.g. `.hud-tr` inside `.hud {
            // display:grid }` with the default align-items:stretch). The
            // 2nd grid pass writes that final cell Height; without this
            // guard the 3rd flex pass collapses Height back to the sum of
            // intrinsic item heights and the column-flex container ends
            // shorter than its grid cell. The earlier flex passes MUST
            // still collapse because grid pass 1/2 reads the column-flex
            // container's intrinsic Height to size auto/min-content row
            // tracks; locking the height in pass 1 inflates the row track
            // to BlockLayout's pre-flex stack height and regresses the
            // entire grid layout (observed: 2465 → 4453 when the guard
            // ran in every pass). The cross-axis (Height for row flex,
            // Width for column flex) is the dual axis here — main axis is
            // Height for column flex (!isRow), Width for row flex (isRow).
            if (inThirdPass
                && container is BlockBox bbMain
                && (isRow ? bbMain.GridStretchedWidth : bbMain.GridStretchedHeight)) {
                // RESTORE from GridStretchedCell{Width,Height} rather than
                // just returning early. The previous "return" assumed the
                // box's Width/Height still held the grid-stamped value at
                // entry — true for the canonical third-pass call site
                // (third flex pass runs immediately after grid pass 2, no
                // re-derivation in between). But the post-RepositionAbsolutes
                // flex repair pass also routes here, and by then
                // BlockLayout.LayoutContent has been re-run by
                // PositioningPass.ApplyAbsoluteAgainst's cache-hit path
                // (PositioningPass.cs:404-429) — which re-stacks the abs
                // box's grid descendants as block-flow, inflating
                // container.Height to the content sum BEFORE this finaliser
                // sees it. Simply returning preserves the inflated value.
                // Mirror of the scroll-container guard below (line 2303-2312):
                // re-stamp the cached cell extent so it survives any
                // intermediate re-derivation. Pinned by
                // RightColGridStretchHeightTests.
                if (isRow) container.Width = bbMain.GridStretchedCellWidth;
                else container.Height = bbMain.GridStretchedCellHeight;
                return;
            }
            // CSS Flexbox §4.5 — Automatic Minimum Size: a flex container
            // that's a scroll container on the main axis (overflow auto/
            // scroll/hidden/overlay) keeps its outer-imposed size even when
            // descendants overflow. The grid-stretched path is the
            // canonical case — a flex column container that's a grid item
            // with a `1fr` row gets its Height stamped by the grid pass;
            // without this guard the finaliser re-inflates Height to the
            // content sum and the user-visible scrollable viewport collapses
            // to zero range (the scrollbar appears but won't scroll far
            // enough to reach content past the cell extent). Mirror of the
            // existing third-pass-only grid-stretched guard above, but
            // pass-agnostic for scroll containers because §4.5 explicitly
            // gives them min-size = 0. v1 caveat: when row sizing is
            // `auto` rather than `1fr`, returning early in pass 1 would
            // expose the grid row to whatever Height was previously stamped,
            // not the intrinsic content sum — that's a separate trade-off,
            // and the spec's min-size:0 rule for scroll containers is
            // already the intent in that case. Pinned by
            // HeroPickerScrollReproTests; user-reported against a
            // hero-picker detail panel.
            // CSS Flexbox §4.5 — Automatic Minimum Size: a flex container
            // that's a scroll container on the main axis (overflow auto/
            // scroll/hidden/overlay) keeps its outer-imposed size even when
            // descendants overflow. The grid-stretched path is the
            // canonical case — a flex column container that's a grid item
            // with a `1fr` row gets its Height stamped by the grid pass;
            // without this guard the finaliser re-inflates Height to the
            // content sum and the user-visible scrollable viewport collapses
            // to zero range (the scrollbar appears but won't scroll far
            // enough to reach content past the cell extent).
            //
            // The guard runs in all passes (not just the third one like the
            // dual guard above) because §4.5 explicitly gives scroll
            // containers min-size = 0. Critically we RESTORE Height from
            // GridStretchedCellHeight rather than just returning early —
            // BlockLayout / other re-derivation paths may have re-inflated
            // box.Height between the grid stamp and this finaliser visit,
            // so simply returning would leave the stale inflated value in
            // place. Pinned by HeroPickerScrollReproTests; user-reported
            // against a hero-picker detail panel.
            if (container is BlockBox bbScroll
                && (isRow ? bbScroll.GridStretchedWidth : bbScroll.GridStretchedHeight)
                && HasScrollableOverflowOnMainAxis(container, isRow)) {
                if (isRow) {
                    container.Width = bbScroll.GridStretchedCellWidth;
                } else {
                    container.Height = bbScroll.GridStretchedCellHeight;
                }
                return;
            }
            // Sum item main-axis extents + gaps. For column flex main = height
            // (single line). For row inline-flex main = width.
            double maxMainExtent = 0;
            foreach (var line in lines) {
                double lineMain = 0;
                int n = line.ItemIndices.Count;
                if (n == 0) continue;
                for (int k = 0; k < n; k++) {
                    var it = items[line.ItemIndices[k]];
                    // Use the HYPOTHETICAL (pre-grow) main size, not the grown
                    // Box size. A container floored by min-height grows its
                    // children to fill the floor; summing the GROWN children to
                    // re-derive the container is circular and balloons it (a
                    // `min-height:100vh` column went 917→2601). The content-based
                    // main size is the sum of flex BASE sizes (CSS Flexbox §9.9
                    // max-content contribution). Grown == base for non-growing
                    // items, so other containers are unaffected; shrink cases
                    // never reach here (a definite main size returns early above).
                    double itemMainSize = it.HypotheticalMainSize;
                    double itemOuter = itemMainSize + it.OuterMainMarginSum;
                    lineMain += itemOuter;
                }
                lineMain += props.MainGap * (n - 1);
                if (lineMain > maxMainExtent) maxMainExtent = lineMain;
            }
            double frame = isRow
                ? container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight
                : container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;
            double contentResolvedMain = maxMainExtent + frame;
            // A column-flex item whose height was grown by its row-flex parent's
            // align-items:stretch keeps that height — its content was already
            // distributed into it (margin:auto / flex-grow / justify-content).
            // Re-deriving from the content extent here would collapse it back
            // (margin:auto contributes 0 to maxMainExtent), undoing the stretch.
            // Distinct from the grid third-pass guard above so no grid regression.
            if (!isRow && container is BlockBox bbFx && bbFx.FlexCrossStretchedMain) {
                container.Height = bbFx.FlexCrossStretchedMainSize;
                return;
            }
            bool preserve = ShouldPreserveFlexParentMainSize(container, isRow, contentResolvedMain);
            if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(container.Element)) {
                Weva.Diagnostics.UILayoutDiagnostics.TraceFor(container.Element, "FlexLayout.FinalizeContainerMainSize",
                    $"isRow={isRow} maxMainExtent={maxMainExtent} mainGap={props.MainGap} " +
                    $"frame={frame} contentResolvedMain={contentResolvedMain} " +
                    $"preserve={preserve} pre.W={container.Width} pre.H={container.Height}");
            }
            if (preserve) return;

            if (isRow) {
                container.Width = ClampContainerMainByMinMax(container, isRow, maxMainExtent + frame);
            } else {
                container.Height = ClampContainerMainByMinMax(container, isRow, maxMainExtent + frame);
            }
            if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(container.Element)) {
                Weva.Diagnostics.UILayoutDiagnostics.TraceFor(container.Element, "FlexLayout.FinalizeContainerMainSize.post",
                    $"post.W={container.Width} post.H={container.Height}");
            }
        }

        bool ShouldPreserveFlexParentMainSize(FlexBox container, bool isRow, double contentResolvedMain) {
            if (!(container.Parent is FlexBox parentFlex) || parentFlex.Style == null || container.Style == null) return false;

            bool parentIsRow = ContainerIsRow(parentFlex);
            if (parentIsRow != isRow) return false;
            if (!ParentHasDefiniteMain(parentFlex, parentIsRow)) return false;

            double currentMain = isRow ? container.Width : container.Height;

            // CSS Flexbox §4.5 — Automatic Minimum Size of Flex Items: when
            // the item is a scroll container on the main axis (overflow:
            // auto / scroll / hidden / overlay), the parent's assignment
            // wins even when content exceeds it. The scrollbar — or the
            // hidden clip — is the spec-mandated outcome. Without this
            // branch, a `.scroll { flex:1; overflow-y:auto }` child whose
            // content sums to 800 px inside a 300 px parent re-inflates
            // its Height to 800 here, defeating the scroll. Repro pinned
            // by FlexOverflowScrollMinSizeTests; user-reported against
            // a hero picker / list / tree layout.
            if (HasScrollableOverflowOnMainAxis(container, isRow)
                && currentMain > 0
                && currentMain < contentResolvedMain - LayoutEpsilons.HalfPixelEqual) {
                return true;
            }

            if (currentMain <= contentResolvedMain + LayoutEpsilons.HalfPixelEqual) return false;

            double fs = StyleResolver.FontSizePx(container.Style, parentFlex.Style, ctx);
            double parentMain = parentIsRow ? parentFlex.ContentWidth : parentFlex.ContentHeight;
            // H5b: thread element line-height for `lh`-typed flex-basis etc.
            double lh = StyleResolver.LineHeightPx(container.Style, fs, ctx, ctx.GetMetrics(container.Style?.Get(CssProperties.FontFamilyId)));
            var lc = ctx.ToLengthContext(fs, parentMain, lh);
            var itemProps = FlexItemProperties.From(container.Style, lc);
            return itemProps.Grow > 0
                || itemProps.Basis.Kind == FlexBasisKind.Length
                || itemProps.Basis.Kind == FlexBasisKind.Percentage;
        }

        double ClampContainerMainByMinMax(FlexBox container, bool isRow, double value) {
            var style = container.Style;
            if (style == null) return value;

            double fs = StyleResolver.FontSizePx(style, container.Parent?.Style, ctx);
            int minId = isRow ? CssProperties.MinWidthId : CssProperties.MinHeightId;
            int maxId = isRow ? CssProperties.MaxWidthId : CssProperties.MaxHeightId;
            var minR = StyleResolver.ResolveLengthFromParsed(style.GetParsed(minId), ctx, fs, null);
            var maxR = StyleResolver.ResolveLengthFromParsed(style.GetParsed(maxId), ctx, fs, null);
            bool borderBox = IsBorderBox(style);
            double frameMain = isRow
                ? container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight
                : container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;

            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frameMain;
                if (value < minPx) value = minPx;
            }
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frameMain;
                if (value > maxPx) value = maxPx;
            }
            if (value < 0) value = 0;
            return value;
        }

        void FinalizeContainerCrossSize(FlexBox container, FlexProperties props, bool isRow, double linesCrossTotal) {
            if (container.Style == null) return;
            double fs = StyleResolver.FontSizePx(container.Style, container.Parent?.Style, ctx);

            string sizeRaw = isRow ? container.Style.Get(CssProperties.HeightId) : container.Style.Get(CssProperties.WidthId);
            var r = StyleResolver.ResolveLength(sizeRaw, container.Style, ctx, fs, null);
            if (r.Kind == StyleResolver.LengthKind.Length) return;
            // CSS Sizing L4 §5: aspect-ratio + definite main → cross is fully
            // determined by the ratio. Don't collapse to max(child cross
            // size) — that would override the geometric size with whatever
            // the descendants happen to need. Canonical cases:
            //   - `.portrait { width:100%; aspect-ratio:1/1; align-items:center }`
            //     in column-flex parent — explicit width.
            //   - `.item-frame { aspect-ratio:1/1; display:flex }` inside a
            //     grid track of fixed width — width came from the grid track
            //     allocation, not the author's style. GridStretchedWidth is
            //     set by GridLayout.ApplyItemAlignment when the cell sized
            //     the box, and that counts as a "definite main" for our
            //     ratio purposes. Without this branch the column-flex item-
            //     frame's height collapses to glyph.height (~50) and the
            //     ApplyAspectRatioFixupVisit at end-of-Layout then grows it
            //     to 80 with delta=30, shifting following content (visible
            //     in vendor.html as item-glyphs rendering 72px below center).
            if (StyleResolver.TryResolveAspectRatio(container.Style, out double containerCrossRatio)
                && containerCrossRatio > 0) {
                string mainRaw = isRow ? container.Style.Get(CssProperties.WidthId) : container.Style.Get(CssProperties.HeightId);
                bool mainExplicit = !string.IsNullOrEmpty(mainRaw) && mainRaw != "auto";
                bool gridStretchedMain = container is BlockBox bbMain2
                    && (isRow ? bbMain2.GridStretchedWidth : bbMain2.GridStretchedHeight);
                if (mainExplicit || gridStretchedMain) return;
            }
            // CSS Flexbox §9.5: when a flex container has a percentage cross-axis
            // size (e.g. `width: 100%` on a column flex), its size is determined
            // by its containing block — for an inner flex container that's the
            // parent flex's TargetMainSize allocation. Collapsing back to
            // linesCrossTotal here would override the parent's allocation: the
            // canonical bug is `.actionbar-wrap { display: flex; flex-direction: column;
            // width: 100% }` inside `.hud-bot { display: flex }`, where hud-bot
            // sets actionbar-wrap.Width = hud-bot.ContentWidth, then this finalize
            // shrinks it to max(child.Width) and the children re-stretch on the
            // next reflow until everything stabilises at a smaller-than-parent size.
            if (r.Kind == StyleResolver.LengthKind.Percent) return;

            // Preserve cross-axis allocation from a stretch-aligning flex parent.
            // Canonical case: `.party { display: flex; flex-direction: column; }`
            // inside `.hud-tl { display: flex; flex-direction: column; }` (column
            // flex item inside column flex). The outer flex's ApplyMainSizesAndReflow
            // recursively calls Layout(.party) BEFORE the outer's AlignItemsInLine
            // can stretch. If we shrink .party.Width to max(child.Width) here, the
            // outer's ComputeLineCrossSize/AlignItemsInLine sees the shrunken value
            // as line.CrossSize and stretch never recovers the parent's allocation
            // (.party stays at e.g. 220 instead of stretching to .hud-tl's 311).
            // Skip the shrink when the parent is a flex container whose cross axis
            // matches our cross axis AND align-self resolves to stretch — the parent
            // owns our cross-axis sizing. Same intent as the percent guard above.
            if (IsStretchedByFlexParent(container, isRow)) return;

            // Mirror of the flex-parent guard above for grid-parent stretch.
            // GridLayout.ApplyItemAlignment stamps GridStretchedWidth /
            // GridStretchedHeight (post-reflow) when the cell's stretch
            // alignment assigned its W/H to the box. The cross axis we're
            // collapsing here is Width for column flex (!isRow) and Height
            // for row flex (isRow) — same axis the flag tracks.
            // Canonical case: `.hud-tr { display:flex; flex-direction:
            // column; align-items:flex-end }` inside `.hud { display:grid }`
            // — without this guard the second flex pass shrinks hud-tr.Width
            // from the 311px cell allocation back to its widest child's 200px.
            if (container is BlockBox bbCross
                && (isRow ? bbCross.GridStretchedHeight : bbCross.GridStretchedWidth)) return;

            // Block-level flex container with width:auto inside a plain block
            // parent (CSS 2.1 §10.3.3) should fill the parent's content width,
            // not shrink to its intrinsic. Canonical case: `.line.party`
            // (display:flex; flex-direction:column) inside `.chat .log`
            // (a plain block container — `flex: 1` is item-level, not
            // container-level). BlockLayout already pre-set Width to parent
            // content width; collapsing here loses that allocation, and the
            // following passes then shrink children to fit (the now-tiny)
            // container — visible as `.col { display:flex; flex-direction:
            // column; padding-left:16px } > .cell { width:240px }` collapsing
            // .col to 240 instead of body's 800. Grid/flex parents own their
            // child's cross-axis sizing via their own algorithms, so we still
            // collapse in those cases; for plain block parents (incl. table /
            // inline-block — they all assign a definite width during
            // BlockLayout) preserve the allocation in every pass. The earlier
            // gate that restricted this to the third pass was over-conservative
            // — grid track sizing uses MaxContentWidth, not container.Width,
            // so preserving the BlockLayout width here doesn't pollute
            // intrinsic sizing for an ancestor grid.
            //
            // Skip inline-flex (IsInlineBlock) — those are shrink-to-fit per
            // CSS Flexbox §9.5 and SHOULD collapse to their intrinsic.
            if (!isRow
                && container.Parent != null
                && !(container.Parent is FlexBox)
                && !(container.Parent is Weva.Layout.Grid.GridBox)
                && !(container.Parent is Weva.Layout.Tables.TableBox)
                && !container.IsInlineBlock
                && container.Width > linesCrossTotal + LayoutEpsilons.HalfPixelEqual) {
                return;
            }

            // Spec default is content-box; route through IsBorderBox so a
            // null/unset slot reads correctly (the prior !="content-box"
            // test inverted the default).
            bool borderBox = IsBorderBox(container.Style);
            if (isRow) {
                double frame = container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom;
                // Floor/ceil the auto cross size by the container's own
                // min/max-height. Without this a row-flex container with
                // `min-height` taller than its content collapsed to content
                // height, so align-items:flex-end / center had no free space
                // to distribute and items pinned to the top (the explicit-
                // `height` path clamps in BlockLayout and worked; the auto
                // path skipped it). CSS Box Sizing L3 §5 + Flexbox §8.3.
                double h = ClampContainerCrossByMinMax(container, fs, true, linesCrossTotal + frame);
                container.Height = h;
                // Stamp ONCE per box lifetime (see Box.PreFlexCrossHeight +
                // BlockLayout.FinalizeBlockSize for the symmetric block-flow
                // case). Captures the content-cross size BEFORE any outer
                // flex/grid pass can stretch container.Height further.
                // PositioningPass.FlexIntrinsicCross prefers this stamp over
                // .Height when computing an outer container's intrinsic
                // cross. Stamp-once gating prevents capturing post-stretch
                // values in re-layout calls. Audit #19b / #280.
                if (container.PreFlexCrossHeight == 0) {
                    container.PreFlexCrossHeight = h;
                }
            } else {
                double frame = container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight;
                container.Width = ClampContainerCrossByMinMax(container, fs, false,
                    linesCrossTotal + (borderBox ? 0 : frame));
            }
        }

        // The container's min cross size (min-height for a row container,
        // min-width for a column) in CONTENT-box units, to compare against
        // containerCrossSize (= ContentHeight / ContentWidth). 0 when unset.
        double MinCrossFloorContent(FlexBox container, double fs, bool isRow) {
            var style = container.Style;
            if (style == null) return 0;
            int minId = isRow ? CssProperties.MinHeightId : CssProperties.MinWidthId;
            var minParsed = style.GetParsed(minId);
            double cbCross = container.Parent != null
                ? (isRow ? container.Parent.ContentHeight : container.Parent.ContentWidth)
                : 0;
            var minR = StyleResolver.ResolveLengthFromParsed(minParsed, ctx, fs, cbCross);
            double px;
            if (minR.Kind == StyleResolver.LengthKind.Length) px = minR.Pixels;
            else if (minR.Kind == StyleResolver.LengthKind.Percent) px = cbCross * (minR.Percent * 0.01);
            else return 0;
            // min-* applies to the box per box-sizing; convert to content-box.
            if (IsBorderBox(style)) {
                px -= isRow
                    ? (container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom)
                    : (container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight);
            }
            return px > 0 ? px : 0;
        }

        // Container-level analog of ClampCrossSizeByMinMax: floor/ceil a flex
        // container's auto cross size by its OWN min/max on the cross axis
        // (min/max-height for a row container, min/max-width for a column).
        // Percentages resolve against the containing block's cross size.
        double ClampContainerCrossByMinMax(FlexBox container, double fs, bool isRow, double value) {
            var style = container.Style;
            if (style == null) return value;
            int minId = isRow ? CssProperties.MinHeightId : CssProperties.MinWidthId;
            int maxId = isRow ? CssProperties.MaxHeightId : CssProperties.MaxWidthId;
            var minParsed = style.GetParsed(minId);
            var maxParsed = style.GetParsed(maxId);
            double cbCross = container.Parent != null
                ? (isRow ? container.Parent.ContentHeight : container.Parent.ContentWidth)
                : 0;
            var minR = StyleResolver.ResolveLengthFromParsed(minParsed, ctx, fs, cbCross);
            var maxR = StyleResolver.ResolveLengthFromParsed(maxParsed, ctx, fs, cbCross);
            bool borderBox = IsBorderBox(style);
            double frameCross = isRow
                ? (container.PaddingTop + container.PaddingBottom + container.BorderTop + container.BorderBottom)
                : (container.PaddingLeft + container.PaddingRight + container.BorderLeft + container.BorderRight);
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frameCross;
                if (value < minPx) value = minPx;
            } else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = cbCross * (minR.Percent * 0.01);
                if (!borderBox) mp += frameCross;
                if (value < mp) value = mp;
            }
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frameCross;
                if (value > maxPx) value = maxPx;
            } else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = cbCross * (maxR.Percent * 0.01);
                if (!borderBox) mp += frameCross;
                if (value > mp) value = mp;
            }
            if (value < 0) value = 0;
            return value;
        }

        // True when `container` is a flex item whose cross axis (from its own
        // POV) coincides with the parent flex container's cross axis AND the
        // parent's resolved align-items / item's align-self is `stretch`. In
        // that case the parent — not this container — determines our cross
        // size, and FinalizeContainerCrossSize must not collapse it.
        bool IsStretchedByFlexParent(FlexBox container, bool isRow) {
            if (!(container.Parent is FlexBox parentFlex) || parentFlex.Style == null) return false;
            bool parentIsRow = ContainerIsRow(parentFlex);
            if (parentIsRow == isRow) {
                // Same-direction nesting — our cross axis matches parent's
                // cross axis. The parent owns our cross size via
                // align-items / align-self stretch.
                string asRaw = container.Style?.Get("align-self");
                string aiRaw = parentFlex.Style.Get("align-items");
                if (!IsStretchKeyword(asRaw)) return false;
                if (string.IsNullOrEmpty(asRaw) || asRaw == "auto") {
                    if (!IsStretchKeyword(aiRaw)) return false;
                }
                // The parent only OWNS our cross size when its cross axis is
                // itself definite — otherwise the parent's cross is derived
                // from the max of its items' intrinsic cross sizes, which
                // means we MUST compute our own intrinsic cross first (and
                // the parent will re-stretch us on the next pass once its
                // cross is settled). Without this guard, a chain of
                // same-direction same-stretch flex containers each defer to
                // the next outermost, and BlockLayout's pre-flex stacked
                // height (sum of children) sticks. Canonical case:
                // `.vitals { display:flex }` inside `.topbar { display:flex;
                // align-items:center }` contains `.bar { display:flex;
                // align-items:center }` containing label/track/value rows;
                // .bar pre-flex height = 47 (= label+track+value), vitals
                // and bar both defer up the chain, the topbar centers vitals
                // at 47 instead of the correct 16 (max child).
                if (!ParentHasDefiniteCross(parentFlex, isRow)) return false;
                return true;
            }
            // Orthogonal nesting — parent's MAIN axis is our cross axis. The
            // parent's flex-grow allocation lives on its main axis, so
            // growing us pushes our cross size (Width for column-in-row,
            // Height for row-in-column). FinalizeContainerCrossSize must
            // not then collapse our cross size back to lines content —
            // doing so undoes the parent's grow. Canonical case:
            //   .primary-row { display:flex; }       /* row */
            //   .primary { flex:1; display:flex;     /* column */
            //              flex-direction:column; }
            // The parent's flex:1 → parent grows .primary's Width to its
            // share of primary-row's main-axis content area. .primary's
            // own Layout (isRow=false) reaches FinalizeContainerCrossSize
            // and would collapse Width back to max(child Width) + frame
            // (~70px) instead of the grown ~159px.
            //
            if (container.Style == null) return false;
            LengthContext lc;
            if (ctx != null) {
                double fs2 = StyleResolver.FontSizePx(container.Style, parentFlex.Style, ctx);
                // H5b: include element line-height for `lh`-typed flex-basis.
                double lh2 = StyleResolver.LineHeightPx(container.Style, fs2, ctx, ctx.GetMetrics(container.Style?.Get(CssProperties.FontFamilyId)));
                lc = ctx.ToLengthContext(fs2, 0, lh2);
            } else {
                lc = default;
            }
            var itemProps = FlexItemProperties.From(container.Style, lc);
            // We grow into the parent's free space along its main axis (= our
            // cross), so the parent owns our cross size.
            if (itemProps.Grow > 0) return true;
            // The same ownership applies even without flex-grow when the parent
            // has a definite main axis AND we declare a size / min / max / basis
            // on that axis (= our cross) for the parent to honour. Canonical
            // case: a row-flex HUD containing `.level-chip { display:flex;
            // flex-direction:column; min-width:112px }` — the parent clamps the
            // item to its 138px border-box min-width and the child column flex
            // must not collapse its cross width to the 55px label text.
            //
            // But a flex-grow:0 item with an AUTO cross size is sized by its own
            // content (its flex line), NOT by the parent's main extent — so we
            // must finalize it here. Without this, BlockLayout's pre-flex
            // VERTICAL-stacked height (wrong for a row flex) sticks. Canonical
            // bug: `.inv-header { display:flex }` (row) inside `.inventory
            // { display:flex; flex-direction:column; height:100% }` rendered
            // 252px tall (stacked child sum) instead of ~54 (max child + frame).
            if (ParentHasDefiniteMain(parentFlex, parentIsRow)
                && ItemHasCrossAxisConstraint(container, isRow)) return true;
            return false;
        }

        // True when the flex item declares an explicit size, min, max, or a
        // non-auto flex-basis on its CROSS axis (height for a row flex, width
        // for a column flex) — i.e. something an orthogonal flex parent can
        // honour when it owns the item's main extent. Used to decide whether
        // FinalizeContainerCrossSize should defer to the parent's allocation
        // or compute the item's own content-derived cross size.
        bool ItemHasCrossAxisConstraint(FlexBox c, bool isRow) {
            var s = c.Style;
            if (s == null) return false;
            string size = isRow ? s.Get(CssProperties.HeightId)    : s.Get(CssProperties.WidthId);
            string min  = isRow ? s.Get(CssProperties.MinHeightId) : s.Get(CssProperties.MinWidthId);
            string max  = isRow ? s.Get(CssProperties.MaxHeightId) : s.Get(CssProperties.MaxWidthId);
            string basis = s.Get(CssProperties.FlexBasisId);
            return IsDefiniteSizeToken(size) || IsDefiniteSizeToken(min)
                || IsDefiniteSizeToken(max) || IsDefiniteSizeToken(basis);
        }

        static bool IsDefiniteSizeToken(string v) {
            return !string.IsNullOrEmpty(v)
                && v != "auto" && v != "none" && v != "content"
                && v != "0" && v != "0px";
        }

        bool ParentHasDefiniteMain(FlexBox parent, bool parentIsRow) {
            if (parent == null || parent.Style == null) return false;
            string mainRaw = parentIsRow ? parent.Style.Get(CssProperties.WidthId) : parent.Style.Get(CssProperties.HeightId);
            if (!string.IsNullOrEmpty(mainRaw) && mainRaw != "auto") return true;
            if (parent is BlockBox bb && (parentIsRow ? bb.GridStretchedWidth : bb.GridStretchedHeight)) return true;
            if ((parent.Position == PositionType.Absolute || parent.Position == PositionType.Fixed)
                && (parentIsRow
                    ? (parent.OffsetLeft.HasValue && parent.OffsetRight.HasValue)
                    : (parent.OffsetTop.HasValue && parent.OffsetBottom.HasValue))) {
                return true;
            }
            if (parentIsRow && parent.Width > 0) return true;
            // A positive Height on an auto-height column flex is usually just
            // BlockLayout's pre-flex child stack, not a definite main-axis
            // allocation. Treating it as definite makes nested row-flex items
            // preserve their inflated block-stacked cross size.
            return false;
        }

        static bool IsStretchKeyword(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "auto" || raw == "normal" || raw == "stretch";
        }

        // Heuristic: the parent flex's cross axis is definite when the parent
        // has an explicit length/percent on that axis, a grid stretch flag,
        // a positioning pin spanning both edges of that axis, or its parent
        // chain leads to such a constraint via stretch. For our purposes,
        // a same-direction stretch chain is only safe when SOMEONE in the
        // chain has a definite cross — otherwise everyone derives from
        // intrinsic content and the lowest flex container must compute its
        // own line cross from children rather than defer.
        bool ParentHasDefiniteCross(FlexBox parent, bool isRow) {
            if (parent.Style == null) return false;
            string crossRaw = isRow ? parent.Style.Get(CssProperties.HeightId) : parent.Style.Get(CssProperties.WidthId);
            if (!string.IsNullOrEmpty(crossRaw) && crossRaw != "auto") return true;
            if (parent is BlockBox bb && (isRow ? bb.GridStretchedHeight : bb.GridStretchedWidth)) return true;
            if ((parent.Position == PositionType.Absolute || parent.Position == PositionType.Fixed)
                && (isRow
                    ? (parent.OffsetTop.HasValue && parent.OffsetBottom.HasValue)
                    : (parent.OffsetLeft.HasValue && parent.OffsetRight.HasValue))) {
                return true;
            }
            // aspect-ratio derives a definite cross when the other axis is itself
            // definite — fall through to the same logic HasDefiniteCross uses.
            if (StyleResolver.TryResolveAspectRatio(parent.Style, out double ratio) && ratio > 0) {
                string otherRaw = isRow ? parent.Style.Get(CssProperties.WidthId) : parent.Style.Get(CssProperties.HeightId);
                if (!string.IsNullOrEmpty(otherRaw) && otherRaw != "auto") return true;
            }
            // Recurse: if our parent is stretched same-axis by ITS parent and
            // that grandparent has a definite cross, the chain is grounded.
            if (parent.Parent is FlexBox grand && grand.Style != null) {
                bool grandIsRow = ContainerIsRow(grand);
                if (grandIsRow == isRow) {
                    string asRaw = parent.Style.Get("align-self");
                    string aiRaw = grand.Style.Get("align-items");
                    bool selfStretch = IsStretchKeyword(asRaw);
                    bool parentDefault = string.IsNullOrEmpty(asRaw) || asRaw == "auto";
                    if (selfStretch && (!parentDefault || IsStretchKeyword(aiRaw))) {
                        return ParentHasDefiniteCross(grand, isRow);
                    }
                }
            }
            return false;
        }
    }
}
