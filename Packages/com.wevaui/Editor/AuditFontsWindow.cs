#if UNITY_2023_1_OR_NEWER && WEVA_TMP
// AuditFontsWindow — Phase 2 of the font-variant DX.
//
// Renders the live list of (family, weight, style) variant misses and emoji
// codepoint misses collected by TmpFontAssetRegistry. For each variant row the
// author picks a source .ttf and clicks "Bake": we call TMP_FontAsset.CreateFontAsset
// with sensible defaults, save it under Assets/UI/Fonts/, register it as a
// fallback in TmpFontAssetRegistry, and drop the entry from the miss list.
//
// For each emoji-codepoint miss we attempt to add the codepoint to the existing
// NotoColorEmoji COLOR + SDF atlases via TMP_FontAsset.TryAddCharacters.
//
// NOTE: emoji already render at runtime via the bundled Noto Color Emoji font
// (SdfBootstrap's ATG color fallback); these baked TMP atlases are only needed
// for the static/TMP measurement path. Absence is informational, not an error.

using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Weva.Paint;
using Weva.Text.Tmp;

namespace Weva.EditorTools {
    public sealed class AuditFontsWindow : EditorWindow {
        const string FontsFolder = "Assets/UI/Fonts";
        const string EmojiColorAssetPath = "Assets/UI/Fonts/NotoColorEmoji COLOR.asset";
        const string EmojiSdfAssetPath   = "Assets/UI/Fonts/NotoColorEmoji SDF.asset";

        // Per-row author-picked source TTF. Keyed by "family|weight|style".
        Dictionary<string, Font> sourcePicks = new();
        Vector2 scroll;

        [MenuItem("Window/Weva/Audit Fonts")]
        public static void Open() {
            GetWindow<AuditFontsWindow>("Audit Fonts").Show();
        }

        void OnGUI() {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Font-variant misses collected this session.",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each row corresponds to a (family, weight, style) tuple that CSS requested " +
                "but no registered TMP_FontAsset matched. Pick a source .ttf and click Bake " +
                "to create + register a matching variant.",
                MessageType.Info);

            if (GUILayout.Button("Refresh / Clear Picks")) {
                sourcePicks.Clear();
                Repaint();
            }

            DrawVariantSection();
            EditorGUILayout.Space();
            DrawEmojiSection();
            EditorGUILayout.EndScrollView();
        }

        // ---- Variant misses ---------------------------------------------------
        void DrawVariantSection() {
            EditorGUILayout.LabelField("Missing Font Variants", EditorStyles.boldLabel);
            var misses = TmpFontAssetRegistry.GetVariantMisses();
            if (misses.Count == 0) {
                EditorGUILayout.HelpBox("No variant misses recorded yet.", MessageType.None);
                return;
            }

            for (int i = 0; i < misses.Count; i++) {
                var m = misses[i];
                string key = m.Family + "|" + m.Weight + "|" + m.Style;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    $"family='{m.Family}'  weight={m.Weight}  style={m.Style}",
                    EditorStyles.miniBoldLabel);

                sourcePicks.TryGetValue(key, out var picked);
                picked = (Font)EditorGUILayout.ObjectField("Source TTF", picked, typeof(Font), false);
                sourcePicks[key] = picked;

                using (new EditorGUI.DisabledScope(picked == null)) {
                    if (GUILayout.Button("Bake")) {
                        BakeVariant(m, picked);
                    }
                }
                EditorGUILayout.EndVertical();
            }
        }

        // Bake a TMP_FontAsset for the requested variant. Saved under
        // Assets/UI/Fonts/ with a name derived from the source .ttf, then
        // appended to the family's fallback chain so subsequent shaping calls
        // route through it.
        void BakeVariant(TmpFontAssetRegistry.VariantMiss miss, Font source) {
            if (source == null) return;
            Directory.CreateDirectory(FontsFolder);
            FontEngine.InitializeFontEngine();

            string baseName = source.name;
            string outPath = $"{FontsFolder}/{baseName} SDF.asset";
            if (File.Exists(outPath)) {
                if (!EditorUtility.DisplayDialog("Overwrite?",
                        $"{outPath} already exists. Replace it?",
                        "Replace", "Cancel")) return;
                AssetDatabase.DeleteAsset(outPath);
            }

            TMP_FontAsset asset;
            try {
                asset = TMP_FontAsset.CreateFontAsset(
                    source,
                    samplingPointSize: 32,
                    atlasPadding: 9,
                    renderMode: GlyphRenderMode.SDFAA,
                    atlasWidth: 2048,
                    atlasHeight: 2048,
                    atlasPopulationMode: AtlasPopulationMode.Dynamic,
                    enableMultiAtlasSupport: true);
            } catch (System.Exception e) {
                Debug.LogError("[AuditFontsWindow] CreateFontAsset threw: " + e.Message);
                return;
            }
            if (asset == null) {
                Debug.LogError("[AuditFontsWindow] CreateFontAsset returned null for " + baseName);
                return;
            }

            asset.name = baseName + " SDF";
            AssetDatabase.CreateAsset(asset, outPath);
            if (asset.atlasTextures != null) {
                for (int i = 0; i < asset.atlasTextures.Length; i++) {
                    var tex = asset.atlasTextures[i];
                    if (tex == null) continue;
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex))) {
                        tex.name = baseName + " Atlas " + i;
                        AssetDatabase.AddObjectToAsset(tex, asset);
                    }
                }
            }
            if (asset.material != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset.material))) {
                asset.material.name = baseName + " Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }

            // Pre-bake the printable ASCII range so first-frame text doesn't
            // stall on dynamic rasterization. The caller's variant is normally
            // a Latin face (Bold / Italic of an existing family); larger
            // ranges can be added later by re-running the bake.
            var unicodes = new List<uint>();
            for (uint cp = 0x20; cp < 0x7F; cp++) unicodes.Add(cp);
            asset.TryAddCharacters(unicodes.ToArray(), out _, includeFontFeatures: false);

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Register as a fallback on the family so the shaper can pick it
            // up immediately. AddFallback is a no-op if the primary isn't
            // registered yet — in that case authors should set the primary
            // via their bootstrap (e.g. UitestController) first.
            TmpFontAssetRegistry.AddFallback(miss.Family, asset);
            TmpFontAssetRegistry.ForgetVariantMiss(miss.Family, miss.Weight, miss.Style);

            Debug.Log($"[AuditFontsWindow] Baked '{baseName}' -> {outPath} and registered as fallback for family '{miss.Family}'.");
            Repaint();
        }

        // ---- Emoji misses -----------------------------------------------------
        void DrawEmojiSection() {
            EditorGUILayout.LabelField("Missing Emoji Codepoints", EditorStyles.boldLabel);
            var misses = TmpFontAssetRegistry.GetEmojiMisses();
            if (misses.Count == 0) {
                EditorGUILayout.HelpBox("No emoji codepoint misses recorded yet.", MessageType.None);
                return;
            }
            EditorGUILayout.HelpBox(
                $"Click Bake to attempt adding each codepoint to existing emoji atlases " +
                $"({EmojiColorAssetPath} and {EmojiSdfAssetPath}).",
                MessageType.Info);

            if (GUILayout.Button("Bake All Missing Emoji")) {
                BakeAllEmoji(misses);
            }
            for (int i = 0; i < misses.Count; i++) {
                uint cp = misses[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                string glyph = CodepointToString(cp);
                EditorGUILayout.LabelField($"U+{cp:X4}   {glyph}");
                if (GUILayout.Button("Bake", GUILayout.Width(60))) {
                    BakeOneEmoji(cp);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void BakeAllEmoji(IReadOnlyList<uint> codepoints) {
            int added = 0;
            var color = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(EmojiColorAssetPath);
            var sdf   = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(EmojiSdfAssetPath);
            if (color == null && sdf == null) {
                Debug.Log("[AuditFontsWindow] No pre-baked emoji atlas at " +
                    EmojiColorAssetPath + " or " + EmojiSdfAssetPath +
                    ". This is optional — emoji already render via the bundled " +
                    "Noto Color Emoji fallback. Create a TMP emoji FontAsset at " +
                    "one of those paths only if you need the static/TMP bake path.");
                return;
            }
            FontEngine.InitializeFontEngine();
            foreach (var cp in codepoints) {
                if (TryAddEmojiCodepoint(cp, color, sdf)) added++;
            }
            if (color != null) EditorUtility.SetDirty(color);
            if (sdf   != null) EditorUtility.SetDirty(sdf);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AuditFontsWindow] Baked {added} of {codepoints.Count} missing emoji codepoints.");
            Repaint();
        }

        void BakeOneEmoji(uint codepoint) {
            var color = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(EmojiColorAssetPath);
            var sdf   = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(EmojiSdfAssetPath);
            FontEngine.InitializeFontEngine();
            bool ok = TryAddEmojiCodepoint(codepoint, color, sdf);
            if (color != null) EditorUtility.SetDirty(color);
            if (sdf   != null) EditorUtility.SetDirty(sdf);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AuditFontsWindow] Bake U+{codepoint:X4} -> {(ok ? "ok" : "miss")}");
            Repaint();
        }

        // Attempts to add a single codepoint to both color and SDF atlases.
        // Supplementary-plane codepoints (> U+FFFF) are expanded into UTF-16
        // surrogate pairs, which is what TMP's TryAddCharacters consumes.
        bool TryAddEmojiCodepoint(uint codepoint, TMP_FontAsset color, TMP_FontAsset sdf) {
            var buf = new List<uint>(2);
            if (codepoint > 0xFFFF) {
                uint v = codepoint - 0x10000;
                buf.Add(0xD800u + (v >> 10));
                buf.Add(0xDC00u + (v & 0x3FFu));
            } else {
                buf.Add(codepoint);
            }
            var arr = buf.ToArray();
            bool any = false;
            if (color != null) {
                color.TryAddCharacters(arr, out var missing, includeFontFeatures: false);
                if (missing == null || missing.Length < arr.Length) any = true;
            }
            if (sdf != null) {
                sdf.TryAddCharacters(arr, out var missing, includeFontFeatures: false);
                if (missing == null || missing.Length < arr.Length) any = true;
            }
            if (any) TmpFontAssetRegistry.ForgetEmojiMiss(codepoint);
            return any;
        }

        static string CodepointToString(uint cp) {
            return cp <= 0xFFFF ? char.ConvertFromUtf32((int)cp) : char.ConvertFromUtf32((int)cp);
        }
    }
}
#endif
