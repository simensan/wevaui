using System;
using Weva.Dom;
using Weva.Events;

namespace Weva.Forms {
    // FormSubmissionEvents — synthesizes the form-control event triad
    // (input / change / submit) on the appropriate elements via the existing
    // EventDispatcher pipeline. v1 spec scope:
    //   - `input` fires per keystroke on the focused control. InputController
    //     calls DispatchInput on every Model.Changed.
    //   - `change` fires on commit (blur for text inputs, click toggle for
    //     checkbox/radio, option-pick for select).
    //   - `submit` fires on the enclosing <form> when a textual control
    //     receives Enter while focused (and isn't multiline). FormElement.Submit
    //     also calls this so programmatic submission goes through the same path.
    //
    // None of these actually perform navigation. The handler is the API.
    public static class FormSubmissionEvents {
        public static void DispatchInput(EventDispatcher d, Element target) {
            if (d == null || target == null) return;
            var evt = new InputEvent { Kind = EventKind.Input };
            DispatchInternal(d, evt, target);
        }

        public static void DispatchChange(EventDispatcher d, Element target) {
            if (d == null || target == null) return;
            var evt = new InputEvent { Kind = EventKind.Change };
            DispatchInternal(d, evt, target);
        }

        public static void DispatchSubmit(EventDispatcher d, Element form, Element submitter) {
            if (d == null || form == null) return;
            if (form.TagName != "form") return;
            var evt = new SubmitFormEvent { Kind = EventKind.Submit, Submitter = submitter };
            DispatchInternal(d, evt, form);
        }

        // Resolves the form owner of a form-associated control per HTML Living
        // Standard §4.10.18.6: if the control has a `form` content attribute,
        // that attribute names a form by id and OVERRIDES ancestor association
        // (no fallback if the id does not resolve to a <form>). Otherwise the
        // nearest <form> ancestor wins. Used by InputController to decide
        // whether Enter on a single-line input should synthesize submit, and
        // mirrored by ButtonElement.FindEnclosingForm.
        public static Element FindEnclosingForm(Element control) {
            if (control == null) return null;
            var formId = control.GetAttribute("form");
            if (!string.IsNullOrEmpty(formId)) {
                var doc = control.OwnerDocument;
                if (doc == null) return null;
                var target = doc.GetElementById(formId);
                return (target != null && target.TagName == "form") ? target : null;
            }
            for (var n = control.Parent as Element; n != null; n = n.Parent as Element) {
                if (n.TagName == "form") return n;
            }
            return null;
        }

        static void DispatchInternal(EventDispatcher d, UIEvent evt, Element target) {
            // EventDispatcher.Dispatch is internal. Use reflection-free routing
            // via the public DispatchKeyDown surface? No — we expose a dedicated
            // path. EventDispatcher exposes `DispatchSyntheticEvent` below; if it
            // is not present, fall back to a thin subscription emit.
            d.DispatchSynthetic(evt, target);
        }
    }

    public sealed class InputEvent : UIEvent { }

    public sealed class SubmitFormEvent : UIEvent {
        public Element Submitter { get; internal set; }
    }
}
