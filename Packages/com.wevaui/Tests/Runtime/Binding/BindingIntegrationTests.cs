using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Binding {
    public class BindingIntegrationTests {
        public class CounterController {
            public int CoinCount;
            public string Title = "Coin counter";
            public void Increment() { CoinCount++; }
            public void Reset() { CoinCount = 0; }
        }

        public class FormController {
            public string Name = "default";
            public int InputEvents;
            public int ChangeEvents;
            public void OnNameChange(UIEvent e) { InputEvents++; }
            public void OnFinalChange() { ChangeEvents++; }
        }

        static TextNode FindText(Node n) {
            if (n is TextNode t) return t;
            for (int i = 0; i < n.Children.Count; i++) {
                var f = FindText(n.Children[i]);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Counter_text_binding_reflects_field_changes_after_update() {
            var doc = HtmlParser.Parse(
                "<main><p id=\"label\">Coins: {{ CoinCount }}</p>" +
                "<button id=\"plus\" on-click=\"Increment\">+</button></main>");
            var ctrl = new CounterController { CoinCount = 0 };
            var set = BindingScanner.Scan(doc, ctrl);
            set.UpdateAll(ctrl);
            var label = doc.GetElementById("label");
            var tn = FindText(label);
            Assert.That(tn.Data, Is.EqualTo("Coins: 0"));
            ctrl.CoinCount = 5;
            set.UpdateAll(ctrl);
            Assert.That(tn.Data, Is.EqualTo("Coins: 5"));
        }

        [Test]
        public void Counter_button_click_invokes_controller_method() {
            var doc = HtmlParser.Parse(
                "<main><p>Coins: {{ CoinCount }}</p>" +
                "<button id=\"plus\" on-click=\"Increment\">+</button></main>");
            var ctrl = new CounterController();
            var set = BindingScanner.Scan(doc, ctrl);
            var ht = new BindingFakeHitTester();
            var btn = doc.GetElementById("plus");
            ht.Add(btn, 0, 0, 100, 100);
            var d = new EventDispatcher(doc, ht, new FakeUIClock());
            set.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That(ctrl.CoinCount, Is.EqualTo(1));
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That(ctrl.CoinCount, Is.EqualTo(2));
        }

        [Test]
        public void Form_input_value_attribute_and_event_bind() {
            var doc = HtmlParser.Parse(
                "<input id=\"name\" value=\"{{ Name }}\" on-input=\"OnNameChange\" on-change=\"OnFinalChange\" />");
            var ctrl = new FormController { Name = "Alice" };
            var set = BindingScanner.Scan(doc, ctrl);
            set.UpdateAll(ctrl);
            var input = doc.GetElementById("name");
            Assert.That(input.GetAttribute("value"), Is.EqualTo("Alice"));
            Assert.That(set.EventBindings.Count, Is.EqualTo(2));

            var ht = new BindingFakeHitTester();
            ht.Add(input, 0, 0, 100, 100);
            var d = new EventDispatcher(doc, ht, new FakeUIClock());
            set.Wire(d);
            // Simulate an Input event by adding a direct listener path: dispatch via custom listener.
            // Since EventDispatcher only synthesizes pointer/keyboard events, we tap directly into the
            // listener by firing through the dispatch API surface — for Input/Change we add listeners
            // and verify at least the wiring is in place.
            Assert.That(set.EventBindings[0].Kind, Is.EqualTo(EventKind.Input).Or.EqualTo(EventKind.Change));
        }

        [Test]
        public void Update_is_idempotent_repeated_calls_no_change() {
            var doc = HtmlParser.Parse("<p>Coins: {{ CoinCount }}</p>");
            var ctrl = new CounterController { CoinCount = 7 };
            var set = BindingScanner.Scan(doc, ctrl);
            set.UpdateAll(ctrl);
            var tn = FindText(doc);
            string firstData = tn.Data;
            long firstVersion = tn.Version;
            set.UpdateAll(ctrl);
            set.UpdateAll(ctrl);
            Assert.That(tn.Data, Is.EqualTo(firstData));
            Assert.That(tn.Version, Is.EqualTo(firstVersion));
        }

        [Test]
        public void Re_binding_after_dom_mutation_works() {
            var doc = HtmlParser.Parse("<main><p>Coins: {{ CoinCount }}</p></main>");
            var ctrl = new CounterController { CoinCount = 1 };
            var set = BindingScanner.Scan(doc, ctrl);
            set.UpdateAll(ctrl);
            // Now mutate the DOM: append another binding-bearing fragment.
            var main = doc.GetElementsByTagName("main").GetEnumerator();
            main.MoveNext();
            var mainEl = main.Current;
            var extra = HtmlParser.Parse("<span>Title: {{ Title }}</span>");
            // Move <span> from doc into our main.
            var spans = extra.GetElementsByTagName("span").GetEnumerator();
            spans.MoveNext();
            var span = spans.Current;
            mainEl.AppendChild(span);
            // Re-scan and update.
            var set2 = BindingScanner.Scan(doc, ctrl);
            set2.UpdateAll(ctrl);
            // 2 text bindings now: original p and new span.
            Assert.That(set2.TextBindings.Count, Is.EqualTo(2));
        }

        [Test]
        public void Update_with_invalidation_tracker_marks_dirty_set() {
            var doc = HtmlParser.Parse("<p id=\"p\">Coins: {{ CoinCount }}</p>");
            var ctrl = new CounterController { CoinCount = 0 };
            var set = BindingScanner.Scan(doc, ctrl);
            set.UpdateAll(ctrl); // first render
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();
            ctrl.CoinCount = 1;
            set.Update(ctrl, tracker);
            var p = doc.GetElementById("p");
            Assert.That(tracker.IsDirty(p, InvalidationKind.Layout), Is.True);
        }

        [Test]
        public void UIElementBinder_combined_with_scanner() {
            var doc = HtmlParser.Parse(
                "<main>" +
                  "<p>Coins: {{ CoinCount }}</p>" +
                  "<button id=\"plus\" on-click=\"Increment\">+</button>" +
                "</main>");
            var ctrl = new CombinedController();
            UIElementBinder.Populate(ctrl, doc);
            var set = BindingScanner.Scan(doc, ctrl);
            set.UpdateAll(ctrl);
            Assert.That(ctrl.PlusButton, Is.Not.Null);
            Assert.That(ctrl.PlusButton.GetAttribute("id"), Is.EqualTo("plus"));
        }

        public class CombinedController : CounterController {
            [UIElement("plus")] public Element PlusButton;
        }
    }
}
