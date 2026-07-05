#if UNITY_2023_1_OR_NEWER
using UnityEngine;
using Weva.Documents;
using Weva.Layout.Text;
using Weva.Text.Unity;

namespace Weva.Text.TextCore {
    // Wires UIDocumentDefaults.FontMetricsFactory so the layout pipeline
    // measures text the same way IMGUI / Unity's renderer paints it. Without
    // this hook every WevaDocument falls back to MonoFontMetrics (the headless
    // stub: every char a fixed half-em wide), which IMGUI does not honor —
    // visible as gaps and overflow under the demo's proportional sans-serif.
    //
    // Order of preference:
    //   1. UnityGUIFontMetrics — fastest path to "layout matches GUI.Label".
    //      Uses GUIStyle.CalcSize against GUI.skin.label so the metrics come
    //      from the exact font IMGUIDocumentRenderer renders with.
    //   2. TextCoreFontMetrics — production-grade SDF path. Currently the
    //      backend's rasterization is stubbed (advance widths work; bitmap
    //      glyphs do not). When the rasterizer is wired up this will become
    //      the default; until then GUI metrics give the user a tight match.
    //   3. MonoFontMetrics — last-ditch fallback so the pipeline never throws.
    //
    // We register at SubsystemRegistration so the factory is in place before
    // any scene-driven WevaDocument.OnEnable runs (BeforeSceneLoad would also
    // work; SubsystemRegistration is earlier and cheap because we only set a
    // delegate). We additionally hook InitializeOnLoadMethod so the editor
    // preview window has a font-metrics provider in edit mode.
    //
    // The factory itself is lazy: CreateFontMetrics runs only when
    // UIDocumentDefaults.CreateDefaultFontMetrics is first invoked, so the
    // FontEngine isn't initialized at editor startup just because we hooked.
    // v0.3 picker. Superseded by Weva.Text.Sdf.SdfBootstrap which runs at the
    // same SubsystemRegistration phase but covers the full TTF/SDF path. We keep
    // CreateFontMetrics() public so existing callers (and tests) can still ask
    // for the legacy GUI-first chain explicitly, but the UIDocumentDefaults
    // factory hookup is owned by SdfBootstrap now to avoid double-registration.
    public static class TextCoreBootstrap {
        public static IFontMetrics CreateFontMetrics() {
            try {
                return new UnityGUIFontMetrics();
            } catch (System.Exception ex) {
                Debug.LogWarning("Weva: UnityGUIFontMetrics failed; trying TextCoreFontMetrics. " + ex.Message);
            }
            try {
                var backend = new UnityFontEngineBackend();
                var face = backend.LoadDefault();
                if (face.IsValid) return new TextCoreFontMetrics(backend, face);
            } catch (System.Exception ex) {
                Debug.LogWarning("Weva: TextCoreBootstrap failed to create font metrics; falling back to MonoFontMetrics. " + ex.Message);
            }
            return new MonoFontMetrics();
        }
    }
}
#endif
