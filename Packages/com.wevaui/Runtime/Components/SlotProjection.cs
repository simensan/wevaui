using System;
using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Components {
    internal static class SlotProjection {
        // Walks the cloned template subtree and replaces every <slot> element with
        // the projected light-dom children. Slots without matching projection use
        // their fallback content; slots without fallback collapse and are removed.
        // The lightDomChildren list MUST be detached from any parent (caller has
        // already removed them from the host) — we AppendChild them into the slot's
        // parent at the slot's position.
        public static void Project(List<Node> clonedRoots, IList<Node> lightDomChildren) {
            if (clonedRoots == null) return;

            var defaultChildren = new List<Node>();
            var namedChildren = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
            if (lightDomChildren != null) {
                for (int i = 0; i < lightDomChildren.Count; i++) {
                    var child = lightDomChildren[i];
                    string slot = null;
                    if (child is Element e) slot = e.GetAttribute("slot");
                    if (string.IsNullOrEmpty(slot)) {
                        defaultChildren.Add(child);
                    } else {
                        if (!namedChildren.TryGetValue(slot, out var list)) {
                            list = new List<Node>();
                            namedChildren[slot] = list;
                        }
                        list.Add(child);
                    }
                }
            }

            // Find every slot inside cloned roots, then fill from outside-in so we
            // do not descend into projected content searching for nested slots.
            var slots = new List<Element>();
            for (int i = 0; i < clonedRoots.Count; i++) {
                CollectSlots(clonedRoots[i], slots);
            }

            // Track which projection lists have already been spent. The first
            // matching slot for a given name takes the original nodes;
            // subsequent slots get a deep clone so they don't steal the
            // already-projected subtree from the prior slot. Without this,
            // two `<slot></slot>` (or two `<slot name="x">`) in the same
            // template caused AppendChild to re-parent the same nodes,
            // emptying the first slot and leaving only the last with content.
            bool defaultConsumed = false;
            HashSet<string> namedConsumed = null;

            foreach (var slot in slots) {
                FillSlot(slot, defaultChildren, namedChildren, ref defaultConsumed, ref namedConsumed);
            }
        }

        static void CollectSlots(Node node, List<Element> sink) {
            if (node is Element e && string.Equals(e.TagName, "slot", StringComparison.OrdinalIgnoreCase)) {
                sink.Add(e);
                return;
            }
            for (int i = 0; i < node.Children.Count; i++) {
                CollectSlots(node.Children[i], sink);
            }
        }

        static void FillSlot(Element slot,
            List<Node> defaultChildren,
            Dictionary<string, List<Node>> namedChildren,
            ref bool defaultConsumed,
            ref HashSet<string> namedConsumed) {
            var parent = slot.Parent;
            if (parent == null) return;

            string name = slot.GetAttribute("name");
            List<Node> source;
            bool needsClone;
            if (string.IsNullOrEmpty(name)) {
                source = defaultChildren.Count > 0 ? defaultChildren : null;
                needsClone = defaultConsumed;
            } else {
                source = namedChildren.TryGetValue(name, out var list) && list.Count > 0 ? list : null;
                needsClone = namedConsumed != null && namedConsumed.Contains(name);
            }

            if (source == null) {
                // Use slot fallback content: detach slot's children and re-parent in slot's place.
                var fallback = new List<Node>(slot.Children);
                int slotIdx = IndexOfChild(parent, slot);
                parent.RemoveChild(slot);
                foreach (var f in fallback) {
                    slot.RemoveChild(f);
                }
                InsertAt(parent, fallback, slotIdx);
                return;
            }

            // Replace slot with projected children. First match consumes the
            // original nodes; subsequent matches get a deep clone (otherwise
            // AppendChild re-parents the SAME nodes into the second slot and
            // empties the first).
            List<Node> toInsert;
            if (needsClone) {
                toInsert = new List<Node>(source.Count);
                for (int i = 0; i < source.Count; i++) {
                    var c = TemplateInstantiator.Clone(source[i]);
                    if (c != null) toInsert.Add(c);
                }
            } else {
                toInsert = source;
                if (string.IsNullOrEmpty(name)) {
                    defaultConsumed = true;
                } else {
                    namedConsumed ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    namedConsumed.Add(name);
                }
            }

            int idx = IndexOfChild(parent, slot);
            parent.RemoveChild(slot);
            InsertAt(parent, toInsert, idx);
        }

        static int IndexOfChild(Node parent, Node child) {
            for (int i = 0; i < parent.Children.Count; i++) {
                if (parent.Children[i] == child) return i;
            }
            return -1;
        }

        // AppendChild always appends at the end. We need to insert at a specific
        // index, so we briefly detach the trailing siblings, append the new nodes,
        // then re-append the detached tail. Each operation flows through the standard
        // DOM mutation path so version and event bookkeeping is preserved.
        static void InsertAt(Node parent, List<Node> nodes, int index) {
            if (nodes == null || nodes.Count == 0) return;
            if (index < 0) index = parent.Children.Count;
            var tail = new List<Node>();
            while (parent.Children.Count > index) {
                var last = parent.Children[parent.Children.Count - 1];
                tail.Add(last);
                parent.RemoveChild(last);
            }
            tail.Reverse();
            foreach (var n in nodes) parent.AppendChild(n);
            foreach (var n in tail) parent.AppendChild(n);
        }
    }
}
