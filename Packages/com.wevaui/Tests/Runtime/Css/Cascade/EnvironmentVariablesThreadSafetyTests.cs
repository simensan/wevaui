using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Cascade;
using Weva.Diagnostics;

namespace Weva.Tests.Css.Cascade {
    // RC8 — EnvironmentVariables.Register / Reset are single-threaded by
    // Unity main-thread convention. The auto-pump from WevaDocument.Update is
    // main-thread, but author callbacks (e.g. from an Input System dispatch
    // on a background thread in some configs) could plausibly arrive off-
    // thread. Both setters call UIMainThreadGuard.AssertMainThread.
    public class EnvironmentVariablesThreadSafetyTests {
        [SetUp]
        public void SetUp() {
            EnvironmentVariables.Reset();
        }

        [TearDown]
        public void TearDown() {
            EnvironmentVariables.Reset();
        }

        [Test]
        public void Register_on_main_thread_succeeds_RC8() {
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId);
            try {
                EnvironmentVariables.Register("custom-inset", "12px");
                Assert.That(EnvironmentVariables.TryGetValue("custom-inset", out var v), Is.True);
                Assert.That(v, Is.EqualTo("12px"));
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
            }
        }

#if UNITY_EDITOR
        // RC-1: positive non-colliding wrong-id (currentId + 100_000) avoids
        // AssertMainThread's `captured < 0` early-exit. Previously this used
        // `-currentId - 1000` which silently skipped the assertion. See
        // CSS_OPEN_GAPS.md RC-1 history.
        [Test]
        public void Register_off_main_thread_fires_assertion_RC8() {
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId + 100_000);
            try {
                LogAssert.Expect(LogType.Assert,
                    new System.Text.RegularExpressions.Regex("Register"));
                EnvironmentVariables.Register("custom-inset", "12px");
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
            }
        }

        [Test]
        public void Reset_off_main_thread_fires_assertion_RC8() {
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId + 100_000);
            try {
                LogAssert.Expect(LogType.Assert,
                    new System.Text.RegularExpressions.Regex("Reset"));
                EnvironmentVariables.Reset();
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                // Reset state from main-thread for clean teardown
                EnvironmentVariables.Reset();
            }
        }
#endif
    }
}
