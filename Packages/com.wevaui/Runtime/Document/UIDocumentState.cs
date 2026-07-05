using System.Collections.Generic;
using Weva.Binding;
using Weva.Components;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using Weva.Layout.Text;
using Weva.ViewTransitions;
using Weva.Paint.Conversion;
using Weva.Paint.Images;
using Weva.Reactive;

namespace Weva.Documents {
    // The bag of all layer instances produced for a single WevaDocument. Held
    // alongside the MonoBehaviour at runtime; reused by tests headlessly.
    //
    // Ownership model: every instance in this object lives for one rebuild
    // cycle. WevaDocument.OnEnable() and Rebuild() construct a fresh state
    // through UIDocumentBuilder; the previous state is detached/disposed
    // before the new one wires up. The InvalidationTracker is the only
    // member that the orchestrator owns separately and re-attaches on each
    // build (single tracker per WevaDocument lifetime).
    public sealed class UIDocumentState {
        public Weva.Dom.Document Doc { get; internal set; }
        public List<OriginatedStylesheet> AuthorStylesheets { get; internal set; }
        public List<Stylesheet> KeyframeStylesheets { get; internal set; }
        public ComponentRegistry Components { get; internal set; }
        public CascadeEngine Cascade { get; internal set; }
        public LayoutEngine LayoutEngine { get; internal set; }
        public BoxToPaintConverter Painter { get; internal set; }
        public IImageRegistry ImageRegistry { get; internal set; }
        public CssAnimationRunner Animator { get; internal set; }
        public InteractionStateProvider State { get; internal set; }
        public IFontMetrics FontMetrics { get; internal set; }
        public LayoutContext LayoutContext { get; internal set; }
        public MediaContext MediaContext { get; internal set; }

        // Built lazily on first Update() so the tracker can be attached to Doc
        // before any DOM mutation occurs. The OnEnable path constructs Doc
        // before binding events, then attaches the tracker; everything in
        // between routes through UIDocumentLifecycle.
        public InvalidationTracker Invalidation { get; internal set; }
        public EventDispatcher Events { get; internal set; }
        public SwappableHitTester HitTester { get; internal set; }
        public BindingSet Bindings { get; internal set; }
        public ElementToBoxIndex ElementToBox { get; internal set; }
        public ScrollEventHandler ScrollEvents { get; internal set; }
        public SmoothScrollAnimator SmoothScroll { get; internal set; }
        // Inertial flick scrolling. Fed drag samples by ScrollEvents'
        // pointer path; glides are integrated per-frame by the lifecycle
        // (step 1b alongside SmoothScroll, which also handles its snap
        // landing animation).
        public ScrollMomentum Momentum { get; internal set; }

        // Per-element form controllers wired automatically by FormControlsRegistry
        // so authors don't have to instantiate InputController themselves. Keyed
        // by Element so the registry can update on DOM mutation. Disposed in
        // teardown alongside Bindings.
        public FormControlsRegistry FormControls { get; internal set; }

        // Cached Func<Element, ComputedStyle> that wraps Cascade.GetComposedStyle
        // with this state's interaction-state provider. UIDocumentLifecycle.RunLayout
        // used to materialise a fresh closure delegate per call —
        // `e => state.Cascade.GetComposedStyle(e, styleProvider)` — which
        // allocated a new captures-class + delegate every layout pass (60Hz
        // on animated docs). Built lazily on first access; the captured
        // Cascade / State references mutate via internal setters during
        // setup, so we defer construction until both are bound.
        System.Func<Weva.Dom.Element, ComputedStyle> styleOfDelegate;
        public System.Func<Weva.Dom.Element, ComputedStyle> StyleOf {
            get {
                if (styleOfDelegate == null && Cascade != null) {
                    styleOfDelegate = ElementToComposedStyle;
                }
                return styleOfDelegate;
            }
        }
        ComputedStyle ElementToComposedStyle(Weva.Dom.Element e) {
            return Cascade.GetComposedStyle(e, State);
        }

        // Cached Func<Element, Box> for ElementToBox.Lookup. EmitPaint
        // previously created a fresh delegate every frame via
        // `(Func<Element, Box>)index.Lookup` — instance method group
        // conversions are NOT cached by the C# compiler, so this allocated
        // ~64 B per frame even in a fully idle document. Build lazily once
        // ElementToBox is bound; the underlying Lookup target is stable
        // across rebuilds because the ElementToBoxIndex instance is reused.
        System.Func<Weva.Dom.Element, Weva.Layout.Boxes.Box> boxLookupDelegate;
        public System.Func<Weva.Dom.Element, Weva.Layout.Boxes.Box> BoxLookup {
            get {
                if (boxLookupDelegate == null && ElementToBox != null) {
                    boxLookupDelegate = ElementToBox.Lookup;
                }
                return boxLookupDelegate;
            }
        }

        // Cached Action<Box, BoxBatchSnapshot> wired to Painter.RegisterSubtreeSnapshot.
        // WevaDocument.EmitPaint used to assign a fresh delegate to
        // backend.SubtreeSnapshotSink every frame — ~80 B per call. Cached
        // once Painter is bound.
        System.Action<Weva.Layout.Boxes.Box, Weva.Paint.IBoxBatchSnapshot> subtreeSnapshotSinkDelegate;
        public System.Action<Weva.Layout.Boxes.Box, Weva.Paint.IBoxBatchSnapshot> SubtreeSnapshotSink {
            get {
                if (subtreeSnapshotSinkDelegate == null && Painter != null) {
                    subtreeSnapshotSinkDelegate = (box, snap) => Painter.RegisterSubtreeSnapshot(box, snap);
                }
                return subtreeSnapshotSinkDelegate;
            }
        }
        public SnapResolver SnapResolver { get; internal set; }
        public ViewTransitionEngine ViewTransitions { get; internal set; }

        // Hot reload metadata. StylesheetPaths is parallel to the *author*
        // stylesheet portion of AuthorStylesheets — i.e. it indexes the
        // sheets that the user supplied via UIDocumentBuilder.StylesheetSources,
        // NOT the UA / form-control sheets prepended to the front of the
        // cascade list. Each non-null entry is the absolute file path the
        // sheet was loaded from. Null entries (or a null list) mean the
        // corresponding sheet was loaded from an in-memory string and
        // cannot be hot-reloaded. The HotReloadCoordinator reads this to map
        // FileSystemWatcher events back to a slot in AuthorStylesheets.
        public List<string> StylesheetPaths { get; internal set; }

        // The clock the builder used; the coordinator passes this to a
        // freshly-constructed CssAnimationRunner on hot-reload so timing
        // remains consistent.
        public IUIClock Clock { get; internal set; }

        // Caret blink phase, recomputed each UIDocumentLifecycle.Update from the
        // tick time. The paint hook (InputCaretOf) reads CaretBlinkOn to hide the
        // caret during the off phase; LastCaretBlinkOn tracks the last phase the
        // lifecycle repainted on, so it only marks the focused input dirty on a
        // flip (≈2 repaints/sec while focused, none otherwise).
        public bool CaretBlinkOn { get; internal set; } = true;
        internal bool LastCaretBlinkOn = true;
        // Input/selection audit #3: the blink clock is anchored to the last
        // caret activity (keystroke / caret move / selection change) so the
        // caret is SOLID while the user types, like Chrome. The controllers
        // raise the pending flag (they don't know the frame clock); the
        // lifecycle stamps it with the tick time on the next Update. NaN =
        // no activity yet (free-running phase from the raw tick time).
        internal bool CaretActivityPending;
        internal double CaretActivitySeconds = double.NaN;

        // Scroll positions captured across a pipeline rebuild (WevaDocument.
        // Rebuild). The pipeline owns scroll state (ScrollContainer keyed by
        // Box), so a rebuild would otherwise snap every scroller to the top.
        // Keyed by DOM path (child indexes from the root) because the rebuild
        // re-parses the document — element references don't survive.
        // Consumed (and cleared) by UIDocumentLifecycle.RunLayout after the
        // new pipeline's first layout, once boxes and MaxScroll exist.
        public struct ScrollRestore {
            public int[] Path;
            public double ScrollX;
            public double ScrollY;
        }
        internal System.Collections.Generic.List<ScrollRestore> PendingScrollRestores;

        // Optional, attached when WevaDocument has EnableHotReload toggled on.
        // Disposed alongside the rest of the state during teardown.
        public Weva.HotReload.CssWatcher CssWatcher { get; internal set; }
        public Weva.HotReload.CssReloadQueue CssReloadQueue { get; internal set; }

        // HTML hot-reload bookkeeping. DocumentPath is the absolute path the
        // HTML source was loaded from (or null for in-memory sources). The
        // watcher and queue mirror the CSS pair and are disposed during
        // teardown.
        public string DocumentPath { get; internal set; }
        public List<string> ComponentTemplatePaths { get; internal set; }
        public Weva.HotReload.HtmlWatcher HtmlWatcher { get; internal set; }
        public Weva.HotReload.HtmlReloadQueue HtmlReloadQueue { get; internal set; }

        // Last value passed to UIDocumentLifecycle.Update. Initialized to NaN so
        // the first Update treats dt as 0 and subsequent calls produce a real
        // delta. Set explicitly by the orchestrator; tests can also seed it.
        public double LastTickSeconds { get; internal set; } = double.NaN;

        // Latest laid-out box tree. Null until the first layout pass; valid
        // afterward as long as the orchestrator has not disposed this state.
        public Box RootBox { get; internal set; }

        public bool HasLayout => RootBox != null;

        // Set to true at the end of UIDocumentLifecycle.Update when anything
        // could have changed the paint output (tracker had dirty entries OR a
        // layout pass actually ran). Consumed by WevaDocument.EmitPaint: if the
        // flag is false AND paint has emitted at least once, the render pass
        // can skip the paint conversion entirely and let the prior frame's
        // batches feed the GPU verbatim.
        public bool PaintInvalidated { get; internal set; } = true;
        public int LastObservedImageRegistryVersion { get; internal set; } = int.MinValue;
        public int LastPaintedImageRegistryVersion { get; internal set; } = int.MinValue;

        // True once EmitPaint has produced batches at least once for this
        // document. Combined with !PaintInvalidated this lets the render pass
        // safely skip — the first frame always paints regardless.
        public bool HasEmittedPaint { get; internal set; }

        public void Reset() {
            RootBox = null;
            ElementToBox?.Clear();
            PaintInvalidated = true;
            HasEmittedPaint = false;
            LastObservedImageRegistryVersion = int.MinValue;
            LastPaintedImageRegistryVersion = int.MinValue;
        }
    }
}
