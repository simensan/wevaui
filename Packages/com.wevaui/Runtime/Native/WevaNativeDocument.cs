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
            _bindings?.Refresh();
            _doc.Update(0);
        }

        private void Release()
        {
#if WEVA_INPUTSYSTEM
            _input?.Dispose();
            _input = null;
#endif
            _bindings?.Dispose();
            _bindings = null;
            _renderer?.Dispose();
            _renderer = null;
            _fonts?.Dispose();
            _fonts = null;
            _doc?.Dispose();
            _doc = null;
        }

        // ---- events, the way the Godot addon exposes its signals ---------------

        /// <summary>Every polled event, in order.</summary>
        public event Action<NativeEvent> Event;
        /// <summary>A click (pointer or keyboard activation): the element's id.</summary>
        public event Action<string> ElementClicked;
        /// <summary>The value of the named on-&lt;event&gt; attribute and the element's id.</summary>
        public event Action<string, string> HandlerInvoked;
        /// <summary>A form control's value changed by the user: id and the new value.</summary>
        public event Action<string, string> ValueChanged;
        /// <summary>A value committed (focus left a changed field, a box toggled): id and value.</summary>
        public event Action<string, string> Changed;
        /// <summary>A form submitted: the form's id.</summary>
        public event Action<string> FormSubmitted;
        /// <summary>Focus moved: the focused element's id, or empty when dropped.</summary>
        public event Action<string> Focused;

        [Tooltip("Read the Input System's mouse and keyboard every frame and feed them to the document.")]
        public bool AutoInput = true;
        /// <summary>Whether the last frame's input was taken by the document (keep it from gameplay then).</summary>
        public bool InputConsumed { get; private set; }

        private readonly System.Collections.Generic.List<NativeEvent> _events = new System.Collections.Generic.List<NativeEvent>(16);
#if WEVA_INPUTSYSTEM
        private NativeInputFeed _input;
#endif

        /// <summary>
        /// Feed <c>Screen.safeArea</c> to the document each frame it changes, scaled to
        /// the document's viewport, so <c>env(safe-area-inset-*)</c> pads around a notch
        /// or a system bar. Off by default: a desktop has no insets.
        /// </summary>
        public bool FollowScreenSafeArea;
        private Rect _lastSafeArea = new Rect(-1, -1, -1, -1);

        private void SyncSafeArea()
        {
            if (!FollowScreenSafeArea) return;
            Rect safe = Screen.safeArea;
            if (safe == _lastSafeArea) return;
            _lastSafeArea = safe;
            float sw = Math.Max(1, Screen.width), sh = Math.Max(1, Screen.height);
            float sx = _width / sw, sy = _height / sh;
            // Unity's safeArea origin is the bottom-left corner of the screen.
            _doc.SetSafeAreaInsets(
                Math.Max(0, sh - (safe.y + safe.height)) * sy,
                Math.Max(0, sw - (safe.x + safe.width)) * sx,
                Math.Max(0, safe.y) * sy,
                Math.Max(0, safe.x) * sx);
        }

        private void Update()
        {
            if (_doc == null) return;
            try
            {
                SyncSafeArea();
#if WEVA_INPUTSYSTEM
                if (AutoInput && Application.isPlaying)
                {
                    _input ??= new NativeInputFeed(_doc);
                    _input.Tick(_width, _height);
                    InputConsumed = _input.Consumed;
                }
#endif
                // Two clocks, deliberately different. Animations run on scaled
                // time so Time.timeScale pauses them with the rest of the game;
                // timed input gestures run on unscaled time so they keep
                // working while it is paused. Feeding Time.deltaTime to both
                // meant that at timeScale = 0 — a menu over a paused game, the
                // case this host exists for — the core got a microsecond of
                // input time per frame, and scrollbar autoscroll, select
                // typeahead, tooltip delay, held-key repeat and smooth-scroll
                // easing all stopped. The Godot host uses monotonic time here
                // for the same reason.
                float dt = Application.isPlaying ? Time.deltaTime : 0;
                float inputDt = Application.isPlaying ? Mathf.Max(Time.unscaledDeltaTime, 1e-6f) : 0;
                _doc.Update(dt, inputDt);
                PumpEvents();
            }
            catch (NativeException ex)
            {
                LastError = ex.Message;
            }
        }

        /// <summary>Drains the core's event queue into the C# events. Called after every update.</summary>
        public void PumpEvents()
        {
            if (_doc == null) return;
            _events.Clear();
            _doc.PollEvents(_events);
            foreach (NativeEvent e in _events)
            {
                Event?.Invoke(e);
                string id = e.Target == WevaNative.WEVA_ELEMENT_NONE ? string.Empty : _doc.ElementId(e.Target);
                if (e.Handler.Length > 0)
                {
                    HandlerInvoked?.Invoke(e.Handler, id);
                    Dispatch(e.Handler, id);
                }
                switch (e.Kind)
                {
                    case weva_event_kind.WEVA_EVENT_CLICK: ElementClicked?.Invoke(id); break;
                    case weva_event_kind.WEVA_EVENT_VALUE_CHANGED:
                        ValueChanged?.Invoke(id, _doc.ElementValue(e.Target));
                        // A control wrote into the model: everything else bound
                        // to that path (a HUD label) follows on this pump.
                        if (_bindings != null && _bindings.WriteBack(e.Target)) _refreshPending = true;
                        break;
                    case weva_event_kind.WEVA_EVENT_CHANGE:
                        Changed?.Invoke(id, _doc.ElementValue(e.Target));
                        if (_bindings != null && _bindings.WriteBack(e.Target)) _refreshPending = true;
                        break;
                    case weva_event_kind.WEVA_EVENT_SUBMIT: FormSubmitted?.Invoke(id); break;
                    case weva_event_kind.WEVA_EVENT_FOCUS: Focused?.Invoke(id); break;
                    case weva_event_kind.WEVA_EVENT_BLUR: Focused?.Invoke(string.Empty); break;
                }
            }
            if (_refreshPending) Refresh();
        }

        // ---- data binding and the controller ----------------------------------

        private NativeBindings _bindings;
        private object _controller;
        private bool _refreshPending;

        /// <summary>The bound data, or null before Bind.</summary>
        public System.Collections.Generic.IDictionary<string, object> Data => _bindings?.Data;
        /// <summary>A data-model control wrote into Data: the path and the text.</summary>
        public event Action<string, string> DataChanged;

        /// <summary>
        /// Binds the document to a data model and a controller: `{{ path }}`,
        /// data-class, data-each and data-model read the model; an
        /// `on-&lt;event&gt;="Name"` attribute calls the controller's public
        /// method `Name(string id)` (or `Name()`), the way the Godot addon's
        /// bind_state does. Call RequestRefresh when the model changes.
        /// </summary>
        public void Bind(System.Collections.Generic.IDictionary<string, object> model, object controller = null)
        {
            _controller = controller;
            if (_doc == null) return;
            if (_bindings == null)
            {
                _bindings = new NativeBindings();
                _bindings.DataChanged += (path, text) => DataChanged?.Invoke(path, text);
            }
            _bindings.Data = model ?? new System.Collections.Generic.Dictionary<string, object>();
            _bindings.Install(_doc);
            _refreshPending = false;
        }

        public object Controller
        {
            get => _controller;
            set => _controller = value;
        }

        /// <summary>Re-reads every binding now. Returns how many nodes changed.</summary>
        public int Refresh()
        {
            _refreshPending = false;
            return _bindings?.Refresh() ?? 0;
        }

        /// <summary>Refreshes on the next update, once, however many times the model moved.</summary>
        public void RequestRefresh()
        {
            _refreshPending = true;
        }

        /// <summary>The data-each row an element sits in (index and key), for a handler that needs its item.</summary>
        public bool TryGetRow(uint element, out int index, out string key)
        {
            index = -1;
            key = string.Empty;
            return _doc != null && _doc.TryGetRow(element, out index, out key);
        }

        private void Dispatch(string handler, string id)
        {
            if (_controller == null) return;
            System.Reflection.MethodInfo method = _controller.GetType().GetMethod(handler,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (method != null)
            {
                method.Invoke(_controller, new object[] { id });
                return;
            }
            method = _controller.GetType().GetMethod(handler, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
            method?.Invoke(_controller, null);
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

        public void EmitNative(UnityEngine.Rendering.CommandBuffer cmd, int viewportWidth, int viewportHeight)
        {
            if (_doc == null || _renderer == null || cmd == null) return;
            _renderer.Sync(_doc);
            // A camera pass into the camera's colour buffer: the shader takes
            // the flip from _ProjectionParams.x and blends in linear space.
            _renderer.Draw(cmd, viewportWidth > 0 ? viewportWidth : _width, viewportHeight > 0 ? viewportHeight : _height, 0, false);
            _drawnSerial = _doc.DrawSerial;
        }
#endif
    }
}
