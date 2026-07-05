using System;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class LabelElement {
        public Element Element { get; }

        public LabelElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "label")
                throw new ArgumentException($"LabelElement requires a <label> element; got <{element.TagName}>", nameof(element));
            Element = element;
        }

        public string For {
            get => Element.GetAttribute("for") ?? "";
            set => Element.SetAttribute("for", value ?? "");
        }

        public Element ResolveTarget() {
            var forId = For;
            if (!string.IsNullOrEmpty(forId)) {
                var doc = Element.OwnerDocument;
                if (doc != null) {
                    return doc.GetElementById(forId);
                }
            }
            return FindFirstFormControl(Element);
        }

        static Element FindFirstFormControl(Node n) {
            foreach (var child in n.Children) {
                if (child is Element e) {
                    if (IsFormControl(e)) return e;
                    var nested = FindFirstFormControl(e);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        static bool IsFormControl(Element e) {
            switch (e.TagName) {
                case "input":
                case "textarea":
                case "select":
                case "button":
                    return true;
                default:
                    return false;
            }
        }
    }
}
