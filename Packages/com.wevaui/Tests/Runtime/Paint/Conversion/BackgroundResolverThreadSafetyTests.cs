using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Cascade;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // RC5 — BackgroundResolver's gradientCache / gradientBrushCache /
    // gradientNoCache / argsListPool are single-threaded by Unity main-thread
    // convention. The pool's pop/push pair is the highest re-entrancy risk
    // (a contended pop could double-rent the same List<string>). The public
    // entrypoint TryParseGradient now asserts main-thread via UIMainThreadGuard.
    public class BackgroundResolverThreadSafetyTests {
        [SetUp]
        public void ResetCaches() {
            BackgroundResolver.ResetCaches_TestOnly();
        }

        [Test]
        public void Resolve_gradient_on_main_thread_succeeds_RC5() {
            // Pin the documented behavior: a normal main-thread gradient
            // resolution must NOT fire the AssertMainThread invariant.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId);
            try {
                var s = new ComputedStyle(new Element("div"));
                s.Set("background-image", "linear-gradient(45deg, red, blue)");
                var brush = BackgroundResolver.ResolveBackground(s, new Weva.Paint.Rect(0, 0, 100, 50));
                Assert.That(brush, Is.Not.Null);
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                BackgroundResolver.ResetCaches_TestOnly();
            }
        }

#if UNITY_EDITOR
        [Test]
        public void Resolve_gradient_off_main_thread_fires_assertion_RC5() {
            // RC-1: positive non-colliding wrong-id avoids AssertMainThread's
            // `captured < 0` early-exit. See CSS_OPEN_GAPS.md RC-1 history.
            int prev = UIMainThreadGuard.OverrideMainThreadId_TestOnly(
                System.Threading.Thread.CurrentThread.ManagedThreadId + 100_000);
            try {
                var s = new ComputedStyle(new Element("div"));
                s.Set("background-image", "linear-gradient(45deg, red, blue)");
                LogAssert.Expect(LogType.Assert,
                    new System.Text.RegularExpressions.Regex("TryParseGradient"));
                BackgroundResolver.ResolveBackground(s, new Weva.Paint.Rect(0, 0, 100, 50));
            } finally {
                UIMainThreadGuard.OverrideMainThreadId_TestOnly(prev);
                BackgroundResolver.ResetCaches_TestOnly();
            }
        }
#endif
    }
}
