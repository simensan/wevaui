using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Events.Manipulators;
using Weva.Parsing;

namespace Weva.Tests.Events.Manipulators {
    // TG33 — direct coverage for PanManipulator. Pins:
    //   * threshold-debounce: pointer-down + small move within Threshold does
    //     NOT raise PanStart/PanMove
    //   * threshold cross: once cumulative travel exceeds Threshold, PanStart
    //     fires with the down-position and the FIRST PanMove carries the full
    //     down→current delta (not just the per-event step)
    //   * Button filter: a non-matching button on PointerDown is ignored (the
    //     gesture never arms, so subsequent moves on the captured pointer do
    //     nothing)
    //   * PanEnd carries the cumulative delta; per-event deltas after the
    //     first PanMove are step deltas (regression guard for delta math).
    public class PanManipulatorTests {
        static EventDispatcher Build(out Element target) {
            var doc = HtmlParser.Parse("<div id=\"drag\"></div>");
            target = doc.GetElementById("drag");
            var ht = new FakeHitTester();
            ht.Add(target, 0, 0, 1000, 1000);
            return new EventDispatcher(doc, ht, new FakeUIClock());
        }

        [Test]
        public void PointerMove_within_threshold_does_not_fire_PanStart_or_PanMove() {
            var dispatcher = Build(out var target);
            var pan = new PanManipulator(target, dispatcher) { Threshold = 10.0 };
            pan.Wire();
            int starts = 0;
            int moves = 0;
            pan.PanStart += (_, _) => starts++;
            pan.PanMove += (_, _) => moves++;

            dispatcher.DispatchPointerDown(100, 100, button: 0, KeyModifiers.None);
            // Multiple sub-threshold moves accumulating only to ~5px from the
            // anchor — still below the 10px Threshold.
            dispatcher.DispatchPointerMove(102, 101, KeyModifiers.None);
            dispatcher.DispatchPointerMove(104, 103, KeyModifiers.None);
            dispatcher.DispatchPointerMove(103, 104, KeyModifiers.None);

            Assert.That(starts, Is.Zero, "PanStart must not fire while cumulative travel < Threshold");
            Assert.That(moves, Is.Zero, "PanMove must not fire before the gesture has started");
        }

        [Test]
        public void PointerMove_beyond_threshold_fires_PanStart_then_PanMove_with_full_down_to_current_delta() {
            var dispatcher = Build(out var target);
            var pan = new PanManipulator(target, dispatcher) { Threshold = 10.0 };
            pan.Wire();
            double startX = 0, startY = 0;
            int starts = 0;
            double firstDx = 0, firstDy = 0;
            int moves = 0;
            pan.PanStart += (x, y) => { starts++; startX = x; startY = y; };
            pan.PanMove += (dx, dy) => {
                if (moves == 0) { firstDx = dx; firstDy = dy; }
                moves++;
            };

            dispatcher.DispatchPointerDown(50, 60, button: 0, KeyModifiers.None);
            // Single move well beyond threshold (distance = 25).
            dispatcher.DispatchPointerMove(70, 75, KeyModifiers.None);

            Assert.That(starts, Is.EqualTo(1), "PanStart must fire exactly once when threshold is crossed");
            Assert.That(startX, Is.EqualTo(50.0), "PanStart x is the pointer-down anchor X");
            Assert.That(startY, Is.EqualTo(60.0), "PanStart y is the pointer-down anchor Y");
            Assert.That(moves, Is.EqualTo(1), "First over-threshold move emits exactly one PanMove");
            Assert.That(firstDx, Is.EqualTo(20.0), "First PanMove dx is full down→current X delta (70-50)");
            Assert.That(firstDy, Is.EqualTo(15.0), "First PanMove dy is full down→current Y delta (75-60)");
        }

        [Test]
        public void Middle_mouse_PointerDown_does_not_arm_a_left_only_pan() {
            var dispatcher = Build(out var target);
            // Default Button == 0 (left). Send middle-mouse (button 1).
            var pan = new PanManipulator(target, dispatcher) { Threshold = 0.0 };
            pan.Wire();
            int starts = 0;
            int moves = 0;
            int ends = 0;
            pan.PanStart += (_, _) => starts++;
            pan.PanMove += (_, _) => moves++;
            pan.PanEnd += (_, _) => ends++;

            dispatcher.DispatchPointerDown(40, 40, button: 1, KeyModifiers.None);
            dispatcher.DispatchPointerMove(120, 200, KeyModifiers.None);
            dispatcher.DispatchPointerUp(120, 200, button: 1, KeyModifiers.None);

            Assert.That(starts, Is.Zero, "Middle-mouse must not arm a left-only pan");
            Assert.That(moves, Is.Zero, "No PanMove fires while the gesture is inactive");
            Assert.That(ends, Is.Zero, "No PanEnd fires when the gesture never started");
        }

        [Test]
        public void PanEnd_carries_cumulative_delta_and_subsequent_PanMoves_are_step_deltas() {
            var dispatcher = Build(out var target);
            var pan = new PanManipulator(target, dispatcher) { Threshold = 5.0 };
            pan.Wire();
            var moveDeltas = new System.Collections.Generic.List<(double dx, double dy)>();
            double endDx = 0, endDy = 0;
            int ends = 0;
            pan.PanMove += (dx, dy) => moveDeltas.Add((dx, dy));
            pan.PanEnd += (dx, dy) => { ends++; endDx = dx; endDy = dy; };

            dispatcher.DispatchPointerDown(0, 0, button: 0, KeyModifiers.None);
            // Crosses threshold immediately (distance 10 > 5). First PanMove
            // must carry the full down→current delta (10, 0).
            dispatcher.DispatchPointerMove(10, 0, KeyModifiers.None);
            // Step move: dx must be the per-event delta (5, 3), not (15, 3).
            dispatcher.DispatchPointerMove(15, 3, KeyModifiers.None);
            dispatcher.DispatchPointerUp(15, 3, button: 0, KeyModifiers.None);

            Assert.That(moveDeltas.Count, Is.EqualTo(2));
            Assert.That(moveDeltas[0].dx, Is.EqualTo(10.0), "First PanMove dx = full down→current X (start cross)");
            Assert.That(moveDeltas[0].dy, Is.EqualTo(0.0));
            Assert.That(moveDeltas[1].dx, Is.EqualTo(5.0), "Step PanMove dx = current-last (15-10)");
            Assert.That(moveDeltas[1].dy, Is.EqualTo(3.0), "Step PanMove dy = current-last (3-0)");
            Assert.That(ends, Is.EqualTo(1));
            Assert.That(endDx, Is.EqualTo(15.0), "PanEnd dx = pointer-up X minus down X");
            Assert.That(endDy, Is.EqualTo(3.0), "PanEnd dy = pointer-up Y minus down Y");
        }
    }
}
