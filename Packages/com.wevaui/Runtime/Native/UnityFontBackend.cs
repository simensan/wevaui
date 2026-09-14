// libweva's font backend, implemented over Unity's FontEngine (TextCore).
//
// The core ships a 5x7 stub face so it can lay out text with no host at all.
// This replaces it with real faces the way the Godot host's TextServer
// adapter does: the same C callback table, filled with static methods that
// recover the adapter from user_data. The core never sees a Unity type.
//
// FontEngine facts this adapter is built around:
//   - It is a state machine: one loaded face and one face size at a time.
//     Every callback activates the face it needs; other FontEngine users
//     in the process (including TextMeshPro) are why NativeDocument
//     invalidates the active face before each update.
//   - LoadFontFace accepts a Font asset, a file path, or the font's bytes,
//     which is how @font-face data from the core's asset reader gets in.
//   - GetFaceInfo() right after LoadFontFace, before any SetFaceSize, reports
//     design units, and pointSize is the units-per-em in that state.
//   - There is no public shaper. The Shaping and Indic partials apply the
//     font's OpenType substitutions and positioning; clusters are UTF-8
//     byte offsets as the core requires.
//     Fallback is per code point across the faces adopted behind the
//     primary, and a glyph id carries which face it came from.
//   - TryAddGlyphToTexture rasterizes into a Texture2D; its visibility
//     differs across Unity 6 releases, so it is bound by reflection as the
//     C# engine's SDF rasterizer already does. Coverage comes out of an
//     Alpha8 scratch texture, bottom-up, and is flipped for the core.
//
// IL2CPP: the callbacks are static methods marked MonoPInvokeCallback and
// reached through delegates kept alive for the adapter's lifetime;
// UnmanagedCallersOnly is not in Unity's class library.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AOT;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace Weva.Native
{
    internal sealed unsafe partial class UnityFontBackend : IDisposable
    {
        // A glyph id names the face slot it came from and the glyph index
        // within that face: slot 0 is the primary, later slots its fallbacks.
        private const int SlotShift = 24;
        private const uint IndexMask = (1u << SlotShift) - 1;

        // FontEngine caches byte-backed faces by buffer identity. Reloading
        // identical font data into fresh arrays otherwise expands another set
        // of native kerning tables for every document. Keep one private buffer
        // per content for the Unity domain's lifetime, including after backend
        // disposal: evicting it would give the next load a fresh native identity.
        private static readonly Dictionary<string, byte[]> s_fontBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        private static byte[] SharedFontBytes(byte[] data)
        {
            string key;
            using (var hash = SHA256.Create()) key = Convert.ToBase64String(hash.ComputeHash(data));
            lock (s_fontBytes)
            {
                if (s_fontBytes.TryGetValue(key, out byte[] shared)) return shared;
                // A caller retaining its input must not be able to corrupt a
                // buffer now shared by unrelated documents or their shapers.
                shared = (byte[])data.Clone();
                s_fontBytes.Add(key, shared);
                return shared;
            }
        }

        /// <summary>One loaded font: an asset, a file, or bytes, plus its design metrics.</summary>
        private sealed class Source
        {
            public Font Asset;
            public byte[] Bytes;
            public string Path;
            public int Index;
            public string Name;
            public bool MetricsKnown;
            public double UnitsPerEm, Ascent, Descent, LineGap;
            public readonly Dictionary<long, GlyphInfo> Glyphs = new Dictionary<long, GlyphInfo>();
            public readonly Dictionary<uint, uint> Indices = new Dictionary<uint, uint>();
            // Pair positioning in em units, keyed (first << 32 | second), read
            // from the engine once per adjacent pair the face has shaped.
            public readonly Dictionary<long, double> Kerning = new Dictionary<long, double>();
        }

        private struct GlyphInfo
        {
            public bool Present;
            public double Advance, BearingX, BearingY;
            public int Width, Height;
        }

        private sealed class Face
        {
            public readonly List<Source> Sources = new List<Source>();
            public readonly Dictionary<int, ulong> Variants = new Dictionary<int, ulong>();
        }

        private readonly Dictionary<ulong, Face> _faces = new Dictionary<ulong, Face>();
        private ulong _nextFace = 1;
        private Source _active;
        private int _activeSize = -1;
        private bool _initialized;
        private GCHandle _self;
        private Texture2D _scratch;
        private IntPtr _bitmap;
        private int _bitmapCapacity;
        private readonly List<GlyphRect> _freeRects = new List<GlyphRect>(2);
        private readonly List<GlyphRect> _usedRects = new List<GlyphRect>(2);
        private readonly List<uint> _codepoints = new List<uint>(64);
        private readonly List<uint> _offsets = new List<uint>(64);
        private readonly List<uint> _pairQuery = new List<uint>(2);

        public int FaceLoads { get; private set; }
        public string LastError { get; private set; }

        // ---- adopting faces ------------------------------------------------

        /// <summary>Adopts a Font asset as a face. Returns the face id for the core.</summary>
        public ulong Adopt(Font font, int index = 0)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));
            return Register(new Source { Asset = font, Index = index, Name = font.name });
        }

        /// <summary>Adopts a font from its bytes (a TTF/OTF, index selecting a face in a collection).</summary>
        public ulong Adopt(byte[] data, int index = 0, string name = null)
        {
            if (data == null || data.Length == 0) throw new ArgumentException("font data is empty", nameof(data));
            return Register(new Source { Bytes = SharedFontBytes(data), Index = index, Name = name ?? "bytes" });
        }

        /// <summary>Adopts a font file by path.</summary>
        public ulong AdoptPath(string path, int index = 0)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is empty", nameof(path));
            return Register(new Source { Path = path, Index = index, Name = System.IO.Path.GetFileName(path) });
        }

        /// <summary>
        /// Faces tried, in order, for code points the primary lacks (a symbol
        /// or emoji face behind the UI face). Each must be an adopted face.
        /// </summary>
        public void SetFallbacks(ulong face, params ulong[] fallbacks)
        {
            Face f = Require(face);
            while (f.Sources.Count > 1) f.Sources.RemoveAt(f.Sources.Count - 1);
            foreach (ulong id in fallbacks)
            {
                Face fb = Require(id);
                if (f.Sources.Count >= (1 << (32 - SlotShift)))
                {
                    throw new InvalidOperationException("too many fallback faces");
                }
                f.Sources.Add(fb.Sources[0]);
            }
        }

        /// <summary>
        /// A real bold or italic face for <paramref name="face"/>: `variant`
        /// answers with it (or the nearest one) instead of the regular face.
        /// weight is the CSS number; zero removes the entry.
        /// </summary>
        public void SetRealVariant(ulong face, int weight, bool italic, ulong variantFace)
        {
            Face f = Require(face);
            int key = VariantKey(weight, italic);
            if (variantFace == 0) f.Variants.Remove(key);
            else
            {
                Require(variantFace);
                f.Variants[key] = variantFace;
            }
        }

        public int FaceCount => _faces.Count;

        /// <summary>Which face slot a glyph id came from (0 = primary).</summary>
        public static int SlotOf(uint glyph) => (int)(glyph >> SlotShift);
        public static uint IndexOf(uint glyph) => glyph & IndexMask;

        /// <summary>
        /// Installs this adapter on a document as its font backend and
        /// positioned shaper, with <paramref name="defaultFace"/> serving
        /// families that are not registered.
        /// </summary>
        public void Install(NativeDocument doc, ulong defaultFace)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            Require(defaultFace);
            EnsureInitialized();
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
                variant = (delegate* unmanaged[Cdecl]<void*, ulong, int, int, ulong>)Marshal.GetFunctionPointerForDelegate(s_variant),
            };
            WevaNative.weva_document_set_font_backend(doc.NativeHandle, &table, defaultFace);
            var positioned = (delegate* unmanaged[Cdecl]<void*, ulong, byte*, nuint, double, weva_shaped_glyph*, nuint, nuint>)Marshal.GetFunctionPointerForDelegate(s_positioned);
            int status = WevaNative.weva_document_set_font_shaper(doc.NativeHandle, positioned);
            if (status != (int)weva_status.WEVA_OK) throw new NativeException("weva_document_set_font_shaper", status);
            doc.BeforeUpdate = Invalidate;
        }

        private readonly Dictionary<string, ulong> _cssFaces = new Dictionary<string, ulong>();
        private readonly Dictionary<string, (string Family, ulong Face)> _cssFamilies =
            new Dictionary<string, (string Family, ulong Face)>();
        private readonly HashSet<string> _hostFamilies = new HashSet<string>();

        /// <summary>
        /// Registers a family the game owns. A family registered this way is
        /// never overwritten or released by <see cref="SyncCssFontFaces"/>, so
        /// a page's @font-face cannot take over a family the host has claimed.
        /// This mirrors the Godot host, where register_font_family records the
        /// same ownership.
        /// <para>
        /// Calling <see cref="NativeDocument.RegisterFontFamily"/> directly
        /// still works but is invisible to that rule — the backend cannot see
        /// registrations that do not go through it.
        /// </para>
        /// </summary>
        public void RegisterFontFamily(NativeDocument doc, string family, ulong face)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (family == null) throw new ArgumentNullException(nameof(family));
            doc.RegisterFontFamily(family, face);
            string key = family.ToLowerInvariant();
            if (face != 0) _hostFamilies.Add(key);
            else _hostFamilies.Remove(key);
        }

        /// <summary>
        /// Registers the stylesheet's @font-face rules with the document: each
        /// source is read through the document's asset reader and adopted once,
        /// the normal face serves the family and bold or italic faces become
        /// its real variants. Call after SetCss. Returns the families served.
        /// <para>
        /// The regular face is the family's exact 400/normal rule wherever it
        /// was declared; failing that, any other upright face under 600; failing
        /// that, the first variant, so a family that declares only a bold or
        /// only an italic still works. Families this method registered and the
        /// new stylesheet no longer declares are released. Families the game
        /// claimed through <see cref="RegisterFontFamily"/> are never taken
        /// over or released.
        /// </para>
        /// </summary>
        public int SyncCssFontFaces(NativeDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var wanted = new Dictionary<string, (string Family, string Normal, string NearNormal, Dictionary<int, string> Variants)>();
            foreach ((string family, string firstUrl, string weight, string style, string source) in doc.FontFaces())
            {
                string key = family.ToLowerInvariant();
                if (key.Length == 0 || key.Contains(",") || key.Contains("\"") || key.Contains("'")) continue;
                string w = weight.ToLowerInvariant(), st = style.ToLowerInvariant();
                int number = w == "bold" || w == "bolder" ? 700 : w.Length == 0 || w == "normal" ? 400 : ParseLeadingInt(w, 400);
                bool italic = st.StartsWith("italic") || st.StartsWith("oblique");
                // Two tiers, so declaration order cannot decide the regular
                // face. An exact 400/normal rule is the family's regular face
                // wherever the author wrote it; any other upright face under
                // 600 (100-300, 500) only stands in when no 400 was declared.
                bool upright = number < 600 && !italic;
                bool exact = number == 400 && !italic;
                if (!wanted.TryGetValue(key, out var entry))
                {
                    entry = (family, exact ? source : null, upright && !exact ? source : null, new Dictionary<int, string>());
                    wanted[key] = entry;
                }
                else if (exact && entry.Normal == null)
                {
                    entry.Normal = source;
                    wanted[key] = entry;
                }
                else if (upright && !exact && entry.NearNormal == null)
                {
                    entry.NearNormal = source;
                    wanted[key] = entry;
                }
                if (!upright) entry.Variants[VariantKey(number, italic)] = source;
            }
            // Release families this stylesheet no longer declares, mirroring the
            // Godot host: re-registering a null face hands the family back to
            // the fallback instead of leaving a stale registration behind for
            // the life of the document. A family the game owns is left alone.
            if (_cssFamilies.Count > 0)
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, (string Family, ulong Face)> kv in _cssFamilies)
                    if (!wanted.ContainsKey(kv.Key)) stale.Add(kv.Key);
                foreach (string key in stale)
                {
                    if (!_hostFamilies.Contains(key)) doc.RegisterFontFamily(_cssFamilies[key].Family, 0);
                    _cssFamilies.Remove(key);
                }
            }

            int served = 0;
            foreach (KeyValuePair<string, (string Family, string Normal, string NearNormal, Dictionary<int, string> Variants)> kv in wanted)
            {
                // The game's own registration wins over @font-face, as on Godot.
                if (_hostFamilies.Contains(kv.Key) && !_cssFamilies.ContainsKey(kv.Key)) continue;
                string normalSource = kv.Value.Normal ?? kv.Value.NearNormal;
                if (normalSource == null)
                {
                    // Only variant faces declared: the first serves as the family's face.
                    foreach (string v in kv.Value.Variants.Values) { normalSource = v; break; }
                }
                string inner = LastError;
                ulong face = AdoptCssSource(doc, normalSource);
                if (face == 0)
                {
                    LastError = "@font-face " + kv.Value.Family + " could not load " + normalSource +
                        (LastError != null && LastError != inner ? " (" + LastError + ")" : "");
                    continue;
                }
                if (!_cssFamilies.TryGetValue(kv.Key, out var current) || current.Face != face)
                {
                    doc.RegisterFontFamily(kv.Value.Family, face);
                }
                _cssFamilies[kv.Key] = (kv.Value.Family, face);
                foreach (KeyValuePair<int, string> variant in kv.Value.Variants)
                {
                    ulong variantFace = AdoptCssSource(doc, variant.Value);
                    if (variantFace == 0 || variantFace == face) continue;
                    SetRealVariant(face, (variant.Key & 2) != 0 ? 700 : 400, (variant.Key & 1) != 0, variantFace);
                }
                served++;
            }
            return served;
        }

        // ---- installed families a page names --------------------------------

        private readonly Dictionary<string, (string Family, ulong Face)> _installedFamilies = new Dictionary<string, (string, ulong)>();
        private readonly HashSet<string> _missingInstalled = new HashSet<string>();

        /// <summary>
        /// Registers, for every family the page's styles name that no
        /// @font-face rule or game registration claims, the installed font of
        /// that name, with its bold and italic files where the OS has them:
        /// what a browser does with <c>font-family: "Segoe UI"</c>. A family
        /// the page stopped naming is released. Returns how many families are
        /// served this way. Call after <see cref="SyncCssFontFaces"/>.
        /// </summary>
        public int SyncInstalledFamilies(NativeDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var wanted = new Dictionary<string, string>();
            foreach (string name in doc.FontFamilyNames())
            {
                string key = name.ToLowerInvariant();
                if (key.Length == 0 || wanted.ContainsKey(key) || _cssFamilies.ContainsKey(key) || _hostFamilies.Contains(key)) continue;
                wanted[key] = name;
            }
            if (_installedFamilies.Count > 0)
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, (string Family, ulong Face)> kv in _installedFamilies)
                    if (!wanted.ContainsKey(kv.Key)) stale.Add(kv.Key);
                foreach (string key in stale)
                {
                    doc.RegisterFontFamily(_installedFamilies[key].Family, 0);
                    _installedFamilies.Remove(key);
                }
            }
            int served = 0;
            foreach (KeyValuePair<string, string> kv in wanted)
            {
                if (_installedFamilies.ContainsKey(kv.Key))
                {
                    served++;
                    continue;
                }
                // Asked of the OS once per name: the font list does not change
                // while the backend lives.
                if (_missingInstalled.Contains(kv.Key)) continue;
                ulong face = AdoptLocalFont(kv.Value);
                if (face == 0)
                {
                    _missingInstalled.Add(kv.Key);
                    continue;
                }
                AdoptInstalledVariants(kv.Value, face);
                doc.RegisterFontFamily(kv.Value, face);
                _installedFamilies[kv.Key] = (kv.Value, face);
                served++;
            }
            return served;
        }

        /// <summary>How many of the page's named families are served by installed fonts.</summary>
        public int InstalledFamilyCount => _installedFamilies.Count;

        // The family's bold, italic and bold-italic files where the OS has
        // them, as real variants of the regular face; a style the OS answers
        // with the regular file itself is not a variant.
        private void AdoptInstalledVariants(string name, ulong face)
        {
            string regularPath = null;
            if (!TrySystemFontFile(name, "Regular", out regularPath, out _)) TrySystemFontFile(name, null, out regularPath, out _);
            foreach ((string style, int weight, bool italic) in new[] { ("Bold", 700, false), ("Italic", 400, true), ("Bold Italic", 700, true) })
            {
                if (!TrySystemFontFile(name, style, out string path, out int index) || string.IsNullOrEmpty(path)) continue;
                if (regularPath != null && string.Equals(path, regularPath, StringComparison.OrdinalIgnoreCase)) continue;
                string key = "local:" + name + ":" + style;
                if (!_cssFaces.TryGetValue(key, out ulong variant))
                {
                    try
                    {
                        variant = AdoptPath(path, index);
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        continue;
                    }
                    if (!Activate(_faces[variant].Sources[0]))
                    {
                        _faces.Remove(variant);
                        continue;
                    }
                    _cssFaces[key] = variant;
                }
                if (variant != face) SetRealVariant(face, weight, italic, variant);
            }
        }

        /// <summary>
        /// The ordered src list of a rule ("url:&lt;path&gt;|local:&lt;name&gt;"): the
        /// first entry that loads wins. A local() name is an installed font,
        /// loaded the way the C# text path loads system fonts.
        /// </summary>
        private ulong AdoptCssSource(NativeDocument doc, string sources)
        {
            if (string.IsNullOrEmpty(sources)) return 0;
            foreach (string entry in sources.Split('|'))
            {
                ulong face = 0;
                if (entry.StartsWith("url:")) face = AdoptCssUrl(doc, entry.Substring(4));
                else if (entry.StartsWith("local:")) face = AdoptLocalFont(entry.Substring(6));
                else face = AdoptCssUrl(doc, entry);
                if (face != 0) return face;
            }
            return 0;
        }

        /// <summary>
        /// The installed fonts a document appends after its bundled fallbacks
        /// so a script or symbol none of them carries (Arabic, Hebrew, Thai;
        /// ⚔, ☥) still draws, as a browser reaches the platform's fonts. The
        /// platform's UI face first (it covers the world's scripts), its
        /// symbol face after it. Names only: a platform without the font, or a
        /// player whose OS font list is closed, skips it.
        /// </summary>
        public static readonly string[] SystemFallbackFonts =
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            { "Segoe UI", "Nirmala UI", "Segoe UI Symbol" };
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            { "Arial", "Kohinoor Devanagari", "Apple Symbols" };
#elif UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            { "DejaVu Sans" };
#else
            new string[0];
#endif

        /// <summary>An installed font by family name; 0 when the OS does not have it.</summary>
        public ulong AdoptInstalled(string name) => AdoptLocalFont(name);

        private ulong AdoptLocalFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string key = "local:" + name;
            if (_cssFaces.TryGetValue(key, out ulong cached)) return cached;
            string[] installed;
            try { installed = Font.GetOSInstalledFontNames(); }
            catch (Exception ex) { LastError = ex.Message; return 0; }
            bool present = false;
            foreach (string candidate in installed)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
            }
            if (!present) return 0;
            // Keep the file's identity in FontEngine's cache. Fresh byte arrays
            // create distinct native faces on every document lifetime, each
            // with its own expanded kerning tables. The shaper reads the file's
            // bytes lazily through BytesOf without changing how it is loaded.
            ulong face = 0;
            if (TrySystemFontFile(name, "Regular", out string filePath, out int faceIndex) ||
                TrySystemFontFile(name, null, out filePath, out faceIndex))
            {
                try
                {
                    face = AdoptPath(filePath, faceIndex);
                    if (!Activate(_faces[face].Sources[0]))
                    {
                        _faces.Remove(face);
                        face = 0;
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
            if (face == 0)
            {
                Font font = Font.CreateDynamicFontFromOSFont(name, 16);
                if (font == null) return 0;
                face = Adopt(font);
            }
            if (!Activate(_faces[face].Sources[0]))
            {
                _faces.Remove(face);
                return 0;
            }
            _cssFaces[key] = face;
            return face;
        }

        // FontEngine.TryGetSystemFontReference and its FontReference struct are
        // internal in this Unity (TextMeshPro reaches them through
        // InternalsVisibleTo), so they are found by reflection once; when
        // absent the dynamic-font route above still serves.
        private static bool _systemFontProbed;
        private static MethodInfo _systemFontReference;
        private static FieldInfo _systemFontPath, _systemFontIndex;

        private static bool TrySystemFontFile(string name, string style, out string path, out int index)
        {
            path = null;
            index = 0;
            if (!_systemFontProbed)
            {
                _systemFontProbed = true;
                Type reference = typeof(FontEngine).Assembly.GetType("UnityEngine.TextCore.LowLevel.FontReference");
                if (reference != null)
                {
                    const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    _systemFontReference = typeof(FontEngine).GetMethod("TryGetSystemFontReference", any, null,
                        new[] { typeof(string), typeof(string), reference.MakeByRefType() }, null);
                    _systemFontPath = reference.GetField("filePath", any);
                    _systemFontIndex = reference.GetField("faceIndex", any);
                }
            }
            if (_systemFontReference == null || _systemFontPath == null) return false;
            object[] args = { name, style, null };
            try
            {
                if (!(bool)_systemFontReference.Invoke(null, args) || args[2] == null) return false;
            }
            catch (Exception)
            {
                return false;
            }
            path = _systemFontPath.GetValue(args[2]) as string;
            index = _systemFontIndex != null ? Convert.ToInt32(_systemFontIndex.GetValue(args[2])) : 0;
            return !string.IsNullOrEmpty(path);
        }

        private ulong AdoptCssUrl(NativeDocument doc, string source)
        {
            if (string.IsNullOrEmpty(source)) return 0;
            if (_cssFaces.TryGetValue(source, out ulong face)) return face;
            byte[] bytes = null;
            try
            {
                bytes = doc.AssetReader?.Invoke(source);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            if (bytes == null || bytes.Length == 0) return 0;
            face = Adopt(bytes, 0, source);
            if (!Activate(_faces[face].Sources[0]))
            {
                _faces.Remove(face);
                return 0;
            }
            _cssFaces[source] = face;
            return face;
        }

        private static int ParseLeadingInt(string text, int fallback)
        {
            int end = 0;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            return end > 0 && int.TryParse(text.Substring(0, end), out int value) ? value : fallback;
        }

        /// <summary>
        /// Forgets which face FontEngine has loaded, so the next callback
        /// reloads it. Called before each update because FontEngine is shared
        /// with every other text user in the process.
        /// </summary>
        public void Invalidate()
        {
            _active = null;
            _activeSize = -1;
        }

        public void Dispose()
        {
            if (_self.IsAllocated) _self.Free();
            if (_scratch != null)
            {
                UnityEngine.Object.DestroyImmediate(_scratch);
                _scratch = null;
            }
            if (_bitmap != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_bitmap);
                _bitmap = IntPtr.Zero;
                _bitmapCapacity = 0;
            }
            _faces.Clear();
            Invalidate();
        }

        // ---- the C callbacks ----------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong LoadFaceFn(void* self, byte* data, nuint length, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FaceMetricsFn(void* self, ulong face, double px, double* ascent, double* descent, double* lineGap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GlyphIndexFn(void* self, ulong face, uint codepoint, uint* glyph);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GlyphMetricsFn(void* self, ulong face, uint glyph, double px, double* advance, double* bearingX, double* bearingY, int* width, int* height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RasterizeFn(void* self, ulong face, uint glyph, double px, weva_glyph_bitmap* bitmap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ShapeFn(void* self, ulong face, byte* utf8, nuint length, double px, uint* glyphs, double* advances, uint* clusters, nuint capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong VariantFn(void* self, ulong face, int weight, int italic);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint PositionedFn(void* self, ulong face, byte* utf8, nuint length, double px, weva_shaped_glyph* glyphs, nuint capacity);

        private static readonly LoadFaceFn s_loadFace = LoadFace;
        private static readonly FaceMetricsFn s_faceMetrics = FaceMetrics;
        private static readonly GlyphIndexFn s_glyphIndex = GlyphIndex;
        private static readonly GlyphMetricsFn s_glyphMetrics = GlyphMetrics;
        private static readonly RasterizeFn s_rasterize = Rasterize;
        private static readonly ShapeFn s_shape = Shape;
        private static readonly VariantFn s_variant = Variant;
        private static readonly PositionedFn s_positioned = ShapePositioned;

        private static UnityFontBackend Self(void* user) => GCHandle.FromIntPtr((IntPtr)user).Target as UnityFontBackend;

        [MonoPInvokeCallback(typeof(LoadFaceFn))]
        private static ulong LoadFace(void* self, byte* data, nuint length, int index)
        {
            UnityFontBackend me = Self(self);
            if (me == null || data == null || length == 0) return 0;
            try
            {
                byte[] copy = new byte[(int)length];
                Marshal.Copy((IntPtr)data, copy, 0, copy.Length);
                ulong face = me.Adopt(copy, index, "core-bytes");
                // A face the engine cannot open is not a face.
                if (!me.Activate(me._faces[face].Sources[0]))
                {
                    me._faces.Remove(face);
                    return 0;
                }
                return face;
            }
            catch (Exception ex)
            {
                me.LastError = ex.Message;
                return 0;
            }
        }

        [MonoPInvokeCallback(typeof(FaceMetricsFn))]
        private static int FaceMetrics(void* self, ulong face, double px, double* ascent, double* descent, double* lineGap)
        {
            UnityFontBackend me = Self(self);
            if (me == null || !me._faces.TryGetValue(face, out Face f)) return 0;
            Source s = f.Sources[0];
            if (!me.Activate(s) || s.UnitsPerEm <= 0) return 0;
            double scale = px / s.UnitsPerEm;
            if (ascent != null) *ascent = s.Ascent * scale;
            if (descent != null) *descent = s.Descent * scale;
            if (lineGap != null) *lineGap = s.LineGap * scale;
            return 1;
        }

        [MonoPInvokeCallback(typeof(GlyphIndexFn))]
        private static int GlyphIndex(void* self, ulong face, uint codepoint, uint* glyph)
        {
            UnityFontBackend me = Self(self);
            if (me == null || !me._faces.TryGetValue(face, out Face f)) return 0;
            uint id = me.Lookup(f, codepoint);
            if (glyph != null) *glyph = id;
            return id != 0 ? 1 : 0;
        }

        [MonoPInvokeCallback(typeof(GlyphMetricsFn))]
        private static int GlyphMetrics(void* self, ulong face, uint glyph, double px, double* advance, double* bearingX, double* bearingY, int* width, int* height)
        {
            UnityFontBackend me = Self(self);
            if (me == null || !me._faces.TryGetValue(face, out Face f)) return 0;
            int slot = SlotOf(glyph);
            if (slot >= f.Sources.Count) return 0;
            if (!me.Metrics(f.Sources[slot], IndexOf(glyph), SizeOf(px), out GlyphInfo info)) return 0;
            if (advance != null) *advance = info.Advance;
            if (bearingX != null) *bearingX = info.BearingX;
            if (bearingY != null) *bearingY = info.BearingY;
            if (width != null) *width = info.Width;
            if (height != null) *height = info.Height;
            return 1;
        }

        [MonoPInvokeCallback(typeof(RasterizeFn))]
        private static int Rasterize(void* self, ulong face, uint glyph, double px, weva_glyph_bitmap* bitmap)
        {
            UnityFontBackend me = Self(self);
            if (me == null || bitmap == null || !me._faces.TryGetValue(face, out Face f)) return 0;
            int slot = SlotOf(glyph);
            if (slot >= f.Sources.Count || IndexOf(glyph) == 0) return 0;
            return me.RasterizeGlyph(f.Sources[slot], IndexOf(glyph), SizeOf(px), bitmap) ? 1 : 0;
        }

        [MonoPInvokeCallback(typeof(ShapeFn))]
        private static nuint Shape(void* self, ulong face, byte* utf8, nuint length, double px, uint* glyphs, double* advances, uint* clusters, nuint capacity)
        {
            UnityFontBackend me = Self(self);
            if (me == null || !me._faces.TryGetValue(face, out Face f)) return 0;
            return me.ShapeRun(f, utf8, (int)length, SizeOf(px), glyphs, advances, clusters, null, (int)capacity);
        }

        [MonoPInvokeCallback(typeof(PositionedFn))]
        private static nuint ShapePositioned(void* self, ulong face, byte* utf8, nuint length, double px, weva_shaped_glyph* glyphs, nuint capacity)
        {
            UnityFontBackend me = Self(self);
            if (me == null || !me._faces.TryGetValue(face, out Face f)) return 0;
            return me.ShapeRun(f, utf8, (int)length, SizeOf(px), null, null, null, glyphs, (int)capacity);
        }

        [MonoPInvokeCallback(typeof(VariantFn))]
        private static ulong Variant(void* self, ulong face, int weight, int italic)
        {
            UnityFontBackend me = Self(self);
            if (me == null || !me._faces.TryGetValue(face, out Face f)) return face;
            return me.PickVariant(f, face, weight, italic != 0);
        }

        // ---- implementation -----------------------------------------------

        private static int SizeOf(double px)
        {
            int s = (int)Math.Round(px, MidpointRounding.AwayFromZero);
            return s > 0 ? s : 1;
        }

        private static int VariantKey(int weight, bool italic) => (italic ? 1 : 0) | ((weight >= 600 ? 1 : 0) << 1);

        private Face Require(ulong face)
        {
            if (!_faces.TryGetValue(face, out Face f)) throw new ArgumentException("unknown face " + face);
            return f;
        }

        private ulong Register(Source source)
        {
            EnsureInitialized();
            var face = new Face();
            face.Sources.Add(source);
            ulong id = _nextFace++;
            _faces[id] = face;
            return id;
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            FontEngine.InitializeFontEngine();
            _initialized = true;
        }

        private bool Activate(Source source)
        {
            if (ReferenceEquals(_active, source)) return true;
            EnsureInitialized();
            // The one-argument overloads load at point size 0, which is what
            // leaves GetFaceInfo reporting design units below. A face index
            // into a collection is not exposed by these overloads; a non-zero
            // index is recorded as an error and the first face is used.
            if (source.Index != 0) LastError = "font collections are not supported; face index " + source.Index + " ignored";
            FontEngineError error;
            if (source.Asset != null) error = FontEngine.LoadFontFace(source.Asset);
            else if (!string.IsNullOrEmpty(source.Path)) error = FontEngine.LoadFontFace(source.Path);
            else error = FontEngine.LoadFontFace(source.Bytes);
            if (error != FontEngineError.Success)
            {
                LastError = "LoadFontFace(" + source.Name + "): " + error;
                _active = null;
                return false;
            }
            FaceLoads++;
            _active = source;
            _activeSize = -1;
            if (!source.MetricsKnown)
            {
                // Design units: no size has been set on this load.
                FaceInfo info = FontEngine.GetFaceInfo();
                source.UnitsPerEm = info.pointSize > 0 ? info.pointSize : 1000;
                source.Ascent = info.ascentLine;
                source.Descent = -info.descentLine;
                source.LineGap = info.lineHeight - (info.ascentLine - info.descentLine);
                source.MetricsKnown = true;
            }
            return true;
        }

        private bool ActivateAt(Source source, int size)
        {
            if (!Activate(source)) return false;
            if (_activeSize == size) return true;
            if (FontEngine.SetFaceSize(size) != FontEngineError.Success) return false;
            _activeSize = size;
            return true;
        }

        private uint IndexIn(Source source, uint codepoint)
        {
            if (source.Indices.TryGetValue(codepoint, out uint index)) return index;
            index = 0;
            if (Activate(source) && FontEngine.TryGetGlyphIndex(codepoint, out uint found)) index = found;
            source.Indices[codepoint] = index;
            return index;
        }

        /// <summary>The glyph id for a code point: the primary's, else the first fallback's.</summary>
        private uint Lookup(Face face, uint codepoint)
        {
            for (int slot = 0; slot < face.Sources.Count; slot++)
            {
                uint index = IndexIn(face.Sources[slot], codepoint);
                if (index != 0) return ((uint)slot << SlotShift) | index;
            }
            return 0;
        }

        private static long GlyphKey(int size, uint index) => ((long)size << 32) | index;

        private bool Metrics(Source source, uint index, int size, out GlyphInfo info)
        {
            long key = GlyphKey(size, index);
            if (source.Glyphs.TryGetValue(key, out info)) return info.Present;
            info = default;
            if (ActivateAt(source, size))
            {
                // Unhinted metrics, as Chrome lays out; fall back to the
                // engine's default for a bitmap-only face, which has no
                // outlines for the hinter to leave alone.
                if (FontEngine.TryGetGlyphWithIndexValue(index, GlyphLoadFlags.LOAD_NO_HINTING | GlyphLoadFlags.LOAD_NO_BITMAP, out Glyph g) ||
                    FontEngine.TryGetGlyphWithIndexValue(index, GlyphLoadFlags.LOAD_DEFAULT, out g))
                {
                    info.Present = true;
                    info.Advance = g.metrics.horizontalAdvance;
                    info.BearingX = g.metrics.horizontalBearingX;
                    info.BearingY = g.metrics.horizontalBearingY;
                    info.Width = (int)Math.Ceiling(g.metrics.width);
                    info.Height = (int)Math.Ceiling(g.metrics.height);
                }
            }
            source.Glyphs[key] = info;
            return info.Present;
        }

        private double KernOf(Source source, int size, uint left, uint right)
        {
            long key = ((long)left << 32) | right;
            if (!source.Kerning.TryGetValue(key, out double em))
            {
                em = QueryPair(source, size, left, right);
                source.Kerning[key] = em;
            }
            return em * size;
        }

        // The engine's pair positioning for one adjacent pair, asked for once
        // per pair through FontEngine.GetPairAdjustmentRecords with a two-glyph
        // list (the query TextMeshPro fills a font asset's feature table with).
        // Records are design units, one per subtable covering the pair in the
        // font's order, and OpenType applies the first match, so the first
        // record wins. Asking per pair keeps that order reliable: a long glyph
        // list answered the same pair with its class subtable first.
        // FontEngine.GetPairAdjustmentRecord(first, second) is NOT used: on
        // Unity 6000.4 it returns an uninitialised record for a pair the face
        // does not kern and segfaulted inside the engine on an ordinary pair.
        private double QueryPair(Source source, int size, uint left, uint right)
        {
            MethodInfo query = PairRecordsMethod();
            if (query == null || !ActivateAt(source, size) || source.UnitsPerEm <= 0) return 0;
            _pairQuery.Clear();
            _pairQuery.Add(left);
            _pairQuery.Add(right);
            try
            {
                if (!(query.Invoke(null, new object[] { _pairQuery }) is GlyphPairAdjustmentRecord[] records)) return 0;
                foreach (GlyphPairAdjustmentRecord r in records)
                {
                    uint a = r.firstAdjustmentRecord.glyphIndex, b = r.secondAdjustmentRecord.glyphIndex;
                    if (a == 0 && b == 0) break;   // the array's unused tail
                    if (a != left || b != right) continue;
                    double units = r.firstAdjustmentRecord.glyphValueRecord.xAdvance +
                                   r.secondAdjustmentRecord.glyphValueRecord.xAdvance;
                    return units / source.UnitsPerEm;
                }
            }
            catch (Exception ex)
            {
                LastError = "GetPairAdjustmentRecords: " + ex.Message;
            }
            return 0;
        }

        private static bool HasGlyph(uint cp) => cp >= 0x20 && cp != 0x7F && !(cp >= 0x80 && cp < 0xA0);

        // Shapes the run (UnityFontBackend.Shaping.cs) and copies it out in
        // visual order. The legacy table callback carries no offsets, so a
        // combining mark through it sits at its pen with no advance; the
        // positioned callback, which the core prefers, carries them.
        private nuint ShapeRun(Face face, byte* utf8, int length, int size, uint* glyphs, double* advances, uint* clusters, weva_shaped_glyph* positioned, int capacity)
        {
            int count = ShapeInto(face, utf8, length, size);
            for (int v = 0; v < count && v < capacity; v++)
            {
                RunGlyph g = _run[_order[v]];
                if (glyphs != null) glyphs[v] = g.Id;
                if (advances != null) advances[v] = g.Advance;
                if (clusters != null) clusters[v] = g.Cluster;
                if (positioned != null)
                {
                    positioned[v].glyph = g.Id;
                    positioned[v].cluster = g.Cluster;
                    positioned[v].x_advance = g.Advance;
                    positioned[v].y_advance = 0;
                    positioned[v].x_offset = g.XOffset;
                    positioned[v].y_offset = g.YOffset;
                }
            }
            return (nuint)count;
        }

        /// <summary>UTF-8 to code points, each with the byte offset it starts at. Malformed bytes decode as U+FFFD, one per byte.</summary>
        private void Decode(byte* utf8, int length)
        {
            _codepoints.Clear();
            _offsets.Clear();
            int i = 0;
            while (i < length)
            {
                byte b = utf8[i];
                int extra;
                uint cp;
                if (b < 0x80) { cp = b; extra = 0; }
                else if ((b & 0xE0) == 0xC0) { cp = (uint)(b & 0x1F); extra = 1; }
                else if ((b & 0xF0) == 0xE0) { cp = (uint)(b & 0x0F); extra = 2; }
                else if ((b & 0xF8) == 0xF0) { cp = (uint)(b & 0x07); extra = 3; }
                else { cp = 0xFFFD; extra = 0; }
                if (extra > 0 && i + extra >= length)
                {
                    cp = 0xFFFD;   // truncated sequence
                    extra = 0;
                }
                bool valid = true;
                for (int k = 1; k <= extra; k++)
                {
                    byte c = utf8[i + k];
                    if ((c & 0xC0) != 0x80) { valid = false; break; }
                    cp = (cp << 6) | (uint)(c & 0x3F);
                }
                if (!valid) { cp = 0xFFFD; extra = 0; }
                _codepoints.Add(cp);
                _offsets.Add((uint)i);
                i += extra + 1;
            }
        }

        private ulong PickVariant(Face face, ulong self, int weight, bool italic)
        {
            if (face.Variants.Count == 0) return self;
            int wanted = VariantKey(weight, italic);
            if (face.Variants.TryGetValue(wanted, out ulong exact)) return exact;
            // Nearest: keep the italic axis if any italic face exists, else
            // the weight axis; the regular face otherwise.
            if (italic)
            {
                if (face.Variants.TryGetValue(VariantKey(weight >= 600 ? 400 : 700, true), out ulong otherWeight)) return otherWeight;
            }
            if (weight >= 600 && face.Variants.TryGetValue(VariantKey(700, false), out ulong bold)) return bold;
            return self;
        }

        // ---- rasterization -------------------------------------------------

        private static MethodInfo s_addGlyphToTexture;
        private static bool s_addGlyphLookedUp;

        private static MethodInfo AddGlyphToTexture()
        {
            if (s_addGlyphLookedUp) return s_addGlyphToTexture;
            s_addGlyphLookedUp = true;
            foreach (MethodInfo mi in typeof(FontEngine).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (mi.Name != "TryAddGlyphToTexture") continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length != 8 || ps[0].ParameterType != typeof(uint) || ps[1].ParameterType != typeof(int) ||
                    ps[6].ParameterType != typeof(Texture2D) || !ps[7].IsOut) continue;
                s_addGlyphToTexture = mi;
                break;
            }
            return s_addGlyphToTexture;
        }

        /// <summary>Whether FontEngine's glyph rasterizer could be bound in this Unity version.</summary>
        public static bool RasterizerAvailable => AddGlyphToTexture() != null;

        private static MethodInfo s_pairRecords;
        private static bool s_pairLookedUp;

        // FontEngine.GetPairAdjustmentRecords(List<uint> glyphIndexes): every
        // pair record among the listed glyphs, in the font's subtable order,
        // in design units. (The two-list overload threw on 6000.4.)
        private static MethodInfo PairRecordsMethod()
        {
            if (s_pairLookedUp) return s_pairRecords;
            s_pairLookedUp = true;
            const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            s_pairRecords = typeof(FontEngine).GetMethod("GetPairAdjustmentRecords", any, null, new[] { typeof(List<uint>) }, null);
            return s_pairRecords;
        }

        /// <summary>Whether the engine's pair-positioning query could be bound in this Unity version.</summary>
        public static bool KerningAvailable => PairRecordsMethod() != null;

        private bool RasterizeGlyph(Source source, uint index, int size, weva_glyph_bitmap* bitmap)
        {
            MethodInfo add = AddGlyphToTexture();
            if (add == null)
            {
                LastError = "FontEngine.TryAddGlyphToTexture not found";
                return false;
            }
            if (!Metrics(source, index, size, out GlyphInfo info)) return false;
            if (info.Width <= 0 || info.Height <= 0) return false;   // a space has no bitmap
            if (!ActivateAt(source, size)) return false;

            int side = Math.Max(info.Width, info.Height) + 4;
            EnsureScratch(side);
            _freeRects.Clear();
            _freeRects.Add(new GlyphRect(0, 0, _scratch.width, _scratch.height));
            _usedRects.Clear();
            var args = new object[] { index, 0, GlyphPackingMode.BestShortSideFit, _freeRects, _usedRects, GlyphRenderMode.SMOOTH, _scratch, default(Glyph) };
            bool ok;
            try
            {
                ok = (bool)add.Invoke(null, args);
            }
            catch (Exception ex)
            {
                LastError = "TryAddGlyphToTexture: " + ex.Message;
                return false;
            }
            if (!ok) return false;
            var packed = (Glyph)args[7];
            GlyphRect rect = packed.glyphRect;
            int w = rect.width, h = rect.height;
            if (w <= 0 || h <= 0 || rect.x < 0 || rect.y < 0 || rect.x + w > _scratch.width || rect.y + h > _scratch.height) return false;

            // The texture is stored bottom-up; the core wants the top row first.
            Unity.Collections.NativeArray<byte> raw = _scratch.GetRawTextureData<byte>();
            int stride = _scratch.width;
            EnsureBitmap(w * h);
            byte* dst = (byte*)_bitmap;
            for (int y = 0; y < h; y++)
            {
                int srcRow = rect.y + (h - 1 - y);
                for (int x = 0; x < w; x++) dst[y * w + x] = raw[srcRow * stride + rect.x + x];
            }
            // The bitmap's own edge is what the atlas packs, so the bearings
            // recorded for this glyph must describe this bitmap. The packed
            // glyph's metrics are still the unhinted outline's fractional
            // extents; FreeType rendered that outline into a bitmap whose left
            // column is floor(bearingX) and whose top row is ceil(bearingY)
            // (the same numbers TextServer reports as a glyph's offset on the
            // Godot host). The core snaps the quad to whole pixels, so a
            // fraction here rounded the other way put a glyph one pixel off
            // inside its word.
            info.Width = w;
            info.Height = h;
            info.BearingX = Math.Floor(packed.metrics.horizontalBearingX);
            info.BearingY = Math.Ceiling(packed.metrics.horizontalBearingY);
            source.Glyphs[GlyphKey(size, index)] = info;
            bitmap->alpha = dst;
            bitmap->width = w;
            bitmap->height = h;
            bitmap->rgba = null;
            return true;
        }

        private void EnsureScratch(int side)
        {
            int wanted = 64;
            while (wanted < side) wanted <<= 1;
            if (_scratch != null && _scratch.width >= wanted)
            {
                ClearScratch();
                return;
            }
            if (_scratch != null) UnityEngine.Object.DestroyImmediate(_scratch);
            _scratch = new Texture2D(wanted, wanted, TextureFormat.Alpha8, false, true) { name = "Weva.NativeGlyphScratch" };
            ClearScratch();
        }

        private void ClearScratch()
        {
            Unity.Collections.NativeArray<byte> raw = _scratch.GetRawTextureData<byte>();
            for (int i = 0; i < raw.Length; i++) raw[i] = 0;
        }

        private void EnsureBitmap(int bytes)
        {
            if (_bitmapCapacity >= bytes) return;
            if (_bitmap != IntPtr.Zero) Marshal.FreeHGlobal(_bitmap);
            _bitmapCapacity = Math.Max(bytes, 4096);
            _bitmap = Marshal.AllocHGlobal(_bitmapCapacity);
        }

        // ---- direct access for tests and diagnostics ----------------------

        public bool TryFaceMetrics(ulong face, double px, out double ascent, out double descent, out double lineGap)
        {
            ascent = descent = lineGap = 0;
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            double a, d, g;
            int ok = FaceMetrics((void*)GCHandle.ToIntPtr(_self), face, px, &a, &d, &g);
            ascent = a; descent = d; lineGap = g;
            return ok != 0;
        }

        public uint GlyphFor(ulong face, uint codepoint) => Lookup(Require(face), codepoint);

        public bool TryGlyphMetrics(ulong face, uint glyph, double px, out double advance, out double bearingX, out double bearingY, out int width, out int height)
        {
            advance = bearingX = bearingY = 0;
            width = height = 0;
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            double a, bx, by;
            int w, h;
            int ok = GlyphMetrics((void*)GCHandle.ToIntPtr(_self), face, glyph, px, &a, &bx, &by, &w, &h);
            advance = a; bearingX = bx; bearingY = by; width = w; height = h;
            return ok != 0;
        }

        /// <summary>Rasterizes through the same path the core uses; the coverage is copied out.</summary>
        public bool TryRasterize(ulong face, uint glyph, double px, out byte[] coverage, out int width, out int height)
        {
            coverage = null;
            width = height = 0;
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            weva_glyph_bitmap bitmap;
            if (Rasterize((void*)GCHandle.ToIntPtr(_self), face, glyph, px, &bitmap) == 0) return false;
            width = bitmap.width;
            height = bitmap.height;
            coverage = new byte[width * height];
            Marshal.Copy((IntPtr)bitmap.alpha, coverage, 0, coverage.Length);
            return true;
        }

        public struct Shaped
        {
            public uint Glyph, Cluster;
            public double Advance;
        }

        /// <summary>Shapes through the legacy table callback, honouring its sizing protocol.</summary>
        public List<Shaped> ShapeText(ulong face, string text, double px, out int total, int capacity = -1)
        {
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
            int cap = capacity < 0 ? utf8.Length + 1 : capacity;
            uint[] glyphs = new uint[Math.Max(cap, 1)];
            double[] advances = new double[Math.Max(cap, 1)];
            uint[] clusters = new uint[Math.Max(cap, 1)];
            fixed (byte* p = utf8)
            fixed (uint* g = glyphs)
            fixed (double* a = advances)
            fixed (uint* c = clusters)
            {
                total = (int)Shape((void*)GCHandle.ToIntPtr(_self), face, p, (nuint)utf8.Length, px, g, a, c, (nuint)cap);
            }
            var result = new List<Shaped>();
            for (int i = 0; i < Math.Min(total, cap); i++) result.Add(new Shaped { Glyph = glyphs[i], Cluster = clusters[i], Advance = advances[i] });
            return result;
        }

        /// <summary>Shapes through the positioned callback the core prefers.</summary>
        public List<weva_shaped_glyph> ShapePositionedText(ulong face, string text, double px, out int total)
        {
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var buffer = new weva_shaped_glyph[utf8.Length + 1];
            fixed (byte* p = utf8)
            fixed (weva_shaped_glyph* out_ = buffer)
            {
                total = (int)ShapePositioned((void*)GCHandle.ToIntPtr(_self), face, p, (nuint)utf8.Length, px, out_, (nuint)buffer.Length);
            }
            var result = new List<weva_shaped_glyph>();
            for (int i = 0; i < Math.Min(total, buffer.Length); i++) result.Add(buffer[i]);
            return result;
        }

        public ulong VariantOf(ulong face, int weight, bool italic) => PickVariant(Require(face), face, weight, italic);
    }
}
