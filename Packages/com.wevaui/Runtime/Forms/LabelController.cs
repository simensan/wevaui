using System;
using Weva.Dom;
using Weva.Events;

namespace Weva.Forms {
    // LabelController — forwards a click on a <label> element to its referenced
    // form control, matching HTML's `activation behavior` for <label>:
    //   - <label for="targetId"> resolves the target by id
    //   - <label> with no `for` resolves to the first nested form control
    // The forwarded event is a synthetic click dispatched through the same
    // capture/target/bubble pipeline as a native click, so an InputController
    // bound to the target sees it as if the user clicked the input directly.
    //
    // This is the missing complement to LabelElement.ResolveTarget(): the DOM
    // helper existed but no controller listened. Without this, clicking
    //   <label for="cb1">Subscribe</label>
    // adjacent to <input id="cb1" type="checkbox"> did nothing — a silent a11y
    // gap surfaced by the menu.html demo and the prior-audit ignored test.
    public sealed class LabelController {
        public Element Element { get; }

        readonly EventDispatcher dispatcher;
        readonly EventListener clickListener;
        readonly LabelElement label;
        bool subscribed;
        bool dispatchingForward;

        public LabelController(Element element, EventDispatcher dispatcher) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (element.TagName != "label")
                throw new ArgumentException($"LabelController requires <label>; got <{element.TagName}>", nameof(element));
            Element = element;
            this.dispatcher = dispatcher;
            this.label = new LabelElement(element);
            this.clickListener = OnClick;
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
            // Re-entrancy guard: the forwarded synthetic click bubbles up from
            // the input through the label (since the input is a descendant or
            // shares a doc with the label). Dispatching a fresh click would
            // loop forever otherwise.
            if (dispatchingForward) return;

            // If the click already targets a form control nested INSIDE the
            // label, the browser does not synthesize a second click — the
            // direct click is the activation. evt.Target tells us where the
            // click actually originated.
            if (evt is PointerEvent pe && pe.Target is Element direct && IsFormControl(direct)) {
                return;
            }

            var target = label.ResolveTarget();
            if (target == null) return;
            if (target.HasAttribute("disabled")) return;

            // Build a synthetic click. We don't have the original coordinates
            // beyond what's in `evt`, but the InputController contract only
            // checks the event kind for checkbox/radio toggling — the X/Y
            // are unused by the toggle path.
            var click = new PointerEvent {
                Kind = EventKind.Click,
                Button = 0,
                Buttons = 0
            };
            try {
                dispatchingForward = true;
                dispatcher.DispatchSynthetic(click, target);
            } finally {
                dispatchingForward = false;
            }
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
