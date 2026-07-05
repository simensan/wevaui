using System;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Reactive;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling.Smooth {
    // P3 + P6 (animator half) regression coverage. Pre-fix `Tick` allocated
    // - `new List<Box>(running.Keys)` (the iteration snapshot), and
    // - `done ??= new List<Box>()` (the finished-this-frame collector)
    // every frame while any smooth scroll was in flight. Both are now hoisted
    // to a single per-instance `scratchRemove` list reused across Ticks.
    //
    // Tracker: P3 and P6 in CODE_AUDIT_FINDINGS.md.
    public class SmoothScrollAnimatorScratchTests {
        static SmoothScrollAnimatorScratchTests() { SmoothScrollProperties.EnsureRegistered(); }

        static (Box vp, ScrollContainer sc, ScrollState state) BuildSmoothViewport(double childHeight = 500) {
            string css = $".vp {{ overflow: auto; height: 100px; width: 200px; scroll-behavior: smooth; }} .child {{ height: {childHeight}px; }}";
            string html = "<div class=\"vp\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.GetAttribute("class");
                if (c == "vp") { vp = b; break; }
            }
            return (vp, sc, sc.Get(vp));
        }

        [Test]
        public void Tick_steady_state_allocates_near_zero_P3() {
            // Allocation parity: once an animation is in flight and the
            // per-instance scratch list has been pre-sized by the first Tick,
            // subsequent Ticks should allocate essentially nothing per frame.
            // Pre-fix this allocated `running.Count * 8 + List header` bytes
            // every frame (the keys snapshot) plus a `done` list each frame
            // any animation finished. We exercise an infinite-duration smooth
            // scroll so the running set stays populated for the entire run.
            var (vp, sc, _) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            // Long duration so Tick(0.001) never finishes — keeps `running`
            // populated across all 100 frames.
            anim.Animate(vp, 0, 50, 100.0);

            // Warmup: prime the scratch list capacity and JIT.
            for (int i = 0; i < 5; i++) anim.Tick(0.001, null);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int frames = 100;
            for (int i = 0; i < frames; i++) anim.Tick(0.001, null);
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            long perFrame = delta / frames;
            TestContext.WriteLine(
                $"P3 steady-state alloc over {frames} Ticks = {delta} B (~{perFrame} B/frame)");
            // Pre-fix each Tick allocated at minimum `new List<Box>(1).Keys`:
            // List header (~24 B) + element array (~16 B header + Box ref).
            // With even 1 running animation that's >= 40 B/frame * 100 = 4 KB.
            // 32 B/frame is a tight bound that still catches a regression to
            // the snapshot path.
            Assert.That(perFrame, Is.LessThan(32),
                "Tick steady-state regressed — scratch list may be re-allocating");
        }

        [Test]
        public void Tick_finished_animations_removed_via_scratch_P3_P6() {
            // Functional parity: the finished-this-tick removal sweep used to
            // queue boxes into a lazy `done` list and remove them post-pass.
            // The new path packs finished boxes into the front of the
            // scratchRemove list and Removes from `running` after the
            // foreach. End state must be identical: the running dict empties
            // and IsAnimating reads false.
            var (vp, sc, state) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 40, 0.10);

            Assert.That(anim.IsAnimating(vp), Is.True);
            Assert.That(anim.RunningCount, Is.EqualTo(1));
            // Mid-flight: not yet removed.
            anim.Tick(0.05, null);
            Assert.That(anim.IsAnimating(vp), Is.True);
            Assert.That(anim.RunningCount, Is.EqualTo(1));
            // Past full duration: removal sweep must clear the entry.
            anim.Tick(0.10, null);
            Assert.That(anim.IsAnimating(vp), Is.False);
            Assert.That(anim.RunningCount, Is.EqualTo(0));
            Assert.That(state.ScrollY, Is.EqualTo(40).Within(0.5));
        }

        [Test]
        public void Tick_multiple_simultaneous_animations_all_complete_P3() {
            // Functional parity stress: three concurrent smooth-scrolls finish
            // in the same Tick. The scratch-list packing has to handle multiple
            // entries being moved into the front of the list mid-iteration
            // without losing any. Pre-fix this used a separate `done` list per
            // call.
            string css = ".vp { overflow: auto; height: 100px; width: 200px; scroll-behavior: smooth; } .child { height: 500px; }";
            string html = "<div>" +
                "<div class=\"vp\" id=\"a\"><div class=\"child\"></div></div>" +
                "<div class=\"vp\" id=\"b\"><div class=\"child\"></div></div>" +
                "<div class=\"vp\" id=\"c\"><div class=\"child\"></div></div>" +
                "</div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box a = null, b = null, c = null;
            foreach (var box in AllBoxes(root)) {
                var id = box.Element?.GetAttribute("id");
                if (id == "a") a = box;
                else if (id == "b") b = box;
                else if (id == "c") c = box;
            }
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(a, 0, 50, 0.10);
            anim.Animate(b, 0, 60, 0.10);
            anim.Animate(c, 0, 70, 0.10);
            Assert.That(anim.RunningCount, Is.EqualTo(3));

            anim.Tick(0.10, null);
            Assert.That(anim.RunningCount, Is.EqualTo(0));
            Assert.That(sc.Get(a).ScrollY, Is.EqualTo(50).Within(0.5));
            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(60).Within(0.5));
            Assert.That(sc.Get(c).ScrollY, Is.EqualTo(70).Within(0.5));
        }
    }
}
