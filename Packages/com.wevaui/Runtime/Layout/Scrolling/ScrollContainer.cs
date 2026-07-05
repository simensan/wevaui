using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Scrolling {
    public sealed class ScrollContainer {
        readonly Dictionary<Box, ScrollState> states = new();
        // P6: per-instance scratch list for RetainOnly's stale-box sweep.
        // Pre-fix every call to RetainOnly with any stale entries lazily
        // allocated `new List<Box>()`. Hoisted to an instance field; cleared
        // at the top of RetainOnly, filled during, never reallocated. Typical
        // stale set after a Reconcile is a handful of recycled boxes, so 8
        // entries pre-sizes the common case.
        readonly List<Box> scratchStale = new(8);

        public ScrollState GetOrCreate(Box box) {
            if (box == null) return null;
            if (states.TryGetValue(box, out var s)) {
                if (s.OwnerGeneration == box.PoolGeneration) return s;
                // Stale resurrection: the key instance was recycled and
                // re-rented as a DIFFERENT box since this entry was linked
                // (see Box.PoolGeneration). Treat as a miss.
                states.Remove(box);
            }
            s = new ScrollState { OwnerGeneration = box.PoolGeneration, OwnerElement = OwnerElementFor(box) };
            states[box] = s;
            return s;
        }

        // Link-time owner identity. Anonymous boxes CAN be scroll containers
        // (a wrapper carrying its element's overflow style — the live tables
        // showed an elementless twin with exactly .page's scroll metrics);
        // without an owner they are unrescuable when their box is replaced.
        // Fall back to the nearest ancestor element so a rescue re-homes the
        // offset onto that element's live box. The anonymous VIEWPORT root
        // has no ancestors, stays null, and is handled by the explicit
        // lastRoot->survivor transfer instead.
        static Weva.Dom.Element OwnerElementFor(Box box) {
            for (var b = box; b != null; b = b.Parent) {
                if (b.Element != null) return b.Element;
            }
            return null;
        }

        public ScrollState Get(Box box) {
            if (box == null) return null;
            if (!states.TryGetValue(box, out var s)) return null;
            if (s.OwnerGeneration != box.PoolGeneration) {
                states.Remove(box);
                return null;
            }
            return s;
        }

        public bool Has(Box box) {
            return Get(box) != null;
        }

        public void Remove(Box box) {
            if (box == null) return;
            states.Remove(box);
        }

        public void TransferScrollPosition(Box from, Box to) {
            if (from == null || to == null || ReferenceEquals(from, to)) return;
            if (!states.TryGetValue(from, out var previous) || previous == null) return;
            // Never transfer a stale-generation entry (a recycled instance's
            // phantom) — that would smear a dead scroll onto a live box.
            if (previous.OwnerGeneration != from.PoolGeneration) {
                states.Remove(from);
                return;
            }

            AdoptInto(previous, to);
            states.Remove(from);
        }

        // Copy a (possibly orphaned) state's offsets onto the live box `to`,
        // stamping ownership (generation + element) so the target entry is
        // valid under the resurrection checks above.
        void AdoptInto(ScrollState previous, Box to) {
            if (!states.TryGetValue(to, out var current) || current == null
                || current.OwnerGeneration != to.PoolGeneration) {
                current = new ScrollState { OwnerGeneration = to.PoolGeneration, OwnerElement = OwnerElementFor(to) };
                states[to] = current;
            }

            double x = previous.ScrollX;
            double y = previous.ScrollY;
            bool currentHasMetrics = current.ViewportWidth > 0
                                     || current.ViewportHeight > 0
                                     || current.ScrollWidth > 0
                                     || current.ScrollHeight > 0;
            if (currentHasMetrics) {
                x = ScrollMath.Clamp(x, 0, current.MaxScrollX);
                y = ScrollMath.Clamp(y, 0, current.MaxScrollY);
            } else {
                if (x < 0) x = 0;
                if (y < 0) y = 0;
            }

            current.ScrollX = x;
            current.ScrollY = y;
            current.Version = previous.Version;
            to.ScrollX = x;
            to.ScrollY = y;
        }

        // P6 scratch for ReanchorOrphans (same pattern as scratchStale).
        readonly List<KeyValuePair<Box, ScrollState>> scratchOrphans = new(4);

        // Rescue scrolled state whose key box is no longer trustworthy and
        // re-home it on the live box of the SAME ELEMENT. An entry is
        // orphaned when its key box left the live tree, OR when the key box
        // is live but its pool generation moved on — the instance was
        // recycled and re-rented as a different box within the pass (live
        // find: .page's scrolled box came back as an anonymous wrapper, so a
        // liveness-only check skipped it and the fresh .page box started at
        // 0). The owner element captured at link time is the only identity
        // that survives that, so resolution goes through it, never through
        // the stale box's current Element.
        public void ReanchorOrphans(HashSet<Box> live,
                                    System.Func<Weva.Dom.Element, Box> resolveLiveBox) {
            if (live == null) return;
            ReanchorCore(live, resolveLiveBox);
        }

        // Generation-only variant for the incremental subtree path, which has
        // no live set (lastRoot survives in place, so computing one would be
        // the O(tree) walk the path exists to avoid). Sufficient there:
        // ResetForPool bumps PoolGeneration whether or not the instance is
        // re-rented afterwards, so every box RecycleSubtree returned this
        // pass — including scroll containers that were DESCENDANTS of the
        // spliced subtree, which PreserveScrollStateForReplacement (splice
        // root only) never covers — shows up as a generation mismatch. The
        // gate is O(#states); the resolve walk runs only when an orphan
        // actually exists.
        public void ReanchorStaleGenerations(System.Func<Weva.Dom.Element, Box> resolveLiveBox) {
            ReanchorCore(null, resolveLiveBox);
        }

        void ReanchorCore(HashSet<Box> liveOrNull,
                          System.Func<Weva.Dom.Element, Box> resolveLiveBox) {
            if (resolveLiveBox == null) return;
            var orphans = scratchOrphans;
            orphans.Clear();
            foreach (var kv in states) {
                var box = kv.Key;
                var s = kv.Value;
                if (s == null) continue;
                bool generationMismatch = s.OwnerGeneration != box.PoolGeneration;
                bool stale = generationMismatch
                             || (liveOrNull != null && !liveOrNull.Contains(box));
                if (!stale) continue;
                bool worthRescuing = (s.ScrollX > 0.5 || s.ScrollY > 0.5) && s.OwnerElement != null;
                // Generation-mismatched entries on LIVE boxes must be removed
                // HERE — RetainOnly keeps them (the box is live) and Get()
                // refuses them, so they would otherwise pile up as unreadable
                // dead weight. Not-live gen-valid entries with nothing worth
                // rescuing are left for RetainOnly's normal sweep.
                if (worthRescuing || generationMismatch) orphans.Add(kv);
            }
            for (int i = 0; i < orphans.Count; i++) {
                var (staleBox, s) = (orphans[i].Key, orphans[i].Value);
                if ((s.ScrollX > 0.5 || s.ScrollY > 0.5) && s.OwnerElement != null) {
                    var target = resolveLiveBox(s.OwnerElement);
                    if (target != null && !ReferenceEquals(target, staleBox)) {
                        // Don't clobber a live target that already carries a
                        // meaningful offset of its own (e.g. an input write
                        // that landed after the rebuild).
                        var existing = Get(target);
                        if (existing == null || (existing.ScrollX <= 0.5 && existing.ScrollY <= 0.5)) {
                            AdoptInto(s, target);
                        }
                    }
                }
                states.Remove(staleBox);
            }
            orphans.Clear();
        }

        public void Clear() {
            states.Clear();
        }

        public IEnumerable<KeyValuePair<Box, ScrollState>> All => states;

        public int Count => states.Count;

        // After a re-layout the Box graph may be replaced with fresh instances by
        // Reconcile. Boxes that no longer appear in the live tree get pruned to
        // keep the dictionary from growing without bound while still preserving
        // scroll position when a Box survives the pass.
        public void RetainOnly(System.Collections.Generic.HashSet<Box> live) {
            if (live == null) { Clear(); return; }
            // P6: reuse instance scratch instead of lazy-allocating per call.
            var stale = scratchStale;
            stale.Clear();
            foreach (var kv in states) {
                if (!live.Contains(kv.Key)) {
                    stale.Add(kv.Key);
                }
            }
            if (stale.Count == 0) return;
            for (int i = 0; i < stale.Count; i++) {
                states.Remove(stale[i]);
            }
            stale.Clear();
        }
    }
}
