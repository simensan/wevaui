using System;
using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Events {
    internal sealed class FocusManager {
        public Func<Element, bool> IsHidden { get; set; }

        // PA7: hoisted scratch lists for NextFocusable. The previous implementation
        // allocated three Lists per Tab keypress (two collection buckets +
        // ordered merge buffer). Hoisting to per-instance fields amortises the
        // alloc to zero in steady state — Clear() preserves the backing array
        // and the high-water-mark grows to the largest tab-order ever seen.
        // Lifetime: NextFocusable is the sole user. Calls are synchronous and
        // not re-entrant (FocusManager has no callbacks back into user code),
        // so a single reusable triple is safe.
        readonly List<Element> scratchPositives = new();
        readonly List<Element> scratchNaturals = new();
        readonly List<Element> scratchOrdered = new();

        public bool IsFocusable(Element e) {
            if (e == null) return false;
            if (IsDisabled(e)) return false;
            if (IsHidden != null && IsHidden(e)) return false;

            var ti = ParseTabIndex(e);
            if (ti.HasValue) return ti.Value >= 0;

            return IsNaturallyFocusable(e);
        }

        public bool IsProgrammaticallyFocusable(Element e) {
            if (e == null) return false;
            if (IsDisabled(e)) return false;
            if (IsHidden != null && IsHidden(e)) return false;
            var ti = ParseTabIndex(e);
            if (ti.HasValue) return true;
            return IsNaturallyFocusable(e);
        }

        public int TabIndex(Element e) {
            if (e == null) return -1;
            if (IsDisabled(e)) return -1;
            var ti = ParseTabIndex(e);
            if (ti.HasValue) return ti.Value;
            return IsNaturallyFocusable(e) ? 0 : -1;
        }

        public Element NextFocusable(Document doc, Element fromElement, bool reverse) {
            if (doc == null) return null;
            // PA7: reuse per-instance scratch buckets. Cleared on entry +
            // before return so reentrant callers (synthetic focus dispatch
            // inside a handler) see fresh state; we don't actually re-enter
            // today but the symmetry pins the contract. Capacity is preserved
            // across calls — first Tab after a tree grows pays the resize,
            // every subsequent press is alloc-free.
            var positives = scratchPositives;
            var naturals = scratchNaturals;
            var ordered = scratchOrdered;
            positives.Clear();
            naturals.Clear();
            ordered.Clear();
            try {
                CollectFocusables(doc, positives, naturals);

                positives.Sort(TabIndexComparison);

                ordered.AddRange(positives);
                ordered.AddRange(naturals);
                if (ordered.Count == 0) return null;

                if (reverse) ordered.Reverse();

                if (fromElement == null) return ordered[0];
                int idx = ordered.IndexOf(fromElement);
                if (idx < 0) return ordered[0];
                return ordered[(idx + 1) % ordered.Count];
            } finally {
                positives.Clear();
                naturals.Clear();
                ordered.Clear();
            }
        }

        // Cached Comparison<Element> to avoid the per-call lambda capture/
        // delegate allocation. TabIndex is an instance method so the
        // delegate over `this` is allocated once at construction.
        Comparison<Element> tabIndexComparison;
        Comparison<Element> TabIndexComparison =>
            tabIndexComparison ??= (a, b) => TabIndex(a).CompareTo(TabIndex(b));

        void CollectFocusables(Node n, List<Element> positives, List<Element> naturals) {
            foreach (var c in n.Children) {
                if (c is Element e) {
                    if (IsHidden != null && IsHidden(e)) continue;
                    if (!IsDisabled(e)) {
                        var ti = ParseTabIndex(e);
                        if (ti.HasValue) {
                            if (ti.Value > 0) positives.Add(e);
                            else if (ti.Value == 0) naturals.Add(e);
                        } else if (IsNaturallyFocusable(e)) {
                            naturals.Add(e);
                        }
                    }
                    CollectFocusables(e, positives, naturals);
                } else {
                    CollectFocusables(c, positives, naturals);
                }
            }
        }

        static int? ParseTabIndex(Element e) {
            if (!e.HasAttribute("tabindex")) return null;
            var v = e.GetAttribute("tabindex");
            if (string.IsNullOrEmpty(v)) return null;
            return int.TryParse(v, out var n) ? n : (int?)null;
        }

        static bool IsNaturallyFocusable(Element e) {
            switch (e.TagName) {
                case "button":
                case "input":
                case "select":
                case "textarea":
                    return true;
                case "a":
                    return e.HasAttribute("href");
                default:
                    return false;
            }
        }

        public static bool IsDisabled(Element e) {
            return e != null && e.HasAttribute("disabled");
        }
    }
}
