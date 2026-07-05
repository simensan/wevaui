using NUnit.Framework;
using Weva.Binding;
using Weva.Components;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Binding {
    // Regression coverage for the working surface where bindings meet
    // user-defined components (templates + slots). The audit confirmed:
    //  - Template registration is via <template id="..."> harvested by
    //    ComponentRegistry.RegisterAllFromDocument, plus slot projection.
    //  - There is NO per-component data context — binding always resolves
    //    against the single document-wide controller — so an event handler
    //    declared inside a template's clone still routes to the host
    //    document's controller.
    //  - {{ ... }} is permitted inside attribute values: AttributeBinding
    //    re-renders the whole template on Update.
    public class BindingComponentInteropTests {
        public class Controller {
            public string Status = "ok";
            public int Clicks;
            public void OnPing() => Clicks++;
        }

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        static (Document doc, Controller c) Build(string html) {
            var doc = HtmlParser.Parse(html);
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            new ComponentExpander(reg).Expand(doc);
            return (doc, new Controller());
        }

        // {{ ... }} inside an attribute value template-replaces and the
        // resulting attribute round-trips through Element.SetAttribute.
        [Test]
        public void Attribute_binding_inside_class_value_renders_via_template() {
            var (doc, c) = Build(
                "<template id=\"badge\"><span class=\"badge badge-{{ Status }}\"><slot></slot></span></template>" +
                "<badge>x</badge>");

            var set = BindingScanner.Scan(doc, c);
            Assert.That(set.AttributeBindings.Count, Is.EqualTo(1), "expected one attribute binding inside the cloned template");

            set.AttributeBindings[0].Update(c);
            var span = FindByTag(doc, "span");
            Assert.That(span.GetAttribute("class"), Is.EqualTo("badge badge-ok"));

            c.Status = "warn";
            set.AttributeBindings[0].Update(c);
            Assert.That(span.GetAttribute("class"), Is.EqualTo("badge badge-warn"));
        }

        // Slot projection preserves the projected light-dom child's identity,
        // and that child can carry its own {{ binding }} resolved against the
        // single document controller (there is no separate component scope).
        [Test]
        public void Slot_projected_text_binding_resolves_against_document_controller() {
            var (doc, c) = Build(
                "<template id=\"card\"><article><slot></slot></article></template>" +
                "<card><p>hi {{ Status }}</p></card>");

            var set = BindingScanner.Scan(doc, c);
            Assert.That(set.TextBindings.Count, Is.EqualTo(1));

            set.TextBindings[0].Update(c);
            var p = FindByTag(doc, "p");
            var textNode = (TextNode)p.Children[0];
            Assert.That(textNode.Data, Is.EqualTo("hi ok"));
        }

        // Event handlers declared inside a template clone are scanned just
        // like any other element and route to the host document's controller.
        [Test]
        public void OnClick_inside_expanded_component_invokes_document_controller() {
            var (doc, c) = Build(
                "<template id=\"pinger\"><button id=\"go\" on-click=\"OnPing\"><slot></slot></button></template>" +
                "<pinger>tap</pinger>");

            var set = BindingScanner.Scan(doc, c);
            Assert.That(set.EventBindings.Count, Is.EqualTo(1));
            Assert.That(set.EventBindings[0].Kind, Is.EqualTo(EventKind.Click));
            Assert.That(set.EventBindings[0].Controller, Is.SameAs(c));

            // Drive a real click through the dispatcher to confirm the
            // template-cloned button is reachable and routes to the host
            // document's controller (no separate component scope exists).
            var inner = set.EventBindings[0].Target;
            var ht = new BindingFakeHitTester();
            ht.Add(inner, 0, 0, 100, 100);
            var dispatcher = new EventDispatcher(doc, ht, new FakeUIClock());
            set.EventBindings[0].Wire(dispatcher);
            dispatcher.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            dispatcher.DispatchPointerUp(10, 10, 0, KeyModifiers.None);

            Assert.That(c.Clicks, Is.EqualTo(1));
        }
    }
}
