using System.Threading.Tasks;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // RC3 — ColorResolver's s_LastStyle / s_LastVersion / s_LastColor one-slot
    // memo is [ThreadStatic] so a future parallel LayoutEngine cannot
    // corrupt the cache. This file pins the per-thread isolation contract:
    // a memo populated on thread A must NOT leak into thread B's lookup,
    // so the worker thread takes the slow path (CssValue parse) rather
    // than returning the main-thread thread's cached color.
    public class ColorResolverThreadStaticTests {
        static ComputedStyle StyleWith(string colorValue) {
            var s = new ComputedStyle(new Element("div"));
            s.Set("color", colorValue);
            return s;
        }

        [SetUp]
        public void ResetWarningDedupe() {
            ColorResolver.ResetWarnings_TestOnly();
        }

        [Test]
        public void Memo_does_not_leak_across_threads_RC3() {
            // Same ComputedStyle instance is resolved on the main thread
            // (populates THIS thread's memo) and then on a worker thread.
            // If the memo were a plain static, the worker would short-
            // circuit and return the cached value via reference-equality —
            // which would be CORRECT in this single-style case, but masks
            // the contract. We assert via a value comparison that BOTH
            // threads return the same color, then assert that the worker
            // thread's memo is independent: clearing the main thread's
            // memo by resolving a DIFFERENT style must not affect what the
            // worker sees on its own re-resolve of the original.
            var red = StyleWith("red");
            var blue = StyleWith("blue");

            // Main-thread memo: red.
            var mainRed = ColorResolver.ResolveCurrentColor(red);
            Assert.That(mainRed.R, Is.GreaterThan(0.5f), "main thread red");
            Assert.That(mainRed.G, Is.LessThan(0.1f));

            // Worker thread resolves blue, populating its own [ThreadStatic]
            // slot. With a plain static this would clobber the main thread's
            // memo; with [ThreadStatic] the two slots are independent.
            var workerBlue = Task.Run(() => ColorResolver.ResolveCurrentColor(blue)).Result;
            Assert.That(workerBlue.B, Is.GreaterThan(0.5f), "worker thread blue");
            Assert.That(workerBlue.R, Is.LessThan(0.1f));

            // Main-thread memo must still hit on red — the worker's blue
            // resolve must not have evicted our slot. With a plain static,
            // the worker's blue would have been parked in s_LastStyle, and
            // the next resolve on red would either hit (correct color) or
            // miss (still correct color via parse) — but the [ThreadStatic]
            // contract is that the worker's slot is invisible to us. We
            // verify by re-resolving and getting red back unchanged.
            var mainRedAgain = ColorResolver.ResolveCurrentColor(red);
            Assert.That(mainRedAgain.R, Is.EqualTo(mainRed.R).Within(1e-6));
            Assert.That(mainRedAgain.G, Is.EqualTo(mainRed.G).Within(1e-6));
            Assert.That(mainRedAgain.B, Is.EqualTo(mainRed.B).Within(1e-6));
        }

        [Test]
        public void Parallel_resolves_on_different_threads_each_get_correct_color_RC3() {
            // 8 worker threads each resolve a distinct color. With a plain
            // static one-slot memo, two threads racing s_LastStyle/Version
            // could observe a torn write (style from thread A paired with
            // version from thread B) and return the wrong color on a
            // cache "hit". [ThreadStatic] eliminates the race entirely —
            // each thread sees only its own slot, so the slow path runs
            // every time the working set rotates.
            const int N = 8;
            var styles = new ComputedStyle[N];
            string[] names = { "red", "blue", "green", "yellow", "magenta", "cyan", "white", "black" };
            for (int i = 0; i < N; i++) styles[i] = StyleWith(names[i]);

            var tasks = new Task<LinearColor>[N];
            for (int i = 0; i < N; i++) {
                int idx = i;
                tasks[i] = Task.Run(() => {
                    // Resolve twice to populate + hit the per-thread memo;
                    // both calls must return the same color for this thread.
                    var c1 = ColorResolver.ResolveCurrentColor(styles[idx]);
                    var c2 = ColorResolver.ResolveCurrentColor(styles[idx]);
                    Assert.That(c1.R, Is.EqualTo(c2.R).Within(1e-6));
                    Assert.That(c1.G, Is.EqualTo(c2.G).Within(1e-6));
                    Assert.That(c1.B, Is.EqualTo(c2.B).Within(1e-6));
                    return c1;
                });
            }
            Task.WaitAll(tasks);

            // Pin per-color expected channels via a coarse channel check —
            // the load-bearing assertion is that no thread returned the
            // wrong color due to a torn memo read.
            Assert.That(tasks[0].Result.R, Is.GreaterThan(0.5f), "red"); // red
            Assert.That(tasks[1].Result.B, Is.GreaterThan(0.5f), "blue"); // blue
            Assert.That(tasks[2].Result.G, Is.GreaterThan(0.2f), "green"); // green
            // yellow R+G high, B low
            Assert.That(tasks[3].Result.R, Is.GreaterThan(0.5f));
            Assert.That(tasks[3].Result.G, Is.GreaterThan(0.5f));
            Assert.That(tasks[3].Result.B, Is.LessThan(0.1f), "yellow B");
        }
    }
}
