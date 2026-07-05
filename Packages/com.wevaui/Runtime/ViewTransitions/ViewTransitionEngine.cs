using System;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Reactive;

namespace Weva.ViewTransitions {
    // Captures a "before" snapshot, runs the caller-supplied DOM mutation,
    // re-runs cascade+layout, captures the "after" snapshot, and produces a
    // ViewTransition that the renderer can sample each Tick to crossfade
    // matched and unmatched elements.
    //
    // v1 simplifications:
    //   - One in-flight transition at a time: starting a second skips the first.
    //   - Crossfade only; no per-name `::view-transition-old(...)` styling.
    //   - Reduced motion (MediaContext.PrefersReducedMotion) skips animation.
    public sealed class ViewTransitionEngine {
        readonly Func<Box> rootBoxProvider;
        readonly Action relayoutCallback;
        readonly Document document;
        ViewTransition active;

        public ViewTransitionEngine(Document document, Func<Box> rootBoxProvider, Action relayoutCallback) {
            this.document = document;
            this.rootBoxProvider = rootBoxProvider;
            this.relayoutCallback = relayoutCallback;
            ViewTransitionProperties.EnsureRegistered();
        }

        public ViewTransition Active => active;
        public bool HasActive => active != null && !active.Done;

        public bool ReducedMotion { get; set; }

        public ViewTransition Start(Action mutate) {
            return Start(mutate, ViewTransition.DefaultDurationSeconds);
        }

        public ViewTransition Start(Action mutate, double durationSeconds) {
            // v1 concurrency: a new Start replaces the in-flight one (skip).
            if (active != null && !active.Done) {
                active.Phase = ViewTransitionPhase.Skipped;
                active = null;
            }

            var vt = new ViewTransition { Duration = durationSeconds };
            active = vt;

            vt.Phase = ViewTransitionPhase.Capturing;
            // 1. Capture the "before" snapshot from the current laid-out tree.
            var beforeRoot = rootBoxProvider != null ? rootBoxProvider() : null;
            vt.Before = SnapshotCapture.Capture(beforeRoot);

            // 2. Run the caller's mutation.
            try {
                mutate?.Invoke();
            } catch {
                vt.Phase = ViewTransitionPhase.Skipped;
                throw;
            }

            // 3. Trigger re-layout so the new tree exists.
            relayoutCallback?.Invoke();

            // 4. Capture the "after" snapshot.
            var afterRoot = rootBoxProvider != null ? rootBoxProvider() : null;
            vt.After = SnapshotCapture.Capture(afterRoot);

            // 5. Pair up names.
            vt.Pairs = CrossFadeAnimator.BuildPairs(vt.Before, vt.After);

            if (ReducedMotion) {
                // Skip animation; mark finished immediately.
                vt.Elapsed = vt.Duration;
                vt.Phase = ViewTransitionPhase.Finished;
            } else if (vt.Pairs.Count == 0) {
                vt.Phase = ViewTransitionPhase.Finished;
            } else {
                vt.Phase = ViewTransitionPhase.Animating;
            }
            return vt;
        }

        public void Tick(double deltaSeconds, InvalidationTracker tracker) {
            if (active == null) return;
            if (active.Done) return;
            CrossFadeAnimator.Tick(active, deltaSeconds, tracker, document);
        }

        public static ViewTransitionEngine Create(Document document, MediaContext media, Func<Box> rootBoxProvider, Action relayout) {
            var engine = new ViewTransitionEngine(document, rootBoxProvider, relayout);
            engine.ReducedMotion = media.PrefersReducedMotion;
            return engine;
        }
    }
}
