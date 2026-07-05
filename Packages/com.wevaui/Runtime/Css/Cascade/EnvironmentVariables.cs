using System.Collections.Generic;

namespace Weva.Css.Cascade {
    /// <summary>
    /// Runtime registry for CSS <c>env()</c> environment variables
    /// (CSS Environment Variables Module Level 1).
    ///
    /// CSS <c>env()</c> references look up values from a user-agent
    /// supplied table — typically used on the web for notch/safe-area
    /// inset avoidance (e.g. <c>env(safe-area-inset-top)</c>).
    ///
    /// <para><b>Auto-pump:</b> when a <c>WevaDocument</c> is active in the
    /// scene, its per-frame <c>Update()</c> polls <c>UnityEngine.Screen.safeArea</c>
    /// and re-registers <c>safe-area-inset-{top,right,bottom,left}</c>
    /// whenever the rect changes (device rotation, notch entry/exit,
    /// system bar show/hide). Authors get correct insets on iOS/Android
    /// shipping builds with no extra setup. Builds on desktop / consoles
    /// where the full screen is safe keep the <c>0px</c> defaults.</para>
    ///
    /// Games that want extra environment variables (custom UI safe
    /// zones, controller-overlay reserved areas, etc.) can register them
    /// via <see cref="Register"/> at startup.
    ///
    /// Values are stored as the literal value-string an author would
    /// write in CSS — they flow through the rest of the cascade exactly
    /// like a <c>var()</c> substitution and are re-parsed by whatever
    /// property consumer the env() expression sits inside.
    ///
    /// Pre-registered defaults:
    ///   - <c>safe-area-inset-top</c>    → <c>0px</c>
    ///   - <c>safe-area-inset-right</c>  → <c>0px</c>
    ///   - <c>safe-area-inset-bottom</c> → <c>0px</c>
    ///   - <c>safe-area-inset-left</c>   → <c>0px</c>
    ///
    /// This registry is global / process-wide. Tests that re-register
    /// values should call <see cref="Reset"/> in their teardown to
    /// avoid bleeding state between cases.
    ///
    /// <para><b>Thread safety (RC8):</b> single-threaded by Unity main-
    /// thread convention. The auto-pump from <c>WevaDocument.Update</c> runs
    /// on the main thread, but author code calling <see cref="Register"/>
    /// from an input callback or async continuation could plausibly arrive
    /// off-thread (the new Input System can dispatch on a background thread
    /// in some configurations). The public mutation entrypoints
    /// (<see cref="Register"/>, <see cref="Reset"/>) call
    /// <see cref="Weva.Diagnostics.UIMainThreadGuard.AssertMainThread"/>
    /// so a misuse fires a debug-build assertion rather than silently
    /// corrupting the backing dictionary.</para>
    /// </summary>
    public static class EnvironmentVariables {
        // Backing store — case-sensitive lookup. CSS env() variable names
        // are spec'd as <custom-ident> with no case normalization.
        static readonly Dictionary<string, string> values = new();

        // Initial set of pre-registered defaults. Captured at static-ctor
        // time so Reset() can restore them after test mutation.
        static readonly Dictionary<string, string> defaults = new() {
            { "safe-area-inset-top",    "0px" },
            { "safe-area-inset-right",  "0px" },
            { "safe-area-inset-bottom", "0px" },
            { "safe-area-inset-left",   "0px" },
        };

        static EnvironmentVariables() {
            Reset();
        }

        /// <summary>
        /// Register or overwrite the value for an environment variable.
        /// <paramref name="value"/> is stored verbatim and substituted
        /// into the CSS value stream when an <c>env(name)</c> reference
        /// resolves — the substitute is re-parsed by the consuming
        /// property, so it should be syntactically what the consumer
        /// expects (e.g. a length like <c>"20px"</c>, a color like
        /// <c>"#ff0000"</c>, etc).
        ///
        /// <para>NG2: a null <paramref name="value"/> is coerced to the
        /// empty string. CSS <c>env()</c> with an empty value is well
        /// defined — it resolves to <c>""</c>, which downstream property
        /// parsers reject — but the dictionary never holds null, so
        /// downstream <c>TryGetValue</c> callers never get a "true with
        /// null" result that would NRE on concatenation.</para>
        /// </summary>
        public static void Register(string name, string value) {
            // RC8: registry is single-threaded by Unity main-thread
            // convention. The dict mutation below would corrupt under
            // a concurrent author callback from a background-thread
            // Input System dispatch.
            Weva.Diagnostics.UIMainThreadGuard.AssertMainThread(nameof(Register));
            if (string.IsNullOrEmpty(name)) return;
            values[name] = value ?? string.Empty;
        }

        /// <summary>
        /// Look up the value for an environment variable. Returns
        /// <c>true</c> when registered (or pre-registered); <c>false</c>
        /// otherwise. A registered value of the empty string still
        /// returns <c>true</c> with <paramref name="value"/> == "".
        /// </summary>
        public static bool TryGetValue(string name, out string value) {
            if (string.IsNullOrEmpty(name)) {
                value = null;
                return false;
            }
            return values.TryGetValue(name, out value);
        }

        /// <summary>
        /// Reset the registry to its initial state — clears any author
        /// registrations and re-seeds the pre-registered
        /// <c>safe-area-inset-*</c> defaults. Intended for tests.
        /// </summary>
        public static void Reset() {
            // RC8: see Register. Reset is typically called from test
            // teardown which is itself main-thread, but assert defensively.
            Weva.Diagnostics.UIMainThreadGuard.AssertMainThread(nameof(Reset));
            values.Clear();
            foreach (var kv in defaults) values[kv.Key] = kv.Value;
        }
    }
}
