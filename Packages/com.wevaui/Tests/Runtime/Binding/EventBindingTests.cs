using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Binding {
    public class EventBindingTests {
        public class TestController {
            public int NoArgCalls;
            public int UIEventCalls;
            public int PointerCalls;
            public PointerEvent LastPointer;
            public UIEvent LastUIEvent;

            public void OnClickNoArgs() => NoArgCalls++;
            public void OnClickUIEvent(UIEvent e) {
                UIEventCalls++;
                LastUIEvent = e;
            }
            public void OnClickPointer(PointerEvent p) {
                PointerCalls++;
                LastPointer = p;
            }
            void Private_OnClick() => NoArgCalls++;
            public MethodInfo GetPrivateMethod() => GetType().GetMethod("Private_OnClick", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static (Document doc, Element el, EventDispatcher d) Build(string id = "btn") {
            var doc = new Document();
            var el = new Element("button");
            el.SetAttribute("id", id);
            doc.AppendChild(el);
            var ht = new BindingFakeHitTester();
            ht.Add(el, 0, 0, 100, 100);
            var d = new EventDispatcher(doc, ht, new FakeUIClock());
            return (doc, el, d);
        }

        static MethodInfo M(string name) =>
            typeof(TestController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void Wire_subscribes_to_click_on_element() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, M("OnClickNoArgs"), c);
            b.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
        }

        [Test]
        public void No_arg_method_invoked_correctly() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, M("OnClickNoArgs"), c);
            b.Wire(d);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
            Assert.That(c.UIEventCalls, Is.EqualTo(0));
        }

        [Test]
        public void UIEvent_method_invoked_with_event_object() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, M("OnClickUIEvent"), c);
            b.Wire(d);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.UIEventCalls, Is.EqualTo(1));
            Assert.That(c.LastUIEvent, Is.Not.Null);
            Assert.That(c.LastUIEvent.Kind, Is.EqualTo(EventKind.Click));
        }

        [Test]
        public void Typed_PointerEvent_method_invoked_with_typed_event() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, M("OnClickPointer"), c);
            b.Wire(d);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.PointerCalls, Is.EqualTo(1));
            Assert.That(c.LastPointer, Is.Not.Null);
        }

        [Test]
        public void Multiple_bindings_on_same_element_both_fire() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b1 = new EventBinding(el, EventKind.Click, M("OnClickNoArgs"), c);
            var b2 = new EventBinding(el, EventKind.Click, M("OnClickUIEvent"), c);
            b1.Wire(d);
            b2.Wire(d);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
            Assert.That(c.UIEventCalls, Is.EqualTo(1));
        }

        [Test]
        public void Unwire_unsubscribes() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, M("OnClickNoArgs"), c);
            b.Wire(d);
            b.Unwire();
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(0));
        }

        [Test]
        public void Wire_is_idempotent() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, M("OnClickNoArgs"), c);
            b.Wire(d);
            b.Wire(d);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
        }

        [Test]
        public void Private_method_can_be_invoked() {
            var (_, el, d) = Build();
            var c = new TestController();
            var b = new EventBinding(el, EventKind.Click, c.GetPrivateMethod(), c);
            b.Wire(d);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
        }

        [Test]
        public void Unknown_method_at_scan_time_warns_without_binding() {
            var doc = new Document();
            var el = new Element("button");
            el.SetAttribute("on-click", "DoesNotExist");
            doc.AppendChild(el);
            var c = new TestController();
            var set = BindingScanner.Scan(doc, c);
            Assert.That(set.EventBindings.Count, Is.EqualTo(0));
            Assert.That(set.Warnings.Count, Is.EqualTo(1));
            Assert.That(set.Warnings[0], Does.Contain("DoesNotExist"));
        }
    }
}
