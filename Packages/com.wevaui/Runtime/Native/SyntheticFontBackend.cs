// The oracle's synthetic face, as a font backend for the core: the metrics
// weva_dump and the C# reference (BaselineGen) lay text out with, so a
// layout dump taken through the Unity host is comparable to theirs. Every
// glyph advances 0.45em (0.6em for the monospace face), an emoji 1.3em or
// 1.0em by the same allowlist as libweva's MonoFontMetrics, ascent is
// 0.85em, descent 0.293em and the line 1.143em. No bitmaps: this face is
// for measuring, not drawing.
using System;
using System.Runtime.InteropServices;
using AOT;

namespace Weva.Native
{
    public sealed unsafe class SyntheticFontBackend : IDisposable
    {
        public const ulong SansFace = 1;
        public const ulong MonospaceFace = 2;
        private const uint NormalGlyph = 1, MediumEmojiGlyph = 2, WideEmojiGlyph = 3;

        private GCHandle _self;

        /// <summary>Installs the synthetic sans face as the document's default and registers `monospace`.</summary>
        public void Install(NativeDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            var table = new weva_font_backend
            {
                user_data = (void*)GCHandle.ToIntPtr(_self),
                load_face = (delegate* unmanaged[Cdecl]<void*, byte*, nuint, int, ulong>)Marshal.GetFunctionPointerForDelegate(s_loadFace),
                face_metrics = (delegate* unmanaged[Cdecl]<void*, ulong, double, double*, double*, double*, int>)Marshal.GetFunctionPointerForDelegate(s_faceMetrics),
                glyph_index = (delegate* unmanaged[Cdecl]<void*, ulong, uint, uint*, int>)Marshal.GetFunctionPointerForDelegate(s_glyphIndex),
                glyph_metrics = (delegate* unmanaged[Cdecl]<void*, ulong, uint, double, double*, double*, double*, int*, int*, int>)Marshal.GetFunctionPointerForDelegate(s_glyphMetrics),
                rasterize = (delegate* unmanaged[Cdecl]<void*, ulong, uint, double, weva_glyph_bitmap*, int>)Marshal.GetFunctionPointerForDelegate(s_rasterize),
                shape = (delegate* unmanaged[Cdecl]<void*, ulong, byte*, nuint, double, uint*, double*, uint*, nuint, nuint>)Marshal.GetFunctionPointerForDelegate(s_shape),
                variant = null,
            };
            WevaNative.weva_document_set_font_backend(doc.NativeHandle, &table, SansFace);
            doc.RegisterFontFamily("monospace", MonospaceFace);
            // The oracle's arithmetic keeps fractional half-leading; a real face
            // would round it down, and weva_dump's synthetic face does not.
            doc.SetFontLeadingRounding(false);
        }

        public void Dispose()
        {
            if (_self.IsAllocated) _self.Free();
        }

        public static double AdvanceEm(ulong face, uint codepoint)
        {
            if (IsWideEmoji(codepoint)) return 1.3;
            if (IsMediumEmoji(codepoint)) return 1.0;
            return face == MonospaceFace ? 0.6 : 0.45;
        }

        // The same allowlist as libweva's font_metrics.cpp: Chrome draws these
        // from an emoji face at about 1.3em, the Dingbats block nearer 1.0em.
        public static bool IsWideEmoji(uint cp)
        {
            return (cp >= 0x1F000 && cp <= 0x1FAFF) || cp == 0x26A1 || cp == 0x26D4 || cp == 0x2600 || cp == 0x2614 ||
                   cp == 0x2615 || cp == 0x2618 || cp == 0x2620 || cp == 0x2705;
        }

        public static bool IsMediumEmoji(uint cp)
        {
            return (cp >= 0x2700 && cp <= 0x27BF) || cp == 0x2699 || cp == 0x2298;
        }

        private static uint GlyphFor(uint cp) => IsWideEmoji(cp) ? WideEmojiGlyph : IsMediumEmoji(cp) ? MediumEmojiGlyph : NormalGlyph;

        private static double AdvanceOf(ulong face, uint glyph, double px)
        {
            double em = glyph == WideEmojiGlyph ? 1.3 : glyph == MediumEmojiGlyph ? 1.0 : face == MonospaceFace ? 0.6 : 0.45;
            return em * px;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong LoadFaceFn(void* self, byte* data, nuint length, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FaceMetricsFn(void* self, ulong face, double px, double* ascent, double* descent, double* lineGap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GlyphIndexFn(void* self, ulong face, uint codepoint, uint* glyph);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GlyphMetricsFn(void* self, ulong face, uint glyph, double px, double* advance, double* bearingX, double* bearingY, int* width, int* height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RasterizeFn(void* self, ulong face, uint glyph, double px, weva_glyph_bitmap* bitmap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ShapeFn(void* self, ulong face, byte* utf8, nuint length, double px, uint* glyphs, double* advances, uint* clusters, nuint capacity);

        private static readonly LoadFaceFn s_loadFace = LoadFace;
        private static readonly FaceMetricsFn s_faceMetrics = FaceMetrics;
        private static readonly GlyphIndexFn s_glyphIndex = GlyphIndex;
        private static readonly GlyphMetricsFn s_glyphMetrics = GlyphMetrics;
        private static readonly RasterizeFn s_rasterize = Rasterize;
        private static readonly ShapeFn s_shape = Shape;

        [MonoPInvokeCallback(typeof(LoadFaceFn))]
        private static ulong LoadFace(void* self, byte* data, nuint length, int index) => 0;   // no files: two faces only

        [MonoPInvokeCallback(typeof(FaceMetricsFn))]
        private static int FaceMetrics(void* self, ulong face, double px, double* ascent, double* descent, double* lineGap)
        {
            if (face != SansFace && face != MonospaceFace) return 0;
            // ChromeSansSerif: 0.85 up, 0.293 down, 1.143 per line; the gap is
            // what the line has left over, computed as the core's own stub does.
            if (ascent != null) *ascent = px * 0.85;
            if (descent != null) *descent = px * 0.293;
            if (lineGap != null) *lineGap = px * (1.143 - 0.85 - 0.293);
            return 1;
        }

        [MonoPInvokeCallback(typeof(GlyphIndexFn))]
        private static int GlyphIndex(void* self, ulong face, uint codepoint, uint* glyph)
        {
            if (glyph != null) *glyph = GlyphFor(codepoint);
            return 1;
        }

        [MonoPInvokeCallback(typeof(GlyphMetricsFn))]
        private static int GlyphMetrics(void* self, ulong face, uint glyph, double px, double* advance, double* bearingX, double* bearingY, int* width, int* height)
        {
            if (advance != null) *advance = AdvanceOf(face, glyph, px);
            if (bearingX != null) *bearingX = 0;
            if (bearingY != null) *bearingY = 0;
            if (width != null) *width = 0;
            if (height != null) *height = 0;
            return 1;
        }

        [MonoPInvokeCallback(typeof(RasterizeFn))]
        private static int Rasterize(void* self, ulong face, uint glyph, double px, weva_glyph_bitmap* bitmap) => 0;

        [MonoPInvokeCallback(typeof(ShapeFn))]
        private static nuint Shape(void* self, ulong face, byte* utf8, nuint length, double px, uint* glyphs, double* advances, uint* clusters, nuint capacity)
        {
            int n = (int)length, i = 0, count = 0;
            while (i < n)
            {
                byte b = utf8[i];
                int extra = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : 0;
                uint cp = extra == 0 ? (b < 0x80 ? b : 0xFFFDu) : (uint)(b & (0x3F >> extra));
                if (i + extra >= n && extra > 0) { cp = 0xFFFD; extra = 0; }
                for (int k = 1; k <= extra; k++) cp = (cp << 6) | (uint)(utf8[i + k] & 0x3F);
                if (count < (int)capacity)
                {
                    uint glyph = GlyphFor(cp);
                    if (glyphs != null) glyphs[count] = glyph;
                    if (advances != null) advances[count] = AdvanceOf(face, glyph, px);
                    if (clusters != null) clusters[count] = (uint)i;
                }
                count++;
                i += extra + 1;
            }
            return (nuint)count;
        }
    }
}
