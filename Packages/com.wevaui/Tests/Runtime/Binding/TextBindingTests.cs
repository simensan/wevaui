using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Binding {
    public class TextBindingTests {
        class Ctx {
            public string Name = "Alice";
            public int N = 1;
        }

        static (Document doc, Element el, TextNode tn) Build(string textData) {
            var doc = new Document();
            var el = new Element("span");
            doc.AppendChild(el);
            var tn = new TextNode(textData);
            el.AppendChild(tn);
            return (doc, el, tn);
        }

        [Test]
        public void Update_writes_template_output_to_text_node() {
            var (_, _, tn) = Build("placeholder");
            var tpl = BindingTemplate.Parse("Hi {{ Name }}");
            var b = new TextBinding(tn, tpl);
            b.Update(new Ctx());
            Assert.That(tn.Data, Is.EqualTo("Hi Alice"));
        }

        [Test]
        public void Update_is_idempotent_returns_false_on_second_call() {
            var (_, _, tn) = Build("placeholder");
            var tpl = BindingTemplate.Parse("Hi {{ Name }}");
            var b = new TextBinding(tn, tpl);
            bool first = b.Update(new Ctx());
            bool second = b.Update(new Ctx());
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(tn.Data, Is.EqualTo("Hi Alice"));
        }

        [Test]
        public void Update_writes_again_after_value_changed() {
            var (_, _, tn) = Build("placeholder");
            var tpl = BindingTemplate.Parse("N={{ N }}");
            var b = new TextBinding(tn, tpl);
            var c = new Ctx { N = 1 };
            b.Update(c);
            Assert.That(tn.Data, Is.EqualTo("N=1"));
            c.N = 2;
            bool changed = b.Update(c);
            Assert.That(changed, Is.True);
            Assert.That(tn.Data, Is.EqualTo("N=2"));
        }

        [Test]
        public void Null_context_renders_bindings_as_empty() {
            var (_, _, tn) = Build("x");
            var tpl = BindingTemplate.Parse("[{{ Name }}]");
            var b = new TextBinding(tn, tpl);
            b.Update(null);
            Assert.That(tn.Data, Is.EqualTo("[]"));
        }

        [Test]
        public void Pure_literal_template_writes_once() {
            var (_, _, tn) = Build("placeholder");
            var tpl = BindingTemplate.Parse("static-text");
            var b = new TextBinding(tn, tpl);
            Assert.That(b.Update(new Ctx()), Is.True);
            Assert.That(tn.Data, Is.EqualTo("static-text"));
            Assert.That(b.Update(new Ctx()), Is.False);
        }

        [Test]
        public void Update_marks_invalidation_tracker_on_change() {
            var (doc, el, tn) = Build("old");
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            // Drain mutation noise from initial DOM build (which happened before attach in this case).
            tracker.Clear();
            var tpl = BindingTemplate.Parse("Hi {{ Name }}");
            var b = new TextBinding(tn, tpl);
            b.Update(new Ctx(), tracker);
            // The text-node's Data setter triggers a DomMutation TextChanged → tracker marks tn Layout|Paint.
            Assert.That(tracker.IsDirty(el, InvalidationKind.Layout), Is.True);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Paint), Is.True);
            Assert.That(tracker.IsDirty(tn, InvalidationKind.Layout), Is.False);
        }

        [Test]
        public void Update_with_no_change_does_not_dirty_tracker() {
            var (doc, el, tn) = Build("Hi Alice");
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();
            var tpl = BindingTemplate.Parse("Hi {{ Name }}");
            var b = new TextBinding(tn, tpl);
            bool changed = b.Update(new Ctx(), tracker);
            Assert.That(changed, Is.False);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Layout), Is.False);
        }
    }
}
