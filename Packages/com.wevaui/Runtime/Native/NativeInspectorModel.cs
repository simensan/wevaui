// What an Elements panel shows for a document hosted by the core, built on
// the C ABI's inspector surface (minor 27): the element tree, and for one
// element its matched rules, computed style and box model. A plain model
// with no editor types so it can be tested headlessly; the editor window
// renders it.
using System;
using System.Collections.Generic;

namespace Weva.Native
{
    internal sealed class NativeInspectorModel
    {
        public sealed class Node
        {
            public uint Element;
            public string Tag, Id, Class;
            public int Depth;
            public readonly List<Node> Children = new List<Node>();

            /// <summary>tag#id.class.class, the way an Elements panel labels a node.</summary>
            public string Label
            {
                get
                {
                    string label = Tag;
                    if (!string.IsNullOrEmpty(Id)) label += "#" + Id;
                    if (!string.IsNullOrEmpty(Class)) label += "." + Class.Trim().Replace(' ', '.');
                    return label;
                }
            }
        }

        public sealed class RuleBlock
        {
            public string Selector;      // empty for the style attribute
            public string Origin;
            public string Layer;
            public string Specificity;
            public bool Inline;
            public readonly List<NativeDocument.MatchedRule> Declarations = new List<NativeDocument.MatchedRule>();
        }

        private readonly NativeDocument _doc;

        public Node Root { get; private set; }
        public int NodeCount { get; private set; }
        public uint Selected { get; private set; } = WevaNative.WEVA_ELEMENT_NONE;
        public List<RuleBlock> Rules { get; } = new List<RuleBlock>();
        public List<KeyValuePair<string, string>> Computed { get; } = new List<KeyValuePair<string, string>>();
        public NativeDocument.BoxModel Box;
        public bool HasBox { get; private set; }
        public NativeBounds Bounds;

        public NativeInspectorModel(NativeDocument doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        /// <summary>Rebuilds the tree from the document root; the selection is re-read if it still exists.</summary>
        public void Rebuild()
        {
            uint html = _doc.Query("html");
            NodeCount = 0;
            Root = html == WevaNative.WEVA_ELEMENT_NONE ? null : Build(html, 0);
            if (Selected != WevaNative.WEVA_ELEMENT_NONE)
                Select(Find(Selected) != null ? Selected : WevaNative.WEVA_ELEMENT_NONE);
        }

        private Node Build(uint element, int depth)
        {
            var node = new Node
            {
                Element = element,
                Tag = _doc.TagName(element),
                Id = _doc.ElementAttribute(element, "id"),
                Class = _doc.ElementAttribute(element, "class"),
                Depth = depth,
            };
            NodeCount++;
            foreach (uint child in _doc.Children(element)) node.Children.Add(Build(child, depth + 1));
            return node;
        }

        /// <summary>The node for an element handle, or null.</summary>
        public Node Find(uint element)
        {
            return Root == null ? null : Find(Root, element);
        }

        private static Node Find(Node node, uint element)
        {
            if (node.Element == element) return node;
            foreach (Node child in node.Children)
            {
                Node found = Find(child, element);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Every node whose label contains the text (case-insensitive), in document order.</summary>
        public List<Node> Search(string text)
        {
            var hits = new List<Node>();
            if (Root == null || string.IsNullOrEmpty(text)) return hits;
            void Walk(Node n)
            {
                if (n.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(n);
                foreach (Node c in n.Children) Walk(c);
            }
            Walk(Root);
            return hits;
        }

        /// <summary>Reads the matched rules, computed style and box model of one element.</summary>
        public void Select(uint element)
        {
            Selected = element;
            Rules.Clear();
            Computed.Clear();
            HasBox = false;
            if (element == WevaNative.WEVA_ELEMENT_NONE) return;
            // Rules are grouped into blocks by rule (selector, origin, layer),
            // winners first: the Styles pane reads top-down from what applied.
            List<NativeDocument.MatchedRule> matched = _doc.MatchedRules(element);
            RuleBlock current = null;
            for (int i = matched.Count - 1; i >= 0; i--)
            {
                NativeDocument.MatchedRule r = matched[i];
                if (current == null || current.Selector != r.Selector || current.Origin != r.Origin ||
                    current.Layer != r.Layer || current.Inline != r.Inline || current.Specificity != r.Specificity)
                {
                    current = new RuleBlock { Selector = r.Selector, Origin = r.Origin, Layer = r.Layer, Specificity = r.Specificity, Inline = r.Inline };
                    Rules.Add(current);
                }
                current.Declarations.Insert(0, r);
            }
            Computed.AddRange(_doc.ComputedStyle(element));
            HasBox = _doc.TryGetBoxModel(element, out Box) && _doc.TryGetBounds(element, out Bounds);
        }

        /// <summary>The element under a document point, selected. Returns false when nothing is there.</summary>
        public bool SelectAt(double x, double y)
        {
            uint e = _doc.ElementAt(x, y);
            if (e == WevaNative.WEVA_ELEMENT_NONE) return false;
            Select(e);
            return true;
        }
    }
}
