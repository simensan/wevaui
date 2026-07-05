using System;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class InputElement {
        public Element Element { get; }

        public InputElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "input")
                throw new ArgumentException($"InputElement requires a <input> element; got <{element.TagName}>", nameof(element));
            Element = element;
        }

        public string Type {
            get {
                var t = Element.GetAttribute("type");
                return string.IsNullOrEmpty(t) ? "text" : t;
            }
            set => Element.SetAttribute("type", value ?? "text");
        }

        public string Value {
            get => Element.GetAttribute("value") ?? "";
            set => Element.SetAttribute("value", value ?? "");
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

        public bool Checked {
            get => Element.HasAttribute("checked");
            set {
                if (value) Element.SetAttribute("checked", "");
                else Element.RemoveAttribute("checked");
            }
        }

        public string Min {
            get => Element.GetAttribute("min");
            set {
                if (value == null) Element.RemoveAttribute("min");
                else Element.SetAttribute("min", value);
            }
        }

        public string Max {
            get => Element.GetAttribute("max");
            set {
                if (value == null) Element.RemoveAttribute("max");
                else Element.SetAttribute("max", value);
            }
        }

        public string Step {
            get => Element.GetAttribute("step");
            set {
                if (value == null) Element.RemoveAttribute("step");
                else Element.SetAttribute("step", value);
            }
        }

        public int? MaxLength {
            get {
                var v = Element.GetAttribute("maxlength");
                if (string.IsNullOrEmpty(v)) return null;
                return int.TryParse(v, out var n) ? n : (int?)null;
            }
            set {
                if (!value.HasValue) Element.RemoveAttribute("maxlength");
                else Element.SetAttribute("maxlength", value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public bool IsTextual {
            get {
                var t = Type;
                return t == "text" || t == "password" || t == "search" || t == "email" ||
                       t == "tel" || t == "url" || t == "number";
            }
        }
    }
}
