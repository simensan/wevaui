using System;
using Weva.Dom;

namespace Weva.Events.Manipulators {
    // PanManipulator — fires PanStart/PanMove/PanEnd events whenever the
    // pointer is dragged over the target element. PointerDown captures the
    // pointer and starts the gesture; PointerMove emits delta deltas;
    // PointerUp ends. The gesture is left-button-only by default (matches
    // canvas-dragging UX); set `Button` to override.
    //
    // Threshold lets the manipulator suppress micro-movements during a
    // click so that a quick down+up doesn't accidentally start a pan.
    // PanStart is delayed until the cumulative pointer travel exceeds the
    // threshold; the first PanMove carries the full delta from down to
    // current position.
    public sealed class PanManipulator : Manipulator {
        public int Button { get; set; } = 0;
        public double Threshold { get; set; } = 0.0;

        public event Action<double, double> PanStart; // (startX, startY)
        public event Action<double, double> PanMove;  // (dx, dy) delta from last event
        public event Action<double, double> PanEnd;   // (totalDx, totalDy) cumulative delta

        readonly EventListener pointerDown;
        readonly EventListener pointerMove;
        readonly EventListener pointerUp;

        bool active;
        bool gestureStarted;
        double startX, startY;
        double lastX, lastY;

        public PanManipulator(Element target, EventDispatcher dispatcher) : base(target, dispatcher) {
            pointerDown = OnPointerDown;
            pointerMove = OnPointerMove;
            pointerUp = OnPointerUp;
        }

        protected override void OnWire() {
            Dispatcher.AddEventListener(Target, EventKind.PointerDown, pointerDown);
            Dispatcher.AddEventListener(Target, EventKind.PointerMove, pointerMove);
            Dispatcher.AddEventListener(Target, EventKind.PointerUp, pointerUp);
        }

        protected override void OnUnwire() {
            Dispatcher.RemoveEventListener(Target, EventKind.PointerDown, pointerDown);
            Dispatcher.RemoveEventListener(Target, EventKind.PointerMove, pointerMove);
            Dispatcher.RemoveEventListener(Target, EventKind.PointerUp, pointerUp);
        }

        void OnPointerDown(UIEvent evt) {
            if (!(evt is PointerEvent pe) || pe.Button != Button) return;
            active = true;
            gestureStarted = false;
            startX = lastX = pe.X;
            startY = lastY = pe.Y;
            Dispatcher.SetPointerCapture(Target);
        }

        void OnPointerMove(UIEvent evt) {
            if (!active) return;
            if (!(evt is PointerEvent pe)) return;
            double dx = pe.X - lastX;
            double dy = pe.Y - lastY;
            if (!gestureStarted) {
                double total = Math.Sqrt((pe.X - startX) * (pe.X - startX) + (pe.Y - startY) * (pe.Y - startY));
                if (total < Threshold) return;
                gestureStarted = true;
                PanStart?.Invoke(startX, startY);
                // First emission carries the full down→current delta so a
                // listener that translates a transform doesn't snap the
                // first time PanMove fires.
                dx = pe.X - startX;
                dy = pe.Y - startY;
            }
            lastX = pe.X;
            lastY = pe.Y;
            PanMove?.Invoke(dx, dy);
        }

        void OnPointerUp(UIEvent evt) {
            if (!active) return;
            if (!(evt is PointerEvent pe) || pe.Button != Button) return;
            active = false;
            Dispatcher.ReleasePointerCapture(Target);
            if (gestureStarted) {
                PanEnd?.Invoke(pe.X - startX, pe.Y - startY);
            }
            gestureStarted = false;
        }
    }
}
