using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Templates;
using Weva.Designer.Validation;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the base component kit: every component installs, instances compile
    /// to themed output, the document validates clean (all token refs resolve), and
    /// variants / slots behave.
    /// </summary>
    public class DesignComponentKitTests
    {
        static DesignDocument WithKit(DesignNode root)
        {
            var doc = new DesignDocument(root);
            DesignComponentKit.Install(doc);
            return doc;
        }

        [Test]
        public void Kit_lists_components_and_installs_theme()
        {
            Assert.That(DesignComponentKit.All(), Has.Count.GreaterThanOrEqualTo(6));
            var doc = new DesignDocument(new DesignNode("r"));
            DesignComponentKit.Install(doc);
            Assert.That(doc.Tokens.Colors.ContainsKey("primary"), Is.True);
            Assert.That(doc.Tokens.Spacing.ContainsKey("md"), Is.True);
            Assert.That(doc.Components.ContainsKey("Button"), Is.True);
        }

        [Test]
        public void Every_kit_component_instance_compiles_and_validates_clean()
        {
            foreach (var comp in DesignComponentKit.All())
            {
                var root = new DesignNode("root") { Layout = LayoutMode.Column };
                root.Add(new DesignNode { ComponentRef = comp.Name });
                var doc = WithKit(root);

                Assert.That(doc.Compile().Html, Is.Not.Empty, comp.Name);
                var diags = DesignValidator.Validate(doc);
                Assert.That(diags, Is.Empty, comp.Name + ": " + string.Join("; ", diags));
            }
        }

        [Test]
        public void Button_variants_produce_distinct_backgrounds()
        {
            DesignDocument Make(string variant)
            {
                var root = new DesignNode("root") { Layout = LayoutMode.Column };
                root.Add(new DesignNode { ComponentRef = "Button", Variant = variant });
                return WithKit(root);
            }
            Assert.That(Make("primary").Compile().Css, Does.Contain("var(--color-primary)"));
            Assert.That(Make("secondary").Compile().Css, Does.Contain("var(--color-surface)"));
            Assert.That(Make("ghost").Compile().Css, Does.Contain("background: transparent"));
        }

        [Test]
        public void Button_label_prop_substitutes()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Button" };
            inst.SetProp("label", "Continue");
            root.Add(inst);
            Assert.That(WithKit(root).Compile().Html, Does.Contain("Continue"));
        }

        [Test]
        public void Button_has_hover_state()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Button" });
            Assert.That(WithKit(root).Compile().Css, Does.Contain(":hover"));
        }

        [Test]
        public void Button_feels_clickable_pointer_transition_and_semibold_label()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Button" });
            string css = WithKit(root).Compile().Css;
            Assert.That(css, Does.Contain("cursor: pointer"));
            Assert.That(css, Does.Contain("transition: all 120ms ease"));
            Assert.That(css, Does.Contain("font-weight: 600")); // semibold label
        }

        [Test]
        public void ListItem_is_interactive_pointer_and_smooth_hover()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "ListItem" });
            string css = WithKit(root).Compile().Css;
            Assert.That(css, Does.Contain("cursor: pointer"));
            Assert.That(css, Does.Contain("transition: all 120ms ease"));
            Assert.That(css, Does.Contain(":hover"));
        }

        [Test]
        public void Card_clips_content_to_rounded_corners()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Card" });
            string css = WithKit(root).Compile().Css;
            Assert.That(css, Does.Contain("overflow: hidden"));
            Assert.That(css, Does.Contain("var(--radius-lg)"));
        }

        [Test]
        public void Card_slot_receives_instance_children()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            var card = new DesignNode { ComponentRef = "Card" };
            card.Add(new DesignNode("inner") { Text = "card body" });
            root.Add(card);
            Assert.That(WithKit(root).Compile().Html, Does.Contain("card body"));
        }

        [Test]
        public void Kit_components_use_tokens_not_raw_geometry()
        {
            // A Button instance should reference spacing/radius vars (token-driven).
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Button" });
            string css = WithKit(root).Compile().Css;
            Assert.That(css, Does.Contain("var(--radius-md)"));
            Assert.That(css, Does.Contain("var(--space-"));
        }
    }
}
