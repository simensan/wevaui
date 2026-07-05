#if WEVA_URP
using System;
using UnityEditor;
using UnityEngine;
using Weva.Documents;
using Weva.Events;
using Weva.Rendering.URP;

namespace Weva.EditorTools.Panels {
    // Base class for custom Unity editor panels rendered by Weva (HTML/CSS) — the way UI
    // Toolkit lets you author an EditorWindow, but with the full CSS box/paint model and an
    // interactive event pipeline.
    //
    // Unlike the read-only preview window, this hosts a live UIDocumentState (built by the
    // headless, pure-C# UIDocumentBuilder) and pumps it every repaint via
    // UIDocumentLifecycle.Update. Because the dispatcher shares the cascade's
    // InteractionStateProvider, :hover / :active / :focus and click handlers actually work and
    // re-render. Pointer/keyboard events come from Event.current (the editor window's IMGUI),
    // translated into the same EventDispatcher the runtime uses.
    //
    // Render path (no Camera, no scene, no URP renderer feature — editor windows draw outside
    // any render pipeline, the same trick UI Toolkit's own renderer uses):
    //   UIDocumentLifecycle.Update → state.Painter.Convert(RootBox) → BatchedURPRenderBackend
    //     → BatchedSurfaceRenderer.Render → persistent RenderTexture → EditorGUI.DrawPreviewTexture
    //
    // Subclass and override Html (and optionally Controller for data/event binding to editor
    // state). Styling goes inline via style=, <style>, or <link> inside the document.
    //
    // ── STATUS: NOT YET VISUALLY VERIFIED IN A LIVE EDITOR. ────────────────────────────────
    // Confirm against a real render before trusting:
    //   1. Color space — the RT is linear (sRGB:false), matching the GPU goldens. Whether
    //      EditorGUI.DrawPreviewTexture composites that correctly over editor chrome (vs.
    //      needing a gamma RT / manual convert) is unconfirmed.
    //   2. Orientation — the Quad shader flips Y to CSS-top-left; the RT may blit upside-down
    //      in an EditorWindow and need a flipped draw rect. If text/boxes are mirrored
    //      vertically, that is the cause.
    //   3. DPI — the document lays out in GUI-point units (1 px == 1 point) so hit-testing
    //      matches mouse coords exactly; the RT is allocated in PHYSICAL pixels and the
    //      logical-coordinate geometry is rendered into it (see RenderInto), so SDF text is
    //      sampled per physical pixel and stays crisp on retina / fractional-scaled displays.
    //      Input stays in points; no pointer rescale is needed because layout is still in
    //      points. NOTE: this sharpens HiDPI upscaling, but small SDF text (~11px UI labels)
    //      is still inherently softer than OS-hinted bitmap chrome — that is the SDF path, not
    //      a DPI issue.
    public abstract class WevaEditorPanel : EditorWindow {
        // The HTML document this panel renders. Re-read every repaint; when it changes, the
        // document is rebuilt. Make it a function of editor state to get a reactive panel.
        protected abstract string Html { get; }

        // Optional controller object for data/event bindings (UIDocumentBuilder.Controller).
        // null in v1 panels; this is the hook for binding panels to editor state.
        protected virtual object Controller => null;

        // Background the panel clears to before painting the document (matches the editor skin).
        protected virtual Color ClearColor =>
            EditorGUIUtility.isProSkin ? new Color(0.18f, 0.18f, 0.19f, 1f)
                                       : new Color(0.86f, 0.86f, 0.87f, 1f);

        // Subclasses may draw IMGUI chrome (toolbar, etc.) above the document surface.
        protected virtual void OnPanelChrome() { }

        UIDocumentState state;
        string builtHtml;
        int builtW, builtH;
        bool builtDark;

        // Look up the laid-out box for an element in the live document. NOTE: Box.X/Y are
        // PARENT-LOCAL (relative to the containing block), NOT absolute viewport coords — to
        // compare against PointerEvent.X/Y (which are absolute) a caller must accumulate the
        // offsets up Box.Parent and subtract each scroll container's ScrollX/ScrollY (the same
        // transform paint/hit-test apply). Null if not laid out.
        protected Weva.Layout.Boxes.Box BoxFor(Weva.Dom.Element el) =>
            (el != null && state != null) ? state.BoxLookup?.Invoke(el) : null;

        BatchedSurfaceRenderer surface;
        RenderTexture rt;
        int rtW, rtH;
        string lastError;

        // Atlas warm-up window (editor time). The SDF glyph atlas is built
        // lazily: the first TryShape for a not-yet-rasterized glyph can fail
        // (the atlas repack returns zero glyphs that frame) and the text run
        // falls to the invisible solid-rect fallback. The live WevaDocument
        // hides this because it repaints every editor frame, so the atlas warms
        // within a few frames. This on-demand panel would otherwise get stuck
        // on the cold frame showing no text — so after every document (re)build
        // we keep repainting until this deadline, letting the atlas populate.
        double warmupUntilTime;
        const double WarmupSeconds = 2.0;

        protected virtual void OnEnable() {
            surface = new BatchedSurfaceRenderer();
            wantsMouseMove = true; // hover needs MouseMove delivery
            // Mirror WevaDocument.OnEnable: a domain reload wipes the editor TMP
            // font registry, so re-register before the first paint or the glyph
            // atlas has no face and ALL text falls to the (invisible) fallback.
            Weva.Text.Sdf.SdfBootstrap.EnsureFontsRegisteredInEditor();
            EditorApplication.update += OnEditorTick;
        }

        protected virtual void OnDisable() {
            EditorApplication.update -= OnEditorTick;
            TearDownDoc();
            surface?.Dispose();
            surface = null;
            ReleaseRt();
        }

        // Drive continuous repaint while animations are live OR the glyph atlas
        // is still warming after a rebuild, so an idle panel doesn't spin the
        // editor but text reliably appears. Input events trigger their own Repaint.
        void OnEditorTick() {
            bool animating = state?.Animator != null && state.Animator.HasActiveCompositions;
            bool warming = EditorApplication.timeSinceStartup < warmupUntilTime;
            if (animating || warming) Repaint();
        }

        void OnGUI() {
            OnPanelChrome();

            // The document fills whatever space remains under the chrome.
            var area = GUILayoutUtility.GetRect(0, 100000, 0, 100000);

            // EventType.Layout passes report a placeholder (0-size) rect in IMGUI's two-pass
            // model; skip it so we never build the document at a bogus size.
            if (Event.current.type == EventType.Layout) return;

            HandleInput(area);

            if (Event.current.type == EventType.Repaint) {
                RenderInto(area);
                if (rt != null) {
                    // Pixel-exact 1:1 blit: integer-aligned origin, destination sized to
                    // exactly the RT's device pixels (rt.width/ppp points). This makes the
                    // RT's pixel grid coincide with the screen grid, so the engine's glyph
                    // pixel-snap (SdfTextRendering.ReplaySnapshot → PixelSnapDelta) actually
                    // lands hinted coverage on whole screen pixels instead of being shifted
                    // off-grid by a fractional StretchToFill of `area`.
                    float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
                    var dst = new Rect(Mathf.Round(area.x), Mathf.Round(area.y),
                                       rt.width / ppp, rt.height / ppp);
                    EditorGUI.DrawPreviewTexture(dst, rt, null, ScaleMode.StretchToFill);
                }
            }

            if (!string.IsNullOrEmpty(lastError)) {
                var msg = new Rect(area.x + 8, area.y + 8, Mathf.Max(0, area.width - 16), 38);
                EditorGUI.HelpBox(msg, lastError, MessageType.Warning);
            }
        }

        void RenderInto(Rect area) {
            // HiDPI: lay the document out in GUI POINTS (1 CSS px == 1 point) so
            // hit-testing still matches Event.current.mousePosition exactly — but
            // allocate the RenderTexture in PHYSICAL pixels (× pixelsPerPoint) and
            // render the logical-coordinate geometry into it. The Quad shader maps
            // px → NDC from _WevaViewport (the LOGICAL w/h passed to Render), and the
            // GPU viewport is the full physical target, so the geometry fills the RT
            // and SDF text is sampled per physical pixel. DrawPreviewTexture then
            // draws that physical RT 1:1 (area_points × ppp == physical), giving crisp
            // text on retina / fractional-scaled displays instead of point-res
            // rasterization bilinear-upscaled to the window. At 100% (ppp == 1) this
            // is identical to the old path.
            float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            int w = Mathf.Max(1, Mathf.RoundToInt(area.width));        // logical points
            int h = Mathf.Max(1, Mathf.RoundToInt(area.height));
            int pw = Mathf.Max(1, Mathf.RoundToInt(area.width  * ppp)); // physical pixels
            int ph = Mathf.Max(1, Mathf.RoundToInt(area.height * ppp));
            EnsureRt(pw, ph);
            EnsureDoc(w, h);
            if (state == null) return;

            if (!surface.IsReady) {
                lastError = "Hidden/Weva/Quad shader not found — add it to Project Settings ▸ " +
                            "Graphics ▸ Always Included Shaders so it ships to the editor.";
                return;
            }
            lastError = null;

            UIDocumentLifecycle.Update(state, Controller, EditorApplication.timeSinceStartup);

            // Suppress ATG's hinted small-text COVERAGE for the panel's paint: those
            // bitmaps only render correctly pixel-exact, and this host draws through a
            // resampled RenderTexture that ruins the hinting on small chrome text
            // (uneven strokes / baseline jitter / clipping). Falling to SDFAA is
            // uniform (slightly soft) instead of broken. Scoped to this synchronous
            // render so play-mode WevaDocuments keep crisp coverage drawn to screen.
#if UNITY_2023_1_OR_NEWER
            // Crisp hinted chrome: coverage ON + per-glyph pixel-snap (each letter's
            // origin/baseline rounded to whole pixels), and the 1:1 pixel-exact blit
            // below so the RT grid == screen grid. Together these land hinted coverage
            // bitmaps on whole screen pixels. If still not crisp, revert to suppress=true.
            bool prevSuppressCoverage = Weva.Text.Atg.AtgGlyphAtlasAdapter.SuppressSmallTextCoverage;
            Weva.Text.Atg.AtgGlyphAtlasAdapter.SuppressSmallTextCoverage = false;
            bool prevSnapGlyphs = Weva.Rendering.URP.SdfTextRendering.SnapGlyphsToIntegerGrid;
            Weva.Rendering.URP.SdfTextRendering.SnapGlyphsToIntegerGrid = true;
            try {
#endif
            // Record the document's paint into a fresh batched backend, then GPU-render it into
            // the panel's RenderTexture. A new backend per frame mirrors the proven GpuGoldenRunner
            // path; pooling it is a later perf pass.
            var backend = new BatchedURPRenderBackend();
            backend.BeginFrame();
            if (state.RootBox != null) {
                var list = state.Painter.Convert(state.RootBox);
                // CRITICAL: pre-rasterize every glyph in this frame's runs into
                // the SDF atlas BEFORE submitting. EmitGlyphs' TryShape returns
                // false for a glyph not yet in the atlas and the run falls to the
                // INVISIBLE solid-rect fallback; PrepareText does one atlas
                // repack up-front so the subsequent Submit/EmitGlyphs shapes
                // real text. The live URP backend does this same prepare pass
                // before paint — omitting it was the cause of blank panel text.
                // If the prepare changed the atlas (cold glyphs ingested), keep
                // the warm-up window open so a follow-up repaint draws them even
                // if the repack landed too late to shape this exact frame.
                if (backend.PrepareText(list)) {
                    warmupUntilTime = EditorApplication.timeSinceStartup + WarmupSeconds;
                }
                for (int i = 0; i < list.Commands.Count; i++) list.Commands[i].Submit(backend);
            }
            backend.EndFrame();
            // Color space: EditorGUI.DrawPreviewTexture re-encodes linear→sRGB on
            // display in a LINEAR project, so the surface must hold LINEAR premul
            // there (srgbEncodeTarget:false) or the panel double-encodes and looks
            // washed out. In a GAMMA project the GUI shows the bytes 1:1, so the
            // surface must hold sRGB-encoded bytes (srgbEncodeTarget:true).
            bool gammaProject = QualitySettings.activeColorSpace == ColorSpace.Gamma;
            surface.Render(backend, rt, w, h, ClearColor, srgbEncodeTarget: gammaProject);
#if UNITY_2023_1_OR_NEWER
            } finally {
                Weva.Text.Atg.AtgGlyphAtlasAdapter.SuppressSmallTextCoverage = prevSuppressCoverage;
                Weva.Rendering.URP.SdfTextRendering.SnapGlyphsToIntegerGrid = prevSnapGlyphs;
            }
#endif
        }

        // ── Document lifecycle ────────────────────────────────────────────────────────────

        void EnsureDoc(int w, int h) {
            string html = Html ?? string.Empty;
            bool dark = EditorGUIUtility.isProSkin;
            if (state != null && builtHtml == html && builtW == w && builtH == h && builtDark == dark) {
                return;
            }
            // v1 rebuilds wholesale on any size/skin/source change. Re-parsing a small panel
            // document is cheap; a fluid in-place relayout on resize (without re-parse) is a
            // later optimization, blocked on cross-assembly access to UIDocumentState's
            // internal MediaContext setter.
            TearDownDoc();
            var media = UIDocumentMediaContextBuilder.Build(w, h, UIDocumentDefaults.DefaultDpi, dark);
            var builder = new UIDocumentBuilder {
                DocumentSource = html,
                MediaContext = media,
                Controller = Controller,
            };
            try {
                state = builder.Build();
                // Disable text-subtree snapshot replay: it expects the backend's snapshot sink to
                // be wired (UIDocument.EmitPaint does this). The panel converts the full tree each
                // frame instead, so turning replay off keeps the converter self-contained.
                if (state.Painter != null) state.Painter.AllowTextSubtreeSnapshotReplay = false;
                builtHtml = html;
                builtW = w;
                builtH = h;
                builtDark = dark;
                // Arm the atlas warm-up: keep repainting for a short window so the
                // glyph atlas finishes rasterizing this document's runs (see
                // warmupUntilTime). Without it, text that missed the cold-atlas
                // first frame stays invisible until some other repaint trigger.
                warmupUntilTime = EditorApplication.timeSinceStartup + WarmupSeconds;
            } catch (Exception ex) {
                lastError = ex.GetType().Name + ": " + ex.Message;
                state = null;
            }
        }

        void TearDownDoc() {
            if (state == null) return;
            // Mirror UIDocument.TearDownPipeline so a rebuild doesn't leak Document.Mutated
            // subscriptions or pin elements via listener/animation/state dictionaries.
            state.Invalidation?.Detach(state.Doc);
            state.Bindings?.Dispose();
            state.FormControls?.Dispose();
            state.ScrollEvents?.Dispose();
            state.Events?.Dispose();
            state.Animator?.Dispose();
            state.State?.Dispose();
            state = null;
            builtHtml = null;
        }

        // ── Input bridge: Event.current → EventDispatcher ───────────────────────────────────

        void HandleInput(Rect area) {
            var dispatcher = state?.Events;
            if (dispatcher == null) return;

            var e = Event.current;
            double x = e.mousePosition.x - area.x;
            double y = e.mousePosition.y - area.y;
            var mods = ToModifiers(e);

            switch (e.type) {
                case EventType.MouseMove:
                    // Hover — no button held. Report 0 so drag controllers don't mistake a
                    // plain move for a drag.
                    dispatcher.DispatchPointerMove(x, y, mods, 0);
                    Repaint();
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    // A button is held (IMGUI only emits MouseDrag while pressed). Report a
                    // non-zero mask so PointerEvent.Buttons stays accurate even though the
                    // panel may rebuild this dispatcher between drag frames.
                    dispatcher.DispatchPointerMove(x, y, mods, 1 << ToDomButton(e.button));
                    Repaint();
                    e.Use();
                    break;
                case EventType.MouseDown:
                    if (!area.Contains(e.mousePosition)) break;
                    dispatcher.DispatchPointerDown(x, y, ToDomButton(e.button), mods);
                    Repaint();
                    e.Use();
                    break;
                case EventType.MouseUp:
                    dispatcher.DispatchPointerUp(x, y, ToDomButton(e.button), mods);
                    Repaint();
                    e.Use();
                    break;
                case EventType.ScrollWheel:
                    if (!area.Contains(e.mousePosition)) break;
                    dispatcher.DispatchWheel(x, y, e.delta.x, e.delta.y, WheelDeltaMode.Line, mods);
                    Repaint();
                    e.Use();
                    break;
                case EventType.KeyDown:
                    dispatcher.DispatchKeyDown(KeyName(e), e.keyCode.ToString(), mods, false);
                    Repaint();
                    e.Use();
                    break;
                case EventType.KeyUp:
                    dispatcher.DispatchKeyUp(KeyName(e), e.keyCode.ToString(), mods, false);
                    Repaint();
                    e.Use();
                    break;
            }
        }

        static KeyModifiers ToModifiers(Event e) {
            var m = KeyModifiers.None;
            if (e.shift) m |= KeyModifiers.Shift;
            if (e.control) m |= KeyModifiers.Ctrl;
            if (e.alt) m |= KeyModifiers.Alt;
            if (e.command) m |= KeyModifiers.Meta;
            return m;
        }

        // Unity Event.button: 0=left, 1=right, 2=middle. DOM MouseEvent.button: 0=left,
        // 1=middle, 2=right. Remap the swapped middle/right so :active and click default
        // actions key off the right button index.
        static int ToDomButton(int unityButton) {
            switch (unityButton) {
                case 1: return 2; // Unity right  → DOM right
                case 2: return 1; // Unity middle → DOM middle
                default: return 0;
            }
        }

        // Best-effort DOM KeyboardEvent.key mapping. Covers the keys the dispatcher acts on
        // (Tab focus advance) plus common navigation/editing keys; falls back to the typed
        // character or the KeyCode name. Spec-complete key/code tables are a later refinement.
        static string KeyName(Event e) {
            switch (e.keyCode) {
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return "Enter";
                case KeyCode.Tab: return "Tab";
                case KeyCode.Backspace: return "Backspace";
                case KeyCode.Delete: return "Delete";
                case KeyCode.Escape: return "Escape";
                case KeyCode.LeftArrow: return "ArrowLeft";
                case KeyCode.RightArrow: return "ArrowRight";
                case KeyCode.UpArrow: return "ArrowUp";
                case KeyCode.DownArrow: return "ArrowDown";
                case KeyCode.Home: return "Home";
                case KeyCode.End: return "End";
                case KeyCode.PageUp: return "PageUp";
                case KeyCode.PageDown: return "PageDown";
                case KeyCode.Space: return " ";
            }
            if (e.character >= ' ') return e.character.ToString();
            return e.keyCode.ToString();
        }

        // ── RenderTexture lifecycle ─────────────────────────────────────────────────────────

        void EnsureRt(int w, int h) {
            if (rt != null && rtW == w && rtH == h) return;
            ReleaseRt();
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0) {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
            };
            rt = new RenderTexture(desc) { hideFlags = HideFlags.HideAndDontSave };
            // Point filtering: the blit is 1:1 (see OnGUI), so each screen pixel maps to
            // exactly one RT texel — Point keeps hinted glyphs crisp; Bilinear would
            // re-soften them if the blit is ever a hair off.
            rt.filterMode = FilterMode.Point;
            rt.Create();
            rtW = w;
            rtH = h;
        }

        void ReleaseRt() {
            if (rt == null) return;
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
            rtW = rtH = 0;
        }
    }
}
#endif
