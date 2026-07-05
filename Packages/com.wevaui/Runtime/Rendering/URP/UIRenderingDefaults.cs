#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace Weva.Rendering.URP {
    // A6 (rendering half): SubsystemRegistration reset for the rendering-side
    // public static feature flags. Lives here rather than in the core
    // UISystemDefaults because Rendering/** is excluded from the headless
    // TestVerifyAll compile and the URP-pass/filter flags are #if-gated; this
    // keeps each reset next to a definition it can actually see. Without it, a
    // rendering debug toggle flipped in one play session persists into the next
    // when domain reload is off.
    public static class UIRenderingDefaults {
#if UNITY_5_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#endif
        public static void ResetRenderingDefaults() {
            // UIBatcher is pure C# (ungated) — its flags reset unconditionally.
            UIBatcher.UseAabbClipping = true;
            UIBatcher.ExactSrgbSourceOverNeedsBackdrop = false;
#if WEVA_URP
            // Must mirror the field's DEFAULT (audit N1: this reset once
            // hardcoded a stale value and silently overrode the field on
            // EVERY play entry). Currently true — the orientation-ledger
            // recalibration landed: the shared path's wrong-band bug was the
            // backdrop downsample blit's uncounted Y-inversion, counted now
            // in both chain walkers (see BackdropDownsampleTogglesFlip).
            UIRenderGraphFilterRuntime.EnableSharedBackdropBlur = true;
            // Audit AR1: this reset was gated on UNITY_2023_3_OR_NEWER (always
            // true on Unity 6), but UIRenderGraphPass only exists under
            // WEVA_URP — the wrong symbol made the package fail to compile
            // when URP is not installed, defeating the versionDefines design.
            UIRenderGraphPass.SrgbCompositeSourceYFlipOverride = false;
#endif
        }
    }
}
