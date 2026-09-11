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
