using System;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Forms {
    // CheckboxState — drives `<input type="checkbox">` semantics.
    //
    // The DOM `checked` attribute is the source of truth. CheckboxState is the
    // ergonomic facade with reactive Version + Toggled event + indeterminate
    // tri-state (matches HTMLInputElement.indeterminate, which is a JS-only
    // property and has no attribute reflection in real browsers).
    //
    // Wire(EventDispatcher) hooks the click → toggle path and fires Change on
    // the dispatcher's listener registry. Without Wire, the state can still be
    // mutated programmatically.
    public sealed class CheckboxState : IVersioned {
        public Element Element { get; }
        long version;
        bool indeterminate;

        EventDispatcher dispatcher;
        EventListener clickListener;
        bool wired;

        public event Action Toggled;

        public CheckboxState(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "input") {
                throw new ArgumentException("CheckboxState requires <input>", nameof(element));
            }
            var t = element.GetAttribute("type");
            if (t != "checkbox") {
                throw new ArgumentException("CheckboxState requires type=\"checkbox\"", nameof(element));
            }
            Element = element;
            version = 1;
        }

        public long Version => version;

        public bool Checked {
            get => Element.HasAttribute("checked");
            set {
                if (value == Checked) return;
                if (value) Element.SetAttribute("checked", "");
                else Element.RemoveAttribute("checked");
                indeterminate = false;
                Bump();
                Toggled?.Invoke();
            }
        }

        public bool Indeterminate {
            get => indeterminate;
            set {
                if (indeterminate == value) return;
                indeterminate = value;
                Bump();
            }
        }

        public bool Disabled => Element.HasAttribute("disabled");

        public void Toggle() {
            if (Disabled) return;
            Checked = !Checked;
        }

        // Wire to an EventDispatcher so click toggles the checkbox and the
        // synthesized `change` event reaches handlers registered through
        // EventDispatcher.AddEventListener(EventKind.Change, ...).
        public void Wire(EventDispatcher d) {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (wired) return;
            dispatcher = d;
            clickListener = OnClick;
            d.AddEventListener(Element, EventKind.Click, clickListener);
            wired = true;
        }

        public void Unwire() {
            if (!wired) return;
            dispatcher.RemoveEventListener(Element, EventKind.Click, clickListener);
            wired = false;
            dispatcher = null;
        }

        void OnClick(UIEvent _) {
            if (Disabled) return;
            Toggle();
            dispatcher.StateProvider?.SetFlag(Element, Weva.Css.Selectors.ElementState.UserInteracted, true);
            FormSubmissionEvents.DispatchChange(dispatcher, Element);
        }

        void Bump() {
            unchecked { version++; }
        }
    }
}
