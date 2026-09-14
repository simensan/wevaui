// The shaper half of the font backend. FontEngine has no shaper of its own:
// it maps a code point to a glyph, rasterizes a glyph, and answers a few
// positioning questions (pair kerning, a mark's anchor on its base). What it
// does not know is anything about Unicode -- which way a run reads, which
// bracket a right-to-left run draws instead, how Arabic letters join -- so
// that is asked of the core (ABI minor 40), from the same tables the core
// resolved the run's bidi level with. And its GSUB queries cannot be used:
// on Unity 6000.4 `GetSingleSubstitutionRecords` crashes the editor on an
// extension lookup (which is every Arabic lookup in Segoe UI) and the
// layout table it returns lists no lookups, so the substitutions are read
// from the font's own bytes here: a GSUB engine for the lookup kinds these
// features use (single, multiple, ligature, contextual and chaining
// contextual in all three formats), and from GPOS the cursive attachments
// that join a Nastaliq or swash script at anchors.
//
// What a run goes through, in logical order:
//   1. direction (the first strong character); in a right-to-left run,
//      brackets are mirrored (UAX #9 L4);
//   2. every code point to a glyph in a face (the primary, then fallbacks);
//   3. Arabic joining forms (ArabicShaping.txt), then the font's GSUB for
//      the features a browser enables by default -- `ccmp`, the form
//      features `isol` / `init` / `medi` / `fina` (each gated to the
//      glyphs in that form), `rlig`, `calt`, `liga` -- every enabled lookup
//      once over the run, in the font's lookup order, as OpenType says;
//   4. advances, pair kerning, and combining marks attached to their base
//      through the font's mark-to-base and mark-to-mark anchors;
//   5. a right-to-left run is reversed into VISUAL order, which is what the
//      core expects back (TextServer returns the same on the Godot host).
//
// An Indic run (Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil,
// Telugu, Kannada, Malayalam) takes its own path through step 3
// (UnityFontBackend.Indic.cs): syllables, the base consonant, the
// role-gated basic features, the matra and reph reordering. Not run:
// reverse chaining substitution, alternates, and the syllable models of
// Sinhala, Khmer, Myanmar and Tibetan; a font that needs them shapes as it
// would in a renderer without them. A face adopted as a Font asset has no bytes to read, so it
// gets no substitutions: the bundled UI face has no joining script anyway,
// and a font that does arrives as a file or as @font-face data, which both
// carry their bytes.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Weva.Native
{
    internal sealed unsafe partial class UnityFontBackend
    {
        // ---- Unicode facts, from the core ----------------------------------

        private readonly Dictionary<uint, int> _joiningTypes = new Dictionary<uint, int>();
        private readonly Dictionary<uint, uint> _mirrors = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> _scriptTags = new Dictionary<uint, uint>();

        private const int JoinNone = 0, JoinRight = 1, JoinLeft = 2, JoinDual = 3, JoinCausing = 4, JoinTransparent = 5;
        private const int FormNone = 0, FormIsol = 1, FormInit = 2, FormMedi = 3, FormFina = 4;
        private const uint ScriptCommon = 0x5A797979, ScriptInherited = 0x5A696E68, ScriptUnknown = 0x5A7A7A7A;   // Zyyy Zinh Zzzz

        private int JoiningTypeOf(uint cp)
        {
            if (!_joiningTypes.TryGetValue(cp, out int jt))
            {
                jt = WevaNative.weva_char_joining_type(cp);
                _joiningTypes[cp] = jt;
            }
            return jt;
        }

        private uint MirrorOf(uint cp)
        {
            if (!_mirrors.TryGetValue(cp, out uint m))
            {
                m = WevaNative.weva_char_mirror(cp);
                _mirrors[cp] = m;
            }
            return m;
        }

        private uint ScriptOf(uint cp)
        {
            if (!_scriptTags.TryGetValue(cp, out uint s))
            {
                s = WevaNative.weva_char_script(cp);
                _scriptTags[cp] = s;
            }
            return s;
        }

        /// <summary>Whether the core reads this run right-to-left (its first strong character is).</summary>
        public static bool IsRightToLeft(byte* utf8, int length) => WevaNative.weva_text_direction(utf8, (nuint)length) != 0;

        // The OpenType script tag of the run: its first character with a
        // script of its own, lower-cased ISO 15924, which is the OpenType tag
        // for every script this shaper runs features for.
        private string RunScriptTag(List<uint> cps)
        {
            for (int i = 0; i < cps.Count; i++)
            {
                uint s = ScriptOf(cps[i]);
                if (s == ScriptCommon || s == ScriptInherited || s == ScriptUnknown) continue;
                return new string(new[] { (char)((s >> 24) & 0xFF), (char)((s >> 16) & 0xFF), (char)((s >> 8) & 0xFF), (char)(s & 0xFF) }).ToLowerInvariant();
            }
            return null;
        }

        // ---- joining forms ---------------------------------------------------

        // The form of every code point in logical order (ArabicShaping.txt's
        // rules): a letter joined to the one before it takes `fina`, joined to
        // the one after it `init`, both `medi`, neither `isol`. Transparent
        // characters (marks) join through; a non-joining character breaks the
        // chain; a join-causing one (ZWJ, tatweel) joins both sides and takes
        // no form itself. Only letters of a joining script get a form at all.
        private readonly List<int> _forms = new List<int>(64);

        private void ResolveForms(List<uint> cps)
        {
            _forms.Clear();
            for (int i = 0; i < cps.Count; i++) _forms.Add(FormNone);
            bool previousConnectsForward = false;
            int previous = -1;
            for (int i = 0; i < cps.Count; i++)
            {
                int jt = JoiningTypeOf(cps[i]);
                if (jt == JoinTransparent) continue;
                if (jt == JoinNone)
                {
                    previousConnectsForward = false;
                    previous = -1;
                    continue;
                }
                bool connectsBackward = jt == JoinDual || jt == JoinRight || jt == JoinCausing;
                bool connectsForward = jt == JoinDual || jt == JoinLeft || jt == JoinCausing;
                bool joinedToPrevious = previousConnectsForward && connectsBackward;
                if (joinedToPrevious && previous >= 0 && _forms[previous] != FormNone)
                {
                    _forms[previous] = _forms[previous] == FormFina ? FormMedi : FormInit;
                }
                _forms[i] = jt == JoinCausing ? FormNone : (joinedToPrevious ? FormFina : FormIsol);
                previousConnectsForward = connectsForward;
                previous = i;
            }
        }

        // ---- GSUB, read from the font's bytes ----------------------------------

        private sealed class GsubLookup
        {
            public int Type;               // after resolving extensions
            public ushort Flag;            // LookupFlag: 0x0008 IgnoreMarks
            public readonly List<int> Subtables = new List<int>(2);   // absolute offsets into the font bytes
        }

        private sealed class GsubTable
        {
            public bool Loaded, Present;
            // script tag -> feature tag -> lookup indices (the script's
            // default language system, or its first language).
            public readonly Dictionary<string, Dictionary<string, List<int>>> FeaturesByScript = new Dictionary<string, Dictionary<string, List<int>>>();
            public readonly List<GsubLookup> Lookups = new List<GsubLookup>();
            public byte[] Bytes;
            // The enabled lookups for a script, in lookup order, each with
            // the forms it is gated to (0: every glyph).
            public readonly Dictionary<string, List<EnabledLookup>> EnabledByScript = new Dictionary<string, List<EnabledLookup>>();
        }

        private struct EnabledLookup
        {
            public int Index;
            public int FormMask;
        }

        private readonly Dictionary<Source, GsubTable> _gsub = new Dictionary<Source, GsubTable>();
        private readonly Dictionary<Source, GsubTable> _gpos = new Dictionary<Source, GsubTable>();

        private GsubTable GsubOf(Source source) => TableOf(source, "GSUB", 7, _gsub);
        private GsubTable GposOf(Source source) => TableOf(source, "GPOS", 9, _gpos);

        private static ushort U16(byte[] b, int at) => at >= 0 && at + 1 < b.Length ? (ushort)((b[at] << 8) | b[at + 1]) : (ushort)0;
        private static uint U32(byte[] b, int at) => at >= 0 && at + 3 < b.Length ? ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3] : 0u;
        private static string Tag4(byte[] b, int at) => at >= 0 && at + 3 < b.Length ? new string(new[] { (char)b[at], (char)b[at + 1], (char)b[at + 2], (char)b[at + 3] }) : "";

        private static byte[] BytesOf(Source source)
        {
            if (source.Bytes != null) return source.Bytes;
            if (!string.IsNullOrEmpty(source.Path))
            {
                try { source.Bytes = System.IO.File.ReadAllBytes(source.Path); }
                catch (Exception) { source.Bytes = null; }
            }
            return source.Bytes;
        }

        // A layout table (GSUB or GPOS; they share their script, feature and
        // lookup lists) of the face's font (or of the face `Index` names in a
        // collection): its scripts with each one's default language system's
        // features, and every lookup with its real type, extensions resolved.
        private GsubTable TableOf(Source source, string tag, int extensionType, Dictionary<Source, GsubTable> cache)
        {
            if (!cache.TryGetValue(source, out GsubTable table))
            {
                table = new GsubTable();
                cache[source] = table;
            }
            if (table.Loaded) return table;
            table.Loaded = true;
            byte[] b = BytesOf(source);
            if (b == null || b.Length < 12) return table;
            table.Bytes = b;
            int sfnt = 0;
            if (Tag4(b, 0) == "ttcf")
            {
                uint faces = U32(b, 8);
                int index = source.Index >= 0 && source.Index < faces ? source.Index : 0;
                sfnt = (int)U32(b, 12 + 4 * index);
            }
            int gsub = 0;
            int tableCount = U16(b, sfnt + 4);
            for (int i = 0; i < tableCount; i++)
            {
                int record = sfnt + 12 + 16 * i;
                if (Tag4(b, record) != tag) continue;
                gsub = (int)U32(b, record + 8);
                break;
            }
            if (gsub <= 0 || gsub + 10 > b.Length) return table;
            int scriptList = gsub + U16(b, gsub + 4);
            int featureList = gsub + U16(b, gsub + 6);
            int lookupList = gsub + U16(b, gsub + 8);
            if (scriptList >= b.Length || featureList >= b.Length || lookupList >= b.Length) return table;

            int lookupCount = U16(b, lookupList);
            for (int i = 0; i < lookupCount; i++)
            {
                int lookup = lookupList + U16(b, lookupList + 2 + 2 * i);
                var entry = new GsubLookup { Type = U16(b, lookup), Flag = U16(b, lookup + 2) };
                int subtableCount = U16(b, lookup + 4);
                bool extension = entry.Type == extensionType;
                for (int s = 0; s < subtableCount; s++)
                {
                    int subtable = lookup + U16(b, lookup + 6 + 2 * s);
                    if (extension)
                    {
                        // An extension (GSUB 7, GPOS 9): format, the real
                        // type, a 32-bit offset to the real subtable. Every
                        // subtable of the lookup wraps the same type.
                        int realType = U16(b, subtable + 2);
                        subtable += (int)U32(b, subtable + 4);
                        if (s == 0) entry.Type = realType;
                    }
                    entry.Subtables.Add(subtable);
                }
                table.Lookups.Add(entry);
            }

            int featureCount = U16(b, featureList);
            int scriptCount = U16(b, scriptList);
            for (int i = 0; i < scriptCount; i++)
            {
                int record = scriptList + 2 + 6 * i;
                string scriptTag = Tag4(b, record);
                int script = scriptList + U16(b, record + 4);
                int langSys = U16(b, script);   // the default language system; 0 when the script has none
                if (langSys == 0 && U16(b, script + 2) > 0) langSys = U16(b, script + 8);   // the first language's offset
                if (langSys == 0) continue;
                langSys += script;
                var byFeature = new Dictionary<string, List<int>>();
                int featureIndexCount = U16(b, langSys + 4);
                for (int f = 0; f < featureIndexCount; f++)
                {
                    int featureIndex = U16(b, langSys + 6 + 2 * f);
                    if (featureIndex >= featureCount) continue;
                    int featureRecord = featureList + 2 + 6 * featureIndex;
                    string featureTag = Tag4(b, featureRecord);
                    int feature = featureList + U16(b, featureRecord + 4);
                    int lookupIndexCount = U16(b, feature + 2);
                    if (!byFeature.TryGetValue(featureTag, out List<int> list)) byFeature[featureTag] = list = new List<int>();
                    for (int l = 0; l < lookupIndexCount; l++)
                    {
                        int lookupIndex = U16(b, feature + 4 + 2 * l);
                        if (lookupIndex < table.Lookups.Count) list.Add(lookupIndex);
                    }
                }
                table.FeaturesByScript[scriptTag] = byFeature;
            }
            table.Present = table.FeaturesByScript.Count > 0 && table.Lookups.Count > 0;
            return table;
        }

        private static Dictionary<string, List<int>> FeaturesFor(GsubTable table, string scriptTag)
        {
            if (table == null || !table.Present) return null;
            Dictionary<string, List<int>> byFeature = null;
            if (scriptTag != null) table.FeaturesByScript.TryGetValue(scriptTag, out byFeature);
            if (byFeature == null && !table.FeaturesByScript.TryGetValue("DFLT", out byFeature)) table.FeaturesByScript.TryGetValue("latn", out byFeature);
            return byFeature;
        }

        // The features a browser turns on by default, and which forms gate
        // them: the joining-form features apply to a glyph in that form only.
        private static readonly (string Tag, int FormMask)[] DefaultFeatures =
        {
            ("ccmp", 0), ("isol", 1 << FormIsol), ("init", 1 << FormInit), ("medi", 1 << FormMedi), ("fina", 1 << FormFina),
            ("rlig", 0), ("calt", 0), ("liga", 0),
        };

        // OpenType applies every enabled lookup once, in LookupList order,
        // whatever features enabled it; a lookup two features share is one
        // entry with both gates.
        private static List<EnabledLookup> EnabledFor(GsubTable table, string scriptTag)
        {
            string key = scriptTag ?? "";
            if (table.EnabledByScript.TryGetValue(key, out List<EnabledLookup> enabled)) return enabled;
            enabled = new List<EnabledLookup>();
            Dictionary<string, List<int>> byFeature = FeaturesFor(table, scriptTag);
            if (byFeature != null)
            {
                var masks = new Dictionary<int, int>();
                var all = new HashSet<int>();
                foreach ((string tag, int mask) in DefaultFeatures)
                {
                    if (!byFeature.TryGetValue(tag, out List<int> lookups)) continue;
                    foreach (int index in lookups)
                    {
                        if (mask == 0) all.Add(index);
                        masks[index] = masks.TryGetValue(index, out int m) ? m | mask : mask;
                    }
                }
                foreach (KeyValuePair<int, int> kv in masks) enabled.Add(new EnabledLookup { Index = kv.Key, FormMask = all.Contains(kv.Key) ? 0 : kv.Value });
                enabled.Sort((x, y) => x.Index.CompareTo(y.Index));
            }
            table.EnabledByScript[key] = enabled;
            return enabled;
        }

        // A glyph's index in a coverage table, or -1.
        private static int CoverageIndex(byte[] b, int coverage, uint glyph)
        {
            int format = U16(b, coverage);
            if (format == 1)
            {
                int count = U16(b, coverage + 2);
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    uint g = U16(b, coverage + 4 + 2 * mid);
                    if (g == glyph) return mid;
                    if (g < glyph) lo = mid + 1; else hi = mid - 1;
                }
                return -1;
            }
            if (format == 2)
            {
                int count = U16(b, coverage + 2);
                for (int i = 0; i < count; i++)
                {
                    int range = coverage + 4 + 6 * i;
                    uint start = U16(b, range), end = U16(b, range + 2);
                    if (glyph < start) return -1;
                    if (glyph <= end) return U16(b, range + 4) + (int)(glyph - start);
                }
            }
            return -1;
        }

        // ---- GPOS attachments, through FontEngine ------------------------------

        private struct Anchor
        {
            public bool Present;
            public float X, Y;   // design units: the mark's origin relative to the base's
        }

        private sealed class AnchorCache
        {
            public readonly Dictionary<long, Anchor> MarkToBase = new Dictionary<long, Anchor>();   // (base << 32 | mark)
            public readonly Dictionary<long, Anchor> MarkToMark = new Dictionary<long, Anchor>();   // (base mark << 32 | mark)
        }

        private readonly Dictionary<Source, AnchorCache> _anchors = new Dictionary<Source, AnchorCache>();

        private static readonly BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static bool s_anchorsLookedUp;
        private static MethodInfo s_getMarkToBase, s_getMarkToMark;
        private static readonly Dictionary<(Type, string), FieldInfo> s_fields = new Dictionary<(Type, string), FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo[]> s_floatFields = new Dictionary<Type, FieldInfo[]>();

        private static void BindAnchorQueries()
        {
            if (s_anchorsLookedUp) return;
            s_anchorsLookedUp = true;
            Type engine = typeof(FontEngine);
            s_getMarkToBase = engine.GetMethod("GetMarkToBaseAdjustmentRecord", AnyStatic, null, new[] { typeof(uint), typeof(uint) }, null);
            s_getMarkToMark = engine.GetMethod("GetMarkToMarkAdjustmentRecord", AnyStatic, null, new[] { typeof(uint), typeof(uint) }, null);
        }

        /// <summary>Whether FontEngine's mark attachment queries could be bound in this Unity version.</summary>
        public static bool MarkPositioningAvailable
        {
            get
            {
                BindAnchorQueries();
                return s_getMarkToBase != null;
            }
        }

        private static object Field(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            if (!s_fields.TryGetValue((t, name), out FieldInfo f))
            {
                f = t.GetField(name, AnyInstance);
                s_fields[(t, name)] = f;
            }
            return f?.GetValue(o);
        }

        // A struct's first two float fields, in declaration order: an anchor's
        // x and y, an adjustment's x and y.
        private static bool FloatPair(object o, out float x, out float y)
        {
            x = y = 0;
            if (o == null) return false;
            Type t = o.GetType();
            if (!s_floatFields.TryGetValue(t, out FieldInfo[] fs))
            {
                var found = new List<FieldInfo>();
                foreach (FieldInfo f in t.GetFields(AnyInstance)) if (f.FieldType == typeof(float)) found.Add(f);
                fs = found.ToArray();
                s_floatFields[t] = fs;
            }
            if (fs.Length < 2) return false;
            x = (float)fs[0].GetValue(o);
            y = (float)fs[1].GetValue(o);
            return true;
        }

        // The script the run's marks are positioned under: the run's tag, or
        // the Indic table tag its pass chose.
        private string _positioningScriptTag;

        // The mark's origin relative to the base's, in design units: the
        // base's anchor minus the mark's own anchor. Read from the font's
        // GPOS when its bytes are there (the `mark`, `mkmk`, `abvm` and
        // `blwm` lookups of the run's script, in lookup order); else from
        // FontEngine, whose record holds the mark anchor as its "position
        // adjustment" and reports both scaled to the ACTIVE face size
        // (measured: Segoe UI's beh/fatha anchors came back as their
        // font-file values times 16/2048 at 16px), so they are taken back to
        // design units. FontEngine's answer is a single record per pair and
        // misses a font that positions the same pair in a later lookup.
        private Anchor AnchorOf(Source source, uint baseGlyph, uint mark, int size, bool markToMark)
        {
            if (!_anchors.TryGetValue(source, out AnchorCache anchors))
            {
                anchors = new AnchorCache();
                _anchors[source] = anchors;
            }
            Dictionary<long, Anchor> cache = markToMark ? anchors.MarkToMark : anchors.MarkToBase;
            long key = ((long)baseGlyph << 32) | mark;
            if (cache.TryGetValue(key, out Anchor anchor)) return anchor;
            anchor = default;
            if (AnchorFromBytes(source, baseGlyph, mark, markToMark, out anchor))
            {
                cache[key] = anchor;
                return anchor;
            }
            BindAnchorQueries();
            MethodInfo query = markToMark ? s_getMarkToMark : s_getMarkToBase;
            if (query != null && ActivateAt(source, size) && source.UnitsPerEm > 0 && size > 0)
            {
                try
                {
                    object record = query.Invoke(null, new object[] { baseGlyph, mark });
                    uint recordBase = Field(record, markToMark ? "m_BaseMarkGlyphID" : "m_BaseGlyphID") is uint b ? b : 0;
                    uint recordMark = Field(record, markToMark ? "m_CombiningMarkGlyphID" : "m_MarkGlyphID") is uint m ? m : 0;
                    if (recordBase == baseGlyph && recordMark == mark &&
                        FloatPair(Field(record, markToMark ? "m_BaseMarkGlyphAnchorPoint" : "m_BaseGlyphAnchorPoint"), out float ax, out float ay) &&
                        FloatPair(Field(record, markToMark ? "m_CombiningMarkPositionAdjustment" : "m_MarkPositionAdjustment"), out float dx, out float dy))
                    {
                        double toUnits = source.UnitsPerEm / (double)size;
                        anchor.Present = true;
                        anchor.X = (float)((ax - dx) * toUnits);
                        anchor.Y = (float)((ay - dy) * toUnits);
                    }
                }
                catch (Exception ex) { LastError = (markToMark ? "GetMarkToMarkAdjustmentRecord: " : "GetMarkToBaseAdjustmentRecord: ") + ex.Message; }
            }
            cache[key] = anchor;
            return anchor;
        }

        private static readonly string[] MarkFeatures = { "mark", "mkmk", "abvm", "blwm" };

        // Mark-to-base (GPOS 4) and mark-to-mark (GPOS 6) attachment read
        // from the font: the first lookup, in lookup order, whose coverages
        // hold both glyphs and whose base record has an anchor for the
        // mark's class. Both formats share one layout: mark coverage, base
        // (or second mark) coverage, class count, mark array, base array.
        private bool AnchorFromBytes(Source source, uint baseGlyph, uint mark, bool markToMark, out Anchor anchor)
        {
            anchor = default;
            GsubTable gpos = GposOf(source);
            if (!gpos.Present) return false;
            Dictionary<string, List<int>> byFeature = FeaturesFor(gpos, _positioningScriptTag);
            if (byFeature == null) return false;
            var lookups = new SortedSet<int>();
            foreach (string feature in MarkFeatures)
                if (byFeature.TryGetValue(feature, out List<int> list)) foreach (int li in list) lookups.Add(li);
            byte[] b = gpos.Bytes;
            int wantedType = markToMark ? 6 : 4;
            foreach (int li in lookups)
            {
                if (li < 0 || li >= gpos.Lookups.Count) continue;
                GsubLookup lookup = gpos.Lookups[li];
                if (lookup.Type != wantedType) continue;
                foreach (int subtable in lookup.Subtables)
                {
                    if (U16(b, subtable) != 1) continue;
                    int markIndex = CoverageIndex(b, subtable + U16(b, subtable + 2), mark);
                    if (markIndex < 0) continue;
                    int baseIndex = CoverageIndex(b, subtable + U16(b, subtable + 4), baseGlyph);
                    if (baseIndex < 0) continue;
                    int classCount = U16(b, subtable + 6);
                    int markArray = subtable + U16(b, subtable + 8);
                    int baseArray = subtable + U16(b, subtable + 10);
                    if (markIndex >= U16(b, markArray) || baseIndex >= U16(b, baseArray)) continue;
                    int markRecord = markArray + 2 + 4 * markIndex;
                    int markClass = U16(b, markRecord);
                    int markAnchor = U16(b, markRecord + 2);
                    if (markClass >= classCount || markAnchor == 0) continue;
                    int baseAnchor = U16(b, baseArray + 2 + 2 * (classCount * baseIndex + markClass));
                    if (baseAnchor == 0) continue;
                    int ma = markArray + markAnchor, ba = baseArray + baseAnchor;
                    anchor.Present = true;
                    anchor.X = (short)U16(b, ba + 2) - (short)U16(b, ma + 2);
                    anchor.Y = (short)U16(b, ba + 4) - (short)U16(b, ma + 4);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Diagnostics: the GSUB read from the face that serves
        /// <paramref name="codepoint"/>, and the lookups <paramref name="feature"/>
        /// enables for its script.
        /// </summary>
        public string DescribeLayout(ulong faceId, uint codepoint, string feature)
        {
            var sb = new System.Text.StringBuilder();
            Face face = Require(faceId);
            uint id = Lookup(face, codepoint);
            if (id == 0) return "no glyph for U+" + codepoint.ToString("X4");
            Source src = face.Sources[SlotOf(id)];
            uint index = IndexOf(id);
            GsubTable t = GsubOf(src);
            sb.Append("source=").Append(src.Name).Append(" bytes=").Append(t.Bytes?.Length ?? 0).Append(" glyph=").Append(index)
              .Append(" present=").Append(t.Present).Append(" lookups=").Append(t.Lookups.Count).Append(" marks=").Append(MarkPositioningAvailable).Append('\n');
            foreach (KeyValuePair<string, Dictionary<string, List<int>>> script in t.FeaturesByScript)
            {
                sb.Append(" script '").Append(script.Key).Append("':");
                foreach (KeyValuePair<string, List<int>> f in script.Value) sb.Append(' ').Append(f.Key).Append('[').Append(string.Join(",", f.Value)).Append(']');
                sb.Append('\n');
            }
            string scriptTag = RunScriptTag(new List<uint> { codepoint });
            Dictionary<string, List<int>> byFeature = FeaturesFor(t, scriptTag);
            List<int> lookups = byFeature != null && byFeature.TryGetValue(feature, out List<int> l) ? l : null;
            sb.Append(" run script=").Append(scriptTag ?? "(none)").Append(" feature ").Append(feature).Append(" lookups=")
              .Append(lookups == null ? "(none)" : string.Join(",", lookups)).Append('\n');
            if (lookups != null)
            {
                foreach (int lookup in lookups)
                {
                    sb.Append("  lookup ").Append(lookup).Append(" type ").Append(t.Lookups[lookup].Type).Append(" flag ").Append(t.Lookups[lookup].Flag)
                      .Append(" subtables ").Append(t.Lookups[lookup].Subtables.Count);
                    if (t.Lookups[lookup].Type == 1) sb.Append(" single ").Append(index).Append(" -> ").Append(SingleSubstitute(t, t.Lookups[lookup], index));
                    sb.Append('\n');
                }
            }
            sb.Append(" enabled for script: ");
            foreach (EnabledLookup e in EnabledFor(t, scriptTag)) sb.Append(e.Index).Append(e.FormMask != 0 ? "g " : " ");
            GsubTable p = GposOf(src);
            sb.Append("\n GPOS present=").Append(p.Present).Append(" lookups=").Append(p.Lookups.Count).Append(" types=");
            foreach (GsubLookup lk in p.Lookups) sb.Append(lk.Type).Append(',');
            Dictionary<string, List<int>> gposFeatures = FeaturesFor(p, scriptTag);
            if (gposFeatures != null)
            {
                sb.Append(" features:");
                foreach (KeyValuePair<string, List<int>> f in gposFeatures) sb.Append(' ').Append(f.Key).Append('[').Append(string.Join(",", f.Value)).Append(']');
            }
            sb.Append("\n lastError=").Append(LastError ?? "(none)");
            return sb.ToString();
        }

        /// <summary>
        /// Diagnostics: the chaining-contextual subtables of a feature's
        /// lookups in the face serving <paramref name="codepoint"/>, and which
        /// of <paramref name="glyphs"/> each coverage table contains.
        /// </summary>
        public string DescribeContextual(ulong faceId, uint codepoint, string feature, params uint[] glyphs)
        {
            var sb = new System.Text.StringBuilder();
            Face face = Require(faceId);
            uint id = Lookup(face, codepoint);
            if (id == 0) return "no glyph";
            Source src = face.Sources[SlotOf(id)];
            GsubTable t = GsubOf(src);
            byte[] b = t.Bytes;
            string scriptTag = RunScriptTag(new List<uint> { codepoint });
            Dictionary<string, List<int>> byFeature = FeaturesFor(t, scriptTag);
            if (byFeature == null || !byFeature.TryGetValue(feature, out List<int> lookups)) return "no " + feature;
            foreach (int li in lookups)
            {
                GsubLookup lk = t.Lookups[li];
                sb.Append("lookup ").Append(li).Append(" type ").Append(lk.Type).Append(" flag ").Append(lk.Flag).Append('\n');
                foreach (int subtable in lk.Subtables)
                {
                    int format = U16(b, subtable);
                    sb.Append("  subtable@").Append(subtable).Append(" format ").Append(format);
                    if (lk.Type == 6 && format == 3)
                    {
                        int p = subtable + 2;
                        int bc = U16(b, p); int back = p + 2; p += 2 + 2 * bc;
                        int ic = U16(b, p); int input = p + 2; p += 2 + 2 * ic;
                        int lc = U16(b, p); int ahead = p + 2; p += 2 + 2 * lc;
                        int sc = U16(b, p);
                        sb.Append(" back ").Append(bc).Append(" input ").Append(ic).Append(" ahead ").Append(lc).Append(" records ").Append(sc);
                        for (int k = 0; k < bc; k++) { sb.Append(" back[").Append(k).Append("]:"); foreach (uint g in glyphs) sb.Append(' ').Append(g).Append('=').Append(CoverageIndex(b, subtable + U16(b, back + 2 * k), g)); }
                        for (int k = 0; k < ic; k++) { sb.Append(" input[").Append(k).Append("]:"); foreach (uint g in glyphs) sb.Append(' ').Append(g).Append('=').Append(CoverageIndex(b, subtable + U16(b, input + 2 * k), g)); }
                        for (int k = 0; k < lc; k++) { sb.Append(" ahead[").Append(k).Append("]:"); foreach (uint g in glyphs) sb.Append(' ').Append(g).Append('=').Append(CoverageIndex(b, subtable + U16(b, ahead + 2 * k), g)); }
                        for (int r = 0; r < sc; r++) sb.Append(" rec(").Append(U16(b, p + 2 + 4 * r)).Append(',').Append(U16(b, p + 4 + 4 * r)).Append(')');
                    }
                    else if (lk.Type == 6 && format == 1)
                    {
                        int coverage = subtable + U16(b, subtable + 2);
                        int setCount = U16(b, subtable + 4);
                        sb.Append(" coverage:");
                        foreach (uint g in glyphs) sb.Append(' ').Append(g).Append('=').Append(CoverageIndex(b, coverage, g));
                        sb.Append(" sets ").Append(setCount);
                        for (int s = 0; s < setCount && s < 4; s++)
                        {
                            int setOffset = U16(b, subtable + 6 + 2 * s);
                            if (setOffset == 0) { sb.Append(" set").Append(s).Append(":null"); continue; }
                            int set = subtable + setOffset;
                            int ruleCount = U16(b, set);
                            sb.Append(" set").Append(s).Append(" rules ").Append(ruleCount);
                            for (int r = 0; r < ruleCount && r < 3; r++)
                            {
                                int rule = set + U16(b, set + 2 + 2 * r);
                                int p = rule;
                                int bc = U16(b, p); p += 2; sb.Append(" [back");
                                for (int k = 0; k < bc; k++) { sb.Append(' ').Append(U16(b, p)); p += 2; }
                                int ic = U16(b, p); p += 2; sb.Append(" input(").Append(ic).Append(')');
                                for (int k = 1; k < ic; k++) { sb.Append(' ').Append(U16(b, p)); p += 2; }
                                int lc = U16(b, p); p += 2; sb.Append(" ahead");
                                for (int k = 0; k < lc; k++) { sb.Append(' ').Append(U16(b, p)); p += 2; }
                                int sc = U16(b, p); p += 2; sb.Append(" recs");
                                for (int k = 0; k < sc; k++) { sb.Append(" (").Append(U16(b, p)).Append(',').Append(U16(b, p + 2)).Append(')'); p += 4; }
                                sb.Append(']');
                            }
                        }
                    }
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        // ---- the run -----------------------------------------------------------

        private struct RunGlyph
        {
            public uint Id;            // slot | index, what the core sees
            public Source Src;
            public uint Index;         // glyph index within Src
            public uint Cluster;       // UTF-8 byte offset
            public int Form;
            public bool Mark;
            public int Base;           // logical index of the glyph this mark sits on, or -1
            public float AnchorX, AnchorY;   // design units, relative to Base's origin
            public double Advance, XOffset, YOffset;
            public int AttachedTo;     // cursive: the glyph this one is lifted with, or -1
            public double AttachY;     // cursive: this glyph's lift above AttachedTo's, pixels
            public uint Codepoint;     // the character this glyph came from
            public int Indic, IndicPos, Role;   // Indic: its categories and its role in the syllable
        }

        private readonly List<RunGlyph> _run = new List<RunGlyph>(64);
        private readonly List<int> _order = new List<int>(64);
        private readonly List<double> _pens = new List<double>(64);
        private readonly List<Source> _runSources = new List<Source>(2);

        /// <summary>
        /// Shapes the run into <see cref="_run"/>, in the order the core
        /// draws it (visual). Returns the glyph count.
        /// </summary>
        private int ShapeInto(Face face, byte* utf8, int length, int size)
        {
            Decode(utf8, length);
            int count = _codepoints.Count;
            bool rtl = count > 0 && IsRightToLeft(utf8, length);

            // 1. A right-to-left run draws mirrored brackets.
            if (rtl)
            {
                for (int i = 0; i < count; i++)
                {
                    uint m = MirrorOf(_codepoints[i]);
                    if (m != _codepoints[i]) _codepoints[i] = m;
                }
            }

            // 2. Every code point to a glyph in a face; 3a. joining forms.
            // A character is shaped as its canonical parts when it is an
            // Indic dependent vowel (a two-part sign has a half before the
            // consonant and a half after) or when no face has its glyph but
            // the parts (é as e + acute); the parts share its cluster.
            string scriptTag = RunScriptTag(_codepoints);
            _positioningScriptTag = scriptTag;
            bool indic = IsIndicScript(scriptTag);
            ResolveForms(_codepoints);
            _run.Clear();
            _runSources.Clear();
            uint* parts = stackalloc uint[4];
            for (int i = 0; i < count; i++)
            {
                uint cp = _codepoints[i];
                int partCount = 0;
                if (HasGlyph(cp) && (Lookup(face, cp) == 0 || (indic && IndicCategoryOf(cp).Category == IndicMatra)))
                {
                    partCount = WevaNative.weva_char_decompose(cp, parts, 4);
                    if (partCount < 1 || partCount > 4) partCount = 0;
                    for (int p = 0; p < partCount; p++) if (Lookup(face, parts[p]) == 0) { partCount = 0; break; }
                }
                for (int p = 0; p < Math.Max(1, partCount); p++)
                {
                    uint part = partCount > 0 ? parts[p] : cp;
                    var g = new RunGlyph { Cluster = _offsets[i], Base = -1, AttachedTo = -1, Codepoint = part };
                    // Control characters have no glyph and take no space; the core
                    // has already turned meaningful ones into breaks.
                    if (HasGlyph(part))
                    {
                        g.Id = Lookup(face, part);
                        if (g.Id != 0)
                        {
                            g.Src = face.Sources[SlotOf(g.Id)];
                            g.Index = IndexOf(g.Id);
                            g.Form = _forms[i];
                            g.Mark = JoiningTypeOf(part) == JoinTransparent;
                            if (!_runSources.Contains(g.Src)) _runSources.Add(g.Src);
                        }
                    }
                    _run.Add(g);
                }
            }

            // 3b. The font's substitutions, one face at a time: a run that
            // fell back mid-word shapes each face's part with that face's GSUB.
            foreach (Source src in _runSources)
            {
                GsubTable table = GsubOf(src);
                if (!table.Present) continue;
                // An Indic script reorders its syllables: its own pass
                // (UnityFontBackend.Indic.cs).
                if (IsIndicScript(scriptTag))
                {
                    ApplyIndic(face, src, scriptTag);
                    continue;
                }
                foreach (EnabledLookup enabled in EnabledFor(table, scriptTag))
                {
                    for (int i = 0; i < _run.Count; i++)
                    {
                        RunGlyph g = _run[i];
                        if (!ReferenceEquals(g.Src, src) || g.Id == 0) continue;
                        if (enabled.FormMask != 0 && (enabled.FormMask & (1 << g.Form)) == 0) continue;
                        ApplyLookupAt(table, src, enabled.Index, i, 0);
                    }
                }
            }

            // 4. Advances, kerning, marks.
            Source previousSource = null;
            uint previousIndex = 0;
            int previousBase = -1;
            for (int i = 0; i < _run.Count; i++)
            {
                RunGlyph g = _run[i];
                if (g.Src != null)
                {
                    if (Metrics(g.Src, g.Index, size, out GlyphInfo info)) g.Advance = info.Advance;
                    if (g.Mark && previousBase >= 0 && ReferenceEquals(_run[previousBase].Src, g.Src) && MarkPositioningAvailable)
                    {
                        // On the mark before it when the font stacks them
                        // (mark-to-mark), else on the base letter.
                        int previous = i - 1;
                        Anchor anchor = default;
                        int on = -1;
                        if (previous > previousBase && _run[previous].Mark && _run[previous].Base >= 0)
                        {
                            anchor = AnchorOf(g.Src, _run[previous].Index, g.Index, size, true);
                            if (anchor.Present) on = previous;
                        }
                        if (on < 0)
                        {
                            anchor = AnchorOf(g.Src, _run[previousBase].Index, g.Index, size, false);
                            if (anchor.Present) on = previousBase;
                        }
                        if (on >= 0)
                        {
                            g.Base = on;
                            g.AnchorX = anchor.X;
                            g.AnchorY = anchor.Y;
                            g.Advance = 0;
                        }
                    }
                    if (!g.Mark)
                    {
                        if (ReferenceEquals(g.Src, previousSource) && previousIndex != 0 && g.Index != 0)
                        {
                            // A pair adjustment belongs to the pair's first glyph.
                            double kern = KernOf(g.Src, size, previousIndex, g.Index);
                            if (kern != 0)
                            {
                                RunGlyph p = _run[previousBase];
                                p.Advance += kern;
                                _run[previousBase] = p;
                            }
                        }
                        previousIndex = g.Index;
                        previousSource = g.Src;
                        previousBase = i;
                    }
                }
                else
                {
                    previousIndex = 0;
                    previousSource = null;
                    previousBase = -1;
                }
                _run[i] = g;
            }

            // 4b. Cursive attachment (`curs`): a joined script whose letters
            // meet at anchors rather than on the baseline (Nastaliq, and the
            // swash forms of Dubai). Before the marks, which sit on the
            // lifted base.
            foreach (Source src in _runSources) ApplyCursive(src, scriptTag, rtl, size);
            ResolveAttachments();

            // 5. Visual order, and the marks placed against their base's
            // origin in that order (a mark's own pen position is wherever the
            // sequence put it; its offset makes up the difference).
            _order.Clear();
            for (int i = 0; i < _run.Count; i++) _order.Add(rtl ? _run.Count - 1 - i : i);
            _pens.Clear();
            double pen = 0;
            for (int v = 0; v < _order.Count; v++)
            {
                _pens.Add(pen);
                pen += _run[_order[v]].Advance;
            }
            // In logical order, so a stacked mark finds the mark below it
            // already placed whichever way the run reads.
            for (int logical = 0; logical < _run.Count; logical++)
            {
                RunGlyph g = _run[logical];
                if (g.Base < 0) continue;
                int v = rtl ? _run.Count - 1 - logical : logical;
                int baseVisual = rtl ? _run.Count - 1 - g.Base : g.Base;
                // The anchor is relative to the base's origin, which is left
                // of its ink in either direction; on a stacked mark the base
                // is the mark below, whose own offset carries.
                RunGlyph b = _run[g.Base];
                double scale = g.Src.UnitsPerEm > 0 ? size / g.Src.UnitsPerEm : 0;
                g.XOffset = (_pens[baseVisual] + b.XOffset + g.AnchorX * scale) - _pens[v];
                g.YOffset = b.YOffset + g.AnchorY * scale;
                _run[logical] = g;
            }
            return _run.Count;
        }

        // ---- cursive attachment (GPOS lookup type 3) ----------------------------

        // The entry and exit anchors a cursive lookup gives a glyph, in design
        // units; false when the glyph is not in the lookup's coverage.
        private static bool CursiveAnchors(byte[] b, GsubLookup lookup, uint glyph, out bool hasEntry, out float entryX, out float entryY,
                                           out bool hasExit, out float exitX, out float exitY)
        {
            hasEntry = hasExit = false;
            entryX = entryY = exitX = exitY = 0;
            foreach (int subtable in lookup.Subtables)
            {
                if (U16(b, subtable) != 1) continue;
                int index = CoverageIndex(b, subtable + U16(b, subtable + 2), glyph);
                if (index < 0 || index >= U16(b, subtable + 4)) continue;
                int record = subtable + 6 + 4 * index;
                int entry = U16(b, record), exit = U16(b, record + 2);
                if (entry != 0)
                {
                    hasEntry = true;
                    entryX = (short)U16(b, subtable + entry + 2);
                    entryY = (short)U16(b, subtable + entry + 4);
                }
                if (exit != 0)
                {
                    hasExit = true;
                    exitX = (short)U16(b, subtable + exit + 2);
                    exitY = (short)U16(b, subtable + exit + 4);
                }
                return true;
            }
            return false;
        }

        // The `curs` lookups of one face over the run: where a glyph's exit
        // anchor meets the next glyph's entry anchor, HarfBuzz's arithmetic.
        // Along the run the first glyph's advance ends at its exit and the
        // second starts at its entry (mirrored for a right-to-left run);
        // across it the child glyph -- the second, or the first when the
        // lookup is flagged right-to-left -- is lifted so the anchors
        // coincide, and a child carries its parent's lift (ResolveAttachments).
        private void ApplyCursive(Source src, string scriptTag, bool rtl, int size)
        {
            GsubTable gpos = GposOf(src);
            if (!gpos.Present) return;
            Dictionary<string, List<int>> byFeature = FeaturesFor(gpos, scriptTag);
            if (byFeature == null || !byFeature.TryGetValue("curs", out List<int> lookups)) return;
            double scale = src.UnitsPerEm > 0 ? size / src.UnitsPerEm : 0;
            byte[] b = gpos.Bytes;
            foreach (int lookupIndex in lookups)
            {
                if (lookupIndex < 0 || lookupIndex >= gpos.Lookups.Count) continue;
                GsubLookup lookup = gpos.Lookups[lookupIndex];
                if (lookup.Type != 3) continue;
                bool childIsFirst = (lookup.Flag & 0x0001) != 0;   // LookupFlag RightToLeft
                int previous = -1;
                for (int i = 0; i < _run.Count; i++)
                {
                    RunGlyph g = _run[i];
                    if (!ReferenceEquals(g.Src, src) || g.Id == 0 || g.Mark) continue;
                    if (previous >= 0 &&
                        CursiveAnchors(b, lookup, _run[previous].Index, out _, out _, out _, out bool hasExit, out float exitX, out float exitY) && hasExit &&
                        CursiveAnchors(b, lookup, g.Index, out bool hasEntry, out float entryX, out float entryY, out _, out _, out _) && hasEntry)
                    {
                        RunGlyph first = _run[previous], second = g;
                        double ex = exitX * scale, ey = exitY * scale, nx = entryX * scale, ny = entryY * scale;
                        if (!rtl)
                        {
                            first.Advance = ex + first.XOffset;
                            double d = nx + second.XOffset;
                            second.Advance -= d;
                            second.XOffset -= d;
                        }
                        else
                        {
                            double d = ex + first.XOffset;
                            first.Advance -= d;
                            first.XOffset -= d;
                            second.Advance = nx + second.XOffset;
                        }
                        if (childIsFirst)
                        {
                            first.AttachedTo = i;
                            first.AttachY = ny - ey;
                        }
                        else
                        {
                            second.AttachedTo = previous;
                            second.AttachY = ey - ny;
                        }
                        _run[previous] = first;
                        _run[i] = second;
                    }
                    previous = i;
                }
            }
        }

        // A cursively attached glyph's lift is its own plus its parent's, up
        // the chain (which can run either way through the run).
        private void ResolveAttachments()
        {
            for (int i = 0; i < _run.Count; i++)
            {
                if (_run[i].AttachedTo < 0) continue;
                double lift = 0;
                int at = i, guard = 0;
                while (at >= 0 && at < _run.Count && _run[at].AttachedTo >= 0 && guard++ < 64)
                {
                    lift += _run[at].AttachY;
                    at = _run[at].AttachedTo;
                }
                RunGlyph g = _run[i];
                g.YOffset += lift;
                _run[i] = g;
            }
        }

        // ---- applying lookups ----------------------------------------------------

        private const int MaxNesting = 4;
        private const ushort IgnoreMarks = 0x0008;

        private void Replace(int at, uint index)
        {
            RunGlyph g = _run[at];
            g.Index = index;
            g.Id = ((uint)SlotOf(g.Id) << SlotShift) | index;
            _run[at] = g;
        }

        // The positions of `count` glyphs of the lookup's sequence from
        // `start` onward (or, backwards, before it), in the same face,
        // skipping marks when the lookup ignores them; false when the run
        // ends or changes face first.
        private bool Sequence(Source src, ushort flag, int start, int count, bool backwards, List<int> positions)
        {
            positions.Clear();
            bool ignoreMarks = (flag & IgnoreMarks) != 0;
            int at = start;
            while (positions.Count < count)
            {
                if (at < 0 || at >= _run.Count) return false;
                RunGlyph c = _run[at];
                if (!ReferenceEquals(c.Src, src) || c.Id == 0) return false;
                if (ignoreMarks && c.Mark)
                {
                    at += backwards ? -1 : 1;
                    continue;
                }
                positions.Add(at);
                at += backwards ? -1 : 1;
            }
            return true;
        }

        private static uint SingleSubstitute(GsubTable table, GsubLookup lookup, uint glyph)
        {
            byte[] b = table.Bytes;
            foreach (int subtable in lookup.Subtables)
            {
                int format = U16(b, subtable);
                int index = CoverageIndex(b, subtable + U16(b, subtable + 2), glyph);
                if (index < 0) continue;
                if (format == 1) return (uint)((glyph + (short)U16(b, subtable + 4)) & 0xFFFF);
                if (format == 2 && index < U16(b, subtable + 4)) return U16(b, subtable + 6 + 2 * index);
            }
            return 0;
        }

        // Applies one lookup at logical position `at`; true when it
        // substituted something there. A ligature or a multiple
        // substitution changes the run's length; callers that hold
        // positions past `at` adjust by the change.
        private bool ApplyLookupAt(GsubTable table, Source src, int lookupIndex, int at, int depth)
        {
            if (depth > MaxNesting || lookupIndex < 0 || lookupIndex >= table.Lookups.Count || at < 0 || at >= _run.Count) return false;
            GsubLookup lookup = table.Lookups[lookupIndex];
            RunGlyph g = _run[at];
            if (!ReferenceEquals(g.Src, src) || g.Id == 0) return false;
            if ((lookup.Flag & IgnoreMarks) != 0 && g.Mark) return false;
            byte[] b = table.Bytes;
            switch (lookup.Type)
            {
                case 1:
                {
                    uint substitute = SingleSubstitute(table, lookup, g.Index);
                    if (substitute == 0) return false;
                    Replace(at, substitute);
                    return true;
                }
                case 2:
                {
                    // One glyph becomes a sequence; the new glyphs share its
                    // cluster and form, and are not marks.
                    foreach (int subtable in lookup.Subtables)
                    {
                        if (U16(b, subtable) != 1) continue;
                        int index = CoverageIndex(b, subtable + U16(b, subtable + 2), g.Index);
                        if (index < 0 || index >= U16(b, subtable + 4)) continue;
                        int sequence = subtable + U16(b, subtable + 6 + 2 * index);
                        int glyphCount = U16(b, sequence);
                        if (glyphCount == 0) continue;
                        Replace(at, U16(b, sequence + 2));
                        for (int k = 1; k < glyphCount; k++)
                        {
                            RunGlyph extra = _run[at];
                            extra.Index = U16(b, sequence + 2 + 2 * k);
                            extra.Id = ((uint)SlotOf(extra.Id) << SlotShift) | extra.Index;
                            extra.Mark = false;
                            extra.Base = -1;
                            _run.Insert(at + k, extra);
                        }
                        return true;
                    }
                    return false;
                }
                case 4:
                {
                    foreach (int subtable in lookup.Subtables)
                    {
                        if (U16(b, subtable) != 1) continue;
                        int index = CoverageIndex(b, subtable + U16(b, subtable + 2), g.Index);
                        if (index < 0 || index >= U16(b, subtable + 4)) continue;
                        int set = subtable + U16(b, subtable + 6 + 2 * index);
                        int ligatureCount = U16(b, set);
                        // The set lists longer ligatures first when the font
                        // is well-formed; the first that matches wins.
                        for (int i = 0; i < ligatureCount; i++)
                        {
                            int ligature = set + U16(b, set + 2 + 2 * i);
                            uint ligatureGlyph = U16(b, ligature);
                            int componentCount = U16(b, ligature + 2);
                            if (ligatureGlyph == 0 || componentCount < 2) continue;
                            var positions = new List<int>(componentCount);
                            if (!Sequence(src, lookup.Flag, at, componentCount, false, positions)) continue;
                            bool match = true;
                            for (int c = 1; c < componentCount && match; c++) match = _run[positions[c]].Index == U16(b, ligature + 4 + 2 * (c - 1));
                            if (!match) continue;
                            Replace(at, ligatureGlyph);
                            // The components go; marks that sat between them
                            // stay, and now follow the ligature.
                            for (int c = componentCount - 1; c >= 1; c--) _run.RemoveAt(positions[c]);
                            return true;
                        }
                    }
                    return false;
                }
                case 5:
                case 6:
                    return ApplyContextualAt(table, src, lookup, at, depth);
                default:
                    return false;
            }
        }

        // A glyph's class in a ClassDef table (0 when unlisted).
        private static int ClassOf(byte[] b, int classDef, uint glyph)
        {
            if (classDef <= 0) return 0;
            int format = U16(b, classDef);
            if (format == 1)
            {
                uint start = U16(b, classDef + 2);
                int count = U16(b, classDef + 4);
                if (glyph < start || glyph >= start + count) return 0;
                return U16(b, classDef + 6 + 2 * (int)(glyph - start));
            }
            if (format == 2)
            {
                int count = U16(b, classDef + 2);
                for (int i = 0; i < count; i++)
                {
                    int range = classDef + 4 + 6 * i;
                    uint start = U16(b, range), end = U16(b, range + 2);
                    if (glyph < start) return 0;
                    if (glyph <= end) return U16(b, range + 4);
                }
            }
            return 0;
        }

        // How a contextual rule names the glyphs it wants: a coverage table
        // per position (format 3), a glyph id (format 1), or a class in a
        // ClassDef (format 2).
        private enum RuleMode { Coverage, Glyph, Class }

        // Whether the glyphs at `positions` satisfy `count` rule elements
        // read from `elements` (u16 each: a coverage offset relative to
        // `subtable`, a glyph id, or a class value under `classDef`).
        private bool RuleMatches(byte[] b, int subtable, RuleMode mode, int classDef, int elements, int count, List<int> positions)
        {
            for (int k = 0; k < count; k++)
            {
                uint want = U16(b, elements + 2 * k);
                uint glyph = _run[positions[k]].Index;
                bool ok = mode == RuleMode.Coverage ? CoverageIndex(b, subtable + (int)want, glyph) >= 0
                        : mode == RuleMode.Glyph ? glyph == want
                        : ClassOf(b, classDef, glyph) == (int)want;
                if (!ok) return false;
            }
            return true;
        }

        // One rule of a contextual (5) or chaining contextual (6) lookup,
        // matched at `at` and applied: `backtrackCount`/`inputCount`/
        // `lookaheadCount` elements at the three `elements` offsets (the
        // input's first element is the glyph at `at` itself, already matched
        // by the coverage, so its elements start at the second glyph), then
        // the nested lookups at positions within the input.
        private bool ApplyRuleAt(GsubTable table, Source src, GsubLookup lookup, int subtable, RuleMode mode, int at, int depth,
                                 int backtrackCount, int backtrackElements, int backtrackClassDef,
                                 int inputCount, int inputElements, int inputClassDef,
                                 int lookaheadCount, int lookaheadElements, int lookaheadClassDef,
                                 int substCount, int records)
        {
            byte[] b = table.Bytes;
            if (inputCount == 0) return false;
            var inputs = new List<int>(inputCount);
            if (!Sequence(src, lookup.Flag, at, inputCount, false, inputs)) return false;
            // The first input glyph is `at` for every format: the coverage
            // (format 3) or the rule set's coverage (formats 1 and 2) already
            // matched it, and formats 1 and 2 list the rest only.
            if (mode == RuleMode.Coverage)
            {
                if (!RuleMatches(b, subtable, mode, inputClassDef, inputElements, inputCount, inputs)) return false;
            }
            else
            {
                var rest = new List<int>(inputCount);
                for (int k = 1; k < inputCount; k++) rest.Add(inputs[k]);
                if (!RuleMatches(b, subtable, mode, inputClassDef, inputElements, inputCount - 1, rest)) return false;
            }
            if (backtrackCount > 0)
            {
                var backs = new List<int>(backtrackCount);
                if (!Sequence(src, lookup.Flag, at - 1, backtrackCount, true, backs)) return false;
                if (!RuleMatches(b, subtable, mode, backtrackClassDef, backtrackElements, backtrackCount, backs)) return false;
            }
            if (lookaheadCount > 0)
            {
                var aheads = new List<int>(lookaheadCount);
                if (!Sequence(src, lookup.Flag, inputs[inputCount - 1] + 1, lookaheadCount, false, aheads)) return false;
                if (!RuleMatches(b, subtable, mode, lookaheadClassDef, lookaheadElements, lookaheadCount, aheads)) return false;
            }

            // The nested lookups, in the record order; one that shortens
            // the run (a ligature) shifts the input positions after it.
            bool any = false;
            for (int r = 0; r < substCount; r++)
            {
                int sequenceIndex = U16(b, records + 4 * r);
                int nested = U16(b, records + 4 * r + 2);
                if (sequenceIndex >= inputs.Count) continue;
                int position = inputs[sequenceIndex];
                int before = _run.Count;
                if (ApplyLookupAt(table, src, nested, position, depth + 1)) any = true;
                int change = _run.Count - before;
                if (change != 0) for (int k = 0; k < inputs.Count; k++) if (inputs[k] > position) inputs[k] += change;
            }
            return any;
        }

        // Contextual (5) and chaining contextual (6) substitution in all
        // three formats: rules by glyph sequence (1), by glyph class (2),
        // by a coverage table per position (3). The first matching rule of
        // the first matching subtable applies.
        private bool ApplyContextualAt(GsubTable table, Source src, GsubLookup lookup, int at, int depth)
        {
            byte[] b = table.Bytes;
            bool chain = lookup.Type == 6;
            uint glyph = _run[at].Index;
            foreach (int subtable in lookup.Subtables)
            {
                int format = U16(b, subtable);
                if (format == 3)
                {
                    int p = subtable + 2;
                    int backtrackCount = 0, backtrack = 0;
                    if (chain)
                    {
                        backtrackCount = U16(b, p);
                        backtrack = p + 2;
                        p += 2 + 2 * backtrackCount;
                    }
                    int inputCount = U16(b, p);
                    int input = p + 2;
                    p += 2 + 2 * inputCount;
                    int lookaheadCount = 0, lookahead = 0;
                    if (chain)
                    {
                        lookaheadCount = U16(b, p);
                        lookahead = p + 2;
                        p += 2 + 2 * lookaheadCount;
                    }
                    int substCount = U16(b, p);
                    if (ApplyRuleAt(table, src, lookup, subtable, RuleMode.Coverage, at, depth,
                                    backtrackCount, backtrack, 0, inputCount, input, 0, lookaheadCount, lookahead, 0, substCount, p + 2))
                        return true;
                    continue;
                }
                if (format != 1 && format != 2) continue;
                // Formats 1 and 2: a coverage picks the rule set (by the
                // glyph itself, or by its class under the input ClassDef),
                // and each rule lists the rest of the sequence.
                int coverageIndex = CoverageIndex(b, subtable + U16(b, subtable + 2), glyph);
                if (coverageIndex < 0) continue;
                RuleMode mode = format == 1 ? RuleMode.Glyph : RuleMode.Class;
                int backtrackClassDef = 0, inputClassDef = 0, lookaheadClassDef = 0;
                int setCountAt, setsAt;
                if (format == 1)
                {
                    setCountAt = subtable + 4;
                }
                else if (chain)
                {
                    backtrackClassDef = subtable + U16(b, subtable + 4);
                    inputClassDef = subtable + U16(b, subtable + 6);
                    lookaheadClassDef = subtable + U16(b, subtable + 8);
                    setCountAt = subtable + 10;
                }
                else
                {
                    inputClassDef = subtable + U16(b, subtable + 4);
                    setCountAt = subtable + 6;
                }
                setsAt = setCountAt + 2;
                int setCount = U16(b, setCountAt);
                int setIndex = format == 1 ? coverageIndex : ClassOf(b, inputClassDef, glyph);
                if (setIndex < 0 || setIndex >= setCount) continue;
                int setOffset = U16(b, setsAt + 2 * setIndex);
                if (setOffset == 0) continue;
                int set = subtable + setOffset;
                int ruleCount = U16(b, set);
                for (int r = 0; r < ruleCount; r++)
                {
                    int rule = set + U16(b, set + 2 + 2 * r);
                    int p = rule;
                    int backtrackCount = 0, backtrack = 0;
                    if (chain)
                    {
                        backtrackCount = U16(b, p);
                        backtrack = p + 2;
                        p += 2 + 2 * backtrackCount;
                    }
                    int inputCount = U16(b, p);
                    p += 2;
                    int substCount = 0, input, lookaheadCount = 0, lookahead = 0;
                    if (chain)
                    {
                        input = p;
                        p += 2 * Math.Max(0, inputCount - 1);
                        lookaheadCount = U16(b, p);
                        lookahead = p + 2;
                        p += 2 + 2 * lookaheadCount;
                        substCount = U16(b, p);
                        p += 2;
                    }
                    else
                    {
                        substCount = U16(b, p);
                        p += 2;
                        input = p;
                        p += 2 * Math.Max(0, inputCount - 1);
                    }
                    if (ApplyRuleAt(table, src, lookup, subtable, mode, at, depth,
                                    backtrackCount, backtrack, backtrackClassDef, inputCount, input, inputClassDef,
                                    lookaheadCount, lookahead, lookaheadClassDef, substCount, p))
                        return true;
                }
            }
            return false;
        }
    }
}
