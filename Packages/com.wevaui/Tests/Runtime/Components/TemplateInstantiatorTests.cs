using NUnit.Framework;
using Weva.Components;
using Weva.Dom;

namespace Weva.Tests.Components {
    public class TemplateInstantiatorTests {
        [Test]
        public void Clone_returns_different_reference_with_equal_attributes_and_tag() {
            var src = new Element("div");
            src.SetAttribute("class", "card");
            src.SetAttribute("id", "x");

            var copy = (Element)TemplateInstantiator.Clone(src);
            Assert.That(copy, Is.Not.SameAs(src));
            Assert.That(copy.TagName, Is.EqualTo("div"));
            Assert.That(copy.GetAttribute("class"), Is.EqualTo("card"));
            Assert.That(copy.GetAttribute("id"), Is.EqualTo("x"));
        }

        [Test]
        public void Clone_recurses_into_descendants() {
            var src = new Element("section");
            var inner = new Element("p");
            inner.AppendChild(new TextNode("hello"));
            src.AppendChild(inner);

            var copy = (Element)TemplateInstantiator.Clone(src);
            Assert.That(copy.Children, Has.Count.EqualTo(1));
            var copyP = (Element)copy.Children[0];
            Assert.That(copyP, Is.Not.SameAs(inner));
            Assert.That(copyP.TagName, Is.EqualTo("p"));
            Assert.That(((TextNode)copyP.Children[0]).Data, Is.EqualTo("hello"));
        }

        [Test]
        public void Clone_preserves_text_node_data() {
            var src = new TextNode("body text");
            var copy = (TextNode)TemplateInstantiator.Clone(src);
            Assert.That(copy, Is.Not.SameAs(src));
            Assert.That(copy.Data, Is.EqualTo("body text"));
        }

        [Test]
        public void Cloned_attribute_list_is_independent() {
            var src = new Element("div");
            src.SetAttribute("foo", "1");
            var copy = (Element)TemplateInstantiator.Clone(src);

            copy.SetAttribute("foo", "2");
            copy.SetAttribute("bar", "added");

            Assert.That(src.GetAttribute("foo"), Is.EqualTo("1"));
            Assert.That(src.HasAttribute("bar"), Is.False);
            Assert.That(copy.GetAttribute("foo"), Is.EqualTo("2"));
            Assert.That(copy.GetAttribute("bar"), Is.EqualTo("added"));
        }

        [Test]
        public void Clone_of_element_with_no_children_returns_single_element() {
            var src = new Element("br");
            var copy = (Element)TemplateInstantiator.Clone(src);
            Assert.That(copy.Children, Has.Count.EqualTo(0));
            Assert.That(copy.TagName, Is.EqualTo("br"));
        }

        [Test]
        public void CloneTemplateBody_clones_each_direct_child() {
            var template = new Element("template");
            template.AppendChild(new Element("div"));
            template.AppendChild(new TextNode("between"));
            template.AppendChild(new Element("span"));

            var clones = TemplateInstantiator.CloneTemplateBody(template);
            Assert.That(clones, Has.Count.EqualTo(3));
            Assert.That(((Element)clones[0]).TagName, Is.EqualTo("div"));
            Assert.That(((TextNode)clones[1]).Data, Is.EqualTo("between"));
            Assert.That(((Element)clones[2]).TagName, Is.EqualTo("span"));
        }

        [Test]
        public void CloneTemplateBody_does_not_share_references() {
            var template = new Element("template");
            var first = new Element("div");
            template.AppendChild(first);

            var clones = TemplateInstantiator.CloneTemplateBody(template);
            Assert.That(clones[0], Is.Not.SameAs(first));
        }

        [Test]
        public void Clone_descendant_attribute_lists_are_independent() {
            var src = new Element("section");
            var inner = new Element("p");
            inner.SetAttribute("data-v", "a");
            src.AppendChild(inner);

            var copy = (Element)TemplateInstantiator.Clone(src);
            var copyP = (Element)copy.Children[0];
            copyP.SetAttribute("data-v", "b");
            Assert.That(inner.GetAttribute("data-v"), Is.EqualTo("a"));
            Assert.That(copyP.GetAttribute("data-v"), Is.EqualTo("b"));
        }
    }
}
