using System;
using System.Text;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class OptionElement {
        public Element Element { get; }

        public OptionElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "option")
                throw new ArgumentException($"OptionElement requires a <option> element; got <{element.TagName}>", nameof(element));
            Element = element;
        }

        public string Value {
            get {
                if (Element.HasAttribute("value")) return Element.GetAttribute("value") ?? "";
                var sb = new StringBuilder();
                CollectText(Element, sb);
                return sb.ToString();
            }
            set => Element.SetAttribute("value", value ?? "");
        }

        public string Label {
            get {
                if (Element.HasAttribute("label")) return Element.GetAttribute("label") ?? "";
                var sb = new StringBuilder();
                CollectText(Element, sb);
                return sb.ToString();
            }
        }

        public bool Selected {
            get => Element.HasAttribute("selected");
            set {
                if (value) Element.SetAttribute("selected", "");
                else Element.RemoveAttribute("selected");
            }
        }

        public bool Disabled {
            get => Element.HasAttribute("disabled");
            set {
                if (value) Element.SetAttribute("disabled", "");
                else Element.RemoveAttribute("disabled");
            }
        }

        static void CollectText(Node node, StringBuilder sb) {
            foreach (var child in node.Children) {
                if (child is TextNode t) sb.Append(t.Data);
                else CollectText(child, sb);
            }
        }
    }
}
