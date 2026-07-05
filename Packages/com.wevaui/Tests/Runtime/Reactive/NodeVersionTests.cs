using NUnit.Framework;
using Weva.Dom;

namespace Weva.Tests.Reactive {
    public class NodeVersionTests {
        [Test]
        public void New_element_starts_at_version_zero() {
            var e = new Element("div");
            Assert.That(e.Version, Is.EqualTo(0));
        }

        [Test]
        public void New_text_node_starts_at_version_zero() {
            var t = new TextNode("x");
            Assert.That(t.Version, Is.EqualTo(0));
        }

        [Test]
        public void New_document_starts_at_version_zero() {
            var d = new Document();
            Assert.That(d.Version, Is.EqualTo(0));
        }

        [Test]
        public void AppendChild_increments_parent_version() {
            var parent = new Element("div");
            var v0 = parent.Version;
            parent.AppendChild(new Element("span"));
            Assert.That(parent.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void AppendChild_increments_child_version() {
            var parent = new Element("div");
            var child = new Element("span");
            var c0 = child.Version;
            parent.AppendChild(child);
            Assert.That(child.Version, Is.GreaterThan(c0));
        }

        [Test]
        public void RemoveChild_increments_parent_version() {
            var parent = new Element("div");
            var child = new Element("span");
            parent.AppendChild(child);
            var v0 = parent.Version;
            parent.RemoveChild(child);
            Assert.That(parent.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void RemoveChild_increments_child_version() {
            var parent = new Element("div");
            var child = new Element("span");
            parent.AppendChild(child);
            var c0 = child.Version;
            parent.RemoveChild(child);
            Assert.That(child.Version, Is.GreaterThan(c0));
        }

        [Test]
        public void RemoveChild_for_non_child_does_not_bump_version() {
            var parent = new Element("div");
            var orphan = new Element("span");
            var v0 = parent.Version;
            parent.RemoveChild(orphan);
            Assert.That(parent.Version, Is.EqualTo(v0));
        }

        [Test]
        public void SetAttribute_add_increments_element_version() {
            var e = new Element("div");
            var v0 = e.Version;
            e.SetAttribute("id", "a");
            Assert.That(e.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void SetAttribute_change_increments_element_version() {
            var e = new Element("div");
            e.SetAttribute("id", "a");
            var v0 = e.Version;
            e.SetAttribute("id", "b");
            Assert.That(e.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void SetAttribute_same_value_does_not_bump_version() {
            var e = new Element("div");
            e.SetAttribute("id", "a");
            var v0 = e.Version;
            e.SetAttribute("id", "a");
            Assert.That(e.Version, Is.EqualTo(v0));
        }

        [Test]
        public void RemoveAttribute_existing_increments_version() {
            var e = new Element("div");
            e.SetAttribute("id", "a");
            var v0 = e.Version;
            e.RemoveAttribute("id");
            Assert.That(e.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void RemoveAttribute_missing_does_not_bump_version() {
            var e = new Element("div");
            var v0 = e.Version;
            e.RemoveAttribute("missing");
            Assert.That(e.Version, Is.EqualTo(v0));
        }

        [Test]
        public void TextNode_data_change_increments_version() {
            var t = new TextNode("hello");
            var v0 = t.Version;
            t.Data = "world";
            Assert.That(t.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void TextNode_data_no_op_does_not_bump_version() {
            var t = new TextNode("hello");
            var v0 = t.Version;
            t.Data = "hello";
            Assert.That(t.Version, Is.EqualTo(v0));
        }

        [Test]
        public void Multiple_mutations_strictly_monotonically_increase_version() {
            var e = new Element("div");
            long last = e.Version;
            for (int i = 0; i < 20; i++) {
                e.SetAttribute("data-i", i.ToString());
                Assert.That(e.Version, Is.GreaterThan(last));
                last = e.Version;
            }
        }

        [Test]
        public void Reparenting_bumps_versions_of_old_parent_new_parent_and_child() {
            var oldParent = new Element("div");
            var newParent = new Element("section");
            var child = new Element("span");
            oldParent.AppendChild(child);
            var op0 = oldParent.Version;
            var np0 = newParent.Version;
            var c0 = child.Version;
            newParent.AppendChild(child);
            Assert.That(oldParent.Version, Is.GreaterThan(op0));
            Assert.That(newParent.Version, Is.GreaterThan(np0));
            Assert.That(child.Version, Is.GreaterThan(c0));
        }
    }
}
