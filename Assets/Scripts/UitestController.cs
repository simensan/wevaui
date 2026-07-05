using System.Globalization;
using UnityEngine;
using Weva;
using Weva.Binding;
using Weva.Forms;
using Weva.Paint.Images;
using TMPro;
using Weva.Text.Tmp;

// ExecuteAlways: WevaDocument previews in the edit-mode Game view, but the
// fonts/sprites this controller registers only existed in play mode — every
// image-using sample previewed with magenta missing-texture fills. OnEnable's
// registrations are edit-safe (static registries + in-memory assets); the
// per-frame gameplay bits (typewriter, perf counters) gate on isPlaying.
[ExecuteAlways]
public sealed class UitestController : MonoBehaviour, IBindingVersion {
    [UIBind] public int ClickCount;
    [UIBind] public string FrameMs = "0.0";
    [UIBind] public string Fps = "0";

    // IBindingVersion: bumped on every bound-data write so the engine's
    // BindingSet.Update can skip the whole per-frame poll on idle frames.
    public int BindingVersion { get; private set; }
    void BumpBindings() => BindingVersion++;

    double perfFrameMs;
    double perfAccumSeconds;

    // ── nook-dialogue typewriter ─────────────────────────────────────────
    // Reveals the dialogue text character-by-character (Animal-Crossing style)
    // while the nook-dialogue sample is the active document. The model carries
    // colour runs so the green player name types in step with the rest.
    struct NookRun {
        public int Line; public string Text; public string Cls;
        public NookRun(int line, string text, string cls) { Line = line; Text = text; Cls = cls; }
    }
    static readonly NookRun[] NookRuns = {
        new NookRun(0, "Welcome, ", ""), new NookRun(0, "Matt", "hl"), new NookRun(0, "!", ""),
        new NookRun(1, "How can I help you today?", ""),
    };
    const double NookCharsPerSecond = 26.0;
    string lastDocName;
    bool nookActive;
    double nookElapsed;
    int nookRevealed;

    // LiberationSans SDF asset (Assets/TextMesh Pro/Fonts/LiberationSans SDF.asset)
    // wired in the Inspector. At OnEnable we register it under the generic CSS
    // family names so all "sans-serif"/"system-ui" text in menu.html and
    // randhtml.html resolves through TMP's pre-baked SDF atlas + kerning table
    // instead of our runtime FontEngine rasterizer.
    //
    // No #if WEVA_TMP guard: that define lives only inside the Weva.Runtime
    // asmdef, while this controller compiles in Assembly-CSharp where the define
    // isn't visible. TMPro + Weva.Text.Tmp resolve via auto-referenced asmdefs,
    // so the using directives are always valid.
    [SerializeField] TMP_FontAsset tmpDefaultFont;

    // Optional emoji TMP_FontAsset registered as a fallback after the
    // primary face. Walked per-codepoint by ShapeTmp so 🛡 ⚔ ⚡ 🐲 etc. can
    // be rendered through SegoeUIEmoji's atlas instead of being dropped.
    // Auto-loaded from Assets/UI/Fonts/SegoeUIEmoji SDF.asset at runtime so
    // the field doesn't have to be wired in the Inspector.
    [SerializeField] TMP_FontAsset tmpEmojiFallback;

    // PLAYER BUILDS: AssetDatabase does not exist outside the editor, so
    // every editor-time `LoadAssetAtPath` above/below silently returns
    // nothing in a build — glyph icons (⌕ ⌁ ☾ ✈ in glass.html) and the
    // 9-slice demo sprites shipped MISSING from the first player build.
    // These serialized references are the build-safe path: wire them in
    // the Inspector and Unity includes the assets in the player. The
    // editor still prefers the AssetDatabase loads (always-fresh), with
    // these as the player/runtime source. Order matters for the emoji
    // chain: COLOR atlas first (true-color), SDF as backstop.
    [SerializeField] TMP_FontAsset[] tmpEmojiChain;
    [SerializeField] Sprite panelFrameSprite;
    [SerializeField] Sprite buttonFrameSprite;
    [SerializeField] Sprite buttonFrameHoverSprite;

    WevaDocument doc;
    DialogElement dialog;

    void OnEnable() {
        // Prefer Segoe UI for "sans-serif" (matches Chrome's default font on
        // Windows so layout-vs-Chrome compare scores converge — LiberationSans
        // measured ~7px wider/narrower than Chrome on individual elements,
        // accumulating to a Δ=330 layout score that the SDF-rendering side
        // couldn't close). Falls back to the Inspector-wired LiberationSans
        // when the SegoeUI SDF asset isn't built.
        TMPro.TMP_FontAsset preferred = tmpDefaultFont;
#if UNITY_EDITOR
        var segoe = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/UI/Fonts/SegoeUI SDF.asset");
        if (segoe != null) preferred = segoe;
#endif
        if (preferred != null) {
            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", preferred);
            TmpFontAssetRegistry.RegisterFontAsset("system-ui", preferred);
            TmpFontAssetRegistry.RegisterFontAsset("Arial", preferred);
            TmpFontAssetRegistry.RegisterFontAsset("Inter", preferred);

            // Resolve the emoji fallback chain. We register the COLOR-baked
            // atlas FIRST so the shaper picks it for codepoints both atlases
            // contain (the v0.9+ _TEXT_COLOR shader variant samples its RGBA
            // bitmaps directly — true-color emoji). The SDF atlas trails
            // behind as a backstop for any codepoint the COLOR bake didn't
            // cover (in practice both bakes carry the same 62-char set, but
            // the redundancy is cheap).
            //
            // Inspector field wins as the last fallback if it was wired
            // explicitly — useful for ad-hoc swaps without rebaking.
            TMP_FontAsset emojiColor = null;
            TMP_FontAsset emojiSdf = null;
#if UNITY_EDITOR
            emojiColor = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/UI/Fonts/SegoeUIEmoji COLOR.asset");
            emojiSdf = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/UI/Fonts/SegoeUIEmoji SDF.asset");
#endif
            // Build the registration order (COLOR > SDF > Inspector) and
            // dedupe so we don't double-add the same asset object. In a
            // player the editor loads above are null and the serialized
            // tmpEmojiChain (wired COLOR-then-SDF in the scene) carries
            // the same assets — see the PLAYER BUILDS note on the field.
            var emojiChain = new System.Collections.Generic.List<TMP_FontAsset>(3);
            void AddEmoji(TMP_FontAsset a) {
                if (a == null) return;
                if (emojiChain.Contains(a)) return;
                emojiChain.Add(a);
            }
            AddEmoji(emojiColor);
            AddEmoji(emojiSdf);
            if (tmpEmojiChain != null) {
                for (int i = 0; i < tmpEmojiChain.Length; i++) AddEmoji(tmpEmojiChain[i]);
            }
            AddEmoji(tmpEmojiFallback);
            // LAST in the chain: BMP symbol glyphs (⌕ ⌁ ◗ ☾ ✈ ☀ ♬ ⌘ …).
            // The EDITOR resolves these through ATG's OS-font fallback
            // (Segoe UI Symbol), which doesn't exist in players — the
            // player's TMP path only walks registered atlases, so glass.html
            // shipped with every tile icon missing. Build a dynamic TMP
            // asset from the OS symbol font at runtime; works in players on
            // Windows. Pre-populate explicitly: Weva reads the character
            // table directly, glyphs are never added on demand.
            AddEmoji(CreateOsSymbolFallback());
            foreach (var emoji in emojiChain) {
                TmpFontAssetRegistry.AddFallback("sans-serif", emoji);
                TmpFontAssetRegistry.AddFallback("system-ui", emoji);
                TmpFontAssetRegistry.AddFallback("Arial", emoji);
                TmpFontAssetRegistry.AddFallback("Inter", emoji);
            }

            // Sample display faces: Patrick Hand (story-bubble handwriting) and
            // Sniglet ExtraBold (nook-dialogue rounded gothic, ~FOT-Rodin).
            // Created as dynamic TMP SDF assets from the bundled TTFs and
            // registered under their CSS family names; the primary SDF face +
            // the emoji chain trail as fallbacks so any glyph the display face
            // lacks (odd punctuation, pictographs) still renders. Editor-only
            // asset loading mirrors the SegoeUI path above.
#if UNITY_EDITOR
            void RegisterDisplayFace(string path, string family, int naturalWeight) {
                var font = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(path);
                if (font == null) { Debug.LogWarning($"Weva demo: font not found at {path}"); return; }
                var fa = TMP_FontAsset.CreateFontAsset(font);
                if (fa == null) return;
                // CreateFontAsset returns a DYNAMIC asset with an EMPTY atlas
                // (characterTable.Count == 0); glyphs are normally added on
                // demand by TMP's own text components, but Weva reads the
                // character table directly, so we must pre-populate it. Without
                // this every glyph misses and the run silently falls back to the
                // default face — the font never visibly renders. Bake the
                // printable-ASCII range now (fallback chain covers the rest).
                var ascii = new System.Text.StringBuilder(95);
                for (char c = ' '; c <= '~'; c++) ascii.Append(c);
                fa.TryAddCharacters(ascii.ToString());
                // Register with the face's TRUE weight so the paint path's
                // faux-bold doesn't double-bold an already-bold face (Sniglet
                // ExtraBold @ font-weight:800 was rendering too heavy vs Chrome).
                var src = new TmpFontAssetSource(fa) { NaturalWeight = naturalWeight };
                TmpFontAssetRegistry.RegisterSource(family, src);
                if (preferred != null) TmpFontAssetRegistry.AddFallback(family, preferred);
                foreach (var emoji in emojiChain) TmpFontAssetRegistry.AddFallback(family, emoji);
                // Also register the TTF path with FontResolver so the SDF /
                // FontEngine fallback (used when the TMP atlas misses a glyph)
                // loads the real file instead of trying — and failing — to find
                // an OS font named '{family}'. Without this the fallback logs a
                // FontEngine 'Invalid_File_Structure' for these project fonts.
                Weva.Text.TextCore.FontResolver.RegisterFont(family, path);
            }
            RegisterDisplayFace("Assets/UI/Fonts/PatrickHand-Regular.ttf", "Patrick Hand", 400);
            RegisterDisplayFace("Assets/UI/Fonts/Sniglet-ExtraBold.ttf", "Sniglet", 800);
#endif
        }
        doc = GetComponent<WevaDocument>();
        if (doc != null) {
            doc.SetController(this);
            RegisterDemoImages();
        }
    }

    // Every BMP symbol codepoint the bundled samples use (grep of
    // Assets/UI/*.html, U+2000–U+2BFF). Pre-added to the OS symbol
    // fallback's character table at registration; codepoints the OS face
    // lacks simply miss (logged) and fall through.
    const string SampleSymbolChars =
        "‽→↓↩⇄−∞⊘⌁⌂⌕⌗⌘⌚⏭⏮⏸─▲▸▼◆◈◊◌◍●◔◗◯☀☄★☠☥☾♦♪♬⚔⚗⚙⚜⚡⛔⛨✈✉✎✓✕✚✦✨✸❄➤⤾⭐";

    static TMP_FontAsset CreateOsSymbolFallback() {
        try {
            // CreateFontAsset(familyName, styleName) goes through
            // FontEngine.TryGetSystemFontReference — the only creation path
            // that works for OS fonts in PLAYER builds. The Font-object
            // overload (CreateDynamicFontFromOSFont + CreateFontAsset(font))
            // fails even in the editor for OS faces (no readable face data
            // on the Font wrapper; players additionally strip dynamic data).
            var fa = TMP_FontAsset.CreateFontAsset("Segoe UI Symbol", "Regular", 90);
            if (fa == null) return null;
            fa.TryAddCharacters(SampleSymbolChars);
            return fa;
        } catch (System.Exception ex) {
            Debug.LogWarning($"Weva demo: OS symbol font fallback unavailable: {ex.Message}");
            return null;
        }
    }

    // Wire the 9-slice demo's sprite frames into an image registry so
    // 9slice-demo.html's <img src="UI/PanelFrame"> and
    // `border-image-source: url(...)` references resolve to real sprites.
    // Each PNG under Assets/UI/Sprites is imported as a Sprite (Single) with
    // a 16px (PanelFrame) / 12px (ButtonFrame) border configured in the
    // Sprite Editor; SpriteImageSource reads sprite.border so the engine
    // 9-slices automatically (IImageNineSliceSource). Registered under every
    // handle the demo uses, including the alias `Settings_Flyout_9slice.png`
    // (which has no asset of its own) so the first border-image card renders
    // too. Editor-only sprite loading mirrors the font-asset path above.
    void RegisterDemoImages() {
        var registry = new InMemoryImageRegistry();
        void RegSprite(Sprite sprite, params string[] handles) {
            if (sprite == null) return;
            var source = new SpriteImageSource(sprite);
            foreach (var h in handles) registry.Register(h, source);
        }
        // Serialized refs are the player-safe source (PLAYER BUILDS note on
        // the fields); the editor falls through to AssetDatabase when a
        // field was left unwired so the demo keeps working scene-agnostic.
        Sprite panel = panelFrameSprite, button = buttonFrameSprite, hover = buttonFrameHoverSprite;
#if UNITY_EDITOR
        Sprite EditorLoad(string assetPath) {
            var s = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (s == null) Debug.LogWarning($"Weva demo: sprite not found at {assetPath}");
            return s;
        }
        if (panel == null) panel = EditorLoad("Assets/UI/Sprites/PanelFrame.png");
        if (button == null) button = EditorLoad("Assets/UI/Sprites/ButtonFrame.png");
        if (hover == null) hover = EditorLoad("Assets/UI/Sprites/ButtonFrameHover.png");
#endif
        RegSprite(panel,
            "UI/PanelFrame", "UI/Sprites/PanelFrame.png", "Settings_Flyout_9slice.png");
        RegSprite(button,
            "UI/Sprites/ButtonFrame", "UI/Sprites/ButtonFrame.png");
        RegSprite(hover,
            "UI/Sprites/ButtonFrameHover", "UI/Sprites/ButtonFrameHover.png");
        if (registry.Count > 0) doc.ImageRegistry = registry;
    }

    void Update() {
        // Gameplay-only per-frame work (typewriter, perf HUD bindings) stays
        // out of the edit-mode preview; OnEnable's registrations are the
        // only part the preview needs.
        if (!Application.isPlaying) return;
        double dt = Time.unscaledDeltaTime;
        if (dt <= 0) return;

        TickNookTypewriter(dt);

        double frameMs = dt * 1000.0;
        perfFrameMs = perfFrameMs <= 0
            ? frameMs
            : perfFrameMs + (frameMs - perfFrameMs) * 0.12;

        perfAccumSeconds += dt;
        if (perfAccumSeconds < 0.25) return;
        perfAccumSeconds = 0;

        FrameMs = perfFrameMs.ToString("F1", CultureInfo.InvariantCulture);
        Fps = perfFrameMs > 0
            ? (1000.0 / perfFrameMs).ToString("F0", CultureInfo.InvariantCulture)
            : "0";
        BumpBindings();
    }

    // Drives the nook-dialogue character reveal. Waits for the document to
    // build, clears the text, then grows the revealed prefix over time.
    void TickNookTypewriter(double dt) {
        string docName = doc != null && doc.DocumentAsset != null ? doc.DocumentAsset.name : null;
        if (docName != lastDocName) {
            lastDocName = docName;
            nookActive = docName == "nook-dialogue";
            nookRevealed = -1;
        }
        if (!nookActive || doc.CurrentState == null) return;
        if (nookRevealed < 0) { nookElapsed = 0; RevealNook(0); nookRevealed = 0; return; }

        nookElapsed += dt;
        int total = 0;
        foreach (var r in NookRuns) total += r.Text.Length;
        int target = (int)(nookElapsed * NookCharsPerSecond);
        if (target > total) target = total;
        if (target != nookRevealed) { RevealNook(target); nookRevealed = target; }
    }

    // Rebuilds the .say paragraphs to show only the first `n` characters across
    // the colour runs (plain text vs the green-name <span class="hl">).
    void RevealNook(int n) {
        if (doc == null || doc.CurrentState == null) return;
        var says = new System.Collections.Generic.List<Weva.Dom.Element>();
        var found = doc.GetElementsByClassName("say");
        if (found != null) foreach (var e in found) says.Add(e);
        if (says.Count == 0) return;

        foreach (var p in says) {
            var kids = new System.Collections.Generic.List<Weva.Dom.Node>(p.Children);
            foreach (var k in kids) p.RemoveChild(k);
        }

        int remaining = n;
        foreach (var run in NookRuns) {
            if (run.Line < 0 || run.Line >= says.Count || remaining <= 0) continue;
            int take = System.Math.Min(run.Text.Length, remaining);
            remaining -= take;
            string shown = run.Text.Substring(0, take);
            var p = says[run.Line];
            if (string.IsNullOrEmpty(run.Cls)) {
                p.AppendChild(new Weva.Dom.TextNode(shown));
            } else {
                var span = new Weva.Dom.Element("span");
                span.SetAttribute("class", run.Cls);
                span.AppendChild(new Weva.Dom.TextNode(shown));
                p.AppendChild(span);
            }
        }
    }

    public void OnStart() {
        ClickCount++;
        BumpBindings();
        Debug.Log($"Weva: clicked. ClickCount={ClickCount}");
    }

    public void OpenDialog() {
        var d = doc?.GetElementById("info-dialog");
        if (d == null) return;
        dialog ??= new DialogElement(d);
        dialog.ShowModal();
    }

    public void CloseDialog() {
        dialog?.Close();
    }

    // Demo handler for the "Jump to bottom" button in the smooth-scroll
    // card: snaps the .smooth-area scroll container to its maximum
    // ScrollY. Smooth animation toward that target is handled by the
    // engine's SmoothScrollAnimator since the container has
    // `scroll-behavior: smooth`.
    public void ScrollBottom() {
        if (doc?.CurrentState == null) return;
        var areas = doc.GetElementsByClassName("smooth-area");
        if (areas == null) return;
        foreach (var area in areas) {
            var box = doc.CurrentState.ElementToBox?.Lookup(area);
            if (box == null) continue;
            var state = doc.LayoutEngine?.ScrollContainer?.GetOrCreate(box);
            state?.ScrollTo(state.ScrollX, double.PositiveInfinity);
        }
    }
}
