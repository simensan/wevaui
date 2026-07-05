using Weva.Dom;

namespace Weva.Events {
    public abstract class UIEvent {
        public EventKind Kind { get; internal set; }
        public Element Target { get; internal set; }
        public Element CurrentTarget { get; internal set; }
        public EventPhase Phase { get; internal set; }
        public bool DefaultPrevented { get; private set; }
        public bool PropagationStopped { get; private set; }
        public bool ImmediatePropagationStopped { get; private set; }
        public double TimestampSeconds { get; internal set; }

        public bool Bubbles { get; internal set; } = true;

        public void PreventDefault() {
            DefaultPrevented = true;
        }

        public void StopPropagation() {
            PropagationStopped = true;
        }

        public void StopImmediatePropagation() {
            PropagationStopped = true;
            ImmediatePropagationStopped = true;
        }

        // Reset the sticky-flag state (DefaultPrevented / PropagationStopped /
        // ImmediatePropagationStopped / Target / CurrentTarget / Phase /
        // TimestampSeconds) so a pooled instance can be re-dispatched without
        // leaking state from its previous use. Payload fields owned by
        // subclasses (X/Y on PointerEvent, etc.) are reset by the subclass.
        // See PointerEvent's lifetime-contract block for the full rationale.
        protected void ResetUIEventState() {
            DefaultPrevented = false;
            PropagationStopped = false;
            ImmediatePropagationStopped = false;
            Target = null;
            CurrentTarget = null;
            Phase = EventPhase.Capture;
            TimestampSeconds = 0;
        }
    }
}
