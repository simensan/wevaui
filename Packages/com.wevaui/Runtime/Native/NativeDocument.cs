// A document hosted by libweva (the shared C++ core) through the weva_core
// plugin. This is the hand-written edge over the generated P/Invoke surface
// in WevaNative.g.cs: UTF-8 marshalling, status checks and ownership. It
// does not touch the C# engine; the two coexist while the Unity host over
// the core is built up (fonts, rendering, input come in later steps).
using System;
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

        /// <summary>Advances animations by <paramref name="deltaSeconds"/> and publishes layout and draws.</summary>
        public void Update(double deltaSeconds)
        {
            Check(WevaNative.weva_document_update(Handle, deltaSeconds), "weva_document_update");
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

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                WevaNative.weva_document_destroy(_handle);
                _handle = IntPtr.Zero;
            }
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
