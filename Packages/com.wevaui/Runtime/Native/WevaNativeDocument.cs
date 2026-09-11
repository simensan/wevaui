// An HTML document hosted by the shared C++ core (weva_core) and drawn by the
// URP render pass, beside the C# engine's WevaDocument. This is the Unity
// host prototype: the core lays out and paints, UnityFontBackend answers
// its font callbacks with FontEngine, NativeDocumentRenderer uploads its
// draw list. Input, events and bindings follow in later steps of the plan.
using System;
using UnityEngine;
#if WEVA_URP
using Weva.Rendering;
#endif

namespace Weva.Native
{
    [AddComponentMenu("Weva/Native Document (core prototype)")]
    [ExecuteAlways]
    public sealed class WevaNativeDocument : MonoBehaviour
#if WEVA_URP
        , IUINativePaintSource, IRenderViewportAwarePaintSource
#endif
    {
        [Tooltip("The document's markup. InlineHtml is used when no asset is set.")]
        public TextAsset Html;
        public TextAsset Css;
        [TextArea(3, 12)] public string InlineHtml = "<body><h1>Weva</h1><p>Hosted by the native core.</p></body>";
        [TextArea(2, 8)] public string InlineCss = "";
        [Tooltip("The UI face; the package's default (Inter) when empty.")]
        public Font Font;
        [Tooltip("Real bold and italic faces for the UI face; the package's when empty and the default face is used.")]
        public Font Bold;
        public Font Italic;
        [Tooltip("Faces tried for code points the UI face lacks; the package's symbol face when empty.")]
        public Font[] Fallbacks;
        [Tooltip("Directory that relative url() and @font-face sources resolve against (editor and desktop file paths).")]
        public string BasePath = "";
        public bool UseUserAgentStylesheet = true;
        public int PaintOrder = 0;

        private NativeDocument _doc;
        private UnityFontBackend _fonts;
        private NativeDocumentRenderer _renderer;
        private ulong _drawnSerial;
        private int _width, _height;

        public NativeDocument Document => _doc;
        public UnityFontBackend Fonts => _fonts;
        public NativeDocumentRenderer Renderer => _renderer;
        public string LastError { get; private set; }

        private void OnEnable()
        {
            try
            {
                Create();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError("WevaNativeDocument: " + ex.Message, this);
                Release();
                return;
            }
#if WEVA_URP
            UIPaintSourceRegistry.Register(this);
#endif
        }

        private void OnDisable()
        {
#if WEVA_URP
            UIPaintSourceRegistry.Unregister(this);
#endif
            Release();
        }

        private void Create()
        {
            _width = Math.Max(1, Screen.width);
            _height = Math.Max(1, Screen.height);
            _doc = new NativeDocument(_width, _height, 1.0, 16.0, UseUserAgentStylesheet);
            _fonts = new UnityFontBackend();
            Font regular = Font != null ? Font : Resources.Load<Font>("Fonts/Weva-Default");
            if (regular == null) throw new InvalidOperationException("no UI font: set Font or keep the package's Resources/Fonts");
            ulong face = _fonts.Adopt(regular);
            Font bold = Bold != null ? Bold : (Font == null ? Resources.Load<Font>("Fonts/Weva-Default-Bold") : null);
            Font italic = Italic != null ? Italic : (Font == null ? Resources.Load<Font>("Fonts/Weva-Default-Italic") : null);
            if (bold != null) _fonts.SetRealVariant(face, 700, false, _fonts.Adopt(bold));
            if (italic != null) _fonts.SetRealVariant(face, 400, true, _fonts.Adopt(italic));
            Font[] fallbacks = Fallbacks != null && Fallbacks.Length > 0 ? Fallbacks : new[] { Resources.Load<Font>("Fonts/NotoSansSymbols2-Regular") };
            var fallbackFaces = new System.Collections.Generic.List<ulong>();
            foreach (Font f in fallbacks) if (f != null) fallbackFaces.Add(_fonts.Adopt(f));
            if (fallbackFaces.Count > 0) _fonts.SetFallbacks(face, fallbackFaces.ToArray());
            _fonts.Install(_doc, face);
            _renderer = new NativeDocumentRenderer();
            Reload();
        }

        /// <summary>Loads the markup and stylesheet again from the assets or inline text.</summary>
        public void Reload()
        {
            if (_doc == null) return;
            if (!string.IsNullOrEmpty(BasePath)) _doc.SetBasePath(BasePath);
            _doc.LoadHtml(Html != null ? Html.text : InlineHtml);
            _doc.SetCss(Css != null ? Css.text : InlineCss);
            _fonts.SyncCssFontFaces(_doc);
            _doc.Update(0);
        }

        private void Release()
        {
            _renderer?.Dispose();
            _renderer = null;
            _fonts?.Dispose();
            _fonts = null;
            _doc?.Dispose();
            _doc = null;
        }

        private void Update()
        {
            if (_doc == null) return;
            try
            {
                _doc.Update(Application.isPlaying ? Time.deltaTime : 0);
            }
            catch (NativeException ex)
            {
                LastError = ex.Message;
            }
        }

#if WEVA_URP
        public int Order => PaintOrder;

        public bool NeedsRepaint => _doc != null && (_doc.DrawSerial != _drawnSerial || _doc.IsAnimating);

        public void EmitPaint(Weva.Paint.IRenderBackend backend)
        {
            // Nothing: this source publishes geometry through EmitNative.
        }

        public void PrepareForRenderViewport(int width, int height)
        {
            if (_doc == null || width <= 0 || height <= 0 || (width == _width && height == _height)) return;
            _width = width;
            _height = height;
            _doc.SetViewport(width, height);
            _doc.Update(0);
        }

        public void EmitNative(IUICommandBuffer cmd, int viewportWidth, int viewportHeight)
        {
            if (_doc == null || _renderer == null || cmd == null) return;
            _renderer.Sync(_doc);
            _renderer.Draw(cmd, viewportWidth > 0 ? viewportWidth : _width, viewportHeight > 0 ? viewportHeight : _height);
            _drawnSerial = _doc.DrawSerial;
        }
#endif
    }
}
