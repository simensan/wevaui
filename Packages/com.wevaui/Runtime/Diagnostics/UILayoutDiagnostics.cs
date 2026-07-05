namespace Weva.Diagnostics {
    // Opt-in trace channel for diagnosing layout / paint divergences on
    // specific elements. Distinct from UICssDiagnostics: no dedupe, fires
    // every call when Enabled is true, NEVER fires when Enabled is false.
    // Use TraceFor(element, message) to scope output to a class-tagged
    // element (e.g. a button you're trying to diagnose) without flooding
    // the console for the rest of the tree.
    //
    // Typical workflow:
    //   1. Set UILayoutDiagnostics.Enabled = true and
    //      UILayoutDiagnostics.MatchClassContains = "play-btn"
    //      somewhere in editor scratch / a BeforeRender hook.
    //   2. Reload / repaint.
    //   3. Read the Unity console for lines tagged "[Weva/Layout]".
    //   4. Set Enabled = false again.
    //
    // The class-match check is a substring on Element.Class — cheap and
    // tolerant of multi-class strings (e.g. "play-btn-label disabled").
    // Set MatchClassContains to null to log for every element when
    // Enabled is true (use sparingly).
    public static class UILayoutDiagnostics {
        public static bool Enabled = false;
        public static string MatchClassContains = null;

        public static bool ShouldTrace(Weva.Dom.Element e) {
            if (!Enabled) return false;
            if (string.IsNullOrEmpty(MatchClassContains)) return true;
            if (e == null) return false;
            string cls = e.ClassName;
            if (string.IsNullOrEmpty(cls)) return false;
            return cls.IndexOf(MatchClassContains, System.StringComparison.Ordinal) >= 0;
        }

        public static void Trace(string source, string message) {
            if (!Enabled) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || TESTVERIFY
            UnityEngine.Debug.Log("[Weva/Layout] " + source + ": " + message);
#endif
        }

        public static void TraceFor(Weva.Dom.Element e, string source, string message) {
            if (!ShouldTrace(e)) return;
            string tag = e != null
                ? (e.TagName + (string.IsNullOrEmpty(e.ClassName) ? "" : "." + e.ClassName) +
                   (string.IsNullOrEmpty(e.Id) ? "" : "#" + e.Id))
                : "<null>";
#if UNITY_EDITOR || DEVELOPMENT_BUILD || TESTVERIFY
            UnityEngine.Debug.Log("[Weva/Layout] " + source + " <" + tag + ">: " + message);
#endif
        }

        // Independent opt-in channel for the GPU mask-layer packing path
        // (UIBatcher.StoreMaskLayer). The mask quad-instance encode is the last
        // CPU step before the shader, so when a mask "doesn't render" this trace
        // tells you whether the data reaching the GPU (kind, tile size, repeat,
        // radial center/radius in params1) is correct — isolating a C# packing
        // bug from a shader sampling bug. Set TraceMaskEncoding = true, repaint
        // the masked element, read "[Weva/Mask]" lines, set it false again.
        // Fires never when false; cheap flag-check when off.
        public static bool TraceMaskEncoding = false;

        public static void TraceMask(string message) {
            if (!TraceMaskEncoding) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || TESTVERIFY
            UnityEngine.Debug.Log("[Weva/Mask] " + message);
#endif
        }
    }
}
