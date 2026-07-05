using System.Collections.Generic;
using Weva.Reactive;

namespace Weva.ViewTransitions {
    public static class CrossFadeAnimator {
        public static IReadOnlyList<ViewTransitionPair> BuildPairs(ViewTransitionSnapshot before, ViewTransitionSnapshot after) {
            var pairs = new List<ViewTransitionPair>();
            if (before == null && after == null) return pairs;
            var seen = new HashSet<string>();
            if (before != null) {
                foreach (var kv in before.ByName) {
                    string name = kv.Key;
                    if (after != null && after.TryGet(name, out var n)) {
                        pairs.Add(new ViewTransitionPair(name, ViewTransitionPairKind.Matched, kv.Value, n));
                    } else {
                        pairs.Add(new ViewTransitionPair(name, ViewTransitionPairKind.OldOnly, kv.Value, default));
                    }
                    seen.Add(name);
                }
            }
            if (after != null) {
                foreach (var kv in after.ByName) {
                    if (seen.Contains(kv.Key)) continue;
                    pairs.Add(new ViewTransitionPair(kv.Key, ViewTransitionPairKind.NewOnly, default, kv.Value));
                }
            }
            return pairs;
        }

        public static void Tick(ViewTransition vt, double deltaSeconds, InvalidationTracker tracker, Weva.Dom.Document doc) {
            if (vt == null) return;
            if (vt.Done) return;
            if (vt.Phase != ViewTransitionPhase.Animating) return;
            vt.Elapsed += deltaSeconds;
            if (vt.Elapsed >= vt.Duration) {
                vt.Elapsed = vt.Duration;
                vt.Phase = ViewTransitionPhase.Finished;
            }
            // Just mark the document root dirty — the view-transition
            // overlay paints at the top level via a dedicated PaintCommand,
            // so dirtying every descendant of `doc` (which is what the
            // previous `MarkSubtreeDirty(doc, Paint)` did) was an O(N)
            // recursive walk per Tick with no useful effect. The painter
            // already takes the "PaintInvalidated" code path from a
            // single root mark.
            if (tracker != null && doc != null) {
                tracker.MarkDirty(doc, InvalidationKind.Paint);
            }
        }
    }
}
