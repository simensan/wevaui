using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for aspect-ratio (item icons, thumbnails, 16:9 media): a node's width÷height
    /// ratio compiles to CSS aspect-ratio, defaults off, round-trips, is editable with undo,
    /// and — proven through the real engine — derives the other axis from a fixed one.
    /// </summary>
    public class DesignAspectRatioTests
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
        public void Aspect_ratio_emits_when_set()
        {
            var n = new DesignNode("thumb") { AspectRatio = 16.0 / 9.0 };
            Assert.That(Css(n), Does.Contain("aspect-ratio: 1.7778"));
        }

        [Test]
        public void Square_ratio_emits_one()
        {
            var n = new DesignNode("icon") { AspectRatio = 1 };
            Assert.That(Css(n), Does.Contain("aspect-ratio: 1"));
        }

        [Test]
        public void Unset_emits_nothing()
        {
            Assert.That(Css(new DesignNode("box") { Layout = LayoutMode.Row }), Does.Not.Contain("aspect-ratio"));
        }

        [Test]
        public void Fixed_width_derives_height_through_engine()
        {
            var root = new DesignNode("col") { Layout = LayoutMode.Column };
            root.SetFixedSize(400, 400);
            // 200px wide, 2:1 ratio → 100px tall, derived by the engine.
            var thumb = new DesignNode("thumb") { WidthMode = SizeMode.Fixed, Width = 200, AspectRatio = 2 };
            root.Add(thumb);

            DesignCompileResult r = new DesignDocument(root).Compile();
            var (layoutRoot, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 800, 600);
            Box t = BoxByClass(layoutRoot, "w1");
            Assert.That(t.Width, Is.EqualTo(200).Within(Tol));
            Assert.That(t.Height, Is.EqualTo(100).Within(Tol));
        }

        [Test]
        public void Aspect_ratio_round_trips_through_serializer()
        {
            var root = new DesignNode("thumb") { AspectRatio = 16.0 / 9.0 };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.AspectRatio, Is.EqualTo(16.0 / 9.0).Within(1e-9));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_aspect_ratio_with_undo()
        {
            var root = new DesignNode("thumb");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetAspectRatio(root, 1.5);
            Assert.That(root.AspectRatio, Is.EqualTo(1.5).Within(1e-9));
            ed.Undo();
            Assert.That(root.AspectRatio, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void Clone_copies_aspect_ratio()
        {
            var n = new DesignNode("thumb") { AspectRatio = 2.35 };
            Assert.That(n.Clone().AspectRatio, Is.EqualTo(2.35).Within(1e-9));
        }
    }
}
