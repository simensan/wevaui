using System.Globalization;
using UnityEngine;
using Weva;
using Weva.Binding;

// The uitest scene's controller: a click counter, a perf HUD and the
// dialog / smooth-scroll demo handlers the sample pages call. The fonts and
// sprites it used to register with the C# engine are the core's now: the
// document's Font/Fallbacks fields, @font-face in the sheets, url() next to
// the page. The nook-dialogue typewriter that rebuilt DOM text nodes is gone
// with the C# DOM; text changes go through bindings.
[ExecuteAlways]
public sealed class UitestController : MonoBehaviour, IBindingVersion {
    [UIBind] public int ClickCount;
    [UIBind] public string FrameMs = "0.0";
    [UIBind] public string Fps = "0";

    public int BindingVersion { get; private set; }
    void BumpBindings() => BindingVersion++;

    double perfFrameMs;
    double perfAccumSeconds;

    WevaDocument doc;

    void OnEnable() {
        doc = GetComponent<WevaDocument>();
        if (doc != null) doc.SetController(this);
    }

    void Update() {
        if (!Application.isPlaying) return;
        double dt = Time.unscaledDeltaTime;
        if (dt <= 0) return;

        double frameMs = dt * 1000.0;
        perfFrameMs = perfFrameMs <= 0 ? frameMs : perfFrameMs + (frameMs - perfFrameMs) * 0.12;

        perfAccumSeconds += dt;
        if (perfAccumSeconds < 0.25) return;
        perfAccumSeconds = 0;

        FrameMs = perfFrameMs.ToString("F1", CultureInfo.InvariantCulture);
        Fps = perfFrameMs > 0 ? (1000.0 / perfFrameMs).ToString("F0", CultureInfo.InvariantCulture) : "0";
        BumpBindings();
    }

    public void OnStart() {
        ClickCount++;
        BumpBindings();
        Debug.Log($"Weva: clicked. ClickCount={ClickCount}");
    }

    public void OpenDialog() => doc?.Query("#info-dialog").ShowDialog(modal: true);

    public void CloseDialog() => doc?.Query("#info-dialog").CloseDialog();

    // "Jump to bottom" in the smooth-scroll card: the container has
    // `scroll-behavior: smooth`, so the core eases there.
    public void ScrollBottom() {
        if (doc == null) return;
        foreach (WevaElement area in doc.QueryAll(".smooth-area")) area.ScrollTo(area.Scroll.x, area.MaxScroll.y);
    }
}
