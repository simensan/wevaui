using System;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class ButtonElement {
        public Element Element { get; }

        public ButtonElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "button")
                throw new ArgumentException($"ButtonElement requires a <button> element; got <{element.TagName}>", nameof(element));
            Element = element;
        }

        public string Type {
            get {
                var t = Element.GetAttribute("type");
                return string.IsNullOrEmpty(t) ? "submit" : t;
            }
            set => Element.SetAttribute("type", value ?? "submit");
        }

        public string Name {
            get => Element.GetAttribute("name") ?? "";
            set => Element.SetAttribute("name", value ?? "");
        }

        public string Value {
            get => Element.GetAttribute("value") ?? "";
            set => Element.SetAttribute("value", value ?? "");
        }

        public bool Disabled {
            get => Element.HasAttribute("disabled");
            set {
                if (value) Element.SetAttribute("disabled", "");
                else Element.RemoveAttribute("disabled");
            }
        }

        public bool IsSubmit => Type == "submit";
        public bool IsReset => Type == "reset";

        public Element FindEnclosingForm() {
            // HTML Living Standard §4.10.18.6 form-associated elements: if the
            // element carries a `form` content attribute, that attribute names
            // a form element by id and overrides any ancestor association. If
            // the id does not resolve to a <form>, the element has NO form
            // owner (no fallback to ancestor walk). Otherwise the nearest
            // <form> ancestor is the form owner.
            var formId = Element.GetAttribute("form");
            if (!string.IsNullOrEmpty(formId)) {
                var doc = Element.OwnerDocument;
                if (doc == null) return null;
                var target = doc.GetElementById(formId);
                return (target != null && target.TagName == "form") ? target : null;
            }
            for (var n = Element.Parent as Element; n != null; n = n.Parent as Element) {
                if (n.TagName == "form") return n;
            }
            return null;
        }
    }
}
