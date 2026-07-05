using System.Collections.Generic;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Validation;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the diagnostics pass (M9): authoring problems are reported instead of
    /// silently mis-rendering — unknown component/variant/token refs, undeclared props,
    /// invalid repeat, unknown events, misplaced slots, component cycles. A clean
    /// document produces no diagnostics.
    /// </summary>
    public class DesignValidatorTests
    {
        static List<DesignDiagnostic> Validate(DesignDocument doc) => DesignValidator.Validate(doc);

        static bool Has(List<DesignDiagnostic> d, string code)
        {
            foreach (var x in d) if (x.Code == code) return true;
            return false;
        }

        [Test]
        public void Clean_document_has_no_diagnostics()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column, Gap = Dim.Token("md"), Fill = "{bg}" };
            root.Add(new DesignNode("label") { Text = "hi", TextColor = "{text}" });
            var doc = new DesignDocument(root);
            doc.Tokens.Color("bg", "#000").Color("text", "#fff").Space("md", 16);

            Assert.That(Validate(doc), Is.Empty);
        }

        [Test]
        public void Unknown_color_token_is_flagged()
        {
            var doc = new DesignDocument(new DesignNode("n") { Fill = "{nope}" });
            Assert.That(Has(Validate(doc), "unknown-color-token"), Is.True);
        }

        [Test]
        public void Unknown_spacing_token_is_flagged()
        {
            var doc = new DesignDocument(new DesignNode("n") { Layout = LayoutMode.Row, Gap = Dim.Token("ghost") });
            Assert.That(Has(Validate(doc), "unknown-spacing-token"), Is.True);
        }

        [Test]
        public void Unknown_radius_and_font_tokens_are_flagged()
        {
            var n = new DesignNode("n") { Radius = Dim.Token("r"), Text = "x", FontSize = Dim.Token("f") };
            var diags = Validate(new DesignDocument(n));
            Assert.That(Has(diags, "unknown-radius-token"), Is.True);
            Assert.That(Has(diags, "unknown-font-token"), Is.True);
        }

        [Test]
        public void Unknown_component_reference_is_an_error()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "Missing" });
            var diags = Validate(new DesignDocument(root));
            Assert.That(Has(diags, "unknown-component"), Is.True);
            Assert.That(DesignValidator.HasErrors(diags), Is.True);
        }

        [Test]
        public void Unknown_variant_and_undeclared_prop_are_warnings()
        {
            var tpl = new DesignNode("Btn") { Fill = "$bg" };
            var comp = new DesignComponent("Btn", tpl).Prop("bg", "#888");
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            var inst = new DesignNode { ComponentRef = "Btn", Variant = "ghost" };
            inst.SetProp("nonexistent", "x");
            root.Add(inst);
            var doc = new DesignDocument(root);
            doc.AddComponent(comp);

            var diags = Validate(doc);
            Assert.That(Has(diags, "unknown-variant"), Is.True);
            Assert.That(Has(diags, "undeclared-prop"), Is.True);
        }

        [Test]
        public void Invalid_repeat_expression_is_flagged()
        {
            var n = new DesignNode("list");
            n.Bind().RepeatEach = "items"; // missing 'as alias'
            Assert.That(Has(Validate(new DesignDocument(n)), "invalid-repeat"), Is.True);
        }

        [Test]
        public void Valid_repeat_expression_is_clean()
        {
            var n = new DesignNode("list");
            n.Bind().RepeatEach = "items as item";
            Assert.That(Has(Validate(new DesignDocument(n)), "invalid-repeat"), Is.False);
        }

        [Test]
        public void Unknown_event_is_flagged()
        {
            var n = new DesignNode("btn");
            n.Bind().BindEvent("hover", "OnHover"); // 'hover' is not an engine event
            Assert.That(Has(Validate(new DesignDocument(n)), "unknown-event"), Is.True);
        }

        [Test]
        public void Slot_outside_component_is_flagged()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode("stray") { IsSlot = true });
            Assert.That(Has(Validate(new DesignDocument(root)), "slot-outside-component"), Is.True);
        }

        [Test]
        public void Slot_inside_component_template_is_clean()
        {
            var tpl = new DesignNode("Card") { Layout = LayoutMode.Column };
            tpl.Add(new DesignNode("slot") { IsSlot = true });
            var doc = new DesignDocument(new DesignNode("root") { Layout = LayoutMode.Column });
            doc.AddComponent(new DesignComponent("Card", tpl));
            Assert.That(Has(Validate(doc), "slot-outside-component"), Is.False);
        }

        [Test]
        public void Multiple_slots_in_component_are_flagged()
        {
            var tpl = new DesignNode("Card") { Layout = LayoutMode.Column };
            tpl.Add(new DesignNode("a") { IsSlot = true });
            tpl.Add(new DesignNode("b") { IsSlot = true });
            var doc = new DesignDocument(new DesignNode("root"));
            doc.AddComponent(new DesignComponent("Card", tpl));
            Assert.That(Has(Validate(doc), "multiple-slots"), Is.True);
        }

        [Test]
        public void Component_cycle_is_flagged()
        {
            var aTpl = new DesignNode("A") { Layout = LayoutMode.Column };
            aTpl.Add(new DesignNode { ComponentRef = "B" });
            var bTpl = new DesignNode("B") { Layout = LayoutMode.Column };
            bTpl.Add(new DesignNode { ComponentRef = "A" });
            var doc = new DesignDocument(new DesignNode("root"));
            doc.AddComponent(new DesignComponent("A", aTpl));
            doc.AddComponent(new DesignComponent("B", bTpl));

            Assert.That(Has(Validate(doc), "component-cycle"), Is.True);
        }

        [Test]
        public void Validator_also_checks_inside_component_templates()
        {
            var tpl = new DesignNode("Btn") { Fill = "{missingToken}" };
            var doc = new DesignDocument(new DesignNode("root"));
            doc.AddComponent(new DesignComponent("Btn", tpl));
            Assert.That(Has(Validate(doc), "unknown-color-token"), Is.True);
        }

        [Test]
        public void Starter_templates_validate_clean()
        {
            foreach (var t in Weva.Designer.Templates.DesignTemplates.Catalog())
            {
                var diags = Validate(t.Create());
                Assert.That(diags, Is.Empty, t.Name + " produced: " + string.Join("; ", diags));
            }
        }
    }
}
