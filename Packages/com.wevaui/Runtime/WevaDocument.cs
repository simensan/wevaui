using System.Collections.Generic;
using UnityEngine;
using Weva.Binding;
using Weva.Components;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Documents;
using Weva.Events;
using Weva.Layout;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;
using Weva.Reactive;
using Weva.Rendering;
using Weva.Rendering.URP;

namespace Weva {
    // The author-facing MonoBehaviour. Game devs attach this to a GameObject,
    // wire up TextAssets + an optional controller, and the rest of the
    // pipeline (parsing, cascade, layout, paint, hit-testing, bindings,
    // animation) wires itself in OnEnable().
    //
    // Lifecycle notes (Unity quirks worth knowing):
    //   - Awake runs once per object lifetime (pre-OnEnable, also pre-domain
    //     reload). We build only the things that survive: nothing — the
    //     full pipeline rebuilds in OnEnable so domain-reload-triggered
    //     re-initialization gets a clean slate.
    //   - OnEnable runs after every Awake, every domain reload, and every
    //     SetActive(true). All heavy work lives here.
    //   - OnDisable mirrors OnEnable; SetActive(false) tears the pipeline
    //     down so the InvalidationTracker stops listening to the Document.
    //   - OnValidate fires in the editor whenever the inspector mutates a
    //     SerializeField, including during domain reload. We schedule a
    //     deferred Rebuild to avoid touching engine APIs that aren't safe in
    //     OnValidate.
    public enum RendererBackendKind {
        // Pick IMGUI when not running under URP, the batched URP pass when present.
        Auto = 0,
        // Force the IMGUI fallback regardless of URP availability.
        IMGUI = 1,
        // Force the batched URP path. Requires UIBatchedRendererFeature in the renderer asset.
        URP = 2
    }

    /// <summary>
    /// The author-facing component: attach it to a GameObject, assign HTML +
    /// CSS <c>TextAsset</c>s in the inspector, and it parses, cascades, lays
    /// out, and paints the UI through the URP (or debug IMGUI) backend. Attach
    /// a controller (<see cref="SetController"/>) to bind <c>[UIBind]</c>
    /// values and <c>on-*</c> handlers. Hot-reloads HTML/CSS edits in play mode.
    /// </summary>
    [AddComponentMenu("Weva/UI Document")]
    [DisallowMultipleComponent]
    // ExecuteAlways: the document builds + registers its paint source in
    // EDIT mode too, so the Game view shows the UI without entering play
    // (gated per-document by `editModePreview` below). The URP feature's
    // Game-camera check already passes for the edit-mode Game view; the
    // missing piece was a registered source + a repaint pump.
    [ExecuteAlways]
    public sealed class WevaDocument : MonoBehaviour, IUIPaintSource, IRenderViewportAwarePaintSource {
        [SerializeField] TextAsset documentAsset;
        [SerializeField] TextAsset[] stylesheetAssets;
        [SerializeField] int sortingOrder;
        [SerializeField] Vector2 viewportOverride;
        [SerializeField] bool autoRebuildOnChange = true;
        // Render the document in the edit-mode Game view (no play mode
        // needed). Inspector edits, HTML/CSS hot-reload, and animations all
        // refresh via the editor pump below. Disable per-document if a
        // heavy page slows editor repaints.
        [SerializeField, Tooltip("Render this document in the Game view while NOT in play mode.")]
        bool editModePreview = true;
        [SerializeField] bool prefersDarkColorScheme;
        [SerializeField] RendererBackendKind rendererBackend = RendererBackendKind.Auto;
        // Default-on in the editor: stylesheet edits are picked up without
        // a domain reload via FileSystemWatcher. Disabled in player builds
        // by default to avoid surprising shipping games. Authors can flip
        // this via the inspector if they want runtime hot-reload (e.g. for
        // a debug build).
        [SerializeField] bool enableHotReload = true;
        [Tooltip("Console verbosity for Weva's engine diagnostics (unresolved fonts, missing emoji glyphs, unsupported CSS). Set to Off to silence them.")]
        [SerializeField] Weva.Diagnostics.WevaLogLevel diagnosticLogLevel = Weva.Diagnostics.WevaLogLevel.Warnings;
        // Build-time bake of <link rel="stylesheet"> contents (parallel
        // arrays: href as authored -> CSS text). Filled by the editor's
        // LinkedStylesheetBakeProcessor during player builds; players have
        // no disk/AssetDatabase so the runtime would otherwise drop every
        // linked sheet and render UA-only (glass.html build, 2026-06-06).
        // The builder consults this only when DocumentPath is unavailable,
        // so in the editor the live file always wins over the bake.
        [SerializeField, HideInInspector] string[] bakedLinkedStylesheetHrefs;
        [SerializeField, HideInInspector] string[] bakedLinkedStylesheetCss;
        // Build-time bake of <template src="..."> HTML (parallel arrays:
        // src href as authored -> template HTML text). Same player-build
        // rationale as the linked-stylesheet bake: ComponentTemplateImporter
        // reads template files relative to DocumentPath (editor-only),
        // so player builds must pre-capture the content at bake time.
        [SerializeField, HideInInspector] string[] bakedTemplateHrefs;
        [SerializeField, HideInInspector] string[] bakedTemplateHtml;

        public TextAsset DocumentAsset {
            get => documentAsset;
            set { documentAsset = value; if (autoRebuildOnChange && isActiveAndEnabled) Rebuild(); }
        }

        public TextAsset[] StylesheetAssets {
            get => stylesheetAssets;
            set { stylesheetAssets = value; if (autoRebuildOnChange && isActiveAndEnabled) Rebuild(); }
        }

        public int SortingOrder {
            get => sortingOrder;
            set => sortingOrder = value;
        }

        public Vector2 ViewportOverride {
            get => viewportOverride;
            set { viewportOverride = value; if (autoRebuildOnChange && isActiveAndEnabled) Rebuild(); }
        }

        public bool AutoRebuildOnChange {
            get => autoRebuildOnChange;
            set => autoRebuildOnChange = value;
        }

        public bool PrefersDarkColorScheme {
            get => prefersDarkColorScheme;
            set { prefersDarkColorScheme = value; if (autoRebuildOnChange && isActiveAndEnabled) Rebuild(); }
        }

        public RendererBackendKind RendererBackend {
            get => rendererBackend;
            set => rendererBackend = value;
        }

        public bool EnableHotReload {
            get => enableHotReload;
            set { enableHotReload = value; if (autoRebuildOnChange && isActiveAndEnabled) Rebuild(); }
        }

        /// <summary>Console verbosity for Weva's engine diagnostics (unresolved
        /// fonts, missing emoji glyphs, unsupported CSS). Set to <c>Off</c> to
        /// silence them. Applied process-globally (the most recently enabled
        /// document wins if several set different levels).</summary>
        public Weva.Diagnostics.WevaLogLevel DiagnosticLogLevel {
            get => diagnosticLogLevel;
            set { diagnosticLogLevel = value; Weva.Diagnostics.UICssDiagnostics.LogLevel = value; }
        }

        UIDocumentState state;
        UIDocumentState suspendedState;
        object controller;
        IImageRegistry imageRegistry;
        bool registered;
        Camera referenceCamera;
        Weva.HotReload.HotReloadCoordinator hotReload;
        Weva.HotReload.HtmlReloadCoordinator htmlReload;
        // Last viewport size we observed on Update(). Initialized to zero so the
        // first frame after OnEnable always reconciles against the freshly-built
        // pipeline (which already laid out using ResolveMediaContext's viewport).
        // If the Game View is resized (Play mode), Update() detects the delta
        // and triggers a lighter relayout — no full Rebuild() — by pushing a
        // new MediaContext + LayoutContext viewport and marking the doc root
        // dirty for layout/paint.
        Vector2 lastViewportSize;
        // Last Screen.safeArea we observed. Updates piped into
        // EnvironmentVariables so author stylesheets using
        // env(safe-area-inset-{top,right,bottom,left}) resolve to the current
        // device's notch/home-indicator insets. Initialized to a sentinel rect
        // that no real safe area will equal so the first Update reconciles
        // unconditionally.
        UnityEngine.Rect lastSafeArea = new UnityEngine.Rect(float.NaN, float.NaN, float.NaN, float.NaN);
        Vector2 lastRenderTargetViewportSize;

        public Dom.Document Doc => state?.Doc;
        public CascadeEngine Cascade => state?.Cascade;
        public LayoutEngine LayoutEngine => state?.LayoutEngine;
        public BoxToPaintConverter Painter => state?.Painter;
        public EventDispatcher Events => state?.Events;
        public InvalidationTracker Invalidation => state?.Invalidation;
        public ComponentRegistry Components => state?.Components;
        public CssAnimationRunner Animator => state?.Animator;
        public BindingSet Bindings => state?.Bindings;
        public InteractionStateProvider State => state?.State;
        public UIDocumentState CurrentState => state;

        public IImageRegistry ImageRegistry {
            get => imageRegistry;
            set {
                imageRegistry = value;
                if (state == null) return;
                state.ImageRegistry = value;
                if (state.LayoutEngine != null) state.LayoutEngine.ImageRegistry = value;
                if (state.Painter != null) state.Painter.ImageRegistry = value;
                if (state.Invalidation != null && state.Doc != null) {
                    state.Invalidation.MarkDirty(state.Doc, InvalidationKind.Layout | InvalidationKind.Paint);
                }
            }
        }

        public Camera ReferenceCamera {
            get => referenceCamera;
            set => referenceCamera = value;
        }

        public int Order => sortingOrder;

        // IUIPaintSource hook: the render pass skips BeginFrame/EmitPaint/EndFrame
        // when no source needs repaint, leaving the prior frame's batches in
        // the batcher to feed the GPU. We say repaint-needed when:
        //  * Lifecycle.Update flagged PaintInvalidated (tracker had dirty
        //    entries before clear, or a layout pass ran), OR
        //  * mid-frame mutations have re-dirtied the tracker after clear
        //    (controller events, scroll wheel, etc. fired between Update
        //    and EmitPaint), OR
        //  * we have not yet emitted any paint for this document — first
        //    frame must always render.
        public bool NeedsRepaint {
            get {
                if (state == null) return false;
                if (!state.HasEmittedPaint) return true;
                if (state.PaintInvalidated) return true;
                if (state.Invalidation != null && state.Invalidation.DirtyCount > 0) return true;
                if (GetImageRegistryVersion(state.ImageRegistry) != state.LastPaintedImageRegistryVersion) return true;
                return false;
            }
        }

        void Awake() {
        }

        void OnEnable() {
            if (!Application.isPlaying && !editModePreview) return;
            // Apply the inspector diagnostics setting before the pipeline builds
            // so build-time font/emoji/CSS warnings honor it.
            Weva.Diagnostics.UICssDiagnostics.LogLevel = diagnosticLogLevel;
#if UNITY_EDITOR
            if (!Weva.Text.Tmp.TmpFontAssetRegistry.IsRegistered("sans-serif")) {
                Weva.Text.Sdf.SdfBootstrap.EnsureFontsRegisteredInEditor();
            }
#endif
            if (!ResumePipeline()) {
                BuildPipeline();
            }
            if (state != null) {
                UIPaintSourceRegistry.Register(this);
                registered = true;
            }
            if (Application.isPlaying) {
                // Edit mode must NOT AddComponent — that dirties the scene
                // and would serialize the auto-attached bridge into it.
                EnsureInputController();
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) {
                UnityEditor.EditorApplication.update -= EditModePump;
                UnityEditor.EditorApplication.update += EditModePump;
                // Kick the first frame so the view isn't blank until the
                // next editor repaint.
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            }
#endif
        }

        void EnsureInputController() {
            // Auto-attach the input bridge so authors don't have to wire it
            // up manually. Wrapped in try/catch — if the Input System package
            // isn't present the type still resolves (it has no hard
            // UnityEngine.InputSystem references at the type level), but if
            // some other initialization fails we don't want to take the
            // whole document down with it.
            try {
                if (gameObject.GetComponent<Forms.Bridge.UnityInputController>() == null) {
                    gameObject.AddComponent<Forms.Bridge.UnityInputController>();
                }
            } catch (System.Exception ex) {
                Debug.LogWarning($"WevaDocument on '{name}' could not attach UnityInputController: {ex.Message}", this);
            }
        }

        void OnDisable() {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditModePump;
#endif
            if (registered) {
                UIPaintSourceRegistry.Unregister(this);
                registered = false;
            }
            SuspendPipeline();
        }

        void Update() {
            if (state == null) return;
            if (!Application.isPlaying && !editModePreview) return;
            // Detect Game View / Screen resize and re-run layout against the
            // new viewport. Runs before the lifecycle so the dirty mark we set
            // is consumed by this same frame's cascade + layout pass.
            if (autoRebuildOnChange) {
                DetectAndApplyViewportResize();
                DetectAndApplySafeAreaChange();
            }
            // HTML reload runs first so a coincident edit to both HTML and
            // CSS arrives at the cascade layer with the new DOM in place.
            if (htmlReload != null) {
                htmlReload.Tick(Time.unscaledTimeAsDouble);
            }
            if (hotReload != null) {
                hotReload.Tick(Time.unscaledTimeAsDouble);
            }
            SyncImageRegistryVersion();
            UIDocumentLifecycle.Update(state, controller, Time.unscaledTimeAsDouble);
        }

        void SyncImageRegistryVersion() {
            if (state == null || state.Invalidation == null || state.Doc == null) return;
            int version = GetImageRegistryVersion(state.ImageRegistry);
            if (version == state.LastObservedImageRegistryVersion) return;
            state.LastObservedImageRegistryVersion = version;
            state.Invalidation.MarkDirty(state.Doc, InvalidationKind.Layout | InvalidationKind.Paint);
        }

        public void PrepareForRenderViewport(int width, int height) {
            if (state == null || width <= 0 || height <= 0) return;
            if (viewportOverride.x > 0 && viewportOverride.y > 0) return;
            if (referenceCamera != null) return;

            var size = new Vector2(width, height);
            lastRenderTargetViewportSize = size;
            if (!ApplyViewportSize(size)) return;
            UIDocumentLifecycle.Update(state, controller, Time.unscaledTimeAsDouble);
        }

        // Compute the viewport the document should be laid out against right
        // now. The URP backend draws screen-space UI into the final target, so
        // the default must track the actual screen/backbuffer dimensions. A
        // caller that wants camera-rect sized UI can still opt in by setting
        // ReferenceCamera explicitly.
        Vector2 ResolveCurrentViewportSize() {
            if (viewportOverride.x > 0 && viewportOverride.y > 0) return viewportOverride;
            if (referenceCamera != null) return new Vector2(referenceCamera.pixelWidth, referenceCamera.pixelHeight);
            if (lastRenderTargetViewportSize.x > 0 && lastRenderTargetViewportSize.y > 0) return lastRenderTargetViewportSize;
            if (Screen.width > 0 && Screen.height > 0) return new Vector2(Screen.width, Screen.height);
            var cam = Camera.main;
            if (cam != null) return new Vector2(cam.pixelWidth, cam.pixelHeight);
            return new Vector2(
                (float)UIDocumentDefaults.DefaultViewportWidthPx,
                (float)UIDocumentDefaults.DefaultViewportHeightPx);
        }

        void DetectAndApplyViewportResize() {
            ApplyViewportSize(ResolveCurrentViewportSize());
        }

        bool ApplyViewportSize(Vector2 size) {
            if (size.x <= 0 || size.y <= 0) return false;
            if (size == lastViewportSize) return false;
            lastViewportSize = size;
            // Skip the first reconcile if state still matches what BuildPipeline
            // produced — the initial Rebuild already laid the doc out against
            // this size, so we'd just mark everything dirty for nothing.
            if (state.LayoutContext != null
                && System.Math.Abs(state.LayoutContext.ViewportWidthPx - size.x) < 0.5
                && System.Math.Abs(state.LayoutContext.ViewportHeightPx - size.y) < 0.5) {
                return false;
            }
            // Push the new viewport to the layout context and the cascade.
            // Only media-query styles need a cascade-version bump on resize.
            // Viewport units remain as CSS values and resolve against
            // LayoutContext during layout, so documents without @media rules
            // can avoid a full selector/cascade walk while resizing.
            var media = UIDocumentMediaContextBuilder.Build(size.x, size.y,
                UIDocumentDefaults.DefaultDpi, prefersDarkColorScheme);
            state.MediaContext = media;
            bool mediaAffectsCascade = state.Cascade != null
                && state.Cascade.SetMediaContextForViewportResize(media);
            if (state.LayoutContext != null) {
                state.LayoutContext.ViewportWidthPx = media.ViewportWidthPx;
                state.LayoutContext.ViewportHeightPx = media.ViewportHeightPx;
            }
            // Mark the document dirty so the lifecycle's incremental layout
            // gate reruns layout this frame. A single document-level mark is
            // enough: LayoutEngine detects the viewport-version change and
            // performs the full pass without needing every node in the DOM
            // entered into the per-frame dirty dictionary.
            if (state.Invalidation != null && state.Doc != null) {
                var kind = InvalidationKind.Layout | InvalidationKind.Paint;
                if (mediaAffectsCascade) kind |= InvalidationKind.Style;
                state.Invalidation.MarkDirty(state.Doc, kind);
            }
            return true;
        }

        // Pipe Unity's Screen.safeArea (rotation/notch obscuration) into
        // EnvironmentVariables so env(safe-area-inset-{top,right,bottom,left})
        // in author stylesheets resolves to the current device's insets.
        // The fields shift on orientation changes and on platforms that
        // re-report the area when the system UI shows/hides — poll each frame
        // because there's no Unity event for it.
        void DetectAndApplySafeAreaChange() {
            var sa = Screen.safeArea;
            // Bail when Screen reports nothing (some test contexts).
            if (Screen.width <= 0 || Screen.height <= 0) return;
            if (lastSafeArea.x == sa.x && lastSafeArea.y == sa.y
                && lastSafeArea.width == sa.width && lastSafeArea.height == sa.height) {
                return;
            }
            lastSafeArea = sa;
            // Unity's safeArea uses bottom-left origin; CSS insets are
            // distances from each respective edge of the screen.
            double top    = System.Math.Max(0, Screen.height - (sa.y + sa.height));
            double right  = System.Math.Max(0, Screen.width  - (sa.x + sa.width));
            double bottom = System.Math.Max(0, sa.y);
            double left   = System.Math.Max(0, sa.x);
            EnvironmentVariables.Register("safe-area-inset-top",    top.ToString("0.##",    System.Globalization.CultureInfo.InvariantCulture) + "px");
            EnvironmentVariables.Register("safe-area-inset-right",  right.ToString("0.##",  System.Globalization.CultureInfo.InvariantCulture) + "px");
            EnvironmentVariables.Register("safe-area-inset-bottom", bottom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px");
            EnvironmentVariables.Register("safe-area-inset-left",   left.ToString("0.##",   System.Globalization.CultureInfo.InvariantCulture) + "px");
            // Force the next cascade pass to miss its cache for every element
            // — env()-using declarations need to re-resolve. Doc-level dirty
            // mark plus the cascade-version bump together cover both the
            // cascade cache and the layout/paint cache key.
            if (state.Cascade != null) state.Cascade.BumpEnvironmentVersion();
            if (state.Invalidation != null && state.Doc != null) {
                state.Invalidation.MarkDirty(state.Doc,
                    InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
            }
        }

#if UNITY_EDITOR
        // Edit-mode repaint pump. The editor only ticks the player loop on
        // demand; without this the preview would stay frozen at its first
        // paint. Queue a loop update (which runs Update() on ExecuteAlways
        // components and renders the Game view) whenever the document says it
        // has something new to show — animated documents keep NeedsRepaint
        // true, so animations run live in edit mode at editor-update rate.
        double nextHotReloadPoll;
        void EditModePump() {
            if (this == null || Application.isPlaying) return;
            if (!editModePreview || state == null) return;
            bool queue = NeedsRepaint;
            // Hot-reload changes are CONSUMED inside Update() (the watcher
            // marks invalidation there), so a CSS/HTML edit can't set
            // NeedsRepaint until Update runs — which this pump gates. Break
            // the deadlock with a low-rate poll while hot reload is on: tick
            // the loop ~4×/s so pending file changes get processed, after
            // which NeedsRepaint drives the actual repaint burst.
            if (!queue && enableHotReload) {
                double now = UnityEditor.EditorApplication.timeSinceStartup;
                if (now >= nextHotReloadPoll) {
                    nextHotReloadPoll = now + 0.25;
                    queue = true;
                }
            }
            if (queue) {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        void OnValidate() {
            // In edit mode the refresh must run even when editModePreview was
            // just toggled OFF (to unregister + stop the pump), so the gate
            // can't early-out on !editModePreview here — DelayedRebuild
            // resolves the toggle's current value.
            if (Application.isPlaying && !autoRebuildOnChange) return;
            if (!isActiveAndEnabled) return;
            UnityEditor.EditorApplication.delayCall += DelayedRebuild;
        }

        void DelayedRebuild() {
            if (this == null) return;
            if (!isActiveAndEnabled) return;
            if (!Application.isPlaying) {
                // Full disable/enable-shaped cycle, not Rebuild(): a document
                // whose preview was OFF at OnEnable time never registered its
                // paint source or hooked the pump, so flipping the toggle ON
                // must do both — and flipping it OFF must undo both (plus one
                // repaint so the Game view doesn't freeze on the stale frame).
                TearDownPipeline();
                if (registered) {
                    UIPaintSourceRegistry.Unregister(this);
                    registered = false;
                }
                UnityEditor.EditorApplication.update -= EditModePump;
                if (editModePreview) {
                    BuildPipeline();
                    if (state != null) {
                        UIPaintSourceRegistry.Register(this);
                        registered = true;
                    }
                    UnityEditor.EditorApplication.update += EditModePump;
                }
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                return;
            }
            Rebuild();
        }
#endif

        /// <summary>Tear down and rebuild the whole pipeline (re-parse HTML/CSS,
        /// re-cascade, re-layout). Call after swapping the source assets; routine
        /// data changes should use the binding system instead.</summary>
        public void Rebuild() {
            // Scroll positions live in the pipeline (ScrollContainer keyed by
            // Box) and would silently die with it — the page snaps to the top
            // on every rebuild (hot reload, OnValidate's DelayedRebuild, asset
            // swaps). Capture them by DOM path before teardown; the lifecycle
            // restores them after the new pipeline's first layout. A rebuild
            // is a LIVE refresh of the same document, so keeping the user's
            // scroll matches hot-reload expectations (and Chrome's in-page
            // stylesheet swaps).
            var preservedScroll = CaptureScrollPositions();
            TearDownPipeline();
            BuildPipeline();
            if (preservedScroll != null && state != null) {
                state.PendingScrollRestores = preservedScroll;
            }
        }

        // Snapshot every meaningfully-scrolled container as (DOM path, x, y).
        // The path is the chain of child indexes from the document root — the
        // rebuilt DOM re-parses the same HTML, so identical paths land on the
        // corresponding elements.
        List<Documents.UIDocumentState.ScrollRestore> CaptureScrollPositions() {
            var sc = state?.LayoutEngine?.ScrollContainer;
            if (sc == null || sc.Count == 0) return null;
            List<Documents.UIDocumentState.ScrollRestore> captured = null;
            foreach (var kv in sc.All) {
                var st = kv.Value;
                var el = kv.Key?.Element;
                if (st == null || el == null) continue;
                if (st.ScrollX <= 0.5 && st.ScrollY <= 0.5) continue;
                var path = DomPathOf(el);
                if (path == null) continue;
                captured ??= new List<Documents.UIDocumentState.ScrollRestore>(2);
                captured.Add(new Documents.UIDocumentState.ScrollRestore {
                    Path = path, ScrollX = st.ScrollX, ScrollY = st.ScrollY,
                });
            }
            return captured;
        }

        static int[] DomPathOf(Dom.Node node) {
            var idx = new List<int>(8);
            for (var n = node; n != null && n.Parent != null; n = n.Parent) {
                int i = -1;
                var siblings = n.Parent.Children;
                for (int s = 0; s < siblings.Count; s++) {
                    if (ReferenceEquals(siblings[s], n)) { i = s; break; }
                }
                if (i < 0) return null;
                idx.Add(i);
            }
            idx.Reverse();
            return idx.ToArray();
        }

        /// <summary>Attach (or replace) the controller whose <c>[UIBind]</c>
        /// members feed <c>{{ }}</c> placeholders and whose methods resolve
        /// <c>on-*</c> handlers. Only re-scans bindings — does not re-cascade or
        /// re-layout.</summary>
        public void SetController(object newController) {
            controller = newController;
            if (state == null) return;
            // Replace the binding set without rebuilding the rest of the
            // pipeline — this matches the spec's SetController semantics
            // and avoids rerunning cascade/layout when only handlers change.
            state.Bindings?.Dispose();
            state.Bindings = BindingScanner.Scan(state.Doc, controller);
            LogBindingWarnings(state.Bindings);
            state.Bindings.Wire(state.Events);
            state.Bindings.AttachLive(state.Doc, controller);
            if (controller != null) {
                UIElementBinder.Populate(controller, state.Doc);
            }
        }

        /// <summary>The controller attached via <see cref="SetController"/>, cast
        /// to <typeparamref name="T"/> (null if none or the cast fails).</summary>
        public T GetController<T>() where T : class {
            return controller as T;
        }

        /// <summary>Find an element by its <c>id</c> attribute (null if absent or
        /// the document hasn't built yet).</summary>
        public Dom.Element GetElementById(string id) {
            return state?.Doc?.GetElementById(id);
        }

        /// <summary>Enumerate elements carrying <paramref name="className"/> in
        /// their <c>class</c> attribute (document order).</summary>
        public IEnumerable<Dom.Element> GetElementsByClassName(string className) {
            if (state?.Doc == null) yield break;
            foreach (var e in state.Doc.GetElementsByClassName(className)) yield return e;
        }

        /// <summary>Enumerate elements with the given tag name (document order).</summary>
        public IEnumerable<Dom.Element> GetElementsByTagName(string tagName) {
            if (state?.Doc == null) yield break;
            foreach (var e in state.Doc.GetElementsByTagName(tagName)) yield return e;
        }

        /// <summary>Mark an element's style (and downstream layout/paint) dirty so
        /// the next frame re-cascades it. Use after mutating an element's
        /// attributes/classes outside the binding system.</summary>
        public void MarkStyleDirty(Dom.Element e) {
            if (state?.Invalidation == null || e == null) return;
            state.Invalidation.MarkDirty(e, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
        }

        /// <summary>Mark an element's layout (and paint) dirty so the next frame
        /// re-lays-it-out without a full style recascade.</summary>
        public void MarkLayoutDirty(Dom.Element e) {
            if (state?.Invalidation == null || e == null) return;
            state.Invalidation.MarkDirty(e, InvalidationKind.Layout | InvalidationKind.Paint);
        }

        public void EmitPaint(IRenderBackend backend) {
            if (backend == null || state == null || state.RootBox == null || state.Painter == null) return;
            // Once we get here, paint conversion is committed for this frame —
            // clear the dirty flags so the next NeedsRepaint check starts
            // fresh. Do this BEFORE the actual conversion so a mutation that
            // fires DURING conversion (event handler triggered by hit test
            // mid-walk would be unusual but possible) still repaints next
            // frame.
            state.PaintInvalidated = false;
            state.HasEmittedPaint = true;
            state.LastPaintedImageRegistryVersion = GetImageRegistryVersion(state.ImageRegistry);
            // state.BoxLookup is a cached delegate built lazily once per
            // pipeline; previously this line built a fresh Func<Element, Box>
            // every EmitPaint (~64 B / frame).
            var lookup = state.BoxLookup;
            var sc = state.LayoutEngine?.ScrollContainer;
            var sp = state.Events?.StateProvider;
            // Pooling contract: the converter rents a PaintList and command
            // instances; the backend consumes them via Submit; we hand both back
            // via Return() so the next frame writes into the same memory.
            //
            // Caveat: backends that RETAIN command references after Submit (like
            // RecordingBackend used in editor previews and tests) must NOT be paired
            // with auto-Return — the post-Submit Reset() would zero out their
            // captured fields. RecordingBackend bypasses this by reading
            // command fields off PaintCommand subtypes synchronously, so the
            // Return below only happens for backends that consume commands fully
            // during Submit. The IMGUI / URP renderers fall into that category.
            // Wire the per-subtree snapshot sink so the batched backend can
            // hand completed snapshots back to the painter for next-frame
            // replay. Backend triggers the callback from Submit(EndSubtree-
            // CaptureCommand) once a capture window closes. Cleared after
            // the frame so other backends in mixed setups don't see stale
            // hooks.
            var batched = backend as BatchedURPRenderBackend;
            if (batched != null) {
                // Cached delegate — see UIDocumentState.SubtreeSnapshotSink.
                // Was a fresh delegate allocation per EmitPaint (~80 B / frame).
                batched.SubtreeSnapshotSink = state.SubtreeSnapshotSink;
                batched.ImageRegistry = state.ImageRegistry;
            }
            // content-visibility:auto's off-viewport skip needs the painter
            // to know the LIVE viewport (resizes included) — refreshed per
            // frame from the layout context; 0 disables the skip.
            if (state.LayoutContext != null) {
                state.Painter.ViewportWidth = state.LayoutContext.ViewportWidthPx;
                state.Painter.ViewportHeight = state.LayoutContext.ViewportHeightPx;
            }
            var list = state.Painter.Convert(state.RootBox, state.Invalidation, lookup, sc, sp);
            // try/finally so an exception thrown from a Submit (or PrepareText)
            // still returns the rented list to the pool and clears the snapshot
            // sink — without it one bad frame leaked the frame's commands AND
            // left the stale sink hook on the backend.
            try {
                if (batched != null) {
                    bool hadReplay = ContainsReplaySubtreeSnapshot(list);
                    bool textAtlasChanged = batched.PrepareText(list);
                    if (textAtlasChanged && hadReplay) {
                        state.Painter.Return(list);
                        state.Painter.InvalidateAll();
                        list = state.Painter.Convert(state.RootBox, state.Invalidation, lookup, sc, sp);
                        batched.PrepareText(list);
                    }
                }
                // B16: wire the path-clip coverage image registry AFTER Convert —
                // the painter creates it LAZILY on the first clip-path: path(...)
                // injection, so an assignment before Convert hands the batcher a
                // null on the very frame the first synthetic mask layer is
                // emitted (the coverage texture then never resolves and the GPU
                // clip silently no-ops).
                if (batched != null) {
                    batched.SyntheticImageRegistry = state.Painter?.SyntheticImageRegistry;
                }
                for (int i = 0; i < list.Commands.Count; i++) {
                    list.Commands[i].Submit(backend);
                }
            } finally {
                if (batched != null) batched.SubtreeSnapshotSink = null;
                // Skip auto-Return when the backend retains command references.
                // RecordingBackend stores commands in a list to be read after
                // EmitPaint returns; auto-resetting them would corrupt that
                // stored data. Caller (e.g. UIPreviewWindow) owns the list; it
                // falls through to the GC. The pool stays one frame "leaky"
                // for preview, which is acceptable since previews aren't on
                // the steady-state hot path.
                if (!(backend is RecordingBackend)) {
                    state.Painter.Return(list);
                }
            }
        }

        static bool ContainsReplaySubtreeSnapshot(PaintList list) {
            if (list == null) return false;
            var commands = list.Commands;
            for (int i = 0; i < commands.Count; i++) {
                if (commands[i] != null && commands[i].Kind == PaintCommandKind.ReplaySubtreeSnapshot) return true;
            }
            return false;
        }

        static int GetImageRegistryVersion(IImageRegistry registry) {
            return registry is Weva.Paint.Images.IVersionedImageRegistry versioned
                ? versioned.Version
                : 0;
        }

        void BuildPipeline() {
            if (documentAsset == null) {
                Debug.LogWarning($"WevaDocument on '{name}' has no document asset; remaining idle.", this);
                return;
            }
            var sources = new List<string>();
            var paths = new List<string>();
            if (stylesheetAssets != null) {
                for (int i = 0; i < stylesheetAssets.Length; i++) {
                    if (stylesheetAssets[i] == null) continue;
                    sources.Add(stylesheetAssets[i].text);
                    paths.Add(ResolveAssetAbsolutePath(stylesheetAssets[i]));
                }
            }
            var media = ResolveMediaContext();
            var docPath = ResolveAssetAbsolutePath(documentAsset);
            var builder = new UIDocumentBuilder {
                DocumentSource = documentAsset.text,
                DocumentPath = docPath,
                StylesheetSources = sources,
                StylesheetPaths = paths,
                Controller = controller,
                MediaContext = media,
                Clock = new UnityClock(),
                ImageRegistry = imageRegistry,
                BakedLinkedHrefs = bakedLinkedStylesheetHrefs,
                BakedLinkedCss = bakedLinkedStylesheetCss,
                BakedTemplateHrefs = bakedTemplateHrefs,
                BakedTemplateHtml = bakedTemplateHtml
            };
            try {
                state = builder.Build();
                LogBindingWarnings(state?.Bindings);
            } catch (System.Exception ex) {
                Debug.LogError($"WevaDocument on '{name}' failed to build: {ex.Message}", this);
                state = null;
            }

            if (state != null && enableHotReload && Application.isEditor) {
                AttachHotReload();
            }
        }

        void LogBindingWarnings(BindingSet bindings) {
            if (bindings == null) return;
            var warnings = bindings.Warnings;
            for (int i = 0; i < warnings.Count; i++) {
                Debug.LogWarning($"WevaDocument on '{name}': {warnings[i]}", this);
            }
        }

        void AttachHotReload() {
            try {
                var queue = new Weva.HotReload.CssReloadQueue();
                var watcher = new Weva.HotReload.CssWatcher(queue);
                if (state.StylesheetPaths != null) {
                    for (int i = 0; i < state.StylesheetPaths.Count; i++) {
                        var p = state.StylesheetPaths[i];
                        if (string.IsNullOrEmpty(p)) continue;
                        watcher.Watch(p);
                    }
                }
                state.CssReloadQueue = queue;
                state.CssWatcher = watcher;
                hotReload = new Weva.HotReload.HotReloadCoordinator(
                    state, queue, msg => Debug.Log(msg, this));
            } catch (System.Exception ex) {
                Debug.LogWarning($"WevaDocument on '{name}' could not enable hot reload: {ex.Message}", this);
            }

            try {
                if (!string.IsNullOrEmpty(state.DocumentPath)) {
                    var hq = new Weva.HotReload.HtmlReloadQueue();
                    var hw = new Weva.HotReload.HtmlWatcher(hq);
                    hw.Watch(state.DocumentPath);
                    if (state.ComponentTemplatePaths != null) {
                        for (int i = 0; i < state.ComponentTemplatePaths.Count; i++) {
                            hw.Watch(state.ComponentTemplatePaths[i]);
                        }
                    }
                    state.HtmlReloadQueue = hq;
                    state.HtmlWatcher = hw;
                    htmlReload = new Weva.HotReload.HtmlReloadCoordinator(
                        state, hq, msg => Debug.Log(msg, this));
                }
            } catch (System.Exception ex) {
                Debug.LogWarning($"WevaDocument on '{name}' could not enable HTML hot reload: {ex.Message}", this);
            }
        }

        // Returns an absolute on-disk path for a TextAsset reference, or
        // null if Unity's AssetDatabase isn't available (player builds) or
        // the asset isn't on disk (e.g. it was instantiated in code).
        string ResolveAssetAbsolutePath(TextAsset asset) {
#if UNITY_EDITOR
            if (asset == null) return null;
            string rel = UnityEditor.AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(rel)) return null;
            // AssetDatabase paths are relative to the project root (which
            // is the parent of Application.dataPath). Combining gives the
            // absolute path File.ReadAllText / FileSystemWatcher both want.
            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, rel));
#else
            return null;
#endif
        }

        void SuspendPipeline() {
            if (state == null) return;
            suspendedState = state;
            state = null;
        }

        bool ResumePipeline() {
            if (suspendedState == null) return false;
            state = suspendedState;
            suspendedState = null;
            state.Reset();
            if (state.Invalidation != null && state.Doc != null) {
                state.Invalidation.MarkDirty(state.Doc, InvalidationKind.Layout | InvalidationKind.Paint);
            }
            return true;
        }

        void TearDownPipeline() {
            lastViewportSize = Vector2.zero;
            suspendedState = null;
            if (state == null) return;
            state.CssWatcher?.Dispose();
            state.CssWatcher = null;
            state.CssReloadQueue = null;
            state.HtmlWatcher?.Dispose();
            state.HtmlWatcher = null;
            state.HtmlReloadQueue = null;
            hotReload = null;
            htmlReload = null;
            state.Invalidation?.Detach(state.Doc);
            state.Bindings?.Dispose();
            state.FormControls?.Dispose();
            state.ScrollEvents?.Dispose();
            // MS1: drop the dispatcher's Document.Mutated subscription so a
            // teardown / rebuild cycle doesn't double-subscribe and so the
            // dispatcher's listener map releases its element references for GC.
            state.Events?.Dispose();
            // MS2: release the animation runner's Document.Mutated
            // subscription and drop every element-keyed dictionary entry
            // so a teardown / rebuild cycle doesn't pin the previous
            // document's animated elements via the eight internal
            // dictionaries. Mirrors the dispatcher disposal immediately
            // above.
            state.Animator?.Dispose();
            // MS5: release the interaction-state provider's Document.Mutated
            // subscription and drop its `states` dictionary so the previous
            // document's focused / :target / hover-residual element refs
            // are released for GC. Mirrors the dispatcher / animator
            // disposals above.
            state.State?.Dispose();
            state = null;
        }

        Css.Media.MediaContext ResolveMediaContext() {
            var size = ResolveCurrentViewportSize();
            return UIDocumentMediaContextBuilder.Build(
                size.x, size.y,
                UIDocumentDefaults.DefaultDpi,
                prefersDarkColorScheme);
        }
    }
}
