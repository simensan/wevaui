using Weva.Layout;
using Weva.Layout.Diagnostics;
using Weva.Layout.Scrolling;
using Weva.Paint.Conversion;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace Weva.Diagnostics {
    // A6: centralized reset of the engine's public static config / diagnostic
    // flags at play-mode entry. With "Enter Play Mode Options -> Reload Domain"
    // OFF, static fields keep their last edit-session value across play
    // sessions — a debug toggle flipped once (or a test that forgot to restore
    // a flag) then silently persists. A single SubsystemRegistration hook
    // resets every such flag to its declared default so each play session
    // starts from a clean, shipping-default state.
    //
    // The attribute is gated on UNITY_5_3_OR_NEWER (mirrors UIMainThreadGuard):
    // the headless TestVerifyAll runner doesn't define it, so the method stays
    // a plain callable that tests invoke directly for per-case isolation.
    //
    // Rendering-side flags (UIBatcher / UIRenderGraphFilterRuntime /
    // UIRenderGraphPass) live under Rendering/** which the headless runner
    // excludes from compilation, so they get their own SubsystemRegistration
    // reset alongside their definitions — see UIRenderingDefaults.
    public static class UISystemDefaults {
#if UNITY_5_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#endif
        public static void ResetCoreDefaults() {
            // Diagnostics
            UICssDiagnostics.Enabled = true;
            UICssDiagnostics.LogLevel = WevaLogLevel.Warnings;
            UILayoutDiagnostics.Enabled = false;
            UILayoutDiagnostics.MatchClassContains = null;
            UILayoutDiagnostics.TraceMaskEncoding = false;
            LayoutInvariants.Enabled = false;

            // Layout feature toggles
            LayoutEngine.EnableScrollBoundaryReuse = true;
            LayoutEngine.EnableBubbleSkip = true;
            LayoutEngine.EnableIncrementalHeightPropagation = true;
            ScrollEventHandler.EnableViewportDragScroll = false;

            // Paint feature toggles
            BoxToPaintConverter.EnableExactSrgbGlassCompositing = true;
        }
    }
}
