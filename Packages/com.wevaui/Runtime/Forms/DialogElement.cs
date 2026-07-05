using System;
using Weva.Dom;

namespace Weva.Forms {
    // HTML <dialog> wrapper. Mirrors the DOM API:
    //   * Show()       — non-modal open: sets `open` attr, no backdrop, no
    //                    focus trap.
    //   * ShowModal()  — modal open: sets `open` attr, marks modal=true so
    //                    paint/event layers can render a ::backdrop and
    //                    block click-through.
    //   * Close(rv)    — clears `open`; stores ReturnValue; fires `close`.
    //   * Cancel       — fired by the host (e.g. Escape handler) before close.
    //
    // Modal-ness is tracked by a separate attribute (`data-modal`) so the
    // CSS author can target it via `dialog[data-modal] { ... }` if they
    // want a different style for modal vs. non-modal. The standard pseudo
    // is `:modal`, which v1 doesn't synthesize; the attribute is the
    // workaround.
    //
    // V1 simplifications:
    //   * Focus trap: not enforced. The dialog raises an event that the
    //     host can use to move focus to its first focusable child; if
    //     focus escapes via tab, we don't recapture.
    //   * `inert` on the rest of the DOM during ShowModal is not applied.
    public sealed class DialogElement {
        public Element Element { get; }

        public event Action<DialogElement> Closed;
        public event Action<DialogElement> Cancelled;

        public DialogElement(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (element.TagName != "dialog")
                throw new ArgumentException($"DialogElement requires a <dialog> element; got <{element.TagName}>", nameof(element));
            Element = element;
        }

        public bool Open {
            get => Element.HasAttribute("open");
            set {
                if (value) Element.SetAttribute("open", "");
                else Element.RemoveAttribute("open");
            }
        }

        public bool IsModal => Element.HasAttribute("data-modal");
        public string ReturnValue { get; set; } = "";

        public void Show() {
            Element.SetAttribute("open", "");
            Element.RemoveAttribute("data-modal");
        }

        public void ShowModal() {
            Element.SetAttribute("open", "");
            Element.SetAttribute("data-modal", "");
        }

        public void Close(string returnValue = null) {
            if (returnValue != null) ReturnValue = returnValue;
            bool wasOpen = Open;
            Element.RemoveAttribute("open");
            Element.RemoveAttribute("data-modal");
            if (wasOpen) Closed?.Invoke(this);
        }

        public void Cancel() {
            Cancelled?.Invoke(this);
            Close();
        }
    }
}
