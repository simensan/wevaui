using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class SelectStateTests {
        sealed class HitFor : IHitTester {
            readonly Element only;
            public HitFor(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        [Test]
        public void Open_round_trips_and_fires_event() {
            var doc = HtmlParser.Parse("<select><option>a</option><option>b</option></select>");
            var sel = System.Linq.Enumerable.First(doc.GetElementsByTagName("select"));
            var s = new SelectState(sel);
            int n = 0;
            s.OpenChanged += () => n++;
            s.Open = true;
            Assert.That(s.Open, Is.True);
            Assert.That(n, Is.EqualTo(1));
            s.Open = false;
            Assert.That(n, Is.EqualTo(2));
        }

        [Test]
        public void SelectIndex_marks_corresponding_option() {
            var doc = HtmlParser.Parse("<select><option value=\"a\">A</option><option value=\"b\">B</option></select>");
            var sel = System.Linq.Enumerable.First(doc.GetElementsByTagName("select"));
            var s = new SelectState(sel);
            s.SelectIndex(1);
            Assert.That(s.Value, Is.EqualTo("b"));
            Assert.That(s.SelectedIndex, Is.EqualTo(1));
        }

        [Test]
        public void Wire_click_toggles_open() {
            var doc = HtmlParser.Parse("<select><option>x</option></select>");
            var sel = System.Linq.Enumerable.First(doc.GetElementsByTagName("select"));
            var d = new EventDispatcher(doc, new HitFor(sel), new FakeUIClock());
            var s = new SelectState(sel);
            s.Wire(d);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(s.Open, Is.True);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(s.Open, Is.False);
        }

        [Test]
        public void SelectIndex_fires_change_event_via_dispatcher() {
            var doc = HtmlParser.Parse("<select><option value=\"a\">A</option><option value=\"b\">B</option></select>");
            var sel = System.Linq.Enumerable.First(doc.GetElementsByTagName("select"));
            var d = new EventDispatcher(doc, new HitFor(sel), new FakeUIClock());
            var s = new SelectState(sel);
            s.Wire(d);
            int changes = 0;
            d.AddEventListener(sel, EventKind.Change, _ => changes++);
            s.SelectIndex(1);
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void Disabled_select_ignores_select_index() {
            var doc = HtmlParser.Parse("<select disabled><option value=\"a\">A</option><option value=\"b\">B</option></select>");
            var sel = System.Linq.Enumerable.First(doc.GetElementsByTagName("select"));
            var s = new SelectState(sel);
            s.SelectIndex(1);
            Assert.That(s.SelectedIndex, Is.EqualTo(0)); // first option default
        }
    }
}
