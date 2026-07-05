using System;
using System.Collections.Generic;
using Weva.Compiled;
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

namespace Weva.Layout {
    // Snapshot-driven mirror of BoxBuilder. Walks DomSnapshot's FirstChild /
    // NextSibling integer arrays directly instead of dereferencing the managed
    // Element tree. Per-element ComputedStyle is looked up via styleOf(int) so
    // callers can pre-build a NodeId-indexed ComputedStyle[] for O(1) hits.
    //
    // Output is the same Box tree shape as BoxBuilder.BuildDocument: an outer
    // anonymous BlockBox wrapping the document's top-level children. Layout
    // passes (BlockLayout, FlexLayout, etc.) consume the result identically.
    //
    // The managed-tree fields on each Box (Element, TextRun.SourceNode) are
    // rehydrated from snapshot.ManagedNodes[nodeId] so downstream consumers
    // (paint converter, hit testing, ElementToBoxIndex, BindingScanner)
    // continue to see the same Element / TextNode references they always did.
    internal sealed class SnapshotBoxBuilder {
        readonly Func<int, ComputedStyle> styleOf;
        readonly BoxPool pool;
        readonly LayoutScratch scratch;
        // Optional resolver for `::backdrop` styles — settable so the pooled
        // SnapshotBoxBuilder instance owned by LayoutEngine can pick up the
        // current engine-level BackdropStyleOf without requiring a new
        // builder per Layout call.
        public Func<Element, ComputedStyle> BackdropStyleOf { get; set; }
        // Resolvers for `::before` / `::after` pseudo-element styles. When
        // set, the snapshot walk synthesizes anonymous child boxes at the
        // appropriate positions. Mirrors BoxBuilder.BeforeStyleOf /
        // AfterStyleOf.
        public Func<Element, ComputedStyle> BeforeStyleOf { get; set; }
        public Func<Element, ComputedStyle> AfterStyleOf { get; set; }
        public Func<Element, ComputedStyle> MarkerStyleOf { get; set; }
        // Element-keyed style resolver — needed by CounterContext.BuildFor
        // which walks the Element tree from `target.Parent` upward calling
        // styleOf(Element) at each step. The snapshot path's primary
        // styleOf is int-keyed (faster, no Element→NodeId lookup); the
        // managed BoxBuilder uses an Element-keyed delegate directly.
        // Set by LayoutEngine.BuildBoxTreeFromSnapshot so counter() in
        // ::before / ::after content resolves correctly. When null, counter
        // resolution silently fails (returns "") and the pseudo paints
        // with no text — GAP-1 from the advanced-dashboard audit.
        public Func<Element, ComputedStyle> ElementStyleOf { get; set; }

        // CSS UI L4 §13 field-sizing: content — font metrics for value-text width
        // measurement. Settable by LayoutEngine after creating the pooled builder.
        // When null, falls back to BoxBuilder.StubCharWidthPx stub measurement.
        public IFontMetrics FieldSizingMetrics { get; set; }

        // PA6 fix — precomputed `<li>` ordinals indexed by the `<li>` Element.
        // BuildChildren seeds this dict in one O(siblings) pass when entering
        // a `<ul>`/`<ol>` parent (honouring `start`/`reversed`/`value`); each
        // `<li>`'s MaybeInjectListMarker then reads the entry instead of
        // re-walking the parent's children. Mirrors the same field on
        // BoxBuilder so both build paths share the same algorithmic shape.
        readonly Dictionary<Element, int> liOrdinals = new();

        // Diagnostic — incremented by MaybeInjectListMarker's fallback when
        // the precomputed slot is missing. Pinned to 0 by the PA6 regression
        // test that builds a 1000-item list through the snapshot path.
        internal int ListMarkerOrdinalWalks;

        public SnapshotBoxBuilder(Func<int, ComputedStyle> styleOf, BoxPool pool, LayoutScratch scratch) {
            this.styleOf = styleOf;
            this.pool = pool;
            this.scratch = scratch;
        }

        public SnapshotBoxBuilder(Func<int, ComputedStyle> styleOf)
            : this(styleOf, new BoxPool(), new LayoutScratch()) {
            this.pool.BeginPass();
        }

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

        static bool IsTableDisplay(string disp) {
            return disp == "table" || disp == "inline-table"
                || disp == "table-row-group" || disp == "table-header-group" || disp == "table-footer-group"
                || disp == "table-row" || disp == "table-cell" || disp == "table-caption"
                || disp == "table-column" || disp == "table-column-group";
        }

        // Mirrors BoxBuilder.IsMulticolContainer: a block container is a multicol
        // container when column-count or column-width is set to a non-auto value.
        static bool IsMulticolContainer(ComputedStyle style) {
            if (style == null) return false;
            string cc = style.Get(CssProperties.ColumnCountId);
            if (!string.IsNullOrEmpty(cc) && cc != "auto") return true;
            string cw = style.Get(CssProperties.ColumnWidthId);
            if (!string.IsNullOrEmpty(cw) && cw != "auto") return true;
            return false;
        }

        // Mirrors BoxBuilder.MaybeInjectBackdrop: synthesize a `::backdrop`
        // sibling box before a top-layer host (open modal dialog or open
        // popover). Backdrop's own style comes from the cascade-supplied
        // resolver; both backdrop and host are position:fixed by UA so
        // neither contributes to in-flow layout.
        void MaybeInjectBackdrop(Element host, Box parent) {
            if (BackdropStyleOf == null) return;
            if (!TopLayer.IsHost(host)) return;
            var backdropStyle = BackdropStyleOf(host);
            if (backdropStyle == null) return;
            var bb = pool.AllocateBlockBox();
            bb.Element = null;
            bb.Style = backdropStyle;
            parent.AddChild(bb);
        }

        // Mirror of BoxBuilder.MaybeInjectPseudoElement — the snapshot walk
        // injects ::before / ::after generated boxes at the same positions
        // as the managed walk so paint and layout produce identical trees
        // regardless of which builder ran.
        void MaybeInjectPseudoElement(Element host, Box parent, Func<Element, ComputedStyle> resolver) {
            if (resolver == null || host == null) return;
            var pseudoStyle = resolver(host);
            if (pseudoStyle == null) return;
            string contentRaw = pseudoStyle.Get("content");
            // Build a CounterContext so counter() / counters() in `content`
            // resolve against counter-reset / counter-increment / counter-set
            // walked from the document root. Mirrors BoxBuilder's wiring.
            // Without ElementStyleOf the snapshot path drops counter() to ""
            // (GAP-1 from the advanced-dashboard audit).
            bool isAfter = ReferenceEquals(resolver, AfterStyleOf);
            var counterCtx = ElementStyleOf != null
                ? Weva.Css.Cascade.CounterContext.BuildFor(host, ElementStyleOf, BeforeStyleOf, AfterStyleOf, isAfter)
                : null;
            string quotesValue = pseudoStyle.Get("quotes");
            string text = Weva.Css.Cascade.CascadeEngine.ResolveContentString(contentRaw, host, counterCtx, quotesValue);
            if (text == null) return;
            string disp = StyleResolver.Display(pseudoStyle);
            if (disp == "none") return;
            BuildPseudoBox(host, parent, pseudoStyle, disp, text);
        }

        void BuildPseudoBox(Element host, Box parent, ComputedStyle pseudoStyle, string disp, string text) {
            // CSS 2.1 §9.7 blockification — see BoxBuilder.BuildPseudoBox for
            // the rationale. Mirrored here so the snapshot path stays in
            // step with the live build path.
            if (string.IsNullOrEmpty(disp) || disp == "inline") {
                // Per-style parsed cache: keyword-typed properties read via
                // direct pattern match on the cached CssValue.
                string pos = KeywordName(pseudoStyle.GetParsed(CssProperties.PositionId));
                if (pos == "absolute" || pos == "fixed") {
                    disp = "block";
                } else {
                    string flt = KeywordName(pseudoStyle.GetParsed(CssProperties.FloatId));
                    if (!string.IsNullOrEmpty(flt) && flt != "none") disp = "block";
                }
            }
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

        public Box BuildFromSnapshot(DomSnapshot snap) {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            // PA6: clear precomputed `<li>` ordinals at the start of each
            // BuildFromSnapshot call. Entries are keyed by Element reference;
            // clearing prevents survivor entries from pinning stale Elements
            // past their lifetime across snapshot rebuilds.
            liOrdinals.Clear();
            // Mirror BoxBuilder.PropagateBodyBackgroundToHtml: per CSS
            // Backgrounds 3 §2.11.2, when html has no background and body
            // does, the body's background propagates to the canvas. We
            // implement by copying onto the html ComputedStyle so paint
            // resolves uniformly regardless of which builder ran.
            PropagateBodyBackgroundToHtml(snap);

            var root = pool.AllocateBlockBox();
            root.Element = null;
            root.Style = null;
            int rootId = snap.RootId;
            int childId = snap.FirstChild[rootId];
            while (childId >= 0) {
                AppendNodeAsBlockChild(snap, childId, null, root);
                childId = snap.NextSibling[childId];
            }
            BoxFinalize.FinalizeBlockChildren(root, pool, scratch);
            // CSS 2.1 §17.2.1 / CSS Tables L3 §3.7 anonymous-table-object
            // insertion (tracker I1). Mirrors BoxBuilder.BuildDocument so
            // both build paths produce identical table-internal box trees.
            Weva.Layout.Tables.AnonymousTableInsertionPass.Run(root, pool);
            return root;
        }

        void PropagateBodyBackgroundToHtml(DomSnapshot snap) {
            int rootId = snap.RootId;
            int htmlId = -1;
            for (int c = snap.FirstChild[rootId]; c >= 0; c = snap.NextSibling[c]) {
                if (snap.Kinds[c] == NodeKind.Element
                    && snap.ManagedNodes[c] is Element e && e.TagName == "html") {
                    htmlId = c; break;
                }
            }
            if (htmlId < 0) return;
            int bodyId = -1;
            for (int c = snap.FirstChild[htmlId]; c >= 0; c = snap.NextSibling[c]) {
                if (snap.Kinds[c] == NodeKind.Element
                    && snap.ManagedNodes[c] is Element e && e.TagName == "body") {
                    bodyId = c; break;
                }
            }
            if (bodyId < 0) return;
            var htmlStyle = styleOf(htmlId);
            var bodyStyle = styleOf(bodyId);
            if (htmlStyle == null || bodyStyle == null) return;
            string htmlColor = htmlStyle.Get("background-color");
            string htmlImage = htmlStyle.Get("background-image");
            bool htmlEmpty =
                (string.IsNullOrEmpty(htmlColor) || string.Equals(htmlColor, "transparent", System.StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(htmlImage) || string.Equals(htmlImage, "none", System.StringComparison.OrdinalIgnoreCase));
            if (!htmlEmpty) return;
            string bodyColor = bodyStyle.Get("background-color");
            string bodyImage = bodyStyle.Get("background-image");
            bool bodyHasColor = !string.IsNullOrEmpty(bodyColor)
                && !string.Equals(bodyColor, "transparent", System.StringComparison.OrdinalIgnoreCase);
            bool bodyHasImage = !string.IsNullOrEmpty(bodyImage)
                && !string.Equals(bodyImage, "none", System.StringComparison.OrdinalIgnoreCase);
            if (!bodyHasColor && !bodyHasImage) return;
            if (bodyHasColor) htmlStyle.Set("background-color", bodyColor);
            if (bodyHasImage) htmlStyle.Set("background-image", bodyImage);
        }

        void AppendNodeAsBlockChild(DomSnapshot snap, int nodeId, ComputedStyle parentStyle, BlockBox parent) {
            // Mirrors BoxBuilder.AppendNodeAsBlockChild's flex/grid blockification
            // (CSS Flexbox §4 / Grid §6): in-flow inline children of a flex/grid
            // container are promoted to block-level for layout. Without this an
            // inline span child of a flex row would slip into the inline branch
            // below and FlexLayout (which collects only BlockBox children) would
            // skip it entirely — leaving its align-items: baseline broken because
            // the items participate as anonymous inline content instead of as
            // proper flex items.
            bool blockifyInlines = parent is FlexBox || parent is GridBox;
            var kind = snap.Kinds[nodeId];
            if (kind == NodeKind.Element) {
                var managed = snap.ManagedNodes[nodeId] as Element;
                var style = styleOf(nodeId);
                string disp = StyleResolver.Display(style);
                if (disp == "none") return;
                // CSS 2.1 §9.7: a floated OR absolutely-positioned element with
                // an inline outer display is blockified — mirrors BoxBuilder.
                // AppendNodeAsBlockChild. Without the abs/fixed promotion, an
                // empty `<span style="position:absolute">` becomes an InlineBox
                // with no glyphs; InlineLayout's CollectInline produces zero
                // items, hits the empty-container branch (one empty LineBox),
                // and never re-attaches the pending inline. The box is lost
                // entirely and PositioningPass has no Box to reposition.
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
                MaybeApplyFieldSizingWidth(managed, style);
                MaybeInjectBackdrop(managed, parent);
                if (disp == "block" || disp == "flex" || disp == "grid" || disp == "flow-root"
                    || disp == "list-item"
                    || disp == "inline-block" || disp == "inline-flex" || disp == "inline-grid"
                    || disp == "inline-table" || IsTableDisplay(disp)) {
                    // CSS Flexbox §4 / Grid §6 outer-display blockification:
                    // an in-flow inline-* child of a flex/grid container has
                    // its outer display forced to block, so it participates
                    // as a proper flex/grid item rather than being treated as
                    // inline content by BoxFinalize and lumped into an
                    // anonymous wrapper. Mirrors BoxBuilder.
                    string boxDisp = disp;
                    if (blockifyInlines) {
                        if (disp == "inline-block") boxDisp = "block";
                        else if (disp == "inline-flex") boxDisp = "flex";
                        else if (disp == "inline-grid") boxDisp = "grid";
                        else if (disp == "inline-table") boxDisp = "table";
                    }
                    // Multicol: a plain block container whose column-count or column-width
                    // is non-auto becomes a MulticolBox.  Flex/grid containers ignore
                    // column properties per CSS Multicol §2.
                    BlockBox bb;
                    if ((boxDisp == "block" || boxDisp == "flow-root" || boxDisp == "list-item")
                        && IsMulticolContainer(style)) {
                        bb = pool.AllocateMulticolBox();
                    } else {
                        bb = NewBlockBoxFor(boxDisp);
                    }
                    bb.Element = managed; bb.Style = style;
                    BuildChildren(snap, nodeId, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                if (disp == "contents") {
                    int c = snap.FirstChild[nodeId];
                    while (c >= 0) {
                        AppendNodeAsBlockChild(snap, c, style, parent);
                        c = snap.NextSibling[c];
                    }
                    return;
                }
                if (blockifyInlines) {
                    // `display: inline` (or unset on a span) inside a flex/grid
                    // container becomes a block-level flex/grid item.
                    var bb = pool.AllocateBlockBox();
                    bb.Element = managed; bb.Style = style;
                    BuildChildren(snap, nodeId, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                var ib = pool.AllocateInlineBox();
                ib.Element = managed; ib.Style = style;
                BuildInlineChildren(snap, nodeId, style, ib);
                parent.AddChild(ib);
                return;
            }
            if (kind == NodeKind.Text) {
                var run = pool.AllocateTextRun();
                run.Text = snap.TextValues[nodeId] ?? "";
                run.Style = parentStyle;
                run.Element = parent.Element;
                run.SourceNode = snap.ManagedNodes[nodeId] as TextNode;
                parent.AddChild(run);
                return;
            }
        }

        void BuildChildren(DomSnapshot snap, int elementId, ComputedStyle style, BlockBox parent) {
            var managed = snap.ManagedNodes[elementId] as Element;
            MaybeInjectPseudoElement(managed, parent, BeforeStyleOf);
            // List-item markers — mirror BoxBuilder so the snapshot path
            // produces the same box-tree shape for `<li>` inside `<ul>`/`<ol>`.
            MaybeInjectListMarker(managed, style, parent);
            // PA6: precompute ordinals for every `<li>` child of this list
            // parent in one O(siblings) pass before descending into them.
            // MaybeInjectListMarker on each `<li>` then reads the seeded
            // value out of `liOrdinals` instead of walking the parent's
            // children itself.
            if (managed != null && (managed.TagName == "ul" || managed.TagName == "ol")) {
                PrecomputeLiOrdinals(managed);
            }
            int c = snap.FirstChild[elementId];
            while (c >= 0) {
                AppendNodeAsBlockChild(snap, c, style, parent);
                c = snap.NextSibling[c];
            }
            MaybeInjectPseudoElement(managed, parent, AfterStyleOf);
            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);
        }

        // Mirror of BoxBuilder.PrecomputeLiOrdinals — same spec coverage
        // (HTML `<ol start>` / `<ol reversed>` / `<li value>`) over the
        // managed Element tree. The snapshot path reaches the same managed
        // Element via `snap.ManagedNodes[id]`, so we can share the Element-
        // keyed dict between the two builders' instances (they each have
        // their own).
        void PrecomputeLiOrdinals(Element listParent) {
            int counter = 1;
            int step = 1;
            bool isOl = listParent.TagName == "ol";
            if (isOl) {
                if (TryParseIntAttr(listParent.GetAttribute("start"), out int start)) counter = start;
                bool reversed = listParent.GetAttribute("reversed") != null;
                if (reversed) {
                    step = -1;
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

        // CSS Lists 3 §3 / CSS Display L3 §2 marker injection — see
        // BoxBuilder.MaybeInjectListMarker for the spec / v2 type-table
        // rationale. The snapshot path resolves sibling index via the managed
        // Element tree (parent.Children) rather than the snapshot's NextSibling
        // array because the host Element is already in scope and the lookup is
        // O(siblings) either way.
        // C8 fix: gated on display:list-item (not tag-name) so any element
        // with display:list-item gets a marker, not just <li> inside <ul>/<ol>.
        void MaybeInjectListMarker(Element element, ComputedStyle style, BlockBox parent) {
            if (element == null || style == null) return;
            // CSS Display L3 §2 / CSS Lists L3 §2: any element with
            // display: list-item generates a marker box.
            string disp = StyleResolver.Display(style);
            if (disp != "list-item") return;
            string lst = style.Get(CssProperties.ListStyleTypeId);
            string img = ListMarkerStyle.ImageOrNull(style.Get(CssProperties.ListStyleImageId));
            if (lst == "none" && img == null) return;
            // Image wins over text (spec §3.3) — emit a marker with no glyph.
            if (img != null) {
                BuildSnapshotMarkerBox(element, parent, style, null, img);
                return;
            }
            // PA6: prefer the value seeded by PrecomputeLiOrdinals in
            // BuildChildren — turns the per-li sibling walk into a single
            // dict lookup. Fallback walk stays for one-shot callers that
            // bypass BuildChildren (e.g. unit tests that call into the
            // marker logic with a freshly constructed builder) or for
            // non-<li> elements with display:list-item.
            int idx;
            if (!liOrdinals.TryGetValue(element, out idx)) {
                ListMarkerOrdinalWalks++;
                idx = 1;
                var p = element.Parent as Element;
                if (p != null) {
                    foreach (var sib in p.Children) {
                        if (sib is Element se) {
                            if (ReferenceEquals(se, element)) break;
                            // Count this sibling if it's a <li> (classic path)
                            // or has display:list-item (general case).
                            bool sibIsLi = se.TagName == "li";
                            if (sibIsLi) { idx++; continue; }
                            // Note: for snapshot path we don't have a
                            // per-element styleOf delegate here; we use the
                            // snapshot's styleOf by nodeId. For the fallback
                            // walk of non-<li> elements, we conservatively
                            // count only <li> siblings since we can't easily
                            // resolve styles for siblings here. This means
                            // ordinals for non-<li> display:list-item elements
                            // may be off by 1 when mixed with <li> siblings —
                            // acceptable v1 approximation.
                        }
                    }
                }
            }
            string text = ListMarkerStyle.MarkerText(lst, idx);
            BuildSnapshotMarkerBox(element, parent, style, text, null);
        }

        void BuildSnapshotMarkerBox(Element managed, BlockBox parent, ComputedStyle style, string text, string image) {
            var markerStyle = MarkerStyleOf?.Invoke(managed) ?? style;
            var bb = pool.AllocateBlockBox();
            bb.Element = null;
            bb.Style = markerStyle;
            bb.IsInlineBlock = true;
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

        void BuildInlineChildren(DomSnapshot snap, int elementId, ComputedStyle style, InlineBox parent) {
            var managed = snap.ManagedNodes[elementId] as Element;
            MaybeInjectPseudoElement(managed, parent, BeforeStyleOf);
            int c = snap.FirstChild[elementId];
            while (c >= 0) {
                AppendInlineChild(snap, c, style, parent);
                c = snap.NextSibling[c];
            }
            MaybeInjectPseudoElement(managed, parent, AfterStyleOf);
        }

        void AppendInlineChild(DomSnapshot snap, int nodeId, ComputedStyle parentStyle, InlineBox parent) {
            var kind = snap.Kinds[nodeId];
            if (kind == NodeKind.Element) {
                var managed = snap.ManagedNodes[nodeId] as Element;
                var style = styleOf(nodeId);
                string disp = StyleResolver.Display(style);
                if (disp == "none") return;
                if (disp == "contents") {
                    int c = snap.FirstChild[nodeId];
                    while (c >= 0) {
                        AppendInlineChild(snap, c, style, parent);
                        c = snap.NextSibling[c];
                    }
                    return;
                }
                // CSS 2.1 §9.7 float / abs-position blockification — mirrors BoxBuilder.
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
                MaybeApplyFieldSizingWidth(managed, style);
                MaybeInjectBackdrop(managed, parent);
                if (disp == "inline-block" || disp == "block" || disp == "flex" || disp == "grid" || disp == "inline-flex" || disp == "inline-grid" || disp == "inline-table" || IsTableDisplay(disp)) {
                    var bb = NewBlockBoxFor(disp);
                    bb.Element = managed; bb.Style = style;
                    BuildChildren(snap, nodeId, style, bb);
                    parent.AddChild(bb);
                    return;
                }
                var ib = pool.AllocateInlineBox();
                ib.Element = managed; ib.Style = style;
                BuildInlineChildren(snap, nodeId, style, ib);
                parent.AddChild(ib);
                return;
            }
            if (kind == NodeKind.Text) {
                var run = pool.AllocateTextRun();
                run.Text = snap.TextValues[nodeId] ?? "";
                run.Style = parentStyle;
                run.Element = parent.Element;
                run.SourceNode = snap.ManagedNodes[nodeId] as TextNode;
                parent.AddChild(run);
                return;
            }
        }

        // CSS UI L4 §13 field-sizing: content — mirrors BoxBuilder.MaybeApplyFieldSizingWidth.
        // When field-sizing: content is cascaded onto a textual <input>, writes an
        // intrinsic border-box width (text-width + caret + padding + border) directly
        // into the ComputedStyle so ApplyBoxModel reads the computed-content size
        // instead of the UA's fixed 218px default.
        void MaybeApplyFieldSizingWidth(Element e, ComputedStyle style) {
            if (e == null || style == null) return;
            if (e.TagName != "input") return;
            string inputType = e.GetAttribute("type");
            if (!string.IsNullOrEmpty(inputType)) {
                string t = CssStringUtil.ToLowerInvariantOrSame(inputType);
                if (t == "checkbox" || t == "radio" || t == "range"
                    || t == "submit" || t == "button" || t == "reset"
                    || t == "image" || t == "file" || t == "color") {
                    return;
                }
            }
            string fieldSizing = style.Get("field-sizing");
            if (string.IsNullOrEmpty(fieldSizing) || fieldSizing != "content") return;

            string value = e.GetAttribute("value") ?? "";
            if (value.Length == 0) {
                string ph = e.GetAttribute("placeholder");
                if (!string.IsNullOrEmpty(ph)) value = ph;
            }

            double textWidth;
            if (FieldSizingMetrics != null && value.Length > 0) {
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
                textWidth = value.Length * BoxBuilder.StubCharWidthPx;
            }

            double padL = ReadSimplePx(style.Get(CssProperties.PaddingLeftId), 8.0);
            double padR = ReadSimplePx(style.Get(CssProperties.PaddingRightId), 8.0);
            double bordL = ReadSimplePx(style.Get(CssProperties.BorderLeftWidthId), 1.0);
            double bordR = ReadSimplePx(style.Get(CssProperties.BorderRightWidthId), 1.0);
            bool isBorderBox = IsSnapshotBorderBox(style);
            double intrinsicWidth;
            if (isBorderBox) {
                intrinsicWidth = textWidth + BoxBuilder.FieldSizingCaretPaddingPx + padL + padR + bordL + bordR;
            } else {
                intrinsicWidth = textWidth + BoxBuilder.FieldSizingCaretPaddingPx;
            }
            if (intrinsicWidth < 0) intrinsicWidth = 0;
            style.Set("width", intrinsicWidth.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "px");
        }

        static bool IsSnapshotBorderBox(ComputedStyle style) {
            if (style == null) return false;
            var v = style.GetParsed(CssProperties.BoxSizingId);
            if (v is CssKeyword k) return k.Identifier == "border-box";
            if (v is CssIdentifier id) return id.Name == "border-box";
            return style.Get(CssProperties.BoxSizingId) == "border-box";
        }

        static double ReadSimplePx(string raw, double fallback) {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (!raw.EndsWith("px")) return fallback;
            if (double.TryParse(raw.AsSpan(0, raw.Length - 2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double v)) return v;
            return fallback;
        }

        // Decodes the keyword name of a parsed CssValue for keyword-typed
        // properties. Mirrors BoxBuilder.KeywordName so the snapshot builder
        // matches the same blockification rules without re-routing through
        // the raw-string Get path.
        static string KeywordName(CssValue parsed) {
            if (parsed is CssKeyword k) return k.Identifier;
            if (parsed is CssIdentifier id) return id.Name;
            return null;
        }
    }
}
