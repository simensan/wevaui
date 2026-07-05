using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class RadioGroupTests {
        sealed class HitFor : IHitTester {
            readonly Element only;
            public HitFor(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        [Test]
        public void Members_collects_radios_with_matching_name() {
            var doc = HtmlParser.Parse("<form><input type=\"radio\" name=\"g\" id=\"a\"><input type=\"radio\" name=\"g\" id=\"b\"><input type=\"radio\" name=\"h\" id=\"c\"></form>");
            var a = doc.GetElementById("a");
            var group = RadioGroup.For(a);
            var ids = group.Members().Select(m => m.GetAttribute("id")).ToList();
            Assert.That(ids, Is.EquivalentTo(new[] { "a", "b" }));
        }

        [Test]
        public void Select_unchecks_other_members_and_checks_target() {
            var doc = HtmlParser.Parse("<form><input type=\"radio\" name=\"g\" id=\"a\" checked><input type=\"radio\" name=\"g\" id=\"b\"></form>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            RadioGroup.For(a).Select(b);
            Assert.That(a.HasAttribute("checked"), Is.False);
            Assert.That(b.HasAttribute("checked"), Is.True);
        }

        [Test]
        public void Wire_clicking_radio_switches_selection() {
            var doc = HtmlParser.Parse("<form><input type=\"radio\" name=\"g\" id=\"a\" checked><input type=\"radio\" name=\"g\" id=\"b\"></form>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new HitFor(b), new FakeUIClock());
            RadioGroup.Wire(b, d);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(b.HasAttribute("checked"), Is.True);
            Assert.That(a.HasAttribute("checked"), Is.False);
        }

        [Test]
        public void ScopeOf_uses_form_when_present() {
            var doc = HtmlParser.Parse("<form><input type=\"radio\" name=\"g\" id=\"r\"></form>");
            var r = doc.GetElementById("r");
            var scope = RadioGroup.ScopeOf(r);
            Assert.That(scope, Is.InstanceOf<Element>());
            Assert.That(((Element)scope).TagName, Is.EqualTo("form"));
        }

        [Test]
        public void ScopeOf_falls_back_to_document_outside_form() {
            var doc = HtmlParser.Parse("<div><input type=\"radio\" name=\"g\" id=\"r\"></div>");
            var r = doc.GetElementById("r");
            var scope = RadioGroup.ScopeOf(r);
            Assert.That(scope, Is.SameAs(doc));
        }

        [Test]
        public void Selected_returns_currently_checked_member() {
            var doc = HtmlParser.Parse("<form><input type=\"radio\" name=\"g\" id=\"a\"><input type=\"radio\" name=\"g\" id=\"b\" checked></form>");
            var b = doc.GetElementById("b");
            var sel = RadioGroup.For(b).Selected;
            Assert.That(sel, Is.SameAs(b));
        }
    }
}
