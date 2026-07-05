using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for flex-wrap: a Row/Column whose children overflow can wrap onto new lines
    /// (tag lists, button groups). Emits flex-wrap:wrap, defaults off, round-trips, is
    /// editable with undo, and — proven through the real engine — actually wraps.
    /// </summary>
    public class DesignWrapTests
    {
        const double Tol = 0.5;
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;

        static Box BoxByClass(Box b, string cls)
        {
            if (b.Element != null)
                foreach (string c in b.Element.ClassList)
                    if (c == cls) return b;
            foreach (Box child in b.Children)
            {
                Box found = BoxByClass(child, cls);
                if (found != null) return found;
            }
            return null;
        }

        [Test]
        public void Wrap_emits_flex_wrap()
        {
            var n = new DesignNode("row") { Layout = LayoutMode.Row, Wrap = true };
            Assert.That(Css(n), Does.Contain("flex-wrap: wrap"));
        }

        [Test]
        public void No_wrap_emits_nothing()
        {
            var n = new DesignNode("row") { Layout = LayoutMode.Row };
            Assert.That(Css(n), Does.Not.Contain("flex-wrap"));
        }

        [Test]
        public void Wrap_only_meaningful_on_flex_containers()
        {
            // A non-flow node with Wrap set emits no flex-wrap (no flex container).
            var n = new DesignNode("box") { Wrap = true };
            Assert.That(Css(n), Does.Not.Contain("flex-wrap"));
        }

        [Test]
        public void Children_wrap_to_second_line_through_engine()
        {
            var row = new DesignNode("row") { Layout = LayoutMode.Row, Wrap = true };
            row.SetFixedSize(200, 200);
            for (int i = 0; i < 3; i++) // 3 * 80 = 240 > 200 → third wraps
                row.Add(new DesignNode("c" + i) { WidthMode = SizeMode.Fixed, Width = 80, HeightMode = SizeMode.Fixed, Height = 40 });

            DesignCompileResult r = new DesignDocument(row).Compile();
            var (layoutRoot, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 800, 600);
            var (_, y1) = LayoutTestHelpers.AbsoluteOrigin(BoxByClass(layoutRoot, "w1"));
            var (_, y2) = LayoutTestHelpers.AbsoluteOrigin(BoxByClass(layoutRoot, "w2"));
            var (_, y3) = LayoutTestHelpers.AbsoluteOrigin(BoxByClass(layoutRoot, "w3"));
            Assert.That(y2, Is.EqualTo(y1).Within(Tol)); // first two share a line
            Assert.That(y3, Is.GreaterThan(y1 + 1));      // third wrapped below
        }

        [Test]
        public void Wrap_round_trips_through_serializer()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row, Wrap = true };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Wrap, Is.True);
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_wrap_with_undo()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetWrap(root, true);
            Assert.That(root.Wrap, Is.True);
            ed.Undo();
            Assert.That(root.Wrap, Is.False);
        }

        [Test]
        public void Clone_copies_wrap()
        {
            var n = new DesignNode("row") { Layout = LayoutMode.Row, Wrap = true };
            Assert.That(n.Clone().Wrap, Is.True);
        }
    }
}
