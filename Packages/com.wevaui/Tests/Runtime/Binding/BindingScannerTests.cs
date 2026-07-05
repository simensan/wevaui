using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Binding {
    public class BindingScannerTests {
        public class TestController {
            public string Name = "Alice";
            public int Count = 3;
            public void OnClick() { }
            public void OnSubmit() { }
            public void OnNameChange() { }
        }

        static Document Html(string s) => HtmlParser.Parse(s);

        [Test]
        public void Detects_text_binding_in_text_content() {
            var doc = Html("<p>Hello {{ Name }}</p>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.TextBindings.Count, Is.EqualTo(1));
            Assert.That(set.AttributeBindings.Count, Is.EqualTo(0));
            Assert.That(set.EventBindings.Count, Is.EqualTo(0));
        }

        [Test]
        public void Detects_attribute_binding() {
            var doc = Html("<input value=\"{{ Name }}\" />");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.AttributeBindings.Count, Is.EqualTo(1));
            Assert.That(set.AttributeBindings[0].AttributeName, Is.EqualTo("value"));
        }

        [Test]
        public void Detects_attribute_binding_after_binding_materializes_value() {
            var doc = Html("<div style=\"width: {{ Count }}%\"></div>");
            var controller = new TestController();
            var first = BindingScanner.Scan(doc, controller);
            Assert.That(first.AttributeBindings.Count, Is.EqualTo(1));

            first.Update(controller);
            Element div = null;
            foreach (var el in doc.GetElementsByTagName("div")) { div = el; break; }
            Assert.That(div, Is.Not.Null);
            Assert.That(div.GetAttribute("style"), Is.EqualTo("width: 3%"));

            var second = BindingScanner.Scan(doc, controller);
            Assert.That(second.AttributeBindings.Count, Is.EqualTo(1));
            Assert.That(second.AttributeBindings[0].AttributeName, Is.EqualTo("style"));
        }

        [Test]
        public void Detects_on_click_attribute_as_event_binding() {
            var doc = Html("<button on-click=\"OnClick\">go</button>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(1));
            Assert.That(set.EventBindings[0].Kind, Is.EqualTo(EventKind.Click));
            Assert.That(set.EventBindings[0].MethodName, Is.EqualTo("OnClick"));
        }

        [Test]
        public void Detects_all_six_event_attributes() {
            var doc = Html(
                "<form on-submit=\"OnSubmit\">" +
                  "<input on-change=\"OnNameChange\" on-input=\"OnNameChange\" on-focus=\"OnClick\" on-blur=\"OnClick\" />" +
                  "<button on-click=\"OnClick\">go</button>" +
                "</form>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(6));
        }

        [Test]
        public void Skips_unknown_on_attribute() {
            var doc = Html("<div on-something-custom=\"OnClick\">x</div>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(0));
        }

        [Test]
        public void Multiple_bindings_per_element() {
            var doc = Html("<button class=\"{{ Name }}\" title=\"count={{ Count }}\" on-click=\"OnClick\">go</button>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.AttributeBindings.Count, Is.EqualTo(2));
            Assert.That(set.EventBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Recurses_into_nested_elements() {
            var doc = Html("<div><section><span>{{ Name }}</span></section></div>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.TextBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Composes_bindingset_with_all_three_types() {
            var doc = Html(
                "<main>" +
                  "<p>Coins: {{ Count }}</p>" +
                  "<button class=\"btn-{{ Name }}\" on-click=\"OnClick\">go</button>" +
                "</main>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.TextBindings.Count, Is.EqualTo(1));
            Assert.That(set.AttributeBindings.Count, Is.EqualTo(1));
            Assert.That(set.EventBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Text_without_braces_produces_no_text_binding() {
            var doc = Html("<p>just text, no bindings here</p>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.TextBindings.Count, Is.EqualTo(0));
        }

        [Test]
        public void Attribute_without_braces_produces_no_attribute_binding() {
            var doc = Html("<input value=\"plain\" />");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.AttributeBindings.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_event_attribute_produces_warning_not_throw() {
            var doc = new Document();
            var btn = new Element("button");
            btn.SetAttribute("on-click", "");
            doc.AppendChild(btn);
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(0));
            Assert.That(set.Warnings.Count, Is.GreaterThan(0));
        }

        [Test]
        public void Unknown_event_method_produces_warning_not_throw() {
            var doc = Html("<button on-click=\"NoSuchMethod\">go</button>");
            var set = BindingScanner.Scan(doc, new TestController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(0));
            Assert.That(set.Warnings.Count, Is.EqualTo(1));
            Assert.That(set.Warnings[0], Does.Contain("NoSuchMethod"));
        }
    }
}
