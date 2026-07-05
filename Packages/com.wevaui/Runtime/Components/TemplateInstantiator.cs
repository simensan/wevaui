using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Components {
    internal static class TemplateInstantiator {
        // Public Clone helper handles Element and TextNode. Returned subtree has fresh
        // identities and is unparented; caller must AppendChild it somewhere.
        public static Node Clone(Node source) {
            if (source == null) return null;
            switch (source) {
                case Element e: return CloneElement(e);
                case TextNode t: return new TextNode(t.Data);
                default: return null;
            }
        }

        // Clones every direct child of the supplied template element. Multi-rooted
        // templates are supported: a template may declare any number of direct
        // children and they are all cloned in document order.
        public static List<Node> CloneTemplateBody(Element template) {
            var result = new List<Node>();
            if (template == null) return result;
            foreach (var child in template.Children) {
                var c = Clone(child);
                if (c != null) result.Add(c);
            }
            return result;
        }

        static Element CloneElement(Element source) {
            var copy = new Element(source.TagName);
            foreach (var kv in source.Attributes) {
                copy.SetAttribute(kv.Key, kv.Value);
            }
            foreach (var child in source.Children) {
                var c = Clone(child);
                if (c != null) copy.AppendChild(c);
            }
            return copy;
        }
    }
}
