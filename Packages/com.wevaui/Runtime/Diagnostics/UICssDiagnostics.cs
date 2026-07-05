using System.Collections.Generic;

namespace Weva.Diagnostics {
    // Severity threshold for Weva's engine diagnostics (font misses, emoji
    // misses, unsupported properties, …). Exposed on WevaDocument as an inspector
    // setting so authors can quiet the console.
    public enum WevaLogLevel {
        // Silence all engine diagnostics.
        Off = 0,
        // (Default) emit non-fatal warnings: unresolved fonts, missing emoji
        // glyphs, unsupported CSS, etc.
        Warnings = 1,
        // Everything Warnings emits (reserved for finer-grained info later).
        Verbose = 2,
    }

    // Centralised non-fatal-warning channel for the CSS / HTML engine.
    //
    // Every silent skip path in the engine (unsupported property, unparseable
    // value, no-op filter chain, unknown @-rule, ...) routes here so authors
    // can tell what's been ignored without grepping the source. Routing is
    // gated on UNITY_EDITOR || DEVELOPMENT_BUILD — release builds compile to
    // a no-op so the cascade doesn't pay the dictionary lookup, the string
    // formatting, or the Debug.LogWarning round-trip.
    //
    // Each (source, detail) pair is logged at most ONCE per session. The
    // first call seeds a HashSet entry; subsequent calls with the same pair
    // return without doing anything. This is the key contract — call sites
    // sit on hot paths that can fire thousands of times per frame, and a
    // single bad property must not flood the console.
    //
    // The dedupe set is a process-global static, never cleared. Tests that
    // need to reassert a warning fire across cases should call ResetForTests
    // between assertions.
    public static class UICssDiagnostics {
        // Default-on in editor + dev builds; off in release. Authored code
        // can flip this at runtime (e.g. a debug toggle); the gate compiles
        // away the call site entirely in release so flipping has no effect
        // there.
        public static bool Enabled = true;

        // Severity threshold. WevaDocument writes this from its inspector
        // `Diagnostic Log Level` setting on enable; set to Off to silence the
        // font/emoji/unsupported-CSS warnings. Process-global (the most recently
        // enabled document wins when several set different levels).
        public static WevaLogLevel LogLevel = WevaLogLevel.Warnings;

        static readonly object gate = new object();
        static readonly HashSet<string> emitted = new HashSet<string>();

        public static void Warn(string source, string detail, Weva.Dom.Element optionalElement = null) {
            if (!Enabled || LogLevel < WevaLogLevel.Warnings) return;
            if (string.IsNullOrEmpty(source)) source = "?";
            if (detail == null) detail = "";
            // Dedupe key intentionally elides the element — we want one
            // warning per "kind of failure", not one per element.
            string key = source + " " + detail;
            // The emitted set is populated unconditionally so HasEmittedForTests
            // works in all build configurations (including the headless test runner
            // which defines neither UNITY_EDITOR nor DEVELOPMENT_BUILD).
            lock (gate) {
                if (!emitted.Add(key)) return;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string message = "[Weva/CSS] " + source + ": " + detail;
            UnityEngine.Debug.LogWarning(message);
#endif
        }

        // Test hook — wipes the dedupe set so a re-running test can observe a
        // warning that was already emitted by an earlier test in the same
        // session. Not part of the production contract.
        internal static void ResetForTests() {
            lock (gate) emitted.Clear();
        }

        // Test introspection — returns the count of unique warnings observed
        // since the last ResetForTests. Used by the diagnostics tests; not
        // part of the production contract.
        internal static int EmittedCountForTests() {
            lock (gate) return emitted.Count;
        }

        internal static bool HasEmittedForTests(string source, string detail) {
            string key = source + " " + detail;
            lock (gate) return emitted.Contains(key);
        }
    }
}
