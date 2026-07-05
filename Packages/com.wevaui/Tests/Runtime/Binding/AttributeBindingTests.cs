using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Binding {
    public class AttributeBindingTests {
        class Ctx {
            public string Name = "Alice";
            public string ButtonClass = "primary big";
        }

        [Test]
        public void Update_writes_attribute_value() {
            var doc = new Document();
            var el = new Element("input");
            doc.AppendChild(el);
            var tpl = BindingTemplate.Parse("{{ Name }}");
            var b = new AttributeBinding(el, "value", tpl);
            b.Update(new Ctx());
            Assert.That(el.GetAttribute("value"), Is.EqualTo("Alice"));
        }

        [Test]
        public void Update_with_mixed_template() {
            var doc = new Document();
            var el = new Element("button");
            doc.AppendChild(el);
            var tpl = BindingTemplate.Parse("btn {{ ButtonClass }} ready");
            var b = new AttributeBinding(el, "class", tpl);
            b.Update(new Ctx());
            Assert.That(el.GetAttribute("class"), Is.EqualTo("btn primary big ready"));
        }

        [Test]
        public void Update_is_idempotent_on_repeated_call() {
            var doc = new Document();
            var el = new Element("input");
            doc.AppendChild(el);
            var tpl = BindingTemplate.Parse("{{ Name }}");
            var b = new AttributeBinding(el, "value", tpl);
            bool first = b.Update(new Ctx());
            bool second = b.Update(new Ctx());
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
        }

        [Test]
        public void Update_fires_dom_mutation() {
            var doc = new Document();
            var el = new Element("input");
            doc.AppendChild(el);
            var seen = new List<DomMutation>();
            doc.Mutated += m => seen.Add(m);
            var tpl = BindingTemplate.Parse("{{ Name }}");
            var b = new AttributeBinding(el, "value", tpl);
            b.Update(new Ctx());
            bool sawAttr = false;
            foreach (var m in seen) {
                if (m.Kind == DomMutationKind.AttributeAdded && m.AttributeName == "value") sawAttr = true;
            }
            Assert.That(sawAttr, Is.True);
        }

        [Test]
        public void Update_marks_invalidation_tracker() {
            var doc = new Document();
            var el = new Element("input");
            doc.AppendChild(el);
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();
            var tpl = BindingTemplate.Parse("{{ Name }}");
            var b = new AttributeBinding(el, "value", tpl);
            b.Update(new Ctx(), tracker);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Style), Is.True);
        }

        [Test]
        public void Null_context_renders_bindings_as_empty_string() {
            var doc = new Document();
            var el = new Element("input");
            doc.AppendChild(el);
            var tpl = BindingTemplate.Parse("{{ Name }}");
            var b = new AttributeBinding(el, "value", tpl);
            b.Update(null);
            Assert.That(el.GetAttribute("value"), Is.EqualTo(""));
        }

        [Test]
        public void Update_writes_again_when_value_changes() {
            var doc = new Document();
            var el = new Element("input");
            doc.AppendChild(el);
            var tpl = BindingTemplate.Parse("{{ Name }}");
            var b = new AttributeBinding(el, "value", tpl);
            var ctx = new Ctx();
            b.Update(ctx);
            Assert.That(el.GetAttribute("value"), Is.EqualTo("Alice"));
            ctx.Name = "Bob";
            bool changed = b.Update(ctx);
            Assert.That(changed, Is.True);
            Assert.That(el.GetAttribute("value"), Is.EqualTo("Bob"));
        }
    }
}
