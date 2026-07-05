using System.Collections.Generic;
using Weva.Animation;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Profiling;
using Weva.Reactive;

namespace Weva.Layout.Scrolling.Smooth {
    public readonly struct SmoothScrollAnimation {
        public readonly double StartX;
        public readonly double StartY;
        public readonly double TargetX;
        public readonly double TargetY;
        public readonly double Duration;
        public readonly double Elapsed;

        public SmoothScrollAnimation(double startX, double startY, double targetX, double targetY, double duration, double elapsed) {
            StartX = startX;
            StartY = startY;
            TargetX = targetX;
            TargetY = targetY;
            Duration = duration;
            Elapsed = elapsed;
        }

        public bool IsDone => Elapsed >= Duration;
    }

    public sealed class SmoothScrollAnimator {
        public const double DefaultDurationSeconds = 0.25;

        readonly Dictionary<Box, SmoothScrollAnimation> running = new();
        readonly ScrollContainer container;
        // P3: per-instance scratch list reused every Tick. Pre-fix this was a
        // fresh `new List<Box>(running.Keys)` snapshot allocated every frame
        // while any smooth scroll was in flight, plus a lazily-allocated
        // `done` list. Both are now hoisted to instance fields: cleared at the
        // top of Tick, filled during the foreach (Removes deferred to a second
        // pass to avoid mutating the dict mid-enumeration), and never
        // reallocated. Typical concurrent smooth-scroll fan-out is small (one
        // or two containers), so 8 entries pre-sizes the common case without
        // forcing a resize on first use.
        readonly List<Box> scratchRemove = new(8);
        EasingFunction easing;

        public SmoothScrollAnimator(ScrollContainer container) {
            this.container = container;
            this.easing = EaseOutEasing.Instance;
        }

        public EasingFunction Easing {
            get => easing;
            set => easing = value ?? EaseOutEasing.Instance;
        }

        public int RunningCount => running.Count;

        public bool IsAnimating(Box box) {
            return box != null && running.ContainsKey(box);
        }

        public bool TryGet(Box box, out SmoothScrollAnimation anim) {
            if (box == null) { anim = default; return false; }
            return running.TryGetValue(box, out anim);
        }

        public void Cancel(Box box) {
            if (box == null) return;
            running.Remove(box);
        }

        public void CancelAll() {
            running.Clear();
        }

        // Begin animation toward (targetX, targetY) over `duration` seconds. If a
        // previous animation is in-flight on the same Box, the new origin is the
        // current interpolated value at the moment of replacement, and the full
        // duration restarts from there. This matches the "scroll-behavior: smooth"
        // feel users expect: each new wheel burst gets a fresh ramp-down.
        public void Animate(Box box, double targetX, double targetY, double duration) {
            if (box == null) return;
            if (duration <= 0) {
                running.Remove(box);
                var st = container?.GetOrCreate(box);
                if (st != null) {
                    targetX = ScrollMath.Clamp(targetX, 0, st.MaxScrollX);
                    targetY = ScrollMath.Clamp(targetY, 0, st.MaxScrollY);
                    st.ScrollX = targetX;
                    st.ScrollY = targetY;
                    st.BumpVersion();
                    box.ScrollX = targetX;
                    box.ScrollY = targetY;
                }
                return;
            }

            double startX, startY;
            var state = container?.GetOrCreate(box);
            if (state != null) {
                startX = state.ScrollX;
                startY = state.ScrollY;
                targetX = ScrollMath.Clamp(targetX, 0, state.MaxScrollX);
                targetY = ScrollMath.Clamp(targetY, 0, state.MaxScrollY);
            } else {
                startX = box.ScrollX;
                startY = box.ScrollY;
            }
            running[box] = new SmoothScrollAnimation(startX, startY, targetX, targetY, duration, 0);
        }

        public void Animate(Box box, double targetX, double targetY) {
            Animate(box, targetX, targetY, DefaultDurationSeconds);
        }

        public void Tick(double deltaSeconds, InvalidationTracker tracker) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.ScrollTick)) {
                if (running.Count == 0 || deltaSeconds <= 0) return;
                // P3: reuse the instance-scoped removal scratch instead of
                // allocating a fresh `List<Box>(running.Keys)` snapshot and a
                // lazy `done` list per frame. We can't enumerate `running`
                // directly here because the loop body sometimes writes back
                // into the dict (the over-scroll retarget assignment), which
                // would invalidate the Dictionary enumerator. Snapshot the
                // keys into the reusable scratch instead, and reuse the same
                // list for the post-pass removal of finished animations.
                scratchRemove.Clear();
                foreach (var k in running.Keys) scratchRemove.Add(k);
                int snapshotCount = scratchRemove.Count;
                // The first `snapshotCount` entries are the iteration set; we
                // overwrite the list in-place with the removal set as we go.
                int doneCount = 0;
                for (int i = 0; i < snapshotCount; i++) {
                    var box = scratchRemove[i];
                    if (!running.TryGetValue(box, out var anim)) continue;

                    var state = container?.GetOrCreate(box);
                    if (state != null && (anim.TargetX > state.MaxScrollX || anim.TargetY > state.MaxScrollY)) {
                        double t0 = anim.Duration > 0 ? anim.Elapsed / anim.Duration : 1.0;
                        if (t0 > 1.0) t0 = 1.0;
                        double eased0 = easing.Evaluate(t0);
                        double curX0 = anim.StartX + (anim.TargetX - anim.StartX) * eased0;
                        double curY0 = anim.StartY + (anim.TargetY - anim.StartY) * eased0;
                        double newTargetX = ScrollMath.Clamp(anim.TargetX, 0, state.MaxScrollX);
                        double newTargetY = ScrollMath.Clamp(anim.TargetY, 0, state.MaxScrollY);
                        double remaining = anim.Duration - anim.Elapsed;
                        if (remaining < 0) remaining = 0;
                        anim = new SmoothScrollAnimation(curX0, curY0, newTargetX, newTargetY, remaining, 0);
                        running[box] = anim;
                    }

                    double newElapsed = anim.Elapsed + deltaSeconds;
                    double t = anim.Duration > 0 ? newElapsed / anim.Duration : 1.0;
                    bool finished = t >= 1.0;
                    if (finished) t = 1.0;
                    double eased = easing.Evaluate(t);

                    double curX = anim.StartX + (anim.TargetX - anim.StartX) * eased;
                    double curY = anim.StartY + (anim.TargetY - anim.StartY) * eased;

                    if (state != null) {
                        curX = ScrollMath.Clamp(curX, 0, state.MaxScrollX);
                        curY = ScrollMath.Clamp(curY, 0, state.MaxScrollY);
                        state.ScrollX = curX;
                        state.ScrollY = curY;
                        state.BumpVersion();
                    }
                    box.ScrollX = curX;
                    box.ScrollY = curY;
                    if (tracker != null && box.Element != null) {
                        tracker.MarkDirty(box.Element, InvalidationKind.Paint);
                    }

                    if (finished) {
                        // Pack the removal set into the front of the scratch
                        // list as we discover finished animations. Safe because
                        // doneCount <= i, so we never overwrite an entry we
                        // still need to read this iteration.
                        scratchRemove[doneCount++] = box;
                    } else {
                        running[box] = new SmoothScrollAnimation(
                            anim.StartX, anim.StartY, anim.TargetX, anim.TargetY, anim.Duration, newElapsed);
                    }
                }
                for (int i = 0; i < doneCount; i++) running.Remove(scratchRemove[i]);
                scratchRemove.Clear();
            }
        }

        public static bool IsSmooth(Box box) {
            if (box?.Style == null) return false;
            string raw = box.Style.Get("scroll-behavior");
            if (string.IsNullOrEmpty(raw)) return false;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "smooth");
        }
    }
}
