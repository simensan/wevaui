namespace Weva.Layout.Boxes {
    public class BlockBox : Box {
        public bool ContainsInlines { get; internal set; }

        // CSS 2.1 §9.5 floats. Stamped by BlockLayout (which reads
        // `float` / `clear` from the cascade) before flowing children.
        // None means the box participates in normal block flow. Left/Right
        // make BlockLayout pull the box out of the in-flow stream and
        // place it at the leading/trailing edge of its containing block,
        // narrowing the area available to subsequent in-flow content.
        // Floats establish a new block-formatting context (CSS 2.1 §9.4.1)
        // — their descendants are isolated from the surrounding flow.
        public Weva.Layout.Floats.FloatType Float { get; internal set; }
        public Weva.Layout.Floats.ClearType Clear { get; internal set; }

        // True when this is a floated box. Convenience predicate used by
        // BlockLayout / PositioningPass to skip floats in places where
        // they shouldn't participate (margin collapsing, in-flow Y
        // advancement, OOF compression). Mirrors the Float field; kept as
        // a separate getter so call sites read "is this a float" without
        // pattern-matching on the enum.
        public bool IsFloat => Float != Weva.Layout.Floats.FloatType.None;

        // True for boxes whose outer behaviour is inline (display: inline-block /
        // inline-flex / inline-grid). The inner formatting context is still block /
        // flex / grid; only the participation in the parent IFC differs. Set by
        // BoxBuilder. Margin collapsing skips inline-block boxes (their margins do
        // not collapse with siblings) per CSS Box Model §8.3.1.
        public bool IsInlineBlock { get; internal set; }

        // Per-pass cache for PositioningPass shrink-to-fit (CSS Positioned
        // Layout L3 §10.3.7). Stores the containing-block width the last
        // shrink-to-fit pass ran against so we skip the (expensive) max/min
        // content probes when neither the box nor its CB has changed.
        // -1 means "never computed" (sentinel; a cb width of 0 is a valid
        // probe input). Reset in ResetForPool so pooled boxes don't reuse
        // stale cache entries.
        public double ShrinkFitCachedAvail { get; internal set; } = -1;
        public double ShrinkFitCachedWidth { get; internal set; } = -1;

        // L2: intrinsic min/max-content of this box's shrink-to-fit content,
        // cached for the duration of ONE layout (stamped with
        // PositioningPass.LayoutGeneration). max/min-content are content-
        // derived and `avail`-INDEPENDENT, so when the pass tower re-probes
        // this box at a different available width (the ShrinkFitCached avail-
        // keyed fast path misses), we recompute only `fitted` and skip the two
        // destructive RelayoutContentAt probes. Content is stable within a
        // layout; a content change rebuilds the box (ResetForPool) and the
        // per-layout generation stamp guards cross-layout reuse.
        public double ShrinkFitCachedMaxContent { get; internal set; } = -1;
        public double ShrinkFitCachedMinContent { get; internal set; } = -1;
        public long ShrinkFitIntrinsicGeneration { get; internal set; } = -1;

        // Set by GridLayout.ApplyItemAlignment when the cell's stretch
        // alignment assigns the cell's W/H to the box (i.e. the
        // axis-relevant size was auto and justify-self/align-self
        // resolved to stretch). Consumed by FlexLayout.FinalizeContainer-
        // CrossSize to skip the "collapse to children's cross-extent"
        // shrink — without these flags, a flex container that's a grid
        // item (e.g. `.hud-tr { display:flex; flex-direction:column;
        // align-items:flex-end }` inside `.hud { display:grid }`) loses
        // the grid-track cross-size and shrinks to its widest child.
        // Same intent as IsStretchedByFlexParent but for grid parents.
        // Stamped AFTER GridLayout's optional RelayoutContentAt + Reflow-
        // FlexDescendants block — those need to see flag=false so the
        // post-reflow content extents resolve naturally first.
        public bool GridStretchedWidth { get; internal set; }
        public bool GridStretchedHeight { get; internal set; }

        // Cell main-size captured at the moment GridStretched* is stamped.
        // Required because BlockLayout / FlexLayout's intrinsic-content
        // re-derivation can over-write box.Width / box.Height on subsequent
        // passes, and downstream consumers (e.g. FlexLayout's
        // FinalizeContainerMainSize on scroll-container grid items) need a
        // stable reference to the grid-allotted cell size to restore the
        // assigned dimension after the re-inflation. Zero when the
        // corresponding stretched flag is false.
        public double GridStretchedCellWidth { get; internal set; }
        public double GridStretchedCellHeight { get; internal set; }

        // FLEX-CROSS-STRETCH-GROW-REFLOW: set when a row-flex parent's
        // align-items:stretch grows THIS column-flex item's height (its MAIN
        // axis) past its content. Distinct from GridStretchedHeight (a grid
        // concept reset per grid pass) so it can drive HasDefiniteMain and the
        // FinalizeContainerMainSize preserve early-out WITHOUT perturbing
        // grid items or the third-pass-only grid guard. FlexCrossStretchedMainSize
        // holds the stretched main extent so the finaliser can restore it after
        // an intrinsic-content re-derivation. Re-evaluated each parent flex pass
        // (cleared in PlaceItemsCross before the stretch decision).
        public bool FlexCrossStretchedMain { get; internal set; }
        public double FlexCrossStretchedMainSize { get; internal set; }

        // FLEX-GROW-ROW-CROSS-STRETCH: set when a COLUMN-flex parent's main-axis
        // distribution grows THIS row-flex item's height (the row's CROSS axis)
        // past its content. The grown height is then a definite cross size, so
        // the row's single-line cross size lifts to it and align-items:stretch
        // children fill it instead of collapsing to content. Re-evaluated each
        // parent main-axis pass (set only when a row child actually grows).
        public bool FlexParentAssignedCross { get; internal set; }

        // CSS Grid L1 §9 (E7): an absolutely-positioned grid child whose
        // grid-placement properties resolve to a definite area uses THAT
        // grid area as its containing block — not the grid container's
        // padding edge. GridLayout stamps the area's origin (relative to
        // the grid container's border-box) and size onto the abs-pos child
        // BEFORE PositioningPass runs; ContainingBlockResolver.ResolveAbsolute
        // consults the flag and substitutes the grid-area rect when set.
        // Origin is in coordinates LOCAL to the grid container's border-box
        // origin (i.e. matches the convention used by cellX / cellY in
        // GridLayout.Layout). Cleared every grid-layout pass before
        // re-stamping so a restyle that drops the definite placement does
        // not leak stale values.
        public bool HasGridAreaContainingBlock { get; internal set; }
        public double GridAreaContainingBlockOffsetX { get; internal set; }
        public double GridAreaContainingBlockOffsetY { get; internal set; }
        public double GridAreaContainingBlockWidth { get; internal set; }
        public double GridAreaContainingBlockHeight { get; internal set; }

        // CSS Lists 3 §3.3 — when the list-marker box for a `<li>` is built
        // with `list-style-image: url(...)` set, the marker's TextRun is
        // suppressed and the URL is stored here for the paint pass to draw
        // as the marker glyph. Null on every non-marker box and on text
        // markers (disc / decimal / …). Set by BoxBuilder.BuildListMarkerBox
        // and SnapshotBoxBuilder's mirror. The marker's regular `Style` is
        // shared with the host li so font / color flow through any fallback.
        public string ListMarkerImage { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            ContainsInlines = false;
            IsInlineBlock = false;
            ShrinkFitCachedAvail = -1;
            ShrinkFitCachedWidth = -1;
            ShrinkFitCachedMaxContent = -1;
            ShrinkFitCachedMinContent = -1;
            ShrinkFitIntrinsicGeneration = -1;
            GridStretchedWidth = false;
            GridStretchedHeight = false;
            GridStretchedCellWidth = 0;
            GridStretchedCellHeight = 0;
            FlexCrossStretchedMain = false;
            FlexCrossStretchedMainSize = 0;
            FlexParentAssignedCross = false;
            HasGridAreaContainingBlock = false;
            GridAreaContainingBlockOffsetX = 0;
            GridAreaContainingBlockOffsetY = 0;
            GridAreaContainingBlockWidth = 0;
            GridAreaContainingBlockHeight = 0;
            Float = Weva.Layout.Floats.FloatType.None;
            Clear = Weva.Layout.Floats.ClearType.None;
            ListMarkerImage = null;
        }
    }
}
