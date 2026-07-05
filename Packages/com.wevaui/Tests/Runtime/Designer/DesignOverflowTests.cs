using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the Overflow box property: a node can clip (overflow:hidden — e.g. a
    /// card cropping its image to rounded corners) or scroll (overflow:auto) its content.
    /// Visible is the default and emits nothing; round-trips and is editable with undo.
    /// </summary>
    public class DesignOverflowTests
    {
        static string Css(DesignNode root)
            => new DesignDocument(root).Compile().Css;

        [Test]
        public void Visible_is_default_and_emits_nothing()
        {
            var n = new DesignNode("box") { Layout = LayoutMode.Column };
            Assert.That(Css(n), Does.Not.Contain("overflow"));
        }

        [Test]
        public void Clip_emits_overflow_hidden()
        {
            var n = new DesignNode("card") { Overflow = Overflow.Clip };
            Assert.That(Css(n), Does.Contain("overflow: hidden"));
        }

        [Test]
        public void Scroll_emits_overflow_auto()
        {
            var n = new DesignNode("list") { Layout = LayoutMode.Column, Overflow = Overflow.Scroll };
            Assert.That(Css(n), Does.Contain("overflow: auto"));
        }

        [Test]
        public void Clip_pairs_with_radius_for_a_cropping_card()
        {
            var n = new DesignNode("card") { Overflow = Overflow.Clip, Radius = 12, Fill = "#fff" };
            string css = Css(n);
            Assert.That(css, Does.Contain("overflow: hidden"));
            Assert.That(css, Does.Contain("border-radius: 12px"));
        }

        [Test]
        public void Overflow_round_trips_through_serializer()
        {
            var root = new DesignNode("card") { Overflow = Overflow.Clip };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Overflow, Is.EqualTo(Overflow.Clip));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_overflow_with_undo()
        {
            var root = new DesignNode("box");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetOverflow(root, Overflow.Scroll);
            Assert.That(root.Overflow, Is.EqualTo(Overflow.Scroll));
            ed.Undo();
            Assert.That(root.Overflow, Is.EqualTo(Overflow.Visible));
        }

        [Test]
        public void Clone_copies_overflow()
        {
            var n = new DesignNode("box") { Overflow = Overflow.Clip };
            Assert.That(n.Clone().Overflow, Is.EqualTo(Overflow.Clip));
        }
    }
}
