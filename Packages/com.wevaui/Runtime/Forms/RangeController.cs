using System;
using System.Globalization;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;

namespace Weva.Forms {
    // Drives a single `<input type="range">` element. PointerDown on the
    // track sets the value to the click position; the controller then
    // captures the pointer (so drag continues outside the track) and
    // updates the value on every PointerMove until PointerUp. Keyboard
    // arrows step by `step`; PageUp/PageDown jump by 10×step; Home/End
    // snap to min/max. Each value change writes back through `value="…"`
    // and dispatches the standard `input` event; the final commit on
    // pointer-up or keyboard-step also dispatches `change`.
    //
    // Value math: parsed from min/max/step attributes (defaults 0/100/1),
    // numeric strings; clamped to [Min, Max] and quantized to the nearest
    // step grid point relative to Min. Mirrors HTMLInputElement behavior.
    public sealed class RangeController {
        public Element Element { get; }
        readonly EventDispatcher dispatcher;
        readonly Func<Element, Box> elementToBox;

        readonly EventListener pointerDown;
        readonly EventListener pointerMove;
        readonly EventListener pointerUp;
        readonly EventListener key;

        bool subscribed;
        bool dragging;
        double valueAtDragStart;

        public event Action ValueChanged;
        public event Action ValueCommitted;

        public RangeController(Element element, EventDispatcher dispatcher, Func<Element, Box> elementToBox) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (element.TagName != "input") throw new ArgumentException("RangeController requires <input>", nameof(element));
            var t = element.GetAttribute("type");
            if (t != "range") throw new ArgumentException("RangeController requires type=\"range\"", nameof(element));
            Element = element;
            this.dispatcher = dispatcher;
            this.elementToBox = elementToBox;
            pointerDown = OnPointerDown;
            pointerMove = OnPointerMove;
            pointerUp = OnPointerUp;
            key = OnKey;
        }

        public double Min => ParseNumeric(Element.GetAttribute("min"), 0);
        public double Max => ParseNumeric(Element.GetAttribute("max"), 100);
        public double Step {
            get {
                var raw = Element.GetAttribute("step");
                // "any" (or absent) → 0, which ClampAndSnap treats as "no quantization".
                if (string.IsNullOrEmpty(raw) || raw == "any") return 0;
                double v = ParseNumeric(raw, 1);
                // HTML spec §4.10.5.1.13: step must be a positive floating-point
                // number. A value of zero, negative, or NaN is invalid and must be
                // treated as the default (1 for range). Return 0 only for "any".
                return (v > 0 && !double.IsInfinity(v)) ? v : 1.0;
            }
        }

        public double Value {
            get {
                var raw = Element.GetAttribute("value");
                if (string.IsNullOrEmpty(raw)) return Min + (Max - Min) * 0.5;
                double parsed = ParseNumeric(raw, Min);
                // Per HTML §4.10.5.1.13 (range input): if the value is not a
                // valid number (NaN), default to the spec's midpoint.
                if (double.IsNaN(parsed) || double.IsInfinity(parsed)) {
                    return Min + (Max - Min) * 0.5;
                }
                return ClampAndSnap(parsed);
            }
            set {
                double clamped = ClampAndSnap(value);
                var serialized = clamped.ToString("R", CultureInfo.InvariantCulture);
                if (Element.GetAttribute("value") == serialized) return;
                Element.SetAttribute("value", serialized);
                ValueChanged?.Invoke();
                FormSubmissionEvents.DispatchInput(dispatcher, Element);
            }
        }

        public bool Disabled => Element.HasAttribute("disabled");

        public void Wire() {
            if (subscribed) return;
            dispatcher.AddEventListener(Element, EventKind.PointerDown, pointerDown);
            dispatcher.AddEventListener(Element, EventKind.PointerMove, pointerMove);
            dispatcher.AddEventListener(Element, EventKind.PointerUp, pointerUp);
            dispatcher.AddEventListener(Element, EventKind.KeyDown, key);
            subscribed = true;
        }

        public void Unwire() {
            if (!subscribed) return;
            dispatcher.RemoveEventListener(Element, EventKind.PointerDown, pointerDown);
            dispatcher.RemoveEventListener(Element, EventKind.PointerMove, pointerMove);
            dispatcher.RemoveEventListener(Element, EventKind.PointerUp, pointerUp);
            dispatcher.RemoveEventListener(Element, EventKind.KeyDown, key);
            subscribed = false;
        }

        void OnPointerDown(UIEvent evt) {
            if (Disabled) return;
            if (!(evt is PointerEvent pe) || pe.Button != 0) return;
            MarkUserInteracted();
            valueAtDragStart = Value;
            dragging = true;
            // Capture so subsequent moves/up come to us even if the pointer
            // wanders off the track. Released automatically on PointerUp.
            dispatcher.SetPointerCapture(Element);
            // Consume the gesture so it does NOT also arm a drag-scroll on an
            // ancestor scroll container (otherwise dragging the slider pans the
            // page).
            pe.StopPropagation();
            UpdateFromPointer(pe.X);
        }

        void OnPointerMove(UIEvent evt) {
            if (!dragging) return;
            if (!(evt is PointerEvent pe)) return;
            pe.StopPropagation();
            UpdateFromPointer(pe.X);
        }

        void OnPointerUp(UIEvent evt) {
            if (!dragging) return;
            dragging = false;
            if (evt is PointerEvent pe) pe.StopPropagation();
            dispatcher.ReleasePointerCapture(Element);
            // Only fire change if the value actually moved — matches DOM
            // semantics where a click that doesn't change the value is a
            // no-op as far as `change` is concerned.
            if (Math.Abs(Value - valueAtDragStart) > 1e-9) {
                FormSubmissionEvents.DispatchChange(dispatcher, Element);
                ValueCommitted?.Invoke();
            }
        }

        void OnKey(UIEvent evt) {
            if (Disabled) return;
            if (!(evt is KeyboardEvent ke)) return;
            double step = Step > 0 ? Step : 1.0;
            double bigStep = step * 10.0;
            double current = Value;
            double next = current;
            switch (ke.Key) {
                case "ArrowLeft":
                case "ArrowDown": next = current - step; break;
                case "ArrowRight":
                case "ArrowUp":   next = current + step; break;
                case "PageDown":  next = current - bigStep; break;
                case "PageUp":    next = current + bigStep; break;
                case "Home":      next = Min; break;
                case "End":       next = Max; break;
                default: return;
            }
            ke.PreventDefault();
            if (Math.Abs(next - current) < 1e-12) return;
            MarkUserInteracted();
            Value = next;
            FormSubmissionEvents.DispatchChange(dispatcher, Element);
            ValueCommitted?.Invoke();
        }

        void UpdateFromPointer(double pointerX) {
            var box = elementToBox?.Invoke(Element);
            if (box == null || box.Width <= 0) return;
            double absX = AbsoluteLeft(box);
            double trackLeft = absX + box.PaddingLeft + box.BorderLeft;
            double trackWidth = box.Width - box.PaddingLeft - box.PaddingRight - box.BorderLeft - box.BorderRight;
            if (trackWidth <= 0) return;
            double t = (pointerX - trackLeft) / trackWidth;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            Value = Min + t * (Max - Min);
        }

        static double AbsoluteLeft(Box box) {
            double x = 0;
            for (var b = box; b != null; b = b.Parent) x += b.X;
            return x;
        }

        double ClampAndSnap(double value) {
            double mn = Min, mx = Max;
            if (mx < mn) mx = mn;
            if (value < mn) value = mn;
            else if (value > mx) value = mx;
            // Per HTML §4.10.5.1.13 (range input step): step="any" → no
            // quantization (Step getter returns 0). Step <= 0 (negative or
            // missing-but-explicit-invalid) → treat as 1.0, matching the
            // keyboard path's `Step > 0 ? Step : 1.0`. The asymmetry between
            // ClampAndSnap and OnKey was a TG15-flagged bug.
            double step = Step;
            if (step < 0) step = 1.0;
            if (step == 0) {
                // step="any" — skip quantization, return the clamped value.
            } else {
                // Per HTML §4.10.5.1.13, range-input quantization rounds the
                // half-step boundary AWAY FROM ZERO (the spec's "step base"
                // bias-toward-larger), not banker's round-to-even.
                double k = Math.Round((value - mn) / step, MidpointRounding.AwayFromZero);
                value = mn + k * step;
                if (value < mn) value = mn;
                else if (value > mx) value = mx;
            }
            return value;
        }

        static double ParseNumeric(string raw, double fallback) {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        void MarkUserInteracted() {
            dispatcher?.StateProvider?.SetFlag(Element, Weva.Css.Selectors.ElementState.UserInteracted, true);
        }
    }
}
