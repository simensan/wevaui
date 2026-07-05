using NUnit.Framework;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Events {
    public class UIEventTests {
        [Test]
        public void PreventDefault_sets_flag() {
            var e = new PointerEvent();
            Assert.That(e.DefaultPrevented, Is.False);
            e.PreventDefault();
            Assert.That(e.DefaultPrevented, Is.True);
        }

        [Test]
        public void StopPropagation_sets_flag() {
            var e = new PointerEvent();
            Assert.That(e.PropagationStopped, Is.False);
            Assert.That(e.ImmediatePropagationStopped, Is.False);
            e.StopPropagation();
            Assert.That(e.PropagationStopped, Is.True);
            Assert.That(e.ImmediatePropagationStopped, Is.False);
        }

        [Test]
        public void StopImmediatePropagation_sets_both_flags() {
            var e = new PointerEvent();
            e.StopImmediatePropagation();
            Assert.That(e.PropagationStopped, Is.True);
            Assert.That(e.ImmediatePropagationStopped, Is.True);
        }

        [Test]
        public void Default_kind_can_be_set() {
            var e = new PointerEvent { Kind = EventKind.PointerDown };
            Assert.That(e.Kind, Is.EqualTo(EventKind.PointerDown));
        }

        [Test]
        public void Target_and_currentTarget_independent() {
            var a = new Element("div");
            var b = new Element("span");
            var e = new PointerEvent { Target = a, CurrentTarget = b };
            Assert.That(e.Target, Is.SameAs(a));
            Assert.That(e.CurrentTarget, Is.SameAs(b));
        }

        [Test]
        public void Phase_can_be_set() {
            var e = new PointerEvent { Phase = EventPhase.Capture };
            Assert.That(e.Phase, Is.EqualTo(EventPhase.Capture));
            e.Phase = EventPhase.AtTarget;
            Assert.That(e.Phase, Is.EqualTo(EventPhase.AtTarget));
            e.Phase = EventPhase.Bubble;
            Assert.That(e.Phase, Is.EqualTo(EventPhase.Bubble));
        }

        [Test]
        public void Pointer_event_carries_coordinates_and_modifiers() {
            var e = new PointerEvent { X = 12.5, Y = 7.25, Button = 2, Buttons = 5,
                ShiftKey = true, CtrlKey = true, AltKey = false, MetaKey = false };
            Assert.That(e.X, Is.EqualTo(12.5));
            Assert.That(e.Y, Is.EqualTo(7.25));
            Assert.That(e.Button, Is.EqualTo(2));
            Assert.That(e.Buttons, Is.EqualTo(5));
            Assert.That(e.ShiftKey, Is.True);
            Assert.That(e.CtrlKey, Is.True);
            Assert.That(e.AltKey, Is.False);
            Assert.That(e.MetaKey, Is.False);
        }

        [Test]
        public void Keyboard_event_carries_key_and_repeat() {
            var k = new KeyboardEvent { Key = "Enter", Code = "Enter", Repeat = true };
            Assert.That(k.Key, Is.EqualTo("Enter"));
            Assert.That(k.Code, Is.EqualTo("Enter"));
            Assert.That(k.Repeat, Is.True);
        }

        [Test]
        public void Focus_event_does_not_bubble_by_default() {
            var f = new FocusEvent();
            Assert.That(f.Bubbles, Is.False);
        }

        [Test]
        public void Listener_exception_is_isolated_via_try_catch() {
            // EventDispatcher.InvokeListeners wraps each listener.Invoke in a
            // try/catch so a throwing handler doesn't tear down the whole
            // dispatch chain. The exception is logged via UICssDiagnostics
            // and sibling listeners still fire.
            var doc = new Document();
            var root = new Element("div");
            doc.AppendChild(root);
            var hits = new FakeHitTester();
            hits.Add(root, 0, 0, 100, 100);
            var dispatcher = new EventDispatcher(doc, hits);
            bool secondRan = false;
            dispatcher.AddEventListener(root, EventKind.Click,
                _ => throw new System.InvalidOperationException("boom"));
            dispatcher.AddEventListener(root, EventKind.Click,
                _ => secondRan = true);
            // The diagnostic Warn() call routes through Debug.LogWarning in
            // editor builds; reset the dedupe set first so this run actually
            // emits, then tell the test runner to expect the warning so the
            // unexpected-warning policy doesn't fail the test.
            Weva.Diagnostics.UICssDiagnostics.ResetForTests();
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex(@"event-listener.*boom"));
            Assert.DoesNotThrow(() => {
                dispatcher.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
                dispatcher.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            });
            Assert.That(secondRan, Is.True, "second listener should still run after first throws");
        }
    }
}
