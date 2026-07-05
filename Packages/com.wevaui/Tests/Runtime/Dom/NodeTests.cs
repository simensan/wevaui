using System;
using NUnit.Framework;
using Weva.Dom;

namespace Weva.Tests.Dom {
    public class NodeTests {
        [Test]
        public void Appended_child_has_parent_set() {
            var doc = new Document();
            var div = new Element("div");
            doc.AppendChild(div);
            Assert.That(div.Parent, Is.SameAs(doc));
        }

        [Test]
        public void Appended_child_inherits_owner_document() {
            var doc = new Document();
            var div = new Element("div");
            doc.AppendChild(div);
            Assert.That(div.OwnerDocument, Is.SameAs(doc));
        }

        [Test]
        public void Owner_document_propagates_to_grandchildren() {
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            div.AppendChild(span);
            doc.AppendChild(div);
            Assert.That(span.OwnerDocument, Is.SameAs(doc));
        }

        [Test]
        public void Reparenting_removes_from_previous_parent() {
            var doc = new Document();
            var a = new Element("a");
            var b = new Element("b");
            var span = new Element("span");
            a.AppendChild(span);
            b.AppendChild(span);
            Assert.That(a.Children, Has.Count.EqualTo(0));
            Assert.That(b.Children, Has.Count.EqualTo(1));
            Assert.That(span.Parent, Is.SameAs(b));
        }

        [Test]
        public void RemoveChild_unlinks_parent() {
            var doc = new Document();
            var div = new Element("div");
            doc.AppendChild(div);
            doc.RemoveChild(div);
            Assert.That(div.Parent, Is.Null);
            Assert.That(doc.Children, Has.Count.EqualTo(0));
        }

        [Test]
        public void RemoveChild_returns_false_for_non_child() {
            var doc = new Document();
            var orphan = new Element("div");
            Assert.That(doc.RemoveChild(orphan), Is.False);
        }

        [Test]
        public void AppendChild_null_throws() {
            var doc = new Document();
            Assert.Throws<ArgumentNullException>(() => doc.AppendChild(null));
        }

        [Test]
        public void AppendChild_self_throws() {
            var div = new Element("div");
            Assert.Throws<InvalidOperationException>(() => div.AppendChild(div));
        }

        [Test]
        public void AppendChild_ancestor_throws() {
            var outer = new Element("div");
            var inner = new Element("span");
            outer.AppendChild(inner);
            Assert.Throws<InvalidOperationException>(() => inner.AppendChild(outer));
        }

        [Test]
        public void Children_reflects_insertion_order() {
            var doc = new Document();
            var a = new Element("a"); var b = new Element("b"); var c = new Element("c");
            doc.AppendChild(a);
            doc.AppendChild(b);
            doc.AppendChild(c);
            Assert.That(doc.Children[0], Is.SameAs(a));
            Assert.That(doc.Children[1], Is.SameAs(b));
            Assert.That(doc.Children[2], Is.SameAs(c));
        }

        [Test]
        public void TextNode_can_be_appended() {
            var div = new Element("div");
            div.AppendChild(new TextNode("hello"));
            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(((TextNode)div.Children[0]).Data, Is.EqualTo("hello"));
        }
    }
}
