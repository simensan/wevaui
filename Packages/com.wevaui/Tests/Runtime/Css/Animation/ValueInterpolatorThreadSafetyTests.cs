using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Animation;
using Weva.Css.Values;
using Weva.Diagnostics;

namespace Weva.Tests.Css.Animation {
    // RC4 — ValueInterpolator's transformFnCache, identityListCache, and
    // the reusable transformOutSb StringBuilder are single-threaded by
    // Unity main-thread convention. InterpolateTransform now calls
    // UIMainThreadGuard.AssertMainThread to fire a debug-build assertion
    // when re-entered off the main thread.
    public class ValueInterpolatorThreadSafetyTests {
        [Test]
        public void Interpolate_transform_on_main_thread_succeeds_RC4() {
            // Pin the documented behavior: a normal main-thread interpolation
            // must not fire the AssertMainThread invariant. We force the
            // captured main-thread id to the current thread so the assertion
            // sees a match regardless of whether RuntimeInitializeOnLoadMethod
            // has run yet.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId);
            try {
                ValueInterpolator.ResetCaches_TestOnly();
                var ctx = LengthContext.Default;
                var result = ValueInterpolator.Interpolate(
                    "rotate(0deg)", "rotate(360deg)", 0.5, PropertyKind.Transform, ctx);
                Assert.That(result, Is.Not.Null.And.Not.Empty);
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                ValueInterpolator.ResetCaches_TestOnly();
            }
        }

#if UNITY_EDITOR
        [Test]
        public void Interpolate_transform_off_main_thread_fires_assertion_RC4() {
            // RC-1: positive non-colliding wrong-id (currentId + 100_000) avoids
            // AssertMainThread's `captured < 0` early-exit. Previously this used
            // `-currentId - 1000` which silently skipped the assertion.
            // Editor-only: the [Conditional("UNITY_EDITOR")] /
            // [Conditional("DEVELOPMENT_BUILD")] guards on
            // UIMainThreadGuard.AssertMainThread compile it out in plain
            // release builds. In editor, the assertion routes through
            // Debug.Assert -> LogType.Assert and we can observe it.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId + 100_000);
            try {
                ValueInterpolator.ResetCaches_TestOnly();
                LogAssert.Expect(LogType.Assert,
                    new System.Text.RegularExpressions.Regex("InterpolateTransform"));
                var ctx = LengthContext.Default;
                ValueInterpolator.Interpolate(
                    "rotate(0deg)", "rotate(360deg)", 0.5, PropertyKind.Transform, ctx);
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                ValueInterpolator.ResetCaches_TestOnly();
            }
        }
#endif
    }
}
