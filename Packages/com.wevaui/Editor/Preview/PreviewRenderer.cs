using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.EditorTools.Preview {
    // Owns the offscreen RenderTexture + Texture2D pair and runs the headless pipeline
    // (parse -> cascade -> layout -> paint -> backend) for the preview window.
    //
    // v1 strategy: software-rasterize via SoftwarePainter (CPU). The URP backend may
    // not yet be valid in editor and is also being built concurrently — keeping the
    // editor preview path on a CPU painter means we can ship the window and the URP
    // backend independently. TODO(v1.x): once URPRenderBackend is solid, swap the
    // backend selection here based on a toolbar toggle (CPU vs URP). The rest of the
    // pipeline above the backend is identical between the two.
    public sealed class PreviewRenderer : IDisposable {
        readonly IFontMetrics fontMetrics = new MonoFontMetrics();
        readonly Dictionary<string, OriginatedStylesheet> cssAssetCache = new();

        Texture2D outputTexture;
        int textureWidth;
        int textureHeight;

        public Texture2D OutputTexture => outputTexture;
        public string LastError { get; private set; }
        public int LastCommandCount { get; private set; }
        public bool HasContent { get; private set; }

        public bool RenderAsset(TextAsset htmlAsset, IReadOnlyList<TextAsset> additionalStylesheets, PreviewViewport viewport) {
            LastError = null;
            HasContent = false;
            if (htmlAsset == null || string.IsNullOrEmpty(htmlAsset.text)) {
                LastError = "Select a .html TextAsset in the Project window.";
                ClearTexture(viewport);
                return false;
            }
            string assetPath = AssetDatabase.GetAssetPath(htmlAsset);
            return RenderHtmlSource(htmlAsset.text, assetPath, additionalStylesheets, viewport);
        }

        public bool RenderHtmlSource(string html, string assetPath, IReadOnlyList<TextAsset> additionalStylesheets, PreviewViewport viewport) {
            try {
                var paintList = BuildPaintList(html, assetPath, additionalStylesheets, viewport);
                if (paintList == null) {
                    // LastError already set by BuildPaintList.
                    ClearTexture(viewport);
                    return false;
                }
                LastCommandCount = paintList.Commands.Count;
                Rasterize(paintList, viewport);
                HasContent = true;
                return true;
            } catch (Exception ex) {
                LastError = ex.GetType().Name + ": " + ex.Message;
                ClearTexture(viewport);
                return false;
            }
        }

        // Runs the headless pipeline (parse → cascade → layout → paint) and returns the
        // PaintList, without touching any output texture/backend. Shared by the CPU
        // SoftwarePainter path (RenderHtmlSource) and the GPU editor-panel host, which
        // records the same PaintList into a BatchedURPRenderBackend. Returns null and sets
        // LastError on failure (empty source or no root box). May throw on parser faults;
        // callers that want a hard guarantee should wrap in try/catch as RenderHtmlSource does.
        public PaintList BuildPaintList(string html, string assetPath, IReadOnlyList<TextAsset> additionalStylesheets, PreviewViewport viewport) {
            LastError = null;
            if (string.IsNullOrEmpty(html)) {
                LastError = "Empty HTML source.";
                return null;
            }
            var doc = HtmlParser.Parse(html, new ParseOptions { ThrowOnError = false });
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UserAgentStylesheet.Parse());

            CollectLinkedStylesheets(html, assetPath, sheets);
            if (additionalStylesheets != null) {
                foreach (var ta in additionalStylesheets) {
                    if (ta == null || string.IsNullOrEmpty(ta.text)) continue;
                    var parsed = CssParser.Parse(ta.text, new ParseOptions { ThrowOnError = false });
                    sheets.Add(OriginatedStylesheet.Author(parsed));
                }
            }

            var media = viewport.ToMediaContext();
            var cascade = new CascadeEngine(sheets, media);
            var styles = cascade.ComputeAll(doc);

            var ctx = new LayoutContext(fontMetrics) {
                ViewportWidthPx = viewport.Width,
                ViewportHeightPx = viewport.Height,
            };
            var layout = new LayoutEngine(fontMetrics);
            var rootBox = layout.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);
            if (rootBox == null) {
                LastError = "Layout produced no root box.";
                return null;
            }

            var converter = new BoxToPaintConverter(new LengthContext {
                BaseFontSizePx = 16,
                RootFontSizePx = 16,
                ViewportWidthPx = viewport.Width,
                ViewportHeightPx = viewport.Height,
                DpiPixelsPerInch = viewport.Dpi
            });
            return converter.Convert(rootBox);
        }

        public bool RenderPaintList(PaintList list, PreviewViewport viewport) {
            LastError = null;
            HasContent = false;
            if (list == null) {
                LastError = "PaintList is null.";
                ClearTexture(viewport);
                return false;
            }
            try {
                LastCommandCount = list.Commands.Count;
                Rasterize(list, viewport);
                HasContent = true;
                return true;
            } catch (Exception ex) {
                LastError = ex.GetType().Name + ": " + ex.Message;
                ClearTexture(viewport);
                return false;
            }
        }

        void Rasterize(PaintList list, PreviewViewport viewport) {
            EnsureTexture(viewport);
            var painter = new SoftwarePainter(textureWidth, textureHeight);
            painter.Clear(BackgroundFor(viewport));
            ((IRenderBackend)painter).Submit(list);
            painter.UploadInto(outputTexture);
        }

        void ClearTexture(PreviewViewport viewport) {
            EnsureTexture(viewport);
            var painter = new SoftwarePainter(textureWidth, textureHeight);
            painter.Clear(BackgroundFor(viewport));
            painter.UploadInto(outputTexture);
            LastCommandCount = 0;
        }

        void EnsureTexture(PreviewViewport viewport) {
            int w = Mathf.Max(1, viewport.Width);
            int h = Mathf.Max(1, viewport.Height);
            if (outputTexture == null) {
                outputTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                outputTexture.filterMode = FilterMode.Point;
                outputTexture.wrapMode = TextureWrapMode.Clamp;
                outputTexture.hideFlags = HideFlags.HideAndDontSave;
                textureWidth = w;
                textureHeight = h;
                return;
            }
            if (textureWidth != w || textureHeight != h) {
                outputTexture.Reinitialize(w, h, TextureFormat.RGBA32, false);
                textureWidth = w;
                textureHeight = h;
            }
        }

        static Color32 BackgroundFor(PreviewViewport viewport) {
            return viewport.ColorScheme == PreviewColorScheme.Dark
                ? new Color32(20, 20, 22, 255)
                : new Color32(255, 255, 255, 255);
        }

        // Pulls <link rel="stylesheet" href="..."> hrefs from the document source and
        // resolves them as TextAssets relative to the HTML asset's folder. v1 keeps
        // the resolver intentionally narrow: a substring scan over the source rather
        // than re-walking the parsed Document tree, which is cheaper and safe because
        // any href that survives the regex must still resolve via AssetDatabase.
        void CollectLinkedStylesheets(string html, string assetPath, List<OriginatedStylesheet> output) {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(assetPath)) return;
            string folder = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (folder == null) return;
            int searchFrom = 0;
            while (true) {
                int linkIdx = IndexOfIgnoreCase(html, "<link", searchFrom);
                if (linkIdx < 0) break;
                int end = html.IndexOf('>', linkIdx);
                if (end < 0) break;
                string tag = html.Substring(linkIdx, end - linkIdx + 1);
                searchFrom = end + 1;
                if (IndexOfIgnoreCase(tag, "rel=\"stylesheet\"", 0) < 0 &&
                    IndexOfIgnoreCase(tag, "rel='stylesheet'", 0) < 0 &&
                    IndexOfIgnoreCase(tag, "rel=stylesheet", 0) < 0) continue;
                string href = ExtractAttribute(tag, "href");
                if (string.IsNullOrEmpty(href)) continue;
                string full = ResolveRelative(folder, href);
                var sheet = LoadCss(full);
                if (sheet != null) output.Add(sheet);
            }
        }

        OriginatedStylesheet LoadCss(string path) {
            if (string.IsNullOrEmpty(path)) return null;
            if (cssAssetCache.TryGetValue(path, out var cached)) return cached;
            var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (ta == null || string.IsNullOrEmpty(ta.text)) {
                cssAssetCache[path] = null;
                return null;
            }
            var sheet = CssParser.Parse(ta.text, new ParseOptions { ThrowOnError = false });
            var result = OriginatedStylesheet.Author(sheet);
            cssAssetCache[path] = result;
            return result;
        }

        public void InvalidateAssetCache() {
            cssAssetCache.Clear();
        }

        static string ResolveRelative(string folder, string href) {
            href = href.Trim().Replace('\\', '/');
            if (href.StartsWith("./")) href = href.Substring(2);
            if (href.StartsWith("Assets/") || href.StartsWith("Packages/")) return href;
            return folder.TrimEnd('/') + "/" + href;
        }

        static int IndexOfIgnoreCase(string source, string needle, int start) {
            return source.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
        }

        static string ExtractAttribute(string tag, string name) {
            int idx = IndexOfIgnoreCase(tag, name + "=", 0);
            if (idx < 0) return null;
            int valueStart = idx + name.Length + 1;
            if (valueStart >= tag.Length) return null;
            char quote = tag[valueStart];
            if (quote == '"' || quote == '\'') {
                int valueEnd = tag.IndexOf(quote, valueStart + 1);
                if (valueEnd < 0) return null;
                return tag.Substring(valueStart + 1, valueEnd - valueStart - 1);
            }
            int spaceEnd = valueStart;
            while (spaceEnd < tag.Length && !char.IsWhiteSpace(tag[spaceEnd]) && tag[spaceEnd] != '>' && tag[spaceEnd] != '/') {
                spaceEnd++;
            }
            return tag.Substring(valueStart, spaceEnd - valueStart);
        }

        public void Dispose() {
            if (outputTexture != null) {
                if (Application.isPlaying) UnityEngine.Object.Destroy(outputTexture);
                else UnityEngine.Object.DestroyImmediate(outputTexture);
                outputTexture = null;
            }
        }
    }
}
