using NUnit.Framework;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    public class DomMutationTests {
        [Test]
        public void ChildAdded_factory_sets_target_subject_kind() {
            var parent = new Element("div");
            var child = new Element("span");
            var m = DomMutation.ChildAdded(parent, child);
            Assert.That(m.Target, Is.SameAs(parent));
            Assert.That(m.Subject, Is.SameAs(child));
            Assert.That(m.Kind, Is.EqualTo(DomMutationKind.ChildAdded));
            Assert.That(m.AttributeName, Is.Null);
            Assert.That(m.OldValue, Is.Null);
            Assert.That(m.NewValue, Is.Null);
        }

        [Test]
        public void AttributeChanged_factory_carries_old_and_new() {
            var e = new Element("div");
            var m = DomMutation.AttributeChanged(e, "class", "old", "new");
            Assert.That(m.Target, Is.SameAs(e));
            Assert.That(m.Subject, Is.SameAs(e));
            Assert.That(m.Kind, Is.EqualTo(DomMutationKind.AttributeChanged));
            Assert.That(m.AttributeName, Is.EqualTo("class"));
            Assert.That(m.OldValue, Is.EqualTo("old"));
            Assert.That(m.NewValue, Is.EqualTo("new"));
        }

        [Test]
        public void TextChanged_factory_subject_equals_target() {
            var t = new TextNode("hi");
            var m = DomMutation.TextChanged(t, "hi", "bye");
            Assert.That(m.Target, Is.SameAs(t));
            Assert.That(m.Subject, Is.SameAs(t));
            Assert.That(m.Kind, Is.EqualTo(DomMutationKind.TextChanged));
            Assert.That(m.OldValue, Is.EqualTo("hi"));
            Assert.That(m.NewValue, Is.EqualTo("bye"));
        }

        [Test]
        public void ToString_includes_kind_and_target_diagnostic() {
            var parent = new Element("div");
            var child = new Element("span");
            var added = DomMutation.ChildAdded(parent, child).ToString();
            Assert.That(added, Does.Contain("ChildAdded"));
            Assert.That(added, Does.Contain("<div>"));
            Assert.That(added, Does.Contain("<span>"));

            var attrChanged = DomMutation.AttributeChanged(parent, "id", "a", "b").ToString();
            Assert.That(attrChanged, Does.Contain("AttributeChanged"));
            Assert.That(attrChanged, Does.Contain("id"));
            Assert.That(attrChanged, Does.Contain("a"));
            Assert.That(attrChanged, Does.Contain("b"));

            var text = new TextNode("x");
            var textChanged = DomMutation.TextChanged(text, "x", "y").ToString();
            Assert.That(textChanged, Does.Contain("TextChanged"));
            Assert.That(textChanged, Does.Contain("#text"));
        }
    }
}
