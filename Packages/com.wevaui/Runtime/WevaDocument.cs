// The author-facing component: attach it to a GameObject, assign HTML and
// CSS TextAssets, and the shared C++ core (weva_core) parses, cascades, lays
// out and paints the page; UnityFontBackend answers its font callbacks with
// FontEngine, NativeDocumentRenderer uploads its draw list to the URP pass,
// NativeInputFeed feeds it the Input System. Attach a controller
// (SetController) to bind [UIBind] values and on-<event> handlers.
//
// This replaced the C# engine's component of the same name at 1.0; that one
// is WevaLegacyDocument until Phase 4.3 deletes it. The serialized field
// names (documentAsset, stylesheetAssets, sortingOrder,
// prefersDarkColorScheme) are kept so a scene carries over.
using System;
using UnityEngine;
using Weva.Native;
#if WEVA_URP
using Weva.Rendering;
#endif

namespace Weva
{
    [AddComponentMenu("Weva/UI Document")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class WevaDocument : MonoBehaviour
#if WEVA_URP
        , IUINativePaintSource, IRenderViewportAwarePaintSource
#endif
    {
        [SerializeField, Tooltip("The document's markup. InlineHtml is used when no asset is set.")]
        TextAsset documentAsset;
        [SerializeField, Tooltip("The stylesheets, applied in order after the user-agent sheet. InlineCss is used when none is set.")]
        TextAsset[] stylesheetAssets;
        [SerializeField, Tooltip("Documents paint in ascending order; equal orders paint in registration order.")]
        int sortingOrder;
        [SerializeField, Tooltip("Answer prefers-color-scheme: dark.")]
        bool prefersDarkColorScheme;
        [TextArea(3, 12)] public string InlineHtml = "<body><h1>Weva</h1><p>Hosted by the native core.</p></body>";
        [TextArea(2, 8)] public string InlineCss = "";

        public TextAsset DocumentAsset
        {
            get => documentAsset;
            set { documentAsset = value; Reload(); }
        }

        /// <summary>The stylesheets this document applies, in order. The getter returns a copy; assign the whole array to change them.</summary>
        public TextAsset[] StylesheetAssets
        {
            get => stylesheetAssets == null ? null : (TextAsset[])stylesheetAssets.Clone();
            set { stylesheetAssets = value; Reload(); }
        }

        public int SortingOrder
        {
            get => sortingOrder;
            set => sortingOrder = value;
        }

        public bool PrefersDarkColorScheme
        {
            get => prefersDarkColorScheme;
            set { prefersDarkColorScheme = value; _doc?.SetColorScheme(value); }
        }
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
                Debug.LogError("WevaDocument: " + ex.Message, this);
                Release();
                return;
            }
#if WEVA_URP
            UIPaintSourceRegistry.Register(this);
#endif
        }

        // An inspector edit reloads on the next update, not inside OnValidate
        // (which also runs during domain reload, when the core is not there).
        private bool _reloadPending;

        private void OnValidate()
        {
            _reloadPending = true;
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
            _doc.LoadHtml(documentAsset != null ? documentAsset.text : InlineHtml);
            _doc.SetCss(StylesheetText());
            _doc.SetColorScheme(prefersDarkColorScheme);
            _fonts.SyncCssFontFaces(_doc);
            // A reload replaces the tree the binding source was installed on,
            // and a controller set before the core existed still applies.
            if (_bindings != null || _controller != null) SetController(_controller);
            _doc.Update(0);
        }

        // The assets in order, one sheet after another the way <link>s
        // cascade; the inline text only when no asset is set.
        private string StylesheetText()
        {
            if (stylesheetAssets == null || stylesheetAssets.Length == 0) return InlineCss;
            var sb = new System.Text.StringBuilder();
            foreach (TextAsset sheet in stylesheetAssets)
            {
                if (sheet == null) continue;
                sb.Append(sheet.text).Append('\n');
            }
            return sb.ToString();
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
                if (_reloadPending)
                {
                    _reloadPending = false;
                    Reload();
                }
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
                Step(dt, inputDt);
            }
            catch (NativeException ex)
            {
                LastError = ex.Message;
            }
        }

        /// <summary>One frame without the input feed: poll the controller, refresh what moved, update, pump. Tests drive the component with this.</summary>
        internal void Step(float dt = 0, float inputDt = 0)
        {
            if (_doc == null) return;
            // The controller is read AFTER the frame's events are pumped: a
            // control's VALUE_CHANGED writes into the controller first, so the
            // poll that follows pushes nothing stale back over what was typed.
            PollController();
            _doc.Update(dt, inputDt);
            PumpEvents();
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
        private System.Collections.Generic.IDictionary<string, object> _model = new System.Collections.Generic.Dictionary<string, object>();
        private object _controller;
        private UIBindResolver _uiBind;
        private Weva.Binding.IBindingVersion _versioned;
        private int _lastVersion;
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
            _model = model ?? new System.Collections.Generic.Dictionary<string, object>();
            SetController(controller);
        }

        /// <summary>
        /// Attach (or replace) the controller whose <c>[UIBind]</c> fields and
        /// properties feed <c>{{ }}</c>, data-class, data-each and data-model,
        /// and whose public methods answer <c>on-&lt;event&gt;="Name"</c>. A
        /// <see cref="Weva.Binding.IBindingVersion"/> controller is re-read only
        /// when its version moves; any other controller is polled every frame,
        /// so mutating a <c>[UIBind]</c> field anywhere is enough. Null detaches.
        /// </summary>
        public void SetController(object newController)
        {
            _controller = newController;
            _uiBind = newController != null ? new UIBindResolver(newController) : null;
            _versioned = newController as Weva.Binding.IBindingVersion;
            _lastVersion = _versioned != null ? _versioned.BindingVersion : 0;
            if (_doc == null) return;
            if (_bindings == null)
            {
                _bindings = new NativeBindings();
                _bindings.DataChanged += (path, text) => DataChanged?.Invoke(path, text);
            }
            _bindings.Data = _model;
            _bindings.Resolver = _uiBind != null && _uiBind.RootCount > 0 ? _uiBind.Resolve : (Func<string, object>)null;
            _bindings.Writer = _uiBind != null && _uiBind.RootCount > 0 ? _uiBind.TryWrite : (Func<string, string, bool>)null;
            _bindings.Install(_doc);
            _refreshPending = false;
        }

        /// <summary>The controller attached via <see cref="SetController"/>, cast to <typeparamref name="T"/> (null if none or the cast fails).</summary>
        public T GetController<T>() where T : class
        {
            return _controller as T;
        }

        public object Controller
        {
            get => _controller;
            set => SetController(value);
        }

        /// <summary>Re-reads every binding now. Returns how many nodes changed.</summary>
        public int Refresh()
        {
            _refreshPending = false;
            if (_versioned != null) _lastVersion = _versioned.BindingVersion;
            return _bindings?.Refresh() ?? 0;
        }

        // The C# engine polled [UIBind] members once a frame; a controller
        // that implements IBindingVersion promised to bump instead. Both
        // contracts hold here: the version gate skips the read, and a
        // controller without one is read every frame the way it always was.
        private void PollController()
        {
            if (_bindings == null || _uiBind == null) return;
            if (_versioned != null)
            {
                if (_versioned.BindingVersion == _lastVersion) return;
                _lastVersion = _versioned.BindingVersion;
            }
            _refreshPending = true;
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
        public int Order => sortingOrder;

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
