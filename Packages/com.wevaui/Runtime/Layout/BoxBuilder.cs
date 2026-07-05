using System;
using System.Collections.Generic;

using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Forms;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using Weva.Layout.Multicol;
using Weva.Layout.Tables;
using Weva.Layout.Text;
using Weva.Paint.Images;

namespace Weva.Layout {
    internal sealed class BoxBuilder {
        // styleOf is settable so the LayoutEngine can pool a single
        // BoxBuilder across Layout / RelayoutOneSubtree calls — rebinding
        // takes a closure that captures the current pass's styleOf
        // delegate without allocating a fresh BoxBuilder (~500B per call,
        // ~5KB/sec at 60Hz on a fixture with one warm flip per frame).
        Func<Element, ComputedStyle> styleOf;
        internal void Rebind(Func<Element, ComputedStyle> styleOfFn, Func<Element, ComputedStyle> backdropStyleOfFn, IImageRegistry imageRegistryRef) {
            this.styleOf = styleOfFn;
            this.backdropStyleOf = backdropStyleOfFn;
            this.imageRegistry = imageRegistryRef;
        }
        // Optional resolver for `::backdrop` styles. When set and an element
        // satisfies `TopLayer.IsHost`, BoxBuilder synthesises a backdrop
        // sibling box (positioned-fixed, viewport-sized) before the host's
        // own box. Null disables backdrop synthesis — used by tests and code
        // paths that don't have a CascadeEngine.
        Func<Element, ComputedStyle> backdropStyleOf;
        // Optional registry consulted for `<img>` natural-size resolution.
        // When set and an `<img>` has neither CSS width/height nor HTML
        // width/height attrs, the registry's source dimensions seed the
        // ComputedStyle so layout produces a non-zero box.
        IImageRegistry imageRegistry;
        readonly BoxPool pool;
        readonly LayoutScratch scratch;

        double pendingIntrinsicWidth;
        double pendingIntrinsicHeight;

        // Optional font metrics used by MaybeApplyFieldSizingWidth to measure
        // value-text width when `field-sizing: content` is set on a form control.
        // Set by LayoutEngine after creating the pooled BoxBuilder. When null, a
        // deterministic stub (StubCharWidthPx per character) is used instead —
        // this is a v1 approximation flagged for upgrade in B27 follow-on work
        // once a reliable headless measurement path exists for all font families.
        internal IFontMetrics FieldSizingMetrics { get; set; }

        // CSS UI L4 §13 field-sizing: content — UA stub constants.
        // StubCharWidthPx: per-character width used when FieldSizingMetrics is
        // null. Matches MonoFontMetrics default at 16px (CharWidthEm 0.5 ×
        // font-size 16px = 8px), so headless tests using MonoFontMetrics with
        // default font-size obtain numerically exact results without a live
        // FontEngine. V2 follow-on: plumb real font metrics from LayoutEngine.
        internal const double StubCharWidthPx = 8.0;
        // UA extra space reserved for the text cursor / caret. CSS UI L4 §13
        // does not mandate an exact amount; browsers typically use 1–4px.
        internal const double FieldSizingCaretPaddingPx = 4.0;

        // PA6 fix — precomputed `<li>` ordinals indexed by the `<li>` Element.
        // Populated in BuildChildren when the parent is `<ul>`/`<ol>` (one pass
        // over the parent's children, applying `start`/`reversed` on the
        // parent and any per-`<li>` `value` attribute). MaybeInjectListMarker
        // then reads the entry instead of walking the parent's children to
        // compute its own index — turning the build cost from O(N^2) into
        // O(N) for a list of N `<li>`s.
        //
        // Keyed by Element reference: nested `<ol>` inside an `<li>` simply
        // populates separate entries (different keys) so the same dict serves
        // every list in the document. Cleared at the top of each Build /
        // BuildDocument call so survivor entries from the previous build
        // don't pin the keyed Elements past their lifetime.
        readonly Dictionary<Element, int> liOrdinals = new();

        // Diagnostic counter — incremented every time MaybeInjectListMarker
        // falls back to the O(siblings) parent-walk because the precomputed
        // ordinal wasn't seeded (e.g. tests / one-shot builders that bypass
        // BuildChildren). Exposed for the PA6 regression test that pins the
        // walk-count to 0 on the precomputed path. Not load-bearing for
        // production behaviour.
        internal int ListMarkerOrdinalWalks;

        public BoxBuilder(Func<Element, ComputedStyle> styleOf, BoxPool pool, LayoutScratch scratch)
            : this(styleOf, null, null, pool, scratch) { }

        public BoxBuilder(Func<Element, ComputedStyle> styleOf, Func<Element, ComputedStyle> backdropStyleOf, BoxPool pool, LayoutScratch scratch)
            : this(styleOf, backdropStyleOf, null, pool, scratch) { }

        public BoxBuilder(Func<Element, ComputedStyle> styleOf, Func<Element, ComputedStyle> backdropStyleOf, IImageRegistry imageRegistry, BoxPool pool, LayoutScratch scratch) {
            this.styleOf = styleOf;
            this.backdropStyleOf = backdropStyleOf;
            this.imageRegistry = imageRegistry;
            this.pool = pool;
            this.scratch = scratch;
        }

        // Pseudo-element resolvers are settable so the LayoutEngine-owned
        // BoxBuilder picks up the current engine wiring without rebuilding
        // the BoxBuilder per Layout call.
        public Func<Element, ComputedStyle> BeforeStyleOf { get; set; }
        public Func<Element, ComputedStyle> AfterStyleOf { get; set; }
        public Func<Element, ComputedStyle> MarkerStyleOf { get; set; }

        // Fallback constructor for tests / one-shot box-tree introspection that
        // don't go through LayoutEngine. Allocates a fresh pool / scratch which
        // are GC'd along with the builder; callers paying for tests don't need
        // amortised pooling. BeginPass on the throwaway pool so Allocated tracks.
        public BoxBuilder(Func<Element, ComputedStyle> styleOf)
            : this(styleOf, null, null, new BoxPool(), new LayoutScratch()) {
            this.pool.BeginPass();
        }

        public BoxBuilder(Func<Element, ComputedStyle> styleOf, Func<Element, ComputedStyle> backdropStyleOf)
            : this(styleOf, backdropStyleOf, null, new BoxPool(), new LayoutScratch()) {
            this.pool.BeginPass();
        }

        // Display-dispatch: returns a BlockBox subclass for elements whose computed
        // display introduces a non-block formatting context. FlexBox / GridBox both
        // extend BlockBox so they participate in normal block flow (BlockLayout sizes
        // the outer frame); LayoutEngine then runs FlexLayout / GridLayout post-passes
        // to arrange their children.
        // See Runtime/Layout/Flex/FlexLayout.cs and Runtime/Layout/Grid/GridLayout.cs.
        BlockBox NewBlockBoxFor(string display) {
            if (display == "flex" || display == "inline-flex") {
                var fb = pool.AllocateFlexBox();
                fb.IsInline = display == "inline-flex";
                fb.IsInlineBlock = display == "inline-flex";
                return fb;
            }
            if (display == "grid" || display == "inline-grid") {
                var gb = pool.AllocateGridBox();
                gb.IsInline = display == "inline-grid";
                gb.IsInlineBlock = display == "inline-grid";
                return gb;
            }
            if (display == "table" || display == "inline-table") {
                var tb = pool.AllocateTableBox();
                tb.IsInline = display == "inline-table";
                tb.IsInlineBlock = display == "inline-table";
                return tb;
            }
            if (display == "table-row-group" || display == "table-header-group" || display == "table-footer-group") {
                var rg = pool.AllocateTableRowGroupBox();
                rg.GroupKind = display == "table-header-group" ? "header"
                    : display == "table-footer-group" ? "footer"
                    : "body";
                return rg;
            }
            if (display == "table-row") return pool.AllocateTableRowBox();
            if (display == "table-cell") return pool.AllocateTableCellBox();
            if (display == "table-caption") return pool.AllocateTableCaptionBox();
            var bb = pool.AllocateBlockBox();
            bb.IsInlineBlock = display == "inline-block";
            return bb;
        }

        // CSS Multi-column Layout L1 §2: a block container becomes a multicol
        // container when column-count or column-width is set to a non-auto value.
        // flex / grid / table containers are NOT multicol containers (spec §2 says
        // "block formatting context" is required — non-block containers ignore
        // column properties).
        static bool IsMulticolContainer(ComputedStyle style) {
            if (style == null) return false;
            string cc = style.Get(CssProperties.ColumnCountId);
            if (!string.IsNullOrEmpty(cc) && cc != "auto") return true;
            string cw = style.Get(CssProperties.ColumnWidthId);
            if (!string.IsNullOrEmpty(cw) && cw != "auto") return true;
            return false;
        }

        // Table-related displays count as block-level outer boxes for the
        // purpose of BoxBuilder's child-classification logic. Per CSS 2.1
        // §17.4: the table wrapper box is block-level; row-group, row, cell,
        // and caption boxes are block-level when they appear as the root of
        // an anonymous-wrapped subtree, but in well-formed HTML they live
        // inside a table and are positioned by TableLayout. Treating them as
        // block-level here ensures BoxFinalize doesn't sweep them into an
        // anonymous-block wrapper next to inline siblings.
        static bool IsTableDisplay(string disp) {
            return disp == "table" || disp == "inline-table"
                || disp == "table-row-group" || disp == "table-header-group" || disp == "table-footer-group"
                || disp == "table-row" || disp == "table-cell" || disp == "table-caption"
                || disp == "table-column" || disp == "table-column-group";
        }

        // Injects a `::backdrop` sibling box before `host`'s own box if the
        // host element is in the top layer (open modal dialog, open popover).
        // The backdrop's ComputedStyle comes from the engine-supplied resolver
        // (`CascadeEngine.ComputeBackdrop`) which forces position:fixed and
        // top/right/bottom/left:0 so the box covers the viewport regardless
        // of the cascaded author rules. Both backdrop and host are
        // position:fixed by UA stylesheet, so neither participates in the
        // parent's normal in-flow layout — they coexist as out-of-flow
        // siblings. Paint order in the parent's children list is the
        // tie-breaker: backdrop first, host on top. v1 simplification: the
        // CSS top-layer model would paint top-layer items above ALL normal
        // content regardless of stacking context; here we rely on
        // position:fixed promoting them in their stacking context, which
        // matches the visual outcome whenever no ancestor establishes a
        // transformed/filtered/will-change containing block.
        void MaybeInjectBackdrop(Element host, Box parent) {
            if (backdropStyleOf == null) return;
            if (!TopLayer.IsHost(host)) return;
            var backdropStyle = backdropStyleOf(host);
            if (backdropStyle == null) return;
            var bb = pool.AllocateBlockBox();
            bb.Element = null;
            bb.Style = backdropStyle;
            parent.AddChild(bb);
        }

        // Resolves a ::before / ::after pseudo-element style for `host` and,
        // if its `content` decodes to a literal string, builds the anonymous
        // child box and appends it to `parent`. The pseudo-element's display
        // is honoured (default is "inline"; authors may opt into block /
        // inline-block to get out-of-flow geometry, e.g. position:absolute
        // decorative pieces). String content becomes a single TextRun child;
        // empty content (`content: ""`) leaves the box childless but still
        // styled — matching the common decorative pattern from the demo's
        // mountain / sun overlays.
        void MaybeInjectPseudoElement(Element host, BlockBox parent, Func<Element, ComputedStyle> resolver) {
            if (resolver == null || host == null) return;
            var pseudoStyle = resolver(host);
            if (pseudoStyle == null) return;
            string contentRaw = pseudoStyle.Get("content");
            // Build a CounterContext so counter() / counters() in `content` resolve
            // against counter-reset / counter-increment / counter-set on the ancestry
            // chain. CSS Lists L3 §5. Also passes BeforeStyleOf / AfterStyleOf so
            // the tree walk accumulates quote depth from preceding pseudo-elements.
            // `afterPseudo=true` when resolving ::after so the walk also accumulates
            // the host's ::before and all descendants' pseudo content in document order.
            bool isAfter = ReferenceEquals(resolver, AfterStyleOf);
            var counterCtx = Weva.Css.Cascade.CounterContext.BuildFor(host, styleOf, BeforeStyleOf, AfterStyleOf, isAfter);
            // Resolve the `quotes` property from the pseudo's inherited style to
            // supply to ResolveContentString for open-quote / close-quote resolution.
            string quotesValue = pseudoStyle.Get("quotes");
            string text = Weva.Css.Cascade.CascadeEngine.ResolveContentString(contentRaw, host, counterCtx, quotesValue);
            if (text == null) return;
            string disp = StyleResolver.Display(pseudoStyle);
            if (disp == "none") return;
            BuildPseudoBox(host, parent, pseudoStyle, disp, text);
        }

        void MaybeInjectPseudoElement(Element host, InlineBox parent, Func<Element, ComputedStyle> resolver) {
            if (resolver == null || host == null) return;
            var pseudoStyle = resolver(host);
            if (pseudoStyle == null) return;
            string contentRaw = pseudoStyle.Get("content");
            // Build a CounterContext so counter() / counters() in `content` resolve
            // against counter-reset / counter-increment / counter-set on the ancestry
            // chain. CSS Lists L3 §5. Also passes BeforeStyleOf / AfterStyleOf so
            // the tree walk accumulates quote depth from preceding pseudo-elements.
            // `afterPseudo=true` when resolving ::after so the walk also accumulates
            // the host's ::before and all descendants' pseudo content in document order.
            bool isAfter = ReferenceEquals(resolver, AfterStyleOf);
            var counterCtx = Weva.Css.Cascade.CounterContext.BuildFor(host, styleOf, BeforeStyleOf, AfterStyleOf, isAfter);
            // Resolve the `quotes` property from the pseudo's inherited style to
            // supply to ResolveContentString for open-quote / close-quote resolution.
            string quotesValue = pseudoStyle.Get("quotes");
            string text = Weva.Css.Cascade.CascadeEngine.ResolveContentString(contentRaw, host, counterCtx, quotesValue);
            if (text == null) return;
            string disp = StyleResolver.Display(pseudoStyle);
            if (disp == "none") return;
            BuildPseudoBox(host, parent, pseudoStyle, disp, text);
        }

        void BuildPseudoBox(Element host, Box parent, ComputedStyle pseudoStyle, string disp, string text) {
            // CSS 2.1 §9.7 "blockification": position:absolute|fixed and
            // float: not-none map an inline outer display to block. Authors
            // routinely write `::before { content:""; position:absolute; ... }`
            // for decorative overlays (mountains, moons, ribbons) without
            // setting `display:block` — without this mapping the pseudo
            // becomes an inline box and BlockLayout never honors its
            // width/height/inset, so the decoration silently disappears.
            // Apply the mapping here so the existing block branch (below)
            // takes over.
            if (string.IsNullOrEmpty(disp) || disp == "inline") {
                // Per-style parsed cache: keyword-typed properties resolve via
                // direct pattern-match on the cached CssValue without
                // touching the raw string.
                string pos = KeywordName(pseudoStyle.GetParsed(CssProperties.PositionId));
                if (pos == "absolute" || pos == "fixed") {
                    disp = "block";
                } else {
                    string flt = KeywordName(pseudoStyle.GetParsed(CssProperties.FloatId));
                    if (!string.IsNullOrEmpty(flt) && flt != "none") disp = "block";
                }
            }
            // Block-display pseudos become a BlockBox; inline (default) and
            // inline-block / inline-flex / inline-grid go through the inline
            // path. The pseudo's host Element is left null on the generated
            // box: it has no DOM identity, so hit-testing / event dispatch
            // won't surface it (matching CSS — a pseudo isn't an Element).
            if (disp == "block" || disp == "flex" || disp == "grid"
                || disp == "inline-block" || disp == "inline-flex" || disp == "inline-grid"
                || disp == "inline-table" || IsTableDisplay(disp)) {
                var bb = NewBlockBoxFor(disp);
                bb.Element = null;
                bb.Style = pseudoStyle;
                if (text.Length > 0) {
                    var run = pool.AllocateTextRun();
                    run.Text = text;
                    run.Style = pseudoStyle;
                    run.Element = null;
                    run.SourceNode = null;
                    bb.AddChild(run);
                }
                // Finalize so a raw TextRun child of a flex/grid pseudo gets
                // wrapped in an anonymous flex/grid item (CSS Flexbox §4 /
                // Grid §6). Without this, FlexLayout/GridLayout — which only
                // collect BlockBox children — skip the run entirely and the
                // pseudo's cross axis collapses to padding height. Symptom:
                // `::after { content:"💣"; display:flex; }` paints W=N H=0.
                BoxFinalize.FinalizeBlockChildren(bb, pool, scratch);
                parent.AddChild(bb);
                return;
            }
            // Default (inline) pseudo box. Wraps the text run in an InlineBox
            // so author-supplied background / border / padding render on the
            // anonymous box itself rather than on each character.
            var ib = pool.AllocateInlineBox();
            ib.Element = null;
            ib.Style = pseudoStyle;
            if (text.Length > 0) {
                var run = pool.AllocateTextRun();
                run.Text = text;
                run.Style = pseudoStyle;
                run.Element = null;
                run.SourceNode = null;
                ib.AddChild(run);
            }
            parent.AddChild(ib);
        }

        // CSS Basic User Interface L4 §13 — `field-sizing: content` layout impact.
        //
        // When an `<input type="text">` (or any textual input) has
        // `field-sizing: content` in its computed style, the UA replaces its
        // default fixed inline-size (the UA `width` from FormControlStylesheet,
        // e.g. 218px border-box) with the intrinsic inline-size of the current
        // `value` attribute text.  Placeholder text is used when the value is
        // empty and a `placeholder` attribute is present, so the caret has a
        // meaningful minimum width.
        //
        // Measurement: uses FieldSizingMetrics when available (wired from
        // LayoutEngine), otherwise falls back to the StubCharWidthPx constant
        // (8px/char at 16px — exact match for MonoFontMetrics default so tests
        // run deterministically in the headless harness).
        //
        // v1 scope: `<input>` only (type=text / password / search / email / tel /
        // url / number). Textarea + select are future follow-on work.
        // Checkbox / radio inputs are excluded — those are replaced elements
        // whose size is set by the widget, not the value text.
        //
        // The method writes the computed intrinsic width directly into the
        // element's ComputedStyle as a `px`-valued `width` string, matching the
        // pattern used by `MaybeApplyImgIntrinsicSize` for `<img>`. The
        // downstream ApplyBoxModel in BlockLayout then reads the overridden
        // width value and applies min/max clamping as normal — so
        // `min-width`/`max-width` still constrain the result correctly.
        void MaybeApplyFieldSizingWidth(Element e, ComputedStyle style) {
            if (e == null || style == null) return;
            // Only <input> is supported in v1.
            if (e.TagName != "input") return;
            // Checkbox and radio use replaced-element widget sizing, not text width.
            string inputType = e.GetAttribute("type");
            if (!string.IsNullOrEmpty(inputType)) {
                string t = CssStringUtil.ToLowerInvariantOrSame(inputType);
                if (t == "checkbox" || t == "radio" || t == "range"
                    || t == "submit" || t == "button" || t == "reset"
                    || t == "image" || t == "file" || t == "color") {
                    return;
                }
            }
            // Read field-sizing from cascade. Must be "content" to activate.
            string fieldSizing = style.Get("field-sizing");
            if (string.IsNullOrEmpty(fieldSizing) || fieldSizing != "content") return;

            // Resolve the text to measure: prefer `value`, fall back to
            // `placeholder` when value is empty (gives the caret some width).
            string value = e.GetAttribute("value") ?? "";
            if (value.Length == 0) {
                string ph = e.GetAttribute("placeholder");
                if (!string.IsNullOrEmpty(ph)) value = ph;
            }

            // Measure the text. Use live font metrics when available; otherwise
            // use the stub constant. The stub (8px/char) is exact for
            // MonoFontMetrics at 16px font-size, so headless tests are precise.
            double textWidth;
            if (FieldSizingMetrics != null && value.Length > 0) {
                // Read font-size from the cascade. We don't have a LayoutContext
                // here so we use a simple fallback: if font-size is set as a px
                // string we parse it; otherwise default to 16px (UA medium).
                double fontSize = 16.0;
                string fsRaw = style.Get(CssProperties.FontSizeId);
                if (!string.IsNullOrEmpty(fsRaw) && fsRaw.EndsWith("px")) {
                    if (double.TryParse(fsRaw.AsSpan(0, fsRaw.Length - 2),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double fsVal) && fsVal > 0) {
                        fontSize = fsVal;
                    }
                }
                textWidth = FieldSizingMetrics.Measure(value, fontSize);
            } else {
                textWidth = value.Length * StubCharWidthPx;
            }

            // Intrinsic border-box width = text-width + caret pad + padding + border.
            // FormControlStylesheet uses box-sizing: border-box for inputs, so the
            // `width` we write must be the OUTER (border-box) size. We read the
            // existing padding/border from the UA style if they're set as px strings.
            double padL = ReadSimplePx(style.Get(CssProperties.PaddingLeftId), 8.0);
            double padR = ReadSimplePx(style.Get(CssProperties.PaddingRightId), 8.0);
            double bordL = ReadSimplePx(style.Get(CssProperties.BorderLeftWidthId), 1.0);
            double bordR = ReadSimplePx(style.Get(CssProperties.BorderRightWidthId), 1.0);

            // Check box-sizing: when border-box, the `width` value covers
            // padding + border so we include them in the computed intrinsic
            // width. Under content-box the engine adds frame separately.
            bool isBorderBox = IsFieldSizingBorderBox(style);
            double intrinsicWidth;
            if (isBorderBox) {
                intrinsicWidth = textWidth + FieldSizingCaretPaddingPx + padL + padR + bordL + bordR;
            } else {
                intrinsicWidth = textWidth + FieldSizingCaretPaddingPx;
            }
            if (intrinsicWidth < 0) intrinsicWidth = 0;

            // Override the CSS width. ApplyBoxModel in BlockLayout will then
            // read this value and apply min-width / max-width clamping normally.
            style.Set("width", intrinsicWidth.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "px");
        }

        // Local border-box check for field-sizing calculation. Mirrors
        // BlockLayout.IsBorderBox but accessible from BoxBuilder without
        // introducing a cross-class dependency.
        static bool IsFieldSizingBorderBox(ComputedStyle style) {
            if (style == null) return false;
            var v = style.GetParsed(CssProperties.BoxSizingId);
            if (v is CssKeyword k) return k.Identifier == "border-box";
            if (v is CssIdentifier id) return id.Name == "border-box";
            return style.Get(CssProperties.BoxSizingId) == "border-box";
        }

        // Parses a simple "<number>px" CSS length string. Returns `fallback`
        // when the string is null/empty, non-px, or unparseable. Used only by
        // MaybeApplyFieldSizingWidth to read padding/border values from the
        // ComputedStyle without allocating a full CssValue parse.
        static double ReadSimplePx(string raw, double fallback) {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (!raw.EndsWith("px")) return fallback;
            if (double.TryParse(raw.AsSpan(0, raw.Length - 2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double v)) return v;
            return fallback;
        }

        // Resolves `<img>` natural sizing per HTML / CSS Sizing L4. Order:
        //   1. CSS `width`/`height` (from cascade)  — if set, leave alone.
        //   2. HTML `width`/`height` attributes — applied as if they were
        //      author CSS lengths in `px`.
        //   3. Image registry intrinsic size — `IImageSource.Width/Height`
        //      from the resolved handle.
        // HTML attributes are written into the ComputedStyle (they act as
        // author CSS). Registry intrinsic dimensions are stored as pending
        // values and applied to the Box after creation so they participate
        // in replaced-element sizing without poisoning HasExplicitDim in
        // the positioning pass.
        //
        // Only applies to `<img>`. Other replaced elements (audio/video)
        // aren't supported in v1.
        void MaybeApplyImgIntrinsicSize(Element e, ComputedStyle style) {
            pendingIntrinsicWidth = 0;
            pendingIntrinsicHeight = 0;
            if (e == null || style == null) return;
            if (e.TagName != "img") return;

            var widthParsed = style.GetParsed(CssProperties.WidthId);
            var heightParsed = style.GetParsed(CssProperties.HeightId);
            bool widthAuto = IsAutoOrMissing(widthParsed);
            bool heightAuto = IsAutoOrMissing(heightParsed);
            if (!widthAuto && !heightAuto) return;

            // HTML width/height attrs: per the HTML Living Standard, these
            // are unitless integers interpreted as CSS pixels.
            if (widthAuto) {
                string attrW = e.GetAttribute("width");
                if (!string.IsNullOrEmpty(attrW) && int.TryParse(attrW, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int wpx) && wpx >= 0) {
                    style.Set("width", wpx + "px");
                    widthAuto = false;
                }
            }
            if (heightAuto) {
                string attrH = e.GetAttribute("height");
                if (!string.IsNullOrEmpty(attrH) && int.TryParse(attrH, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int hpx) && hpx >= 0) {
                    style.Set("height", hpx + "px");
                    heightAuto = false;
                }
            }
            if (!widthAuto && !heightAuto) return;

            if (imageRegistry == null) return;
            string src = e.GetAttribute("src");
            if (string.IsNullOrEmpty(src)) return;
            if (!imageRegistry.TryResolve(src, out var source) || source == null) return;

            bool pinned = HasAllInsets(style);
            // When the img is `position: absolute|fixed` with all four
            // insets pinned, CSS Positioned Layout L3 §10.3.7 says width
            // auto stretches to (containing-block + |insetLeft| +
            // |insetRight|). That stretch must win over the natural
            // image size — authors who wrote `inset: -2px` did so to
            // overlap a parent border, and silently sizing the box to
            // the source PNG's pixel dimensions defeats the intent.
            // Skip BOTH the CSS-width override AND the IntrinsicWidth
            // stamp: the latter would otherwise make
            // PositioningPass.HasExplicitDim return true and skip the
            // stretch branch, leaving the box at ApplyBoxModel's
            // fallback `avail` (= containing-block's content width
            // only, NOT extended past insets).
            if (widthAuto && source.Width > 0 && !pinned) {
                style.Set("width", source.Width + "px");
                pendingIntrinsicWidth = source.Width;
            }
            if (heightAuto && source.Height > 0 && !pinned) {
                style.Set("height", source.Height + "px");
                pendingIntrinsicHeight = source.Height;
            }
        }

        static bool HasAllInsets(ComputedStyle style) {
            if (style == null) return false;
            string pos = style.Get(CssProperties.PositionId);
            if (pos != "absolute" && pos != "fixed") return false;
            string t = style.Get(CssProperties.TopId);
            string r = style.Get(CssProperties.RightId);
            string b = style.Get(CssProperties.BottomId);
            string l = style.Get(CssProperties.LeftId);
            return !string.IsNullOrEmpty(t) && t != "auto"
                && !string.IsNullOrEmpty(r) && r != "auto"
                && !string.IsNullOrEmpty(b) && b != "auto"
                && !string.IsNullOrEmpty(l) && l != "auto";
        }

        void ApplyPendingIntrinsicSize(Box box) {
            if (pendingIntrinsicWidth > 0) box.IntrinsicWidth = pendingIntrinsicWidth;
            if (pendingIntrinsicHeight > 0) box.IntrinsicHeight = pendingIntrinsicHeight;
            pendingIntrinsicWidth = 0;
            pendingIntrinsicHeight = 0;
        }

        public Box Build(Element root, ComputedStyle rootStyle) {
            string display = StyleResolver.Display(rootStyle);
            if (display == "none") return null;

            // PA6: clear precomputed `<li>` ordinals at the start of each
            // build so survivor entries from the previous build don't pin
            // their keyed Elements past their lifetime.
            liOrdinals.Clear();

            BlockBox rootBox;
            if ((display == "block" || display == "flow-root" || display == "list-item")
                && IsMulticolContainer(rootStyle)) {
                rootBox = pool.AllocateMulticolBox();
            } else {
                rootBox = NewBlockBoxFor(display);
            }
            rootBox.Element = root;
            rootBox.Style = rootStyle;
            BuildChildren(root, rootStyle, rootBox);
            // CSS 2.1 §17.2.1 / CSS Tables L3 §3.7 anonymous-table-object
            // insertion (tracker I1). Mirrors BuildDocument so single-element
            // entry points produce the same table-internal box tree.
            Weva.Layout.Tables.AnonymousTableInsertionPass.Run(rootBox, pool);
            return rootBox;
        }

        public Box BuildDocument(Document doc) {
            // PA6: clear precomputed `<li>` ordinals at the start of each
            // BuildDocument call. See `liOrdinals` field comment.
            liOrdinals.Clear();

            // CSS Backgrounds 3 §2.11.2 — when the root element (`<html>`) has no
            // background-color and no background-image, the canvas takes its
            // background from the body. We propagate the body's background up
            // to html *before* descending: building order doesn't matter since
            // we read styles via `styleOf` directly, and writing into the html
            // ComputedStyle ahead of `AppendNodeAsBlockChild` ensures every
            // downstream consumer (BackgroundResolver in paint, debug/devtools)
            // sees the propagated values uniformly.
            PropagateBodyBackgroundToHtml(doc);

            var root = pool.AllocateBlockBox();
            root.Element = null;
            root.Style = null;
            foreach (var child in doc.Children) {
                AppendNodeAsBlockChild(child, null, root);
            }
            FinalizeBlockChildren(root);
            // CSS 2.1 §17.2.1 / CSS Tables L3 §3.7 anonymous-table-object
            // insertion (tracker I1). Runs after the regular build so the
            // pass sees the final tree shape with ::before/::after generated
            // boxes and pseudo-element wrappers already in place. Walks
            // depth-first wrapping bare `display: table-*` boxes whose
            // ancestors don't satisfy the table containment requirements.
            Weva.Layout.Tables.AnonymousTableInsertionPass.Run(root, pool);
            return root;
        }

        // Implements CSS Backgrounds 3 §2.11.2 propagation: if html has no
        // background (color is initial/transparent, image is initial/none) AND
        // body has a non-default one, copy body's `background-color` and
        // `background-image` onto html. We treat empty/null/"transparent" as
        // "no color" and empty/null/"none" as "no image", matching the
        // initial-value strings used by CssProperties (see
        // CssProperties.cs:318 background-color initial = "transparent").
        void PropagateBodyBackgroundToHtml(Document doc) {
            Element html = null;
            foreach (var c in doc.Children) {
                if (c is Element e && e.TagName == "html") { html = e; break; }
            }
            if (html == null) return;
            Element body = null;
            foreach (var c in html.Children) {
                if (c is Element e && e.TagName == "body") { body = e; break; }
            }
            if (body == null) return;
            var htmlStyle = styleOf(html);
            var bodyStyle = styleOf(body);
            if (htmlStyle == null || bodyStyle == null) return;
            if (!IsBackgroundEmpty(htmlStyle)) return;

            string bodyColor = bodyStyle.Get(CssProperties.BackgroundColorId);
            string bodyImage = bodyStyle.Get(CssProperties.BackgroundImageId);
            bool bodyHasColor = !string.IsNullOrEmpty(bodyColor)
                && !string.Equals(bodyColor, "transparent", System.StringComparison.OrdinalIgnoreCase);
            bool bodyHasImage = !string.IsNullOrEmpty(bodyImage)
                && !string.Equals(bodyImage, "none", System.StringComparison.OrdinalIgnoreCase);
            if (!bodyHasColor && !bodyHasImage) return;
            if (bodyHasColor) htmlStyle.Set("background-color", bodyColor);
            if (bodyHasImage) htmlStyle.Set("background-image", bodyImage);
        }

        static bool IsBackgroundEmpty(ComputedStyle style) {
            string color = style.Get(CssProperties.BackgroundColorId);
            string image = style.Get(CssProperties.BackgroundImageId);
            bool noColor = string.IsNullOrEmpty(color)
                || string.Equals(color, "transparent", System.StringComparison.OrdinalIgnoreCase);
            bool noImage = string.IsNullOrEmpty(image)
                || string.Equals(image, "none", System.StringComparison.OrdinalIgnoreCase);
            return noColor && noImage;
        }

        void AppendNodeAsBlockChild(Node node, ComputedStyle parentStyle, BlockBox parent) {
            // Flexbox / Grid blockification: per CSS Flexbox §4 and Grid §6,
            // each in-flow child of a flex/grid container becomes a flex item,
            // and inline-displayed children get "blockified" — promoted to
            // block-level for layout purposes. Without this an inline span
            // child of a flex row would silently be dropped (FlexLayout only
            // collects BlockBox children).
            bool blockifyInlines = parent is FlexBox || parent is GridBox;
            if (node is Element e) {
                var style = styleOf(e);
                string disp = StyleResolver.Display(style);
                if (disp == "none") return;
                // CSS 2.1 §9.7: a floated element with an inline outer
                // display is "blockified" — its outer display becomes
                // block. Authors routinely write `<span style="float:left">`
                // / `<img style="float:left">` and expect block-flow
                // semantics on the float; without this, the box would
                // become an InlineBox and BlockLayout would never see it
                // as a float. Note: floats inside flex/grid containers
                // are excluded — those containers' items can't float
                // (the float property is ignored on flex/grid items per
                // CSS Flexbox §3 / Grid §6.4); we honour that by NOT
                // re-promoting inline content when the parent is flex/grid.
                if (!blockifyInlines && (disp == "inline" || string.IsNullOrEmpty(disp))) {
                    string pos = KeywordName(style?.GetParsed(CssProperties.PositionId));
                    if (pos == "absolute" || pos == "fixed") {
                        disp = "block";
                    } else {
                        string flt = KeywordName(style?.GetParsed(CssProperties.FloatId));
                        if (!string.IsNullOrEmpty(flt) && flt != "none") {
                            disp = "block";
                        }
                    }
                }
                MaybeApplyImgIntrinsicSize(e, style);
                MaybeApplyFieldSizingWidth(e, style);
                MaybeInjectBackdrop(e, parent);
                if (disp == "block" || disp == "flex" || disp == "grid" || disp == "flow-root" || disp == "list-item") {
                    // Multicol: a plain block container whose column-count or column-width
                    // is non-auto becomes a MulticolBox.  Flex/grid containers ignore
                    // column properties per CSS Multicol §2.
                    BlockBox bb;
                    if ((disp == "block" || disp == "flow-root" || disp == "list-item")
                        && IsMulticolContainer(style)) {
                        bb = pool.AllocateMulticolBox();
                    } else {
                        bb = NewBlockBoxFor(disp);
                    }
                    bb.Element = e; bb.Style = style;
                    ApplyPendingIntrinsicSize(bb);
                    BuildChildren(e, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                if (disp == "inline-block" || disp == "inline-flex" || disp == "inline-grid" || disp == "inline-table") {
                    // CSS Flexbox §4 / Grid §6 blockification: an in-flow
                    // child of a flex/grid container has its OUTER display
                    // forced to block. inline-flex → flex (block outer),
                    // inline-block → block, inline-grid → grid, inline-table →
                    // table. Without this, BoxFinalize.IsBlockLevel sees the
                    // IsInlineBlock flag and lumps the items together into
                    // a single anonymous wrapper, so e.g. `<footer class=
                    // "composer" style="display:flex"> <button>📎</button>
                    // <input type="text" style="flex:1"> ... </footer>`
                    // collapsed all four real children plus the inter-element
                    // whitespace text nodes into one flex item — the input's
                    // `flex:1` was then never honoured because flex sizing
                    // didn't see the input at all.
                    string blockDisp = disp;
                    if (blockifyInlines) {
                        if (disp == "inline-block") blockDisp = "block";
                        else if (disp == "inline-flex") blockDisp = "flex";
                        else if (disp == "inline-grid") blockDisp = "grid";
                        else if (disp == "inline-table") blockDisp = "table";
                    }
                    var bb = NewBlockBoxFor(blockDisp);
                    bb.Element = e; bb.Style = style;
                    ApplyPendingIntrinsicSize(bb);
                    BuildChildren(e, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                if (IsTableDisplay(disp)) {
                    var bb = NewBlockBoxFor(disp);
                    bb.Element = e; bb.Style = style;
                    ApplyPendingIntrinsicSize(bb);
                    BuildChildren(e, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                if (disp == "contents") {
                    foreach (var c in e.Children) AppendNodeAsBlockChild(c, style, parent);
                    return;
                }
                if (blockifyInlines) {
                    // `display: inline` (or unset on a span) inside a flex/grid
                    // container becomes a block-level flex/grid item.
                    var bb = pool.AllocateBlockBox();
                    bb.Element = e; bb.Style = style;
                    ApplyPendingIntrinsicSize(bb);
                    BuildChildren(e, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                var ib = pool.AllocateInlineBox();
                ib.Element = e; ib.Style = style;
                ApplyPendingIntrinsicSize(ib);
                BuildInlineChildren(e, style, ib);
                parent.AddChild(ib);
                return;
            }
            if (node is TextNode tn) {
                var run = pool.AllocateTextRun();
                run.Text = tn.Data ?? "";
                run.Style = parentStyle;
                run.Element = parent.Element;
                run.SourceNode = tn;
                parent.AddChild(run);
                return;
            }
        }

        void BuildChildren(Element element, ComputedStyle style, BlockBox parent) {
            // ::before runs first so the generated box appears at index 0 of
            // the originating element's children, per CSS 2.1 §12.1.
            MaybeInjectPseudoElement(element, parent, BeforeStyleOf);
            // List-item markers (CSS Lists 3 §3) — `<li>` inside `<ul>`/`<ol>`
            // gets a `disc` / decimal-index marker at the start of its
            // children. The marker's style inherits from the li so font /
            // color flow through. v1 simplification: the marker is an
            // inline-block at the start of the li's content area rather
            // than positioned in the negative-margin region.
            MaybeInjectListMarker(element, style, parent);
            // PA6: precompute ordinals for every `<li>` child of this list
            // parent in one O(siblings) pass before recursing into them.
            // Each `<li>`'s MaybeInjectListMarker will then read its slot
            // out of `liOrdinals` instead of re-walking the parent's
            // children itself. See PrecomputeLiOrdinals.
            if (element != null && (element.TagName == "ul" || element.TagName == "ol")) {
                PrecomputeLiOrdinals(element);
            }
            foreach (var c in element.Children) {
                AppendNodeAsBlockChild(c, style, parent);
            }
            // ::after appended last so the generated box is the final child.
            MaybeInjectPseudoElement(element, parent, AfterStyleOf);
            FinalizeBlockChildren(parent);
        }

        // CSS Lists 3 §3 / HTML §4.4.5-6 — populate `liOrdinals` with the
        // 1-based marker counter value for every `<li>` child of the given
        // `<ul>` or `<ol>` parent in a single pass.
        //
        //   - `<ol start="N">` seeds the counter at N (default 1).
        //   - `<ol reversed>` counts down by 1 per `<li>` instead of up.
        //   - `<li value="V">` resets the counter to V at that item; the
        //     counter continues from V+1 (or V-1 when reversed) for the
        //     following `<li>` siblings.
        //
        // `<ul>` ignores `start` / `reversed` / `value` (those attributes
        // are defined on `<ol>` in HTML), but we still pre-seed unitary
        // 1..N counts so unordered lists with text-counter `list-style-type`
        // (decimal etc.) at the `<li>` level still get a stable counter.
        void PrecomputeLiOrdinals(Element listParent) {
            int counter = 1;
            int step = 1;
            bool isOl = listParent.TagName == "ol";
            if (isOl) {
                if (TryParseIntAttr(listParent.GetAttribute("start"), out int start)) counter = start;
                bool reversed = listParent.GetAttribute("reversed") != null;
                if (reversed) {
                    step = -1;
                    // If no explicit `start`, HTML says the initial counter
                    // for a reversed list is the count of `<li>` children.
                    if (!HasAttr(listParent, "start")) {
                        int liCount = 0;
                        foreach (var c in listParent.Children) {
                            if (c is Element ce && ce.TagName == "li") liCount++;
                        }
                        counter = liCount;
                    }
                }
            }
            foreach (var c in listParent.Children) {
                if (c is Element ce && ce.TagName == "li") {
                    if (isOl && TryParseIntAttr(ce.GetAttribute("value"), out int v)) {
                        counter = v;
                    }
                    liOrdinals[ce] = counter;
                    counter += step;
                }
            }
        }

        static bool HasAttr(Element e, string name) {
            return e.Attributes != null && e.Attributes.Contains(name);
        }

        static bool TryParseIntAttr(string s, out int value) {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // CSS Lists 3 §3 list-item marker injection. Triggered for any element
        // whose computed `display` is `list-item` (CSS Lists L3 §2 — the
        // property governs marker generation regardless of tag name). The
        // existing `<li>` path is preserved because the UA stylesheet already
        // sets `display: list-item` on `<li>` elements.
        //
        // For text-based types the marker glyph is resolved via
        // `ListMarkerStyle.MarkerText` (disc / circle / square / decimal /
        // decimal-leading-zero / lower-/upper-roman / lower-/upper-alpha aka
        // latin). When `list-style-image` is set to a non-`none` value it
        // REPLACES the text glyph entirely (spec §3.3) — the marker box gets
        // a `background-image` style and no TextRun child.
        // `list-style-position: inside` and `outside` both render in-flow as
        // the first inline-block child in v1 (no negative-margin outside pass).
        void MaybeInjectListMarker(Element element, ComputedStyle style, BlockBox parent) {
            if (element == null || style == null) return;
            // CSS Display L3 §2 / CSS Lists L3 §2: any element with
            // display: list-item generates a marker box.
            string disp = StyleResolver.Display(style);
            if (disp != "list-item") return;
            string lst = style.Get(CssProperties.ListStyleTypeId);
            if (lst == "none") {
                // CSS Lists 3 §3.4: even with type=none, a non-none image
                // produces a marker. Check image before bailing.
                string img0 = ListMarkerStyle.ImageOrNull(style.Get(CssProperties.ListStyleImageId));
                if (img0 == null) return;
                BuildListMarkerBox(element, parent, style, null, img0);
                return;
            }
            // Image wins over text per spec §3.3 — the URL replaces the glyph.
            string img = ListMarkerStyle.ImageOrNull(style.Get(CssProperties.ListStyleImageId));
            if (img != null) {
                BuildListMarkerBox(element, parent, style, null, img);
                return;
            }
            // PA6 fast path: BuildChildren seeds liOrdinals for every `<li>`
            // child of a `<ul>`/`<ol>` parent in one pass. Reading the
            // precomputed value turns a per-li O(siblings) walk into a
            // single dictionary lookup, collapsing total list-marker cost
            // from O(N^2) to O(N).
            int ordinal;
            if (!liOrdinals.TryGetValue(element, out ordinal)) {
                // Fallback: one-shot Build paths (tests / external callers
                // invoking Build on an element below a `<ul>`/`<ol>`) OR
                // non-`<li>` elements with `display: list-item` never ran
                // the parent's BuildChildren with PrecomputeLiOrdinals, so
                // the precomputed slot is missing. Walk the parent the old
                // way to stay correct. For non-`<li>` display:list-item
                // siblings we query their computed style to determine if
                // they also participate in the counter.
                ListMarkerOrdinalWalks++;
                ordinal = 1;
                var p = element.Parent as Element;
                if (p != null) {
                    bool isLiElement = element.TagName == "li";
                    foreach (var sib in p.Children) {
                        if (sib is Element se) {
                            if (ReferenceEquals(se, element)) break;
                            // Count this sibling if it participates in the
                            // list-item counter — either it's also an <li>
                            // (matched by tag for the classic path) OR it has
                            // display: list-item in its computed style.
                            bool sibIsLi = se.TagName == "li";
                            bool sibIsListItem = !sibIsLi && styleOf != null
                                && StyleResolver.Display(styleOf(se)) == "list-item";
                            if (sibIsLi || sibIsListItem) ordinal++;
                        }
                    }
                }
            }
            string text = ListMarkerStyle.MarkerText(lst, ordinal);
            BuildListMarkerBox(element, parent, style, text, null);
        }

        void BuildListMarkerBox(Element host, Box parent, ComputedStyle liStyle, string text, string image) {
            // Inline-block atom so the marker reserves a fixed slot at the
            // start of the li's flow without participating in inline wrapping.
            // Element is left null — the marker has no DOM identity, mirroring
            // how ::before / ::after generated boxes are handled.
            var markerStyle = MarkerStyleOf?.Invoke(host) ?? liStyle;
            var bb = pool.AllocateBlockBox();
            bb.Element = null;
            bb.Style = markerStyle;
            bb.IsInlineBlock = true;
            // Image markers store the resolved url(...) on the BlockBox itself
            // (BlockBox.ListMarkerImage) rather than mutating the li's shared
            // ComputedStyle. The paint pass picks it up and draws the bitmap
            // in place of a text glyph. No TextRun is appended in that case.
            if (image != null) {
                bb.ListMarkerImage = image;
            } else if (text != null) {
                var run = pool.AllocateTextRun();
                run.Text = text;
                run.Style = markerStyle;
                run.Element = null;
                run.SourceNode = null;
                bb.AddChild(run);
            }
            parent.AddChild(bb);
        }

        void BuildInlineChildren(Element element, ComputedStyle style, InlineBox parent) {
            MaybeInjectPseudoElement(element, parent, BeforeStyleOf);
            foreach (var c in element.Children) {
                AppendInlineChild(c, style, parent);
            }
            MaybeInjectPseudoElement(element, parent, AfterStyleOf);
        }

        void AppendInlineChild(Node node, ComputedStyle parentStyle, InlineBox parent) {
            if (node is Element e) {
                var style = styleOf(e);
                string disp = StyleResolver.Display(style);
                if (disp == "none") return;
                if (disp == "contents") {
                    foreach (var c in e.Children) AppendInlineChild(c, style, parent);
                    return;
                }
                // CSS 2.1 §9.7: a floated element with an inline outer
                // display is "blockified". See AppendNodeAsBlockChild
                // for the rationale; here we also promote inline floats
                // nested inside an InlineBox to block so they participate
                // in float layout instead of being collapsed into the
                // inline-flow stream.
                if (disp == "inline" || string.IsNullOrEmpty(disp)) {
                    string pos = KeywordName(style?.GetParsed(CssProperties.PositionId));
                    if (pos == "absolute" || pos == "fixed") {
                        disp = "block";
                    } else {
                        string flt = KeywordName(style?.GetParsed(CssProperties.FloatId));
                        if (!string.IsNullOrEmpty(flt) && flt != "none") {
                            disp = "block";
                        }
                    }
                }
                MaybeApplyImgIntrinsicSize(e, style);
                MaybeApplyFieldSizingWidth(e, style);
                MaybeInjectBackdrop(e, parent);
                if (disp == "inline-block" || disp == "block" || disp == "flex" || disp == "grid" || disp == "inline-flex" || disp == "inline-grid" || disp == "inline-table" || disp == "list-item" || IsTableDisplay(disp)) {
                    var bb = NewBlockBoxFor(disp);
                    bb.Element = e; bb.Style = style;
                    ApplyPendingIntrinsicSize(bb);
                    BuildChildren(e, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                var ib = pool.AllocateInlineBox();
                ib.Element = e; ib.Style = style;
                ApplyPendingIntrinsicSize(ib);
                BuildInlineChildren(e, style, ib);
                parent.AddChild(ib);
                return;
            }
            if (node is TextNode tn) {
                var run = pool.AllocateTextRun();
                run.Text = tn.Data ?? "";
                run.Style = parentStyle;
                run.Element = parent.Element;
                run.SourceNode = tn;
                parent.AddChild(run);
                return;
            }
        }

        void FinalizeBlockChildren(BlockBox parent) {
            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);
        }

        // Decodes the keyword name of a parsed CssValue for keyword-typed
        // properties (display, position, float, ...). CssKeyword surfaces the
        // canonical lowercase identifier; CssIdentifier surfaces a raw name.
        // Returns null when the slot is unset or the parse tree is not a
        // keyword/identifier — callers treat that as "initial value".
        static string KeywordName(CssValue parsed) {
            if (parsed is CssKeyword k) return k.Identifier;
            if (parsed is CssIdentifier id) return id.Name;
            return null;
        }

        // Matches "auto" (typed CssKeyword/CssIdentifier or absent slot) the
        // way `MaybeApplyImgIntrinsicSize` needs it: both an unset width and a
        // literal `auto` keyword mean "fall through to intrinsic-size pickup".
        static bool IsAutoOrMissing(CssValue parsed) {
            if (parsed == null) return true;
            if (parsed is CssKeyword k) return k.Identifier == "auto";
            if (parsed is CssIdentifier id) return id.Name == "auto";
            return false;
        }
    }
}
