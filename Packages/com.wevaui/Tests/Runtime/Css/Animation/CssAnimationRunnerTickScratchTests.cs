using System;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // P4 regression coverage. Pre-fix `TickInternal` lazily allocated four
    // `List<(Element, string)>` instances per call (two for the transitions
    // sweep, two for the animations sweep) whenever any record finished that
    // tick. The new path hoists them to per-instance `scratchDoneTransitions`
    // and `scratchDoneAnimations` fields, cleared at the top of each sweep
    // and reused across Ticks.
    //
    // Tracker: P4 in CODE_AUDIT_FINDINGS.md.
    public class CssAnimationRunnerTickScratchTests {
        const double Eps = 1e-3;

        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner(string css = null) {
            var clock = new FakeUIClock();
            var sheets = css == null ? System.Array.Empty<Stylesheet>() : new[] { CssParser.Parse(css) };
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, sheets, clock);
            return (runner, clock);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [Test]
        public void Transition_completion_sweep_uses_reused_scratch_P4() {
            // Functional parity: when a transition completes mid-Tick the
            // record must be removed from `transitions`, `transitionsByElement`,
            // and `transitioningElements`. The post-pass loop (now driven by
            // the per-instance scratchDoneTransitions list rather than a
            // lazily-allocated `done`) must produce identical end state.
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 0.2s linear"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 0.2s linear"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1));

            // Tick mid-flight: NO sweep entry; scratch stays empty for this
            // call.
            clock.Set(0.1);
            runner.Tick(0.1);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1));

            // Tick past the end of the transition: sweep clears the record.
            clock.Set(0.25);
            runner.Tick(0.25);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0));
            Assert.That(runner.TransitionsByElementCount, Is.EqualTo(0));
            Assert.That(runner.TransitioningElementsCount, Is.EqualTo(0));

            // Re-arming a transition and completing it again uses the SAME
            // scratch list; behaviour must repeat.
            runner.OnStyleChange(e, next, prev);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1));
            clock.Set(0.6);
            runner.Tick(0.6);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0));
            Assert.That(runner.TransitionsByElementCount, Is.EqualTo(0));
            Assert.That(runner.TransitioningElementsCount, Is.EqualTo(0));
        }

        [Test]
        public void Animation_completion_sweep_uses_reused_scratch_P4() {
            // Functional parity for the animation sweep: a finite-iteration
            // animation past its end window must be evicted from
            // `animations`, `animationsByElement`, and `animatedElements` via
            // the scratchDoneAnimations path.
            var (runner, clock) = MakeRunner(
                "@keyframes fade { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "fade"),
                ("animation-duration", "0.2s"),
                ("animation-iteration-count", "1"),
                ("animation-fill-mode", "none"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));

            // Mid-flight: still active, sweep finds nothing to remove.
            clock.Set(0.1);
            runner.Tick(0.1);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));

            // Past the end: sweep evicts via scratch list.
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(0));
            Assert.That(runner.AnimationsByElementCount, Is.EqualTo(0));
            Assert.That(runner.AnimatedElementsCount, Is.EqualTo(0));
        }

        [Test]
        public void Tick_steady_state_allocates_near_zero_P4() {
            // Allocation parity: a long-running infinite animation drives
            // TickInternal every frame WITHOUT ever finishing — exercises the
            // hot-path foreach over `animations` and confirms the per-instance
            // scratchDone lists don't grow / reallocate. Pre-fix this path
            // didn't allocate the `done` list directly (only when records
            // finish), so the regression we're guarding is "we accidentally
            // started reallocating scratchDoneAnimations every frame" — e.g.
            // if a future refactor reverts to lazy `new List<>` semantics.
            var (runner, clock) = MakeRunner(
                "@keyframes pulse { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "pulse"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);

            // Warmup.
            for (int i = 0; i < 30; i++) {
                double t = 0.01 * i;
                clock.Set(t);
                runner.Tick(t);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int frames = 200;
            for (int i = 0; i < frames; i++) {
                double t = 1.0 + 0.005 * i;
                clock.Set(t);
                runner.Tick(t);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            long perFrame = delta / frames;
            TestContext.WriteLine(
                $"P4 steady-state alloc over {frames} Ticks = {delta} B " +
                $"(~{perFrame} B/frame)");
            // Per-frame budget: ValueInterpolator typed-overlay lerps and
            // the sample dictionary update may allocate a handful of bytes;
            // 128 B/frame is a tight bound that catches the regression
            // (lazy `new List<(Element,string)>` is ~40 B + entry array, so
            // a regression would push perFrame well above this).
            Assert.That(perFrame, Is.LessThan(128),
                "Tick steady-state regressed — verify scratchDone lists are reused");
        }

        [Test]
        public void Tick_burst_completions_steady_state_allocates_near_zero_P4() {
            // Bursty completion path: many transitions completing across
            // repeated Ticks must not re-allocate the scratch list. We churn
            // the same element through a series of short transitions, each of
            // which completes within a single Tick.
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var a = Style(e, ("opacity", "0"), ("transition", "opacity 0.05s linear"));
            var b = Style(e, ("opacity", "1"), ("transition", "opacity 0.05s linear"));

            // Warmup: complete a few transitions to size the scratch list.
            for (int i = 0; i < 10; i++) {
                runner.OnStyleChange(e, i % 2 == 0 ? a : b, i % 2 == 0 ? b : a);
                clock.Set(i * 0.1 + 0.06);
                runner.Tick(i * 0.1 + 0.06);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int cycles = 50;
            double t0 = 100.0;
            for (int i = 0; i < cycles; i++) {
                runner.OnStyleChange(e, i % 2 == 0 ? a : b, i % 2 == 0 ? b : a);
                t0 += 0.1;
                clock.Set(t0);
                runner.Tick(t0);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            long perCycle = delta / cycles;
            TestContext.WriteLine(
                $"P4 burst-completion alloc over {cycles} cycles = {delta} B " +
                $"(~{perCycle} B/cycle)");
            // Each cycle starts a transition (some unavoidable allocations
            // for the RunningTransitionRecord plus the typed-value parsing
            // path) and completes it (the scratch list reuse should keep
            // sweep allocation at zero). A generous 2 KB/cycle bound
            // catches a regression that allocates a fresh sweep list per
            // tick on top of the per-start record allocation.
            Assert.That(perCycle, Is.LessThan(2048),
                "Burst-completion regressed — verify scratchDoneTransitions reuse");
        }
    }
}
