// The author-facing component: attach it to a GameObject, assign HTML and
// CSS TextAssets, and the shared C++ core (weva_core) parses, cascades, lays
// out and paints the page; UnityFontBackend answers its font callbacks with
// FontEngine, NativeDocumentRenderer uploads its draw list to the URP pass,
// NativeInputFeed feeds it the Input System. Attach a controller
// (SetController) to bind [UIBind] values and on-<event> handlers.
//
// This replaced the C# engine's component of the same name at 1.0 and
// carries its script GUID and serialized field names (documentAsset,
// stylesheetAssets, sortingOrder, prefersDarkColorScheme), so a 0.1.x scene
// binds to it unchanged.
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
        [Tooltip("The machine's installed fonts: a family the page names in font-family (with its bold and italic files) as a browser resolves it, and after the fallbacks the platform's fonts (Segoe UI, Nirmala UI and Segoe UI Symbol; Arial, Kohinoor Devanagari and Apple Symbols; DejaVu Sans) for scripts and symbols none of the faces carry. Off for output identical on every machine.")]
        public bool SystemFontFallback = true;
        [Tooltip("Directory that relative url() and @font-face sources resolve against (editor and desktop file paths).")]
        public string BasePath = "";
        public bool UseUserAgentStylesheet = true;

        private NativeDocument _doc;
        private UnityFontBackend _fonts;
        private NativeDocumentRenderer _renderer;
        private ulong _drawnSerial;
        private int _width, _height;

        internal NativeDocument Document => _doc;
        internal UnityFontBackend Fonts => _fonts;
        internal NativeDocumentRenderer Renderer => _renderer;
        public string LastError { get; private set; }

        /// <summary>Bumped whenever the tree is replaced (Reload, enable): an element handle from an earlier generation is stale.</summary>
        internal int Generation { get; private set; }

        // ---- elements ----------------------------------------------------------

        /// <summary>The first element a CSS selector matches, or WevaElement.None.</summary>
        public WevaElement Query(string selector)
        {
            if (_doc == null || string.IsNullOrEmpty(selector)) return WevaElement.None;
            return new WevaElement(this, _doc.Query(selector), Generation);
        }

        /// <summary>Every element a CSS selector matches, in document order.</summary>
        public WevaElement[] QueryAll(string selector)
        {
            if (_doc == null || string.IsNullOrEmpty(selector)) return Array.Empty<WevaElement>();
            uint[] handles = _doc.QueryAll(selector);
            var result = new WevaElement[handles.Length];
            for (int i = 0; i < handles.Length; i++) result[i] = new WevaElement(this, handles[i], Generation);
            return result;
        }

        /// <summary>The element that has keyboard focus, or None.</summary>
        public WevaElement FocusedElement => _doc != null ? new WevaElement(this, _doc.Focus, Generation) : WevaElement.None;

        /// <summary>The CSS <c>cursor</c> keyword under the pointer (<c>pointer</c>, <c>text</c>, <c>not-allowed</c>, ... or <c>default</c>), for a game to map to its own cursor textures.</summary>
        public string Cursor => _doc != null ? _doc.Cursor : "default";

        /// <summary>What <c>env(safe-area-inset-*)</c> resolves to, in document pixels. FollowScreenSafeArea sets this from Screen.safeArea.</summary>
        public void SetSafeAreaInsets(float top, float right, float bottom, float left)
        {
            _doc?.SetSafeAreaInsets(top, right, bottom, left);
        }

        private Func<string, byte[]> _assetReader;
        private readonly System.Collections.Generic.Dictionary<string, Font> _fontFamilies = new System.Collections.Generic.Dictionary<string, Font>();

        /// <summary>
        /// How the document obtains an asset's bytes (images, @font-face files):
        /// a path as written in the markup, resolved by you; null for one you do
        /// not have. Unset, the document reads files relative to BasePath.
        /// </summary>
        public Func<string, byte[]> AssetReader
        {
            get => _assetReader;
            set
            {
                _assetReader = value;
                if (_doc != null) _doc.AssetReader = value;
            }
        }

        /// <summary>Names a Unity font for CSS <c>font-family</c>, the way an @font-face would. Survives a reload and a disable.</summary>
        public void RegisterFontFamily(string family, Font font)
        {
            if (string.IsNullOrEmpty(family)) throw new ArgumentException("a family name is required", nameof(family));
            if (font == null) throw new ArgumentNullException(nameof(font));
            _fontFamilies[family] = font;
            // Through the backend, which records the family as the game's so a
            // page's @font-face or an installed font of the same name never
            // takes it over.
            if (_doc != null && _fonts != null) _fonts.RegisterFontFamily(_doc, family, _fonts.Adopt(font));
        }

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
            // Last, the platform's fonts, as a browser falls back to a system
            // font for a script or glyph the page's fonts lack.
            if (SystemFontFallback)
            {
                foreach (string name in UnityFontBackend.SystemFallbackFonts)
                {
                    ulong installed = _fonts.AdoptInstalled(name);
                    if (installed != 0) fallbackFaces.Add(installed);
                }
            }
            if (fallbackFaces.Count > 0) _fonts.SetFallbacks(face, fallbackFaces.ToArray());
            _fonts.Install(_doc, face);
            foreach (var family in _fontFamilies) _fonts.RegisterFontFamily(_doc, family.Key, _fonts.Adopt(family.Value));
            if (_assetReader != null) _doc.AssetReader = _assetReader;
            _renderer = new NativeDocumentRenderer();
            Reload();
        }

        /// <summary>Loads the markup and stylesheet again from the assets or inline text.</summary>
        public void Reload()
        {
            if (_doc == null) return;
            string basePath = BasePath;
#if UNITY_EDITOR
            // url(), @import and @font-face resolve next to the document
            // asset unless the author points elsewhere.
            if (string.IsNullOrEmpty(basePath)) basePath = DocumentAssetDirectory();
#endif
            if (!string.IsNullOrEmpty(basePath)) _doc.SetBasePath(basePath);
            string html = documentAsset != null ? documentAsset.text : InlineHtml;
            Generation++;
            _doc.LoadHtml(html);
            _linkedHrefs.Clear();
            _linkedHrefs.AddRange(LinkedHrefs(_doc));
            _doc.SetCss(StylesheetText());
            _doc.SetColorScheme(prefersDarkColorScheme);
            _fonts.SyncCssFontFaces(_doc);
            // Then the families the page names that nothing else serves, from
            // the installed fonts, as a browser resolves `font-family`.
            if (SystemFontFallback) _fonts.SyncInstalledFamilies(_doc);
            // A reload replaces the tree the binding source was installed on,
            // and a controller set before the core existed still applies.
            if (_bindingRequested) SetController(_controller);
            _doc.Update(0);
        }

        // The page's <link rel="stylesheet"> sheets in document order, then
        // the inspector's assets (a later sheet wins, as in a browser); the
        // inline text only when the page links nothing and no asset is set.
        private string StylesheetText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (string href in _linkedHrefs)
            {
                string css = ResolveLinkedStylesheet(href);
                if (css == null)
                {
                    Debug.LogWarning($"WevaDocument on '{name}': linked stylesheet '{href}' not found (next to the document asset, baked, or under BasePath).", this);
                    continue;
                }
                sb.Append(css).Append('\n');
            }
            if (stylesheetAssets != null)
            {
                foreach (TextAsset sheet in stylesheetAssets)
                {
                    if (sheet == null) continue;
                    sb.Append(sheet.text).Append('\n');
                }
            }
            if (sb.Length == 0) return InlineCss;
            return sb.ToString();
        }

        // ---- <link rel="stylesheet"> -------------------------------------------
        //
        // The core reads <style> elements and @import; a <link> is the host's
        // to fetch. In the editor the sheet is the TextAsset next to the
        // document asset (the live file, so an edit is never shadowed); a
        // player has no files, so the scene-processing hook bakes every
        // linked sheet's text into the component (WevaDocumentLinkBaker);
        // the core's asset reader under BasePath is the last resort, for a
        // desktop build shipping its UI as files.

        [SerializeField, HideInInspector] string[] bakedLinkedStylesheetHrefs;
        [SerializeField, HideInInspector] string[] bakedLinkedStylesheetCss;
        private readonly System.Collections.Generic.List<string> _linkedHrefs = new System.Collections.Generic.List<string>();

        /// <summary>The href of every <c>&lt;link rel="stylesheet"&gt;</c> in the markup, in document order.</summary>
        public static System.Collections.Generic.List<string> LinkedHrefs(string html)
        {
            if (string.IsNullOrEmpty(html)) return new System.Collections.Generic.List<string>();
            // Baking runs without a live document. Use the same parser and DOM
            // queries as Reload; no fonts or layout are needed to discover links.
            using (var parsed = new NativeDocument(1, 1, useUserAgentStylesheet: false))
            {
                parsed.LoadHtml(html);
                return LinkedHrefs(parsed);
            }
        }

        private static System.Collections.Generic.List<string> LinkedHrefs(NativeDocument parsed)
        {
            var hrefs = new System.Collections.Generic.List<string>();
            foreach (uint link in parsed.QueryAll("link[rel~=\"stylesheet\" i][href]"))
            {
                string href = parsed.ElementAttribute(link, "href");
                if (!string.IsNullOrEmpty(href)) hrefs.Add(href);
            }
            return hrefs;
        }

        /// <summary>The hrefs the current markup links, after a Reload.</summary>
        public System.Collections.Generic.IReadOnlyList<string> LinkedStylesheetHrefs => _linkedHrefs;

        /// <summary>What the last bake stored (for the baker to tell a changed bake from a repeated one).</summary>
        internal (string[] Hrefs, string[] Css) BakedLinkedStylesheets => (bakedLinkedStylesheetHrefs, bakedLinkedStylesheetCss);

        /// <summary>
        /// Stores the text of every linked stylesheet on the component for a
        /// player, which has no files to read; <paramref name="read"/> returns
        /// a sheet's text for an href, or null. Returns how many were baked.
        /// The editor's scene-processing hook calls this at build time.
        /// </summary>
        public int BakeLinkedStylesheets(Func<string, string> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            var hrefs = LinkedHrefs(documentAsset != null ? documentAsset.text : InlineHtml);
            var keptHrefs = new System.Collections.Generic.List<string>();
            var keptCss = new System.Collections.Generic.List<string>();
            foreach (string href in hrefs)
            {
                string css = read(href);
                if (css == null) continue;
                keptHrefs.Add(href);
                keptCss.Add(css);
            }
            bakedLinkedStylesheetHrefs = keptHrefs.ToArray();
            bakedLinkedStylesheetCss = keptCss.ToArray();
            return keptHrefs.Count;
        }

        private string ResolveLinkedStylesheet(string href)
        {
#if UNITY_EDITOR
            string path = DocumentAssetDirectory();
            if (path != null)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(System.IO.Path.Combine(path, href).Replace('\\', '/'));
                if (asset != null) return asset.text;
            }
#endif
            if (bakedLinkedStylesheetHrefs != null && bakedLinkedStylesheetCss != null)
            {
                for (int i = 0; i < bakedLinkedStylesheetHrefs.Length && i < bakedLinkedStylesheetCss.Length; i++)
                {
                    if (bakedLinkedStylesheetHrefs[i] == href) return bakedLinkedStylesheetCss[i];
                }
            }
            byte[] bytes = _doc?.AssetReader?.Invoke(href);
            return bytes != null ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }

#if UNITY_EDITOR
        /// <summary>The asset-database directory of the document asset (editor only), or null for an in-memory asset.</summary>
        public string DocumentAssetDirectory()
        {
            if (documentAsset == null) return null;
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(documentAsset);
            if (string.IsNullOrEmpty(assetPath)) return null;
            return System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        }
#endif

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

        /// <summary>Every core event, in order, with its kind, target, position and text.</summary>
        public event Action<WevaEvent> Event;
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
        private bool _pumpingEvents;
        private bool _wrapTab = true;
        private bool _gamepadTextEntry;
        private bool _acceptsKeyboard = true;

        /// <summary>Tab and Shift+Tab wrap inside the document (true), or leave it and raise TabbedOut so the host continues its own focus chain.</summary>
        public bool WrapTab
        {
            get => _wrapTab;
            set { _wrapTab = value; ApplyInputKnobs(); }
        }

        /// <summary>Tab left the document (only with WrapTab off): true when backwards.</summary>
        public event Action<bool> TabbedOut;

        /// <summary>A gamepad's accept button on a text field raises TextEntryRequested (open your on-screen keyboard) instead of pressing Enter.</summary>
        public bool GamepadTextEntry
        {
            get => _gamepadTextEntry;
            set { _gamepadTextEntry = value; ApplyInputKnobs(); }
        }

        /// <summary>The focused text control asked for text entry from a gamepad: its id. Update its bound model when the player is done, or its Value if unbound.</summary>
        public event Action<string> TextEntryRequested;

        /// <summary>Whether keys and text reach the document; the pointer always does. Off keeps the keyboard for the game.</summary>
        public bool AcceptsKeyboard
        {
            get => _acceptsKeyboard;
            set { _acceptsKeyboard = value; ApplyInputKnobs(); }
        }

#if WEVA_INPUTSYSTEM
        private NativeInputFeed _input;

        /// <summary>The Input System feed, created on demand (the first playing frame with AutoInput on, or here).</summary>
        internal NativeInputFeed Input
        {
            get
            {
                if (_input == null && _doc != null) CreateInputFeed();
                return _input;
            }
        }

        private void CreateInputFeed()
        {
            _input = new NativeInputFeed(_doc);
            _input.Order = () => sortingOrder;
            _input.TabbedOut += backwards => TabbedOut?.Invoke(backwards);
            _input.TextEntryRequested += id => TextEntryRequested?.Invoke(id);
            ApplyInputKnobs();
        }

        private void ApplyInputKnobs()
        {
            if (_input == null) return;
            _input.WrapTab = _wrapTab;
            _input.GamepadTextEntry = _gamepadTextEntry;
            _input.AcceptsKeyboard = _acceptsKeyboard;
        }
#else
        private void ApplyInputKnobs() { }
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
                    if (_input == null) CreateInputFeed();
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

        /// <summary>Drains the core's event queue into the C# events. A callback that reloads or disables the document ends this batch; recursive calls defer to the next pump.</summary>
        public void PumpEvents()
        {
            if (_doc == null || _pumpingEvents) return;
            NativeDocument document = _doc;
            int generation = Generation;
            bool IsCurrent() => ReferenceEquals(_doc, document) && Generation == generation;
            _pumpingEvents = true;
            try
            {
                _events.Clear();
                document.PollEvents(_events);
                foreach (NativeEvent e in _events)
                {
                    // Every notification can run game code (including closing
                    // this menu). Never interpret an old target in a new tree,
                    // or continue dispatching after the native document dies.
                    if (!IsCurrent()) return;
                    string id = e.Target == WevaNative.WEVA_ELEMENT_NONE ? string.Empty : document.ElementId(e.Target);
                    Event?.Invoke(new WevaEvent((WevaEventKind)(int)e.Kind, new WevaElement(this, e.Target, generation),
                        new Vector2((float)e.X, (float)e.Y), e.Buttons, e.Modifiers, e.Text, e.Handler));
                    if (!IsCurrent()) return;
                    if (e.Handler.Length > 0)
                    {
                        HandlerInvoked?.Invoke(e.Handler, id);
                        if (!IsCurrent()) return;
                        Dispatch(e.Handler, id);
                        if (!IsCurrent()) return;
                    }
                    switch (e.Kind)
                    {
                        case weva_event_kind.WEVA_EVENT_CLICK: ElementClicked?.Invoke(id); break;
                        case weva_event_kind.WEVA_EVENT_VALUE_CHANGED:
                            ValueChanged?.Invoke(id, document.ElementValue(e.Target));
                            if (!IsCurrent()) return;
                            // A control wrote into the model: everything else
                            // bound to that path follows on this pump.
                            if (_bindings != null && _bindings.WriteBack(e.Target)) _refreshPending = true;
                            break;
                        case weva_event_kind.WEVA_EVENT_CHANGE:
                            Changed?.Invoke(id, document.ElementValue(e.Target));
                            if (!IsCurrent()) return;
                            if (_bindings != null && _bindings.WriteBack(e.Target)) _refreshPending = true;
                            break;
                        case weva_event_kind.WEVA_EVENT_SUBMIT: FormSubmitted?.Invoke(id); break;
                        case weva_event_kind.WEVA_EVENT_FOCUS: Focused?.Invoke(id); break;
                        case weva_event_kind.WEVA_EVENT_BLUR: Focused?.Invoke(string.Empty); break;
                    }
                }
                if (IsCurrent() && _refreshPending) Refresh();
            }
            finally
            {
                _events.Clear();
                _pumpingEvents = false;
            }
        }

        // ---- data binding and the controller ----------------------------------

        private NativeBindings _bindings;
        private System.Collections.Generic.IDictionary<string, object> _model = new System.Collections.Generic.Dictionary<string, object>();
        private object _controller;
        private UIBindResolver _uiBind;
        private Weva.Binding.IBindingVersion _versioned;
        private int _lastVersion;
        private bool _refreshPending;
        private bool _bindingRequested;

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
            // Remember the request independently of the native adapter, which
            // does not exist while disabled. Bind(model) also uses this path
            // when there is no controller.
            _bindingRequested = true;
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
        internal bool TryGetRow(uint element, out int index, out string key)
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

        public void PrepareForRenderViewport(int width, int height)
        {
            if (_doc == null || width <= 0 || height <= 0 || (width == _width && height == _height)) return;
            _width = width;
            _height = height;
            _doc.SetViewport(width, height);
            _doc.Update(0);
        }

        // Explicit: the target type is the host's (internal), the class is not.
        void IUINativePaintSource.EmitNative(UnityEngine.Rendering.CommandBuffer cmd, int viewportWidth, int viewportHeight, in NativeRenderTarget target)
        {
            if (_doc == null || _renderer == null || cmd == null) return;
            _renderer.Sync(_doc);
            // A camera pass into the camera's colour buffer: the shader takes
            // the flip from _ProjectionParams.x and blends in linear space.
            _renderer.Draw(cmd, viewportWidth > 0 ? viewportWidth : _width, viewportHeight > 0 ? viewportHeight : _height, 0, false, target);
            _drawnSerial = _doc.DrawSerial;
        }

        // Asked by the renderer feature before the pass is enqueued; the sync
        // here is the one EmitNative would do (it is keyed on the draw serial).
        bool IUINativePaintSource.NeedsBackdropCopy
        {
            get
            {
                if (_doc == null || _renderer == null) return false;
                _renderer.Sync(_doc);
                return _renderer.BackdropDraws > 0;
            }
        }
#endif
    }
}
