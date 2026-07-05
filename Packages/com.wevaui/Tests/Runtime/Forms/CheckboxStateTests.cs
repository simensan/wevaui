using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class CheckboxStateTests {
        sealed class FixedHit : IHitTester {
            readonly Element only;
            public FixedHit(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        [Test]
        public void Toggle_flips_attribute_and_bumps_version() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            var cb = new CheckboxState(e);
            long v0 = cb.Version;
            cb.Toggle();
            Assert.That(cb.Checked, Is.True);
            Assert.That(e.HasAttribute("checked"), Is.True);
            Assert.That(cb.Version, Is.GreaterThan(v0));
            cb.Toggle();
            Assert.That(cb.Checked, Is.False);
            Assert.That(e.HasAttribute("checked"), Is.False);
        }

        [Test]
        public void Toggle_fires_Toggled_event() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            var cb = new CheckboxState(e);
            int n = 0;
            cb.Toggled += () => n++;
            cb.Toggle();
            cb.Toggle();
            Assert.That(n, Is.EqualTo(2));
        }

        [Test]
        public void Disabled_checkbox_ignores_toggle() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            e.SetAttribute("disabled", "");
            var cb = new CheckboxState(e);
            cb.Toggle();
            Assert.That(cb.Checked, Is.False);
        }

        [Test]
        public void Constructor_rejects_non_checkbox() {
            var e = new Element("input");
            e.SetAttribute("type", "radio");
            Assert.Throws<System.ArgumentException>(() => new CheckboxState(e));
        }

        [Test]
        public void Wire_fires_change_event_on_click_toggle() {
            var doc = HtmlParser.Parse("<input type=\"checkbox\" id=\"c\">");
            var e = doc.GetElementById("c");
            var d = new EventDispatcher(doc, new FixedHit(e), new FakeUIClock());
            d.Focus(e);
            var cb = new CheckboxState(e);
            cb.Wire(d);
            int changes = 0;
            d.AddEventListener(e, EventKind.Change, _ => changes++);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(cb.Checked, Is.True);
        }

        [Test]
        public void Indeterminate_clears_when_setting_checked() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            var cb = new CheckboxState(e);
            cb.Indeterminate = true;
            cb.Checked = true;
            Assert.That(cb.Indeterminate, Is.False);
        }
    }
}
