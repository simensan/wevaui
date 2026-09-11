// A document hosted by libweva (the shared C++ core) through the weva_core
// plugin. This is the hand-written edge over the generated P/Invoke surface
// in WevaNative.g.cs: UTF-8 marshalling, status checks and ownership. It
// does not touch the C# engine; the two coexist while the Unity host over
// the core is built up (fonts, rendering, input come in later steps).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Weva.Native
{
    /// <summary>A failed call into the core, with the status the core returned.</summary>
    public sealed class NativeException : Exception
    {
        public int Status { get; }

        public NativeException(string operation, int status)
            : base($"weva_core: {operation} failed with status {status} ({(weva_status)status})")
        {
            Status = status;
        }
    }

    /// <summary>Layout bounds of an element in CSS pixels, as the core reports them.</summary>
    public struct NativeBounds
    {
        public double X, Y, Width, Height;
    }

    /// <summary>One queued document event, as weva_event with its full text read back.</summary>
    public struct NativeEvent
    {
        public weva_event_kind Kind;
        public uint Target;
        public double X, Y;
        public uint Buttons;
        public weva_key Key;
        public uint Modifiers;
        /// <summary>The text a key produced, a value, or a toggle state; complete, not the struct's 8-byte prefix.</summary>
        public string Text;
        /// <summary>The value of the nearest `on-&lt;event&gt;` attribute, or empty.</summary>
        public string Handler;
    }

    public sealed unsafe class NativeDocument : IDisposable
    {
        private IntPtr _handle;

        /// <summary>The ABI version the loaded plugin reports, as (major, minor).</summary>
        public static (int Major, int Minor) AbiVersion()
        {
            uint version = WevaNative.weva_abi_version();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }

        /// <summary>
        /// The native size of a weva_c.h struct, or 0 for a name the plugin does
        /// not know. Tests compare the generated mirrors against it.
        /// </summary>
        public static int NativeSizeOf(string structName)
        {
            byte[] name = NullTerminated(structName);
            fixed (byte* p = name)
            {
                return (int)WevaNative.weva_unity_sizeof(p);
            }
        }

        public NativeDocument(int viewportWidth, int viewportHeight, double devicePixelRatio = 1.0,
                              double rootFontSize = 16.0, bool useUserAgentStylesheet = true)
        {
            var config = new weva_config
            {
                viewport_width = viewportWidth,
                viewport_height = viewportHeight,
                device_pixel_ratio = devicePixelRatio,
                root_font_size = rootFontSize,
                use_user_agent_stylesheet = useUserAgentStylesheet ? 1 : 0,
            };
            _handle = WevaNative.weva_document_create(&config);
            if (_handle == IntPtr.Zero)
            {
                throw new NativeException("weva_document_create", (int)weva_status.WEVA_ERR_INTERNAL);
            }
        }

        public bool IsAlive => _handle != IntPtr.Zero;

        public void LoadHtml(string html)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(html ?? string.Empty);
            fixed (byte* p = bytes)
            {
                Check(WevaNative.weva_document_load_html(Handle, p, (nuint)bytes.Length), "weva_document_load_html");
            }
        }

        public void SetCss(string css)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(css ?? string.Empty);
            fixed (byte* p = bytes)
            {
                Check(WevaNative.weva_document_set_css(Handle, p, (nuint)bytes.Length), "weva_document_set_css");
            }
        }

        /// <summary>
        /// Runs before every update. A font backend sets this to forget which
        /// face FontEngine has loaded, since other text users share it.
        /// </summary>
        public Action BeforeUpdate { get; set; }

        /// <summary>The raw document handle, for adapters that install backends.</summary>
        internal IntPtr NativeHandle => Handle;

        /// <summary>Advances animations by <paramref name="deltaSeconds"/> and publishes layout and draws.</summary>
        public void Update(double deltaSeconds)
        {
            BeforeUpdate?.Invoke();
            Check(WevaNative.weva_document_update(Handle, deltaSeconds), "weva_document_update");
        }

        /// <summary>The first element matching a CSS selector, or WEVA_ELEMENT_NONE.</summary>
        public uint Query(string selector)
        {
            byte[] text = NullTerminated(selector);
            fixed (byte* p = text)
            {
                return WevaNative.weva_document_query(Handle, p);
            }
        }

        /// <summary>Maps one CSS family name to a face of the installed font backend; a zero face removes it.</summary>
        public void RegisterFontFamily(string family, ulong face)
        {
            byte[] name = NullTerminated(family);
            fixed (byte* p = name)
            {
                Check(WevaNative.weva_document_register_font_family(Handle, p, face), "weva_document_register_font_family");
            }
        }

        /// <summary>Moves focus to the next (or previous) focusable element; WEVA_ELEMENT_NONE when there is none.</summary>
        public uint FocusNext(bool backwards = false)
        {
            return WevaNative.weva_document_focus_next(Handle, backwards ? 1 : 0);
        }

        public bool TryGetBounds(uint element, out NativeBounds bounds)
        {
            double x, y, w, h;
            int status = WevaNative.weva_element_bounds(Handle, element, &x, &y, &w, &h);
            bounds = new NativeBounds { X = x, Y = y, Width = w, Height = h };
            return status == (int)weva_status.WEVA_OK;
        }

        /// <summary>The element's lower-case tag name, or an empty string for an unknown element.</summary>
        public string TagName(uint element)
        {
            byte* buffer = stackalloc byte[64];
            nuint length = WevaNative.weva_element_tag_name(Handle, element, buffer, 64);
            if (length == 0)
            {
                return string.Empty;
            }
            if (length >= 64)
            {
                byte[] large = new byte[(int)length + 1];
                fixed (byte* p = large)
                {
                    length = WevaNative.weva_element_tag_name(Handle, element, p, (nuint)large.Length);
                    return Encoding.UTF8.GetString(p, (int)length);
                }
            }
            return Encoding.UTF8.GetString(buffer, (int)length);
        }

        /// <summary>
        /// The draw list of the last update. The memory belongs to the core and is
        /// valid until the next update or the document's destruction.
        /// </summary>
        public ReadOnlySpan<weva_draw> Draws()
        {
            nuint count;
            weva_draw* draws = WevaNative.weva_document_draws(Handle, &count);
            return draws == null ? ReadOnlySpan<weva_draw>.Empty : new ReadOnlySpan<weva_draw>(draws, (int)count);
        }

        /// <summary>The textures the draws reference (8-bit RGBA), same lifetime as the draws.</summary>
        public ReadOnlySpan<weva_texture> Textures()
        {
            nuint count;
            weva_texture* textures = WevaNative.weva_document_textures(Handle, &count);
            return textures == null ? ReadOnlySpan<weva_texture>.Empty : new ReadOnlySpan<weva_texture>(textures, (int)count);
        }

        /// <summary>Changes when, and only when, an update publishes a new draw list.</summary>
        public ulong DrawSerial => WevaNative.weva_document_draw_serial(Handle);

        /// <summary>Whether a transition is still running, so a host keeps handing over time.</summary>
        public bool IsAnimating => WevaNative.weva_document_is_animating(Handle) != 0;

        public void SetViewport(int width, int height)
        {
            WevaNative.weva_document_set_viewport(Handle, width, height);
        }

        /// <summary>The directory relative url() and @font-face sources resolve against.</summary>
        public void SetBasePath(string path)
        {
            byte[] text = NullTerminated(path);
            fixed (byte* p = text)
            {
                Check(WevaNative.weva_document_set_base_path(Handle, p), "weva_document_set_base_path");
            }
            BasePath = path;
            if (_assetReader == null) AssetReader = null;   // the default file reader, resolving against BasePath
        }

        public string BasePath { get; private set; }

        /// <summary>
        /// How the core obtains an asset's bytes (images, @font-face files). The
        /// default reads files from disk, resolving a relative path against
        /// BasePath; a game supplies its own to serve Addressables or bundles.
        /// Null bytes mean "no such asset".
        /// </summary>
        public Func<string, byte[]> AssetReader
        {
            get => _assetReader;
            set
            {
                _assetReader = value ?? ReadFile;
                if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
                var reader = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, nuint, nuint>)Marshal.GetFunctionPointerForDelegate(s_readAsset);
                Check(WevaNative.weva_document_set_asset_reader(Handle, reader, (void*)GCHandle.ToIntPtr(_self)), "weva_document_set_asset_reader");
            }
        }

        private Func<string, byte[]> _assetReader;
        private GCHandle _self;
        private string _lastAssetPath;
        private byte[] _lastAssetBytes;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nuint ReadAssetFn(void* user, byte* path, byte* buffer, nuint capacity);
        private static readonly ReadAssetFn s_readAsset = ReadAsset;

        // Two-call convention: the size, then the bytes. The bytes read for
        // the size call are kept for the fill call that follows it.
        [AOT.MonoPInvokeCallback(typeof(ReadAssetFn))]
        private static nuint ReadAsset(void* user, byte* path, byte* buffer, nuint capacity)
        {
            var doc = GCHandle.FromIntPtr((IntPtr)user).Target as NativeDocument;
            if (doc == null || path == null) return 0;
            int length = 0;
            while (path[length] != 0) length++;
            string name = Encoding.UTF8.GetString(path, length);
            if (buffer == null || capacity == 0 || doc._lastAssetPath != name)
            {
                doc._lastAssetPath = name;
                try
                {
                    doc._lastAssetBytes = doc._assetReader?.Invoke(name);
                }
                catch (Exception)
                {
                    doc._lastAssetBytes = null;
                }
            }
            byte[] bytes = doc._lastAssetBytes;
            if (bytes == null) return 0;
            if (buffer == null || capacity == 0) return (nuint)bytes.Length;
            if ((int)capacity < bytes.Length) return 0;
            Marshal.Copy(bytes, 0, (IntPtr)buffer, bytes.Length);
            doc._lastAssetPath = null;
            doc._lastAssetBytes = null;
            return (nuint)bytes.Length;
        }

        private byte[] ReadFile(string path)
        {
            string full = path;
            if (!System.IO.Path.IsPathRooted(full) && !string.IsNullOrEmpty(BasePath)) full = System.IO.Path.Combine(BasePath, path);
            return System.IO.File.Exists(full) ? System.IO.File.ReadAllBytes(full) : null;
        }

        /// <summary>
        /// The stylesheet's @font-face rules, one per entry: family, source
        /// (resolved like an image url), font-weight and font-style descriptor
        /// texts. The host loads and registers them; the core never loads fonts.
        /// </summary>
        public List<(string Family, string Source, string Weight, string Style)> FontFaces()
        {
            var result = new List<(string, string, string, string)>();
            nuint needed = WevaNative.weva_document_font_faces(Handle, null, 0);
            if (needed == 0) return result;
            byte[] buffer = new byte[(int)needed + 1];
            fixed (byte* p = buffer)
            {
                WevaNative.weva_document_font_faces(Handle, p, (nuint)buffer.Length);
            }
            string text = Encoding.UTF8.GetString(buffer, 0, (int)needed);
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] fields = line.Split('\t');
                if (fields.Length < 2 || fields[0].Length == 0 || fields[1].Length == 0) continue;
                result.Add((fields[0].Trim(), fields[1], fields.Length > 2 ? fields[2].Trim() : "", fields.Length > 3 ? fields[3].Trim() : ""));
            }
            return result;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                WevaNative.weva_document_destroy(_handle);
                _handle = IntPtr.Zero;
            }
            if (_self.IsAllocated) _self.Free();
            GC.SuppressFinalize(this);
        }

        ~NativeDocument()
        {
            // Native memory does not wait for a finalizer in ordinary use; this
            // only keeps a forgotten document from leaking.
            if (_handle != IntPtr.Zero)
            {
                WevaNative.weva_document_destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }

        // ---- interaction: the same calls the Godot host's _gui_input makes ----

        /// <summary>Moves the pointer in document pixels; buttons is a weva_pointer_button mask, modifiers a weva_key_modifier mask.</summary>
        public void SetPointer(double x, double y, uint buttons = 0, uint modifiers = 0)
        {
            WevaNative.weva_document_set_pointer_modifiers(Handle, x, y, buttons, modifiers);
        }

        public void ClearPointer()
        {
            WevaNative.weva_document_clear_pointer(Handle);
        }

        /// <summary>A key edge. Returns true when the document consumed it (a host then keeps it from gameplay).</summary>
        public bool Key(weva_key key, bool down, uint modifiers = 0)
        {
            return WevaNative.weva_document_key(Handle, (int)key, modifiers, down ? 1 : 0) != 0;
        }

        /// <summary>Characters a key produced. Returns true when a field or a select took them.</summary>
        public bool TryTextInput(string text, uint modifiers = 0)
        {
            byte[] utf8 = NullTerminated(text);
            fixed (byte* p = utf8)
            {
                return WevaNative.weva_document_try_text_input_modifiers(Handle, p, modifiers) != 0;
            }
        }

        public bool PasteText(string text)
        {
            byte[] utf8 = NullTerminated(text);
            fixed (byte* p = utf8)
            {
                return WevaNative.weva_document_paste_text(Handle, p) != 0;
            }
        }

        /// <summary>A wheel or pan at a point. Returns true when something scrolled; a host passes the wheel on otherwise.</summary>
        public bool Scroll(double x, double y, double dx, double dy)
        {
            return WevaNative.weva_document_scroll(Handle, x, y, dx, dy) != 0;
        }

        public bool SelectWordAt(double x, double y)
        {
            return WevaNative.weva_document_select_word_at(Handle, x, y) != 0;
        }

        public bool SelectAll() => WevaNative.weva_document_select_all(Handle) != 0;
        public bool Undo() => WevaNative.weva_document_undo(Handle) != 0;
        public bool Redo() => WevaNative.weva_document_redo(Handle) != 0;

        /// <summary>The selected text in the focused field, or empty.</summary>
        public string SelectedText()
        {
            return ReadString((buffer, capacity) => WevaNative.weva_document_selected_text(Handle, buffer, capacity));
        }

        /// <summary>Tab order: the next focusable element, or WEVA_ELEMENT_NONE at the edge when wrap is false.</summary>
        public uint FocusStep(bool backwards, bool wrap)
        {
            return WevaNative.weva_document_focus_step(Handle, backwards ? 1 : 0, wrap ? 1 : 0);
        }

        public uint Focus => WevaNative.weva_document_focus(Handle);

        public bool SetFocus(uint element)
        {
            return WevaNative.weva_document_set_focus(Handle, element) == (int)weva_status.WEVA_OK;
        }

        public bool SetFocus(string selector)
        {
            uint e = Query(selector);
            return e != WevaNative.WEVA_ELEMENT_NONE && SetFocus(e);
        }

        /// <summary>The element under a point after hit testing (pointer-events honoured), or WEVA_ELEMENT_NONE.</summary>
        public uint ElementAt(double x, double y) => WevaNative.weva_document_element_at(Handle, x, y);

        /// <summary>Whether a press at the point belongs to this document, so a host can route it elsewhere otherwise.</summary>
        public bool AcceptsPointer(double x, double y) => WevaNative.weva_document_accepts_pointer(Handle, x, y) != 0;

        /// <summary>The focused text control an IME should be active over, or WEVA_ELEMENT_NONE.</summary>
        public uint TextInputTarget => WevaNative.weva_document_text_input_target(Handle);

        public bool SetComposition(string text, int start, int end)
        {
            byte[] utf8 = NullTerminated(text);
            fixed (byte* p = utf8)
            {
                return WevaNative.weva_document_set_composition(Handle, p, start, end) != 0;
            }
        }

        public bool CommitComposition(string text)
        {
            byte[] utf8 = NullTerminated(text);
            fixed (byte* p = utf8)
            {
                return WevaNative.weva_document_commit_composition(Handle, p) != 0;
            }
        }

        public bool TryGetCaretBounds(out NativeBounds bounds)
        {
            double x, y, w, h;
            int ok = WevaNative.weva_document_caret_bounds(Handle, &x, &y, &w, &h);
            bounds = new NativeBounds { X = x, Y = y, Width = w, Height = h };
            return ok != 0;
        }

        /// <summary>Whether a held gesture (a pressed slider, a held key) wants input time on the next update.</summary>
        public bool NeedsInputTick => WevaNative.weva_document_needs_input_tick(Handle) != 0;

        /// <summary>Update with separate animation and input clocks (a paused game keeps its gestures alive).</summary>
        public void Update(double animationSeconds, double inputSeconds)
        {
            BeforeUpdate?.Invoke();
            Check(WevaNative.weva_document_update_with_input_time(Handle, animationSeconds, inputSeconds), "weva_document_update_with_input_time");
        }

        /// <summary>
        /// Drains the event queue. Events are queued by the core and polled by
        /// the host after an update, never called back mid-update.
        /// </summary>
        public int PollEvents(List<NativeEvent> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            int count = 0;
            weva_event e;
            while (WevaNative.weva_document_poll_event(Handle, &e) != 0)
            {
                var ev = new NativeEvent
                {
                    Kind = (weva_event_kind)e.kind,
                    Target = e.target,
                    X = e.x,
                    Y = e.y,
                    Buttons = e.buttons,
                    Key = (weva_key)e.key,
                    Modifiers = e.modifiers,
                    Handler = Encoding.UTF8.GetString(e.handler, StringLength(e.handler, 48)),
                };
                // The struct carries a short prefix; the full text (a paste, a
                // composition) is read back before the next poll.
                int inline = StringLength(e.text, 8);
                nuint needed = WevaNative.weva_document_event_text(Handle, null, 0);
                if (needed > (nuint)inline)
                {
                    byte[] buffer = new byte[(int)needed + 1];
                    fixed (byte* p = buffer)
                    {
                        WevaNative.weva_document_event_text(Handle, p, (nuint)buffer.Length);
                    }
                    ev.Text = Encoding.UTF8.GetString(buffer, 0, (int)needed);
                }
                else
                {
                    ev.Text = Encoding.UTF8.GetString(e.text, inline);
                }
                into.Add(ev);
                count++;
            }
            return count;
        }

        private static int StringLength(byte* text, int capacity)
        {
            int n = 0;
            while (n < capacity && text[n] != 0) n++;
            return n;
        }

        // ---- elements --------------------------------------------------------

        public string ElementId(uint element) => ElementAttribute(element, "id");

        public bool ElementContains(uint ancestor, uint descendant) => WevaNative.weva_element_contains(Handle, ancestor, descendant) != 0;

        /// <summary>A form control's current value ("on" for a checked box); empty for anything else.</summary>
        public string ElementValue(uint element)
        {
            return ReadString((buffer, capacity) => WevaNative.weva_element_value(Handle, element, buffer, capacity));
        }

        /// <summary>Changes whenever a control's value, validity or edit source changes; what a host compares to know a write did something.</summary>
        public ulong ElementFormVersion(uint element) => WevaNative.weva_element_form_version(Handle, element);

        public bool SetElementValue(uint element, string value)
        {
            byte[] utf8 = NullTerminated(value);
            fixed (byte* p = utf8)
            {
                return WevaNative.weva_element_set_value(Handle, element, p) == (int)weva_status.WEVA_OK;
            }
        }

        public string ElementAttribute(uint element, string name)
        {
            byte[] n = NullTerminated(name);
            fixed (byte* np = n)
            {
                byte* namePtr = np;
                return ReadString((buffer, capacity) => WevaNative.weva_element_attribute(Handle, element, namePtr, buffer, capacity));
            }
        }

        public bool ElementHasAttribute(uint element, string name)
        {
            byte[] n = NullTerminated(name);
            fixed (byte* p = n)
            {
                return WevaNative.weva_element_has_attribute(Handle, element, p) != 0;
            }
        }

        public bool SetElementAttribute(uint element, string name, string value)
        {
            byte[] n = NullTerminated(name);
            byte[] v = NullTerminated(value);
            fixed (byte* np = n)
            fixed (byte* vp = v)
            {
                return WevaNative.weva_element_set_attribute(Handle, element, np, vp) == (int)weva_status.WEVA_OK;
            }
        }

        public string ElementText(uint element)
        {
            return ReadString((buffer, capacity) => WevaNative.weva_element_text(Handle, element, buffer, capacity));
        }

        public bool TryGetElementScroll(uint element, out double x, out double y, out double maxX, out double maxY)
        {
            double sx, sy, mx, my;
            int status = WevaNative.weva_element_scroll(Handle, element, &sx, &sy, &mx, &my);
            x = sx; y = sy; maxX = mx; maxY = my;
            return status == (int)weva_status.WEVA_OK;
        }

        public bool SetElementScroll(uint element, double x, double y)
        {
            return WevaNative.weva_element_set_scroll(Handle, element, x, y) == (int)weva_status.WEVA_OK;
        }

        /// <summary>
        /// The layout dump the differential oracle compares, as weva_dump's JSON,
        /// produced by the core from this document's box tree. Valid after an update.
        /// </summary>
        public string LayoutDump(string source)
        {
            byte[] name = NullTerminated(source);
            fixed (byte* np = name)
            {
                byte* namePtr = np;
                return ReadString((buffer, capacity) => WevaNative.weva_document_layout_dump(Handle, namePtr, buffer, capacity));
            }
        }

        /// <summary>
        /// Whether the installed font backend's half-leading rounds down to whole
        /// pixels as real font layout does (the default). A synthetic face compared
        /// with the oracle's arithmetic passes false. Takes effect on the next update.
        /// </summary>
        public void SetFontLeadingRounding(bool rounds)
        {
            WevaNative.weva_document_set_font_leading_rounding(Handle, rounds ? 1 : 0);
        }

        /// <summary>Every element the selector matches, in document order.</summary>
        public uint[] QueryAll(string selector)
        {
            byte[] sel = NullTerminated(selector);
            fixed (byte* p = sel)
            {
                nuint count = WevaNative.weva_document_query_all(Handle, p, null, 0);
                if (count == 0) return Array.Empty<uint>();
                uint[] result = new uint[(int)count];
                fixed (uint* r = result)
                {
                    nuint written = WevaNative.weva_document_query_all(Handle, p, r, count);
                    if (written < count) Array.Resize(ref result, (int)written);
                }
                return result;
            }
        }

        /// <summary>A data path with the element's data-each aliases unwound (`quest.Done` to `Quests.3.Done`).</summary>
        public string ModelPath(uint element, string path)
        {
            byte[] raw = NullTerminated(path);
            fixed (byte* rp = raw)
            {
                byte* rawPtr = rp;
                return ReadString((buffer, capacity) => WevaNative.weva_element_model_path(Handle, element, rawPtr, buffer, capacity));
            }
        }

        /// <summary>The data-each row an element sits in: its index and key. False outside a row.</summary>
        public bool TryGetRow(uint element, out int index, out string key)
        {
            int i;
            byte* buffer = stackalloc byte[128];
            int ok = WevaNative.weva_element_row(Handle, element, &i, buffer, 128);
            index = i;
            key = ok != 0 ? Encoding.UTF8.GetString(buffer, StringLength(buffer, 128)) : string.Empty;
            return ok != 0;
        }

        public bool ShowDialog(uint dialog, bool modal = true)
        {
            return WevaNative.weva_element_show_dialog(Handle, dialog, modal ? 1 : 0) == (int)weva_status.WEVA_OK;
        }

        public bool CloseDialog(uint dialog)
        {
            return WevaNative.weva_element_close_dialog(Handle, dialog) == (int)weva_status.WEVA_OK;
        }

        // ---- the inspector surface (ABI minor 27) -------------------------------

        /// <summary>The parent element, or WEVA_ELEMENT_NONE for the root.</summary>
        public uint Parent(uint element) => WevaNative.weva_element_parent(Handle, element);

        /// <summary>The element children in document order (text nodes are not listed).</summary>
        public uint[] Children(uint element)
        {
            nuint count = WevaNative.weva_element_children(Handle, element, null, 0);
            if (count == 0) return Array.Empty<uint>();
            uint[] result = new uint[(int)count];
            fixed (uint* p = result)
            {
                nuint written = WevaNative.weva_element_children(Handle, element, p, count);
                if (written < count) Array.Resize(ref result, (int)written);
            }
            return result;
        }

        /// <summary>The rectangles behind the border box: edges of margin, border and padding, and the content box.</summary>
        public struct BoxModel
        {
            public double MarginTop, MarginRight, MarginBottom, MarginLeft;
            public double BorderTop, BorderRight, BorderBottom, BorderLeft;
            public double PaddingTop, PaddingRight, PaddingBottom, PaddingLeft;
            public double ContentX, ContentY, ContentWidth, ContentHeight;
        }

        public bool TryGetBoxModel(uint element, out BoxModel model)
        {
            double* v = stackalloc double[16];
            int status = WevaNative.weva_element_box_model(Handle, element, v);
            model = new BoxModel
            {
                MarginTop = v[0], MarginRight = v[1], MarginBottom = v[2], MarginLeft = v[3],
                BorderTop = v[4], BorderRight = v[5], BorderBottom = v[6], BorderLeft = v[7],
                PaddingTop = v[8], PaddingRight = v[9], PaddingBottom = v[10], PaddingLeft = v[11],
                ContentX = v[12], ContentY = v[13], ContentWidth = v[14], ContentHeight = v[15],
            };
            return status == (int)weva_status.WEVA_OK;
        }

        /// <summary>One declaration that applies to an element, as the cascade saw it.</summary>
        public struct MatchedRule
        {
            public string Origin;          // ua, user, author
            public string Layer;           // the layer ordinal, empty when unlayered
            public string Specificity;     // "a,b,c", empty for the style attribute
            public int Source;             // rule order within the document, -1 for the style attribute
            public bool Inline;
            public string Selector;
            public string Property;
            public string Value;
            public bool Important;
            public bool Applied;           // the winning declaration for its property
        }

        /// <summary>Every declaration that applies to the element, in cascade order (the last line for a property wins).</summary>
        public List<MatchedRule> MatchedRules(uint element)
        {
            var rules = new List<MatchedRule>();
            string text = ReadString((buffer, capacity) => WevaNative.weva_element_matched_rules(Handle, element, buffer, capacity));
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                if (f.Length < 10) continue;
                rules.Add(new MatchedRule
                {
                    Origin = f[0], Layer = f[1], Specificity = f[2],
                    Source = int.TryParse(f[3], out int source) ? source : -1,
                    Inline = f[4] == "1", Selector = f[5], Property = f[6], Value = f[7],
                    Important = f[8] == "1", Applied = f[9] == "1",
                });
            }
            return rules;
        }

        /// <summary>The whole computed style: every registered property resolved, then the custom properties in scope.</summary>
        public List<KeyValuePair<string, string>> ComputedStyle(uint element)
        {
            var result = new List<KeyValuePair<string, string>>();
            string text = ReadString((buffer, capacity) => WevaNative.weva_element_computed_style_all(Handle, element, buffer, capacity));
            foreach (string line in text.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                result.Add(new KeyValuePair<string, string>(line.Substring(0, tab), line.Substring(tab + 1)));
            }
            return result;
        }

        private delegate nuint SizedRead(byte* buffer, nuint capacity);

        // The ABI's two-call convention: the size, then the bytes.
        private string ReadString(SizedRead read)
        {
            byte* small = stackalloc byte[128];
            nuint needed = read(small, 128);
            if (needed == 0) return string.Empty;
            if (needed < 128) return Encoding.UTF8.GetString(small, (int)needed);
            byte[] large = new byte[(int)needed + 1];
            fixed (byte* p = large)
            {
                needed = read(p, (nuint)large.Length);
                return Encoding.UTF8.GetString(p, (int)needed);
            }
        }

        private IntPtr Handle
        {
            get
            {
                if (_handle == IntPtr.Zero)
                {
                    throw new ObjectDisposedException(nameof(NativeDocument));
                }
                return _handle;
            }
        }

        private static void Check(int status, string operation)
        {
            if (status != (int)weva_status.WEVA_OK)
            {
                throw new NativeException(operation, status);
            }
        }

        private static byte[] NullTerminated(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
            byte[] result = new byte[utf8.Length + 1];
            Buffer.BlockCopy(utf8, 0, result, 0, utf8.Length);
            return result;
        }
    }
}
