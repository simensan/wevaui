using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Diagnostics;

namespace Weva.Tests.Diagnostics {
    // RC family — UIMainThreadGuard is the shared "main-thread-only"
    // assertion helper used by ColorResolver, ValueInterpolator,
    // BackgroundResolver, TextRunSnapshotCache, and EnvironmentVariables.
    //
    // The guard captures the Unity main thread's managed id at
    // SubsystemRegistration and asserts that subsequent mutations of
    // process-static state happen on that thread. The captured id is
    // exposed for tests so the assert mechanism can be exercised without
    // needing the actual Unity main thread.
    public class UIMainThreadGuardTests {
        [Test]
        public void MainThreadId_is_either_minus_one_or_a_real_thread_id() {
            int id = UIMainThreadGuard.MainThreadId;
            // -1 means RuntimeInitializeOnLoadMethod hasn't fired (pure-C#
            // NUnit runner). Otherwise the captured id must be positive.
            // Either is a valid state — the production path's assertion
            // is a no-op when id < 0.
            Assert.That(id == -1 || id > 0, Is.True, "MainThreadId = " + id);
        }

        [Test]
        public void AssertMainThread_is_noop_when_id_is_minus_one_explicit_override() {
            // Force the "not initialised" state. The assertion must be a
            // no-op (no log, no throw). This pins the contract that pure-
            // C# test runners running before RuntimeInitializeOnLoadMethod
            // do not get spurious failures.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(-1);
            try {
                Assert.DoesNotThrow(() => UIMainThreadGuard.AssertMainThread("test"));
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
            }
        }

        [Test]
        public void AssertMainThread_passes_when_caller_thread_matches_captured() {
            // Set the captured id to the current thread, then call —
            // must succeed silently. No LogAssert.Expect because the
            // [Conditional] guards on UNITY_EDITOR / DEVELOPMENT_BUILD
            // mean the call may compile to a no-op in some builds.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(Thread.CurrentThread.ManagedThreadId);
            try {
                Assert.DoesNotThrow(() => UIMainThreadGuard.AssertMainThread("test"));
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
            }
        }

#if UNITY_EDITOR
        [Test]
        public void AssertMainThread_fires_when_caller_thread_does_not_match_captured_RC_family() {
            // RC-1: capture a positive non-colliding wrong-id so AssertMainThread's
            // `captured < 0` early-exit (the "not yet initialised" guard) doesn't
            // short-circuit before the mismatch can fire. Previously this used
            // `-currentId - 1000` which always hit the early-exit and silently
            // skipped the assertion. Editor-only because in release builds the
            // call compiles out via [Conditional("UNITY_EDITOR")] /
            // [Conditional("DEVELOPMENT_BUILD")].
            int wrongId = Thread.CurrentThread.ManagedThreadId + 100_000;
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(wrongId);
            try {
                LogAssert.Expect(LogType.Assert,
                    new System.Text.RegularExpressions.Regex(
                        @"Weva: process-static mutation on non-main thread"));
                UIMainThreadGuard.AssertMainThread("test-site");
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
            }
        }
#endif
    }
}
