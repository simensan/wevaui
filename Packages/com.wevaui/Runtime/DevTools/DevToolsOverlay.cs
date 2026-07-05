using System.Collections.Generic;
using UnityEngine;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.DevTools {
    // Toggleable diagnostic overlay rendered via IMGUI on top of whatever
    // backend the host WevaDocument paints with. Lives outside the main paint
    // pipeline (per the v1 simplification in the spec) so even if the overlay
    // crashes it can never corrupt PaintList state. The overlay attaches to
    // exactly one WevaDocument — the one on its GameObject — and queries it for
    // the laid-out box tree, the InvalidationTracker, the hit tester, and the
    // paint cache stats every frame.
    [AddComponentMenu("Weva/DevTools Overlay")]
    [DisallowMultipleComponent]
    public sealed class DevToolsOverlay : MonoBehaviour {
        [SerializeField] bool enabledOverlay = false;
        // Independent toggle for the FPS / perf readout box. Lets you keep
        // the rest of the overlay (outlines, dirty tracking, hover) running
        // while hiding the top-left perf chip — useful when grabbing
        // screenshots where the FPS number would clutter the frame. Gates
        // `DrawPerf` below; the `OverlayMode.Performance` bit must ALSO be
        // set in `mode` for the perf chip to draw (this toggle can hide
        // it, not force it on).
        [SerializeField] bool enabledFps = false;
        [SerializeField] KeyCode toggleKey = KeyCode.F12;
        [SerializeField] OverlayMode mode = OverlayMode.All;
        // Empty / null = draw every box. Non-empty = scope outlines to boxes
        // whose Element.ClassName contains this substring, plus all of their
        // descendants. Cuts overlay overload to one subtree of interest.
        [SerializeField] string outlineMatchClassContains = "";

        public bool Enabled {
            get => enabledOverlay;
            set => enabledOverlay = value;
        }

        public bool EnabledFps {
            get => enabledFps;
            set => enabledFps = value;
        }

        public KeyCode ToggleKey {
            get => toggleKey;
            set => toggleKey = value;
        }

        public OverlayMode Mode {
            get => mode;
            set => mode = value;
        }

        public string OutlineMatchClassContains {
            get => outlineMatchClassContains;
            set => outlineMatchClassContains = value;
        }

        WevaDocument doc;
        readonly BoxOutlineRenderer outliner = new();
        readonly DirtyHighlighter dirty = new();
        readonly HoverInspector hover = new();
        readonly CacheStats cache = new();
        readonly PerfReadout perf = new();

        readonly List<OverlayRect> outlineBuffer = new();
        readonly List<DirtyBoxHighlight> dirtyBuffer = new();
        Vector2 lastPointer;

        // W7 phase 2 -- pinned Styles panel. Click pins the hovered element
        // and opens a Chrome-DevTools-style panel rendering
        // StyleInspector.Dump (computed values + cascade trace + box model).
        // The cascade-trace capture flag is flipped ON only while something
        // is pinned, honoring StyleInspector's zero-cost-when-off contract.
        Element pinnedElement;
        Vector2 stylesScroll;
        string pinnedReportText;
        int pinnedReportFrame = -1;

        Texture2D whitePixel;
        GUIStyle labelStyle;
        GUIStyle perfStyle;

        // Conventional Chrome DevTools palette. Linear-space color authoring
        // is fine here because IMGUI is gamma-space and we're only painting
        // into the diagnostic overlay — no engine pixels look at these.
        static readonly Color MarginColor = new(1.00f, 0.60f, 0.00f, 0.45f);
        static readonly Color BorderColor = new(1.00f, 0.85f, 0.20f, 0.55f);
        static readonly Color PaddingColor = new(0.40f, 0.85f, 0.30f, 0.45f);
        static readonly Color ContentColor = new(0.30f, 0.55f, 0.95f, 0.45f);
        static readonly Color DirtyLayoutColor = new(1.00f, 0.20f, 0.20f, 0.55f);
        static readonly Color DirtyStyleColor = new(1.00f, 0.85f, 0.10f, 0.55f);
        static readonly Color DirtyPaintColor = new(0.55f, 0.55f, 0.55f, 0.45f);

        public BoxOutlineRenderer Outliner => outliner;
        public DirtyHighlighter Dirty => dirty;
        public HoverInspector Hover => hover;
        public CacheStats Cache => cache;
        public PerfReadout Perf => perf;

        public void SetDocument(WevaDocument document) {
            doc = document;
        }

        void Awake() {
            doc = GetComponent<WevaDocument>();
        }

        void OnEnable() {
            perf.Start();
        }

        void OnDisable() {
            perf.Dispose();
        }

        void Update() {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(toggleKey)) {
                enabledOverlay = !enabledOverlay;
            }
#endif
            if (!enabledOverlay) return;
            if (doc == null) return;

            // Capture the current dirty set BEFORE the WevaDocument calls
            // tracker.Clear() in its own Update. Unity does not guarantee
            // Update order between two MonoBehaviours on the same GameObject,
            // so we sample defensively whenever we can — if we run after
            // WevaDocument the set is already empty and DirtyHighlighter just
            // decays existing entries one frame.
            if ((mode & OverlayMode.DirtyTracking) != 0 && doc.Invalidation != null) {
                dirty.CaptureFrame(doc.Invalidation);
            }
            if ((mode & OverlayMode.Performance) != 0) {
                perf.RecordFrame(Time.unscaledDeltaTime);
                if (doc.Painter != null) cache.RecordFrame(doc.Painter);
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            lastPointer = Input.mousePosition;
            // Click pins/unpins the element under the cursor; Escape unpins.
            // (Same legacy-input pathway as the F12 toggle above.)
            if (Input.GetMouseButtonDown(0)) {
                var hitNow = ResolveUnderPointer();
                pinnedElement = hitNow == pinnedElement ? null : hitNow;
                pinnedReportFrame = -1;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) pinnedElement = null;
#endif
            StyleInspector.CaptureCascadeTrace = pinnedElement != null;
        }

        // Shared screen->CSS pointer resolution used by hover + pinning.
        Element ResolveUnderPointer() {
            double invScaleX = 1.0, invScaleY = 1.0;
            var lctx = doc.CurrentState?.LayoutContext;
            if (lctx != null && lctx.ViewportWidthPx > 0.5 && lctx.ViewportHeightPx > 0.5
                && Screen.width > 0 && Screen.height > 0) {
                invScaleX = lctx.ViewportWidthPx  / Screen.width;
                invScaleY = lctx.ViewportHeightPx / Screen.height;
            }
            double x = lastPointer.x * invScaleX;
            double y = (Screen.height - lastPointer.y) * invScaleY;
            var hit = doc.CurrentState?.HitTester;
            var index = doc.CurrentState?.ElementToBox;
            System.Func<Element, Box> lookup = index != null ? (System.Func<Element, Box>)index.Lookup : null;
            return hover.Resolve(hit, x, y, lookup);
        }

        void OnGUI() {
            if (!enabledOverlay) return;
            if (doc == null) return;
            if (doc.CurrentState == null || doc.CurrentState.RootBox == null) return;
            if (Event.current.type != EventType.Repaint) return;

            EnsureGuiResources();
            var root = doc.CurrentState.RootBox;

            if ((mode & OverlayMode.Outlines) != 0) {
                // Calibration markers: a 50px red square in each screen corner.
                // If IMGUI's coord system matches the URP paint's, these will
                // land tightly in the visible corners of the Game View; any
                // offset / scale will be immediately visible.
                if (Weva.Diagnostics.UILayoutDiagnostics.Enabled) {
                    FillScreenRect(0,                 0,                  50, 50, new Color(1f, 0f, 0f, 0.7f));
                    FillScreenRect(Screen.width - 50, 0,                  50, 50, new Color(0f, 1f, 0f, 0.7f));
                    FillScreenRect(0,                 Screen.height - 50, 50, 50, new Color(0f, 0f, 1f, 0.7f));
                    FillScreenRect(Screen.width - 50, Screen.height - 50, 50, 50, new Color(1f, 1f, 0f, 0.7f));
                }
                DrawOutlines(root);
            }
            if ((mode & OverlayMode.DirtyTracking) != 0) {
                DrawDirty();
            }
            // Hover inspection is always-on when the overlay itself is enabled —
            // it's the most expected DevTools affordance, and the readout pops
            // out of the way when the cursor isn't over an element.
            DrawHover();
            if (pinnedElement != null) {
                DrawStylesPanel();
            }
            if (enabledFps && (mode & OverlayMode.Performance) != 0) {
                DrawPerf();
            }
        }

        void DrawOutlines(Box root) {
            outlineBuffer.Clear();
            outliner.MatchClassContains = string.IsNullOrEmpty(outlineMatchClassContains)
                ? null
                : outlineMatchClassContains;
            outliner.EmitInto(root, outlineBuffer);
            // CSS coords use the LayoutContext viewport (which can be the
            // override, the reference camera's pixel rect, or the URP target
            // descriptor size). IMGUI's OnGUI draws in Screen.width/height
            // pixels. When the two differ — e.g. an Editor Game View at 800×600
            // while the document was laid out against a 1920×1080 viewport —
            // overlay rects drawn at raw CSS coords land off-screen relative to
            // where the URP backend actually painted the pixels. Apply the
            // viewport→screen scale so the outline rects sit on top of the
            // painted boxes the user can see.
            double scaleX = 1.0, scaleY = 1.0;
            var lctx = doc.CurrentState?.LayoutContext;
            if (lctx != null && lctx.ViewportWidthPx > 0.5 && lctx.ViewportHeightPx > 0.5
                && Screen.width > 0 && Screen.height > 0) {
                scaleX = Screen.width  / lctx.ViewportWidthPx;
                scaleY = Screen.height / lctx.ViewportHeightPx;
            }
            // PAINT-1 / overlay-offset diagnostic: prints once per frame when
            // UILayoutDiagnostics.Enabled. Lets us correlate the overlay-side
            // viewport math with the URP-side viewport the actual paint uses.
            if (Weva.Diagnostics.UILayoutDiagnostics.Enabled) {
                var cam = Camera.main;
                Weva.Diagnostics.UILayoutDiagnostics.Trace("DevToolsOverlay.Frame",
                    $"Screen={Screen.width}x{Screen.height} " +
                    $"LayoutCtx.Viewport={lctx?.ViewportWidthPx}x{lctx?.ViewportHeightPx} " +
                    $"Camera.main.pixelRect={(cam != null ? cam.pixelRect.ToString() : "<null>")} " +
                    $"scaleX={scaleX:F4} scaleY={scaleY:F4} outlineCount={outlineBuffer.Count}");
                // Dump the first ~4 rects so we can see CSS coords vs screen-mapped.
                int dumpN = System.Math.Min(4, outlineBuffer.Count);
                for (int j = 0; j < dumpN; j++) {
                    var rr = outlineBuffer[j];
                    Weva.Diagnostics.UILayoutDiagnostics.Trace("DevToolsOverlay.Frame",
                        $"rect[{j}] kind={rr.Kind} css=({rr.X:F2},{rr.Y:F2},{rr.Width:F2},{rr.Height:F2}) " +
                        $"→ screen=({rr.X*scaleX:F2},{rr.Y*scaleY:F2},{rr.Width*scaleX:F2},{rr.Height*scaleY:F2})");
                }
            }
            for (int i = 0; i < outlineBuffer.Count; i++) {
                var r = outlineBuffer[i];
                Color c;
                switch (r.Kind) {
                    case OverlayRectKind.Margin: c = MarginColor; break;
                    case OverlayRectKind.Border: c = BorderColor; break;
                    case OverlayRectKind.Padding: c = PaddingColor; break;
                    default: c = ContentColor; break;
                }
                StrokeRect(r.X * scaleX, r.Y * scaleY,
                           r.Width * scaleX, r.Height * scaleY, c);
            }
        }

        void DrawDirty() {
            dirtyBuffer.Clear();
            var index = doc.CurrentState?.ElementToBox;
            System.Func<Element, Box> lookup = index != null ? (System.Func<Element, Box>)index.Lookup : null;
            dirty.ResolveBoxes(lookup, dirtyBuffer);
            for (int i = 0; i < dirtyBuffer.Count; i++) {
                var d = dirtyBuffer[i];
                Color c;
                switch (d.Highlight.Kind) {
                    case DirtyHighlightKind.Layout: c = DirtyLayoutColor; break;
                    case DirtyHighlightKind.Style: c = DirtyStyleColor; break;
                    default: c = DirtyPaintColor; break;
                }
                // Fade as the highlight ages so a hover-flicker doesn't strobe.
                float ageFade = Mathf.Max(0.2f, d.Highlight.FramesRemaining / (float)dirty.FlashFrames);
                c.a *= ageFade;
                FillBoxAbsolute(d.Box, c);
            }
        }

        void DrawHover() {
            // GUI mouse coords have y-down origin matching CSS — no flip needed.
            // Input.mousePosition has y-up origin (Unity legacy), but the
            // hit-tester expects CSS coords. We flip via Screen.height.
            // Mouse comes in screen coords; the hit-tester expects CSS coords.
            // Apply the inverse of the DrawOutlines scale (screen→CSS).
            double invScaleX = 1.0, invScaleY = 1.0;
            var lctxH = doc.CurrentState?.LayoutContext;
            if (lctxH != null && lctxH.ViewportWidthPx > 0.5 && lctxH.ViewportHeightPx > 0.5
                && Screen.width > 0 && Screen.height > 0) {
                invScaleX = lctxH.ViewportWidthPx  / Screen.width;
                invScaleY = lctxH.ViewportHeightPx / Screen.height;
            }
            double x = lastPointer.x * invScaleX;
            double y = (Screen.height - lastPointer.y) * invScaleY;
            var hit = doc.CurrentState?.HitTester;
            var index = doc.CurrentState?.ElementToBox;
            System.Func<Element, Box> lookup = index != null ? (System.Func<Element, Box>)index.Lookup : null;
            var element = hover.Resolve(hit, x, y, lookup);
            if (element == null) return;

            var style = doc.Cascade != null ? doc.Cascade.GetComposedStyle(element, doc.State) : null;
            string text = hover.Format(element, hover.CurrentBox, style);
            var size = labelStyle.CalcSize(new GUIContent(text));
            float lx = lastPointer.x + 16f;
            float ly = (Screen.height - lastPointer.y) + 16f;
            // Clamp to screen so the readout never falls off the right/bottom.
            if (lx + size.x + 8f > Screen.width) lx = Screen.width - size.x - 8f;
            if (ly + size.y + 8f > Screen.height) ly = Screen.height - size.y - 8f;
            FillScreenRect(lx - 4, ly - 4, size.x + 8, size.y + 8, new Color(0, 0, 0, 0.85f));
            GUI.Label(new Rect(lx, ly, size.x, size.y), text, labelStyle);
        }

        void DrawPerf() {
            string text = perf.Format() + "\n" + cache.Format();
            var content = new GUIContent(text);
            var size = perfStyle.CalcSize(content);
            float x = 8f;
            float y = 8f;
            FillScreenRect(x - 4, y - 4, size.x + 8, size.y + 8, new Color(0, 0, 0, 0.85f));
            GUI.Label(new Rect(x, y, size.x, size.y), text, perfStyle);
        }

        // W7 phase 2: right-docked Styles panel for the pinned element.
        // Rendering leans on StyleInspectorReport.ToString() (the data core
        // owns structure; the panel is presentation) -- the report is
        // recomputed at most once per frame, and only while pinned. Unpins
        // itself if the element leaves the document (lookup turns null).
        void DrawStylesPanel() {
            var index = doc.CurrentState?.ElementToBox;
            var box = index != null ? index.Lookup(pinnedElement) : null;
            if (box == null) { pinnedElement = null; return; }

            if (pinnedReportFrame != Time.frameCount) {
                var style = doc.Cascade != null ? doc.Cascade.GetComposedStyle(pinnedElement, doc.State) : null;
                var report = StyleInspector.Dump(pinnedElement, style, box, doc.Cascade, doc.State);
                pinnedReportText = report.ToString();
                pinnedReportFrame = Time.frameCount;
            }

            // Highlight the pinned element's box-model in place.
            FillBoxAbsolute(box, new Color(0.30f, 0.55f, 0.95f, 0.25f));

            // Plain GUI.* only — this OnGUI early-outs on every event except
            // Repaint, and GUILayout controls need the Layout event pass to
            // measure (the first cut used GUILayout and rendered an empty
            // panel). Non-layout BeginScrollView + a CalcHeight'd label rect
            // need no Layout pass.
            const float panelW = 380f;
            float panelH = Screen.height - 16f;
            float px = Screen.width - panelW - 8f;
            FillScreenRect(px - 4, 4, panelW + 8, panelH + 8, new Color(0, 0, 0, 0.88f));
            string text = pinnedReportText ?? "";
            var content = new GUIContent(text);
            float textH = labelStyle.CalcHeight(content, panelW - 20f);
            var outer = new Rect(px, 8, panelW, panelH);
            var view = new Rect(0, 0, panelW - 20f, Mathf.Max(textH, panelH));
            stylesScroll = GUI.BeginScrollView(outer, stylesScroll, view);
            GUI.Label(new Rect(4, 0, view.width - 8f, textH), content, labelStyle);
            GUI.EndScrollView();
        }

        void StrokeRect(double x, double y, double w, double h, Color c) {
            FillScreenRect(x, y, w, 1, c);
            FillScreenRect(x, y + h - 1, w, 1, c);
            FillScreenRect(x, y, 1, h, c);
            FillScreenRect(x + w - 1, y, 1, h, c);
        }

        void FillBoxAbsolute(Box box, Color c) {
            if (box == null) return;
            double absX = 0, absY = 0;
            for (var b = box; b != null; b = b.Parent) {
                absX += b.X + b.StickyOffsetX;
                absY += b.Y + b.StickyOffsetY;
            }
            // Same viewport→screen scale as DrawOutlines, see comment there.
            double scaleX = 1.0, scaleY = 1.0;
            var lctx = doc.CurrentState?.LayoutContext;
            if (lctx != null && lctx.ViewportWidthPx > 0.5 && lctx.ViewportHeightPx > 0.5
                && Screen.width > 0 && Screen.height > 0) {
                scaleX = Screen.width  / lctx.ViewportWidthPx;
                scaleY = Screen.height / lctx.ViewportHeightPx;
            }
            FillScreenRect(absX * scaleX, absY * scaleY,
                           box.Width * scaleX, box.Height * scaleY, c);
        }

        void FillScreenRect(double x, double y, double w, double h, Color c) {
            if (w <= 0 || h <= 0) return;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect((float)x, (float)y, (float)w, (float)h), whitePixel, ScaleMode.StretchToFill, alphaBlend: true);
            GUI.color = prev;
        }

        void EnsureGuiResources() {
            if (whitePixel == null) {
                whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false, linear: false);
                whitePixel.SetPixel(0, 0, Color.white);
                whitePixel.Apply();
                whitePixel.hideFlags = HideFlags.HideAndDontSave;
            }
            if (labelStyle == null) {
                labelStyle = new GUIStyle();
                labelStyle.alignment = TextAnchor.UpperLeft;
                labelStyle.wordWrap = false;
                labelStyle.richText = false;
                labelStyle.fontSize = 11;
                labelStyle.padding = new RectOffset(4, 4, 2, 2);
                labelStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 1f);
            }
            if (perfStyle == null) {
                perfStyle = new GUIStyle();
                perfStyle.alignment = TextAnchor.UpperLeft;
                perfStyle.wordWrap = false;
                perfStyle.richText = false;
                perfStyle.fontSize = 11;
                perfStyle.padding = new RectOffset(4, 4, 2, 2);
                perfStyle.normal.textColor = new Color(0.85f, 1.00f, 0.85f, 1f);
            }
        }
    }
}
