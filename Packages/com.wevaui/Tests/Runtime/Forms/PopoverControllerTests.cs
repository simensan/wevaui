using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;
using Weva.Tests.Events;

namespace Weva.Tests.Forms {
    public class PopoverControllerTests {
        EventDispatcher Build(Document doc, FakeHitTester ht) {
            return new EventDispatcher(doc, ht, new FakeUIClock());
        }

        [Test]
        public void PopoverTarget_with_toggle_action_toggles_on_click() {
            var doc = HtmlParser.Parse(
                "<main><button id='b' popovertarget='p'>btn</button>" +
                "<div id='p' popover>x</div></main>");
            var btn = doc.GetElementById("b");
            var pop = doc.GetElementById("p");
            var ht = new FakeHitTester();
            ht.Add(btn, 0, 0, 50, 30);
            var disp = Build(doc, ht);
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);

            Assert.That(Popover.IsOpen(pop), Is.False);
            disp.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            disp.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(Popover.IsOpen(pop), Is.True);
        }

        [Test]
        public void PopoverTarget_with_show_action_only_shows() {
            var doc = HtmlParser.Parse(
                "<main><button id='b' popovertarget='p' popovertargetaction='show'>btn</button>" +
                "<div id='p' popover>x</div></main>");
            var btn = doc.GetElementById("b");
            var pop = doc.GetElementById("p");
            var ht = new FakeHitTester();
            ht.Add(btn, 0, 0, 50, 30);
            var disp = Build(doc, ht);
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);

            ctrl.Stack.Show(pop);
            Assert.That(Popover.IsOpen(pop), Is.True);
            // Second click via show button: still open, no toggle.
            disp.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            disp.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(Popover.IsOpen(pop), Is.True);
        }

        [Test]
        public void PopoverTarget_with_hide_action_hides() {
            var doc = HtmlParser.Parse(
                "<main><button id='b' popovertarget='p' popovertargetaction='hide'>btn</button>" +
                "<div id='p' popover>x</div></main>");
            var btn = doc.GetElementById("b");
            var pop = doc.GetElementById("p");
            var ht = new FakeHitTester();
            ht.Add(btn, 0, 0, 50, 30);
            var disp = Build(doc, ht);
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);

            ctrl.Stack.Show(pop);
            disp.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            disp.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(Popover.IsOpen(pop), Is.False);
        }

        [Test]
        public void Escape_key_closes_top_auto_popover() {
            var doc = HtmlParser.Parse(
                "<main><div id='p' popover>x</div></main>");
            var pop = doc.GetElementById("p");
            var disp = Build(doc, new FakeHitTester());
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);
            ctrl.Stack.Show(pop);

            disp.DispatchKeyDown("Escape", "Escape", KeyModifiers.None, false);
            Assert.That(Popover.IsOpen(pop), Is.False);
        }

        [Test]
        public void Escape_does_not_close_manual_popover() {
            var doc = HtmlParser.Parse(
                "<main><div id='p' popover='manual'>x</div></main>");
            var pop = doc.GetElementById("p");
            var disp = Build(doc, new FakeHitTester());
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);
            ctrl.Stack.Show(pop);

            disp.DispatchKeyDown("Escape", "Escape", KeyModifiers.None, false);
            Assert.That(Popover.IsOpen(pop), Is.True);
        }

        [Test]
        public void Outside_click_dismisses_auto_popover() {
            var doc = HtmlParser.Parse(
                "<main id='m'><div id='p' popover>x</div></main>");
            var main = doc.GetElementById("m");
            var pop = doc.GetElementById("p");
            var ht = new FakeHitTester();
            // Main covers the page; popover is a smaller inset region, but the
            // hit test for outside-click should land on `main` (not on `p`).
            ht.Add(main, 0, 0, 1000, 1000);
            ht.Add(pop, 0, 0, 100, 100);
            var disp = Build(doc, ht);
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);
            ctrl.Stack.Show(pop);

            // Click outside the popover region.
            disp.DispatchPointerDown(500, 500, 0, KeyModifiers.None);
            disp.DispatchPointerUp(500, 500, 0, KeyModifiers.None);
            Assert.That(Popover.IsOpen(pop), Is.False);
        }

        [Test]
        public void Outside_click_does_not_dismiss_manual() {
            var doc = HtmlParser.Parse(
                "<main id='m'><div id='p' popover='manual'>x</div></main>");
            var main = doc.GetElementById("m");
            var pop = doc.GetElementById("p");
            var ht = new FakeHitTester();
            ht.Add(main, 0, 0, 1000, 1000);
            ht.Add(pop, 0, 0, 100, 100);
            var disp = Build(doc, ht);
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);
            ctrl.Stack.Show(pop);

            disp.DispatchPointerDown(500, 500, 0, KeyModifiers.None);
            disp.DispatchPointerUp(500, 500, 0, KeyModifiers.None);
            Assert.That(Popover.IsOpen(pop), Is.True);
        }

        [Test]
        public void HandleTrigger_works_synchronously_without_dispatcher() {
            var doc = HtmlParser.Parse(
                "<main><button id='b' popovertarget='p'>btn</button>" +
                "<div id='p' popover>x</div></main>");
            var ctrl = new PopoverController(doc);
            ctrl.HandleTrigger(doc.GetElementById("b"));
            Assert.That(Popover.IsOpen(doc.GetElementById("p")), Is.True);
        }

        [Test]
        public void Stack_top_changes_as_popovers_show_and_hide() {
            var doc = HtmlParser.Parse(
                "<main><div id='a' popover>A</div><div id='b' popover>B</div></main>");
            var ctrl = new PopoverController(doc);
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            ctrl.Stack.Show(a);
            ctrl.Stack.Show(b);
            Assert.That(ctrl.Stack.Top, Is.SameAs(b));
            ctrl.Stack.Hide(b);
            Assert.That(ctrl.Stack.Top, Is.SameAs(a));
        }

        [Test]
        public void Escape_walks_stack_one_step_per_press() {
            var doc = HtmlParser.Parse(
                "<main><div id='a' popover>A</div><div id='b' popover>B</div></main>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var disp = Build(doc, new FakeHitTester());
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);
            ctrl.Stack.Show(a);
            ctrl.Stack.Show(b);

            disp.DispatchKeyDown("Escape", "Escape", KeyModifiers.None, false);
            Assert.That(Popover.IsOpen(b), Is.False);
            Assert.That(Popover.IsOpen(a), Is.True);

            disp.DispatchKeyDown("Escape", "Escape", KeyModifiers.None, false);
            Assert.That(Popover.IsOpen(a), Is.False);
        }

        [Test]
        public void PopoverTarget_action_show_then_hide_via_separate_buttons() {
            var doc = HtmlParser.Parse(
                "<main>" +
                "<button id='s' popovertarget='p' popovertargetaction='show'>s</button>" +
                "<button id='h' popovertarget='p' popovertargetaction='hide'>h</button>" +
                "<div id='p' popover>x</div></main>");
            var pop = doc.GetElementById("p");
            var ctrl = new PopoverController(doc);
            ctrl.HandleTrigger(doc.GetElementById("s"));
            Assert.That(Popover.IsOpen(pop), Is.True);
            ctrl.HandleTrigger(doc.GetElementById("h"));
            Assert.That(Popover.IsOpen(pop), Is.False);
        }

        [Test]
        public void Click_inside_popover_does_not_dismiss() {
            var doc = HtmlParser.Parse(
                "<main id='m'><div id='p' popover>x</div></main>");
            var pop = doc.GetElementById("p");
            var ht = new FakeHitTester();
            ht.Add(pop, 100, 100, 100, 100);
            var disp = Build(doc, ht);
            var ctrl = new PopoverController(doc);
            ctrl.Wire(disp);
            ctrl.Stack.Show(pop);

            // Click inside popover.
            disp.DispatchPointerDown(150, 150, 0, KeyModifiers.None);
            disp.DispatchPointerUp(150, 150, 0, KeyModifiers.None);
            Assert.That(Popover.IsOpen(pop), Is.True);
        }
    }
}
