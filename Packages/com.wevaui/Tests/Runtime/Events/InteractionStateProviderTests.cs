using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    public class InteractionStateProviderTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        EventDispatcher Dispatcher(Document doc, FakeHitTester ht) =>
            new EventDispatcher(doc, ht, new FakeUIClock());

        [Test]
        public void Initial_state_for_unknown_element_is_none() {
            var sp = new InteractionStateProvider();
            var e = new Element("div");
            Assert.That(sp.GetState(e), Is.EqualTo(ElementState.None));
        }

        [Test]
        public void Hover_set_on_pointer_move() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(a) & ElementState.Hover) != 0, Is.True);
        }

        [Test]
        public void Hover_clears_when_pointer_leaves() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Dispatcher(doc, ht);

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            d.DispatchPointerMove(150, 50, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(a) & ElementState.Hover) != 0, Is.False);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Hover) != 0, Is.True);
        }

        [Test]
        public void Active_set_during_pointer_down_held() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);

            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Active) != 0, Is.True);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Active) != 0, Is.False);
        }

        [Test]
        public void Focus_set_on_focused_element_only() {
            var doc = Html("<button id=\"a\"></button><button id=\"b\"></button>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            var d = Dispatcher(doc, ht);

            d.Focus(a);
            Assert.That((d.StateProvider.GetState(a) & ElementState.Focus) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Focus) != 0, Is.False);
        }

        [Test]
        public void FocusWithin_set_on_ancestors_of_focused() {
            var doc = Html("<section id=\"s\"><div id=\"wrap\"><button id=\"b\"></button></div></section>");
            var s = doc.GetElementById("s");
            var wrap = doc.GetElementById("wrap");
            var b = doc.GetElementById("b");
            var d = Dispatcher(doc, new FakeHitTester());

            d.Focus(b);
            Assert.That((d.StateProvider.GetState(b) & ElementState.FocusWithin) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(wrap) & ElementState.FocusWithin) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(s) & ElementState.FocusWithin) != 0, Is.True);
        }

        [Test]
        public void Checked_state_from_attribute() {
            var doc = Html("<input id=\"x\" type=\"checkbox\" checked><input id=\"y\" type=\"checkbox\">");
            var x = doc.GetElementById("x");
            var y = doc.GetElementById("y");
            var sp = new InteractionStateProvider();
            Assert.That((sp.GetState(x) & ElementState.Checked) != 0, Is.True);
            Assert.That((sp.GetState(y) & ElementState.Checked) != 0, Is.False);
        }

        [Test]
        public void Checked_only_for_checkbox_or_radio() {
            var doc = Html("<input id=\"t\" type=\"text\" checked><input id=\"r\" type=\"radio\" checked>");
            var t = doc.GetElementById("t");
            var r = doc.GetElementById("r");
            var sp = new InteractionStateProvider();
            Assert.That((sp.GetState(t) & ElementState.Checked) != 0, Is.False);
            Assert.That((sp.GetState(r) & ElementState.Checked) != 0, Is.True);
        }

        [Test]
        public void Placeholder_shown_when_input_value_empty_and_placeholder_present() {
            var doc = Html("<input id=\"a\" placeholder=\"hi\"><input id=\"b\" placeholder=\"hi\" value=\"x\"><input id=\"c\" value=\"\">");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var c = doc.GetElementById("c");
            var sp = new InteractionStateProvider();
            Assert.That((sp.GetState(a) & ElementState.PlaceholderShown) != 0, Is.True);
            Assert.That((sp.GetState(b) & ElementState.PlaceholderShown) != 0, Is.False);
            Assert.That((sp.GetState(c) & ElementState.PlaceholderShown) != 0, Is.False);
        }

        [Test]
        public void Disabled_attribute_shows_as_disabled_state() {
            var doc = Html("<button id=\"b\" disabled></button>");
            var b = doc.GetElementById("b");
            var sp = new InteractionStateProvider();
            Assert.That((sp.GetState(b) & ElementState.Disabled) != 0, Is.True);
        }

        [Test]
        public void Reparenting_focus_within_updates_correctly() {
            var doc = Html("<section id=\"s1\"></section><section id=\"s2\"><button id=\"b\"></button></section>");
            var s1 = doc.GetElementById("s1");
            var s2 = doc.GetElementById("s2");
            var b = doc.GetElementById("b");
            var d = Dispatcher(doc, new FakeHitTester());

            d.Focus(b);
            Assert.That((d.StateProvider.GetState(s2) & ElementState.FocusWithin) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(s1) & ElementState.FocusWithin) != 0, Is.False);

            s2.RemoveChild(b);
            s1.AppendChild(b);
            d.Focus(b);
            Assert.That((d.StateProvider.GetState(s1) & ElementState.FocusWithin) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(s2) & ElementState.FocusWithin) != 0, Is.False);
        }

        [Test]
        public void FocusVisible_set_on_keyboard_focus_cleared_on_pointer_focus() {
            var doc = Html("<button id=\"a\"></button><button id=\"b\"></button>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Dispatcher(doc, ht);

            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.None, false);
            Assert.That(d.FocusedElement, Is.SameAs(a));
            Assert.That((d.StateProvider.GetState(a) & ElementState.FocusVisible) != 0, Is.True);

            d.DispatchPointerDown(150, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(150, 50, 0, KeyModifiers.None);
            Assert.That(d.FocusedElement, Is.SameAs(b));
            Assert.That((d.StateProvider.GetState(b) & ElementState.FocusVisible) != 0, Is.False);
        }
    }
}
