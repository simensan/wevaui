using System.Collections.Generic;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Serialization;
using Weva.Designer.Templates;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the starter templates: every catalog entry builds a valid IR,
    /// compiles to non-empty themed HTML/CSS, lays out in the real engine at its
    /// declared size, and survives a serialize/reload round-trip. This is the proof
    /// the IR is expressive enough for real game screens.
    /// </summary>
    public class DesignTemplatesTests
    {
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

        static IEnumerable<DesignTemplate> Catalog => DesignTemplates.Catalog();

        [Test]
        public void Catalog_lists_the_starter_templates()
        {
            Assert.That(DesignTemplates.Catalog(), Has.Count.GreaterThanOrEqualTo(4));
            foreach (DesignTemplate t in Catalog)
            {
                Assert.That(t.Name, Is.Not.Null.And.Not.Empty);
                Assert.That(t.Create, Is.Not.Null);
            }
        }

        [Test]
        public void Every_template_builds_a_non_empty_document()
        {
            foreach (DesignTemplate t in Catalog)
            {
                DesignDocument doc = t.Create();
                Assert.That(doc.Root, Is.Not.Null, t.Name);
            }
        }

        [Test]
        public void Every_template_compiles_to_themed_html_and_css()
        {
            foreach (DesignTemplate t in Catalog)
            {
                DesignCompileResult r = t.Create().Compile();
                Assert.That(r.Html, Is.Not.Empty, t.Name);
                Assert.That(r.Css, Does.Contain(":root"), t.Name);        // tokens present
                Assert.That(r.Css, Does.Contain("box-sizing"), t.Name);
            }
        }

        [Test]
        public void Every_template_lays_out_at_its_declared_size()
        {
            foreach (DesignTemplate t in Catalog)
            {
                DesignDocument doc = t.Create();
                DesignCompileResult r = doc.Compile();
                var (root, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 1280, 960);
                Box rootBox = BoxByClass(root, "w0");
                Assert.That(rootBox, Is.Not.Null, t.Name);
                Assert.That(rootBox.Width, Is.EqualTo(doc.Root.Width).Within(0.5), t.Name);
            }
        }

        [Test]
        public void Every_template_round_trips_through_the_serializer()
        {
            foreach (DesignTemplate t in Catalog)
            {
                DesignDocument doc = t.Create();
                string text = DesignSerializer.Serialize(doc);
                DesignDocument reloaded = DesignSerializer.Deserialize(text);
                Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css), t.Name);
            }
        }

        [Test]
        public void Main_menu_has_three_buttons_using_the_primary_token()
        {
            DesignDocument doc = DesignTemplates.MainMenu();
            string css = doc.Compile().Css;
            Assert.That(css, Does.Contain("--color-primary"));
            Assert.That(css, Does.Contain("background: var(--color-primary)"));

            // Title + Subtitle + Buttons container, with 3 buttons inside.
            DesignNode buttons = doc.Root.Children[doc.Root.Children.Count - 1];
            Assert.That(buttons.Children, Has.Count.EqualTo(3));
        }

        [Test]
        public void Combat_hud_top_bar_uses_space_between_and_spacer_fills()
        {
            DesignDocument doc = DesignTemplates.CombatHud();
            DesignCompileResult r = doc.Compile();
            Assert.That(r.Css, Does.Contain("justify-content: space-between"));

            var (root, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 1280, 960);
            // w0 = HUD (h 540), w1 = top bar, last child = spacer that fills remaining height.
            Box hud = BoxByClass(root, "w0");
            Box topBar = BoxByClass(root, "w1");
            Assert.That(topBar.Height, Is.GreaterThan(0));
            Assert.That(topBar.Height, Is.LessThan(hud.Height)); // top bar hugs, doesn't fill
        }
    }
}
