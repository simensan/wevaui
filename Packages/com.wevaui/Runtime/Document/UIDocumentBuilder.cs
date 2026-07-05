using System;
using System.Collections.Generic;
using System.IO;
using Weva.Binding;
using Weva.Components;
using Weva.Components.Scoping;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Css.Values;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Events;
using Weva.Layout;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using Weva.Layout.Text;
using Weva.Paint.Conversion;
using Weva.Paint.Images;
using Weva.Parsing;
using Weva.Reactive;
using Weva.ViewTransitions;

namespace Weva.Documents {
    // Builds a UIDocumentState from the inputs WevaDocument exposes — the HTML
    // source string, a list of CSS source strings, an optional controller, a
    // pre-built MediaContext, and an IUIClock. Pure C# — testable headlessly,
    // does not touch UnityEngine.
    //
    // Build order matches the lifecycle § from the orchestrator spec:
    //   1. parse HTML -> Document
    //   2. parse CSS sources -> Stylesheets
    //   3. ComponentRegistry.RegisterAllFromDocument + ComponentExpander.Expand
    //   4. compose UA + author + component-scoped sheets, build CascadeEngine
    //   5. wire CssAnimationRunner onto the cascade
    //   6. build LayoutEngine + LayoutContext + BoxToPaintConverter
    //   7. allocate InvalidationTracker, attach to Doc, route into the engines
    //   8. build EventDispatcher (with a stub hit-tester) and BindingSet, wire
    //
    // The orchestrator (WevaDocument MonoBehaviour or test) is then free to call
    // UIDocumentLifecycle.Update each frame.
    public sealed class UIDocumentBuilder {
        public string DocumentSource { get; set; }
        // Optional absolute path the DocumentSource was loaded from. Used
        // by the HTML hot-reload coordinator; null when the source is an
        // in-memory string.
        public string DocumentPath { get; set; }
        public IList<string> StylesheetSources { get; set; }
        // Optional, parallel to StylesheetSources — each entry is the
        // absolute path the corresponding source was loaded from. Used by
        // the hot-reload coordinator; null entries are allowed for inline
        // sources.
        public IList<string> StylesheetPaths { get; set; }
        public object Controller { get; set; }
        public MediaContext MediaContext { get; set; } = MediaContext.Default(
            UIDocumentDefaults.DefaultViewportWidthPx,
            UIDocumentDefaults.DefaultViewportHeightPx);
        public IUIClock Clock { get; set; }
        public IFontMetrics FontMetricsOverride { get; set; }
        public IImageRegistry ImageRegistry { get; set; }
        public bool LenientHtmlParsing { get; set; } = true;
        public bool LenientCssParsing { get; set; } = true;
        // Build-time bake of <link rel="stylesheet"> contents, parallel
        // lists keyed by the href EXACTLY as authored. Player builds have no
        // disk to resolve hrefs against (DocumentPath is editor-only), so an
        // editor build hook (LinkedStylesheetBakeProcessor) captures each
        // link's CSS text into the WevaDocument and it flows in here. Only
        // consulted when DocumentPath is unavailable — with a disk path the
        // live file always wins, so a stale bake can never shadow an edit
        // in the editor.
        public IReadOnlyList<string> BakedLinkedHrefs { get; set; }
        public IReadOnlyList<string> BakedLinkedCss { get; set; }
        // Build-time bake of <template src="..."> HTML, parallel lists keyed
        // by the src attribute value EXACTLY as authored. Same player-build
        // rationale as BakedLinkedHrefs/Css: ComponentTemplateImporter reads
        // template files relative to DocumentPath, which is null in players.
        public IReadOnlyList<string> BakedTemplateHrefs { get; set; }
        public IReadOnlyList<string> BakedTemplateHtml { get; set; }

        public UIDocumentState Build() {
            var state = new UIDocumentState();
            state.MediaContext = MediaContext;

            // 1. HTML -> Document. Empty source: produce an empty document so
            //    the rest of the pipeline still composes cleanly (matches the
            //    "documentAsset == null -> idle" behavior the spec requires).
            var docOpts = new ParseOptions { ThrowOnError = !LenientHtmlParsing };
            state.Doc = string.IsNullOrEmpty(DocumentSource)
                ? new Weva.Dom.Document()
                : HtmlParser.Parse(DocumentSource, docOpts);
            var componentTemplatePaths = new List<string>();
            ComponentTemplateImporter.Resolve(state.Doc, DocumentPath, docOpts, componentTemplatePaths,
                BakedTemplateHrefs, BakedTemplateHtml);
            state.ComponentTemplatePaths = componentTemplatePaths;

            // 2. CSS sources -> Stylesheets. We keep raw Stylesheets (for
            //    KeyframesResolver) and OriginatedStylesheets (for the
            //    cascade) in parallel.
            var cssOpts = new ParseOptions { ThrowOnError = !LenientCssParsing };
            var rawSheets = new List<Stylesheet>();
            var originated = new List<OriginatedStylesheet>();
            originated.Add(UserAgentStylesheet.Parse());
            originated.Add(Weva.Forms.FormControlStylesheet.Parse());
            var paths = new List<string>();
            var authorSources = new List<string>();
            var authorPaths = new List<string>();
            AppendExplicitStylesheets(authorSources, authorPaths);
            AppendLinkedStylesheets(state.Doc, DocumentPath, state.MediaContext, authorSources, authorPaths);
            AppendInlineStyleBlocks(state.Doc, DocumentPath, state.MediaContext, authorSources, authorPaths);
            if (authorSources.Count > 0) {
                for (int i = 0; i < authorSources.Count; i++) {
                    string src = authorSources[i];
                    if (string.IsNullOrEmpty(src)) continue;
                    string path = i < authorPaths.Count ? authorPaths[i] : null;
                    var parsed = CssParser.Parse(src, cssOpts);
                    // CSS Cascading L4 §6 — resolve `@import` rules synchronously
                    // against the importing sheet's path before the cascade
                    // sees the sheet. The loader recursively splices imported
                    // rules in place; the cascade engine has no `ImportRule`
                    // case because none should reach it after this pass.
                    var sheet = AtImportLoader.Resolve(parsed, path, state.MediaContext, parseOptions: cssOpts);
                    rawSheets.Add(sheet);
                    originated.Add(OriginatedStylesheet.Author(sheet));
                    paths.Add(path);
                }
            }
            state.AuthorStylesheets = originated;
            state.KeyframeStylesheets = rawSheets;
            state.StylesheetPaths = paths;
            state.DocumentPath = DocumentPath;

            // 3. Component registration + expansion. Done before cascade so
            //    expanded subtrees are present when selectors run.
            state.Components = new ComponentRegistry();
            state.Components.RegisterAllFromDocument(state.Doc);
            new ComponentExpander(state.Components).Expand(state.Doc);

            // Component-scoped author sheets join the cascade after author
            // sheets so their specificity ties resolve in registration order.
            foreach (var os in ComponentStyleIntegration.RewrittenStylesheets(state.Components)) {
                originated.Add(os);
            }

            // 4. Cascade.
            state.State = new InteractionStateProvider();
            // Subscribe to DOM mutations so element-removed events compact
            // the state map. Without this, an element that is focused (or
            // had been the :target) when it's removed from the tree pins
            // one Dictionary<Element, ElementState> entry forever — the
            // dispatcher's ForgetIfInSubtree nulls `focused` but never
            // clears the now-orphaned Focus / FocusVisible bits stored
            // against the removed Element. See MS5 in CODE_AUDIT_FINDINGS.md.
            state.State.AttachToDocument(state.Doc);
            state.Cascade = new CascadeEngine(originated, state.MediaContext);
            // Plumb the engine into the state provider so its SetFlag path
            // can ask the RuleFeatureSet "does any subject selector with
            // this state pseudo actually match the element?" — chain
            // ancestors that no rule targets on :active stay clean of
            // PseudoClassState marks and don't trigger ancestor-Version
            // fan-out in the incremental cascade. See
            // CascadeEngine.SubjectMatchAffectedByStateBit.
            state.State.AttachCascade(state.Cascade);

            // 5. Animation runner — IUIClock supplied by the orchestrator;
            //    fall back to SystemUIClock for headless tests that don't
            //    care about deterministic time. Tracker is set in step 7.
            var clock = Clock ?? new SystemUIClock();
            state.Clock = clock;
            state.Animator = new CssAnimationRunner(state.Cascade, rawSheets, clock);
            state.Cascade.AttachAnimationRunner(state.Animator);
            // Subscribe to DOM mutations so element-removed events compact
            // the runner's eight element-keyed dictionaries. Without this,
            // an element with `animation-iteration-count: infinite` removed
            // from the tree mid-animation stays pinned forever — the per-
            // tick sweep only deletes records when an animation NATURALLY
            // completes. See MS2 in CODE_AUDIT_FINDINGS.md.
            state.Animator.AttachToDocument(state.Doc);

            // 6. Layout + paint.
            var fontMetrics = FontMetricsOverride ?? UIDocumentDefaults.CreateDefaultFontMetrics();
            state.FontMetrics = fontMetrics;
            state.LayoutContext = new LayoutContext(fontMetrics) {
                ViewportWidthPx = state.MediaContext.ViewportWidthPx,
                ViewportHeightPx = state.MediaContext.ViewportHeightPx,
                DpiPixelsPerInch = state.MediaContext.DpiPixelsPerInch,
                RootFontSizePx = UIDocumentDefaults.DefaultFontSizePx,
                // Per-element font-family at layout time (advances). The TMP/SDF
                // backend installs this so a Sniglet run measures with Sniglet.
                FamilyMetricsResolver = UIDocumentDefaults.FamilyMetricsResolver
            };
            state.LayoutEngine = new LayoutEngine(fontMetrics);
            state.LayoutEngine.ImageRegistry = ImageRegistry;
            // Wire CSS pseudo-element resolvers from the cascade. The layout
            // engine queries these per-element during box-tree construction;
            // when null (e.g. tests that bypass UIDocumentBuilder) the
            // corresponding pseudo box is simply skipped.
            state.LayoutEngine.BackdropStyleOf = e => state.Cascade?.ComputeBackdrop(e, state.State);
            state.LayoutEngine.BeforeStyleOf = e => state.Cascade?.ComputeBefore(e, state.State);
            state.LayoutEngine.AfterStyleOf = e => state.Cascade?.ComputeAfter(e, state.State);
            state.LayoutEngine.MarkerStyleOf = e => state.Cascade?.ComputeMarker(e, state.State);
            // CRITICAL: paint's LengthContext must mirror layout's viewport so
            // CSS clamp(min, vmin/vw/vh expr, max) values resolve to the SAME
            // number on both sides. The default LengthContext hardcodes a
            // 1920×1080 viewport — when the actual surface is smaller (e.g.
            // 1529×934) every clamp() that maxed out at the upper bound
            // (because 1.15vmin × 1080/100 > max) actually clamped at the
            // mid value at the smaller viewport. Layout used the smaller
            // value; paint used the larger; rendered glyphs were 10-15%
            // wider than measured and adjacent text runs visibly overlapped.
            state.Painter = new BoxToPaintConverter(new LengthContext {
                BaseFontSizePx = UIDocumentDefaults.DefaultFontSizePx,
                RootFontSizePx = UIDocumentDefaults.DefaultFontSizePx,
                ViewportWidthPx = state.MediaContext.ViewportWidthPx,
                ViewportHeightPx = state.MediaContext.ViewportHeightPx,
                DpiPixelsPerInch = state.MediaContext.DpiPixelsPerInch
            });
            state.Painter.ImageRegistry = ImageRegistry;
            // Render backend (URP) registers atlas-aware BoxBatchSnapshots
            // via IValidatedBoxBatchSnapshot — those revalidate against
            // SdfTextRendering.CurrentAtlasVersion on every Convert, so
            // text-bearing subtrees are safe to capture/replay. Opt in
            // here so the converter doesn't conservatively re-walk every
            // card subtree containing a label on every warm frame; this
            // is the dominant paint-warm-flip win on text-heavy UIs.
            // Document-builder integration tests (which mock the snapshot
            // registry with FakeSnapshot) can override on their own
            // Painter instance.
            state.Painter.AllowTextSubtreeSnapshotReplay = true;
            // Retained paint for static scrollable regions (replay a CLEAN
            // scroll-content subtree instead of re-converting it). Capability
            // kept but DISABLED: it only pays off when the captured subtree's
            // snapshot stays VALID across frames, and a text-heavy region whose
            // glyph atlas churns every frame (re-shaping) invalidates the
            // snapshot every frame → re-capture overhead (measured WORSE on
            // layout-stress: commands 925→1529). Re-enable once text shaping is
            // stable (cached) so the atlas version holds.
            state.Painter.AllowScrollContentSnapshotReplay = false;
            state.ImageRegistry = ImageRegistry;
            // ::placeholder cascade resolver — the painter calls this for
            // empty `<input>` boxes to pull the author-declared placeholder
            // color (and any other inherited text properties).
            state.Painter.PlaceholderStyleOf = e => state.Cascade?.ComputePlaceholder(e, state.State);
            // ::selection style so the highlight honors background-color.
            state.Painter.SelectionStyleOf = e => state.Cascade?.ComputeSelection(e, state.State);
            // Caret + selection geometry for the FOCUSED input: measure the
            // focused control's TextEditModel caret/selection against the layout
            // font metrics (same recipe as the registry's caret measurer) so the
            // painted caret aligns with the value text. Null for any non-focused
            // or non-text-editable element.
            state.Painter.InputCaretOf = element => {
                var ev = state.Events;
                if (ev == null || !ReferenceEquals(ev.FocusedElement, element)) return null;
                if (state.FormControls == null || !state.FormControls.TryGet(element, out var ic) || ic == null) return null;
                var model = ic.Model;
                var st = state.StyleOf != null ? state.StyleOf(element) : null;
                var lctx = state.LayoutContext;
                if (model == null || st == null || lctx == null) return null;
                var parentStyle = element.Parent is Weva.Dom.Element pe && state.StyleOf != null
                    ? state.StyleOf(pe) : null;
                double fs = Weva.Layout.StyleResolver.FontSizePx(st, parentStyle, lctx);
                string family = st.Get("font-family");
                var metrics = lctx.GetMetrics(family);
                if (metrics == null) return null;
                string text = model.Text ?? "";
                if (element.TagName == "input" && element.GetAttribute("type") == "password")
                    text = Weva.Forms.InputRenderer.BulletMask(text.Length); // PF2: cached mask
                int len = text.Length;
                var sel = model.Selection;
                int caretIdx = sel.Focus < 0 ? 0 : (sel.Focus > len ? len : sel.Focus);
                int s = sel.Start < sel.End ? sel.Start : sel.End;
                int e = sel.Start < sel.End ? sel.End : sel.Start;
                if (s < 0) s = 0; if (e > len) e = len;
                // TX3: measure with the SAME face the value text renders with.
                // The engine's metrics are weight/style-aware (bold/italic
                // route to variant faces with DIFFERENT advances), so a
                // weight-blind measure drifts the caret px-per-char on an
                // `input { font-weight: 600 }` — cumulatively visible by
                // mid-string.
                int weight = Weva.Paint.Conversion.TextRunResolver.ResolveFontWeight(st);
                var fstyle = Weva.Paint.Conversion.TextRunResolver.ResolveFontStyle(st);
                var styled = metrics as Weva.Layout.Text.IStyledFontMetrics;
                double MeasureTo(int idx) => styled != null
                    ? styled.Measure(text, 0, idx, fs, family, fstyle, weight)
                    : metrics.Measure(text, 0, idx, fs);
                double caretX = MeasureTo(caretIdx);
                bool hasSel = e > s;
                double selStartX = hasSel ? MeasureTo(s) : 0;
                double selEndX = hasSel ? MeasureTo(e) : 0;
                return new Weva.Paint.Conversion.BoxToPaintConverter.InputCaretGeometry(
                    caretX, hasSel, selStartX, selEndX, state.CaretBlinkOn,
                    // Audit #7: the controller's persistent edit window — the
                    // painter shifts the text/caret/selection by it instead
                    // of re-deriving a stateless caret-follow.
                    scrollX: ic.EditScrollX);
            };
            // Multiline caret/selection for a focused <textarea> (audit #6):
            // align the painted LineBox/TextRun geometry back to model
            // indices (TextAreaCaretMap) and hand the painter ready-made
            // border-box-relative rects. Rebuilt per paint of the focused
            // textarea only (~2/sec blink + edits).
            state.Painter.TextAreaOverlayOf = element => {
                var ev = state.Events;
                if (ev == null || !ReferenceEquals(ev.FocusedElement, element)) return null;
                if (element.TagName != "textarea") return null;
                if (state.FormControls == null || !state.FormControls.TryGet(element, out var ic) || ic == null) return null;
                var model = ic.Model;
                if (model == null || model.MeasureSubstring == null) return null;
                var box = state.BoxLookup != null ? state.BoxLookup(element) : null;
                if (box == null) return null;
                var map = Weva.Forms.TextAreaCaretMap.Build(box, model.Text ?? "", model.MeasureSubstring);
                if (map == null) return null;
                var sel = model.Selection;
                int s = sel.Start < sel.End ? sel.Start : sel.End;
                int e = sel.Start < sel.End ? sel.End : sel.Start;
                System.Collections.Generic.List<(double X, double Y, double W, double H)> rects = null;
                if (e > s) {
                    rects = new System.Collections.Generic.List<(double X, double Y, double W, double H)>(4);
                    map.AddSelectionRects(s, e, rects);
                }
                var (cx, cy, ch) = map.CaretRectFor(sel.Focus);
                if (ch <= 0) {
                    // Empty textarea — the map synthesized a zero-height line;
                    // stand the caret up at the element's line height.
                    var st2 = state.StyleOf != null ? state.StyleOf(element) : null;
                    var parent2 = element.Parent is Weva.Dom.Element pe2 && state.StyleOf != null
                        ? state.StyleOf(pe2) : null;
                    double fs2 = st2 != null && state.LayoutContext != null
                        ? Weva.Layout.StyleResolver.FontSizePx(st2, parent2, state.LayoutContext)
                        : 16;
                    ch = fs2 * Weva.Layout.StyleResolver.DefaultLineHeightFactor;
                }
                return new Weva.Paint.Conversion.BoxToPaintConverter.TextAreaOverlayGeometry {
                    SelectionRects = rects,
                    CaretX = cx, CaretY = cy, CaretHeight = ch,
                    // Chrome: highlight OR caret, never both.
                    CaretVisible = state.CaretBlinkOn && e <= s,
                };
            };
            // ::-webkit-scrollbar* pseudo-element resolver — the painter queries this
            // at scrollbar paint time so webkit scrollbar styles override CSS Scrollbars
            // L1 (scrollbar-color / scrollbar-width) when present.
            state.Painter.ScrollbarCascade = state.Cascade;

            // 7. Invalidation. Attach to Doc here so any subsequent mutation
            //    (e.g. controller-driven SetAttribute calls) lands in the
            //    tracker before the next Update sees it.
            state.Invalidation = new InvalidationTracker();
            state.Invalidation.Attach(state.Doc);
            // CX3: enable the tracker's :has() ancestor-walk invalidation when
            // the sheet actually contains a :has() selector. The tracker-side
            // machinery (ChildAdded/ChildRemoved/Attribute* branches) and the
            // engine-side detector both existed, but this wiring line never
            // did — structural `:has()` NEVER re-matched on DOM/attribute
            // mutation in production (`.card:has(.open)` stayed stale forever
            // after a class flip on a descendant).
            state.Invalidation.HasSensitive = state.Cascade.HasAnyHasSelector;
            state.Animator.InvalidationTracker = state.Invalidation;
            // Without this, every :hover/:active/:focus flip via SetFlag does
            // `tracker?.MarkDirty(...)` against a null tracker and silently
            // no-ops — the cascade resolves the new state's style correctly
            // when it's next asked, but nothing tells it to re-ask, so the
            // hover background stays the unhovered color until some other
            // mutation happens to dirty the element.
            state.State.Tracker = state.Invalidation;
            state.ElementToBox = new ElementToBoxIndex();

            // 8. Events. We can't build a real BoxTreeHitTester yet because
            //    no layout has run; the EventDispatcher's hit-tester is
            //    readonly so we install a swappable wrapper that returns null
            //    until the first layout pass populates its inner tester.
            var hit = new SwappableHitTester();
            state.HitTester = hit;
            // Share the cascade's InteractionStateProvider with the dispatcher
            // so :hover/:active/:focus written by pointer events take effect
            // on the next cascade pass. Without this shared instance, the
            // dispatcher writes to a private provider and visual state lags
            // behind input forever.
            state.Events = new EventDispatcher(state.Doc, hit, state.State, clock);
            // W3 spatial navigation needs box geometry; the dispatcher stays
            // box-tree-agnostic via these providers. Lazy lambdas because
            // RootBox / ElementToBox are (re)populated by later passes and
            // rebuilds — capturing `state` keeps them current.
            state.Events.RootBoxProvider = () => state.RootBox;
            state.Events.ElementToBox = e => state.ElementToBox?.Lookup(e);

            // 9. Bindings. Scanner walks the (already-expanded) document and
            //    produces both data + event bindings; Wire ties events into
            //    the dispatcher built in step 8.
            state.Bindings = BindingScanner.Scan(state.Doc, Controller);
            state.Bindings.Wire(state.Events);
            state.Bindings.AttachLive(state.Doc, Controller);
            if (Controller != null) {
                UIElementBinder.Populate(Controller, state.Doc);
            }

            // Auto-wire form controls so <input type="text"> et al. receive
            // KeyDown and commit Value on blur without authors instantiating
            // InputController themselves. Mirrors BindingSet.AttachLive — the
            // registry observes DOM mutations so dynamically-inserted inputs
            // wire up too.
            state.FormControls = new Weva.Forms.FormControlsRegistry(state.Doc, state.Events,
                // W4: metric-aware caret math. The inner lambda resolves
                // style/font-size/metrics PER CALL so cascade-driven font
                // changes (hover-grow inputs, container queries) never leave
                // the caret measuring with a stale face or size. Caret-nav
                // call rate makes the per-call StyleOf lookup negligible.
                inputElement => (text, start, count) => {
                    var style = state.StyleOf != null ? state.StyleOf(inputElement) : null;
                    var ctx2 = state.LayoutContext;
                    if (style == null || ctx2 == null) return 0;
                    var parentStyle = inputElement.Parent is Weva.Dom.Element pe && state.StyleOf != null
                        ? state.StyleOf(pe) : null;
                    double fs = Weva.Layout.StyleResolver.FontSizePx(style, parentStyle, ctx2);
                    string family = style.Get("font-family");
                    var metrics = ctx2.GetMetrics(family);
                    if (metrics == null) return 0;
                    // TX3: same weight/style-aware measure as the paint hook —
                    // click-to-caret and vertical nav must agree with the
                    // painted glyphs, not just with each other.
                    if (metrics is Weva.Layout.Text.IStyledFontMetrics styled) {
                        return styled.Measure(text, start, count, fs, family,
                            Weva.Paint.Conversion.TextRunResolver.ResolveFontStyle(style),
                            Weva.Paint.Conversion.TextRunResolver.ResolveFontWeight(style));
                    }
                    return metrics.Measure(text, start, count, fs);
                },
                // Box resolver so range-slider drag controllers can map a pointer
                // X to a value along the track.
                state.BoxLookup,
                // Tracker so the <select> dropdown popup can invalidate the doc.
                state.Invalidation);
            // Input/selection audit #3: anchor the caret blink to edit
            // activity so the caret stays solid while typing (Chrome resets
            // the blink timer on every keystroke/caret move). The lifecycle
            // stamps the tick time on the next Update.
            state.FormControls.CaretActivity = () => state.CaretActivityPending = true;
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            // Input/selection audit #5: system clipboard for Ctrl+C/X/V.
            // Desktop/editor only (GUIUtility.systemCopyBuffer); other
            // platforms leave the bridge null and the chords inert.
            state.FormControls.Clipboard = new Weva.Forms.Bridge.UnityClipboardBridge();
#endif

            // 10. Scroll event bridge. Listens for wheel/key events on the
            //     document root and mutates ScrollState entries on the layout
            //     engine's ScrollContainer. Programmatic ScrollBy/ScrollTo
            //     also goes through this object.
            state.ScrollEvents = new ScrollEventHandler(
                state.Events,
                state.Doc,
                state.LayoutEngine.ScrollContainer,
                e => state.ElementToBox?.Lookup(e),
                () => state.LayoutContext.RootFontSizePx,
                () => clock.NowSeconds,
                e => state.Invalidation?.MarkDirty(e, InvalidationKind.Paint));

            // 11. Smooth scroll + snap + momentum. The animator and resolver
            //     are additive — ScrollEventHandler routes through them when
            //     the container's CSS opts in (scroll-behavior: smooth,
            //     scroll-snap-type). Momentum is fed drag samples by the
            //     pointer path and integrated by the lifecycle (step 1b).
            state.SmoothScroll = new SmoothScrollAnimator(state.LayoutEngine.ScrollContainer);
            state.SnapResolver = new SnapResolver(state.LayoutEngine.ScrollContainer);
            state.ScrollEvents.SmoothAnimator = state.SmoothScroll;
            state.ScrollEvents.SnapResolver = state.SnapResolver;
            state.Momentum = new Weva.Layout.Scrolling.Smooth.ScrollMomentum(state.LayoutEngine.ScrollContainer) {
                SnapAnimator = state.SmoothScroll,
                SnapResolver = state.SnapResolver
            };
            state.ScrollEvents.MomentumAnimator = state.Momentum;

            // 12. View transitions. Bound to this document via the extension
            //     method registry so callers can `doc.StartViewTransition(...)`.
            state.ViewTransitions = ViewTransitionEngine.Create(
                state.Doc,
                state.MediaContext,
                () => state.RootBox,
                () => UIDocumentLifecycle.RunLayout(state));
            DocumentViewTransitionExtensions.AttachViewTransitionEngine(state.Doc, state.ViewTransitions);

            return state;
        }

        void AppendExplicitStylesheets(List<string> sources, List<string> paths) {
            if (sources == null || paths == null || StylesheetSources == null) return;
            for (int i = 0; i < StylesheetSources.Count; i++) {
                string src = StylesheetSources[i];
                if (string.IsNullOrEmpty(src)) continue;
                sources.Add(src);
                paths.Add(StylesheetPaths != null && i < StylesheetPaths.Count ? StylesheetPaths[i] : null);
            }
        }

        void AppendLinkedStylesheets(Document doc, string documentPath, MediaContext media,
                                     List<string> sources, List<string> paths) {
            if (doc == null || sources == null || paths == null) return;
            var links = new List<Element>();
            CollectStylesheetLinks(doc, links);
            if (links.Count == 0) return;
            bool haveDiskBase = !string.IsNullOrEmpty(documentPath);
            bool haveBaked = BakedLinkedHrefs != null && BakedLinkedCss != null
                && BakedLinkedHrefs.Count == BakedLinkedCss.Count
                && BakedLinkedHrefs.Count > 0;
            if (!haveDiskBase && !haveBaked) {
                UICssDiagnostics.Warn("html-link-stylesheet",
                    "<link rel=\"stylesheet\"> requires UIDocumentBuilder.DocumentPath (editor: disk resolve) " +
                    "or BakedLinkedHrefs/Css (player: build-time bake) so CSS files can be resolved.");
                return;
            }

            string basePath = haveDiskBase ? NormalizePath(documentPath) : null;
            for (int i = 0; i < links.Count; i++) {
                var link = links[i];
                string href = link.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) {
                    UICssDiagnostics.Warn("html-link-stylesheet", "<link rel=\"stylesheet\"> without href skipped.");
                    continue;
                }
                if (IsRemoteHref(href)) {
                    UICssDiagnostics.Warn("html-link-stylesheet", "remote stylesheet links are not supported (v1): " + href);
                    continue;
                }
                // The media attribute evaluates against the RUNTIME media
                // context (viewport, color-scheme) for both disk and baked
                // sources — bake-time can't know the device, so the bake
                // stores every link and this check stays the gate.
                if (!MatchesMedia(link.GetAttribute("media"), media)) {
                    continue;
                }

                if (haveDiskBase) {
                    string resolved = ResolveHref(href, basePath);
                    if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) {
                        string content;
                        try {
                            content = File.ReadAllText(resolved);
                        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                            UICssDiagnostics.Warn("html-link-stylesheet",
                                "failed to read stylesheet link '" + resolved + "': " + ex.Message);
                            continue;
                        }
                        sources.Add(content);
                        paths.Add(NormalizePath(resolved));
                        continue;
                    }
                    if (!TryAppendBaked(href, sources, paths, haveBaked)) {
                        UICssDiagnostics.Warn("html-link-stylesheet", "could not resolve stylesheet link: " + href);
                    }
                    continue;
                }

                if (!TryAppendBaked(href, sources, paths, haveBaked)) {
                    UICssDiagnostics.Warn("html-link-stylesheet",
                        "stylesheet link '" + href + "' was not baked at build time; " +
                        "rebuild the player so LinkedStylesheetBakeProcessor captures it.");
                }
            }
        }

        // Appends the baked CSS for `href` (exact match against the authored
        // href) when present. Baked sheets carry a null path: there is no
        // on-disk file in a player, so @import inside a baked linked sheet
        // cannot resolve (documented player limitation — inline the import
        // or list the imported file as its own <link>).
        bool TryAppendBaked(string href, List<string> sources, List<string> paths, bool haveBaked) {
            if (!haveBaked) return false;
            for (int i = 0; i < BakedLinkedHrefs.Count; i++) {
                if (string.Equals(BakedLinkedHrefs[i], href, StringComparison.Ordinal)) {
                    if (string.IsNullOrEmpty(BakedLinkedCss[i])) return false;
                    sources.Add(BakedLinkedCss[i]);
                    paths.Add(null);
                    return true;
                }
            }
            return false;
        }

        // Build-tooling seam: the src hrefs of every <template src="..."> in
        // document order, exactly as authored. Used by the editor bake so it
        // can capture each template's HTML text before the build strips disk
        // access. Dedup is applied so a src that appears multiple times is
        // baked once.
        public static List<string> CollectTemplateHrefs(string htmlSource) {
            return Weva.Components.ComponentTemplateImporter.CollectTemplateHrefs(htmlSource);
        }

        // Build-tooling seam: resolves a template src href against the owning
        // document's path the same way ComponentTemplateImporter does at runtime.
        public static string ResolveTemplateHref(string href, string ownerDocumentPath) {
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrEmpty(ownerDocumentPath)) return null;
            try {
                if (System.IO.Path.IsPathRooted(href)) return href;
                string dir = System.IO.Path.GetDirectoryName(NormalizePath(ownerDocumentPath));
                return string.IsNullOrEmpty(dir) ? href : System.IO.Path.Combine(dir, href);
            } catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) {
                UICssDiagnostics.Warn("template-import",
                    "failed to resolve template href '" + href + "': " + ex.Message);
                return null;
            }
        }

        // Build-tooling seam: the hrefs of every <link rel="stylesheet"> in
        // document order, exactly as authored — empty/remote/disabled/
        // alternate links excluded, the `media` attribute NOT evaluated
        // (runtime decides; the bake must store every candidate). Used by
        // the editor's LinkedStylesheetBakeProcessor and pinned by tests.
        public static List<string> CollectLinkedStylesheetHrefs(string htmlSource) {
            var hrefs = new List<string>();
            if (string.IsNullOrEmpty(htmlSource)) return hrefs;
            var doc = HtmlParser.Parse(htmlSource, new ParseOptions { ThrowOnError = false });
            var links = new List<Element>();
            CollectStylesheetLinks(doc, links);
            for (int i = 0; i < links.Count; i++) {
                string href = links[i].GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (IsRemoteHref(href)) continue;
                if (hrefs.Contains(href)) continue;
                hrefs.Add(href);
            }
            return hrefs;
        }

        // Build-tooling seam: resolves a linked-stylesheet href against the
        // owning document's path the same way the runtime does, so the
        // editor bake reads exactly the file the editor preview reads.
        public static string ResolveStylesheetHref(string href, string ownerDocumentPath) {
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrEmpty(ownerDocumentPath)) return null;
            return ResolveHref(href, NormalizePath(ownerDocumentPath));
        }

        // Inline `<style>` blocks (CSS Style Attributes / HTML §4.2.6). The HTML
        // parser keeps <style> as a raw-text element, so its CSS survives intact
        // as a TextNode child. Previously only <link> sheets and explicit
        // StylesheetSources were collected, so any sample that styled itself with
        // a <style> block (9slice-demo, grid-stress, layout-stress, etc.) rendered
        // completely unstyled. Collect each <style>'s text as an author sheet,
        // appended after linked sheets (document-order among multiple <style>s).
        static void AppendInlineStyleBlocks(Document doc, string documentPath, MediaContext media,
                                            List<string> sources, List<string> paths) {
            if (doc == null || sources == null || paths == null) return;
            var styles = new List<Element>();
            CollectStyleElements(doc, styles);
            if (styles.Count == 0) return;
            string basePath = string.IsNullOrEmpty(documentPath) ? null : NormalizePath(documentPath);
            for (int i = 0; i < styles.Count; i++) {
                var styleEl = styles[i];
                if (styleEl.HasAttribute("disabled")) continue;
                if (!MatchesMedia(styleEl.GetAttribute("media"), media)) continue;
                var sb = new System.Text.StringBuilder();
                CollectRawText(styleEl, sb);
                string css = sb.ToString();
                if (string.IsNullOrWhiteSpace(css)) continue;
                sources.Add(css);
                paths.Add(basePath); // base for @import resolution
            }
        }

        static void CollectStyleElements(Node node, List<Element> output) {
            if (node == null) return;
            if (node is Element el && string.Equals(el.TagName, "style", StringComparison.OrdinalIgnoreCase)) {
                output.Add(el);
                return; // raw-text children; no nested <style>
            }
            for (int i = 0; i < node.Children.Count; i++) CollectStyleElements(node.Children[i], output);
        }

        static void CollectRawText(Node node, System.Text.StringBuilder sb) {
            for (int i = 0; i < node.Children.Count; i++) {
                var c = node.Children[i];
                if (c is Weva.Dom.TextNode tn) sb.Append(tn.Data);
                else CollectRawText(c, sb);
            }
        }

        static void CollectStylesheetLinks(Node node, List<Element> output) {
            if (node == null) return;
            if (node is Element el && IsStylesheetLink(el)) output.Add(el);
            for (int i = 0; i < node.Children.Count; i++) {
                CollectStylesheetLinks(node.Children[i], output);
            }
        }

        static bool IsStylesheetLink(Element el) {
            if (el == null || !string.Equals(el.TagName, "link", StringComparison.OrdinalIgnoreCase)) return false;
            if (el.HasAttribute("disabled")) return false;
            string rel = el.GetAttribute("rel");
            if (!RelContains(rel, "stylesheet")) return false;
            if (RelContains(rel, "alternate")) return false;
            return true;
        }

        static bool RelContains(string rel, string token) {
            if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrEmpty(token)) return false;
            var parts = rel.Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) {
                if (string.Equals(parts[i], token, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool MatchesMedia(string mediaText, MediaContext media) {
            if (string.IsNullOrWhiteSpace(mediaText)) return true;
            try {
                var list = MediaQueryParser.Parse(mediaText);
                return MediaQueryEvaluator.Evaluate(list, media);
            } catch (MediaQueryParseException) {
                UICssDiagnostics.Warn("html-link-stylesheet", "invalid link media query skipped: " + mediaText);
                return false;
            }
        }

        static bool IsRemoteHref(string href) {
            if (string.IsNullOrEmpty(href)) return false;
            if (href.StartsWith("//", StringComparison.Ordinal)) return true;
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
            int colon = href.IndexOf(':');
            if (colon <= 0) return false;
            for (int i = 0; i < colon; i++) {
                char c = href[i];
                bool schemeChar = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '+'
                    || c == '-'
                    || c == '.';
                if (!schemeChar) return false;
            }
            if (colon + 2 < href.Length && href[colon + 1] == '/' && href[colon + 2] == '/') return true;
            string scheme = href.Substring(0, colon);
            return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
        }

        static string ResolveHref(string href, string ownerPath) {
            try {
                if (Path.IsPathRooted(href)) return href;
                var dir = Path.GetDirectoryName(ownerPath);
                return string.IsNullOrEmpty(dir) ? href : Path.Combine(dir, href);
            } catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) {
                UICssDiagnostics.Warn("html-link-stylesheet",
                    "failed to resolve stylesheet link '" + href + "': " + ex.Message);
                return null;
            }
        }

        static string NormalizePath(string path) {
            if (string.IsNullOrEmpty(path)) return path;
            try {
                return Path.GetFullPath(path);
            } catch {
                return path;
            }
        }
    }
}
