using System.Threading;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace Weva.Diagnostics {
    // Captures the Unity main thread's managed thread id at subsystem
    // registration and exposes a debug-build assertion helper for the
    // engine's process-static caches and registries.
    //
    // The vast majority of Weva's mutable static state (paint caches,
    // gradient memos, animation interpolator scratch, env() registry, etc.)
    // is single-threaded by Unity convention — Unity invokes update / paint
    // callbacks on a single main thread, and the engine relies on that to
    // avoid the cost of locks on hot paths. None of those sites previously
    // *checked* the convention, so a misuse from an Addressables completion
    // continuation (which CAN land off the main thread in some configs) or
    // an Input System background callback corrupts the caches silently.
    //
    // This helper provides a `[Conditional("UNITY_EDITOR")]` /
    // `[Conditional("DEVELOPMENT_BUILD")]` assertion site that fires in
    // editor & development builds and compiles out in release. Use it at
    // the public mutation entrypoints of process-static state.
    //
    // The captured id is initialised via `[RuntimeInitializeOnLoadMethod]`
    // with `SubsystemRegistration` — earliest available stage that still
    // runs on the main thread, before any other engine code.
    public static class UIMainThreadGuard {
        // -1 = not initialised yet (e.g. the helper was hit from a pure-C#
        // test runner before Unity's RuntimeInitializeOnLoadMethod fires).
        // The assertion is a no-op in that case so headless NUnit doesn't
        // get spurious failures during static-ctor chains.
        static int s_MainThreadId = -1;

#if UNITY_5_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#endif
        static void CaptureMainThreadId() {
            s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// Returns the captured main thread id, or -1 if the helper hasn't
        /// been initialised yet (pure-C# test runner before
        /// RuntimeInitializeOnLoadMethod fires).
        /// </summary>
        public static int MainThreadId => s_MainThreadId;

        /// <summary>
        /// Asserts that the caller is on the Unity main thread. No-op when
        /// the helper hasn't been initialised yet (pure-C# tests). Compiles
        /// out entirely in release builds.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void AssertMainThread(string callsite = null) {
            int captured = s_MainThreadId;
            if (captured < 0) return; // not yet initialised — pure-C# test
            int actual = Thread.CurrentThread.ManagedThreadId;
            if (actual == captured) return;
            // Debug.LogAssertion routes to LogType.Assert — same channel as
            // Debug.Assert(false, ...) but more deterministic across Unity
            // versions (Debug.Assert is gated on the UNITY_ASSERTIONS define
            // which can be flipped off in some build configs). LogAssertion
            // is always on. The message includes both ids so a misuse
            // pinpoints which off-thread caller fired.
#if UNITY_5_3_OR_NEWER
            Debug.LogAssertion(
                "Weva: process-static mutation on non-main thread ("
                + (callsite ?? "<unknown>")
                + "). actualThreadId=" + actual
                + ", mainThreadId=" + captured
                + ". The affected cache/registry is documented as single-threaded "
                + "by Unity main-thread convention.");
#endif
        }

        /// <summary>
        /// Test-only hook so a unit test running on a worker thread can
        /// temporarily masquerade as the main thread (or vice-versa) without
        /// needing the real Unity main thread. Returns the previous value so
        /// the test can restore it in TearDown.
        /// </summary>
        internal static int OverrideMainThreadId_TestOnly(int newId) {
            int prev = s_MainThreadId;
            s_MainThreadId = newId;
            return prev;
        }
    }
}
