using System;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Forms {
    // SelectState — single-selection UI state for a `<select>` element.
    //
    // The DOM owns the source-of-truth (each `<option selected>` attribute);
    // SelectState adds an Open flag for popover rendering and a SelectedIndex
    // computed view that round-trips through the option list. v1 is single-
    // select only (PLAN.md §11 — "no multi-select").
    //
    // Wire(EventDispatcher) hooks click → toggle-open. Selection of an option
    // is performed externally (e.g. by the popover renderer) via SelectIndex
    // and fires `change`.
    public sealed class SelectState : IVersioned {
        public Element Element { get; }
        readonly SelectElement select;
        long version;
        bool open;

        EventDispatcher dispatcher;
        EventListener clickListener;
        bool wired;

        public event Action OpenChanged;
        public event Action SelectionChanged;

        public SelectState(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            select = new SelectElement(element);
            Element = element;
            version = 1;
        }

        public long Version => version;

        public bool Open {
            get => open;
            set {
                if (open == value) return;
                open = value;
                Bump();
                OpenChanged?.Invoke();
            }
        }

        public bool Disabled => Element.HasAttribute("disabled");

        // SelectedIndex — index into the flat option list (optgroups flattened),
        // matching the order yielded by SelectElement.Options. -1 if no option
        // is selected.
        public int SelectedIndex {
            get {
                int i = 0;
                int defaultIndex = -1;
                foreach (var o in select.Options) {
                    if (o.Selected) return i;
                    if (defaultIndex == -1) defaultIndex = i;
                    i++;
                }
                return defaultIndex;
            }
            set {
                if (value < 0) {
                    select.ClearSelection();
                    Bump();
                    SelectionChanged?.Invoke();
                    return;
                }
                int i = 0;
                bool any = false;
                foreach (var o in select.Options) {
                    bool match = i == value;
                    o.Selected = match;
                    if (match) any = true;
                    i++;
                }
                if (!any) {
                    select.ClearSelection();
                }
                Bump();
                SelectionChanged?.Invoke();
            }
        }

        public string Value => select.Value;

        // Select an option by index, fire `change`, and close the popover.
        public void SelectIndex(int index) {
            if (Disabled) return;
            int prev = SelectedIndex;
            SelectedIndex = index;
            Open = false;
            if (SelectedIndex != prev && dispatcher != null) {
                dispatcher.StateProvider?.SetFlag(Element, Weva.Css.Selectors.ElementState.UserInteracted, true);
                FormSubmissionEvents.DispatchChange(dispatcher, Element);
            }
        }

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
            Open = !Open;
        }

        void Bump() {
            unchecked { version++; }
        }
    }
}
