using System.Collections.Generic;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for components &amp; variants (M5): instance expansion substitutes props
    /// (defaults ⊕ variant ⊕ instance), fills slots with instance children, applies
    /// instance sizing, updates all instances when the component changes, round-trips
    /// through the serializer, and is editable with undo.
    /// </summary>
    public class DesignComponentTests
    {
        // A Button component: label text from $label, background from $bg (default grey),
        // with a "primary" variant. No slot.
        static DesignComponent Button()
        {
            var tpl = new DesignNode("Button")
            {
                Layout = LayoutMode.Row, MainAlign = MainAlign.Center, CrossAlign = CrossAlign.Center,
                Fill = "$bg", Radius = 8,
            };
            tpl.SetPadding(12);
            tpl.Add(new DesignNode("Label") { Text = "$label", TextColor = "#fff", FontSize = 16 });

            var comp = new DesignComponent("Button", tpl);
            comp.Prop("label", "Button").Prop("bg", "#888");
            comp.Variant("primary", new Dictionary<string, string> { { "bg", "#5b8cff" } });
            return comp;
        }

        // A Card component with a slot for arbitrary content.
        static DesignComponent Card()
        {
            var tpl = new DesignNode("Card") { Layout = LayoutMode.Column, Fill = "#222", Radius = 12 };
            tpl.SetPadding(16);
            var slot = new DesignNode("Slot") { Layout = LayoutMode.Column, IsSlot = true };
            tpl.Add(slot);
            return new DesignComponent("Card", tpl);
        }

        static DesignDocument Doc(DesignNode root, params DesignComponent[] comps)
        {
            var doc = new DesignDocument(root);
            foreach (var c in comps) doc.AddComponent(c);
            return doc;
        }

        // --- Expansion ---

        [Test]
        public void Instance_expands_with_default_props()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Button" });
            string html = Doc(root, Button()).Compile().Html;
            Assert.That(html, Does.Contain("Button")); // default $label
        }

        [Test]
        public void Instance_prop_override_substitutes()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Button" };
            inst.SetProp("label", "Play");
            root.Add(inst);
            var r = Doc(root, Button()).Compile();
            Assert.That(r.Html, Does.Contain("Play"));
            Assert.That(r.Css, Does.Contain("background: #888")); // default bg
        }

        [Test]
        public void Variant_supplies_prop_values()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Button", Variant = "primary" };
            root.Add(inst);
            Assert.That(Doc(root, Button()).Compile().Css, Does.Contain("background: #5b8cff"));
        }

        [Test]
        public void Instance_prop_overrides_variant()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Button", Variant = "primary" };
            inst.SetProp("bg", "#00ff00");
            root.Add(inst);
            Assert.That(Doc(root, Button()).Compile().Css, Does.Contain("background: #00ff00"));
        }

        [Test]
        public void Slot_receives_instance_children()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Card" };
            inst.Add(new DesignNode("Inner") { Text = "slotted!" });
            root.Add(inst);
            Assert.That(Doc(root, Card()).Compile().Html, Does.Contain("slotted!"));
        }

        [Test]
        public void Editing_the_component_updates_all_instances()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Button" });
            root.Add(new DesignNode { ComponentRef = "Button" });
            DesignComponent btn = Button();
            var doc = Doc(root, btn);

            // Change the component's radius → both instances reflect it.
            btn.Template.Radius = 20;
            string css = doc.Compile().Css;
            int matches = System.Text.RegularExpressions.Regex.Matches(css, "border-radius: 20px").Count;
            Assert.That(matches, Is.EqualTo(2));
        }

        [Test]
        public void Instance_sizing_overrides_template()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Row };
            var inst = new DesignNode { ComponentRef = "Button" };
            inst.SetSize(SizeMode.Fill, SizeMode.Hug);
            root.Add(inst);
            Assert.That(Doc(root, Button()).Compile().Css, Does.Contain("flex-grow: 1"));
        }

        [Test]
        public void Unknown_component_ref_compiles_as_plain_element()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            root.Add(new DesignNode("ghost") { ComponentRef = "DoesNotExist" });
            Assert.That(Doc(root).Compile().Html, Does.Contain("data-name=\"ghost\""));
        }

        // --- Serializer ---

        [Test]
        public void Components_and_instances_round_trip()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Button", Variant = "primary" };
            inst.SetProp("label", "Go");
            root.Add(inst);
            var doc = Doc(root, Button());

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Components.ContainsKey("Button"), Is.True);
            Assert.That(reloaded.Components["Button"].Props["bg"], Is.EqualTo("#888"));
            Assert.That(reloaded.Components["Button"].Variants["primary"]["bg"], Is.EqualTo("#5b8cff"));
            Assert.That(reloaded.Root.Children[0].ComponentRef, Is.EqualTo("Button"));
            Assert.That(reloaded.Root.Children[0].Props["label"], Is.EqualTo("Go"));

            Assert.That(reloaded.Compile().Html, Is.EqualTo(doc.Compile().Html));
        }

        // --- Editor ---

        [Test]
        public void Editor_adds_instance_sets_variant_and_prop_with_undo()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column };
            var ed = new DocumentEditor(Doc(root, Button()));

            DesignNode inst = ed.AddInstance(root, "Button");
            ed.SetVariant(inst, "primary");
            ed.SetInstanceProp(inst, "label", "Start");

            Assert.That(inst.ComponentRef, Is.EqualTo("Button"));
            Assert.That(inst.Variant, Is.EqualTo("primary"));
            Assert.That(inst.Props["label"], Is.EqualTo("Start"));

            ed.Undo(); // undo prop
            Assert.That(inst.Props == null || !inst.Props.ContainsKey("label"), Is.True);
            ed.Undo(); // undo variant
            Assert.That(inst.Variant, Is.Null);
            ed.Undo(); // undo add
            Assert.That(root.Children, Has.Count.EqualTo(0));
        }
    }
}
