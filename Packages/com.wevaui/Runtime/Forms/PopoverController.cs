using System;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Events;

namespace Weva.Forms {
    // Listens for `popovertarget` button clicks, Escape key, and outside
    // clicks; mutates a PopoverStack accordingly.
    //
    // Wiring contract:
    //   * The orchestrator constructs a controller per WevaDocument (one stack
    //     per document, since popovers are document-scoped).
    //   * The controller subscribes to the document's bubbling events:
    //       Click   — both popovertarget invocation and outside-click dismiss
    //       KeyDown — Escape closes top auto popover
    //   * Stack mutation is synchronous; CSS sees the `data-popover-open`
    //     attribute change on the next cascade pass.
    //
    // The hit target for a click is `event.Target`. We walk ancestors looking
    // for a `popovertarget` attribute; that gives us the trigger element.
    // The trigger references a popover element by id.
    public sealed class PopoverController {
        readonly Document doc;
        readonly PopoverStack stack;

        public PopoverController(Document doc, PopoverStack stack = null) {
            this.doc = doc ?? throw new ArgumentNullException(nameof(doc));
            this.stack = stack ?? new PopoverStack();
        }

        public PopoverStack Stack => stack;

        // Wires into an EventDispatcher. Idempotent in the sense that
        // re-wiring after disposal of the dispatcher requires reconstructing
        // the controller; we don't keep a removable handler list here.
        public void Wire(EventDispatcher dispatcher) {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            // Bubble-phase listeners on the root element catch the event
            // after the natural target's handlers run. The dispatcher
            // requires a target Element; we walk the doc to find the root.
            Element root = null;
            foreach (var c in doc.Children) {
                if (c is Element e) { root = e; break; }
            }
            if (root == null) return;
            dispatcher.AddEventListener(root, EventKind.Click, OnClick);
            dispatcher.AddEventListener(root, EventKind.KeyDown, OnKey);
        }

        void OnClick(UIEvent evt) {
            if (evt.Target == null) return;
            // Pass 1: was this click on (or inside) a popovertarget button?
            var trigger = FindPopoverTrigger(evt.Target);
            if (trigger != null) {
                HandleTrigger(trigger);
                return;
            }
            // Pass 2: light-dismiss. If we have an auto popover on the
            // stack and the click was NOT inside that popover, close it.
            var top = stack.Top;
            if (top != null && Popover.GetMode(top) == "auto" && !IsInside(evt.Target, top)) {
                stack.Hide(top);
            }
        }

        void OnKey(UIEvent evt) {
            if (evt is KeyboardEvent ke && ke.Key == "Escape") {
                stack.HideTopAuto();
            }
        }

        public void HandleTrigger(Element trigger) {
            if (trigger == null) return;
            string targetId = trigger.GetAttribute("popovertarget");
            if (string.IsNullOrEmpty(targetId)) return;
            var target = doc.GetElementById(targetId);
            if (target == null) return;
            string action = trigger.GetAttribute("popovertargetaction");
            if (string.IsNullOrEmpty(action)) action = "toggle";
            switch (CssStringUtil.ToLowerInvariantOrSame(action)) {
                case "show":
                    stack.Show(target);
                    break;
                case "hide":
                    stack.Hide(target);
                    break;
                default:
                    stack.Toggle(target);
                    break;
            }
        }

        static Element FindPopoverTrigger(Element start) {
            for (var n = start; n != null; n = n.Parent as Element) {
                if (n.HasAttribute("popovertarget")) return n;
            }
            return null;
        }

        static bool IsInside(Element candidate, Element ancestor) {
            for (var n = candidate; n != null; n = n.Parent as Element) {
                if (ReferenceEquals(n, ancestor)) return true;
            }
            return false;
        }
    }
}
