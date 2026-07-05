using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for background-image fills (textured panels, item thumbnails): an image URL
    /// compiles to background-image/size/position/repeat, layers over the colour fill, sizes
    /// cover/contain/stretch, escapes the URL safely, round-trips, and is editable with undo.
    /// </summary>
    public class DesignBackgroundImageTests
    {
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;

        [Test]
        public void Image_emits_background_image_size_position_repeat()
        {
            var n = new DesignNode("panel") { BackgroundImage = "ui/panel.png" };
            string css = Css(n);
            Assert.That(css, Does.Contain("background-image: url(\"ui/panel.png\")"));
            Assert.That(css, Does.Contain("background-size: cover"));      // default
            Assert.That(css, Does.Contain("background-position: center"));
            Assert.That(css, Does.Contain("background-repeat: no-repeat"));
        }

        [Test]
        public void No_image_emits_no_background_image()
        {
            Assert.That(Css(new DesignNode("box") { Fill = "#fff" }), Does.Not.Contain("background-image"));
        }

        [Test]
        public void Contain_and_stretch_sizes()
        {
            var c = new DesignNode("a") { BackgroundImage = "x.png", BackgroundSize = BackgroundSize.Contain };
            var s = new DesignNode("b") { BackgroundImage = "x.png", BackgroundSize = BackgroundSize.Stretch };
            Assert.That(Css(c), Does.Contain("background-size: contain"));
            Assert.That(Css(s), Does.Contain("background-size: 100% 100%"));
        }

        [Test]
        public void Color_fill_and_image_coexist_image_layered_after()
        {
            var n = new DesignNode("panel") { Fill = "#222", BackgroundImage = "p.png" };
            string css = Css(n);
            // Fill shorthand first, then background-image overrides the shorthand's reset.
            int bg = css.IndexOf("background: #222", System.StringComparison.Ordinal);
            int img = css.IndexOf("background-image:", System.StringComparison.Ordinal);
            Assert.That(bg, Is.GreaterThanOrEqualTo(0));
            Assert.That(img, Is.GreaterThan(bg));
        }

        [Test]
        public void Url_is_escaped_against_breakout()
        {
            var n = new DesignNode("p") { BackgroundImage = "a\")evil.png" };
            string css = Css(n);
            Assert.That(css, Does.Contain("url(\"a\\\")evil.png\")"));
        }

        [Test]
        public void Background_image_round_trips_through_serializer()
        {
            var root = new DesignNode("panel") { BackgroundImage = "ui/p.png", BackgroundSize = BackgroundSize.Contain };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.BackgroundImage, Is.EqualTo("ui/p.png"));
            Assert.That(reloaded.Root.BackgroundSize, Is.EqualTo(BackgroundSize.Contain));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_background_image_with_undo()
        {
            var root = new DesignNode("panel");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetBackgroundImage(root, "p.png");
            ed.SetBackgroundSize(root, BackgroundSize.Contain);
            Assert.That(root.BackgroundImage, Is.EqualTo("p.png"));
            Assert.That(root.BackgroundSize, Is.EqualTo(BackgroundSize.Contain));
            ed.Undo();
            Assert.That(root.BackgroundSize, Is.EqualTo(BackgroundSize.Cover));
            ed.Undo();
            Assert.That(root.BackgroundImage, Is.Null);
        }

        [Test]
        public void Clone_copies_background_image()
        {
            var n = new DesignNode("p") { BackgroundImage = "p.png", BackgroundSize = BackgroundSize.Stretch };
            DesignNode c = n.Clone();
            Assert.That(c.BackgroundImage, Is.EqualTo("p.png"));
            Assert.That(c.BackgroundSize, Is.EqualTo(BackgroundSize.Stretch));
        }
    }
}
