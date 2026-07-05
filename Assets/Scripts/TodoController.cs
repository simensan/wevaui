using UnityEngine;
using Weva;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using TMPro;
using Weva.Text.Tmp;

// Dynamic-list sample. Demonstrates:
//   - adding elements at runtime via input + button (AddFromInput)
//   - adding elements without input (AddRandom)
//   - removing a single element by reading the event Target (RemoveItem)
//   - clearing the whole list (ClearAll)
//   - a reactive counter binding ({{ ItemCount }} in todo.html)
//
// DOM mutation contract: AppendChild / RemoveChild on the live Document
// bump the node's Version and raise a mutation event that the
// InvalidationTracker subscribes to. The next WevaDocument.Update tick
// drains the dirty set and runs cascade / layout / paint for the
// affected subtree — so the only thing this controller needs to do is
// mutate the tree.
public sealed class TodoController : MonoBehaviour, IBindingVersion {
    [UIBind] public int ItemCount;

    // IBindingVersion: bumped whenever ItemCount changes so idle frames skip
    // the binding poll entirely. The DOM rows themselves are mutated directly
    // (AppendChild/RemoveChild) and flow through the InvalidationTracker, so
    // only the {{ ItemCount }} binding needs the version signal.
    public int BindingVersion { get; private set; }
    void BumpBindings() => BindingVersion++;

    // Same TMP wiring UitestController uses — registers the project's
    // bundled Segoe UI SDF asset under the generic CSS family names so
    // text rendering goes through TMP's atlas instead of the runtime
    // FontEngine rasterizer.
    [SerializeField] TMP_FontAsset tmpDefaultFont;

    WevaDocument doc;
    static readonly string[] s_RandomTitles = {
        "Refactor renderer",
        "Write tests",
        "Profile cold path",
        "Buy milk",
        "Review PR",
        "Fix flaky test",
        "Add docs",
        "Ship it",
    };
    int randomCursor;

    // Wiring runs in both OnEnable and Start. Start is the safety net: in
    // some play-mode entry paths (DisableDomainReload re-entry, or when
    // OnEnable fires before sibling MonoBehaviours have finished Awake)
    // OnEnable can fire too early or be skipped silently, leaving the
    // WevaDocument's controller field unset and every on-click attribute
    // dangling. Calling EnsureWired() from both hooks makes it idempotent
    // and guarantees handlers are bound by the first frame.
    void OnEnable() { EnsureWired(); }
    void Start() { EnsureWired(); }

    void EnsureWired() {
        if (doc == null) doc = GetComponent<WevaDocument>();
        if (doc == null) return;
        if (doc.CurrentState == null) return;
        if (doc.GetElementById("todo-list") == null) return;

        // Pin Segoe UI SDF as the primary face for sans-serif/system-ui.
        // SdfBootstrap's editor auto-register might otherwise pick
        // LiberationSans (which TMP ships with a static atlas — chars cannot
        // be added at runtime, so the demo text never appears).
        //
        // After registering the primary we explicitly wire the two emoji
        // FontAssets as fallbacks. This is what restores glyph coverage for
        // ✕ (U+2715), — (U+2014), ↩, ⏸, ⚠, and the dual-presentation emoji
        // set. RegisterFontAsset preserves any fallbacks already on the
        // chain, but we add them here unconditionally so a clean play-mode
        // entry (no prior chain state) still ends up with full coverage.
        TMP_FontAsset preferred = tmpDefaultFont;
#if UNITY_EDITOR
        var segoe = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/UI/Fonts/SegoeUI SDF.asset");
        if (segoe != null) preferred = segoe;
#endif
        if (preferred != null) {
            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", preferred);
            TmpFontAssetRegistry.RegisterFontAsset("system-ui", preferred);
            // Pre-rasterize every character the demo will use so the atlas
            // is stable across paints. The snapshot/cache system caches glyph
            // UVs; if a later TryAddCharacters call repacks the atlas, those
            // cached UVs go stale and ✕ / — glyphs visibly garble after the
            // first hover/state change. Seeding here forces the rasterizer
            // to lay out all needed glyphs once, up front.
            try {
                preferred.TryAddCharacters(
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ" +
                    "0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
                    out _, false);
            } catch { }
#if UNITY_EDITOR
            foreach (var path in new[] {
                "Assets/UI/Fonts/SegoeUIEmoji SDF.asset",
                "Assets/UI/Fonts/SegoeUIEmoji COLOR.asset",
            }) {
                var fb = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (fb == null) continue;
                TmpFontAssetRegistry.AddFallback("sans-serif", fb);
                TmpFontAssetRegistry.AddFallback("system-ui", fb);
                // The fallback also needs its glyphs warmed for ✕ + em dash
                // so the first paint references stable atlas UVs.
                try {
                    fb.TryAddCharacters("✕—↩⏸⚠", out _, false);
                } catch { }
            }
#endif
        }
        doc.SetController(this);
        RecountFromDom();
    }

    // -- Add --------------------------------------------------------------

    // Reads the text from the <input id="new-item"> and appends a row.
    // Blank input is a no-op: pressing Add without typing should feel
    // like a UX dead-end, not silently append a placeholder.
    public void AddFromInput() {
        var input = doc?.GetElementById("new-item");
        if (input == null) return;
        var ui = new InputElement(input);
        string text = ui.Value;
        if (string.IsNullOrWhiteSpace(text)) return;
        AppendItem(text);
        ui.Value = "";
    }

    public void AddRandom() {
        string text = s_RandomTitles[randomCursor % s_RandomTitles.Length];
        randomCursor++;
        AppendItem(text);
    }

    void AppendItem(string text) {
        var list = doc?.GetElementById("todo-list");
        if (list == null) return;
        // Build the row:
        //   <li class="item">
        //     <span class="text">…</span>
        //     <button class="remove" on-click="RemoveItem">✕</button>
        //   </li>
        var li = new Element("li");
        li.SetAttribute("class", "item");
        var span = new Element("span");
        span.SetAttribute("class", "text");
        span.AppendChild(new TextNode(text));
        var btn = new Element("button");
        btn.SetAttribute("class", "remove");
        btn.SetAttribute("on-click", "RemoveItem");
        btn.AppendChild(new TextNode("✕"));
        li.AppendChild(span);
        li.AppendChild(btn);
        list.AppendChild(li);
        ItemCount++;
        BumpBindings();
    }

    // -- Remove -----------------------------------------------------------

    // Bound via on-click on each row's ✕ button. The bound method takes a
    // UIEvent so we can read evt.Target — that's the button — then walk
    // up one level to the <li> and detach it from the list.
    public void RemoveItem(UIEvent evt) {
        var btn = evt?.Target;
        var li = btn?.Parent as Element;
        if (li == null) return;
        var list = li.Parent;
        if (list == null) return;
        list.RemoveChild(li);
        if (ItemCount > 0) { ItemCount--; BumpBindings(); }
    }

    public void ClearAll() {
        var list = doc?.GetElementById("todo-list");
        if (list == null) return;
        // Iterate a copy because RemoveChild mutates Children mid-walk.
        var copy = new System.Collections.Generic.List<Node>(list.Children);
        for (int i = 0; i < copy.Count; i++) list.RemoveChild(copy[i]);
        ItemCount = 0;
        BumpBindings();
    }

    // Seed ItemCount from whatever rows the HTML shipped with so the
    // counter binding starts truthful.
    void RecountFromDom() {
        var list = doc?.GetElementById("todo-list");
        if (list == null) { ItemCount = 0; BumpBindings(); return; }
        int n = 0;
        foreach (var c in list.Children) {
            if (c is Element e && e.TagName == "li") n++;
        }
        ItemCount = n;
        BumpBindings();
    }
}
