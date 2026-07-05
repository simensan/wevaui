#if UNITY_2023_1_OR_NEWER
using System.IO;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Weva.Paint;
using Weva.Text.TextCore;
using UFontStyle = Weva.Paint.FontStyle;

namespace Weva.Text.Sdf {
    // Unity-bound IFaceLoader implementation.
    //
    // Order:
    //   1. Resources.Load<Font>("Fonts/{family}-{weight}{style}") — direct hit.
    //   2. Resources.Load<Font>("Fonts/{family}") — generic.
    //   3. StreamingAssets/Fonts/{family}-{weight}{style}.ttf via path.
    //   4. The package's bundled Weva-Default font (Resources/Fonts/Weva-Default
    //      after the user copies it into Assets/Resources, or the package path).
    //   5. Font.CreateDynamicFontFromOSFont(family, 16) for system fonts.
    //
    // We register the loaded font with FontEngine via LoadFontFace(Font) so the
    // existing UnityFontEngineBackend path (advance lookup, glyph index) is
    // active without further changes. Note FontEngine on Unity 6000.4.x exposes
    // both LoadFontFace(Font) and LoadFontFace(string path); the byte[] overload
    // also exists. We prefer Font when available because Unity owns its lifetime
    // and the Font asset is reused on subsequent calls (no double-decode).
    public sealed class UnityFontLoader : FontLoader.IFaceLoader {
        // Font-resolution UNIFICATION bridge. Families registered here resolve to
        // a specific Font object instead of the Resources/OS lookup below.
        // SdfBootstrap registers the ATG primary's source font under the generic
        // families so the SDF fallback inside the ATG dispatcher rasterizes the
        // SAME face the ATG primary uses — both paths then differ only in HOW they
        // rasterize (hinted bitmap vs SDF), never in WHICH font (kills the
        // "font-flap"). A Font object (not a path) is used deliberately so this
        // also works in player builds, where TMP_FontAsset.sourceFontFile carries
        // no asset path.
        static readonly System.Collections.Generic.Dictionary<string, Font> familyFontOverrides =
            new System.Collections.Generic.Dictionary<string, Font>(System.StringComparer.OrdinalIgnoreCase);

        public static void RegisterFamilyFontOverride(string family, Font font) {
            if (string.IsNullOrEmpty(family) || font == null) return;
            familyFontOverrides[family] = font;
        }

        public static void ClearFamilyFontOverrides() => familyFontOverrides.Clear();

        public bool TryLoad(string family, UFontStyle style, int weight, out FaceInfo face) {
            face = default;
            if (string.IsNullOrEmpty(family)) return false;
            if (weight <= 0) weight = 400;

            EnsureInitialized();

            // Sanitise illegal-in-path characters in the family name. CSS font-family
            // values can carry quotes, commas, and other tokens that crash
            // Path.Combine; the FontLoader caller normally splits the stack, but
            // we defend here too in case someone passes a raw value through.
            string safeFamily = SanitizePathSegment(family);
            if (string.IsNullOrEmpty(safeFamily)) return false;

            // 0. Unification override — a family bridged to a specific Font (see
            //    RegisterFamilyFontOverride) wins over Resources/OS lookup, so the
            //    SDF fallback resolves the same face as the ATG primary. Mirrors
            //    the LoadFontFace(Font) → FaceInfo(family, font.name, …) path used
            //    for Resources-loaded fonts below; falls through on load failure.
            if (familyFontOverrides.TryGetValue(family, out var overrideFont) && overrideFont != null) {
                var oerr = FontEngine.LoadFontFace(overrideFont);
                if (oerr == FontEngineError.Success) {
                    face = new FaceInfo(family, overrideFont.name, weight, MapStyleFlags(style));
                    return true;
                }
            }

            // 1. Resources/Fonts/{family}-{weight}{style}
            string variantName = BuildVariantResourceName(safeFamily, weight, style);
            Font font = Resources.Load<Font>("Fonts/" + variantName);

            // 2. Resources/Fonts/{family}
            if (font == null) font = Resources.Load<Font>("Fonts/" + safeFamily);

            // 3. StreamingAssets/Fonts/{family}-{weight}{style}.ttf
            string path = null;
            if (font == null) {
                string saPath = Path.Combine(Application.streamingAssetsPath, "Fonts", variantName + ".ttf");
                if (File.Exists(saPath)) path = saPath;
            }

            // 4. Bundled package default — registered explicitly by callers via
            //    UnityFontEngineWarmup.RegisterPackagedDefault, which sets the
            //    "sans-serif"/"serif"/"monospace" generic mappings.
            if (font == null && path == null) {
                if (FontResolver.TryResolve(family, out var registered) && !string.IsNullOrEmpty(registered.Path)) {
                    if (File.Exists(registered.Path)) {
                        path = registered.Path;
                    } else {
                        // The registered path isn't a loose file on disk — e.g. a
                        // "Packages/com.wevaui/..." path that isn't shipped in a
                        // player build. Fall back to Resources by basename so
                        // bundled fonts placed under Resources/Fonts still load at
                        // runtime. (In the editor File.Exists is true, so this
                        // branch only matters for builds.)
                        string baseName = Path.GetFileNameWithoutExtension(registered.Path);
                        if (!string.IsNullOrEmpty(baseName)) {
                            font = Resources.Load<Font>("Fonts/" + baseName);
                        }
                        if (font == null) path = registered.Path; // last resort; LoadFontFace will warn
                    }
                }
            }

            // 5. System font (Win/macOS/Linux). Returns null on platforms with
            //    no font enumeration; the caller falls back to the OS default
            //    in that case.
            if (font == null && path == null) {
                font = Font.CreateDynamicFontFromOSFont(family, 16);
            }

            if (font != null) {
                var err = FontEngine.LoadFontFace(font);
                if (err != FontEngineError.Success) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("font-load",
                        "UnityFontLoader: FontEngine.LoadFontFace(Font='" + font.name + "') " +
                        "returned " + err + " for family '" + family + "' weight " + weight +
                        " style " + style + ". Font asset is present but the face data can't " +
                        "be loaded — likely a corrupt/unsupported .ttf/.otf, or the source " +
                        "was stripped by 'Clear Dynamic Data On Build'.");
                    return false;
                }
                int styleFlags = MapStyleFlags(style);
                face = new FaceInfo(family, font.name, weight, styleFlags);
                return true;
            }
            if (!string.IsNullOrEmpty(path)) {
                var err = FontEngine.LoadFontFace(path);
                if (err != FontEngineError.Success) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("font-load",
                        "UnityFontLoader: FontEngine.LoadFontFace(path='" + path + "') " +
                        "returned " + err + " for family '" + family + "' weight " + weight +
                        " style " + style + ". File path resolved but the face data can't " +
                        "be loaded — likely file missing, unreadable, or unsupported format.");
                    return false;
                }
                int styleFlags = MapStyleFlags(style);
                face = new FaceInfo(family, path, weight, styleFlags);
                return true;
            }
            // No font found through any path — log once per (family, weight, style)
            // so authors can see which font names are unresolvable.
            Weva.Diagnostics.UICssDiagnostics.Warn("font-load",
                "UnityFontLoader: no font found for family '" + family + "' weight " + weight +
                " style " + style + ". Searched: " +
                "Resources/Fonts/" + BuildVariantResourceName(safeFamily, weight, style) + ", " +
                "Resources/Fonts/" + safeFamily + ", " +
                "StreamingAssets/Fonts/" + BuildVariantResourceName(safeFamily, weight, style) + ".ttf, " +
                "FontResolver registry, OS system fonts. " +
                "Place the .ttf under Assets/Resources/Fonts/" + safeFamily + ".ttf " +
                "OR Assets/StreamingAssets/Fonts/" + BuildVariantResourceName(safeFamily, weight, style) + ".ttf " +
                "OR register the family via FontResolver.Register(...).");
            return false;
        }

        static string BuildVariantResourceName(string family, int weight, UFontStyle style) {
            string weightToken = weight switch {
                <= 350 => "Light",
                <= 450 => "Regular",
                <= 550 => "Medium",
                <= 650 => "SemiBold",
                <= 750 => "Bold",
                _ => "Black"
            };
            string styleToken = style == UFontStyle.Italic || style == UFontStyle.Oblique ? "Italic" : "";
            // Inter-style naming: "Family-WeightStyle" with no separator between weight and style.
            return family + "-" + weightToken + styleToken;
        }

        static int MapStyleFlags(UFontStyle style) {
            switch (style) {
                case UFontStyle.Italic: return FaceInfo.StyleItalic;
                case UFontStyle.Oblique: return FaceInfo.StyleOblique;
                default: return FaceInfo.StyleNormal;
            }
        }

        static bool initialized;
        static void EnsureInitialized() {
            if (initialized) return;
            FontEngine.InitializeFontEngine();
            initialized = true;
        }

        // Strips characters that are illegal in file paths or Resources keys.
        // Returns null/empty when the family name is entirely illegal so the
        // caller can short-circuit without throwing.
        static string SanitizePathSegment(string raw) {
            if (string.IsNullOrEmpty(raw)) return raw;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw) {
                bool bad = c == '"' || c == '\'' || c == ',' || c == '<' || c == '>' || c == '|';
                if (!bad) {
                    bool isInvalid = false;
                    for (int i = 0; i < invalid.Length; i++) {
                        if (c == invalid[i]) { isInvalid = true; break; }
                    }
                    if (!isInvalid) sb.Append(c);
                }
            }
            return sb.ToString().Trim();
        }
    }
}
#endif
