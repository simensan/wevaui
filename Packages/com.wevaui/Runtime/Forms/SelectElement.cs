using System;
using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Forms {
    public sealed class SelectElement {
        public Element Element { get; }

        public SelectElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "select")
                throw new ArgumentException($"SelectElement requires a <select> element; got <{element.TagName}>", nameof(element));
            Element = element;
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

        public bool Multiple => Element.HasAttribute("multiple");
        public bool Required => Element.HasAttribute("required");

        public IEnumerable<OptionElement> Options {
            get {
                foreach (var opt in CollectOptions(Element)) yield return new OptionElement(opt);
            }
        }

        public OptionElement SelectedOption {
            get {
                OptionElement first = null;
                foreach (var o in Options) {
                    if (o.Selected) return o;
                    if (first == null) first = o;
                }
                if (Multiple) return null;
                return first;
            }
        }

        public IEnumerable<OptionElement> SelectedOptions {
            get {
                foreach (var o in Options) if (o.Selected) yield return o;
            }
        }

        public string Value {
            get {
                var sel = SelectedOption;
                return sel == null ? "" : sel.Value;
            }
            set {
                bool any = false;
                foreach (var o in Options) {
                    bool match = !any && o.Value == value;
                    if (match) {
                        o.Selected = true;
                        any = true;
                    } else {
                        if (!Multiple) o.Selected = false;
                    }
                }
            }
        }

        public void ClearSelection() {
            foreach (var o in Options) o.Selected = false;
        }

        static IEnumerable<Element> CollectOptions(Node node) {
            foreach (var child in node.Children) {
                if (child is Element e) {
                    if (e.TagName == "option") yield return e;
                    else if (e.TagName == "optgroup") {
                        foreach (var nested in CollectOptions(e)) yield return nested;
                    }
                }
            }
        }
    }
}
