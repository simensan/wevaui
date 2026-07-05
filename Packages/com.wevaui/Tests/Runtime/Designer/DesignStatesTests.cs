using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for interactive states (M6): per-state overrides compile to the right
    /// pseudo-class / state-class rules, leave the base rule intact, round-trip through
    /// the serializer, and are editable with undo.
    /// </summary>
    public class DesignStatesTests
    {
        static string Css(DesignNode root, System.Action<DesignTokens> tokens = null)
        {
            var doc = new DesignDocument(root);
            tokens?.Invoke(doc.Tokens);
            return doc.Compile().Css;
        }

        // --- Pseudo-class mapping ---

        [Test]
        public void Hover_override_emits_hover_rule()
        {
            var n = new DesignNode("btn") { Fill = "#333" };
            n.State(InteractionState.Hover).Fill = "#555";
            string css = Css(n);
            Assert.That(css, Does.Contain(".w0 {"));            // base preserved
            Assert.That(css, Does.Contain("background: #333"));
            Assert.That(css, Does.Contain(".w0:hover {"));
            Assert.That(css, Does.Contain("background: #555"));
        }

        [Test]
        public void Pressed_maps_to_active()
        {
            var n = new DesignNode("btn");
            n.State(InteractionState.Pressed).Opacity = 0.6;
            Assert.That(Css(n), Does.Contain(".w0:active {"));
        }

        [Test]
        public void Focus_maps_to_focus()
        {
            var n = new DesignNode("btn");
            n.State(InteractionState.Focus).Shadow = "0 0 0 2px blue";
            string css = Css(n);
            Assert.That(css, Does.Contain(".w0:focus {"));
            Assert.That(css, Does.Contain("box-shadow: 0 0 0 2px blue"));
        }

        [Test]
        public void Disabled_maps_to_state_class()
        {
            var n = new DesignNode("btn");
            n.State(InteractionState.Disabled).Opacity = 0.4;
            string css = Css(n);
            Assert.That(css, Does.Contain(".w0.is-disabled {"));
            Assert.That(css, Does.Contain("opacity: 0.4"));
        }

        [Test]
        public void State_overrides_resolve_tokens()
        {
            var n = new DesignNode("btn");
            n.State(InteractionState.Hover).Fill = "{primary}";
            n.State(InteractionState.Hover).Radius = Dim.Token("card");
            string css = Css(n, t => t.Color("primary", "#5b8cff").Radius("card", 8));
            Assert.That(css, Does.Contain(".w0:hover {"));
            Assert.That(css, Does.Contain("background: var(--color-primary)"));
            Assert.That(css, Does.Contain("border-radius: var(--radius-card)"));
        }

        [Test]
        public void Empty_state_emits_nothing()
        {
            var n = new DesignNode("btn") { Fill = "#333" };
            n.State(InteractionState.Hover); // created but no overrides
            string css = Css(n);
            Assert.That(css, Does.Not.Contain(":hover"));
        }

        [Test]
        public void Multiple_states_emit_in_stable_order()
        {
            var n = new DesignNode("btn");
            n.State(InteractionState.Disabled).Opacity = 0.4;
            n.State(InteractionState.Hover).Fill = "#555";
            n.State(InteractionState.Pressed).Opacity = 0.8;
            string css = Css(n);
            int hover = css.IndexOf(":hover", System.StringComparison.Ordinal);
            int active = css.IndexOf(":active", System.StringComparison.Ordinal);
            int disabled = css.IndexOf(".is-disabled", System.StringComparison.Ordinal);
            // Order: Hover, Pressed, Disabled (Focus absent) — matches AllStates order.
            Assert.That(hover, Is.LessThan(active));
            Assert.That(active, Is.LessThan(disabled));
        }

        // --- Serializer ---

        [Test]
        public void States_round_trip_through_serializer()
        {
            var root = new DesignNode("btn") { Fill = "#333" };
            root.State(InteractionState.Hover).Fill = "{primary}";
            root.State(InteractionState.Hover).Opacity = 0.9;
            root.State(InteractionState.Disabled).Opacity = 0.4;
            var doc = new DesignDocument(root);
            doc.Tokens.Color("primary", "#5b8cff");

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            StateStyle hover = reloaded.Root.GetState(InteractionState.Hover);
            Assert.That(hover, Is.Not.Null);
            Assert.That(hover.Fill, Is.EqualTo("{primary}"));
            Assert.That(hover.Opacity, Is.EqualTo(0.9).Within(1e-9));
            Assert.That(reloaded.Root.GetState(InteractionState.Disabled).Opacity, Is.EqualTo(0.4).Within(1e-9));

            // Compiled output identical after reload.
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Editor ---

        [Test]
        public void Editor_sets_state_fill_with_undo()
        {
            var root = new DesignNode("btn");
            var ed = new DocumentEditor(new DesignDocument(root));

            ed.SetStateFill(root, InteractionState.Hover, "#555");
            Assert.That(root.GetState(InteractionState.Hover).Fill, Is.EqualTo("#555"));

            ed.Undo();
            Assert.That(root.GetState(InteractionState.Hover), Is.Null); // state removed on undo
        }

        [Test]
        public void Editor_clears_state_with_undo()
        {
            var root = new DesignNode("btn");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetStateOpacity(root, InteractionState.Disabled, 0.4);

            ed.ClearState(root, InteractionState.Disabled);
            Assert.That(root.GetState(InteractionState.Disabled), Is.Null);

            ed.Undo();
            Assert.That(root.GetState(InteractionState.Disabled).Opacity, Is.EqualTo(0.4).Within(1e-9));
        }
    }
}
