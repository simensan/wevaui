using UnityEngine;
using Weva.Paint;
using Weva.Paint.Filters;
using URect = UnityEngine.Rect;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Rendering {
    // Debug fallback renderer — uses Unity IMGUI (GUI.DrawTexture / GUI.Label) to draw the
    // PaintList every OnGUI tick. Intentionally crude: solid rects, simple borders, and text
    // labels. Skips shadows, filters, gradients-as-gradients, and transform/opacity stacking.
    //
    // Use this to verify the headless pipeline (parse → cascade → layout → paint) is producing
    // correct geometry while the URP backend is still being validated. Attach to the same
    // GameObject as a WevaDocument.
    [RequireComponent(typeof(WevaDocument))]
    public sealed class IMGUIDocumentRenderer : MonoBehaviour {
        [SerializeField] bool drawDebugBoxes = false;

        /// <summary>Draw magenta outlines + coordinate labels around every painted box.</summary>
        public bool DrawDebugBoxes { get => drawDebugBoxes; set => drawDebugBoxes = value; }

        WevaDocument doc;
        IMGUIBackend backend;

        void Awake() {
            doc = GetComponent<WevaDocument>();
            backend = new IMGUIBackend();
        }

        void OnGUI() {
            if (doc == null) return;
            if (Event.current.type != EventType.Repaint) return;
            if (doc.CurrentState == null || doc.CurrentState.RootBox == null) return;
            // Honor the runtime backend selector: when the user picks URP explicitly we
            // skip the IMGUI repaint to avoid double-drawing. Auto + play-mode also defers
            // to URP under the assumption the renderer feature is configured.
            if (doc.RendererBackend == RendererBackendKind.URP) return;
            // In Auto mode, only defer to URP when the batched renderer feature has
            // actually registered itself (UIBatchedRendererFeature.Create() ran). Without
            // that, the URP pipeline is active but nothing is wired to draw — bailing out
            // here would render nothing at all. Fall through to IMGUI in that case.
            if (doc.RendererBackend == RendererBackendKind.Auto && Application.isPlaying && IsUrpActive() && IsUrpBackendWired()) return;
            backend.BeginFrame();
            backend.DrawDebugBoxes = drawDebugBoxes;
            doc.EmitPaint(backend);
        }

        static bool IsUrpActive() {
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (rp == null) return false;
            // Identify URP by type name to avoid a hard reference to the URP assembly here
            // (kept compatible with builds that don't link URP).
            var t = rp.GetType();
            return t.Name == "UniversalRenderPipelineAsset" || t.FullName != null && t.FullName.Contains("Universal");
        }

        static bool IsUrpBackendWired() {
            // Only the actual UIBatchedRendererFeature registers a backend. Reflect into
            // the registry so this file stays WEVA_URP-independent.
            var t = System.Type.GetType("Weva.Rendering.URP.BatchedRendererBackendRegistry, Weva.Runtime");
            if (t == null) return false;
            var f = t.GetField("Active", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return f?.GetValue(null) != null;
        }
    }

    internal sealed class IMGUIBackend : IRenderBackend {
        public bool DrawDebugBoxes;

        Texture2D white;
        GUIStyle textStyle;
        GUIStyle debugStyle;
        Font defaultFont;
        bool fontProbed;

        public void BeginFrame() {
            if (white == null) {
                white = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false, linear: false);
                white.SetPixel(0, 0, Color.white);
                white.Apply();
                white.hideFlags = HideFlags.HideAndDontSave;
            }
            if (!fontProbed) {
                fontProbed = true;
                // Prefer the package-bundled font (matches what SdfFontMetrics
                // loads in Play Mode) so IMGUI labels are drawn in the same
                // family the layout layer measured. Falls back to whatever
                // GUI.skin.label was using if the bundled file isn't reachable.
                defaultFont = TryLoadBundledFont();
            }
            if (textStyle == null) {
                textStyle = new GUIStyle();
                textStyle.alignment = TextAnchor.UpperLeft;
                textStyle.wordWrap = false;
                textStyle.richText = false;
                textStyle.normal.textColor = Color.black;
                if (defaultFont != null) textStyle.font = defaultFont;
            }
            if (debugStyle == null) {
                debugStyle = new GUIStyle();
                debugStyle.alignment = TextAnchor.UpperLeft;
                debugStyle.wordWrap = false;
                debugStyle.fontSize = 9;
                debugStyle.normal.textColor = new Color(1f, 0.4f, 0.4f, 1f);
            }
        }

        public void Submit(FillRectCommand cmd) {
            if (cmd.Brush == null) return;
            var col = BrushToColor(cmd.Brush);
            if (col.a <= 0) {
                MaybeDebug(cmd.Bounds, col);
                return;
            }
            DrawSolid(ToRect(cmd.Bounds), col);
            MaybeDebug(cmd.Bounds, col);
        }

        void MaybeDebug(PaintRect r, Color tint) {
            if (!DrawDebugBoxes) return;
            var prev = GUI.color;
            GUI.color = new Color(1f, 0f, 1f, 0.9f);
            var rect = ToRect(r);
            GUI.DrawTexture(new URect(rect.x, rect.y, rect.width, 1), white);
            GUI.DrawTexture(new URect(rect.x, rect.y + rect.height - 1, rect.width, 1), white);
            GUI.DrawTexture(new URect(rect.x, rect.y, 1, rect.height), white);
            GUI.DrawTexture(new URect(rect.x + rect.width - 1, rect.y, 1, rect.height), white);
            GUI.color = prev;
            GUI.Label(new URect(rect.x + 2, rect.y + 2, 200, 12),
                string.Format("{0:0},{1:0} {2:0}x{3:0}", rect.x, rect.y, rect.width, rect.height),
                debugStyle);
        }

        public void Submit(StrokeBorderCommand cmd) {
            var b = cmd.Bounds;
            DrawEdge(b.X, b.Y, b.Width, cmd.Borders.Top, horizontal: true);
            DrawEdge(b.X, b.Y + b.Height - cmd.Borders.Bottom.Width, b.Width, cmd.Borders.Bottom, horizontal: true);
            DrawEdgeVert(b.X, b.Y, b.Height, cmd.Borders.Left);
            DrawEdgeVert(b.X + b.Width - cmd.Borders.Right.Width, b.Y, b.Height, cmd.Borders.Right);
        }

        public void Submit(DrawTextCommand cmd) {
            if (string.IsNullOrEmpty(cmd.Text)) return;
            var prev = GUI.color;
            // IMGUI ignores Linear color space; convert linear → gamma so the colors
            // from the cascade actually look right on screen.
            var lin = new Color(cmd.Color.R, cmd.Color.G, cmd.Color.B, cmd.Color.A);
            GUI.color = lin.gamma;
            int size = (int)cmd.Font.Size;
            if (size < 1) size = 14;
            textStyle.fontSize = size;
            textStyle.fontStyle = cmd.Font.Style == Weva.Paint.FontStyle.Italic ? UnityEngine.FontStyle.Italic
                : cmd.Font.Weight >= 600 ? UnityEngine.FontStyle.Bold
                : UnityEngine.FontStyle.Normal;
            textStyle.normal.textColor = lin.gamma;
            GUI.Label(ToRect(cmd.Bounds), cmd.Text, textStyle);
            GUI.color = prev;
        }

        // Shadows are intentionally a no-op in the IMGUI fallback. The blur passes the
        // SoftwareRasterizer uses are far too expensive to dispatch into IMGUI per-frame
        // (IMGUI re-issues every command on every OnGUI repaint, on the main thread, in
        // gamma color space). The URP backend is the real shadow path; IMGUI exists to
        // sanity-check geometry and color flow with no shadow blur in either direction.
        public void Submit(DrawShadowCommand cmd) { }

        public void Submit(PushClipCommand cmd) {
            GUI.BeginClip(ToRect(cmd.Bounds));
        }

        public void Submit(PopClipCommand cmd) {
            GUI.EndClip();
        }

        public void Submit(PushOpacityCommand cmd) { }
        public void Submit(PopOpacityCommand cmd) { }
        public void Submit(PushTransformCommand cmd) { }
        public void Submit(PopTransformCommand cmd) { }
        public void Submit(PushFilterCommand cmd) { }
        public void Submit(PopFilterCommand cmd) { }
        public void Submit(BeginSubtreeCaptureCommand cmd) { }
        public void Submit(EndSubtreeCaptureCommand cmd) { }
        public void Submit(ReplaySubtreeSnapshotCommand cmd) { }

        public void Submit(PaintList list) {
            for (int i = 0; i < list.Commands.Count; i++) list.Commands[i].Submit(this);
        }

        void DrawSolid(URect rect, Color color) {
            var prev = GUI.color;
            // IMGUI is gamma-space; convert linear-space colors before sending.
            GUI.color = color.gamma;
            GUI.DrawTexture(rect, white, ScaleMode.StretchToFill, alphaBlend: true);
            GUI.color = prev;
        }

        void DrawEdge(double x, double y, double width, BorderEdge edge, bool horizontal) {
            if (edge.Style == BorderStyle.None || edge.Width <= 0) return;
            var col = new Color(edge.Color.R, edge.Color.G, edge.Color.B, edge.Color.A);
            DrawStyledEdge(x, y, width, edge.Width, edge.Style, col, horizontal);
        }

        void DrawEdgeVert(double x, double y, double height, BorderEdge edge) {
            if (edge.Style == BorderStyle.None || edge.Width <= 0) return;
            var col = new Color(edge.Color.R, edge.Color.G, edge.Color.B, edge.Color.A);
            DrawStyledEdge(x, y, edge.Width, height, edge.Style, col, horizontal: false);
        }

        void DrawStyledEdge(double x, double y, double w, double h, BorderStyle style, Color col, bool horizontal) {
            switch (style) {
                case BorderStyle.Solid:
                    DrawSolid(new URect((float)x, (float)y, (float)w, (float)h), col);
                    return;
                case BorderStyle.Double: {
                    double width = horizontal ? h : w;
                    double third = width / 3.0;
                    if (third <= 0) {
                        DrawSolid(new URect((float)x, (float)y, (float)w, (float)h), col);
                        return;
                    }
                    if (horizontal) {
                        DrawSolid(new URect((float)x, (float)y, (float)w, (float)third), col);
                        DrawSolid(new URect((float)x, (float)(y + h - third), (float)w, (float)third), col);
                    } else {
                        DrawSolid(new URect((float)x, (float)y, (float)third, (float)h), col);
                        DrawSolid(new URect((float)(x + w - third), (float)y, (float)third, (float)h), col);
                    }
                    return;
                }
                case BorderStyle.Dashed:
                case BorderStyle.Dotted: {
                    // IMGUI cannot rasterize anti-aliased circles for dotted patterns, and
                    // the per-frame overhead of dozens of GUI.DrawTexture calls per box is
                    // material. We approximate both as a stripe pattern: stroke = 2*width,
                    // gap = 2*width. Acceptable for the dev fallback; the SoftwareRasterizer
                    // and URP backend render the spec-correct dotted-as-circles form.
                    double width = horizontal ? h : w;
                    double dash = 2.0 * width;
                    double gap = 2.0 * width;
                    double period = dash + gap;
                    if (horizontal) {
                        double cur = x;
                        double end = x + w;
                        while (cur < end) {
                            double stroke = System.Math.Min(dash, end - cur);
                            DrawSolid(new URect((float)cur, (float)y, (float)stroke, (float)h), col);
                            cur += period;
                        }
                    } else {
                        double cur = y;
                        double end = y + h;
                        while (cur < end) {
                            double stroke = System.Math.Min(dash, end - cur);
                            DrawSolid(new URect((float)x, (float)cur, (float)w, (float)stroke), col);
                            cur += period;
                        }
                    }
                    return;
                }
                default:
                    DrawSolid(new URect((float)x, (float)y, (float)w, (float)h), col);
                    return;
            }
        }

        static URect ToRect(PaintRect r) {
            return new URect((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
        }

        static Color BrushToColor(Brush b) {
            switch (b.Kind) {
                case BrushKind.SolidColor:
                    return new Color(b.Color.R, b.Color.G, b.Color.B, b.Color.A);
                case BrushKind.Gradient:
                    if (b.GradientValue is LinearGradient lg && lg.Stops.Count > 0) {
                        var c = lg.Stops[0].Color;
                        return new Color(c.R, c.G, c.B, c.A);
                    }
                    return new Color(0.5f, 0.5f, 0.5f, 1f);
                default:
                    return new Color(1f, 0f, 1f, 1f);
            }
        }

        // Loads the bundled package default font (Apache-2 RobotoMono) so the
        // IMGUI debug renderer paints text in the same face SdfFontMetrics
        // measures with. Resources.Load is the only path that works in built
        // players; the AssetDatabase fallback is editor-only.
        static Font TryLoadBundledFont() {
            // 1. Resources/Fonts/Weva-Default — works once a user (or the
            //    package's importer) drops the asset into a Resources/ folder.
            var f = UnityEngine.Resources.Load<Font>("Fonts/Weva-Default");
            if (f != null) return f;
#if UNITY_EDITOR
            // 2. Editor-only direct asset path. Lets the in-package preview
            //    window pick up the font without requiring the user to copy it.
            string[] candidates = {
                "Packages/com.wevaui/Runtime/Resources/Fonts/Weva-Default.ttf"
            };
            foreach (var p in candidates) {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(p);
                if (asset != null) return asset;
            }
#endif
            return null;
        }
    }
}
