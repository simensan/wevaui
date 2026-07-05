using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Events {
    internal sealed class EventListeners {
        readonly Dictionary<Element, List<EventListenerRegistration>> map = new();

        public void AddListener(Element target, EventKind kind, EventListener handler, bool useCapture) {
            if (target == null || handler == null) return;
            if (!map.TryGetValue(target, out var list)) {
                list = new List<EventListenerRegistration>();
                map[target] = list;
            }
            for (int i = 0; i < list.Count; i++) {
                var r = list[i];
                if (r.Kind == kind && r.Handler == handler && r.UseCapture == useCapture) return;
            }
            list.Add(new EventListenerRegistration(kind, handler, useCapture));
        }

        public bool RemoveListener(Element target, EventKind kind, EventListener handler, bool useCapture) {
            if (target == null || handler == null) return false;
            if (!map.TryGetValue(target, out var list)) return false;
            for (int i = 0; i < list.Count; i++) {
                var r = list[i];
                if (r.Kind == kind && r.Handler == handler && r.UseCapture == useCapture) {
                    list.RemoveAt(i);
                    if (list.Count == 0) map.Remove(target);
                    return true;
                }
            }
            return false;
        }

        // Appends matching listeners into the caller's buffer. Allocation-free
        // when `into` already has capacity. Kept alongside the legacy
        // `GetListeners` API so existing tests / external callers compile;
        // EventDispatcher's hot path uses this overload via a single
        // per-dispatcher scratch list.
        public void AppendListeners(Element target, EventKind kind, EventPhase phase, List<EventListener> into) {
            if (into == null || target == null) return;
            if (!map.TryGetValue(target, out var list)) return;
            for (int i = 0; i < list.Count; i++) {
                var r = list[i];
                if (r.Kind != kind) continue;
                if (phase == EventPhase.Capture && !r.UseCapture) continue;
                if (phase == EventPhase.Bubble && r.UseCapture) continue;
                into.Add(r.Handler);
            }
        }

        public List<EventListener> GetListeners(Element target, EventKind kind, EventPhase phase) {
            var result = new List<EventListener>();
            AppendListeners(target, kind, phase, result);
            return result;
        }

        public bool HasAny(Element target) {
            return target != null && map.ContainsKey(target);
        }

        // Drops the entire listener list for `target`. Called by
        // EventDispatcher's DOM-mutation subscription when an element is
        // removed from the tree — without this, the Dictionary keeps a hard
        // reference to the orphaned Element and every registered listener
        // closure (and any objects those closures capture) for the lifetime
        // of the dispatcher. Returns true if an entry was actually removed.
        public bool RemoveAllForElement(Element target) {
            if (target == null) return false;
            return map.Remove(target);
        }

        // Subtree variant: walks the removed node and every descendant
        // top-down, dropping any listener-bearing element along the way. The
        // listener map is keyed by Element, so removing only the subtree root
        // would leak every descendant that had its own listeners. Safe to
        // call on a node with no descendants and on nodes whose subtree
        // contains zero listener-bearing elements — both are O(subtree size)
        // with no Dictionary churn beyond the actual removals.
        public int RemoveSubtree(Node root) {
            if (root == null) return 0;
            int removed = 0;
            if (root is Element e && map.Remove(e)) removed++;
            var kids = root.Children;
            for (int i = 0; i < kids.Count; i++) {
                removed += RemoveSubtree(kids[i]);
            }
            return removed;
        }

        // Test-only accessor: lets the leak-regression suite assert that the
        // dispatcher's mutation subscription has actually compacted the map.
        // Kept `internal` so production code cannot grow a dependency on the
        // dictionary's internal shape.
        internal bool ContainsKey(Element target) {
            return target != null && map.ContainsKey(target);
        }

        internal int Count => map.Count;
    }
}
