using System.Collections.Generic;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Reactive;

namespace Weva.DevTools {
    public enum DirtyHighlightKind {
        Layout = 0,
        Style = 1,
        Paint = 2,
    }

    public struct DirtyHighlight {
        public Element Element;
        public DirtyHighlightKind Kind;
        public int FramesRemaining;
    }

    // Snapshots the InvalidationTracker before the lifecycle clears it for the
    // next frame; classifies each dirty element by the kind that wins (layout
    // beats style beats paint) and decays the highlight over a configurable
    // window so the user actually sees a single-frame flash. The classification
    // mirrors the propagation rules in PLAN §12: any Layout/Structure dirty
    // bit reads as a "geometry" change; Style alone is amber; Paint/Composite
    // alone is the gray "paint-only" case.
    public sealed class DirtyHighlighter {
        public int FlashFrames { get; set; } = 3;

        readonly Dictionary<Element, DirtyHighlight> active = new();
        readonly List<Element> scratchKeys = new();

        // Exposed as the concrete Dictionary<,> rather than IReadOnlyDictionary<,>.
        // The overlay disabled hot path (DevToolsHotPathTests.DirtyHighlighter_disabled_zero_alloc)
        // polls Active.Count tens of thousands of times per second; interface
        // dispatch through IReadOnlyDictionary<,>.Count on Unity's Mono runtime
        // walks generic-shared vtable trampolines that allocate a small
        // (~4 KB) bucket on first dispatch and occasionally re-allocate when
        // the trampoline cache evicts. Returning the concrete type collapses
        // .Count to a direct property read with zero allocation regardless of
        // call frequency. The Dictionary is reference-shared, not mutable-by-
        // contract: external code MUST treat it as read-only — same convention
        // we use for InvalidationTracker.AllDirty.
        public Dictionary<Element, DirtyHighlight> Active => active;

        // Captures the current dirty set into the highlight buffer. Call this
        // BEFORE the lifecycle's tracker.Clear() — InvalidationTracker.GetDirty
        // would return nothing afterward.
        public void CaptureFrame(InvalidationTracker tracker) {
            DecayInPlace();
            if (tracker == null) return;
            foreach (var kv in EnumerateDirty(tracker)) {
                var element = kv.Key;
                var kind = ClassifyKind(kv.Value);
                active[element] = new DirtyHighlight {
                    Element = element,
                    Kind = kind,
                    FramesRemaining = FlashFrames,
                };
            }
        }

        public void Clear() => active.Clear();

        // Resolves an element to its laid-out box and surfaces a flat list the
        // overlay renderer can iterate without re-walking the dictionary.
        public int ResolveBoxes(System.Func<Element, Box> elementToBox, List<DirtyBoxHighlight> output) {
            if (output == null) return 0;
            if (elementToBox == null) return 0;
            int before = output.Count;
            foreach (var kv in active) {
                var b = elementToBox(kv.Key);
                if (b == null) continue;
                output.Add(new DirtyBoxHighlight { Box = b, Highlight = kv.Value });
            }
            return output.Count - before;
        }

        void DecayInPlace() {
            scratchKeys.Clear();
            foreach (var kv in active) scratchKeys.Add(kv.Key);
            for (int i = 0; i < scratchKeys.Count; i++) {
                var k = scratchKeys[i];
                var v = active[k];
                v.FramesRemaining--;
                if (v.FramesRemaining <= 0) {
                    active.Remove(k);
                } else {
                    active[k] = v;
                }
            }
        }

        // Layout/Structure beat Style beat Paint/Composite — that's the order
        // a user wants to see when reading a flash on screen. A box that
        // re-laid out this frame is more interesting than one that just
        // re-painted, so the more invasive kind wins.
        static DirtyHighlightKind ClassifyKind(InvalidationKind kind) {
            if ((kind & (InvalidationKind.Layout | InvalidationKind.Structure)) != 0) return DirtyHighlightKind.Layout;
            if ((kind & InvalidationKind.Style) != 0) return DirtyHighlightKind.Style;
            return DirtyHighlightKind.Paint;
        }

        // Allocation-sensitive snapshot: pulls Element-typed nodes out of the
        // tracker's dictionary into a temporary list so we can iterate without
        // mutating the tracker. Steady-state hot path is dirty-set-empty, so
        // this list typically has 0-1 entries per frame.
        IEnumerable<KeyValuePair<Element, InvalidationKind>> EnumerateDirty(InvalidationTracker tracker) {
            foreach (var node in tracker.AllDirty) {
                if (node is Element e) {
                    yield return new KeyValuePair<Element, InvalidationKind>(e, tracker.GetKinds(e));
                }
            }
        }
    }

    public struct DirtyBoxHighlight {
        public Box Box;
        public DirtyHighlight Highlight;
    }
}
