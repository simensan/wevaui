using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Events.Manipulators;
using Weva.Parsing;

namespace Weva.Tests.Events.Manipulators {
    // TG33 — direct coverage for ContextualMenuManipulator. Pins:
    //   * right-click (PointerDown with Button == 2) fires MenuRequested at
    //     the click coordinates
    //   * long-press path: pressing left-mouse, holding past LongPressSeconds
    //     while pointer stays within LongPressMoveTolerance, fires
    //     MenuRequested at the last-known pointer position
    //   * Tick() also fires the long-press once the clock advances even
    //     without intervening PointerMove events (touch-hold-without-jitter)
    //   * pointer wander past LongPressMoveTolerance cancels the long-press
    //   * Shift+F10 keyboard shortcut on a focused target fires MenuRequested
    //   * F10 without Shift, and Shift+F10 with no focused target on the
    //     manipulator's element, are ignored (negative case for the keyboard
    //     path)
    public class ContextualMenuManipulatorTests {
        static EventDispatcher Build(out Element target, string html = "<button id=\"t\">x</button>") {
            var doc = HtmlParser.Parse(html);
            target = doc.GetElementById("t");
            var ht = new FakeHitTester();
            ht.Add(target, 0, 0, 1000, 1000);
            return new EventDispatcher(doc, ht, new FakeUIClock());
        }

        [Test]
        public void RightClick_PointerDown_fires_MenuRequested_at_click_position() {
            var dispatcher = Build(out var target);
            var clock = new FakeUIClock();
            var menu = new ContextualMenuManipulator(target, dispatcher, clock);
            menu.Wire();
            int fired = 0;
            double rx = -1, ry = -1;
            menu.MenuRequested += (x, y) => { fired++; rx = x; ry = y; };

            // Button 2 is right-click per UnityPointerSource mapping
            // (left=0, middle=1, right=2).
            dispatcher.DispatchPointerDown(123, 456, button: 2, KeyModifiers.None);

            Assert.That(fired, Is.EqualTo(1), "Right-click must fire exactly one MenuRequested");
            Assert.That(rx, Is.EqualTo(123.0), "x is the right-click X");
            Assert.That(ry, Is.EqualTo(456.0), "y is the right-click Y");
        }

        [Test]
        public void LongPress_via_PointerMove_after_threshold_elapses_fires_MenuRequested() {
            var dispatcher = Build(out var target);
            var clock = new FakeUIClock();
            var menu = new ContextualMenuManipulator(target, dispatcher, clock) {
                LongPressSeconds = 0.5,
                LongPressMoveTolerance = 5.0
            };
            menu.Wire();
            int fired = 0;
            double rx = -1, ry = -1;
            menu.MenuRequested += (x, y) => { fired++; rx = x; ry = y; };

            // Left-button press starts the long-press timer.
            dispatcher.DispatchPointerDown(50, 60, button: 0, KeyModifiers.None);
            // Tiny jitter still within tolerance — should NOT cancel.
            clock.Advance(0.1);
            dispatcher.DispatchPointerMove(51, 60, KeyModifiers.None);
            Assert.That(fired, Is.Zero, "Long-press must not fire before the duration elapses");

            // Cross the duration with another small move.
            clock.Advance(0.5);
            dispatcher.DispatchPointerMove(52, 61, KeyModifiers.None);

            Assert.That(fired, Is.EqualTo(1), "Long-press fires exactly once after duration elapses");
            Assert.That(rx, Is.EqualTo(52.0), "x is the last-known pointer X at fire time");
            Assert.That(ry, Is.EqualTo(61.0), "y is the last-known pointer Y at fire time");
        }

        [Test]
        public void LongPress_via_Tick_fires_without_intervening_PointerMove() {
            var dispatcher = Build(out var target);
            var clock = new FakeUIClock();
            var menu = new ContextualMenuManipulator(target, dispatcher, clock) {
                LongPressSeconds = 0.4
            };
            menu.Wire();
            int fired = 0;
            double rx = -1, ry = -1;
            menu.MenuRequested += (x, y) => { fired++; rx = x; ry = y; };

            dispatcher.DispatchPointerDown(200, 300, button: 0, KeyModifiers.None);
            // Stationary touch: no PointerMove events arrive, but Lifecycle
            // calls Tick — long-press still fires after the duration.
            menu.Tick();
            Assert.That(fired, Is.Zero, "Tick before duration must not fire");
            clock.Advance(0.5);
            menu.Tick();

            Assert.That(fired, Is.EqualTo(1));
            Assert.That(rx, Is.EqualTo(200.0));
            Assert.That(ry, Is.EqualTo(300.0));

            // Idempotent: a second Tick must not re-fire while still pressed.
            menu.Tick();
            Assert.That(fired, Is.EqualTo(1), "Tick is single-shot per press");
        }

        [Test]
        public void Pointer_wander_past_tolerance_cancels_LongPress() {
            var dispatcher = Build(out var target);
            var clock = new FakeUIClock();
            var menu = new ContextualMenuManipulator(target, dispatcher, clock) {
                LongPressSeconds = 0.5,
                LongPressMoveTolerance = 5.0
            };
            menu.Wire();
            int fired = 0;
            menu.MenuRequested += (_, _) => fired++;

            dispatcher.DispatchPointerDown(0, 0, button: 0, KeyModifiers.None);
            // Wander 20px away — well past the 5px tolerance — cancels.
            dispatcher.DispatchPointerMove(20, 0, KeyModifiers.None);
            clock.Advance(1.0);
            menu.Tick();
            dispatcher.DispatchPointerMove(20, 0, KeyModifiers.None);

            Assert.That(fired, Is.Zero, "Wander past tolerance must cancel the long-press for the rest of the press");
        }

        [Test]
        public void ShiftF10_on_focused_target_fires_MenuRequested() {
            var dispatcher = Build(out var target);
            var clock = new FakeUIClock();
            var menu = new ContextualMenuManipulator(target, dispatcher, clock);
            menu.Wire();
            int fired = 0;
            double rx = 99, ry = 99;
            menu.MenuRequested += (x, y) => { fired++; rx = x; ry = y; };

            // KeyDown dispatches to `focused ?? RootElement` and bubbles up;
            // focusing the target ensures the manipulator's KeyDown listener
            // is on the dispatch path.
            dispatcher.Focus(target);
            dispatcher.DispatchKeyDown("F10", "F10", KeyModifiers.Shift, repeat: false);

            Assert.That(fired, Is.EqualTo(1), "Shift+F10 on the focused target fires MenuRequested");
            // The manipulator passes (0, 0) — callers are expected to compute
            // the menu position relative to their own target.
            Assert.That(rx, Is.EqualTo(0.0));
            Assert.That(ry, Is.EqualTo(0.0));
        }

        [Test]
        public void Plain_F10_or_unfocused_target_does_not_fire_MenuRequested() {
            var dispatcher = Build(out var target);
            var clock = new FakeUIClock();
            var menu = new ContextualMenuManipulator(target, dispatcher, clock);
            menu.Wire();
            int fired = 0;
            menu.MenuRequested += (_, _) => fired++;

            // (a) Focused, but no Shift modifier: ignored.
            dispatcher.Focus(target);
            dispatcher.DispatchKeyDown("F10", "F10", KeyModifiers.None, repeat: false);
            Assert.That(fired, Is.Zero, "F10 without Shift must not fire");

            // (b) Shift+F10 with focus elsewhere — listener never sees the
            // event because KeyDown dispatches to the focused element and the
            // target isn't on that path.
            var doc = HtmlParser.Parse(
                "<section><button id=\"t\">x</button><button id=\"other\">y</button></section>");
            target = doc.GetElementById("t");
            var other = doc.GetElementById("other");
            var ht = new FakeHitTester();
            ht.Add(target, 0, 0, 100, 100);
            ht.Add(other, 100, 0, 100, 100);
            dispatcher = new EventDispatcher(doc, ht, new FakeUIClock());
            menu = new ContextualMenuManipulator(target, dispatcher, clock);
            menu.Wire();
            int fired2 = 0;
            menu.MenuRequested += (_, _) => fired2++;
            dispatcher.Focus(other);
            dispatcher.DispatchKeyDown("F10", "F10", KeyModifiers.Shift, repeat: false);
            Assert.That(fired2, Is.Zero,
                "Shift+F10 with focus on a sibling must not fire on this target");
        }
    }
}
