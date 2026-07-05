using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for typography overrides in interactive states — the iconic case being a
    /// link that underlines on hover, plus bold-on-hover. They compile into the state's
    /// pseudo-class rule, round-trip, and are editable with undo.
    /// </summary>
    public class DesignStateTypographyTests
    {
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;

        [Test]
        public void Underline_on_hover_emits_in_hover_rule()
        {
            var link = new DesignNode("link") { Text = "Home", TextColor = "#5b8cff" };
            link.State(InteractionState.Hover).TextDecoration = TextDecoration.Underline;
            string css = Css(link);
            Assert.That(css, Does.Contain(".w0:hover {"));
            Assert.That(css, Does.Contain("text-decoration: underline"));
        }

        [Test]
        public void Bold_on_hover_emits_font_weight()
        {
            var n = new DesignNode("link") { Text = "Home" };
            n.State(InteractionState.Hover).FontWeight = FontWeight.Bold;
            Assert.That(Css(n), Does.Contain("font-weight: 700"));
        }

        [Test]
        public void State_can_turn_decoration_off()
        {
            // Base underlined, hover removes it (None is a meaningful override → "none").
            var n = new DesignNode("link") { Text = "Home", TextDecoration = TextDecoration.Underline };
            n.State(InteractionState.Hover).TextDecoration = TextDecoration.None;
            string css = Css(n);
            Assert.That(css, Does.Contain(".w0:hover {"));
            Assert.That(css, Does.Contain("text-decoration: none"));
        }

        [Test]
        public void State_typography_round_trips()
        {
            var root = new DesignNode("link") { Text = "Home" };
            root.State(InteractionState.Hover).TextDecoration = TextDecoration.Underline;
            root.State(InteractionState.Hover).FontWeight = FontWeight.SemiBold;
            var doc = new DesignDocument(root);

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            StateStyle hover = reloaded.Root.GetState(InteractionState.Hover);
            Assert.That(hover.TextDecoration, Is.EqualTo(TextDecoration.Underline));
            Assert.That(hover.FontWeight, Is.EqualTo(FontWeight.SemiBold));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_state_typography_with_undo()
        {
            var root = new DesignNode("link") { Text = "Home" };
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetStateTextDecoration(root, InteractionState.Hover, TextDecoration.Underline);
            Assert.That(root.GetState(InteractionState.Hover).TextDecoration, Is.EqualTo(TextDecoration.Underline));
            ed.Undo();
            Assert.That(root.GetState(InteractionState.Hover), Is.Null);
        }
    }
}
