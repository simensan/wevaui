using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Weva.Dom;

namespace Weva.Compiled {
    // A read-only frozen view of a Document at one point in time, laid out as
    // struct-of-arrays for cache-friendly traversal. Any DOM mutation
    // invalidates the snapshot; rebuild via DomSnapshot.Build after mutating,
    // or refill an existing instance via DomSnapshot.Refill.
    //
    // Conventions:
    //   - NodeId is an int index in [0, NodeCount). 0 is conventionally the
    //     Document node when present.
    //   - Symbol id 0 means "absent." Tag/Id/class/attr-name/attr-value lookups
    //     of 0 are guaranteed to map to "" via the symbol table.
    //   - -1 in any link array (FirstChild, NextSibling, Parent, FirstAttribute)
    //     means "no such node/attribute."
    //   - Children appear in document order.
    internal sealed class DomSnapshot {
        public SymbolTable Symbols { get; private set; }

        public NodeKind[] Kinds;
        public int[] TagSymbols;
        public int[] IdSymbols;
        public int[] FirstChild;
        public int[] NextSibling;
        public int[] Parent;
        public int[] FirstAttribute;
        public int[] AttributeCount;

        public int[] ClassRangeOffset;
        public int[] ClassRangeCount;
        public int[] ClassSymbols;

        public int[] AttributeNames;
        public int[] AttributeValues;

        public string[] TextValues;

        // Sidecar: parallel managed Element references keyed by NodeId. Non-element
        // entries are null. Held only so the snapshot matcher can delegate the
        // verification pass back to the existing managed SelectorMatcher; once
        // SnapshotMatcher gains a snapshot-native verifier this can drop.
        public Dom.Node[] ManagedNodes;

        // Reverse index Node → NodeId built during Build/Refill, used by
        // RefreshNode for per-node incremental refresh. Without this the
        // cascade would have to scan ManagedNodes linearly to find the
        // slot for a mutated node — O(N) per refresh defeats the win.
        // Cleared on every full Refill so stale entries from prior tree
        // shapes don't survive.
        readonly Dictionary<Dom.Node, int> nodeToId = new();

        // Expose the Node-to-NodeId index for SnapshotPassState so it can
        // reuse the already-built mapping instead of rebuilding its own
        // ElementToNodeId dictionary from a full ManagedNodes scan on every
        // ComputeAll. The returned dictionary is the live backing store —
        // read-only callers must not mutate it. It is cleared and rebuilt on
        // every Refill so the snapshot identity tracks the dict version.
        internal IReadOnlyDictionary<Dom.Node, int> NodeToIdMap => nodeToId;

        // Live counts. NodeCount is the authoritative size used by consumers.
        // The backing arrays may be sized larger after a Refill against a
        // smaller document; the trailing slots are unused but kept for the
        // next Refill that may grow back. ClassSymbolsCount / AttributeNames-
        // Count likewise track the populated prefix of those buffers.
        int nodeCount;
        int classSymbolsCount;
        int attributesCount;

        public int RootId { get; private set; }
        public int NodeCount => nodeCount;

        DomSnapshot(SymbolTable symbols) {
            Symbols = symbols;
            Kinds = Array.Empty<NodeKind>();
            TagSymbols = Array.Empty<int>();
            IdSymbols = Array.Empty<int>();
            FirstChild = Array.Empty<int>();
            NextSibling = Array.Empty<int>();
            Parent = Array.Empty<int>();
            FirstAttribute = Array.Empty<int>();
            AttributeCount = Array.Empty<int>();
            ClassRangeOffset = Array.Empty<int>();
            ClassRangeCount = Array.Empty<int>();
            ClassSymbols = Array.Empty<int>();
            AttributeNames = Array.Empty<int>();
            AttributeValues = Array.Empty<int>();
            TextValues = Array.Empty<string>();
            ManagedNodes = Array.Empty<Dom.Node>();
        }

        // Walks the managed DOM tree, interns every tag/id/class/attr-name/attr-value
        // into symbols, and produces a flat snapshot. Skip rules (e.g. display:none)
        // are NOT applied here: the snapshot reflects the literal DOM. Cascade-driven
        // visibility is a separate concern.
        public static DomSnapshot Build(Document doc, SymbolTable symbols) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (symbols == null) throw new ArgumentNullException(nameof(symbols));
            var snap = new DomSnapshot(symbols);
            snap.Refill(doc, symbols);
            return snap;
        }

        // In-place refill: walks the document and overwrites the snapshot's
        // contents, growing arrays only when the new document is bigger than
        // any prior pass. Steady-state on a stable tree shape is zero-alloc.
        // Behavior is otherwise identical to a fresh Build — same NodeIds,
        // same parent/child links, same symbol interning. The supplied
        // SymbolTable may be the same instance the snapshot was built with
        // (preferred — preserves prior intern ids) or a fresh one (forces
        // re-interning of every string).
        public void Refill(Document doc) {
            Refill(doc, Symbols);
        }

        public void Refill(Document doc, SymbolTable symbols) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (symbols == null) throw new ArgumentNullException(nameof(symbols));
            Symbols = symbols;

            int total = CountNodes(doc);
            EnsureNodeCapacity(total);
            nodeCount = total;

            // Reset link arrays to "absent" sentinels for the live range. We
            // only touch the populated prefix; trailing capacity stays as-is
            // (it's invisible to consumers via NodeCount).
            for (int i = 0; i < total; i++) {
                FirstChild[i] = -1;
                NextSibling[i] = -1;
                Parent[i] = -1;
                FirstAttribute[i] = -1;
                AttributeCount[i] = 0;
                TagSymbols[i] = 0;
                IdSymbols[i] = 0;
                ClassRangeOffset[i] = 0;
                ClassRangeCount[i] = 0;
                TextValues[i] = null;
                ManagedNodes[i] = null;
            }

            classSymbolsCount = 0;
            attributesCount = 0;
            nodeToId.Clear();

            int next = 0;
            int rootId = AssignAndFill(doc, -1, ref next, this, symbols);
            RootId = rootId;
        }

        static int CountNodes(Node n) {
            int c = 1;
            // Indexed iteration avoids allocating the IReadOnlyList<Node>
            // enumerator that `foreach` would synthesize per recursive call.
            // With ~3000 nodes a foreach version costs ~3000 enumerator allocs
            // per Refill, which dominates the steady-state alloc budget.
            var children = n.Children;
            for (int i = 0; i < children.Count; i++) c += CountNodes(children[i]);
            return c;
        }

        // Append-with-grow into ClassSymbols / AttributeNames / AttributeValues
        // so callers don't need a List<int> staging buffer.
        void AppendClass(int sym) {
            EnsureClassCapacity(classSymbolsCount + 1);
            ClassSymbols[classSymbolsCount++] = sym;
        }

        void AppendAttribute(int nameSym, int valueSym) {
            EnsureAttributeCapacity(attributesCount + 1);
            AttributeNames[attributesCount] = nameSym;
            AttributeValues[attributesCount] = valueSym;
            attributesCount++;
        }

        void EnsureNodeCapacity(int min) {
            int cap = Kinds.Length;
            if (cap >= min) return;
            int next = cap == 0 ? 16 : cap * 2;
            while (next < min) next *= 2;
            Array.Resize(ref Kinds, next);
            Array.Resize(ref TagSymbols, next);
            Array.Resize(ref IdSymbols, next);
            Array.Resize(ref FirstChild, next);
            Array.Resize(ref NextSibling, next);
            Array.Resize(ref Parent, next);
            Array.Resize(ref FirstAttribute, next);
            Array.Resize(ref AttributeCount, next);
            Array.Resize(ref ClassRangeOffset, next);
            Array.Resize(ref ClassRangeCount, next);
            Array.Resize(ref TextValues, next);
            Array.Resize(ref ManagedNodes, next);
        }

        void EnsureClassCapacity(int min) {
            int cap = ClassSymbols.Length;
            if (cap >= min) return;
            int next = cap == 0 ? 16 : cap * 2;
            while (next < min) next *= 2;
            Array.Resize(ref ClassSymbols, next);
        }

        void EnsureAttributeCapacity(int min) {
            int cap = AttributeNames.Length;
            if (cap >= min) return;
            int next = cap == 0 ? 16 : cap * 2;
            while (next < min) next *= 2;
            Array.Resize(ref AttributeNames, next);
            Array.Resize(ref AttributeValues, next);
        }

        static int AssignAndFill(Node n, int parentId, ref int next, DomSnapshot snap, SymbolTable symbols) {
            int myId = next++;
            snap.Parent[myId] = parentId;
            snap.ManagedNodes[myId] = n;
            snap.nodeToId[n] = myId;

            if (n is Document) {
                snap.Kinds[myId] = NodeKind.Document;
            } else if (n is Element e) {
                snap.Kinds[myId] = NodeKind.Element;
                snap.TagSymbols[myId] = symbols.Intern(e.TagName);

                // ONE indexed pass over the AttributeMap (NameAt + ValueAt,
                // both O(1) list reads) captures id/class inline. The old
                // shape paid THREE OrdinalIgnoreCase dictionary hashes per
                // element (e.Id, e.ClassName, attrs[name] per attribute) —
                // the PERF-1 Mono bisect measured those at ~1.6ms of the
                // 3.1ms 2004-node Refill.
                string idAttr = null;
                string rawClass = null;
                int attrOffset = snap.attributesCount;
                int attrCount = 0;
                var attrs = e.Attributes;
                int attrTotal = attrs.Count;
                for (int ai = 0; ai < attrTotal; ai++) {
                    var name = attrs.NameAt(ai);
                    var value = attrs.ValueAt(ai);
                    // Names are canonical lowercase (AttributeMap.Canonicalize).
                    if (name.Length == 2 && name == "id") idAttr = value;
                    else if (name.Length == 5 && name == "class") rawClass = value;
                    snap.AppendAttribute(symbols.Intern(name), value == null ? 0 : symbols.Intern(value));
                    attrCount++;
                }
                if (attrCount > 0) {
                    snap.FirstAttribute[myId] = attrOffset;
                    snap.AttributeCount[myId] = attrCount;
                }

                snap.IdSymbols[myId] = string.IsNullOrEmpty(idAttr) ? 0 : symbols.Intern(idAttr);

                int classOffset = snap.classSymbolsCount;
                int classCount = 0;
                if (!string.IsNullOrEmpty(rawClass)) {
                    int len = rawClass.Length;
                    int i = 0;
                    while (i < len) {
                        while (i < len && IsWs(rawClass[i])) i++;
                        int start = i;
                        while (i < len && !IsWs(rawClass[i])) i++;
                        if (i > start) {
                            snap.AppendClass(symbols.Intern(rawClass, start, i - start));
                            classCount++;
                        }
                    }
                }
                snap.ClassRangeOffset[myId] = classOffset;
                snap.ClassRangeCount[myId] = classCount;
            } else if (n is TextNode t) {
                snap.Kinds[myId] = NodeKind.Text;
                snap.TextValues[myId] = t.Data;
            } else {
                snap.Kinds[myId] = NodeKind.None;
            }

            int prevSibling = -1;
            int firstChildId = -1;
            // Indexed iteration: avoids the per-recursion enumerator alloc that
            // foreach over IReadOnlyList<Node> would incur. See CountNodes for
            // the same rationale.
            var nChildren = n.Children;
            int nChildCount = nChildren.Count;
            for (int ci = 0; ci < nChildCount; ci++) {
                int childId = AssignAndFill(nChildren[ci], myId, ref next, snap, symbols);
                if (firstChildId < 0) firstChildId = childId;
                if (prevSibling >= 0) snap.NextSibling[prevSibling] = childId;
                prevSibling = childId;
            }
            snap.FirstChild[myId] = firstChildId;
            return myId;
        }

        // Incremental per-node refresh — re-extracts tag / id / class /
        // attribute data for one Node without rebuilding the whole
        // snapshot. Skips silently if the node isn't in the current
        // snapshot (orphan from a tree-shape change should go through
        // full Refill instead).
        //
        // Class/attribute slot reuse: this implementation APPENDS the
        // new entries to the end of ClassSymbols / AttributeNames /
        // AttributeValues and updates the node's offset to point at
        // the new range. Old slots are orphaned but not reclaimed —
        // they just waste space. A full Refill compacts. For a typical
        // interactive frame with <50 attribute mutations between full
        // rebuilds, the waste is bounded and the win is large
        // (avoiding O(N) interning for a single-node change).
        //
        // Tree-shape changes invalidate this path: the node's existing
        // nodeId may have moved or vanished. Callers must trigger a
        // full Refill for child-added/removed mutations and only use
        // RefreshNode for attribute / text mutations.
        public void RefreshNode(Node n, SymbolTable symbols) {
            if (n == null || symbols == null) return;
            if (!nodeToId.TryGetValue(n, out int id)) return;
            // Sanity: id must be within the populated range.
            if (id < 0 || id >= nodeCount) return;
            if (n is Element e) {
                Kinds[id] = NodeKind.Element;
                TagSymbols[id] = symbols.Intern(e.TagName);
                // ONE indexed pass (NameAt + ValueAt) with inline id/class
                // capture — same PERF-1 rationale as AssignAndFill.
                string idAttr = null;
                string rawClass = null;
                int newAttrOffset = attributesCount;
                int newAttrCount = 0;
                var attrs = e.Attributes;
                int attrTotal = attrs.Count;
                for (int ai = 0; ai < attrTotal; ai++) {
                    var name = attrs.NameAt(ai);
                    var value = attrs.ValueAt(ai);
                    if (name.Length == 2 && name == "id") idAttr = value;
                    else if (name.Length == 5 && name == "class") rawClass = value;
                    AppendAttribute(symbols.Intern(name), value == null ? 0 : symbols.Intern(value));
                    newAttrCount++;
                }
                if (newAttrCount > 0) {
                    FirstAttribute[id] = newAttrOffset;
                    AttributeCount[id] = newAttrCount;
                } else {
                    FirstAttribute[id] = -1;
                    AttributeCount[id] = 0;
                }
                IdSymbols[id] = string.IsNullOrEmpty(idAttr) ? 0 : symbols.Intern(idAttr);
                // Re-emit class symbols at the end of the packed array.
                int newClassOffset = classSymbolsCount;
                int newClassCount = 0;
                if (!string.IsNullOrEmpty(rawClass)) {
                    int len = rawClass.Length;
                    int i = 0;
                    while (i < len) {
                        while (i < len && IsWs(rawClass[i])) i++;
                        int start = i;
                        while (i < len && !IsWs(rawClass[i])) i++;
                        if (i > start) {
                            AppendClass(symbols.Intern(rawClass, start, i - start));
                            newClassCount++;
                        }
                    }
                }
                ClassRangeOffset[id] = newClassOffset;
                ClassRangeCount[id] = newClassCount;
            } else if (n is TextNode t) {
                Kinds[id] = NodeKind.Text;
                TextValues[id] = t.Data;
            }
        }

        // Estimate of how much packed-array space is orphaned due to
        // RefreshNode appends. Callers can use this to decide whether
        // to trigger a full Refill for compaction. Compares the live
        // sum of per-node ranges against the populated buffer prefix.
        public int OrphanedClassSlots {
            get {
                int live = 0;
                for (int i = 0; i < nodeCount; i++) live += ClassRangeCount[i];
                return classSymbolsCount - live;
            }
        }

        public int OrphanedAttributeSlots {
            get {
                int live = 0;
                for (int i = 0; i < nodeCount; i++) live += AttributeCount[i];
                return attributesCount - live;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<int> ClassesOf(int nodeId) {
            int off = ClassRangeOffset[nodeId];
            int cnt = ClassRangeCount[nodeId];
            if (cnt == 0) return ReadOnlySpan<int>.Empty;
            return new ReadOnlySpan<int>(ClassSymbols, off, cnt);
        }

        // Returns symbol id of attribute value if present, else 0.
        public int GetAttributeValue(int nodeId, int nameSym) {
            int off = FirstAttribute[nodeId];
            if (off < 0) return 0;
            int cnt = AttributeCount[nodeId];
            for (int i = 0; i < cnt; i++) {
                if (AttributeNames[off + i] == nameSym) return AttributeValues[off + i];
            }
            return 0;
        }
    }
}
