using System;
using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Reactive;

namespace Weva.Forms {
    // Drives a single-choice <select> (not multiple / not [size]): clicking it
    // opens a ContextMenu-style popup of the <option>s; picking one selects it,
    // dispatches `change`, and the popup dismisses itself. The closed select's
    // label is painted by BoxToPaintConverter.EmitSelectLabel from the selected
    // option, so no extra render wiring is needed.
    //
    // SelectState already exposed an Open flag + SelectedIndex but nothing ever
    // rendered the popup or performed the option pick ("performed externally by
    // the popover renderer" — which was never built). This controller is that
    // missing piece, reusing the existing ContextMenu popup machinery.
    public sealed class SelectController {
        public Element Element { get; }

        readonly EventDispatcher dispatcher;
        readonly Document doc;
        readonly InvalidationTracker tracker;
        readonly Func<Element, Box> elementToBox;
        readonly SelectElement select;
        readonly EventListener clickListener;
        bool subscribed;

        public SelectController(Element element, EventDispatcher dispatcher, Document doc,
                                InvalidationTracker tracker, Func<Element, Box> elementToBox) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (element.TagName != "select")
                throw new ArgumentException("SelectController requires <select>", nameof(element));
            Element = element;
            this.dispatcher = dispatcher;
            this.doc = doc;
            this.tracker = tracker;
            this.elementToBox = elementToBox;
            select = new SelectElement(element);
            clickListener = OnClick;
        }

        public void Wire() {
            if (subscribed) return;
            dispatcher.AddEventListener(Element, EventKind.Click, clickListener);
            subscribed = true;
        }

        public void Unwire() {
            if (!subscribed) return;
            dispatcher.RemoveEventListener(Element, EventKind.Click, clickListener);
            subscribed = false;
        }

        void OnClick(UIEvent evt) {
            if (!(evt is PointerEvent pe) || pe.Button != 0) return;
            if (Element.HasAttribute("disabled")) return;
            // multiple / size selects render an inline listbox (the UA sheet lays
            // their options out as blocks) — they don't use the dropdown popup.
            if (select.Multiple || Element.HasAttribute("size")) return;
            if (doc == null) return;

            var opts = new List<OptionElement>(select.Options);
            if (opts.Count == 0) return;

            var items = new List<MenuItem>(opts.Count);
            for (int i = 0; i < opts.Count; i++) {
                int idx = i; // capture per-iteration index for the closure
                var opt = opts[i];
                string label = !string.IsNullOrEmpty(opt.Label) ? opt.Label : opt.Value;
                items.Add(MenuItem.Item(label, () => SelectIndex(idx), disabled: opt.Disabled));
            }

            // Open the popup just below the select's box (absolute paint coords).
            AbsolutePosition(out double x, out double y);
            var box = elementToBox?.Invoke(Element);
            double dropY = y + (box != null ? box.Height : 0);
            ContextMenu.Show(doc, dispatcher, tracker, x, dropY, items);

            // Consume so the same click doesn't also trigger other handlers.
            pe.StopPropagation();
            pe.PreventDefault();
        }

        void SelectIndex(int idx) {
            var opts = new List<OptionElement>(select.Options);
            if (idx < 0 || idx >= opts.Count) return;
            // Single-select: exactly one option carries `selected`.
            for (int j = 0; j < opts.Count; j++) opts[j].Selected = (j == idx);
            // The label is painted from the parent <select>; a child-option attr
            // mutation may not invalidate it, so mark the select dirty explicitly.
            tracker?.MarkDirty(Element, InvalidationKind.Layout | InvalidationKind.Paint);
            FormSubmissionEvents.DispatchChange(dispatcher, Element);
        }

        // Absolute paint position of the select's box (sum of X/Y up the box tree,
        // mirroring RangeController.AbsoluteLeft — scroll offset is ignored, which
        // is fine for v1 popup placement).
        void AbsolutePosition(out double x, out double y) {
            x = 0; y = 0;
            var box = elementToBox?.Invoke(Element);
            for (var b = box; b != null; b = b.Parent) { x += b.X; y += b.Y; }
        }
    }
}
