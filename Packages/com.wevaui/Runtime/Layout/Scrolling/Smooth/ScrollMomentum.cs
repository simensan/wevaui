using System;
using System.Collections.Generic;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling.Snap;
using Weva.Reactive;

namespace Weva.Layout.Scrolling.Smooth {
    // Inertial (momentum / flick) scroll for touch- and gamepad-driven scroll
    // containers.  After a drag ends with non-zero velocity the container
    // continues scrolling with exponential-decay deceleration, then settles on
    // a snap point (if any).
    //
    // Decay model — iOS-style continuous exponential decay:
    //
    //   position(t) = p0 + v0 * ∑ pow(decayBase, k) * dt   (numerical Euler)
    //   which collapses to:
    //   velocity(t) = v0 * pow(decayBase, t_ms)
    //
    // with decayBase = 0.998 per millisecond (~half-life 347 ms, fully stopped
    // at ~2 s for typical flick velocities), stop threshold 5 px/s.
    //
    // Rubber-band overscroll (iOS-style):
    //
    //   During an active touch drag, pulling past an edge applies a diminishing
    //   resistance formula from Apple's UIScrollView implementation:
    //
    //     visualOverscroll = (1 - 1/(rawOverscroll * C / dimension + 1)) * dimension
    //
    //   with C ≈ 0.55 (RubberBandC) and dimension = viewport extent on that axis.
    //   rawOverscroll is accumulated separately; ScrollX/Y may temporarily leave
    //   [0, MaxScroll] only during an active armed drag or a live spring-back.
    //
    // Spring-back (critically damped):
    //
    //   When released while overscrolled, or when a momentum glide reaches an
    //   edge with remaining velocity, the content springs back to the boundary
    //   using a critically-damped oscillator:
    //
    //     x(t) = x0 * (1 + w*t) * exp(-w*t)
    //
    //   with w = SpringOmega (≈ 12/s). The spring always terminates exactly at
    //   the boundary (final frame is snapped to 0 overscroll).
    //
    // All time is injected as double seconds (same contract as
    // SmoothScrollAnimator). No UnityEngine.Time or DateTime usage.
    //
    // Thread safety: none — single-threaded game-loop use only.
    public sealed class ScrollMomentum {
        // ------------------------------------------------------------------ //
        //  Tunables (public static so tests can override them per-suite if    //
        //  needed without recreating the instance; same pattern as            //
        //  SmoothScrollAnimator.DefaultDurationSeconds).                      //
        // ------------------------------------------------------------------ //

        // Velocity decay factor per millisecond.  0.998 ≈ iOS momentum feel.
        // At this rate:  after 100 ms → 0.998^100 ≈ 0.819 of original speed.
        //                after 347 ms → 0.998^347 ≈ 0.500 (half-life).
        //                after 1000 ms → 0.998^1000 ≈ 0.135.
        public static double DecayBasePerMs = 0.998;

        // Glide stops when |velocity| drops below this threshold (px/s).
        public static double StopThresholdPxPerSec = 5.0;

        // Velocity is estimated over the trailing window of this duration.
        // Only samples within this window contribute to the estimate.
        public static double VelocityWindowSeconds = 0.100;  // 100 ms

        // Maximum number of samples kept in the ring buffer per container.
        public const int RingCapacity = 64;

        // Rubber-band resistance coefficient (iOS UIScrollView default: 0.55).
        // Higher values = more resistance; 0 = no rubber-banding (hard clamp).
        // Formula: visual = (1 - 1/(raw * C / dim + 1)) * dim
        public static double RubberBandC = 0.55;

        // Natural frequency (rad/s) for the critically-damped spring-back.
        // w = 12/s gives a ~250 ms settle time — snappy but not jarring.
        // Critically damped: x(t) = x0 * (1 + w*t) * exp(-w*t)
        public static double SpringOmega = 12.0;

        // Maximum overshoot (px) when a momentum glide converts edge velocity
        // into an overshoot before spring-back. Capped independently of velocity
        // to keep the feel bounded. Formula: overshoot = min(v/w, OvershootCapPx).
        public static double OvershootCapPx = 80.0;

        // Spring-back is considered finished when |overscroll| < this threshold.
        public static double SpringStopThresholdPx = 0.15;

        // ------------------------------------------------------------------ //
        //  Internal state                                                      //
        // ------------------------------------------------------------------ //

        readonly ScrollContainer container;

        // Per-box active glide state.
        readonly Dictionary<Box, GlideState> glides = new Dictionary<Box, GlideState>();

        // Per-box active spring-back state (runs after overscroll drag release
        // or after a glide hits an edge with remaining velocity).
        readonly Dictionary<Box, SpringState> springs = new Dictionary<Box, SpringState>();

        // Per-box sample ring buffer for velocity estimation.
        readonly Dictionary<Box, RingBuffer> rings = new Dictionary<Box, RingBuffer>();

        // Scratch list to avoid allocation during Tick.
        readonly List<Box> scratchDone = new List<Box>(8);

        // Optional animator — when set and the container has scroll-snap-type,
        // the final approach to a snap point is delegated to this animator so
        // the landing is smooth rather than a hard cut.
        SmoothScrollAnimator snapAnimator;

        // Optional snap resolver — required for snap integration.
        SnapResolver snapResolver;

        public ScrollMomentum(ScrollContainer container) {
            this.container = container;
        }

        public SmoothScrollAnimator SnapAnimator {
            get => snapAnimator;
            set => snapAnimator = value;
        }

        public SnapResolver SnapResolver {
            get => snapResolver;
            set => snapResolver = value;
        }

        public int GlideCount => glides.Count;

        // Returns true when a glide OR a spring-back is active on the given box.
        public bool IsGliding(Box box) {
            return box != null && (glides.ContainsKey(box) || springs.ContainsKey(box));
        }

        // True when a spring-back (post-overscroll settle) is active on this box.
        public bool IsSpringBack(Box box) {
            return box != null && springs.ContainsKey(box);
        }

        // ------------------------------------------------------------------ //
        //  Rubber-band formula                                                 //
        // ------------------------------------------------------------------ //

        // Converts a raw overscroll amount (px past the boundary) to a visual
        // overscroll using the iOS rubber-band formula:
        //
        //   visual = (1 - 1/(raw * C / dim + 1)) * dim
        //
        // where dim is the viewport extent on the overscrolling axis.
        // Returns a non-negative value in [0, dim).  For dim <= 0, falls back
        // to raw (no rubber-banding makes sense for zero-size containers).
        public static double RubberBandVisual(double rawOverscroll, double dim) {
            if (dim <= 0) return rawOverscroll;
            double c = RubberBandC;
            return (1.0 - 1.0 / (rawOverscroll * c / dim + 1.0)) * dim;
        }

        // ------------------------------------------------------------------ //
        //  Spring-back management (called by ScrollEventHandler)               //
        // ------------------------------------------------------------------ //

        // Start a spring-back from the given overscroll amounts on each axis.
        // overX/overY are signed: positive = past bottom/right edge; negative = past top/left.
        // boundaryX/boundaryY are the clamped positions the spring snaps back to.
        // Call this instead of / after clearing raw overscroll on PointerUp.
        public void StartSpringBack(Box box, double nowSeconds,
                                    double overX, double overY,
                                    double boundaryX, double boundaryY) {
            if (box == null) return;
            if (Math.Abs(overX) < SpringStopThresholdPx && Math.Abs(overY) < SpringStopThresholdPx) {
                // Already at boundary — just snap.
                var state = container?.GetOrCreate(box);
                if (state != null) {
                    ApplyPosition(box, state, boundaryX, boundaryY);
                }
                return;
            }
            springs[box] = new SpringState(overX, overY, boundaryX, boundaryY, nowSeconds);
            // Cancel any glide that might conflict.
            glides.Remove(box);
        }

        // ------------------------------------------------------------------ //
        //  Velocity tracking                                                   //
        // ------------------------------------------------------------------ //

        // Record a drag sample.  Call on every PointerMove while dragging.
        // `nowSeconds` must be the same clock as StartGlide / Tick.
        public void AddSample(Box box, double nowSeconds, double scrollX, double scrollY) {
            if (box == null) return;
            if (!rings.TryGetValue(box, out var ring)) {
                ring = new RingBuffer(RingCapacity);
                rings[box] = ring;
            }
            ring.Add(new Sample(nowSeconds, scrollX, scrollY));
        }

        // Compute velocity over the trailing window from samples and start the
        // glide.  Call on PointerUp (drag release).
        //
        // If the estimated speed is below StopThresholdPxPerSec, no glide is
        // started (zero-velocity release = no-op per spec requirement R6).
        // Cancels any in-flight smooth animation on this box if snapAnimator
        // is set (interrupt contract).
        //
        // overX / overY: current overscroll amounts (signed, from TouchDrag).
        // When non-zero, the container is currently past an edge and a spring-back
        // must be triggered. If the velocity is inward (towards the boundary), the
        // glide runs from the boundary and the overscroll collapses immediately;
        // otherwise spring-back runs from the overscrolled position.
        public void StartGlide(Box box, double nowSeconds,
                                double overX = 0, double overY = 0) {
            if (box == null) return;
            // Always clear the sample ring after a release regardless of
            // whether we actually start a glide, to avoid stale data.
            rings.TryGetValue(box, out var ring);

            double vx = 0, vy = 0;
            if (ring != null && ring.Count >= 2) {
                EstimateVelocity(ring, nowSeconds, out vx, out vy);
            }
            rings.Remove(box);

            var state = container?.GetOrCreate(box);
            if (state == null) return;

            // Cancel any competing smooth animation on this box.
            snapAnimator?.Cancel(box);
            springs.Remove(box);

            // Determine boundary positions (clamped = no overscroll).
            double boundaryX = ScrollMath.Clamp(state.ScrollX - overX, 0, state.MaxScrollX);
            double boundaryY = ScrollMath.Clamp(state.ScrollY - overY, 0, state.MaxScrollY);

            bool overscrolled = Math.Abs(overX) > SpringStopThresholdPx
                             || Math.Abs(overY) > SpringStopThresholdPx;

            if (overscrolled) {
                // Check whether velocity is inward (toward the boundary).
                // Inward: the glide would push us back in-bounds from the start.
                // We start the glide from the boundary, not from the overscrolled pos.
                bool inwardX = overX > 0 ? vx < 0 : vx > 0;  // over-right with leftward vel = inward
                bool inwardY = overY > 0 ? vy < 0 : vy > 0;

                if ((Math.Abs(overX) > SpringStopThresholdPx && inwardX)
                    || (Math.Abs(overY) > SpringStopThresholdPx && inwardY)) {
                    // Inward velocity: snap position to boundary, then continue glide normally.
                    // Axis-specific: only snap the axis that's overscrolled + inward.
                    double snapX = state.ScrollX;
                    double snapY = state.ScrollY;
                    if (Math.Abs(overX) > SpringStopThresholdPx && inwardX) snapX = boundaryX;
                    if (Math.Abs(overY) > SpringStopThresholdPx && inwardY) snapY = boundaryY;
                    ApplyPosition(box, state, snapX, snapY);
                    // Continue with normal glide/snap logic from boundary position.
                } else {
                    // No inward velocity (or velocity is zero / outward): spring back.
                    double speed = Math.Sqrt(vx * vx + vy * vy);
                    if (speed < StopThresholdPxPerSec) {
                        // Low velocity release while overscrolled — pure spring-back.
                        StartSpringBack(box, nowSeconds, overX, overY, boundaryX, boundaryY);
                        return;
                    }
                    // Has non-negligible outward velocity: spring still takes over
                    // (outward velocity into open air makes no sense; spring from current pos).
                    StartSpringBack(box, nowSeconds, overX, overY, boundaryX, boundaryY);
                    return;
                }
            }

            double speed2 = Math.Sqrt(vx * vx + vy * vy);
            if (speed2 < StopThresholdPxPerSec) {
                // Zero or negligible release — no glide.
                return;
            }

            // Check for snap retargeting.  If the container has a snap type,
            // compute where the glide would naturally rest and pick the nearest
            // snap point, then animate to that target rather than running the
            // raw exponential glide.
            if (snapResolver != null) {
                var snapType = SnapResolver.ResolveType(box);
                if (snapType.IsActive) {
                    double restX, restY;
                    ComputeNaturalRest(state, vx, vy, out restX, out restY);
                    bool snappedY = snapType.Axis != SnapAxis.X
                        && snapResolver.TryFindSnapTargetY(box, state.ScrollY, restY, snapType, out restY);
                    bool snappedX = snapType.Axis != SnapAxis.Y
                        && snapResolver.TryFindSnapTargetX(box, state.ScrollX, restX, snapType, out restX);
                    if (snappedX || snappedY) {
                        // Retarget to snap position via smooth animator (if available)
                        // or immediate jump.
                        if (snapAnimator != null) {
                            snapAnimator.Animate(box, restX, restY);
                        } else {
                            // Direct set — clamp and apply.
                            double cx = ScrollMath.Clamp(restX, 0, state.MaxScrollX);
                            double cy = ScrollMath.Clamp(restY, 0, state.MaxScrollY);
                            ApplyPosition(box, state, cx, cy);
                        }
                        return;
                    }
                }
            }

            // Raw exponential-decay glide.
            glides[box] = new GlideState(vx, vy, nowSeconds);
        }

        // Cancel an in-flight glide on `box`.  Safe to call when no glide is
        // running.  Also clears any pending sample ring so stale samples from
        // a prior drag don't contaminate the next one. Clears spring-back too.
        public void Cancel(Box box) {
            if (box == null) return;
            glides.Remove(box);
            springs.Remove(box);
            rings.Remove(box);
        }

        public void CancelAll() {
            glides.Clear();
            springs.Clear();
            rings.Clear();
        }

        // Per-frame tick.  `deltaSeconds` is the time since the last Tick call.
        // `nowSeconds` is the absolute wall-clock time (injected, same clock as
        // StartGlide / AddSample).
        // `tracker` may be null if the caller does not need paint invalidation.
        public void Tick(double deltaSeconds, double nowSeconds, InvalidationTracker tracker) {
            if ((glides.Count == 0 && springs.Count == 0) || deltaSeconds <= 0) return;

            TickGlides(deltaSeconds, nowSeconds, tracker);
            TickSprings(deltaSeconds, nowSeconds, tracker);
        }

        // ------------------------------------------------------------------ //
        //  Glide tick (exponential decay, edge → overshoot conversion)        //
        // ------------------------------------------------------------------ //

        void TickGlides(double deltaSeconds, double nowSeconds, InvalidationTracker tracker) {
            if (glides.Count == 0) return;

            scratchDone.Clear();
            foreach (var kv in glides) scratchDone.Add(kv.Key);
            int count = scratchDone.Count;

            for (int i = 0; i < count; i++) {
                var box = scratchDone[i];
                if (!glides.TryGetValue(box, out var g)) continue;

                var state = container?.GetOrCreate(box);
                if (state == null) {
                    // Will remove below.
                    continue;
                }

                // Exponential decay: v_new = v_old * decayBase^(dt_ms).
                double dtMs = deltaSeconds * 1000.0;
                double factor = Math.Pow(DecayBasePerMs, dtMs);

                double newVx = g.Vx * factor;
                double newVy = g.Vy * factor;

                // Move by average velocity over the interval (trapezoidal rule).
                double avgVx = (g.Vx + newVx) * 0.5;
                double avgVy = (g.Vy + newVy) * 0.5;

                double newX = state.ScrollX + avgVx * deltaSeconds;
                double newY = state.ScrollY + avgVy * deltaSeconds;

                // Edge handling: convert residual velocity to overshoot + spring-back
                // instead of the old hard clamp.
                bool clampedX = false, clampedY = false;
                double overshootX = 0, overshootY = 0;

                if (newX < 0) {
                    // Compute overshoot from remaining velocity (negative vx is leftward).
                    double ov = ComputeOvershoot(newVx);
                    overshootX = -ov;  // negative = past top/left edge
                    newX = -RubberBandVisual(ov, state.ViewportWidth > 0 ? state.ViewportWidth : 100);
                    clampedX = true;
                } else if (newX > state.MaxScrollX) {
                    double ov = ComputeOvershoot(newVx);
                    overshootX = ov;   // positive = past bottom/right edge
                    newX = state.MaxScrollX + RubberBandVisual(ov, state.ViewportWidth > 0 ? state.ViewportWidth : 100);
                    clampedX = true;
                }

                if (newY < 0) {
                    double ov = ComputeOvershoot(newVy);
                    overshootY = -ov;
                    newY = -RubberBandVisual(ov, state.ViewportHeight > 0 ? state.ViewportHeight : 100);
                    clampedY = true;
                } else if (newY > state.MaxScrollY) {
                    double ov = ComputeOvershoot(newVy);
                    overshootY = ov;
                    newY = state.MaxScrollY + RubberBandVisual(ov, state.ViewportHeight > 0 ? state.ViewportHeight : 100);
                    clampedY = true;
                }

                if (clampedX) newVx = 0;
                if (clampedY) newVy = 0;

                // Apply scroll (may be out of bounds during overshoot).
                state.ScrollX = newX;
                state.ScrollY = newY;
                state.BumpVersion();
                box.ScrollX = newX;
                box.ScrollY = newY;
                if (tracker != null && box.Element != null) {
                    tracker.MarkDirty(box.Element, InvalidationKind.Paint);
                }

                // If we hit an edge, remove the glide and start spring-back.
                if (clampedX || clampedY) {
                    scratchDone[i] = box; // mark for removal
                    double bndX = ScrollMath.Clamp(newX, 0, state.MaxScrollX);
                    double bndY = ScrollMath.Clamp(newY, 0, state.MaxScrollY);
                    // overscrollX/Y from current position relative to boundary.
                    double osX = newX - bndX;  // signed
                    double osY = newY - bndY;
                    if (Math.Abs(osX) > SpringStopThresholdPx || Math.Abs(osY) > SpringStopThresholdPx) {
                        springs[box] = new SpringState(osX, osY, bndX, bndY, nowSeconds);
                    }
                    continue;
                }

                // Check stop condition: speed below threshold.
                double speed = Math.Sqrt(newVx * newVx + newVy * newVy);
                bool done = speed < StopThresholdPxPerSec;

                if (done) {
                    // Leave scratchDone[i] as the key to remove.
                } else {
                    glides[box] = new GlideState(newVx, newVy, nowSeconds);
                    scratchDone[i] = null; // mark as still-running
                }
            }

            // Remove finished glides.
            for (int i = 0; i < count; i++) {
                if (scratchDone[i] != null) glides.Remove(scratchDone[i]);
            }
            scratchDone.Clear();
        }

        // Compute overshoot distance from edge velocity.
        // Cap at OvershootCapPx; minimum zero.
        static double ComputeOvershoot(double velocity) {
            double w = SpringOmega;
            if (w <= 0) return 0;
            double raw = Math.Abs(velocity) / w * 0.5;  // 0.5 * v/w per design spec
            return Math.Min(raw, OvershootCapPx);
        }

        // ------------------------------------------------------------------ //
        //  Spring-back tick (critically damped oscillator)                     //
        // ------------------------------------------------------------------ //

        void TickSprings(double deltaSeconds, double nowSeconds, InvalidationTracker tracker) {
            if (springs.Count == 0) return;

            scratchDone.Clear();
            foreach (var kv in springs) scratchDone.Add(kv.Key);
            int count = scratchDone.Count;

            for (int i = 0; i < count; i++) {
                var box = scratchDone[i];
                if (!springs.TryGetValue(box, out var sp)) continue;

                var state = container?.GetOrCreate(box);
                if (state == null) {
                    // Will remove below.
                    continue;
                }

                double elapsed = nowSeconds - sp.StartTime;
                double w = SpringOmega;

                // Critically damped: x(t) = x0 * (1 + w*t) * exp(-w*t)
                double scale = (1.0 + w * elapsed) * Math.Exp(-w * elapsed);
                double overX = sp.InitialOverX * scale;
                double overY = sp.InitialOverY * scale;

                bool done = Math.Abs(overX) < SpringStopThresholdPx
                         && Math.Abs(overY) < SpringStopThresholdPx;

                double newX, newY;
                if (done) {
                    // Snap exactly to boundary.
                    newX = sp.BoundaryX;
                    newY = sp.BoundaryY;
                } else {
                    newX = sp.BoundaryX + overX;
                    newY = sp.BoundaryY + overY;
                    scratchDone[i] = null; // still running
                }

                state.ScrollX = newX;
                state.ScrollY = newY;
                state.BumpVersion();
                box.ScrollX = newX;
                box.ScrollY = newY;
                if (tracker != null && box.Element != null) {
                    tracker.MarkDirty(box.Element, InvalidationKind.Paint);
                }
            }

            // Remove finished springs.
            for (int i = 0; i < count; i++) {
                if (scratchDone[i] != null) springs.Remove(scratchDone[i]);
            }
            scratchDone.Clear();

            // After springs finish, allow snap to settle (caller owns that).
        }

        // ------------------------------------------------------------------ //
        //  Helpers                                                             //
        // ------------------------------------------------------------------ //

        // Apply a scroll position directly (used for boundary snaps + in-bounds
        // moves that don't need rubber-banding).
        void ApplyPosition(Box box, ScrollState state, double x, double y) {
            state.ScrollX = x;
            state.ScrollY = y;
            state.BumpVersion();
            box.ScrollX = x;
            box.ScrollY = y;
        }

        // Estimate velocity (px/s) over the last VelocityWindowSeconds of
        // samples.  Uses a simple linear regression of position vs time to
        // be robust against jitter.  Falls back to first/last delta when fewer
        // than 3 samples are in window.
        static void EstimateVelocity(RingBuffer ring, double nowSeconds, out double vx, out double vy) {
            vx = 0;
            vy = 0;
            double cutoff = nowSeconds - VelocityWindowSeconds;
            // Collect window samples (oldest first — RingBuffer iterates
            // oldest-to-newest).
            int n = 0;
            double sumT = 0, sumT2 = 0;
            double sumX = 0, sumTX = 0;
            double sumY = 0, sumTY = 0;
            Sample first = default, last = default;
            bool hasSample = false;

            for (int i = 0; i < ring.Count; i++) {
                var s = ring.Get(i);
                if (s.Time < cutoff) continue;
                double t = s.Time;
                sumT  += t;
                sumT2 += t * t;
                sumX  += s.X;
                sumTX += t * s.X;
                sumY  += s.Y;
                sumTY += t * s.Y;
                n++;
                if (!hasSample) { first = s; hasSample = true; }
                last = s;
            }

            if (n < 2) return;

            double dt = last.Time - first.Time;
            if (dt < 1e-6) return;

            if (n == 2) {
                // Simple finite difference.
                vx = (last.X - first.X) / dt;
                vy = (last.Y - first.Y) / dt;
                return;
            }

            // Least-squares linear regression: pos = a + b*t, b = velocity.
            double denom = n * sumT2 - sumT * sumT;
            if (Math.Abs(denom) < 1e-12) {
                // All samples at same time — use first/last.
                vx = (last.X - first.X) / dt;
                vy = (last.Y - first.Y) / dt;
                return;
            }
            vx = (n * sumTX - sumT * sumX) / denom;
            vy = (n * sumTY - sumT * sumY) / denom;
        }

        // Compute the natural resting position of a glide from current
        // scroll state with given initial velocity, by analytically integrating
        // the exponential decay.
        //
        // For v(t) = v0 * decayBase^(t_ms):
        //   integral from 0..∞ = v0 / ln(1/decayBase) * (1/1000)   [in px/s → s]
        //   = v0_px_per_s * (1 / (1000 * -ln(decayBase)))
        //
        // In practice the glide stops at StopThresholdPxPerSec so we compute
        // t_stop = log(v0/threshold) / log(1/decayBase) (ms),
        // then integrate v over [0, t_stop].
        static void ComputeNaturalRest(ScrollState state, double vx, double vy,
                                       out double restX, out double restY) {
            restX = state.ScrollX;
            restY = state.ScrollY;

            double lnk = Math.Log(DecayBasePerMs);    // negative number
            double negLnk = -lnk;                      // positive

            // X axis.
            double speedX = Math.Abs(vx);
            if (speedX > StopThresholdPxPerSec && negLnk > 1e-15) {
                double tStopMs = Math.Log(speedX / StopThresholdPxPerSec) / negLnk;
                double kPowT = Math.Pow(DecayBasePerMs, tStopMs);
                double dispX = (vx / (1000.0 * negLnk)) * (1.0 - kPowT);
                restX = state.ScrollX + dispX;
            }

            // Y axis.
            double speedY = Math.Abs(vy);
            if (speedY > StopThresholdPxPerSec && negLnk > 1e-15) {
                double tStopMs = Math.Log(speedY / StopThresholdPxPerSec) / negLnk;
                double kPowT = Math.Pow(DecayBasePerMs, tStopMs);
                double dispY = (vy / (1000.0 * negLnk)) * (1.0 - kPowT);
                restY = state.ScrollY + dispY;
            }

            // Clamp rest positions to valid scroll bounds.
            restX = ScrollMath.Clamp(restX, 0, state.MaxScrollX);
            restY = ScrollMath.Clamp(restY, 0, state.MaxScrollY);
        }

        // ------------------------------------------------------------------ //
        //  Inner types                                                         //
        // ------------------------------------------------------------------ //

        struct GlideState {
            public readonly double Vx;   // px/s, signed
            public readonly double Vy;
            public readonly double StartTime;

            public GlideState(double vx, double vy, double startTime) {
                Vx = vx;
                Vy = vy;
                StartTime = startTime;
            }
        }

        // State for a critically-damped spring-back from overscroll.
        struct SpringState {
            public readonly double InitialOverX;  // signed overscroll at spring start
            public readonly double InitialOverY;
            public readonly double BoundaryX;     // target (clamped boundary) position
            public readonly double BoundaryY;
            public readonly double StartTime;

            public SpringState(double overX, double overY,
                               double boundaryX, double boundaryY,
                               double startTime) {
                InitialOverX = overX;
                InitialOverY = overY;
                BoundaryX = boundaryX;
                BoundaryY = boundaryY;
                StartTime = startTime;
            }
        }

        struct Sample {
            public readonly double Time; // seconds
            public readonly double X;   // ScrollX at sample time (px)
            public readonly double Y;

            public Sample(double time, double x, double y) {
                Time = time;
                X = x;
                Y = y;
            }
        }

        // Fixed-capacity ring buffer — oldest-to-newest iteration via Get(i).
        sealed class RingBuffer {
            readonly Sample[] buf;
            int head;  // index of next write slot
            int count;

            public RingBuffer(int capacity) {
                buf = new Sample[capacity];
                head = 0;
                count = 0;
            }

            public int Count => count;

            public void Add(Sample s) {
                buf[head] = s;
                head = (head + 1) % buf.Length;
                if (count < buf.Length) count++;
            }

            // Get sample at logical index i (0 = oldest).
            public Sample Get(int i) {
                // oldest entry is at (head - count + buf.Length) % buf.Length
                int idx = (head - count + i + buf.Length * 2) % buf.Length;
                return buf[idx];
            }
        }
    }
}
