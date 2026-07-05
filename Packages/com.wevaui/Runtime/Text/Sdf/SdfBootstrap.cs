#if UNITY_2023_1_OR_NEWER
using UnityEngine;
using Weva.Documents;
using Weva.Layout.Text;
using Weva.Text.TextCore;
using Weva.Text.Tmp;
using Weva.Text.Unity;

namespace Weva.Text.Sdf {
    // SdfBootstrap is the v1 picker. It replaces the v0.3 TextCoreBootstrap path:
    //
    //   1. SdfFontMetrics — full TTF/SDF path. Selected when running in the
    //      Player or Editor Play Mode (Application.isPlaying), the FontEngine
    //      is available, and the bundled / system fallback can produce a
    //      valid FaceInfo for "sans-serif". This is the production path.
    //
    //   2. UnityGUIFontMetrics — IMGUI / GUIStyle-derived. Used in Editor
    //      Edit Mode (preview, scene-view document instances) where Play
    //      Mode hasn't started but GUI.skin is available. This still
    //      produces proportional metrics that match IMGUIDocumentRenderer.
    //
    //   3. MonoFontMetrics — last-ditch headless fallback. Reached only when
    //      both TextCore and GUI paths throw.
    //
    // Hooked at SubsystemRegistration so UIDocumentDefaults.FontMetricsFactory
    // is in place before any WevaDocument.OnEnable. Editor InitializeOnLoadMethod
    // runs the same wiring so the preview window has fonts in Edit Mode.
    public static class SdfBootstrap {
        static FontLoader sharedLoader;
        static UnityFontEngineBackend sharedBackend;
        static GlyphAtlas sharedAtlas;
        static SdfGlyphRasterizer sharedRasterizer;
        static SdfGlyphAtlasAdapter sharedAdapter;
        // Cached dispatching atlas + metrics from the last TryCreateTmp, keyed on the resolved
        // primary face. Reused while that face is unchanged so the per-build SetAtlas is a no-op
        // (an atlas-reference flip clears TextRunSnapshotCache, forcing every text run to
        // re-shape — catastrophic for editor panels that rebuild the whole document each frame).
        static TmpFontAssetSource cachedDispatcherSource;
        static FamilyDispatchingGlyphAtlas cachedDispatcher;
        static TmpFontMetrics cachedTmpMetrics;
        static bool tmpRegisteredWarningEmitted;
        static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<uint>> atgAdvanceAttempts = new();
        static readonly System.Text.StringBuilder atgAdvanceScratch = new(2);
        const string BasicAsciiCharacters =
            " !\"#$%&'()*+,-./0123456789:;<=>?@" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
            "abcdefghijklmnopqrstuvwxyz{|}~" +
            // U+00A0 NO-BREAK SPACE. Authors use &nbsp; constantly (e.g.
            // "Input&nbsp;Test"). It was NOT in the prepopulated set, so a run
            // containing it forced an on-demand TryAddCharacters at first shape,
            // which can repack the ATG atlas mid-GenerateText and intermittently
            // return zero glyphs — the run then silently falls to the SDF/
            // FontEngine fallback face (a DIFFERENT font from the ATG primary),
            // so e.g. the inputtest title randomly rendered in the bundled
            // fallback font between runs. Pre-rasterizing nbsp keeps the atlas
            // stable so nbsp-bearing runs shape through ATG deterministically.
            " ";
        const string LatinTextCharacters =
            "ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞß" +
            "àáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿ" +
            "ĀāĂăĄąĆćĈĉĊċČčĎďĐđĒēĔĕĖėĘęĚěĞğİıŁłŃńŇňŒœŚśŠšŪūŮůŹźŻżŽž";
        // U+2010–U+203A General Punctuation that real copy uses constantly —
        // curly quotes (the ’ in "you’d"), en/em dashes, ellipsis, bullet,
        // daggers, primes, per-mille, single guillemets — plus U+2212 minus.
        // Exactly the U+00A0 story above: these were NOT prepopulated, so the
        // first run containing one forced an on-demand TryAddCharacters that
        // can repack the ATG atlas mid-GenerateText and intermittently return
        // zero glyphs; the run then silently fell to the SDF/TMP fallback face
        // whose advances differ from the layout metrics — the whole line
        // rendered with overlapping/crammed glyphs (weva-landing "Everything
        // you’d reach for on the web — in your game." garbled on cold start).
        const string TypographicPunctuationCharacters =
            "‐‑‒–—―" + // hyphens & dashes ‐‑‒–—―
            "‘’‚‛“”„‟" + // quotes ‘’‚‛“”„‟
            "†‡•…‰′″‹›" + // †‡•…‰′″‹›
            "−"; // minus sign −
        const string RequiredLatinTextCharacters = "Åå";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init() {
            atgAdvanceAttempts.Clear();
            atgAdvanceScratch.Clear();
            UIDocumentDefaults.FontMetricsFactory = PickBest;
            EnsurePackageDefaultRegistered();
#if !UNITY_EDITOR
            // Editor has its own AutoRegisterTmpFontsInEditor that finds every
            // TMP_FontAsset via AssetDatabase and deprioritises TMP's bundled
            // LiberationSans default. Running this player-side path inside the
            // Editor would race with that, registering LiberationSans before
            // the editor's deferred pick lands and leaving WevaDocument.OnEnable
            // to load the wrong asset. Gate to player builds only.
            AutoRegisterTmpFontsAtRuntime();
#endif
        }

        // Logs once-per-session when no TMP_FontAsset is registered for any of
        // the generic families AND TMP_Settings.defaultFontAsset is missing.
        // Returns the new value of the `emitted` latch (true once logged).
        static bool LogTmpMissingWarning(bool emitted) {
            if (emitted) return true;
            string defaultName = "<unset>";
            try {
                var def = TMPro.TMP_Settings.defaultFontAsset;
                if (def != null) defaultName = def.name;
            } catch { /* TMP_Settings not present is fine — we already failed */ }
            Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                "No TMP_FontAsset registered for sans-serif/serif/monospace/system-ui — " +
                "text will render through the raw SDF path without ATG hinted-bitmap " +
                "rasterization. Small body text typically looks fuzzy or overlapping. " +
                "TMP_Settings.defaultFontAsset = " + defaultName + ". " +
                "Fix: assign a TMP_FontAsset under Project Settings → TextMeshPro → " +
                "Default Font Asset (it must also be referenced from a scene/prefab or " +
                "appear in Preloaded Assets so the build doesn't strip it), OR call " +
                "TmpFontAssetRegistry.RegisterFontAsset(\"sans-serif\", asset) before " +
                "the first WevaDocument loads.");
            return true;
        }

        // Runtime auto-registration. Editor uses AssetDatabase.FindAssets (above)
        // to discover every TMP_FontAsset in the project; the player has no such
        // database, so we look at TMP_Settings.defaultFontAsset and the project's
        // fallbackFontAssetTable — the standard TMP places the project author
        // configures via Project Settings → TextMeshPro → Default Font Asset.
        //
        // Without this, PickBest() calls TryCreateTmp() which immediately returns
        // null (no registered TMP asset), the bootstrap falls through to
        // TryCreateSdf() → raw SdfGlyphAtlasAdapter, and the ATG hinted-bitmap
        // wrap is never even attempted. Small body text then renders through the
        // SDFAA atlas at heavy downscale with no hinting → visibly fuzzy /
        // overlapping characters in the player. The Editor doesn't show this
        // because AutoRegisterTmpFontsInEditor wires SegoeUI (or whatever) into
        // the registry, TryCreateTmp succeeds, and the ATG wrap kicks in.
        //
        // Idempotent: TmpFontAssetRegistry.RegisterFontAsset replaces by family
        // name, so a project that also wires its own asset later still wins.
        // Caveat: TMP_Settings.defaultFontAsset has to actually ship in the build,
        // which usually means it's referenced from a scene/prefab or from a
        // [Preloaded Assets] slot. A TMP asset that exists only under Assets/UI
        // without any inbound reference is stripped at build time and arrives
        // as null here — same null we'd see if the project hadn't configured
        // TMP defaults at all.
        static void AutoRegisterTmpFontsAtRuntime() {
            try {
                var settings = TMPro.TMP_Settings.instance;
                if (settings == null) return;
                var primary = TMPro.TMP_Settings.defaultFontAsset;
                if (primary == null) return;
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("sans-serif", primary);
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("system-ui", primary);
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("Arial", primary);
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("Inter", primary);
                // Unify font resolution (player): same bridge as the editor path —
                // the SDF fallback resolves the same Font the ATG primary uses, so
                // unstyled text doesn't flap face when a glyph misses the ATG atlas.
                // Uses the Font object (sourceFontFile has no asset path in a build).
                if (primary.sourceFontFile != null) {
                    UnityFontLoader.RegisterFamilyFontOverride("sans-serif", primary.sourceFontFile);
                    UnityFontLoader.RegisterFamilyFontOverride("system-ui", primary.sourceFontFile);
                }
                var fallbackTable = TMPro.TMP_Settings.fallbackFontAssets;
                if (fallbackTable != null) {
                    for (int i = 0; i < fallbackTable.Count; i++) {
                        var fb = fallbackTable[i];
                        if (fb == null || fb == primary) continue;
                        Tmp.TmpFontAssetRegistry.AddFallback("sans-serif", fb);
                        Tmp.TmpFontAssetRegistry.AddFallback("system-ui", fb);
                    }
                }
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                    "AutoRegisterTmpFontsAtRuntime threw: " + ex.GetType().Name + ": " + ex.Message
                    + ". Player text will fall through to the raw SDF path (small text may look fuzzy).");
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void EditorInit() {
            Init();
            // Auto-register any TMP_FontAsset found in the project so that
            // edit-mode rendering has a font available without waiting for
            // a play-mode script (UitestController etc.) to register one.
            // Every domain reload otherwise wipes TmpFontAssetRegistry,
            // dropping us back to the GUI fallback and "no text on screen".
            // Idempotent: TmpFontAssetRegistry replaces by family name.
            UnityEditor.EditorApplication.delayCall += AutoRegisterTmpFontsInEditor;
        }

        // Public re-entry point for callers that observed an empty
        // TmpFontAssetRegistry (e.g. WevaDocument.OnEnable after a test run
        // that cleared the registry in TearDown). Idempotent — the
        // underlying registry replaces by family name. Safe to call from
        // any editor frame.
        public static void EnsureFontsRegisteredInEditor() {
            AutoRegisterTmpFontsInEditor();
        }

        static void AutoRegisterTmpFontsInEditor() {
            try {
                var guids = UnityEditor.AssetDatabase.FindAssets("t:TMP_FontAsset");
                if (guids == null || guids.Length == 0) return;
                // Prefer the first SDF asset we find. Sort priorities (low to
                // high penalty wins):
                //   1. Assets/ before Packages/ — user-owned beats package.
                //   2. Non-LiberationSans before LiberationSans — TMP ships
                //      LiberationSans as the Essential Resources default,
                //      which has Static atlas mode (cannot add glyphs at
                //      runtime) and only Latin coverage. If the user has
                //      added their own font (Segoe UI, Inter, etc.), prefer
                //      that one even though it sorts after "TextMesh Pro/"
                //      alphabetically. Without this guard the primary HUD
                //      font lacks every author glyph beyond ASCII (✦ ⚔ ☥
                //      ☠ ❄ ✚ ◯ ◈ all miss).
                //   3. Non-Emoji before Emoji — emoji atlases are wide on
                //      color glyphs but missing on alphanumerics.
                //   4. Alphabetical, as a stable tiebreaker.
                System.Array.Sort(guids, (a, b) => {
                    string pa = UnityEditor.AssetDatabase.GUIDToAssetPath(a);
                    string pb = UnityEditor.AssetDatabase.GUIDToAssetPath(b);
                    bool aPkg = pa.StartsWith("Packages/", System.StringComparison.Ordinal);
                    bool bPkg = pb.StartsWith("Packages/", System.StringComparison.Ordinal);
                    if (aPkg != bPkg) return aPkg ? 1 : -1;
                    bool aLib = pa.IndexOf("LiberationSans", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    bool bLib = pb.IndexOf("LiberationSans", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (aLib != bLib) return aLib ? 1 : -1;
                    bool aEmoji = pa.IndexOf("Emoji", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    bool bEmoji = pb.IndexOf("Emoji", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (aEmoji != bEmoji) return aEmoji ? 1 : -1;
                    return string.Compare(pa, pb, System.StringComparison.Ordinal);
                });
                var primary = UnityEditor.AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
                primary = EnsureEditorPrimaryHasLatinCoverage(primary);
                if (primary == null) return;
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("sans-serif", primary);
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("system-ui", primary);
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("Arial", primary);
                Tmp.TmpFontAssetRegistry.RegisterFontAsset("Inter", primary);
                // Unify font resolution: bridge the ATG primary's source font to
                // the SDF loader for the generic families, so the in-path SDF
                // fallback rasterizes the SAME face the ATG primary uses (kills
                // the font-flap for unstyled text). Generics only — named families
                // resolve normally.
                if (primary.sourceFontFile != null) {
                    UnityFontLoader.RegisterFamilyFontOverride("sans-serif", primary.sourceFontFile);
                    UnityFontLoader.RegisterFamilyFontOverride("system-ui", primary.sourceFontFile);
                }

                var pendingFallbackFamilies = new System.Collections.Generic.List<string>();
                // Register non-primary, non-emoji TMP fonts as text fallbacks.
                // Static TMP assets can't add glyphs at runtime, so we create
                // a dynamic clone from the source TTF when needed. This ensures
                // accented Latin (å, é, ü, etc.) resolves from a fallback font
                // when the primary (e.g. LilitaOne) doesn't cover it.
                // Sort fallback candidates: prefer Regular/Medium weight over
                // Bold/Display so the fallback glyph blends with body text.
                var fallbackGuids = new System.Collections.Generic.List<string>();
                for (int gi = 1; gi < guids.Length; gi++) fallbackGuids.Add(guids[gi]);
                fallbackGuids.Sort((a, b) => {
                    string pa = UnityEditor.AssetDatabase.GUIDToAssetPath(a);
                    string pb = UnityEditor.AssetDatabase.GUIDToAssetPath(b);
                    bool aBold = pa.IndexOf("Bold", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    bool bBold = pb.IndexOf("Bold", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (aBold != bBold) return aBold ? 1 : -1;
                    return string.Compare(pa, pb, System.StringComparison.Ordinal);
                });
                for (int gi = 0; gi < fallbackGuids.Count; gi++) {
                    string fallbackPath = UnityEditor.AssetDatabase.GUIDToAssetPath(fallbackGuids[gi]);
                    if (fallbackPath.IndexOf("Emoji", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (fallbackPath.IndexOf("LiberationSans", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var fallbackAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(fallbackPath);
                    if (fallbackAsset == null || fallbackAsset == primary) continue;
                    var usable = EnsureDynamicWithLatin(fallbackAsset);
                    if (usable == null) continue;
                    Tmp.TmpFontAssetRegistry.AddFallback("sans-serif", usable);
                    Tmp.TmpFontAssetRegistry.AddFallback("system-ui", usable);
                    // Also register the font family in FontResolver so the SDF
                    // paint path's CharacterFallback chain can find it. The
                    // measurement path uses TmpFontAssetRegistry fallbacks, but
                    // the paint path resolves faces via FontResolver → FontEngine.
                    var usableSource = usable.sourceFontFile;
                    if (usableSource != null) {
                        string familyName = usableSource.name;
                        string ttfPath = UnityEditor.AssetDatabase.GetAssetPath(usableSource);
                        if (!string.IsNullOrEmpty(ttfPath)) {
                            TextCore.FontResolver.RegisterFont(familyName, ttfPath);
                            pendingFallbackFamilies.Add(familyName);
                        }
                    }
                }

                // Wire emoji fonts into the family's fallback chain so the SDF
                // path can resolve emoji codepoints in edit mode.
                // Optional pre-baked TMP emoji atlases (consumer-provided via the
                // AuditFonts window). No-op when absent — the ATG path above
                // already renders emoji from the bundled Noto Color Emoji font.
                TryRegisterEmojiFallback("Assets/UI/Fonts/NotoColorEmoji COLOR.asset");
                TryRegisterEmojiFallback("Assets/UI/Fonts/NotoColorEmoji SDF.asset");
                // Force-populate the TMP asset's character lookup with ASCII.
                // TMP atlases authored with `m_ClearDynamicDataOnBuild` ship
                // with only the originally-baked subset (often <20 chars). Our
                // layout pass measures advance through that lookup, so missing
                // characters measure as 0px wide — buttons shrink to just
                // padding, and ATG-painted text overflows them. Eagerly add
                // the ASCII set so every layout measurement has real metrics.
                try {
                    primary.TryAddCharacters(
                        BasicAsciiCharacters +
                        LatinTextCharacters +
                        TypographicPunctuationCharacters +
                        // Common BMP monochrome symbols routinely used in
                        // game-UI typography (HUD icons, button glyphs,
                        // bullet markers). Segoe UI / DejaVu / most system
                        // fonts cover these natively; pre-populating ensures
                        // they live in the SDF atlas before paint time so
                        // authors can drop ✦/⚔/❄/☠/etc. into HTML without
                        // a domain-reload race or a tmp-emoji-miss warning.
                        "✦✧⚔⚒⚓⚖⚙⚛⚜" +     // Misc Symbols #1
                        "☀☁☂☃☄★☆☉☎☑☒☓" +  // Misc Symbols #2
                        "☘☙☠☢☣☤☥☦☧☨☪☫☬" +  // Misc Symbols (skulls/crosses)
                        "♀♁♂♃♄♅♆♇♈♉♊♋♌♍♎♏♐♑♒♓" + // Zodiac/Planets
                        "♔♕♖♗♘♙♚♛♜♝♞♟" +  // Chess
                        "♠♡♢♣♤♥♦♧" +       // Cards
                        "♨♩♪♫♬♭♮♯" +       // Music
                        "✁✂✃✄✆✇✈✉✊✋✌✎✏✐✑✒" + // Dingbats
                        "✓✔✕✖✗✘✙✚✛✜✝✞✟✠✡" + // Crosses (✚)
                        "✢✣✤✥✦✧✩✪✫✬✭✮✯✰" + // Stars
                        "✱✲✳✴✵✶✷✸✹✺✻✼✽✾✿" +
                        "❀❁❂❃❄❅❆❇❈❉❊❋❍❏❑❒❖" + // Snowflakes (❄)
                        "❗❘❙❚❛❜❝❞❟❡❢❣❤❥❦❧" + // ❣
                        "◆◇◈◉◊○◌◍◎●◐◑◒◓◔◕◖◗◘◙◚◛" + // Geometric ◈ ◊
                        "◜◝◞◟◠◡◢◣◤◥◦◧◨◩◪◫◬◭◮◯" + // Geometric ◯
                        "▲△▴▵▶▷▸▹►▻▼▽▾▿◀◁◂◃◄◅",
                        out _, includeFontFeatures: false);
                } catch (System.Exception ex) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                        "TMP asset ASCII pre-populate failed: " + ex.Message);
                }
                // Re-run PickBest so SdfTextRendering.Atlas picks up the TMP
                // (and possibly ATG-wrapped) path immediately, not on first
                // text render.
                PickBest();
                // Now that sharedFallback exists (PickBest creates it), wire
                // the font families into the CharacterFallback chain so the
                // SDF paint path can resolve accented Latin from these fonts.
                if (sharedFallback != null) {
                    foreach (var fam in pendingFallbackFamilies) {
                        sharedFallback.AddToChain(fam);
                    }
                }
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                    "Auto-register TMP fonts (editor) failed: " + ex.Message);
            }
        }

        static void TryRegisterEmojiFallback(string path) {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(path);
            if (asset == null) return;
            // Pre-populate the lookup table with codepoints used in the scene.
            // TMP emoji atlases authored with `ClearDynamicDataOnBuild` ship
            // with very few characters (often <20). Without these in the
            // lookup, TmpFontMetrics.Measure returns 0 for emoji codepoints
            // — every `<div>🔨</div>` measures as 0px wide, the resulting
            // TextRun has empty bounds, and the paint pass drops it. The
            // emoji never reaches the shader, never renders.
            // Common emoji + symbols used in our scene. Add yours here if
            // a new emoji codepoint shows blank — the auto-register doesn't
            // walk the scene to discover them.
            try {
                string preload =
                    "\U0001F528\U0001F504\u26A1\u2605\u2606\u23F8" +
                    "\U0001F600\U0001F389\u2728\U0001F525" +
                    "\u21A9\u21AA\u25CF\u25B2\u25BC\u25A0\u25C6\u2190\u2192\u2191\u2193" +
                    "\u26A0\u2764\u2763\u2699\u2692\u2693";
                asset.TryAddCharacters(preload, out _, includeFontFeatures: false);
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                    "Emoji asset pre-populate failed: " + ex.Message);
            }
            Tmp.TmpFontAssetRegistry.AddFallback("sans-serif", asset);
            Tmp.TmpFontAssetRegistry.AddFallback("system-ui", asset);
            Tmp.TmpFontAssetRegistry.AddFallback("Arial", asset);
            Tmp.TmpFontAssetRegistry.AddFallback("Inter", asset);
        }
        static TMPro.TMP_FontAsset EnsureDynamicWithLatin(TMPro.TMP_FontAsset asset) {
            if (asset == null) return null;
            // Only attempt TryAddCharacters for Dynamic-mode assets. TMP's
            // TryAddCharacters unconditionally emits a Unity Debug.LogWarning
            // on EVERY call when the asset's AtlasPopulationMode is Static
            // ("Unable to add characters to font asset [X] because its
            // AtlasPopulationMode is set to Static"), spamming the console
            // on every domain reload while the auto-register pass scans
            // every project TMP asset. Pre-checking the mode skips straight
            // to the source-font clone path below for Static assets.
            if (asset.atlasPopulationMode == TMPro.AtlasPopulationMode.Dynamic) {
                try {
                    if (asset.TryAddCharacters(RequiredLatinTextCharacters, out string missing, false)) {
                        asset.TryAddCharacters(BasicAsciiCharacters + LatinTextCharacters + TypographicPunctuationCharacters, out _, false);
                        return asset;
                    }
                } catch (System.Exception) {
                }
            }
            // Static asset — create a dynamic clone from the source TTF.
            var sourceFont = asset.sourceFontFile;
            if (sourceFont == null) {
                // sourceFontFile is null when "Clear Dynamic Data On Build" strips
                // it. Try to find the TTF by name in the same directory as the SDF.
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(assetPath)) {
                    string dir = System.IO.Path.GetDirectoryName(assetPath);
                    // Strip " SDF" suffix to get the TTF base name.
                    string baseName = asset.name;
                    if (baseName.EndsWith(" SDF")) baseName = baseName.Substring(0, baseName.Length - 4);
                    if (baseName.EndsWith(" SDF - Fallback")) baseName = baseName.Substring(0, baseName.Length - 15);
                    string ttfPath = dir + "/" + baseName + ".ttf";
                    sourceFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
                    if (sourceFont == null) {
                        string otfPath = dir + "/" + baseName + ".otf";
                        sourceFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(otfPath);
                    }
                }
                if (sourceFont == null) return null;
            }
            try {
                var dynamic = TMPro.TMP_FontAsset.CreateFontAsset(
                    sourceFont, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                    1024, 1024,
                    TMPro.AtlasPopulationMode.Dynamic, true);
                if (dynamic == null) return null;
                dynamic.name = asset.name + " Dynamic Fallback";
                dynamic.hideFlags = HideFlags.HideAndDontSave;
                dynamic.TryAddCharacters(BasicAsciiCharacters + LatinTextCharacters + TypographicPunctuationCharacters, out _, false);
                if (!TmpFontAssetContains(dynamic, RequiredLatinTextCharacters)) {
                    UnityEngine.Object.DestroyImmediate(dynamic);
                    return null;
                }
                return dynamic;
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                    "Dynamic fallback creation failed for " + asset.name + ": " + ex.Message);
                return null;
            }
        }
#endif

        static TMPro.TMP_FontAsset editorDynamicPrimary;

        static TMPro.TMP_FontAsset EnsureEditorPrimaryHasLatinCoverage(TMPro.TMP_FontAsset primary) {
            if (primary == null) return null;
            if (TmpFontAssetContains(primary, RequiredLatinTextCharacters)) return primary;
            var sourceFont = primary.sourceFontFile;
            if (sourceFont == null) return primary;
            if (editorDynamicPrimary != null
                && editorDynamicPrimary.sourceFontFile == sourceFont
                && TmpFontAssetContains(editorDynamicPrimary, RequiredLatinTextCharacters)) {
                return editorDynamicPrimary;
            }
            try {
                var dynamicPrimary = TMPro.TMP_FontAsset.CreateFontAsset(
                    sourceFont,
                    90,
                    9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                    1024,
                    1024,
                    TMPro.AtlasPopulationMode.Dynamic,
                    true);
                if (dynamicPrimary == null) return primary;
                dynamicPrimary.name = primary.name + " Dynamic";
                dynamicPrimary.hideFlags = HideFlags.HideAndDontSave;
                dynamicPrimary.TryAddCharacters(BasicAsciiCharacters + LatinTextCharacters + TypographicPunctuationCharacters, out _, false);
                if (!TmpFontAssetContains(dynamicPrimary, RequiredLatinTextCharacters)) {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(dynamicPrimary);
                    else UnityEngine.Object.DestroyImmediate(dynamicPrimary);
                    return primary;
                }
                editorDynamicPrimary = dynamicPrimary;
                return dynamicPrimary;
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                    "TMP dynamic Latin primary creation failed: " + ex.Message);
                return primary;
            }
        }

        static bool TmpFontAssetContains(TMPro.TMP_FontAsset asset, string text) {
            if (asset == null || string.IsNullOrEmpty(text)) return false;
            var lookup = asset.characterLookupTable;
            if (lookup == null) return false;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                uint cp;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, text[++i]);
                } else {
                    cp = c;
                }
                if (!lookup.ContainsKey(cp)) return false;
            }
            return true;
        }

        public static IFontMetrics PickBest() {
            // If the author has registered a TMP_FontAsset for the default
            // family, prefer it: TMP atlases ship with kerning + tuned SDF
            // baking that beat our runtime FontEngine rasterizer. The TMP
            // path leans on the same SDF rendering machinery downstream
            // (SdfTextRunBaker / SdfGlyphAtlasAdapter) — only the glyph data
            // source changes. The TMP path is ALSO the only entry that
            // wraps the SDF adapter with ATG (hinted-bitmap "Chrome-quality"
            // small text) — drop into the raw SDF path below and small body
            // text loses hinting + sharper rasterization, which is the
            // single most common cause of "Editor looks fine, builds look
            // fuzzy" reports.
            try {
                var tmp = TryCreateTmp();
                if (tmp != null) return tmp;
                // One-shot diagnostic: no TMP_FontAsset registered for any of
                // the generic families. In the Editor, AutoRegisterTmpFontsInEditor
                // wires this up via AssetDatabase. In the player, the project
                // must either ship a TMP_Settings.defaultFontAsset (which
                // AutoRegisterTmpFontsAtRuntime picks up) or call
                // TmpFontAssetRegistry.RegisterFontAsset manually before the
                // first WevaDocument loads.
                tmpRegisteredWarningEmitted = LogTmpMissingWarning(tmpRegisteredWarningEmitted);
            } catch (System.Exception ex) {
                Debug.LogWarning("Weva: TmpFontMetrics failed; falling back to SdfFontMetrics. " + ex.Message);
            }
            // Prefer SdfFontMetrics in any context where the FontEngine works
            // (Player or Editor; Editor includes Edit Mode because the FontEngine
            // is initialized lazily on first call). If the backend can't load
            // the default face we fall through to the GUI / Mono paths.
            try {
                var sdf = TryCreateSdf();
                if (sdf != null) return sdf;
            } catch (System.Exception ex) {
                Debug.LogWarning("Weva: SdfFontMetrics failed; trying UnityGUIFontMetrics. " + ex.Message);
            }
            try {
                return new UnityGUIFontMetrics();
            } catch (System.Exception ex) {
                Debug.LogWarning("Weva: UnityGUIFontMetrics failed; falling back to MonoFontMetrics. " + ex.Message);
            }
            return new MonoFontMetrics();
        }

        static SdfFontMetrics TryCreateSdf() {
            EnsurePackageDefaultRegistered();
            sharedBackend ??= new UnityFontEngineBackend();
            sharedAtlas ??= new GlyphAtlas();
            sharedLoader ??= new FontLoader(new UnityFontLoader(), sharedBackend);
            var sdf = new SdfFontMetrics(sharedLoader, sharedBackend, sharedAtlas).WithKernProvider(KernFromBackend);
            // Probe: if we cannot resolve a default face, signal failure so the
            // bootstrap can fall through to GUI metrics rather than returning a
            // never-loaded SdfFontMetrics that quietly measures everything to 0.
            var face = sdf.FaceFor("sans-serif", Weva.Paint.FontStyle.Normal, 400);
            if (!face.IsValid) return null;
            // Build the shared fallback chain on first success. Subsequent
            // SdfFontMetrics instances reuse the same probe via SharedFallback.
            sharedFallback ??= new CharacterFallback(new BackendGlyphProbe(sharedBackend));
            // Lazy-create the SDF rasterizer + IGlyphAtlas adapter. Wires
            // SdfTextRendering.Atlas so the URP backend's DrawText path produces
            // SDF glyph quads instead of placeholder rects. The adapter keeps a
            // shared SdfTextRunBaker that re-fills its scratch result per call.
            sharedRasterizer ??= new SdfGlyphRasterizer(sharedBackend);
            // Wire the rasterizer into the backend so the existing GlyphAtlas
            // RequestGlyph path (which calls backend.RasterizeGlyph) gets real
            // SDF bytes instead of the legacy zero-filled stub.
            if (sharedBackend.RasterizerHook == null) {
                var rasterizer = sharedRasterizer;
                sharedBackend.RasterizerHook = (face, codepoint, fontSize) => {
                    if (rasterizer.TryRasterizeAsRasterizedGlyph(face, codepoint, fontSize, out var raster)) {
                        return raster;
                    }
                    return null;
                };
            }
            var baker = new SdfTextRunBaker(sdf) { Fallback = sharedFallback };
            // Clear any stale TMP routing so a previous TMP-backed adapter
            // doesn't leak into this all-SDF path. (Tests that register then
            // unregister TMP_FontAssets exercise this seam.)
            sharedAdapter = new SdfGlyphAtlasAdapter(baker, sdf, sharedRasterizer) {
                TmpSource = null,
                TmpAtlas = null,
                TmpFace = FaceInfo.Empty
            };
            Weva.Rendering.URP.SdfTextRendering.SetAtlas(sharedAdapter);
            return sdf;
        }

        // TMP-backed metrics. Returns null when no TMP_FontAsset is registered
        // under the default family — the bootstrap then falls through to the
        // FontEngine SDF path. Otherwise, we set up the same downstream wiring
        // (rasterizer hook for missing glyphs, IGlyphAtlas adapter, AtlasRegistry
        // entry) but with the TMP atlas as the primary source of glyph bytes.
        static TmpFontMetrics TryCreateTmp() {
            // Look up under the default family. The brief calls out probing
            // multiple generic families in priority order; for v1 we only need
            // the first registered match to opt the doc into the TMP path.
            string[] families = { "sans-serif", "serif", "monospace", "system-ui" };
            TmpFontAssetSource source = null;
            string resolvedFamily = null;
            for (int i = 0; i < families.Length; i++) {
                if (TmpFontAssetRegistry.TryGet(families[i], out source) && source != null) {
                    resolvedFamily = families[i];
                    break;
                }
                source = null;
            }
            if (source == null) source = TmpFontAssetRegistry.Any();
            if (source == null || source.Asset == null) return null;

            // Fast path: the registered primary face is the same instance as last build, so the
            // already-built dispatcher is still valid. Re-installing it (SetAtlas with the SAME
            // reference) leaves TextRunSnapshotCache intact, so unchanged text runs replay their
            // shaped quads instead of re-shaping. (Re-registering a font yields a new
            // TmpFontAssetSource, which fails this ReferenceEquals and rebuilds — see
            // TmpFontAssetRegistry.RegisterFontAsset.)
            if (ReferenceEquals(source, cachedDispatcherSource) && cachedDispatcher != null && cachedTmpMetrics != null) {
                defaultTmpSource = source;
                Weva.Documents.UIDocumentDefaults.FamilyMetricsResolver = ResolveFamilyMetrics;
                Weva.Rendering.URP.SdfTextRendering.SetAtlas(cachedDispatcher);
                return cachedTmpMetrics;
            }

            // Register the bundled TTF with FontResolver so the FontEngine
            // fallback (used for chars TMP's atlas doesn't cover, e.g. ·, •,
            // emoji) can resolve a face and rasterize on-demand. Without
            // this, partial-coverage runs roll back to a font-less fallback
            // and EmitFallback paints monospace placeholder boxes.
            EnsurePackageDefaultRegistered();

            var metrics = new TmpFontMetrics(source);
            // Plumb the fallback chain so layout-time glyph metrics walk past
            // the primary face for codepoints (e.g. emoji) the primary atlas
            // doesn't contain. Without this, Measure() returns 0 advance for
            // emoji and the paint pass discards the run at the Bounds.IsEmpty
            // gate, leaving an empty rectangle where the glyph should be.
            if (resolvedFamily != null) {
                metrics.Fallbacks = TmpFontAssetRegistry.GetChain(resolvedFamily);
            }
            // Ensure the TMP face is registered against AtlasRegistry under a
            // fresh atlasId so UIRenderGraphPass.IssueBatch can bind the TMP
            // atlas texture via AtlasRegistry.GetTextureById. We wrap the TMP
            // Texture2D in a GlyphAtlas shell whose TextureOverride exposes
            // the TMP atlas to the existing GetTextureById path. (The shell
            // owns no shelves; we only use it as a texture-lookup vehicle.)
            sharedTmpAtlas ??= new GlyphAtlas();
            sharedTmpAtlas.TextureOverride = source.Atlas;
            AtlasRegistry.RegisterAtlas(metrics.Face, sharedTmpAtlas);

            // Wire the IGlyphAtlas adapter so the URP DrawText path produces
            // SDF glyph quads sourced from TMP. We reuse the existing
            // SdfGlyphAtlasAdapter but route shaping through the TMP source —
            // see SdfGlyphAtlasAdapter.TmpSource.
            sharedBackend ??= new UnityFontEngineBackend();
            sharedAtlas ??= new GlyphAtlas();
            sharedRasterizer ??= new SdfGlyphRasterizer(sharedBackend);
            // Wire the rasterizer hook on the backend so missing TMP glyphs
            // fall through to the FontEngine path (same codepath the all-SDF
            // bootstrap uses). Idempotent — checks whether a hook is set.
            if (sharedBackend.RasterizerHook == null) {
                var rasterizer = sharedRasterizer;
                sharedBackend.RasterizerHook = (face, codepoint, fontSize) => {
                    if (rasterizer.TryRasterizeAsRasterizedGlyph(face, codepoint, fontSize, out var raster)) {
                        return raster;
                    }
                    return null;
                };
            }
            // Build a baker that drives the underlying SdfFontMetrics for
            // missing-glyph fallback (TMP atlases don't cover every codepoint).
            sharedLoader ??= new FontLoader(new UnityFontLoader(), sharedBackend);
            var fallbackSdf = new SdfFontMetrics(sharedLoader, sharedBackend, sharedAtlas).WithKernProvider(KernFromBackend);
            var baker = new SdfTextRunBaker(fallbackSdf);
            sharedAdapter = new SdfGlyphAtlasAdapter(baker, fallbackSdf, sharedRasterizer) {
                TmpSource = source,
                TmpFace = metrics.Face,
                TmpAtlas = sharedTmpAtlas,
                // Plumb the registered fallback chain through. When no fallbacks
                // are registered, GetChain returns a 1-element list containing
                // the primary — the adapter's chain-walk short-circuits at
                // length<=1 so behaviour is identical to single-face shaping.
                TmpChain = resolvedFamily != null
                    ? TmpFontAssetRegistry.GetChain(resolvedFamily)
                    : null
            };
            // Per-element font-family at LAYOUT time: map a family to a
            // TmpFontMetrics bound to its registered face, so a Sniglet run is
            // measured with Sniglet advances (matching the paint dispatcher).
            // Without this, layout uses the global default's advances while
            // paint draws the per-family face — misaligned glyphs.
            familyMetricsCache.Clear();
            defaultTmpSource = source;
            Weva.Documents.UIDocumentDefaults.FamilyMetricsResolver = ResolveFamilyMetrics;

            // Per-element font-family at PAINT time: wrap the default (ATG-
            // wrapped) adapter in a dispatcher that routes each run to a
            // per-family adapter built from its registered TMP face. Generics /
            // the default face / unregistered families fall through to the
            // default, so single-font docs are unchanged.
            var defaultAdapter = TryWrapWithAtg(sharedAdapter, source);
            var dispatcher = new FamilyDispatchingGlyphAtlas(defaultAdapter, source, BuildTmpAdapterForSource);
            // Cache for reuse on the next build while this face stays registered (see fast path).
            cachedDispatcherSource = source;
            cachedDispatcher = dispatcher;
            cachedTmpMetrics = metrics;
            Weva.Rendering.URP.SdfTextRendering.SetAtlas(dispatcher);
            return metrics;
        }

        // Builds a standalone SDF/TMP adapter for one registered face, with its
        // own atlas shell (distinct atlasId so the batcher binds the right
        // texture). Used by FamilyDispatchingGlyphAtlas for non-default
        // families. Reuses the shared FontEngine backend / fallback atlas /
        // rasterizer / loader set up by TryCreateTmp.
        static Weva.Rendering.URP.IGlyphAtlasWithId BuildTmpAdapterForSource(
            TmpFontAssetSource src, System.Collections.Generic.IReadOnlyList<TmpFontAssetSource> chain) {
            if (src == null || src.Asset == null) return null;
            sharedBackend ??= new UnityFontEngineBackend();
            sharedAtlas ??= new GlyphAtlas();
            sharedRasterizer ??= new SdfGlyphRasterizer(sharedBackend);
            if (sharedBackend.RasterizerHook == null) {
                var rasterizer = sharedRasterizer;
                sharedBackend.RasterizerHook = (face, cp, fs) =>
                    rasterizer.TryRasterizeAsRasterizedGlyph(face, cp, fs, out var raster) ? raster : null;
            }
            sharedLoader ??= new FontLoader(new UnityFontLoader(), sharedBackend);

            var faceInfo = src.Face;
            var tmpAtlasShell = new GlyphAtlas { TextureOverride = src.Atlas };
            AtlasRegistry.RegisterAtlas(faceInfo, tmpAtlasShell);

            var fallbackSdf = new SdfFontMetrics(sharedLoader, sharedBackend, sharedAtlas).WithKernProvider(KernFromBackend);
            var baker = new SdfTextRunBaker(fallbackSdf);
            return new SdfGlyphAtlasAdapter(baker, fallbackSdf, sharedRasterizer) {
                TmpSource = src,
                TmpFace = faceInfo,
                TmpAtlas = tmpAtlasShell,
                TmpChain = chain
            };
        }

        // Kern provider wired onto every SDF/FontEngine SdfFontMetrics so both
        // layout (MeasureText) and paint (SdfTextRunBaker) pick up the font's
        // pair kerning — GetKern was a 0-stub. Reads pair adjustments from the
        // shared FontEngine backend; returns 0 when unavailable (headless tests
        // use StubBackend and never install this).
        static double KernFromBackend(FaceInfo face, uint left, uint right, double fontSize) {
            var be = sharedBackend;
            return be != null && be.TryGetKernAdvance(face, left, right, fontSize, out double k) ? k : 0;
        }

        static readonly System.Collections.Generic.Dictionary<string, TmpFontMetrics> familyMetricsCache =
            new(System.StringComparer.OrdinalIgnoreCase);

        // Resolver for UIDocumentDefaults.FamilyMetricsResolver. Returns a
        // TmpFontMetrics for a family with a registered TMP face, else null
        // (caller falls through to the default metrics).
        static TmpFontAssetSource defaultTmpSource;

        static IFontMetrics ResolveFamilyMetrics(string family) {
            if (string.IsNullOrEmpty(family)) return null;
            if (familyMetricsCache.TryGetValue(family, out var cached)) return cached;
            if (!TmpFontAssetRegistry.TryGet(family, out var src) || src == null || src.Asset == null) {
                return null;
            }
            // Families that map to the DEFAULT face must fall through to the
            // document's DefaultFontMetrics (the global instance) — returning a
            // fresh TmpFontMetrics here would subtly diverge its measurements
            // from the default and shift layout for every plain run. Only build a
            // per-family metrics for genuinely-different faces (Sniglet, …).
            if (defaultTmpSource != null && (ReferenceEquals(src, defaultTmpSource) || src.Asset == defaultTmpSource.Asset)) {
                familyMetricsCache[family] = null;
                return null;
            }
            var m = new TmpFontMetrics(src) { Fallbacks = TmpFontAssetRegistry.GetChain(family) };
            familyMetricsCache[family] = m;
            return m;
        }

#if UNITY_2023_1_OR_NEWER
        // Wraps an SDF adapter with the ATG (Advanced Text Generator) primary
        // path. ATG produces hinted-bitmap glyphs ("Chrome-quality" small UI
        // text); SDF stays as the fallback for blur / transforms / when ATG
        // bindings fail. Returns the SDF adapter unchanged when ATG isn't
        // available on this Unity version, so the rest of the bootstrap
        // doesn't have to special-case the configuration.
        static Weva.Rendering.URP.IGlyphAtlasWithId TryWrapWithAtg(
            Weva.Rendering.URP.IGlyphAtlasWithId sdfAdapter,
            TmpFontAssetSource source) {
            if (!Weva.Text.Atg.AtgGlyphAtlasAdapter.IsAvailable) return sdfAdapter;
            if (source == null || source.Asset == null) return sdfAdapter;
            var sourceFont = source.Asset.sourceFontFile;
            if (sourceFont == null) return sdfAdapter;
            // Reuse a previously-built ATG asset if we still have one alive.
            // Unity's GC otherwise unloads unsaved FontAssets between scenes /
            // domain reloads, leaving Atg.FontAsset as a destroyed object and
            // every shape failing with MissingReferenceException. The FontAsset
            // itself gets HideAndDontSave below, but its atlas TEXTURES are
            // separate Unity objects that can be GC'd independently — when
            // that happens, the FontAsset is "alive" but every shape produces
            // a destroyed-Texture2D exception. Treat a dead atlas texture as
            // requiring a full rebuild.
            UnityEngine.Object atgFontAsset = sharedAtgFontAsset;
            if (atgFontAsset != null) {
                var fa = atgFontAsset as UnityEngine.TextCore.Text.FontAsset;
                bool atlasAlive = fa != null && fa.atlasTextures != null
                    && fa.atlasTextures.Length > 0 && fa.atlasTextures[0] != null;
                if (!atlasAlive) {
                    sharedAtgFontAsset = null;
                    atgFontAsset = null;
                }
            }
            if (atgFontAsset == null) {
                try {
                    string sourceFamily = source.Asset.faceInfo.familyName;
                    string sourceStyle = string.IsNullOrEmpty(source.Asset.faceInfo.styleName)
                        ? "Regular"
                        : source.Asset.faceInfo.styleName;
                    if (!string.IsNullOrEmpty(sourceFamily)) {
                        atgFontAsset = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                            sourceFamily,
                            sourceStyle,
                            pointSize: 90,
                            padding: 9,
                            renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
                        var familyAsset = atgFontAsset as UnityEngine.TextCore.Text.FontAsset;
                        if (familyAsset != null && !AtgStyleMatches(familyAsset.faceInfo.styleName, sourceStyle)) {
                            DestroyAtgFontAsset(familyAsset);
                            atgFontAsset = null;
                        }
                    }
#if UNITY_EDITOR
                    string sourceFontPath = UnityEditor.AssetDatabase.GetAssetPath(sourceFont);
                    if (atgFontAsset == null && IsEditorFontFilePath(sourceFontPath)) {
                        atgFontAsset = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                            sourceFontPath,
                            faceIndex: 0,
                            samplingPointSize: 90,
                            atlasPadding: 9,
                            renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED,
                            atlasWidth: 1024,
                            atlasHeight: 1024);
                    }
#endif
                    atgFontAsset ??= UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                        sourceFont,
                        samplingPointSize: 90,
                        atlasPadding: 9,
                        renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED,
                        atlasWidth: 1024,
                        atlasHeight: 1024,
                        atlasPopulationMode: UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic,
                        enableMultiAtlasSupport: true);
                } catch (System.Exception ex) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                        "Could not create TextCore FontAsset for ATG: " + ex.Message + " — staying on SDF path");
                    return sdfAdapter;
                }
                if (atgFontAsset == null) return sdfAdapter;
                // HideAndDontSave keeps the asset alive across scene loads and
                // prevents Unity from unloading it when no scene references it
                // directly. Without this, the next domain reload / GC pass
                // destroys the asset and shape calls die with
                // MissingReferenceException → empty render → "no text".
                atgFontAsset.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
                // CreateFontAsset does NOT allocate an atlas texture upfront —
                // the first TryAddCharacters call materialises it. Force that
                // now with a basic ASCII set so the very first paint has a
                // valid atlas to sample. Without this, the renderer binds a
                // missing texture and the screen reads "no text".
                PrepopulateAtgAscii(atgFontAsset as UnityEngine.TextCore.Text.FontAsset);
                sharedAtgFontAsset = atgFontAsset;
            }
            var atgSemiboldFontAsset = GetOrCreateAtgWeightVariant(source, ref sharedAtgSemiboldFontAsset, "Semibold");
            var atgBoldFontAsset = GetOrCreateAtgWeightVariant(source, ref sharedAtgBoldFontAsset, "Bold");
            // Wire emoji FontAssets into the primary's fallback chain so ATG
            // can resolve emoji codepoints through its own shaper instead of
            // routing the whole run through SDF. Two fallbacks, in order:
            //   1. Mono SDFAA emoji: catches text-default codepoints (↩ ⏸
            //      ⚠ etc.) so they render monochrome — picks up CSS color
            //      via the standard SDF coverage path.
            //   2. Color bitmap emoji: catches everything else (🔨 🔄 🐱).
            // The per-fallback codepoint partitioning in AtgGlyphAtlasAdapter
            // ensures each fallback's character lookup only contains the
            // codepoints it should own, so the chain walk routes correctly.
            TryWireAtgMonoEmojiFallback(atgFontAsset);
            TryWireAtgEmojiFallback(atgFontAsset);
            TryWireAtgMonoEmojiFallback(atgSemiboldFontAsset);
            TryWireAtgEmojiFallback(atgSemiboldFontAsset);
            TryWireAtgMonoEmojiFallback(atgBoldFontAsset);
            TryWireAtgEmojiFallback(atgBoldFontAsset);
            // Install the layout-time advance fall-through. TMP's emoji
            // assets (`SegoeUIEmoji COLOR.asset` etc.) ship with limited
            // baked glyph coverage and the dynamic-rasterizer path for
            // color-only emoji glyphs fails for many codepoints. ATG's
            // TextCore code path handles those — we consult ATG when the
            // TMP chain can't measure a codepoint, so layout produces real
            // widths instead of 0 (which drops the run before paint).
            Weva.Text.Tmp.TmpFontMetrics.ExternalAdvanceProvider = AdvanceFromAtgFontAssets;
            // Force text-default codepoints (↩ ⏸ ⚠) past TMP's chain so
            // the measured advance comes from the same mono Symbol asset
            // the ATG renderer rasterizes from. Otherwise TMP returns
            // Segoe UI Emoji COLOR's wider advance, sizing the layout cell
            // around a glyph that's drawn ~17px narrower — the visible
            // glyph hugs one side of an oversized cell.
            Weva.Text.Tmp.TmpFontMetrics.ExternalAdvancePreferred =
                Weva.Text.Atg.AtgPrimaryFallbackAdapter.IsTextDefaultEmoji;
            var atg = new Weva.Text.Atg.AtgGlyphAtlasAdapter {
                FontAsset = atgFontAsset,
                SemiboldFontAsset = atgSemiboldFontAsset,
                BoldFontAsset = atgBoldFontAsset,
                EnableSmallTextCoverage = true,
                SmallTextCoverageMaxSize = 20
            };
            return new Weva.Text.Atg.AtgPrimaryFallbackAdapter {
                Atg = atg,
                SdfFallback = sdfAdapter
            };
        }
#else
        static Weva.Rendering.URP.IGlyphAtlasWithId TryWrapWithAtg(
            Weva.Rendering.URP.IGlyphAtlasWithId sdfAdapter, TmpFontAssetSource source) {
            return sdfAdapter;
        }
#endif

        static GlyphAtlas sharedTmpAtlas;

#if UNITY_2023_1_OR_NEWER
        // Persistent across reloads so ATG doesn't lose its rasterized atlas
        // every time PickBest runs. See TryWrapWithAtg.
        static UnityEngine.Object sharedAtgFontAsset;
        static UnityEngine.Object sharedAtgSemiboldFontAsset;
        static UnityEngine.Object sharedAtgBoldFontAsset;
        // Parallel: emoji fallback FontAsset (color-rendered) wired into the
        // primary's fallbackFontAssetTable so ATG resolves emoji codepoints
        // through its own shaper instead of falling out to the SDF path.
        static UnityEngine.Object sharedAtgEmojiFontAsset;
        // Mono SDF emoji fallback — covers text-default codepoints (↩ ⏸ ⚠)
        // that should render monochrome with CSS color rather than as the
        // platform's filled-button color emoji. Wired BEFORE the color
        // fallback so codepoints present in both atlases prefer the mono
        // outline form. See TryWireAtgMonoEmojiFallback.
        static UnityEngine.Object sharedAtgMonoEmojiFontAsset;

        static UnityEngine.Object GetOrCreateAtgWeightVariant(
            TmpFontAssetSource source,
            ref UnityEngine.Object slot,
            string styleName) {
            var alive = slot as UnityEngine.TextCore.Text.FontAsset;
            if (AtgFontAssetAlive(alive)) return slot;
            slot = null;
            string family = source?.Asset != null ? source.Asset.faceInfo.familyName : null;
            if (string.IsNullOrEmpty(family)) return null;
            UnityEngine.TextCore.Text.FontAsset fa = null;
            try {
                fa = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                    family,
                    styleName,
                    pointSize: 90,
                    padding: 9,
                    renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            } catch {
                fa = null;
            }
            if (fa == null) return null;
            if (!AtgStyleMatches(fa.faceInfo.styleName, styleName)) {
                if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(fa);
                else UnityEngine.Object.DestroyImmediate(fa);
                return null;
            }
            fa.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            PrepopulateAtgAscii(fa);
            slot = fa;
            return slot;
        }

        static bool AtgFontAssetAlive(UnityEngine.TextCore.Text.FontAsset fa) {
            try {
                return fa != null
                    && fa.atlasTextures != null
                    && fa.atlasTextures.Length > 0
                    && fa.atlasTextures[0] != null;
            } catch {
                return false;
            }
        }

        static void DestroyAtgFontAsset(UnityEngine.TextCore.Text.FontAsset fa) {
            if (fa == null) return;
            if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(fa);
            else UnityEngine.Object.DestroyImmediate(fa);
        }

        static bool IsEditorFontFilePath(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path);
            return ext.Equals(".ttf", System.StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".otf", System.StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ttc", System.StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".otc", System.StringComparison.OrdinalIgnoreCase);
        }

        static bool AtgStyleMatches(string actual, string requested) {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(requested)) return false;
            if (requested.Equals("Regular", System.StringComparison.OrdinalIgnoreCase)) {
                return actual.IndexOf("Regular", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || actual.IndexOf("Normal", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || actual.IndexOf("Book", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || actual.IndexOf("Roman", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return actual.IndexOf(requested, System.StringComparison.OrdinalIgnoreCase) >= 0
                || requested.IndexOf(actual, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void PrepopulateAtgAscii(UnityEngine.TextCore.Text.FontAsset fa) {
            if (fa == null) return;
            try {
                fa.TryAddCharacters(
                    BasicAsciiCharacters +
                    LatinTextCharacters +
                    TypographicPunctuationCharacters,
                    out _, false);
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                    "ATG atlas pre-populate failed: " + ex.Message);
            }
        }

        // Resolves a codepoint's advance via ATG font assets when TMP can't.
        // Order matches the runtime chain walk in AtgGlyphAtlasAdapter so
        // layout reserves the same cell width that the renderer eventually
        // fills:
        //   1. Mono symbol asset — covers text-default codepoints (↩ ⏸ ⚠)
        //      AND symbol-only codepoints (★ U+2605 — not classified as
        //      Emoji by Unicode but only present in Segoe UI Symbol). The
        //      ATG chain walk tries this fallback first, so layout must
        //      agree to keep cell width aligned with rendered glyph width.
        //   2. Color emoji asset — emoji-default codepoints (🔨 🐱).
        //   3. Primary text asset — covers Latin / CJK that TMP somehow lacks.
        // Lazy-rasterizes via TryAddCharacters so the lookup is populated on
        // first use; subsequent calls hit the cached lookup.
        static double AdvanceFromAtgFontAssets(uint codepoint, double fontSize) {
            var mono = sharedAtgMonoEmojiFontAsset as UnityEngine.TextCore.Text.FontAsset;
            if (TryGetAtgAdvance(mono, codepoint, fontSize, out double monoAdvance)) return monoAdvance;
            var emoji = sharedAtgEmojiFontAsset as UnityEngine.TextCore.Text.FontAsset;
            if (TryGetAtgAdvance(emoji, codepoint, fontSize, out double emojiAdvance)) return emojiAdvance;
            var primary = sharedAtgFontAsset as UnityEngine.TextCore.Text.FontAsset;
            if (TryGetAtgAdvance(primary, codepoint, fontSize, out double primaryAdvance)) return primaryAdvance;
            return 0;
        }

        static bool TryGetAtgAdvance(
            UnityEngine.TextCore.Text.FontAsset asset,
            uint codepoint,
            double fontSize,
            out double advance) {
            advance = 0;
            if (asset == null) return false;
            try {
                var lookup = asset.characterLookupTable;
                if (lookup != null && lookup.TryGetValue(codepoint, out var existing) && existing?.glyph != null) {
                    double scale = fontSize / System.Math.Max(1.0, asset.faceInfo.pointSize);
                    advance = existing.glyph.metrics.horizontalAdvance * scale;
                    return advance > 0;
                }

                if (!MarkAtgAdvanceAttempt(asset, codepoint)) return false;
                atgAdvanceScratch.Clear();
                AppendCodepoint(atgAdvanceScratch, codepoint);
                asset.TryAddCharacters(atgAdvanceScratch.ToString(), out _, false);

                lookup = asset.characterLookupTable;
                if (lookup != null && lookup.TryGetValue(codepoint, out var added) && added?.glyph != null) {
                    double scale = fontSize / System.Math.Max(1.0, asset.faceInfo.pointSize);
                    advance = added.glyph.metrics.horizontalAdvance * scale;
                    return advance > 0;
                }
            } catch {
                return false;
            }
            return false;
        }

        static bool MarkAtgAdvanceAttempt(UnityEngine.TextCore.Text.FontAsset asset, uint codepoint) {
            int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(asset);
            if (!atgAdvanceAttempts.TryGetValue(key, out var attempted)) {
                attempted = new System.Collections.Generic.HashSet<uint>();
                atgAdvanceAttempts[key] = attempted;
            }
            return attempted.Add(codepoint);
        }

        static void AppendCodepoint(System.Text.StringBuilder sb, uint codepoint) {
            if (codepoint <= 0xFFFF) {
                sb.Append((char)codepoint);
                return;
            }
            int scalar = (int)codepoint - 0x10000;
            sb.Append((char)((scalar >> 10) + 0xD800));
            sb.Append((char)((scalar & 0x3FF) + 0xDC00));
        }

        // Parallel to TryWireAtgEmojiFallback but creates the asset in SDFAA
        // render mode so the atlas stores a coverage SDF (Alpha8). Used as
        // the FIRST emoji fallback so text-default codepoints (↩ ⏸ ⚠ etc.)
        // route here instead of the COLOR atlas — the SDF coverage path
        // multiplies by the run's fillColor, so the glyph picks up CSS
        // `color` and renders monochrome (matching Chrome's text presentation
        // for these codepoints). Emoji-default codepoints (🔨 🔄 🐱) are
        // explicitly excluded from this asset's TryAddCharacters calls in
        // AtgGlyphAtlasAdapter, so the chain falls through to COLOR for them.
        static void TryWireAtgMonoEmojiFallback(UnityEngine.Object primaryAsset) {
            var primary = primaryAsset as UnityEngine.TextCore.Text.FontAsset;
            if (primary == null) return;
            var mono = sharedAtgMonoEmojiFontAsset as UnityEngine.TextCore.Text.FontAsset;
            bool monoAlive = false;
            if (mono != null) {
                try {
                    var tex = mono.atlasTextures != null && mono.atlasTextures.Length > 0
                        ? mono.atlasTextures[0] : null;
                    monoAlive = tex != null;
                    if (monoAlive && mono.atlasPadding != 9) {
                        monoAlive = false;
                    }
                } catch {
                    monoAlive = false;
                }
            }
            if (!monoAlive) {
                mono = null;
                sharedAtgMonoEmojiFontAsset = null;
            }
            if (mono == null) {
                // Monochrome symbol fallback for text-default codepoints
                // (★ ◆ ▲ ● ♠ ✓ ⚠ ❤ …). The bundled OFL Noto Sans Symbols 2
                // covers the Geometric Shapes / Dingbats / Misc Symbols blocks
                // that the color emoji font (and Inter) don't, and as outline
                // glyphs SDFAA rasterization gives a tight silhouette that picks
                // up CSS color cleanly. (Segoe UI Symbol — the old source — is
                // Microsoft-proprietary and can't ship; a consumer override at
                // Assets/UI/Fonts/ still wins in the editor.)
                var symbolFont = LoadMonoSymbolFont();
                if (symbolFont == null) return;
                try {
                    // Keep this padding aligned with the primary SDF text
                    // atlases. Text-shadow blur now renders through a filter
                    // scope, so the old oversized Symbol padding just inflated
                    // glyph quads and made icon text (notably ★) diverge from
                    // browser layout.
                    mono = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                        symbolFont,
                        samplingPointSize: 90,
                        atlasPadding: 9,
                        renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                        atlasWidth: 1024,
                        atlasHeight: 1024,
                        atlasPopulationMode: UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic,
                        enableMultiAtlasSupport: true);
                } catch (System.Exception ex) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                        "Could not create ATG mono symbol FontAsset: " + ex.Message);
                    return;
                }
                if (mono == null) return;
                mono.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
                sharedAtgMonoEmojiFontAsset = mono;
            }
            if (primary.fallbackFontAssetTable == null) {
                primary.fallbackFontAssetTable = new System.Collections.Generic.List<UnityEngine.TextCore.Text.FontAsset>();
            }
            // Always FIRST in the chain — ATG walks fallbacks in order; mono
            // needs to win over color for codepoints both atlases cover. Move
            // it to index 0 if already present further down.
            int existingIdx = primary.fallbackFontAssetTable.IndexOf(mono);
            if (existingIdx > 0) {
                primary.fallbackFontAssetTable.RemoveAt(existingIdx);
                primary.fallbackFontAssetTable.Insert(0, mono);
            } else if (existingIdx < 0) {
                primary.fallbackFontAssetTable.Insert(0, mono);
            }
        }

        static void TryWireAtgEmojiFallback(UnityEngine.Object primaryAsset) {
            var primary = primaryAsset as UnityEngine.TextCore.Text.FontAsset;
            if (primary == null) return;
            // Build/reuse the emoji asset. Validate it's still alive (atlas
            // texture can be GC'd separately from the FontAsset itself).
            // HideAndDontSave on the FontAsset doesn't protect its child
            // Texture2Ds — those get GC'd independently after domain reloads
            // / scene switches. When that happens, TryAddCharacters throws
            // MissingReferenceException trying to read width on the dead
            // Texture2D, so we need to detect and rebuild from scratch.
            var emoji = sharedAtgEmojiFontAsset as UnityEngine.TextCore.Text.FontAsset;
            bool emojiAlive = false;
            if (emoji != null) {
                try {
                    var tex = emoji.atlasTextures != null && emoji.atlasTextures.Length > 0
                        ? emoji.atlasTextures[0] : null;
                    emojiAlive = tex != null;
                } catch {
                    emojiAlive = false;
                }
            }
            if (!emojiAlive) {
                emoji = null;
                sharedAtgEmojiFontAsset = null;
            }
            if (emoji == null) {
                // Resolve the color-emoji TTF: a consumer override in the project
                // wins, otherwise the OFL-licensed Noto Color Emoji bundled with
                // the package (loaded from Resources so it works in player builds,
                // not just the editor). Segoe UI Emoji is Microsoft-proprietary
                // and can't ship, which is why the bundled default is Noto.
                var emojiFont = LoadColorEmojiFont();
                if (emojiFont == null) return;
                try {
                    // COLOR render mode: Segoe UI Emoji ships emoji as color-
                    // only glyphs (COLR/CPAL tables). SDFAA rasterization
                    // can't extract a monochrome outline for those — every
                    // TryAddCharacters call returns the codepoint as missing.
                    // COLOR mode rasterizes the actual color bitmap into the
                    // atlas; the renderer dispatches to the `_TEXT_COLOR`
                    // shader path via IsColorAtlasId on the atlas registry
                    // (see MarkColorAtlasById call after CreateFontAsset).
                    emoji = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                        emojiFont,
                        samplingPointSize: 109,
                        atlasPadding: 4,
                        renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.COLOR,
                        atlasWidth: 2048,
                        atlasHeight: 2048,
                        atlasPopulationMode: UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic,
                        enableMultiAtlasSupport: true);
                } catch (System.Exception ex) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("SdfBootstrap",
                        "Could not create ATG emoji FontAsset: " + ex.Message);
                    return;
                }
                if (emoji == null) return;
                emoji.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
                // No hardcoded emoji list: the adapter calls TryAddCharacters
                // on every fallback FontAsset at shape time, so any emoji in
                // a rendered run gets rasterized into this atlas on demand.
                // A starter set used to live here but it would have ranked
                // some emoji as "supported" while leaving every other emoji
                // as a missing-glyph tofu placeholder — confusing behavior.
                sharedAtgEmojiFontAsset = emoji;
            }
            // Attach to primary's fallback chain (idempotent).
            if (primary.fallbackFontAssetTable == null) {
                primary.fallbackFontAssetTable = new System.Collections.Generic.List<UnityEngine.TextCore.Text.FontAsset>();
            }
            if (!primary.fallbackFontAssetTable.Contains(emoji)) {
                primary.fallbackFontAssetTable.Add(emoji);
            }
        }

        // Resolve the color-emoji source font, consumer-override first. Drop a
        // TTF at one of the Assets/ paths to swap in a different emoji set
        // (Twemoji, OpenMoji, …); otherwise the bundled OFL Noto Color Emoji is
        // used. The bundled font lives under Runtime/Resources/Fonts so it loads
        // via Resources.Load in player builds too — not just the editor.
        static UnityEngine.Font LoadColorEmojiFont() {
#if UNITY_EDITOR
            // Editor only: a project-level override TTF wins over the bundled
            // font. AssetDatabase resolves both Assets/ and Packages/ paths.
            string[] overrides = {
                "Assets/UI/Fonts/NotoColorEmoji.ttf",   // consumer override (preferred)
                "Assets/UI/Fonts/SegoeUIEmoji.ttf",     // legacy local override (back-compat)
            };
            foreach (var p in overrides) {
                var f = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Font>(p);
                if (f != null) return f;
            }
#endif
            // Bundled default — Resources works in editor AND player builds.
            var bundled = Resources.Load<UnityEngine.Font>("Fonts/NotoColorEmoji");
            if (bundled != null) return bundled;
            return null;
        }

        // Resolve the monochrome symbol fallback font, consumer-override first.
        // Bundled OFL Noto Sans Symbols 2 covers Geometric Shapes / Dingbats /
        // Misc Symbols (★ ◆ ▲ ● ♠ ✓ …); loaded from Resources so it works in
        // builds. A project SegoeUISymbol.ttf override still wins in the editor.
        static UnityEngine.Font LoadMonoSymbolFont() {
#if UNITY_EDITOR
            string[] overrides = {
                "Assets/UI/Fonts/NotoSansSymbols2-Regular.ttf", // consumer override
                "Assets/UI/Fonts/SegoeUISymbol.ttf",            // legacy local override
            };
            foreach (var p in overrides) {
                var f = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Font>(p);
                if (f != null) return f;
            }
#endif
            var bundled = Resources.Load<UnityEngine.Font>("Fonts/NotoSansSymbols2-Regular");
            if (bundled != null) return bundled;
            return null;
        }
#endif

        static CharacterFallback sharedFallback;
        public static CharacterFallback SharedFallback => sharedFallback;
        public static FontLoader SharedLoader => sharedLoader;
        public static GlyphAtlas SharedAtlas => sharedAtlas;
        public static UnityFontEngineBackend SharedBackend => sharedBackend;
        public static SdfGlyphRasterizer SharedRasterizer => sharedRasterizer;
        public static SdfGlyphAtlasAdapter SharedAdapter => sharedAdapter;

        // Registers the package's bundled default font under generic family
        // names ("sans-serif", "serif", "monospace", "system-ui") via
        // FontResolver. Idempotent; safe to call repeatedly.
        public static void EnsurePackageDefaultRegistered() {
            if (defaultRegistered) return;
            defaultRegistered = true;
            // Locate the bundled Regular face. Resources lookup is the production
            // path (the package consumer may override via Assets/Resources/Fonts/).
            string path = ResolveBundledDefaultPath();
            if (string.IsNullOrEmpty(path)) return;

            // CSS Fonts L4 §11 — register the Regular face covering weight 100–600,
            // the Bold face covering 700–1000, and the Italic face.
            // "Don't overwrite a user-registered family" still applies: we only
            // register when the family has NO entries yet (HasRegistered probes
            // TryResolve which now matches any face in the list).
            string boldPath   = ResolveBundledVariantPath("Weva-Default-Bold.ttf");
            string italicPath = ResolveBundledVariantPath("Weva-Default-Italic.ttf");

            // Register under each generic-family alias.
            string[] families = { "sans-serif", "serif", "monospace", "system-ui" };
            foreach (var family in families) {
                if (HasRegistered(family)) continue;
                // Regular: weight 100–600, non-italic.
                FontResolver.RegisterFontFace(family, path, 100f, 600f, false);
                // Bold: weight 700–1000, non-italic. Falls back to Regular path
                // when the Bold TTF isn't found (builds without the variant file).
                string bp = !string.IsNullOrEmpty(boldPath) ? boldPath : path;
                FontResolver.RegisterFontFace(family, bp, 700f, 1000f, false);
                // Italic: all weights, italic flag set. Falls back to Regular.
                string ip = !string.IsNullOrEmpty(italicPath) ? italicPath : path;
                FontResolver.RegisterFontFace(family, ip, 100f, 1000f, true);
            }
        }

        static bool defaultRegistered;
        static bool HasRegistered(string family) {
            return FontResolver.TryResolve(family, out var f) && !string.IsNullOrEmpty(f.Path);
        }

        static string ResolveBundledDefaultPath() {
            // Probe the in-package path first (works in editor, embedded packages,
            // and PackageCache). Build pipelines preserve the file because it's
            // under Runtime/ which is included in the asmdef-defined assembly's
            // Resources scope.
            string[] candidates = {
                "Packages/com.wevaui/Runtime/Resources/Fonts/Weva-Default.ttf",
                System.IO.Path.Combine(Application.streamingAssetsPath, "Fonts", "Weva-Default.ttf")
            };
            foreach (var p in candidates) {
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        // Resolves a named weight/style variant (e.g. "Weva-Default-Bold.ttf")
        // from the same locations as the Regular face. Returns null when the
        // file is not found — callers fall back to the Regular path.
        static string ResolveBundledVariantPath(string fileName) {
            string[] candidates = {
                "Packages/com.wevaui/Runtime/Resources/Fonts/" + fileName,
                System.IO.Path.Combine(Application.streamingAssetsPath, "Fonts", fileName)
            };
            foreach (var p in candidates) {
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        sealed class BackendGlyphProbe : CharacterFallback.IGlyphProbe {
            readonly ITextCoreBackend backend;
            public BackendGlyphProbe(ITextCoreBackend b) { backend = b; }
            public bool HasGlyph(FaceInfo face, uint codepoint) {
                if (backend == null || !face.IsValid) return false;
                return backend.TryGetGlyphAdvance(face, codepoint, 16, out _);
            }
        }
    }
}
#endif
