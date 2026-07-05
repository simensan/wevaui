using System.Collections.Generic;

namespace Weva.Css.Cascade {
    public static class CssProperties {
        // Process-lifetime data structures. Ids are assigned in registration
        // order and are stable for the lifetime of the process — call sites
        // cache them once. nextId == number of registered (non-custom)
        // properties; ComputedStyle reads RegisteredCount at construction to
        // size its per-element values array.
        static readonly object regLock = new object();
        static readonly Dictionary<string, CssProperty> registry = new Dictionary<string, CssProperty>();
        static readonly Dictionary<string, int> idByName = new Dictionary<string, int>();
        // Reverse index: index -> property name. Grown alongside idByName by
        // Register(). Read by GetName() on the Enumerate() path. Capacity
        // doubles when needed; entries never move, so cached ids stay valid.
        static string[] nameById = new string[128];
        static CssProperty[] propertyById = new CssProperty[128];
        static int nextId;

        // Hot-property id constants. Resolved once at type init so layout /
        // paint hot paths can index ComputedStyle.values without paying a
        // string lookup. The static ctor populates the registry first, then
        // captures these ids — re-registration via Register() preserves them.
        static CssProperties() {
            BuildRegistry();
            DisplayId = GetId("display");
            PositionId = GetId("position");
            TopId = GetId("top");
            RightId = GetId("right");
            BottomId = GetId("bottom");
            LeftId = GetId("left");
            ZIndexId = GetId("z-index");
            OverflowId = GetId("overflow");
            OverflowXId = GetId("overflow-x");
            OverflowYId = GetId("overflow-y");
            OverflowClipMarginId = GetId("overflow-clip-margin");
            OverflowClipMarginTopId = GetId("overflow-clip-margin-top");
            OverflowClipMarginRightId = GetId("overflow-clip-margin-right");
            OverflowClipMarginBottomId = GetId("overflow-clip-margin-bottom");
            OverflowClipMarginLeftId = GetId("overflow-clip-margin-left");
            WidthId = GetId("width");
            HeightId = GetId("height");
            MinWidthId = GetId("min-width");
            MinHeightId = GetId("min-height");
            MaxWidthId = GetId("max-width");
            MaxHeightId = GetId("max-height");
            BoxSizingId = GetId("box-sizing");
            PaddingId = GetId("padding");
            PaddingTopId = GetId("padding-top");
            PaddingRightId = GetId("padding-right");
            PaddingBottomId = GetId("padding-bottom");
            PaddingLeftId = GetId("padding-left");
            MarginId = GetId("margin");
            MarginTopId = GetId("margin-top");
            MarginRightId = GetId("margin-right");
            MarginBottomId = GetId("margin-bottom");
            MarginLeftId = GetId("margin-left");
            BorderTopId = GetId("border-top");
            BorderRightId = GetId("border-right");
            BorderBottomId = GetId("border-bottom");
            BorderLeftId = GetId("border-left");
            BorderTopWidthId = GetId("border-top-width");
            BorderRightWidthId = GetId("border-right-width");
            BorderBottomWidthId = GetId("border-bottom-width");
            BorderLeftWidthId = GetId("border-left-width");
            BorderTopStyleId = GetId("border-top-style");
            BorderRightStyleId = GetId("border-right-style");
            BorderBottomStyleId = GetId("border-bottom-style");
            BorderLeftStyleId = GetId("border-left-style");
            FontSizeId = GetId("font-size");
            FontFamilyId = GetId("font-family");
            ColorId = GetId("color");
            LineHeightId = GetId("line-height");
            WhiteSpaceId = GetId("white-space");
            TextAlignId = GetId("text-align");
            TextAlignLastId = GetId("text-align-last");
            TextJustifyId = GetId("text-justify");
            TextIndentId = GetId("text-indent");
            TextWrapId = GetId("text-wrap");
            DirectionId = GetId("direction");
            WritingModeId = GetId("writing-mode");
            UnicodeBidiId = GetId("unicode-bidi");
            TabSizeId = GetId("tab-size");
            HyphensId = GetId("hyphens");
            WordBreakId = GetId("word-break");
            LineBreakId = GetId("line-break");
            OverflowWrapId = GetId("overflow-wrap");
            WordWrapId = GetId("word-wrap");
            FlexDirectionId = GetId("flex-direction");
            FlexFlowId = GetId("flex-flow");
            OpacityId = GetId("opacity");
            TransformId = GetId("transform");
            TextOverflowId = GetId("text-overflow");
            AnchorNameId = GetId("anchor-name");
            PositionAnchorId = GetId("position-anchor");
            AspectRatioId = GetId("aspect-ratio");
            FloatId = GetId("float");
            ClearId = GetId("clear");

            // Property IDs added for the per-style GetParsed migration. Each
            // consumer that wants to read its property via the typed cache
            // needs an int constant to pass to ComputedStyle.GetParsed.
            LetterSpacingId = GetId("letter-spacing");
            FontWeightId = GetId("font-weight");
            FontStyleId = GetId("font-style");
            FontVariantId = GetId("font-variant");
            BackgroundImageId = GetId("background-image");
            BackgroundColorId = GetId("background-color");
            BackgroundPositionId = GetId("background-position");
            BackgroundSizeId = GetId("background-size");
            BackgroundRepeatId = GetId("background-repeat");
            BorderRadiusId = GetId("border-radius");
            BorderTopLeftRadiusId = GetId("border-top-left-radius");
            BorderTopRightRadiusId = GetId("border-top-right-radius");
            BorderBottomRightRadiusId = GetId("border-bottom-right-radius");
            BorderBottomLeftRadiusId = GetId("border-bottom-left-radius");
            BorderTopColorId = GetId("border-top-color");
            BorderRightColorId = GetId("border-right-color");
            BorderBottomColorId = GetId("border-bottom-color");
            BorderLeftColorId = GetId("border-left-color");
            BoxShadowId = GetId("box-shadow");
            TextShadowId = GetId("text-shadow");
            FilterId = GetId("filter");
            BackdropFilterId = GetId("backdrop-filter");
            TextDecorationColorId = GetId("text-decoration-color");
            TextDecorationStyleId = GetId("text-decoration-style");
            TextDecorationThicknessId = GetId("text-decoration-thickness");
            TextUnderlineOffsetId = GetId("text-underline-offset");
            // CSS Text Decoration L4 §10 — `text-stroke` / `-webkit-text-stroke`.
            // Both longhand pairs and the WebKit-prefixed shorthand resolve to
            // these two IDs. v1 paints the stroke as a phantom DrawTextCommand
            // emitted BEFORE the glyph fill so the fill sits on top.
            WebkitTextStrokeWidthId = GetId("-webkit-text-stroke-width");
            WebkitTextStrokeColorId = GetId("-webkit-text-stroke-color");
            // CSS Fonts 4 §6.10 — `font-variation-settings` and `font-optical-sizing`.
            // The settings value is a comma-list of (<string> <number>) pairs
            // like `"wght" 350, "opsz" 14`; the resolver materialises them
            // into a FontAxisList (see TextRunResolver). font-optical-sizing
            // toggles whether the engine drives the `opsz` axis automatically
            // from the resolved font-size.
            FontVariationSettingsId = GetId("font-variation-settings");
            FontOpticalSizingId = GetId("font-optical-sizing");
            // CSS Fonts 4 §6.4 — `font-feature-settings` selects OpenType
            // feature tags (`"liga" 1`, `"smcp" on`, etc.). Registered for
            // round-trip and inherit semantics — the resolver does not yet
            // wire these into TMP feature tables; fix tracked as #251.
            FontFeatureSettingsId = GetId("font-feature-settings");
            // CSS Color Adjustment 1 §3.1 — `color-scheme` declares which
            // schemes the element is willing to render in (light / dark /
            // light dark / only dark). Inherited; default `normal`. The
            // LightDarkResolver still reads MediaContext.ColorScheme as
            // the active scheme (fix tracked as #257). Registered here so
            // round-trip + inheritance work and the unknown-property
            // diagnostic stops firing.
            ColorSchemeId = GetId("color-scheme");
            VisibilityId = GetId("visibility");

            // Second wave of property IDs for the layout / text / animation /
            // outline / filter / border-image / background-layout / grid /
            // container-query / scroll consumer migrations. These get the
            // GetParsed cache fast-path the same way the paint resolvers do —
            // a per-property int constant indexes ComputedStyle.values directly.
            InsetId = GetId("inset");
            FlexId = GetId("flex");
            FlexWrapId = GetId("flex-wrap");
            FlexBasisId = GetId("flex-basis");
            FlexGrowId = GetId("flex-grow");
            FlexShrinkId = GetId("flex-shrink");
            JustifyContentId = GetId("justify-content");
            AlignItemsId = GetId("align-items");
            AlignSelfId = GetId("align-self");
            AlignContentId = GetId("align-content");
            GapId = GetId("gap");
            RowGapId = GetId("row-gap");
            ColumnGapId = GetId("column-gap");
            OrderId = GetId("order");
            GridTemplateColumnsId = GetId("grid-template-columns");
            GridTemplateRowsId = GetId("grid-template-rows");
            GridTemplateAreasId = GetId("grid-template-areas");
            GridTemplateId = GetId("grid-template");
            GridColumnId = GetId("grid-column");
            GridRowId = GetId("grid-row");
            GridColumnStartId = GetId("grid-column-start");
            GridColumnEndId = GetId("grid-column-end");
            GridRowStartId = GetId("grid-row-start");
            GridRowEndId = GetId("grid-row-end");
            GridAutoFlowId = GetId("grid-auto-flow");
            GridAutoColumnsId = GetId("grid-auto-columns");
            GridAutoRowsId = GetId("grid-auto-rows");
            GridAreaId = GetId("grid-area");
            PlaceItemsId = GetId("place-items");
            PlaceContentId = GetId("place-content");
            PlaceSelfId = GetId("place-self");
            JustifyItemsId = GetId("justify-items");
            JustifySelfId = GetId("justify-self");
            WordSpacingId = GetId("word-spacing");
            FontId = GetId("font");
            TextTransformId = GetId("text-transform");
            TextDecorationId = GetId("text-decoration");
            TextDecorationLineId = GetId("text-decoration-line");
            ImageRenderingId = GetId("image-rendering");
            CaretColorId = GetId("caret-color");
            AccentColorId = GetId("accent-color");
            ObjectFitId = GetId("object-fit");
            ObjectPositionId = GetId("object-position");
            BackgroundClipId = GetId("background-clip");
            BackgroundOriginId = GetId("background-origin");
            BackgroundAttachmentId = GetId("background-attachment");
            BackgroundId = GetId("background");
            BorderId = GetId("border");
            BorderWidthId = GetId("border-width");
            BorderStyleId = GetId("border-style");
            BorderColorId = GetId("border-color");
            BorderImageSourceId = GetId("border-image-source");
            BorderImageSliceId = GetId("border-image-slice");
            BorderImageWidthId = GetId("border-image-width");
            BorderImageOutsetId = GetId("border-image-outset");
            BorderImageRepeatId = GetId("border-image-repeat");
            CursorId = GetId("cursor");
            PointerEventsId = GetId("pointer-events");
            IsolationId = GetId("isolation");
            ContainerTypeId = GetId("container-type");
            ContainerNameId = GetId("container-name");
            ContainerId = GetId("container");
            TransformOriginId = GetId("transform-origin");
            TransformBoxId = GetId("transform-box");
            PerspectiveId = GetId("perspective");
            WillChangeId = GetId("will-change");
            ContainId = GetId("contain");
            ContentVisibilityId = GetId("content-visibility");
            TransitionId = GetId("transition");
            TransitionPropertyId = GetId("transition-property");
            TransitionDurationId = GetId("transition-duration");
            TransitionTimingFunctionId = GetId("transition-timing-function");
            TransitionDelayId = GetId("transition-delay");
            TransitionBehaviorId = GetId("transition-behavior");
            AnimationId = GetId("animation");
            AnimationNameId = GetId("animation-name");
            AnimationDurationId = GetId("animation-duration");
            AnimationTimingFunctionId = GetId("animation-timing-function");
            AnimationDelayId = GetId("animation-delay");
            AnimationIterationCountId = GetId("animation-iteration-count");
            AnimationDirectionId = GetId("animation-direction");
            AnimationFillModeId = GetId("animation-fill-mode");
            AnimationPlayStateId = GetId("animation-play-state");
            AnimationCompositionId = GetId("animation-composition");
            ContentId = GetId("content");
            ListStyleTypeId = GetId("list-style-type");
            ListStyleImageId = GetId("list-style-image");
            BorderCollapseId = GetId("border-collapse");
            EmptyCellsId = GetId("empty-cells");
            ClipPathId = GetId("clip-path");
            MaskId = GetId("mask");
            MaskImageId = GetId("mask-image");
            MaskModeId = GetId("mask-mode");
            MaskRepeatId = GetId("mask-repeat");
            MaskPositionId = GetId("mask-position");
            MaskSizeId = GetId("mask-size");
            MaskOriginId = GetId("mask-origin");
            MaskClipId = GetId("mask-clip");
            MaskCompositeId = GetId("mask-composite");
            MixBlendModeId = GetId("mix-blend-mode");
            BackgroundBlendModeId = GetId("background-blend-mode");
            OutlineId = GetId("outline");
            OutlineColorId = GetId("outline-color");
            OutlineStyleId = GetId("outline-style");
            OutlineWidthId = GetId("outline-width");
            OutlineOffsetId = GetId("outline-offset");
            PositionTryFallbacksId = GetId("position-try-fallbacks");
            BoxDecorationBreakId = GetId("box-decoration-break");
            ContainIntrinsicWidthId = GetId("contain-intrinsic-width");
            ContainIntrinsicHeightId = GetId("contain-intrinsic-height");
            ContainIntrinsicSizeId = GetId("contain-intrinsic-size");
            // CSS Multi-column Layout L1 §3 — column properties.
            // column-count / column-width are non-inherited; initial: auto.
            // column-gap is already registered above (shared with flex/grid).
            // column-rule-* are non-inherited; initial values match border defaults.
            // column-fill is non-inherited; initial: balance.
            ColumnCountId = GetId("column-count");
            ColumnWidthId = GetId("column-width");
            ColumnsId = GetId("columns");
            ColumnRuleWidthId = GetId("column-rule-width");
            ColumnRuleStyleId = GetId("column-rule-style");
            ColumnRuleColorId = GetId("column-rule-color");
            ColumnRuleId = GetId("column-rule");
            ColumnFillId = GetId("column-fill");
            ColumnSpanId = GetId("column-span");
        }

        public static readonly int DisplayId, PositionId, TopId, RightId, BottomId, LeftId, ZIndexId;
        public static readonly int OverflowId, OverflowXId, OverflowYId, OverflowClipMarginId;
        public static readonly int OverflowClipMarginTopId, OverflowClipMarginRightId, OverflowClipMarginBottomId, OverflowClipMarginLeftId;
        public static readonly int WidthId, HeightId, MinWidthId, MinHeightId, MaxWidthId, MaxHeightId, BoxSizingId;
        public static readonly int PaddingId, PaddingTopId, PaddingRightId, PaddingBottomId, PaddingLeftId;
        public static readonly int MarginId, MarginTopId, MarginRightId, MarginBottomId, MarginLeftId;
        public static readonly int BorderTopId, BorderRightId, BorderBottomId, BorderLeftId;
        public static readonly int BorderTopWidthId, BorderRightWidthId, BorderBottomWidthId, BorderLeftWidthId;
        public static readonly int BorderTopStyleId, BorderRightStyleId, BorderBottomStyleId, BorderLeftStyleId;
        public static readonly int FontSizeId, FontFamilyId, ColorId, LineHeightId;
        public static readonly int WhiteSpaceId, TextAlignId, TextAlignLastId, TextJustifyId, TextIndentId, TextWrapId;
        public static readonly int DirectionId, WritingModeId, UnicodeBidiId, TabSizeId, HyphensId;
        public static readonly int WordBreakId, LineBreakId, OverflowWrapId, WordWrapId;
        public static readonly int FlexDirectionId, FlexFlowId;
        public static readonly int OpacityId, TransformId, TextOverflowId;
        public static readonly int AnchorNameId, PositionAnchorId;
        public static readonly int AspectRatioId;
        public static readonly int FloatId, ClearId;
        public static readonly int LetterSpacingId, FontWeightId, FontStyleId, FontVariantId;
        public static readonly int BackgroundImageId, BackgroundColorId, BackgroundPositionId, BackgroundSizeId, BackgroundRepeatId;
        public static readonly int BorderRadiusId, BorderTopLeftRadiusId, BorderTopRightRadiusId, BorderBottomRightRadiusId, BorderBottomLeftRadiusId;
        public static readonly int BorderTopColorId, BorderRightColorId, BorderBottomColorId, BorderLeftColorId;
        public static readonly int BoxShadowId, TextShadowId, FilterId, BackdropFilterId;
        public static readonly int TextDecorationColorId, TextDecorationStyleId, TextDecorationThicknessId, TextUnderlineOffsetId;
        public static readonly int WebkitTextStrokeWidthId, WebkitTextStrokeColorId;
        public static readonly int FontVariationSettingsId, FontOpticalSizingId, FontFeatureSettingsId, ColorSchemeId;
        public static readonly int VisibilityId;
        // Second wave (typed-cascade migration phase 2).
        public static readonly int InsetId;
        public static readonly int FlexId, FlexWrapId, FlexBasisId, FlexGrowId, FlexShrinkId;
        public static readonly int JustifyContentId, AlignItemsId, AlignSelfId, AlignContentId;
        public static readonly int GapId, RowGapId, ColumnGapId, OrderId;
        public static readonly int GridTemplateColumnsId, GridTemplateRowsId, GridTemplateAreasId, GridTemplateId;
        public static readonly int GridColumnId, GridRowId, GridColumnStartId, GridColumnEndId, GridRowStartId, GridRowEndId;
        public static readonly int GridAutoFlowId, GridAutoColumnsId, GridAutoRowsId, GridAreaId;
        public static readonly int PlaceItemsId, PlaceContentId, PlaceSelfId, JustifyItemsId, JustifySelfId;
        public static readonly int WordSpacingId, FontId, TextTransformId, TextDecorationId, TextDecorationLineId, ImageRenderingId;
        public static readonly int CaretColorId, AccentColorId, ObjectFitId, ObjectPositionId;
        public static readonly int BackgroundClipId, BackgroundOriginId, BackgroundAttachmentId, BackgroundId;
        public static readonly int BorderId, BorderWidthId, BorderStyleId, BorderColorId;
        public static readonly int BorderImageSourceId, BorderImageSliceId, BorderImageWidthId, BorderImageOutsetId, BorderImageRepeatId;
        public static readonly int CursorId, PointerEventsId, IsolationId;
        public static readonly int ContainerTypeId, ContainerNameId, ContainerId;
        public static readonly int TransformOriginId, TransformBoxId, PerspectiveId, WillChangeId, ContainId, ContentVisibilityId;
        public static readonly int TransitionId, TransitionPropertyId, TransitionDurationId, TransitionTimingFunctionId, TransitionDelayId;
        public static readonly int TransitionBehaviorId;
        public static readonly int AnimationId, AnimationNameId, AnimationDurationId, AnimationTimingFunctionId, AnimationDelayId;
        public static readonly int AnimationIterationCountId, AnimationDirectionId, AnimationFillModeId, AnimationPlayStateId;
        public static readonly int AnimationCompositionId;
        public static readonly int ContentId, ListStyleTypeId, ListStyleImageId, ClipPathId, MaskId;
        public static readonly int BorderCollapseId, EmptyCellsId;
        public static readonly int MaskImageId, MaskModeId, MaskRepeatId, MaskPositionId, MaskSizeId, MaskOriginId, MaskClipId, MaskCompositeId;
        public static readonly int MixBlendModeId;
        public static readonly int BackgroundBlendModeId;
        public static readonly int OutlineId, OutlineColorId, OutlineStyleId, OutlineWidthId, OutlineOffsetId;
        public static readonly int PositionTryFallbacksId;
        // CSS Fragmentation L3 §6.1 / CSS Box Model L4 §5.
        // Non-inherited; initial: slice. Paint uses this to decide whether
        // each inline-box fragment gets full borders (clone) or suppresses
        // break-edge borders (slice).
        public static readonly int BoxDecorationBreakId;
        // CSS Containment L2 §4 / CSS Sizing L4 §5 — fast-path IDs for the
        // layout-side contain-intrinsic-size resolution hook points.
        public static readonly int ContainIntrinsicWidthId, ContainIntrinsicHeightId, ContainIntrinsicSizeId;
        // CSS Multi-column Layout L1 §3 — non-inherited column properties.
        public static readonly int ColumnCountId, ColumnWidthId, ColumnsId;
        public static readonly int ColumnRuleWidthId, ColumnRuleStyleId, ColumnRuleColorId, ColumnRuleId;
        public static readonly int ColumnFillId, ColumnSpanId;

        public static IReadOnlyDictionary<string, CssProperty> All => registry;

        // Number of registered (non-custom) property ids. ComputedStyle reads
        // this at construction to size its per-element values array. Grows
        // monotonically via Register(); ComputedStyle handles growth lazily.
        public static int RegisteredCount {
            get { lock (regLock) return nextId; }
        }

        // PA5: bitmask whose set bits identify the property ids whose
        // `CssProperty.IsInherited == true`. Used by `CascadeEngine.FillInherited`
        // to skip the ~150+ non-inherited property ids entirely when filling
        // missing slots from the parent style (the only "fill from parent" path
        // — non-inherited properties just take their initial value, which is
        // the slot's default state for any element). One bit per property id;
        // sized to ceil(RegisteredCount/64) ulongs. Regenerated lazily when
        // `RegisteredCount` outgrows the cached length (late `Register()` calls
        // from feature modules), guarded by `regLock`. Read-only after that —
        // hot-path callers grab the array reference once via `GetInheritedMask`
        // and walk it allocation-free.
        static ulong[] inheritedMaskCache;
        static int inheritedMaskBuiltForCount;

        // Returns a ulong[] of length ceil(RegisteredCount/64). Bit (id & 63) of
        // word (id >> 6) is set iff that property id is inherited. Stable for
        // any cascade pass that doesn't trigger a fresh `Register()` mid-flight.
        // The returned array is internal to the registry — callers must treat
        // it as read-only.
        public static ulong[] GetInheritedMask() {
            // Fast path: snapshot fields under no lock when the cached mask
            // covers every currently-registered id. The cache is monotonically
            // grown (RebuildInheritedMaskLocked replaces the array atomically
            // under regLock), and a stale read here just falls through to the
            // locked rebuild path — never produces an undersized mask.
            var cached = inheritedMaskCache;
            int builtFor = inheritedMaskBuiltForCount;
            int current = RegisteredCount;
            if (cached != null && builtFor == current) return cached;
            lock (regLock) {
                if (inheritedMaskCache != null && inheritedMaskBuiltForCount == nextId) {
                    return inheritedMaskCache;
                }
                RebuildInheritedMaskLocked();
                return inheritedMaskCache;
            }
        }

        static void RebuildInheritedMaskLocked() {
            int n = nextId;
            int words = (n + 63) >> 6;
            if (words == 0) words = 1;
            var mask = new ulong[words];
            for (int id = 0; id < n; id++) {
                var p = propertyById[id];
                if (p != null && p.IsInherited) {
                    mask[id >> 6] |= 1UL << (id & 63);
                }
            }
            inheritedMaskCache = mask;
            inheritedMaskBuiltForCount = n;
        }

        public static bool TryGet(string name, out CssProperty property) {
            return registry.TryGetValue(name, out property);
        }

        public static CssProperty Get(string name) {
            if (IsCustomProperty(name)) return CustomProperty(name);
            return registry.TryGetValue(name ?? "", out var p) ? p : null;
        }

        public static CssProperty Get(int id) {
            if (id < 0 || id >= nextId) return null;
            return propertyById[id];
        }

        static string[] initialValueById;
        static int initialValueBuiltForCount = -1;

        public static string GetInitialValue(int id) {
            if (id < 0) return null;
            var arr = initialValueById;
            if (arr == null || initialValueBuiltForCount != nextId) {
                lock (regLock) {
                    arr = new string[nextId];
                    for (int i = 0; i < nextId; i++) {
                        arr[i] = propertyById[i]?.InitialValue;
                    }
                    initialValueById = arr;
                    initialValueBuiltForCount = nextId;
                }
            }
            if ((uint)id >= (uint)arr.Length) return null;
            return arr[id];
        }

        public static bool IsCustomProperty(string name) {
            return name != null
                && name.Length >= 2
                && name[0] == '-'
                && name[1] == '-';
        }

        public static CssProperty CustomProperty(string name) {
            return new CssProperty(name, true, "");
        }

        // Returns the registered integer id, or -1 for custom properties (`--*`)
        // and unknown names. -1 routes the value through the side dictionary in
        // ComputedStyle. Process-global; cache the result, never call this on
        // the per-element hot path.
        public static int GetId(string name) {
            if (string.IsNullOrEmpty(name)) return -1;
            if (name[0] == '-' && name.Length > 1 && name[1] == '-') return -1;
            return idByName.TryGetValue(name, out var id) ? id : -1;
        }

        // Inverse of GetId. Returns null when the id is out of range. Used by
        // ComputedStyle.Enumerate() to project array indices back to property
        // names. Backed by the nameById array so this is O(1).
        public static string GetName(int id) {
            if (id < 0 || id >= nextId) return null;
            return nameById[id];
        }

        // Additive registration so feature modules (anchor positioning, subgrid
        // helpers) can add their own properties without editing the central
        // registry. Idempotent: re-registering with the same property reuses
        // the original Id (callers may have cached it). Re-registering with
        // a different definition overwrites the registry entry but preserves
        // the existing Id — Id stability is the load-bearing invariant.
        public static void Register(string name, bool inherited, string initial) {
            if (string.IsNullOrEmpty(name)) return;
            lock (regLock) {
                CssProperty registered;
                if (idByName.TryGetValue(name, out var existingId)) {
                    var prev = (uint)existingId < (uint)propertyById.Length ? propertyById[existingId] : null;
                    registered = new CssProperty(name, inherited, initial, existingId);
                    registry[name] = registered;
                    if ((uint)existingId < (uint)propertyById.Length) propertyById[existingId] = registered;
                    // PA5: only the IsInherited bit affects the cached
                    // inheritance bitmap. Skip the rebuild if it didn't flip.
                    if (prev == null || prev.IsInherited != inherited) {
                        inheritedMaskCache = null;
                        inheritedMaskBuiltForCount = -1;
                    }
                    return;
                }
                int id = nextId++;
                if (id >= nameById.Length) {
                    var grown = new string[nameById.Length * 2];
                    System.Array.Copy(nameById, grown, nameById.Length);
                    nameById = grown;
                    var grownProps = new CssProperty[propertyById.Length * 2];
                    System.Array.Copy(propertyById, grownProps, propertyById.Length);
                    propertyById = grownProps;
                }
                registered = new CssProperty(name, inherited, initial, id);
                nameById[id] = name;
                propertyById[id] = registered;
                idByName[name] = id;
                registry[name] = registered;
                // PA5: a freshly-registered id may set a new inherited bit,
                // and definitely grows the mask length. Force lazy rebuild on
                // next read.
                inheritedMaskCache = null;
                inheritedMaskBuiltForCount = -1;
            }
        }

        public static string InitialValueOf(string name) {
            var p = Get(name);
            return p != null ? p.InitialValue : "";
        }

        public static bool IsInherited(string name) {
            if (IsCustomProperty(name)) return true;
            return registry.TryGetValue(name ?? "", out var p) && p.IsInherited;
        }

        static void BuildRegistry() {
            void Add(string name, bool inherited, string initial) => Register(name, inherited, initial);

            Add("display", false, "inline");
            Add("position", false, "static");
            Add("top", false, "auto");
            Add("right", false, "auto");
            Add("bottom", false, "auto");
            Add("left", false, "auto");
            Add("inset", false, "auto");
            Add("inset-inline", false, "auto");
            Add("inset-inline-start", false, "auto");
            Add("inset-inline-end", false, "auto");
            Add("inset-block", false, "auto");
            Add("inset-block-start", false, "auto");
            Add("inset-block-end", false, "auto");
            Add("z-index", false, "auto");

            Add("overflow", false, "visible");
            Add("overflow-x", false, "visible");
            Add("overflow-y", false, "visible");
            Add("overflow-clip-margin", false, "0px");
            Add("overflow-clip-margin-top", false, "0px");
            Add("overflow-clip-margin-right", false, "0px");
            Add("overflow-clip-margin-bottom", false, "0px");
            Add("overflow-clip-margin-left", false, "0px");
            Add("overflow-clip-margin-block-start", false, "0px");
            Add("overflow-clip-margin-block-end", false, "0px");
            Add("overflow-clip-margin-inline-start", false, "0px");
            Add("overflow-clip-margin-inline-end", false, "0px");

            Add("flex", false, "0 1 auto");
            Add("flex-direction", false, "row");
            Add("flex-wrap", false, "nowrap");
            Add("flex-basis", false, "auto");
            Add("flex-grow", false, "0");
            Add("flex-shrink", false, "1");
            Add("flex-flow", false, "row nowrap");
            // CSS Box Alignment §3: initial is `normal`. Flex consumes
            // `normal` as `flex-start` (FlexProperties.ParseJustify), grid
            // treats `normal` as a stretch hint for auto/fr tracks while
            // explicit `start`/`flex-start` opts out (see GridLayout's
            // stretchCols computation). Storing "normal" preserves the
            // "no value set" signal so grid can disambiguate from an
            // explicit `start`.
            Add("justify-content", false, "normal");
            Add("align-items", false, "stretch");
            Add("align-self", false, "auto");
            // Mirror of justify-content: initial = `normal` per CSS Box
            // Alignment §3. Stored as "normal" so grid can distinguish
            // "no value set" (stretch auto tracks) from explicit `start`
            // (opt out of stretch — e.g. inventory's `align-content: start`).
            Add("align-content", false, "normal");
            Add("gap", false, "normal");
            Add("row-gap", false, "normal");
            Add("column-gap", false, "normal");
            Add("order", false, "0");

            Add("grid-template-columns", false, "none");
            Add("grid-template-rows", false, "none");
            Add("grid-template-areas", false, "none");
            Add("grid-template", false, "none");
            Add("grid-column", false, "auto");
            Add("grid-row", false, "auto");
            Add("grid-column-start", false, "auto");
            Add("grid-column-end", false, "auto");
            Add("grid-row-start", false, "auto");
            Add("grid-row-end", false, "auto");
            Add("grid-auto-flow", false, "row");
            Add("grid-auto-columns", false, "auto");
            Add("grid-auto-rows", false, "auto");
            Add("grid-area", false, "auto");
            Add("place-items", false, "normal legacy");
            Add("place-content", false, "normal");
            Add("place-self", false, "auto");
            Add("justify-items", false, "legacy");
            Add("justify-self", false, "auto");

            Add("width", false, "auto");
            Add("height", false, "auto");
            Add("min-width", false, "auto");
            Add("min-height", false, "auto");
            Add("max-width", false, "none");
            Add("max-height", false, "none");
            Add("inline-size", false, "auto");
            Add("block-size", false, "auto");
            Add("min-inline-size", false, "auto");
            Add("min-block-size", false, "auto");
            Add("max-inline-size", false, "none");
            Add("max-block-size", false, "none");
            Add("aspect-ratio", false, "auto");

            // CSS 2.1 §9.5 floats. `float` removes the box from normal block
            // flow and shifts it to the leading/trailing edge of its
            // containing block; subsequent in-flow content flows around it.
            // `clear` forces the box below any matching earlier float. Both
            // are non-inherited; initial value `none` keeps the box in flow.
            // BlockLayout consults these via CssProperties.FloatId / ClearId;
            // BoxBuilder uses the raw `float` value to "blockify" inline
            // floats per CSS 2.1 §9.7.
            Add("float", false, "none");
            Add("clear", false, "none");

            // CSS Images 3 §5: object-fit / object-position. Applies to
            // replaced elements (`<img>`, eventually `<video>`/`<canvas>`).
            // BoxToPaintConverter consults these when painting the image
            // brush into the box bounds.
            Add("object-fit", false, "fill");
            Add("object-position", false, "50% 50%");

            // CSS Tables. TableLayout consumes border-spacing and vertical-align
            // today; the rest are carried as first-class properties so authored
            // table CSS does not spill through the unknown-property path.
            // Remaining table-model gaps are documented in CONFORMANCE.md:
            // collapsed border conflict resolution/painting and advanced
            // fragmentation.
            Add("border-collapse", true, "separate");
            Add("border-spacing", true, "0");
            Add("caption-side", true, "top");
            Add("empty-cells", true, "show");
            Add("table-layout", false, "auto");
            Add("vertical-align", false, "baseline");

            Add("padding", false, "0");
            Add("padding-top", false, "0");
            Add("padding-right", false, "0");
            Add("padding-bottom", false, "0");
            Add("padding-left", false, "0");
            Add("padding-inline", false, "0");
            Add("padding-inline-start", false, "0");
            Add("padding-inline-end", false, "0");
            Add("padding-block", false, "0");
            Add("padding-block-start", false, "0");
            Add("padding-block-end", false, "0");

            Add("margin", false, "0");
            Add("margin-top", false, "0");
            Add("margin-right", false, "0");
            Add("margin-bottom", false, "0");
            Add("margin-left", false, "0");
            Add("margin-inline", false, "0");
            Add("margin-inline-start", false, "0");
            Add("margin-inline-end", false, "0");
            Add("margin-block", false, "0");
            Add("margin-block-start", false, "0");
            Add("margin-block-end", false, "0");

            Add("border", false, "medium none currentColor");
            Add("border-width", false, "medium");
            Add("border-style", false, "none");
            Add("border-color", false, "currentColor");
            Add("border-top", false, "medium none currentColor");
            Add("border-right", false, "medium none currentColor");
            Add("border-bottom", false, "medium none currentColor");
            Add("border-left", false, "medium none currentColor");
            Add("border-top-width", false, "medium");
            Add("border-right-width", false, "medium");
            Add("border-bottom-width", false, "medium");
            Add("border-left-width", false, "medium");
            Add("border-top-style", false, "none");
            Add("border-right-style", false, "none");
            Add("border-bottom-style", false, "none");
            Add("border-left-style", false, "none");
            Add("border-top-color", false, "currentColor");
            Add("border-right-color", false, "currentColor");
            Add("border-bottom-color", false, "currentColor");
            Add("border-left-color", false, "currentColor");
            Add("border-inline", false, "medium none currentColor");
            Add("border-inline-start", false, "medium none currentColor");
            Add("border-inline-end", false, "medium none currentColor");
            Add("border-inline-width", false, "medium");
            Add("border-inline-style", false, "none");
            Add("border-inline-color", false, "currentColor");
            Add("border-inline-start-width", false, "medium");
            Add("border-inline-start-style", false, "none");
            Add("border-inline-start-color", false, "currentColor");
            Add("border-inline-end-width", false, "medium");
            Add("border-inline-end-style", false, "none");
            Add("border-inline-end-color", false, "currentColor");
            Add("border-block", false, "medium none currentColor");
            Add("border-block-start", false, "medium none currentColor");
            Add("border-block-end", false, "medium none currentColor");
            Add("border-block-width", false, "medium");
            Add("border-block-style", false, "none");
            Add("border-block-color", false, "currentColor");
            Add("border-block-start-width", false, "medium");
            Add("border-block-start-style", false, "none");
            Add("border-block-start-color", false, "currentColor");
            Add("border-block-end-width", false, "medium");
            Add("border-block-end-style", false, "none");
            Add("border-block-end-color", false, "currentColor");

            Add("border-radius", false, "0");
            Add("border-top-left-radius", false, "0");
            Add("border-top-right-radius", false, "0");
            Add("border-bottom-right-radius", false, "0");
            Add("border-bottom-left-radius", false, "0");
            Add("border-start-start-radius", false, "0");
            Add("border-start-end-radius", false, "0");
            Add("border-end-start-radius", false, "0");
            Add("border-end-end-radius", false, "0");

            // CSS Basic User Interface §4.1: the initial value of `box-sizing`
            // is `content-box` (width/height set the content area; padding +
            // border add to the outside). Earlier versions of this project
            // deliberately broke the spec here and defaulted to `border-box`,
            // but that diverged from Chrome's getBoundingClientRect (the
            // ground truth the LayoutDiff suite compares against). Authors who
            // want the historic behaviour can still write `* { box-sizing:
            // border-box }`.
            Add("box-sizing", false, "content-box");

            Add("color", true, "black");
            // CSS Basic User Interface 4 §5.4: `caret-color` colours the
            // text-input insertion caret. `auto` (the initial value) lets
            // the UA pick — InputRenderer falls back to currentColor when
            // unset/auto. The property is inherited so authors can theme an
            // entire form region by setting it on a wrapping element.
            Add("caret-color", true, "auto");
            // CSS Basic User Interface 4 §5.5: `accent-color` tints UA-drawn
            // form-control accents (checkbox tick, radio dot, etc.). Inherited
            // so authors can theme an entire form region by setting it on a
            // wrapping element. Initial `auto` defers to the platform default;
            // InputRenderer falls back to its hard-coded indigo in that case.
            Add("accent-color", true, "auto");
            Add("font-family", true, "sans-serif");
            Add("font-size", true, "16px");
            Add("font-weight", true, "normal");
            Add("font-style", true, "normal");
            Add("font-variant", true, "normal");
            // CSS Fonts L4 §6.1–§6.6, §6.12 — font-variant-* longhands.
            // All six specify "Inherited: yes". Without these registrations the
            // properties spill to customProps and don't propagate via the
            // inheritance bitmask path. Gap A12 closed 2026-05-30.
            Add("font-variant-ligatures", true, "normal");
            Add("font-variant-position", true, "normal");
            Add("font-variant-caps", true, "normal");
            Add("font-variant-alternates", true, "normal");
            Add("font-variant-east-asian", true, "normal");
            Add("font-variant-emoji", true, "normal");
            Add("font", true, "normal normal normal medium sans-serif");
            Add("line-height", true, "normal");
            Add("letter-spacing", true, "normal");
            Add("word-spacing", true, "normal");

            Add("direction", true, "ltr");
            Add("writing-mode", true, "horizontal-tb");
            Add("unicode-bidi", false, "normal");
            Add("text-align", true, "start");
            Add("text-align-last", true, "auto");
            Add("text-justify", true, "auto");
            Add("text-indent", true, "0");
            Add("text-wrap", true, "wrap");
            Add("tab-size", true, "8");
            Add("hyphens", true, "manual");
            Add("text-transform", true, "none");
            Add("text-shadow", true, "none");
            // CSS Writing Modes L4 §5.4 — text-orientation controls glyph
            // orientation inside vertical writing modes. Inherited per spec.
            // Initial `mixed`. Renderer is horizontal-only in v1 so the
            // value round-trips through cascade but doesn't affect paint.
            Add("text-orientation", true, "mixed");
            // CSS Text Decoration L4 §5 — text-emphasis-* longhands. All
            // inherited per spec. Initial: -style=none, -color=currentcolor,
            // -position=over right. Cascade round-trip only — emphasis marks
            // are not yet rendered by the SDF text path.
            Add("text-emphasis-style", true, "none");
            Add("text-emphasis-color", true, "currentcolor");
            Add("text-emphasis-position", true, "over right");
            // CSS Paint Order §6 (SVG-inherited) — paint-order controls the
            // fill / stroke / markers paint sequence. Inherited per spec.
            // Initial `normal`. Round-trip only; the renderer always paints
            // fill before stroke in v1.
            Add("paint-order", true, "normal");
            // CSS Fragmentation L3 §3 — break-before / break-after / break-inside
            // declare forced and discretionary fragmentation breaks. Not inherited
            // per spec. Initial `auto`. Round-trip only; game UI is non-paginated
            // so these never trigger a fragment break, but cascade must carry
            // the value for tools / @page contexts that inspect computed style.
            Add("break-before", false, "auto");
            Add("break-after", false, "auto");
            Add("break-inside", false, "auto");
            // CSS Color Adjustment L1 §10 — forced-color-adjust opts an element
            // out of the OS forced-colors color rewrite. Not inherited per spec.
            // Initial `auto` (UA may rewrite colors). Round-trip only — game UI
            // does not integrate with OS forced-colors mode in v1.
            Add("forced-color-adjust", false, "auto");
            // CSS Text Decoration L4 §10 — text-stroke. Inherited per spec so a
            // single `body { -webkit-text-stroke: 1px black }` propagates to
            // every text-bearing descendant. Initial 0/currentcolor = no stroke.
            Add("-webkit-text-stroke-width", true, "0");
            Add("-webkit-text-stroke-color", true, "currentcolor");
            // CSS Fonts 4 §6.10 — variable font axis controls. Inherited.
            // `normal` on the settings list = no axis overrides (font defaults).
            // `auto` on optical-sizing = engine drives `opsz` from font-size.
            Add("font-variation-settings", true, "normal");
            Add("font-feature-settings", true, "normal");
            Add("color-scheme", true, "normal");
            Add("font-optical-sizing", true, "auto");
            // CSS Fonts 4 §6.3 — font-stretch. Registered for parser/cascade
            // compatibility so authors can use `font-stretch: condensed`,
            // `expanded`, `<percentage>`. Variable fonts pick up the value via
            // the `wdth` axis when expressed as percentage; the underlying
            // backend's per-static-face mapping is best-effort and not
            // browser-complete. CSS_FEATURE_AUDIT marks this Parse-only.
            Add("font-stretch", true, "normal");
            // CSS Fonts 4 §6.6 — kerning toggle. Cascade carries the value;
            // the text path always applies kern (SdfFontMetrics.GetKern) so
            // `font-kerning: none` is currently a no-op. Tracked as Parse-only.
            Add("font-kerning", true, "auto");
            // CSS Fonts 4 §6.5 — font-synthesis controls fallback faux-bold /
            // faux-italic / faux-small-caps / faux-superscript. Weva's
            // text renderer doesn't synthesize any of these; the register
            // exists so author CSS round-trips cleanly.
            Add("font-synthesis", true, "weight style small-caps");
            Add("font-synthesis-weight", true, "auto");
            Add("font-synthesis-style", true, "auto");
            Add("font-synthesis-small-caps", true, "auto");
            Add("font-synthesis-position", true, "auto");
            // CSS Fonts 4 §6.7 — font-size-adjust. Cascade-only; metric-based
            // adaptive sizing is deferred to v2.
            Add("font-size-adjust", true, "none");
            // CSS Overflow L4 §6 — line-clamp. CSS Overflow L4 specifies
            // `line-clamp: <integer> | none` as the L4 standard form; the
            // pre-L4 vendor-prefixed `-webkit-line-clamp` is the legacy
            // alias most authors already use. Both register for cascade
            // round-trip; layout-side line truncation is wired separately
            // in the inline / block layout passes (planned).
            Add("line-clamp", false, "none");
            Add("-webkit-line-clamp", false, "none");
            // CSS Box Model L4 §5 — `box-decoration-break`. Splits decorations
            // across line/page fragments. Paint honors both values for inline
            // boxes wrapping across lines: `slice` (default) suppresses the
            // break-edge borders; `clone` decorates every fragment with full
            // borders/radius/background (BoxToPaintConverter, B22).
            Add("box-decoration-break", false, "slice");
            // `contain` and `will-change` are registered further down with
            // the stacking-context-related properties — see comments there.
            // CSS Containment L2 §4 — `content-visibility`. Author-controlled
            // rendering skip. Registered cascade-only; we don't skip layout
            // for off-screen content the way browsers do. Parse-only.
            Add("content-visibility", false, "visible");
            // CSS Containment L2 §4 — `contain-intrinsic-size`. Companion
            // dimension hint for `content-visibility: auto`. Parse-only.
            Add("contain-intrinsic-size", false, "none");
            Add("contain-intrinsic-width", false, "none");
            Add("contain-intrinsic-height", false, "none");
            Add("contain-intrinsic-block-size", false, "none");
            Add("contain-intrinsic-inline-size", false, "none");
            Add("image-rendering", true, "auto");
            Add("text-decoration", false, "none");

            // background-position/size/repeat were already registered as
            // shorthand-component properties via earlier groups (auto for
            // size, "0% 0%" for position, "repeat" for repeat). These
            // explicit Adds are no-ops if already registered; documented
            // here for clarity since the BackgroundResolver path now reads
            // them by name.
            Add("text-decoration-line", false, "none");
            Add("text-decoration-style", false, "solid");
            Add("text-decoration-color", false, "currentColor");
            // CSS Text Decoration 4 §3.3 / §4: thickness + offset are non-
            // inherited and default to `auto`. The TextRunResolver resolves
            // `auto` to font-derived defaults (ascent/12 thickness, 0 offset)
            // at paint time so the cascade can carry the keyword as-is.
            Add("text-decoration-thickness", false, "auto");
            // CSS Text Decoration L4 §9.7 — "Inherited: yes". Flipped from
            // false → true so `body { text-underline-offset: 3px }` propagates
            // to all descendant text-bearing elements. Was incorrectly registered
            // as non-inherited; gap closed 2026-05-30 (A8).
            Add("text-underline-offset", true, "auto");
            Add("text-overflow", false, "clip");
            Add("white-space", true, "normal");
            Add("white-space-collapse", true, "collapse");
            Add("word-break", true, "normal");
            // CSS Text L3 §5.3: line-break controls kinsoku prohibition
            // strictness for CJK text. Inherited; initial value "auto"
            // (Chrome-parity: auto resolves to "normal" behaviour in v1).
            Add("line-break", true, "auto");
            Add("overflow-wrap", true, "normal");
            Add("word-wrap", true, "normal");

            Add("background", false, "none");
            Add("background-color", false, "transparent");
            Add("background-image", false, "none");
            Add("background-size", false, "auto");
            Add("background-position", false, "0% 0%");
            Add("background-repeat", false, "repeat");
            Add("background-clip", false, "border-box");
            Add("background-origin", false, "padding-box");

            // border-image longhands (CSS Backgrounds 3 §6).
            Add("border-image-source", false, "none");
            Add("border-image-slice", false, "100%");
            Add("border-image-width", false, "1");
            Add("border-image-outset", false, "0");
            Add("border-image-repeat", false, "stretch");
            Add("background-attachment", false, "scroll");

            Add("opacity", false, "1");
            Add("visibility", true, "visible");
            Add("cursor", true, "auto");
            Add("box-shadow", false, "none");
            Add("pointer-events", false, "auto");
            Add("isolation", false, "auto");

            Add("container-type", false, "normal");
            Add("container-name", false, "none");
            Add("container", false, "none");

            Add("transform", false, "none");
            Add("transform-origin", false, "50% 50% 0");
            // CSS Transforms L1 §6.2 — `transform-box` selects the reference
            // box that `transform-origin` resolves against. Initial is
            // `view-box` (HTML default; SVG default is `fill-box`).
            // Non-inherited. Keywords: content-box, border-box, fill-box,
            // stroke-box, view-box.
            //
            // For HTML elements (the v1 engine target), `view-box`,
            // `fill-box`, `stroke-box`, and `border-box` all resolve to the
            // element's border box — there is no SVG viewport. Only
            // `content-box` produces a distinct origin basis (offset by
            // padding+border + reduced by padding+border). The cascade
            // round-trips every keyword verbatim; the resolver in
            // BoxToPaintConverter.ResolveTransformOrigin honours
            // `content-box`. Tracked via the `transform-box` round-trip
            // tests in TransformIndividualPropertyTests + the resolver
            // tests added with this registration.
            Add("transform-box", false, "view-box");
            // CSS Transforms L2 §3 — individual transform properties.
            // Composed as `translate * rotate * scale * transform` at paint
            // time (TransformResolver). Initial `none` per spec.
            Add("translate", false, "none");
            Add("rotate", false, "none");
            Add("scale", false, "none");

            // CSS Transforms L2 §13 — 3D transform properties. Weva's
            // paint pipeline is 2D-only (no perspective shader, no z-axis
            // transforms in URP), so these are cascade-only round-trips:
            // author stylesheets pasted from web stay valid and the values
            // are queryable via style.Get(name), but rendering ignores them.
            // Initial values per spec:
            //   perspective: none
            //   perspective-origin: 50% 50%
            //   transform-style: flat
            //   backface-visibility: visible
            Add("perspective", false, "none");
            Add("perspective-origin", false, "50% 50%");
            Add("transform-style", false, "flat");
            Add("backface-visibility", false, "visible");

            Add("filter", false, "none");
            Add("backdrop-filter", false, "none");
            Add("mix-blend-mode", false, "normal");
            // CSS Compositing and Blending L1 §6.3 — initial `normal`, non-inherited,
            // value `<blend-mode>#` (comma list). Multi-layer behaviour (repeating
            // shorter lists) is resolved at paint time by BackgroundResolver.
            // Gap A11 closed 2026-05-30.
            Add("background-blend-mode", false, "normal");
            Add("clip-path", false, "none");
            Add("mask", false, "none");
            Add("mask-image", false, "none");
            Add("mask-mode", false, "match-source");
            Add("mask-repeat", false, "repeat");
            Add("mask-position", false, "0% 0%");
            Add("mask-size", false, "auto");
            Add("mask-origin", false, "border-box");
            Add("mask-clip", false, "border-box");
            Add("mask-composite", false, "add");

            // CSS Will Change 1 §3 / CSS Containment 2 §3. Both establish a
            // stacking context for certain values (`will-change:
            // transform|opacity|filter`, `contain: layout|paint|strict|
            // content`) — CreatesStackingContext reads the cascaded value.
            // Real containment is implemented via ContainmentResolver (B15):
            // paint = padding-box clip + abs CB + independent FC; layout =
            // margin-collapse barrier + abs CB; size = zero content
            // contribution to auto block size. `style` containment and
            // inline-axis size remain documented gaps (see CSS_OPEN_GAPS).
            Add("will-change", false, "auto");
            Add("contain", false, "none");

            Add("transition", false, "all 0s ease 0s");
            Add("transition-property", false, "all");
            Add("transition-duration", false, "0s");
            Add("transition-timing-function", false, "ease");
            Add("transition-delay", false, "0s");
            // CSS Transitions L2 §3.1 — transition-behavior: normal | allow-discrete.
            // initial `normal`, non-inherited. `allow-discrete` enables discrete
            // animation of `display` and `content-visibility` (L2 §2.1).
            // Gap A13 closed 2026-05-30.
            Add("transition-behavior", false, "normal");

            Add("animation", false, "none 0s ease 0s 1 normal none running");
            Add("animation-name", false, "none");
            Add("animation-duration", false, "0s");
            Add("animation-timing-function", false, "ease");
            Add("animation-delay", false, "0s");
            Add("animation-iteration-count", false, "1");
            Add("animation-direction", false, "normal");
            Add("animation-fill-mode", false, "none");
            Add("animation-play-state", false, "running");
            // CSS Animations L2 §10. Value parsed and carried through the
            // cascade. `CssAnimationRunner.Compose` honours `replace` (default,
            // overwrite), `add` (sum with the underlying value when both sides
            // are numbers / lengths in matching units; falls back to `replace`
            // for other typed values for v1), and `accumulate` (treated as
            // `add` for v1 — strict spec accumulation across iteration counts
            // for transforms is deferred; tracked as H2b).
            Add("animation-composition", false, "replace");

            // CSS Anchor Positioning. Registering here (rather than only via
            // AnchorPositioningProperties.EnsureRegistered) guarantees these
            // ids are assigned BEFORE the static field captures below
            // (`AnchorNameId = GetId(...)`). Otherwise the captures would
            // resolve to -1 and the cascade would spill these properties
            // into the side dictionary while readers later look them up by
            // their now-positive id and find nothing — anchor positioning
            // silently breaks. EnsureRegistered remains a no-op idempotent
            // safety net for module-driven registration.
            Add("anchor-name", false, "none");
            Add("position-anchor", false, "auto");
            Add("position-try-fallbacks", false, "none");

            // CSS Generated Content (CSS 2.1 §12). The `content` property is
            // consulted by the cascade's ::before / ::after pseudo-element
            // path: a non-default value generates an anonymous child box. The
            // initial value "normal" computes to "none" for ::before / ::after
            // (per CSS 2.1) — i.e. by default no pseudo box is generated.
            // Authors opt in by writing `content: "..."` (or `content: ""` for
            // an empty decorative box).
            Add("content", false, "normal");

            // CSS Lists 3 §3.2: `list-style-type` is inherited so authors can
            // change the marker style on a `<ul>`/`<ol>` and have it apply to
            // every descendant `<li>`. Initial is "disc"; the UA stylesheet
            // overrides `ol { list-style-type: decimal }`. v2 honours the
            // CSS Counter Styles 3 §6 predefined identifiers:
            // disc, circle, square, decimal, decimal-leading-zero,
            // lower-/upper-roman, lower-/upper-alpha (alias latin), none.
            Add("list-style-type", true, "disc");

            // CSS Lists 3 §3.1 / §3.3: `list-style-position` controls whether
            // the marker is laid out inside the principal block (`inside`) or
            // in the marker box outside its left edge (`outside`). Both are
            // inherited. Initial is `outside`. v1's marker placement is the
            // same in-flow inline-block at the start of the li's content for
            // both values (no negative-margin outside-positioning pass), but
            // the longhand value is now carried by the cascade so authors can
            // observe / target it.
            Add("list-style-position", true, "outside");

            // CSS Lists 3 §3.3: `list-style-image` overrides `list-style-type`
            // when set to a non-`none` value. The token is carried by the
            // cascade and consumed by BoxBuilder.MaybeInjectListMarker: when
            // present (and non-`none`), the marker box drops its TextRun and
            // gets `background-image` set to the same url() so the rendered
            // glyph is the bitmap, per spec §3.3.
            Add("list-style-image", true, "none");

            // CSS Generated Content L3 §3 — `quotes` selects the pair(s) of
            // quotation marks rendered by `content: open-quote` and
            // `content: close-quote`. Values: `auto` | `none` | <string> <string>+.
            // Inherited: yes. Initial: `auto` (UA language-appropriate default).
            // v1 round-trips author values through the cascade; the rendering
            // layer for the quote-content keywords still resolves against
            // browser-default "" / '' fallbacks if the runtime quote-stack
            // resolver isn't wired.
            Add("quotes", true, "auto");

            // CSS Lists L3 §5 — counter-reset / counter-increment / counter-set.
            // All three are non-inherited (each box manages its own counter
            // scope) with initial value "none". The rendering layer
            // (BoxBuilder / PseudoElement counter() resolver) consumes the
            // raw token list; the cascade just round-trips the author value.
            // The counter() rendering still relies on the BoxBuilder counter
            // scope resolution — registration here is the prerequisite for
            // that resolver ever seeing the declaration.
            Add("counter-reset", false, "none");
            Add("counter-increment", false, "none");
            Add("counter-set", false, "none");

            // CSS Masking 1 §6.1 / §1: `clip-path` and `mask` are registered
            // as pass-through string properties so the cascade carries the
            // author value (and so authors writing them don't trip the
            // "unknown property" diagnostic). The renderer doesn't actually
            // honour them in v1 — UnknownStubProperties tracks the set so
            // ComputedStyle can fire a one-shot diagnostic when they're set.
            // Forward-compat round-tripping for properties commonly authored
            // in modern stylesheets. The cascade preserves the value;
            // rendering is a no-op (UnknownStubProperties below). Authors
            // writing these get one diagnostic warning per property name —
            // not the per-element flood from the unknown-property path.
            Add("user-select", false, "auto");
            // CSS Fonts L4 §6.5 — font-variant-numeric: "Inherited: yes".
            // Without inheritance, `body { font-variant-numeric: tabular-nums }`
            // doesn't propagate to descendants when queried via getComputedStyle
            // (and disagrees with the longhand expansion of font-variant, which
            // IS inherited).
            Add("font-variant-numeric", true, "normal");
            Add("scrollbar-width", false, "auto");
            // CSS Scrollbars L1 §3.2 — scrollbar-color: "Inherited: yes".
            Add("scrollbar-color", true, "auto");
            Add("scrollbar-gutter", false, "auto");
            Add("overscroll-behavior", false, "auto");
            Add("overscroll-behavior-x", false, "auto");
            Add("overscroll-behavior-y", false, "auto");

            // CSS UI 4 §7: `outline` paints around (not inside) the border
            // edge and does not affect layout. Registered as longhands +
            // shorthand so the cascade carries the value; BoxToPaintConverter
            // emits a single non-radii stroke when outline-style is non-`none`
            // and outline-width > 0. Initial values per spec: invert/medium/none.
            // We approximate `invert` with currentColor since v1 has no color
            // inversion primitive — the a11y-baseline focus ring still shows.
            Add("outline", false, "medium none invert");
            Add("outline-color", false, "invert");
            Add("outline-style", false, "none");
            Add("outline-width", false, "medium");
            Add("outline-offset", false, "0");

            // CSS Basic User Interface 4 §13 — `field-sizing` controls whether
            // form controls (`<input>`, `<textarea>`) use the UA's fixed-size
            // default box (`auto`) or shrink-wrap to the content of their value
            // (`content`). Initial value is `auto`. Non-inherited — each form
            // control picks its own sizing mode.
            // v1: parse + cascade round-trip only. The layout impact of
            // `field-sizing: content` (intrinsic width driven by `value` text)
            // is not yet implemented in InputLayoutHelper. The value is
            // queryable via `ComputedStyle.Get("field-sizing")` and can be
            // read by a future layout pass.
            Add("field-sizing", false, "auto");

            // CSS Multi-column Layout L1 §3.
            // column-count / column-width / columns are non-inherited; initial: auto.
            // column-gap is already registered above (shared with flex / grid gap).
            // column-rule-* follow the border model: width=medium, style=none, color=currentcolor.
            // column-fill: balance (default) / auto — non-inherited.
            // column-span: none / all — non-inherited, v1 parse-and-ignore for `all`.
            Add("column-count", false, "auto");
            Add("column-width", false, "auto");
            Add("columns", false, "auto");
            Add("column-rule-width", false, "medium");
            Add("column-rule-style", false, "none");
            Add("column-rule-color", false, "currentcolor");
            Add("column-rule", false, "medium none currentcolor");
            Add("column-fill", false, "balance");
            Add("column-span", false, "none");
        }

        // Opt-in set of properties whose visual intent is NOT honored — the
        // cascade carries the value but rendering / hit-testing ignores it.
        // ComputedStyle.Set emits a one-shot "not implemented" warning when an
        // author sets a member to a non-default value, surfacing the missed
        // visual effect in the console. Currently empty: every property the
        // cascade registers either honors its value or is a forward-compat
        // round-trip whose absence has no visual surprise (warning would be
        // noise). Add entries here only when an unimplemented property would
        // mislead an author about what they see on screen.
        static readonly HashSet<string> UnknownStubProperties = new HashSet<string>();

        public static bool IsStubProperty(string name) {
            return name != null && UnknownStubProperties.Contains(name);
        }
    }
}
