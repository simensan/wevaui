using System;
using Weva.Dom;
using Weva.Events;

namespace Weva.Forms {
    // Drives <details>/<summary>: clicking the (first) <summary> toggles the
    // `open` attribute on the parent <details>, which the UA stylesheet keys off
    // (details > * { display:none } / details[open] > * { display:block }) to
    // show/hide the disclosure body. The attribute mutation re-cascades, so no
    // explicit invalidation is needed.
    //
    // Without this the element rendered (UA styling present) but the disclosure
    // never opened/closed — a standard control that was completely inert.
    public sealed class DetailsController {
        public Element Element { get; }   // the <details>

        readonly EventDispatcher dispatcher;
        readonly EventListener clickListener;
        bool subscribed;

        public DetailsController(Element element, EventDispatcher dispatcher) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (element.TagName != "details")
                throw new ArgumentException("DetailsController requires <details>", nameof(element));
            Element = element;
            this.dispatcher = dispatcher;
            clickListener = OnClick;
        }

        public void Wire() {
            if (subscribed) return;
            // Listen on the <details>; a click on the summary bubbles up to here.
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
            // Only the first <summary> is the disclosure trigger; a click elsewhere
            // in the open body must not collapse it.
            if (!(pe.Target is Element target) || !WithinSummary(target)) return;
            if (Element.HasAttribute("open")) Element.RemoveAttribute("open");
            else Element.SetAttribute("open", "");
        }

        // True when `target` is the <details>'s first direct <summary> child, or a
        // descendant of it.
        bool WithinSummary(Element target) {
            for (Node n = target; n != null && !ReferenceEquals(n, Element); n = n.Parent) {
                if (n is Element e
                    && string.Equals(e.TagName, "summary", StringComparison.OrdinalIgnoreCase)
                    && ReferenceEquals(e.Parent, Element)) {
                    return true;
                }
            }
            return false;
        }
    }
}
