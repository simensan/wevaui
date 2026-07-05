namespace Weva.Events {
    // LIFETIME / REUSE CONTRACT (P21 — added 2026-05-24):
    // Instances dispatched by `EventDispatcher.DispatchPointer{Down,Up,Move}`
    // and by `UpdateHover` (enter/leave fan-out) are RENTED FROM A POOL and
    // RETURNED on dispatch completion. Handlers MUST NOT capture the event
    // reference past the synchronous invocation of the handler. If a handler
    // needs to retain pointer data beyond the call, it MUST snapshot the
    // primitive fields it cares about (X, Y, Button, Buttons, modifier flags)
    // — not the event object itself.
    //
    // Synthetic events created by callers (DispatchSynthetic, tests that
    // construct `new PointerEvent { ... }` directly) are owned by the caller
    // and are NOT pooled — those remain safe to retain. The pool only ever
    // returns instances it itself constructed via `EventDispatcher.RentPointerEvent`.
    //
    // Field semantics: every dispatch populates ALL public mutable fields
    // (Kind, X, Y, Button, Buttons, modifiers, Bubbles) plus the inherited
    // UIEvent state. The dispatcher calls ResetForReuse() on rent to clear
    // the stop-propagation / default-prevented bits that a previous dispatch
    // may have left set.
    public sealed class PointerEvent : UIEvent {
        public double X { get; internal set; }
        public double Y { get; internal set; }
        public int Button { get; internal set; }
        public int Buttons { get; internal set; }
        public bool ShiftKey { get; internal set; }
        public bool CtrlKey { get; internal set; }
        public bool AltKey { get; internal set; }
        public bool MetaKey { get; internal set; }
        // DOM UIEvent.detail for pointer/click events: the click-streak count
        // (1 = single, 2 = double, 3 = triple). The dispatcher counts streaks
        // by time + proximity on button-0 downs (input/selection audit #4);
        // consumers key word/paragraph selection modes off it. Always ≥ 1 on
        // a dispatched down/click; 0 on move/up events that carry no streak.
        public int Detail { get; internal set; }

        public PointerEvent() {
            Bubbles = true;
        }

        // Called by the dispatcher's pool on rent. Resets every field that
        // a previously-dispatched PointerEvent could have left in a non-default
        // state (stop-propagation bits inherited from UIEvent, Bubbles toggled
        // false by an enter/leave dispatch, etc.). All payload fields are
        // overwritten immediately after the rent so we only reset the
        // sticky-flag fields here, not the payload.
        internal void ResetForReuse() {
            Bubbles = true;
            // Detail is only assigned on pointer-DOWN dispatches; clear it so
            // pooled reuse for move/up events doesn't leak a stale streak.
            Detail = 0;
            ResetUIEventState();
        }
    }
}
