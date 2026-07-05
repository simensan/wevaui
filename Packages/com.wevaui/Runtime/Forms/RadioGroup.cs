using System;
using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;

namespace Weva.Forms {
    // RadioGroup — collects radios sharing the same `name` attribute within a
    // scope (the nearest enclosing <form> or, if none, the document) and
    // enforces single-selection semantics: selecting one removes `checked`
    // from every other member.
    //
    // The group is materialized lazily by walking the scope; we don't subscribe
    // to DOM mutations because the scope is small and Select() is the only
    // mutating operation.
    public sealed class RadioGroup {
        public string Name { get; }
        public Node Scope { get; }

        public RadioGroup(string name, Node scope) {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Name = name;
            Scope = scope;
        }

        public IEnumerable<Element> Members() {
            return CollectRadios(Scope, Name);
        }

        public Element Selected {
            get {
                foreach (var r in Members()) {
                    if (r.HasAttribute("checked")) return r;
                }
                return null;
            }
        }

        public void Select(Element radio) {
            if (radio == null) throw new ArgumentNullException(nameof(radio));
            if (radio.GetAttribute("name") != Name) {
                throw new ArgumentException("Radio name does not match group", nameof(radio));
            }
            foreach (var r in Members()) {
                if (r == radio) {
                    r.SetAttribute("checked", "");
                } else {
                    r.RemoveAttribute("checked");
                }
            }
        }

        public static RadioGroup For(Element radio) {
            if (radio == null) throw new ArgumentNullException(nameof(radio));
            string name = radio.GetAttribute("name") ?? "";
            return new RadioGroup(name, ScopeOf(radio));
        }

        // Scope mirrors what InputController.FindRadioScope uses: nearest <form>,
        // else the document.
        public static Node ScopeOf(Element radio) {
            for (var n = radio.Parent as Element; n != null; n = n.Parent as Element) {
                if (n.TagName == "form") return n;
            }
            return radio.OwnerDocument;
        }

        // Wire — for tests and for programmatic flows that don't go through
        // InputController. Click on a radio selects it within its group and
        // fires `change`. InputController already handles this for full forms,
        // so wiring here is opt-in.
        public static void Wire(Element radio, EventDispatcher d) {
            if (radio == null) throw new ArgumentNullException(nameof(radio));
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (radio.TagName != "input" || radio.GetAttribute("type") != "radio") {
                throw new ArgumentException("Wire expects an <input type=\"radio\">", nameof(radio));
            }
            d.AddEventListener(radio, EventKind.Click, evt => {
                if (radio.HasAttribute("disabled")) return;
                var group = For(radio);
                group.Select(radio);
                d.StateProvider?.SetFlag(radio, Weva.Css.Selectors.ElementState.UserInteracted, true);
                FormSubmissionEvents.DispatchChange(d, radio);
            });
        }

        static IEnumerable<Element> CollectRadios(Node scope, string name) {
            foreach (var c in scope.Children) {
                if (c is Element e) {
                    if (e.TagName == "input"
                        && e.GetAttribute("type") == "radio"
                        && e.GetAttribute("name") == name) {
                        yield return e;
                    }
                    foreach (var nested in CollectRadios(e, name)) yield return nested;
                }
            }
        }
    }
}
