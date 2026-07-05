using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    public class MutationEventTests {
        [Test]
        public void AppendChild_fires_ChildAdded_on_parent() {
            var parent = new Element("div");
            DomMutation? captured = null;
            parent.Mutated += m => captured = m;
            var child = new Element("span");
            parent.AppendChild(child);
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.ChildAdded));
            Assert.That(captured.Value.Target, Is.SameAs(parent));
            Assert.That(captured.Value.Subject, Is.SameAs(child));
        }

        [Test]
        public void RemoveChild_fires_ChildRemoved_on_parent() {
            var parent = new Element("div");
            var child = new Element("span");
            parent.AppendChild(child);
            DomMutation? captured = null;
            parent.Mutated += m => captured = m;
            parent.RemoveChild(child);
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.ChildRemoved));
            Assert.That(captured.Value.Target, Is.SameAs(parent));
            Assert.That(captured.Value.Subject, Is.SameAs(child));
        }

        [Test]
        public void SetAttribute_new_key_fires_AttributeAdded() {
            var e = new Element("div");
            DomMutation? captured = null;
            e.Mutated += m => captured = m;
            e.SetAttribute("id", "x");
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.AttributeAdded));
            Assert.That(captured.Value.AttributeName, Is.EqualTo("id"));
            Assert.That(captured.Value.OldValue, Is.Null);
            Assert.That(captured.Value.NewValue, Is.EqualTo("x"));
        }

        [Test]
        public void SetAttribute_existing_key_fires_AttributeChanged() {
            var e = new Element("div");
            e.SetAttribute("id", "old");
            DomMutation? captured = null;
            e.Mutated += m => captured = m;
            e.SetAttribute("id", "new");
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.AttributeChanged));
            Assert.That(captured.Value.AttributeName, Is.EqualTo("id"));
            Assert.That(captured.Value.OldValue, Is.EqualTo("old"));
            Assert.That(captured.Value.NewValue, Is.EqualTo("new"));
        }

        [Test]
        public void SetAttribute_same_value_does_not_fire() {
            var e = new Element("div");
            e.SetAttribute("id", "x");
            int count = 0;
            e.Mutated += _ => count++;
            e.SetAttribute("id", "x");
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void RemoveAttribute_existing_fires_AttributeRemoved() {
            var e = new Element("div");
            e.SetAttribute("id", "x");
            DomMutation? captured = null;
            e.Mutated += m => captured = m;
            e.RemoveAttribute("id");
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.AttributeRemoved));
            Assert.That(captured.Value.AttributeName, Is.EqualTo("id"));
            Assert.That(captured.Value.OldValue, Is.EqualTo("x"));
            Assert.That(captured.Value.NewValue, Is.Null);
        }

        [Test]
        public void RemoveAttribute_missing_does_not_fire() {
            var e = new Element("div");
            int count = 0;
            e.Mutated += _ => count++;
            e.RemoveAttribute("missing");
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void TextNode_Data_change_fires_TextChanged() {
            var t = new TextNode("hello");
            DomMutation? captured = null;
            t.Mutated += m => captured = m;
            t.Data = "world";
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.TextChanged));
            Assert.That(captured.Value.OldValue, Is.EqualTo("hello"));
            Assert.That(captured.Value.NewValue, Is.EqualTo("world"));
        }

        [Test]
        public void TextNode_Data_no_op_does_not_fire() {
            var t = new TextNode("hello");
            int count = 0;
            t.Mutated += _ => count++;
            t.Data = "hello";
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void Mutation_bubbles_to_document_subscribers() {
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);

            var seen = new List<DomMutation>();
            doc.Mutated += m => seen.Add(m);
            span.SetAttribute("class", "foo");
            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(seen[0].Kind, Is.EqualTo(DomMutationKind.AttributeAdded));
            Assert.That(seen[0].Target, Is.SameAs(span));
        }

        [Test]
        public void Multiple_subscribers_all_fire() {
            var e = new Element("div");
            int a = 0, b = 0;
            e.Mutated += _ => a++;
            e.Mutated += _ => b++;
            e.SetAttribute("id", "x");
            Assert.That(a, Is.EqualTo(1));
            Assert.That(b, Is.EqualTo(1));
        }

        [Test]
        public void Removed_subscriber_stops_firing() {
            var e = new Element("div");
            int count = 0;
            System.Action<DomMutation> handler = _ => count++;
            e.Mutated += handler;
            e.SetAttribute("id", "x");
            Assert.That(count, Is.EqualTo(1));
            e.Mutated -= handler;
            e.SetAttribute("id", "y");
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Bubbled_mutation_preserves_original_target() {
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);

            DomMutation? atDiv = null;
            DomMutation? atDoc = null;
            div.Mutated += m => atDiv = m;
            doc.Mutated += m => atDoc = m;
            span.SetAttribute("data-x", "1");
            Assert.That(atDiv.HasValue, Is.True);
            Assert.That(atDoc.HasValue, Is.True);
            Assert.That(atDiv.Value.Target, Is.SameAs(span));
            Assert.That(atDoc.Value.Target, Is.SameAs(span));
        }

        [Test]
        public void RemoveChild_fires_with_intact_parent_chain() {
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);

            Node parentAtFire = null;
            doc.Mutated += m => {
                if (m.Kind == DomMutationKind.ChildRemoved) parentAtFire = m.Subject.Parent;
            };
            div.RemoveChild(span);
            Assert.That(parentAtFire, Is.SameAs(div));
        }

        [Test]
        public void Element_Attributes_indexer_fires_same_as_SetAttribute() {
            var e = new Element("div");
            DomMutation? captured = null;
            e.Mutated += m => captured = m;
            e.Attributes["id"] = "x";
            Assert.That(captured.HasValue, Is.True);
            Assert.That(captured.Value.Kind, Is.EqualTo(DomMutationKind.AttributeAdded));
            Assert.That(captured.Value.AttributeName, Is.EqualTo("id"));
            Assert.That(captured.Value.NewValue, Is.EqualTo("x"));
        }
    }
}
