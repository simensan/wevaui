using System;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Events.Manipulators {
    // ContextualMenuManipulator — fires `MenuRequested` whenever the user
    // performs the platform context-menu gesture on the target. Classic
    // gestures handled:
    //
    //   * right-click (PointerDown with button == 2)
    //   * Shift+F10 keyboard shortcut while focused
    //   * long-press (touch only) — synthesized by holding pointer-down
    //     beyond LongPressSeconds without moving more than ~5 px
    //
    // The handler receives screen-space (x, y) coordinates so callers can
    // position their menu/popover near the trigger. Authors typically pair
    // this with a `<dialog>` or popover element they show in the handler.
    public sealed class ContextualMenuManipulator : Manipulator {
        public double LongPressSeconds { get; set; } = 0.6;
        public double LongPressMoveTolerance { get; set; } = 5.0;

        public event Action<double, double> MenuRequested; // (x, y) in document pixels

        readonly EventListener pointerDown;
        readonly EventListener pointerMove;
        readonly EventListener pointerUp;
        readonly EventListener key;
        readonly IUIClock clock;

        bool pressing;
        double pressStartSeconds;
        double pressStartX, pressStartY;
        double lastMoveX, lastMoveY;
        bool longPressFired;

        public ContextualMenuManipulator(Element target, EventDispatcher dispatcher, IUIClock clock = null)
            : base(target, dispatcher) {
            this.clock = clock ?? new SystemUIClock();
            pointerDown = OnPointerDown;
            pointerMove = OnPointerMove;
            pointerUp = OnPointerUp;
            key = OnKey;
        }

        protected override void OnWire() {
            Dispatcher.AddEventListener(Target, EventKind.PointerDown, pointerDown);
            Dispatcher.AddEventListener(Target, EventKind.PointerMove, pointerMove);
            Dispatcher.AddEventListener(Target, EventKind.PointerUp, pointerUp);
            Dispatcher.AddEventListener(Target, EventKind.KeyDown, key);
        }

        protected override void OnUnwire() {
            Dispatcher.RemoveEventListener(Target, EventKind.PointerDown, pointerDown);
            Dispatcher.RemoveEventListener(Target, EventKind.PointerMove, pointerMove);
            Dispatcher.RemoveEventListener(Target, EventKind.PointerUp, pointerUp);
            Dispatcher.RemoveEventListener(Target, EventKind.KeyDown, key);
        }

        // Drives the long-press timer. Lifecycle.Update calls Tick on every
        // active manipulator (or you can rely on PointerMove to keep the
        // gesture alive — moves typically arrive at framerate). Without
        // Tick a stationary touch wouldn't synthesize a long-press until
        // the next pointer event lands.
        public void Tick() {
            if (!pressing || longPressFired) return;
            if (clock.NowSeconds - pressStartSeconds < LongPressSeconds) return;
            longPressFired = true;
            MenuRequested?.Invoke(lastMoveX, lastMoveY);
        }

        void OnPointerDown(UIEvent evt) {
            if (!(evt is PointerEvent pe)) return;
            if (pe.Button == 2) {
                MenuRequested?.Invoke(pe.X, pe.Y);
                evt.PreventDefault();
                return;
            }
            // Touch / left-button hold path. We don't differentiate touch vs
            // mouse here — a mouse user holding LMB for the long-press
            // duration also triggers the menu, which matches mobile-style
            // touch UIs that work on desktop.
            if (pe.Button == 0) {
                pressing = true;
                longPressFired = false;
                pressStartSeconds = clock.NowSeconds;
                pressStartX = lastMoveX = pe.X;
                pressStartY = lastMoveY = pe.Y;
            }
        }

        void OnPointerMove(UIEvent evt) {
            if (!pressing) return;
            if (!(evt is PointerEvent pe)) return;
            lastMoveX = pe.X;
            lastMoveY = pe.Y;
            double dx = pe.X - pressStartX;
            double dy = pe.Y - pressStartY;
            if (dx * dx + dy * dy > LongPressMoveTolerance * LongPressMoveTolerance) {
                // Pointer wandered — cancel the long-press but stay in the
                // pressing state so PointerUp still resets cleanly.
                longPressFired = true;
            }
            // If the duration has elapsed, fire here too so an active
            // gesture progressing through PointerMove still triggers
            // (don't depend on Tick being wired).
            if (!longPressFired && clock.NowSeconds - pressStartSeconds >= LongPressSeconds) {
                longPressFired = true;
                MenuRequested?.Invoke(lastMoveX, lastMoveY);
            }
        }

        void OnPointerUp(UIEvent evt) {
            pressing = false;
            longPressFired = false;
        }

        void OnKey(UIEvent evt) {
            if (!(evt is KeyboardEvent ke)) return;
            if (!ke.ShiftKey) return;
            if (ke.Key != "F10") return;
            // Use the keyboard target's hit-testable position via the focus
            // origin — callers can compute it themselves; we pass (0,0) and
            // let the menu position itself relative to the target.
            ke.PreventDefault();
            MenuRequested?.Invoke(0, 0);
        }
    }
}
