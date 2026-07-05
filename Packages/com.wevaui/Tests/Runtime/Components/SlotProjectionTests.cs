using System.Collections.Generic;
using NUnit.Framework;
using Weva.Components;
using Weva.Dom;

namespace Weva.Tests.Components {
    public class SlotProjectionTests {
        // Test helpers build a cloned-template-body shape (a list of root nodes that
        // would have been emitted by TemplateInstantiator.CloneTemplateBody) plus
        // the host's light-dom children (which the caller has detached).

        static List<Node> SingleRoot(Element root) => new List<Node> { root };

        static Element MakeSlot(string name = null) {
            var s = new Element("slot");
            if (name != null) s.SetAttribute("name", name);
            return s;
        }

        [Test]
        public void Single_default_slot_receives_all_light_dom_children() {
            var div = new Element("div");
            div.AppendChild(MakeSlot());
            var p1 = new Element("p");
            var p2 = new Element("p");

            SlotProjection.Project(SingleRoot(div), new List<Node> { p1, p2 });

            Assert.That(div.Children, Has.Count.EqualTo(2));
            Assert.That(div.Children[0], Is.SameAs(p1));
            Assert.That(div.Children[1], Is.SameAs(p2));
        }

        [Test]
        public void Named_slots_receive_matching_children() {
            var root = new Element("div");
            var header = new Element("header");
            header.AppendChild(MakeSlot("header"));
            var footer = new Element("footer");
            footer.AppendChild(MakeSlot("footer"));
            root.AppendChild(header);
            root.AppendChild(footer);

            var h = new Element("h1");
            h.SetAttribute("slot", "header");
            var small = new Element("small");
            small.SetAttribute("slot", "footer");

            SlotProjection.Project(SingleRoot(root), new List<Node> { h, small });

            Assert.That(header.Children, Has.Count.EqualTo(1));
            Assert.That(header.Children[0], Is.SameAs(h));
            Assert.That(footer.Children, Has.Count.EqualTo(1));
            Assert.That(footer.Children[0], Is.SameAs(small));
        }

        [Test]
        public void Light_dom_child_without_slot_attribute_goes_to_default_slot() {
            var root = new Element("div");
            var named = new Element("section");
            named.AppendChild(MakeSlot("named"));
            var defslot = new Element("section");
            defslot.AppendChild(MakeSlot());
            root.AppendChild(named);
            root.AppendChild(defslot);

            var p = new Element("p");
            SlotProjection.Project(SingleRoot(root), new List<Node> { p });

            Assert.That(named.Children, Has.Count.EqualTo(0));
            Assert.That(defslot.Children, Has.Count.EqualTo(1));
            Assert.That(defslot.Children[0], Is.SameAs(p));
        }

        [Test]
        public void Multiple_default_children_projected_in_document_order() {
            var div = new Element("div");
            div.AppendChild(MakeSlot());
            var a = new Element("a");
            var b = new Element("b");
            var c = new Element("c");

            SlotProjection.Project(SingleRoot(div), new List<Node> { a, b, c });

            Assert.That(div.Children[0], Is.SameAs(a));
            Assert.That(div.Children[1], Is.SameAs(b));
            Assert.That(div.Children[2], Is.SameAs(c));
        }

        [Test]
        public void Slot_fallback_used_when_no_projected_children() {
            var div = new Element("div");
            var slot = MakeSlot();
            var fb = new Element("em");
            fb.AppendChild(new TextNode("default"));
            slot.AppendChild(fb);
            div.AppendChild(slot);

            SlotProjection.Project(SingleRoot(div), new List<Node>());

            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(div.Children[0], Is.SameAs(fb));
            Assert.That(((TextNode)fb.Children[0]).Data, Is.EqualTo("default"));
        }

        [Test]
        public void Named_slot_with_no_match_uses_fallback() {
            var div = new Element("div");
            var slot = MakeSlot("footer");
            var fb = new Element("small");
            slot.AppendChild(fb);
            div.AppendChild(slot);

            var unrelated = new Element("p");
            SlotProjection.Project(SingleRoot(div), new List<Node> { unrelated });

            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(div.Children[0], Is.SameAs(fb));
        }

        [Test]
        public void Mixed_slot_and_unslotted_children_distributed_correctly() {
            var root = new Element("div");
            var header = new Element("header");
            header.AppendChild(MakeSlot("header"));
            var body = new Element("section");
            body.AppendChild(MakeSlot());
            root.AppendChild(header);
            root.AppendChild(body);

            var h = new Element("h1");
            h.SetAttribute("slot", "header");
            var p = new Element("p");
            var span = new Element("span");

            SlotProjection.Project(SingleRoot(root), new List<Node> { p, h, span });

            Assert.That(header.Children, Has.Count.EqualTo(1));
            Assert.That(header.Children[0], Is.SameAs(h));
            Assert.That(body.Children, Has.Count.EqualTo(2));
            Assert.That(body.Children[0], Is.SameAs(p));
            Assert.That(body.Children[1], Is.SameAs(span));
        }

        [Test]
        public void Empty_slot_with_no_fallback_collapses() {
            var div = new Element("div");
            div.AppendChild(MakeSlot());

            SlotProjection.Project(SingleRoot(div), new List<Node>());

            Assert.That(div.Children, Has.Count.EqualTo(0));
        }

        [Test]
        public void Slot_position_preserved_among_siblings() {
            var div = new Element("div");
            var before = new Element("hr");
            var after = new Element("hr");
            div.AppendChild(before);
            div.AppendChild(MakeSlot());
            div.AppendChild(after);

            var p = new Element("p");
            SlotProjection.Project(SingleRoot(div), new List<Node> { p });

            Assert.That(div.Children, Has.Count.EqualTo(3));
            Assert.That(div.Children[0], Is.SameAs(before));
            Assert.That(div.Children[1], Is.SameAs(p));
            Assert.That(div.Children[2], Is.SameAs(after));
        }
    }
}
