using System;
using System.Collections.Generic;
using System.Text;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.DevTools {
    // Chrome DevTools "Elements" panel — flat ordered DOM tree model.
    //
    // DomTreeModel flattens a Weva.Dom.Document into a linear list of DomTreeNode
    // entries, each carrying depth, parent linkage, a stable integer Id, and a
    // Label formatted like Chrome DevTools ("element.style", class/id attrs shown).
    //
    // The Version counter bumps on each Rebuild() call. The editor window polls
    // IsDirty (set true by Document.Mutated subscription) and calls Rebuild() when
    // the document changes, then resets IsDirty.
    //
    // No Unity APIs — headless-testable.
    public sealed class DomTreeModel {
        // Stable Id counter. Each Element gets a unique Id per Rebuild() call;
        // TextNodes get a negative Id so callers can distinguish them.
        int nextId = 1;
        int nextTextId = -1;

        // Ordered flat list rebuilt by Rebuild().
        readonly List<DomTreeNode> nodes = new List<DomTreeNode>(64);

        // Map from Element reference to node Id for fast lookup.
        readonly Dictionary<Element, int> elementToId = new Dictionary<Element, int>(64);

        // Bumped on each Rebuild().
        public int Version { get; private set; }

        // Set true when the subscribed Document fires Mutated; reset by the
        // caller (the EditorWindow) after it has called Rebuild().
        public bool IsDirty { get; set; }

        // The document whose Mutated event we are subscribed to. We hold a weak
        // reference to avoid keeping dead documents alive.
        Document subscribedDoc;
        Action<DomMutation> mutationHandler;

        // --- public read-only view ---
        public IReadOnlyList<DomTreeNode> Nodes => nodes;
        public int Count => nodes.Count;

        // Subscribe to a document's Mutated event. Unsubscribes from any previous doc.
        public void SubscribeTo(Document doc) {
            UnsubscribeFromCurrent();
            if (doc == null) return;
            subscribedDoc = doc;
            mutationHandler = _ => IsDirty = true;
            doc.Mutated += mutationHandler;
        }

        // Unsubscribe from the currently subscribed document (if any).
        public void UnsubscribeFromCurrent() {
            if (subscribedDoc != null && mutationHandler != null) {
                subscribedDoc.Mutated -= mutationHandler;
            }
            subscribedDoc = null;
            mutationHandler = null;
        }

        // Rebuild the flat list from the supplied document root.
        // Bumps Version and clears IsDirty.
        public void Rebuild(Document doc) {
            nodes.Clear();
            elementToId.Clear();
            nextId = 1;
            nextTextId = -1;

            if (doc != null) {
                Walk(doc, depth: 0, parentId: 0);
            }

            Version++;
            IsDirty = false;
        }

        // Lookup the node for a given Element (by reference identity).
        // Returns null if not found or if the last Rebuild didn't include it.
        public DomTreeNode FindNode(Element element) {
            if (element == null) return null;
            if (!elementToId.TryGetValue(element, out var id)) return null;
            foreach (var n in nodes) {
                if (n.Id == id) return n;
            }
            return null;
        }

        // Walk depth-first, adding nodes in document order.
        void Walk(Node node, int depth, int parentId) {
            if (node is Document doc) {
                // Don't emit a node for Document itself — walk its children.
                foreach (var child in doc.Children) {
                    Walk(child, depth, parentId: 0);
                }
                return;
            }

            if (node is Element element) {
                int id = nextId++;
                elementToId[element] = id;
                var label = BuildElementLabel(element);
                nodes.Add(new DomTreeNode(id, depth, parentId, element, null, label));
                foreach (var child in element.Children) {
                    Walk(child, depth + 1, parentId: id);
                }
                return;
            }

            if (node is TextNode textNode) {
                // Chrome's Elements panel hides whitespace-only text nodes —
                // inter-tag newlines/indentation would otherwise litter the
                // tree with empty "" entries.
                if (string.IsNullOrWhiteSpace(textNode.Data)) return;
                int id = nextTextId--;
                var preview = BuildTextPreview(textNode.Data);
                var label = preview;
                nodes.Add(new DomTreeNode(id, depth, parentId, null, textNode, label));
                // TextNodes have no children.
            }
        }

        // Build a Chrome-style element label: <tag#id.class> with extra attrs elided.
        //   <div id="card" class="foo bar">   → <div id="card" class="foo bar">
        //   (other attributes elided with " …" when present)
        static string BuildElementLabel(Element element) {
            var sb = new StringBuilder(64);
            sb.Append('<');
            sb.Append(element.TagName);

            bool hasOtherAttrs = false;

            // id first, class second, rest elided.
            string id = element.GetAttribute("id");
            string cls = element.GetAttribute("class");

            if (!string.IsNullOrEmpty(id)) {
                sb.Append(" id=\"").Append(id).Append('"');
            }
            if (!string.IsNullOrEmpty(cls)) {
                sb.Append(" class=\"").Append(cls).Append('"');
            }

            // Check for other attributes (AttributeMap enumerates as KVP).
            foreach (var kv in element.Attributes) {
                if (kv.Key == "id" || kv.Key == "class") continue;
                hasOtherAttrs = true;
                break;
            }
            if (hasOtherAttrs) {
                sb.Append(" …"); // horizontal ellipsis
            }

            sb.Append('>');
            return sb.ToString();
        }

        // Quoted preview of text node content, trimmed to ~60 chars.
        static string BuildTextPreview(string data) {
            if (data == null) return "\"\"";
            data = data.Trim();
            const int maxLen = 60;
            if (data.Length > maxLen) {
                data = data.Substring(0, maxLen) + "…";
            }
            return "\"" + data + "\"";
        }
    }

    // One entry in the flat DomTreeModel node list.
    // Id > 0 for elements; Id < 0 for text nodes.
    public sealed class DomTreeNode {
        public readonly int Id;
        public readonly int Depth;
        public readonly int ParentId;

        // One of Element / TextNode will be set; the other will be null.
        public readonly Element Element;     // null for text nodes
        public readonly TextNode TextNode;   // null for element nodes

        // Chrome-style label: "<div id="x" class="card">" or "\"preview text\""
        public readonly string Label;

        public bool IsElement => Element != null;
        public bool IsTextNode => TextNode != null;

        internal DomTreeNode(int id, int depth, int parentId,
                             Element element, TextNode textNode,
                             string label) {
            Id = id;
            Depth = depth;
            ParentId = parentId;
            Element = element;
            TextNode = textNode;
            Label = label;
        }
    }
}
