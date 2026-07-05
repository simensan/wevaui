using System;
using System.Collections.Generic;
using Weva.Css.Values;
using Weva.Paint;

namespace Weva.Text.TextCore {
    // FontResolver maps a CSS-style font-family token (e.g. "Inter, sans-serif")
    // to a FaceInfo the active ITextCoreBackend can load.
    //
    // Resolution order:
    //   1. Each comma-separated family in handle.Family is tried in order.
    //   2. For each token: lookup is case-insensitive against the registered
    //      families (RegisterFont / RegisterFontFace) first, then against the
    //      OS-default mapping (sans-serif / monospace / serif / system-ui).
    //   3. Weight + style matching follows CSS Fonts L4 §5.2 (simplified) via
    //      FontFaceMatcher. The single-face fast path remains allocation-free.
    //   4. If nothing matches, falls back to DefaultFamily (default: "sans-serif").
    //
    // Registration:
    //   RegisterFont(family, path)    — back-compat: registers a single face
    //                                   covering weights 1-1000, style=normal.
    //   RegisterFontFace(family, path, weightMin, weightMax, isItalic)
    //                                — full @font-face registration with
    //                                  weight range and italic flag.
    //   Both are idempotent by (family, path, weightMin, weightMax, isItalic)
    //   tuple — duplicate calls are silently ignored.
    //
    // The OS-default mapping is populated lazily; tests can set it deterministically
    // via SetSystemDefaults.
    public static class FontResolver {
        // Per-family list of registered face entries (weight range + italic + path).
        // Key = family name, case-insensitive.
        static readonly Dictionary<string, List<FontFaceMatcher.FaceEntry>> faceRegistry =
            new Dictionary<string, List<FontFaceMatcher.FaceEntry>>(StringComparer.OrdinalIgnoreCase);

        // System-default mapping: generic-family → path.
        // Holds single-entry lists like the back-compat registered dict did.
        static readonly Dictionary<string, string> systemDefaults =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string defaultFamily = "sans-serif";
        static bool systemDefaultsInitialized;

        public static string DefaultFamily {
            get => defaultFamily;
            set {
                if (string.IsNullOrEmpty(value)) return;
                defaultFamily = value;
            }
        }

        // --- Registration API -----------------------------------------------

        // Back-compat: registers a single full-range (1–1000) normal face.
        // Idempotent: if the exact (path, 1, 1000, false) entry already exists
        // for this family, the call is a no-op.
        public static void RegisterFont(string family, string path) {
            RegisterFontFace(family, path, 1f, 1000f, false);
        }

        // Full-descriptor registration used by @font-face parsing (CSS Fonts L4 §11).
        // Adds (path, weightMin, weightMax, isItalic) to the face list for family.
        // Idempotent: duplicate (path, min, max, italic) entries are not added twice.
        //
        // weightMin/weightMax are clamped to [1, 1000] per CSS spec.
        // When weightMin > weightMax the values are swapped (defensive normalisation).
        public static void RegisterFontFace(
            string family, string path,
            float weightMin, float weightMax, bool isItalic)
        {
            if (string.IsNullOrEmpty(family)) return;
            string key = family.Trim();
            if (key.Length == 0) return;

            // Clamp and normalise
            weightMin = Math.Max(1f, Math.Min(1000f, weightMin));
            weightMax = Math.Max(1f, Math.Min(1000f, weightMax));
            if (weightMin > weightMax) { float t = weightMin; weightMin = weightMax; weightMax = t; }

            var entry = new FontFaceMatcher.FaceEntry(weightMin, weightMax, isItalic, path ?? string.Empty);

            if (!faceRegistry.TryGetValue(key, out var list)) {
                list = new List<FontFaceMatcher.FaceEntry>(2);
                faceRegistry[key] = list;
            }
            // Idempotency guard: skip if an identical entry already exists.
            for (int i = 0; i < list.Count; i++) {
                var existing = list[i];
                if (existing.Path == entry.Path
                    && existing.WeightMin == entry.WeightMin
                    && existing.WeightMax == entry.WeightMax
                    && existing.IsItalic == entry.IsItalic) {
                    return;
                }
            }
            list.Add(entry);
        }

        public static void UnregisterFont(string family) {
            if (string.IsNullOrEmpty(family)) return;
            faceRegistry.Remove(family.Trim());
        }

        public static void ClearRegistered() {
            faceRegistry.Clear();
        }

        // --- System defaults ------------------------------------------------

        public static void SetSystemDefaults(IDictionary<string, string> defaults) {
            systemDefaults.Clear();
            if (defaults != null) {
                foreach (var kv in defaults) {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    systemDefaults[kv.Key.Trim()] = kv.Value ?? string.Empty;
                }
            }
            systemDefaultsInitialized = true;
        }

        public static IReadOnlyDictionary<string, string> GetSystemDefaults() {
            EnsureSystemDefaultsInitialized();
            return systemDefaults;
        }

        // --- Resolution -----------------------------------------------------

        public static FaceInfo Resolve(FontHandle handle) {
            EnsureSystemDefaultsInitialized();
            int weight = handle.Weight == 0 ? 400 : handle.Weight;
            int styleFlags = MapStyleFlags(handle.Style);
            bool wantItalic = handle.Style == FontStyle.Italic || handle.Style == FontStyle.Oblique;

            string raw = handle.Family;
            if (!string.IsNullOrEmpty(raw)) {
                foreach (var token in SplitFamilies(raw)) {
                    if (TryResolveFace(token, weight, wantItalic, out var path, out var canonical)) {
                        return new FaceInfo(canonical, path, weight, styleFlags);
                    }
                }
            }
            if (TryResolveFace(defaultFamily, weight, wantItalic, out var defPath, out var defCanonical)) {
                return new FaceInfo(defCanonical, defPath, weight, styleFlags);
            }
            return new FaceInfo(defaultFamily, string.Empty, weight, styleFlags);
        }

        public static bool TryResolve(string family, out FaceInfo face) {
            EnsureSystemDefaultsInitialized();
            if (!string.IsNullOrEmpty(family)) {
                foreach (var token in SplitFamilies(family)) {
                    // TryResolve is a presence test used by SdfBootstrap.HasRegistered;
                    // it uses weight 400, non-italic (normal/regular face).
                    if (TryResolveFace(token, 400, false, out var path, out var canonical)) {
                        face = new FaceInfo(canonical, path, 400, FaceInfo.StyleNormal);
                        return true;
                    }
                }
            }
            face = FaceInfo.Empty;
            return false;
        }

        // --- Internal helpers -----------------------------------------------

        // Tries to resolve a single (already-split) family token to a path using
        // the registered face list and weight/italic matching, then the system
        // defaults (which are single-face, no weight discrimination needed).
        static bool TryResolveFace(
            string token, int weight, bool wantItalic,
            out string path, out string canonical)
        {
            string trimmed = StripFamilyQuotes(token);
            if (trimmed == null || trimmed.Length == 0) {
                path = null; canonical = null; return false;
            }

            // Registered face list — use CSS Fonts L4 §5.2 matching.
            if (faceRegistry.TryGetValue(trimmed, out var list) && list != null && list.Count > 0) {
                path = FontFaceMatcher.Match(list, weight, wantItalic);
                if (path != null) { canonical = trimmed; return true; }
            }

            // System defaults — weight-unaware (single path per generic family).
            if (systemDefaults.TryGetValue(trimmed, out path)) {
                canonical = trimmed;
                return true;
            }

            canonical = null;
            path = null;
            return false;
        }

        // Strips CSS quotes from a token and trims whitespace.
        // Returns null for empty results.
        static string StripFamilyQuotes(string token) {
            if (token == null) return null;
            string t = token.Trim();
            if (t.Length >= 2) {
                char first = t[0];
                char last  = t[t.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                    t = t.Substring(1, t.Length - 2).Trim();
                }
            }
            return t.Length == 0 ? null : t;
        }

        static IEnumerable<string> SplitFamilies(string raw) {
            int i = 0;
            int n = raw.Length;
            while (i < n) {
                int j = i;
                bool inDouble = false;
                bool inSingle = false;
                while (j < n) {
                    char c = raw[j];
                    if (c == '"' && !inSingle) inDouble = !inDouble;
                    else if (c == '\'' && !inDouble) inSingle = !inSingle;
                    else if (c == ',' && !inDouble && !inSingle) break;
                    j++;
                }
                string token = raw.Substring(i, j - i);
                if (!string.IsNullOrWhiteSpace(token)) yield return token;
                i = j + 1;
            }
        }

        static int MapStyleFlags(FontStyle style) {
            switch (style) {
                case FontStyle.Italic:  return FaceInfo.StyleItalic;
                case FontStyle.Oblique: return FaceInfo.StyleOblique;
                default:                return FaceInfo.StyleNormal;
            }
        }

        static void EnsureSystemDefaultsInitialized() {
            if (systemDefaultsInitialized) return;
            // Default mapping uses well-known generic family names. The actual
            // path is filled in by the active ITextCoreBackend at load time;
            // an empty path is a hint to "ask the OS for the default of this
            // generic family" via FontEngine.TryGetSystemFontReferences.
            systemDefaults["sans-serif"]    = string.Empty;
            systemDefaults["serif"]         = string.Empty;
            systemDefaults["monospace"]     = string.Empty;
            systemDefaults["system-ui"]     = string.Empty;
            systemDefaults["ui-sans-serif"] = string.Empty;
            systemDefaults["ui-serif"]      = string.Empty;
            systemDefaults["ui-monospace"]  = string.Empty;
            systemDefaultsInitialized = true;
        }
    }
}
