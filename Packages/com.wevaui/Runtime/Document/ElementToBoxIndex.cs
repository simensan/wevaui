using System.Collections.Generic;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.Documents {
    // Maintains a flat Element -> Box mapping rebuilt each layout pass. The
    // BoxToPaintConverter wants a Func<Element, Box> so it can map dirty
    // elements onto boxes when applying invalidation. We expose Lookup
    // directly (matches the converter's signature) and keep a property for
    // sanity checks in tests.
    public sealed class ElementToBoxIndex {
        readonly Dictionary<Element, Box> map = new();

        public int Count => map.Count;

        public void Clear() => map.Clear();

        public void Rebuild(Box root) {
            map.Clear();
            if (root == null) return;
            Walk(root);
        }

        public Box Lookup(Element e) {
            if (e == null) return null;
            return map.TryGetValue(e, out var b) ? b : null;
        }

        public bool TryGet(Element e, out Box box) {
            if (e == null) { box = null; return false; }
            return map.TryGetValue(e, out box);
        }

        void Walk(Box box) {
            if (box.Element != null) {
                // The element's PRINCIPAL box wins (A-BUTTON-BOXINDEX).
                // TextRun fragments share their element's pointer for
                // styling and are visited AFTER the principal box in this
                // depth-first walk — the previous unconditional assignment
                // left every text-bearing element mapped to its LAST text
                // fragment (local X/Y, fragment-sized), which broke
                // element-keyed consumers (DevTools pin, DirtyHighlighter
                // box resolution). A TextRun only registers when no
                // principal box exists for the element (bare-text edge).
                if (!(box is TextRun) || !map.ContainsKey(box.Element)) {
                    map[box.Element] = box;
                }
            }
            for (int i = 0; i < box.Children.Count; i++) {
                Walk(box.Children[i]);
            }
        }
    }
}
