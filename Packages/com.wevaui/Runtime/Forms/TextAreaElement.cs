using System;
using System.Text;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class TextAreaElement {
        public Element Element { get; }

        public TextAreaElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "textarea")
                throw new ArgumentException($"TextAreaElement requires a <textarea> element; got <{element.TagName}>", nameof(element));
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

        static void CollectText(Node node, StringBuilder sb) {
            foreach (var child in node.Children) {
                if (child is TextNode t) sb.Append(t.Data);
                else CollectText(child, sb);
            }
        }

        public string Placeholder {
            get => Element.GetAttribute("placeholder") ?? "";
            set => Element.SetAttribute("placeholder", value ?? "");
        }

        public string Name {
            get => Element.GetAttribute("name") ?? "";
            set => Element.SetAttribute("name", value ?? "");
        }

        public bool Disabled {
            get => Element.HasAttribute("disabled");
            set {
                if (value) Element.SetAttribute("disabled", "");
                else Element.RemoveAttribute("disabled");
            }
        }

        public bool ReadOnly {
            get => Element.HasAttribute("readonly");
            set {
                if (value) Element.SetAttribute("readonly", "");
                else Element.RemoveAttribute("readonly");
            }
        }

        public bool Required {
            get => Element.HasAttribute("required");
            set {
                if (value) Element.SetAttribute("required", "");
                else Element.RemoveAttribute("required");
            }
        }

        public int? Rows {
            get {
                var v = Element.GetAttribute("rows");
                if (string.IsNullOrEmpty(v)) return null;
                return int.TryParse(v, out var n) ? n : (int?)null;
            }
        }

        public int? Cols {
            get {
                var v = Element.GetAttribute("cols");
                if (string.IsNullOrEmpty(v)) return null;
                return int.TryParse(v, out var n) ? n : (int?)null;
            }
        }
    }
}
