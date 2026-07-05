namespace Weva.Events {
    // Software-driven pointer that turns gamepad / D-pad / scripted input
    // into the same `EventDispatcher.DispatchPointer*` calls a real mouse
    // would produce. Game UIs without a hardware pointer (console games,
    // VR menus, key-only navigation) wire this up so the existing
    // `:hover` / `:active` / `:focus` cascade hooks fire correctly.
    //
    // The source itself is input-agnostic — it doesn't know about Unity's
    // Input System, Steam Deck buttons, or controller mappings. Game code
    // pumps it each frame with stick deltas and button state, and the
    // source forwards those into the event dispatcher. Because clicks are
    // synthesized by `EventDispatcher` from a matched down/up pair on the
    // same target, holding a button while the cursor is moving over the
    // target produces one click on release — same model as a real mouse.
    //
    // Memory: zero per-frame allocation in steady state. The cursor
    // position is two doubles; the dispatcher's hit-test path itself
    // allocates only on hover transitions.
    //
    // v1 scope: a single virtual pointer per source. Multi-cursor (split-
    // screen, multiple gamepads addressing one document) is deferred.
    public sealed class VirtualPointerSource {
        readonly EventDispatcher dispatcher;

        public VirtualPointerSource(EventDispatcher dispatcher) {
            this.dispatcher = dispatcher
                ?? throw new System.ArgumentNullException(nameof(dispatcher));
        }

        // Current cursor position in document-local screen pixels (the
        // same coordinate space `EventDispatcher.DispatchPointerMove`
        // accepts). Mutated via `Move`, `MoveTo`, or directly by callers
        // that have their own logic.
        public double X { get; set; }
        public double Y { get; set; }

        // Optional clamp bounds. When set, every `Move`/`MoveTo` call
        // saturates the cursor against this rect. Unset by default —
        // callers without a viewport just leave them as `null` and
        // manage edges themselves.
        public double? MinX { get; set; }
        public double? MinY { get; set; }
        public double? MaxX { get; set; }
        public double? MaxY { get; set; }

        // Modifier key state forwarded with every dispatched event.
        // Game code sets this when the player is holding shift/ctrl on
        // a hardware keyboard simultaneously with gamepad input. Default
        // is no modifiers, which matches the typical controller-only flow.
        public KeyModifiers Modifiers { get; set; }

        // Buttons currently held down. Bitmask of `1 << button`. Public
        // so tests can introspect. Mutated by `ButtonDown`/`ButtonUp`.
        public int ButtonsHeld { get; private set; }

        public void SetViewport(double minX, double minY, double maxX, double maxY) {
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        public void ClearViewport() {
            MinX = null; MinY = null; MaxX = null; MaxY = null;
        }

        public bool IsButtonDown(int button) {
            return (ButtonsHeld & (1 << button)) != 0;
        }

        // Absolute move. Dispatches a PointerMove event after clamping.
        // Idempotent on identical positions — duplicate moves still fire
        // the event because hover-enter / hover-leave can be consumers
        // that depend on the dispatch even if the coordinate didn't
        // change (e.g. when an underlying box moved).
        public void MoveTo(double x, double y) {
            X = Clamp(x, MinX, MaxX);
            Y = Clamp(y, MinY, MaxY);
            dispatcher.DispatchPointerMove(X, Y, Modifiers);
        }

        // Relative move. Convenience wrapper for `MoveTo(X + dx, Y + dy)`
        // — the pattern game code uses every frame when integrating a
        // gamepad stick reading scaled by frame delta.
        public void Move(double dx, double dy) {
            MoveTo(X + dx, Y + dy);
        }

        // High-level helper for "stick * speed * dt". Saves callers the
        // common multiplication when they want a fixed pixels/sec speed.
        // Returns true if the cursor actually moved (sub-pixel motion is
        // still applied; this is a hint for callers that want to skip
        // dispatch when the stick is at rest).
        public bool AdvanceFromStick(double stickX, double stickY, double pixelsPerSecond, double deltaSeconds) {
            if (stickX == 0 && stickY == 0) return false;
            Move(stickX * pixelsPerSecond * deltaSeconds, stickY * pixelsPerSecond * deltaSeconds);
            return true;
        }

        public void ButtonDown(int button) {
            ButtonsHeld |= 1 << button;
            dispatcher.DispatchPointerDown(X, Y, button, Modifiers);
        }

        public void ButtonUp(int button) {
            ButtonsHeld &= ~(1 << button);
            dispatcher.DispatchPointerUp(X, Y, button, Modifiers);
        }

        // Press-and-release at the current position. The two events are
        // dispatched back-to-back; `EventDispatcher` synthesises a click
        // because down-target equals up-target. Most controller-driven
        // UIs wire this to the controller's primary button (A on Xbox,
        // X on PlayStation, the south face button on Switch).
        public void Click(int button = 0) {
            ButtonDown(button);
            ButtonUp(button);
        }

        static double Clamp(double v, double? min, double? max) {
            if (min.HasValue && v < min.Value) v = min.Value;
            if (max.HasValue && v > max.Value) v = max.Value;
            return v;
        }
    }
}
