using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class FormSubmissionEventsTests {
        sealed class HitFor : IHitTester {
            readonly Element only;
            public HitFor(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        [Test]
        public void Input_event_fires_on_every_keystroke() {
            var doc = HtmlParser.Parse("<input id=\"i\" value=\"\">");
            var i = doc.GetElementById("i");
            var d = new EventDispatcher(doc, new HitFor(i), new FakeUIClock());
            d.Focus(i);
            var ctrl = new InputController(i, d);
            ctrl.Wire();
            int inputs = 0;
            d.AddEventListener(i, EventKind.Input, _ => inputs++);
            d.DispatchTextInput("a");
            d.DispatchTextInput("b");
            d.DispatchTextInput("c");
            Assert.That(inputs, Is.EqualTo(3));
        }

        [Test]
        public void Change_event_fires_on_blur_after_value_change() {
            var doc = HtmlParser.Parse("<input id=\"i\" value=\"\"><input id=\"j\" value=\"\">");
            var i = doc.GetElementById("i");
            var j = doc.GetElementById("j");
            var d = new EventDispatcher(doc, new HitFor(i), new FakeUIClock());
            d.Focus(i);
            var ctrl = new InputController(i, d);
            ctrl.Wire();
            int changes = 0;
            d.AddEventListener(i, EventKind.Change, _ => changes++);
            d.DispatchTextInput("h");
            d.DispatchTextInput("i");
            // Blur by focusing another control.
            d.Focus(j);
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void Change_does_not_fire_when_blurring_without_change() {
            var doc = HtmlParser.Parse("<input id=\"i\" value=\"abc\"><input id=\"j\" value=\"\">");
            var i = doc.GetElementById("i");
            var j = doc.GetElementById("j");
            var d = new EventDispatcher(doc, new HitFor(i), new FakeUIClock());
            d.Focus(i);
            var ctrl = new InputController(i, d);
            ctrl.Wire();
            int changes = 0;
            d.AddEventListener(i, EventKind.Change, _ => changes++);
            // Don't type anything, just blur.
            d.Focus(j);
            Assert.That(changes, Is.EqualTo(0));
        }

        [Test]
        public void Submit_synthesized_on_Enter_inside_form() {
            var doc = HtmlParser.Parse("<form><input id=\"i\" value=\"\"></form>");
            var form = doc.GetElementsByTagName("form").First();
            var i = doc.GetElementById("i");
            var d = new EventDispatcher(doc, new HitFor(i), new FakeUIClock());
            d.Focus(i);
            var ctrl = new InputController(i, d);
            ctrl.Wire();
            int submits = 0;
            d.AddEventListener(form, EventKind.Submit, _ => submits++);
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, false);
            Assert.That(submits, Is.EqualTo(1));
        }

        [Test]
        public void Submit_does_not_fire_outside_a_form() {
            var doc = HtmlParser.Parse("<input id=\"i\" value=\"\">");
            var i = doc.GetElementById("i");
            var d = new EventDispatcher(doc, new HitFor(i), new FakeUIClock());
            d.Focus(i);
            var ctrl = new InputController(i, d);
            ctrl.Wire();
            // No form wrapper. Add a submit listener on the document root.
            var root = doc.Children.OfType<Element>().FirstOrDefault();
            int submits = 0;
            if (root != null) {
                d.AddEventListener(root, EventKind.Submit, _ => submits++);
            }
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, false);
            Assert.That(submits, Is.EqualTo(0));
        }

        [Test]
        public void DispatchSynthetic_runs_through_capture_then_bubble() {
            var doc = HtmlParser.Parse("<div id=\"a\"><div id=\"b\"></div></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new HitFor(b), new FakeUIClock());
            string seq = "";
            d.AddEventListener(a, EventKind.Change, _ => seq += "a;");
            d.AddEventListener(b, EventKind.Change, _ => seq += "b;");
            FormSubmissionEvents.DispatchChange(d, b);
            Assert.That(seq, Is.EqualTo("b;a;"));
        }

        [Test]
        public void FindEnclosingForm_returns_nearest_form() {
            var doc = HtmlParser.Parse("<form><div><input id=\"i\"></div></form>");
            var i = doc.GetElementById("i");
            var form = FormSubmissionEvents.FindEnclosingForm(i);
            Assert.That(form, Is.Not.Null);
            Assert.That(form.TagName, Is.EqualTo("form"));
        }
    }
}
