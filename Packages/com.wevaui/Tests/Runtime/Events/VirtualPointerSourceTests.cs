using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using static Weva.Tests.Events.EventTestHelpers;

namespace Weva.Tests.Events {
    // VirtualPointerSource lets gamepad / D-pad / scripted code drive the
    // same dispatch path a hardware pointer would. The dispatcher produces
    // PointerMove/Down/Up events plus click synthesis; these tests verify
    // the wrapper forwards positions, modifiers, and clamping correctly.
    public class VirtualPointerSourceTests {
        static (EventDispatcher disp, FakeHitTester hit, Document doc) Build(string html) {
            var doc = Html(html);
            var hit = new FakeHitTester();
            var disp = new EventDispatcher(doc, hit);
            return (disp, hit, doc);
        }

        [Test]
        public void MoveTo_dispatches_pointer_move_at_target_position() {
            var (disp, hit, doc) = Build("<div id=\"a\"></div>");
            var a = ById(doc, "a");
            hit.Add(a, 0, 0, 100, 100);

            var src = new VirtualPointerSource(disp);
            src.MoveTo(50, 50);
            Assert.That(disp.HoveredElement, Is.SameAs(a));
            Assert.That(src.X, Is.EqualTo(50));
            Assert.That(src.Y, Is.EqualTo(50));
        }

        [Test]
        public void Move_is_delta_relative_and_updates_state() {
            var (disp, hit, doc) = Build("<div id=\"a\"></div>");
            var a = ById(doc, "a");
            hit.Add(a, 0, 0, 100, 100);

            var src = new VirtualPointerSource(disp);
            src.MoveTo(20, 20);
            src.Move(10, 5);
            Assert.That(src.X, Is.EqualTo(30));
            Assert.That(src.Y, Is.EqualTo(25));
        }

        [Test]
        public void Viewport_clamps_position_to_bounds() {
            var (disp, _, _) = Build("<div></div>");
            var src = new VirtualPointerSource(disp);
            src.SetViewport(0, 0, 100, 100);
            src.MoveTo(150, -50);
            Assert.That(src.X, Is.EqualTo(100));
            Assert.That(src.Y, Is.EqualTo(0));
        }

        [Test]
        public void ClearViewport_disables_clamping() {
            var (disp, _, _) = Build("<div></div>");
            var src = new VirtualPointerSource(disp);
            src.SetViewport(0, 0, 100, 100);
            src.ClearViewport();
            src.MoveTo(500, -100);
            Assert.That(src.X, Is.EqualTo(500));
            Assert.That(src.Y, Is.EqualTo(-100));
        }

        [Test]
        public void Click_synthesises_click_event_when_down_target_equals_up_target() {
            var (disp, hit, doc) = Build("<div id=\"a\"></div>");
            var a = ById(doc, "a");
            hit.Add(a, 0, 0, 100, 100);

            int clicks = 0;
            disp.AddEventListener(a, EventKind.Click, _ => clicks++);

            var src = new VirtualPointerSource(disp);
            src.MoveTo(50, 50);
            src.Click();
            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void ButtonDown_then_drift_then_ButtonUp_does_not_synthesise_click_off_target() {
            var (disp, hit, doc) = Build("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            hit.Add(a, 0, 0, 50, 50);
            hit.Add(b, 60, 0, 50, 50);

            int clicksOnA = 0, clicksOnB = 0;
            disp.AddEventListener(a, EventKind.Click, _ => clicksOnA++);
            disp.AddEventListener(b, EventKind.Click, _ => clicksOnB++);

            var src = new VirtualPointerSource(disp);
            src.MoveTo(25, 25);
            src.ButtonDown(0);
            src.MoveTo(80, 25); // drift to b before releasing
            src.ButtonUp(0);
            Assert.That(clicksOnA, Is.EqualTo(0));
            Assert.That(clicksOnB, Is.EqualTo(0));
        }

        [Test]
        public void IsButtonDown_tracks_held_state() {
            var (disp, _, _) = Build("<div></div>");
            var src = new VirtualPointerSource(disp);
            Assert.That(src.IsButtonDown(0), Is.False);
            src.ButtonDown(0);
            Assert.That(src.IsButtonDown(0), Is.True);
            src.ButtonUp(0);
            Assert.That(src.IsButtonDown(0), Is.False);
        }

        [Test]
        public void Modifiers_propagate_to_dispatched_events() {
            var (disp, hit, doc) = Build("<div id=\"a\"></div>");
            var a = ById(doc, "a");
            hit.Add(a, 0, 0, 100, 100);

            PointerEvent received = null;
            disp.AddEventListener(a, EventKind.PointerDown, e => received = e as PointerEvent);

            var src = new VirtualPointerSource(disp);
            src.Modifiers = KeyModifiers.Shift | KeyModifiers.Ctrl;
            src.MoveTo(10, 10);
            src.ButtonDown(0);

            Assert.That(received, Is.Not.Null);
            Assert.That(received.ShiftKey, Is.True);
            Assert.That(received.CtrlKey, Is.True);
            Assert.That(received.AltKey, Is.False);
        }

        [Test]
        public void AdvanceFromStick_scales_by_speed_and_dt() {
            var (disp, _, _) = Build("<div></div>");
            var src = new VirtualPointerSource(disp);
            src.MoveTo(0, 0);

            // 1.0 stick * 600 px/s * 0.016 s ≈ 9.6 px
            bool moved = src.AdvanceFromStick(1.0, 0, pixelsPerSecond: 600, deltaSeconds: 0.016);
            Assert.That(moved, Is.True);
            Assert.That(src.X, Is.EqualTo(9.6).Within(1e-9));
            Assert.That(src.Y, Is.EqualTo(0));
        }

        [Test]
        public void AdvanceFromStick_returns_false_for_dead_zone() {
            var (disp, _, _) = Build("<div></div>");
            var src = new VirtualPointerSource(disp);
            src.MoveTo(50, 50);
            bool moved = src.AdvanceFromStick(0, 0, pixelsPerSecond: 600, deltaSeconds: 0.016);
            Assert.That(moved, Is.False);
            Assert.That(src.X, Is.EqualTo(50));
            Assert.That(src.Y, Is.EqualTo(50));
        }

        [Test]
        public void Null_dispatcher_throws() {
            Assert.Throws<System.ArgumentNullException>(() => new VirtualPointerSource(null));
        }

        [Test]
        public void Multiple_buttons_track_independently() {
            var (disp, _, _) = Build("<div></div>");
            var src = new VirtualPointerSource(disp);
            src.ButtonDown(0);
            src.ButtonDown(1);
            Assert.That(src.IsButtonDown(0), Is.True);
            Assert.That(src.IsButtonDown(1), Is.True);
            src.ButtonUp(0);
            Assert.That(src.IsButtonDown(0), Is.False);
            Assert.That(src.IsButtonDown(1), Is.True);
        }
    }
}
