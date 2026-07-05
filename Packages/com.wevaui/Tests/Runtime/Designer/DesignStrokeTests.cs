using System.Collections.Generic;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Designer.Validation;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the border/stroke style property (M4 Style panel): a node's stroke
    /// color + width compiles to a <c>border</c> shorthand, resolves color tokens, defaults
    /// the weight to 1px (Figma parity), overrides per interactive state, round-trips
    /// through the serializer, is validated for unknown token refs, and is editable with
    /// undo. Pure/headless.
    /// </summary>
    public class DesignStrokeTests
    {
        static string Css(DesignNode root, System.Action<DesignTokens> tokens = null)
        {
            var doc = new DesignDocument(root);
            tokens?.Invoke(doc.Tokens);
            return doc.Compile().Css;
        }

        // --- Base compilation ---

        [Test]
        public void Stroke_color_emits_border_with_default_1px_weight()
        {
            var n = new DesignNode("box") { Stroke = "#222" };
            string css = Css(n);
            Assert.That(css, Does.Contain("border: 1px solid #222"));
        }

        [Test]
        public void Stroke_width_sets_border_weight()
        {
            var n = new DesignNode("box") { Stroke = "#222", StrokeWidth = 3 };
            Assert.That(Css(n), Does.Contain("border: 3px solid #222"));
        }

        [Test]
        public void Stroke_color_resolves_color_token()
        {
            var n = new DesignNode("box") { Stroke = "{line}", StrokeWidth = 2 };
            string css = Css(n, t => t.Color("line", "#3a3a3a"));
            Assert.That(css, Does.Contain("border: 2px solid var(--color-line)"));
        }

        [Test]
        public void No_stroke_emits_no_border()
        {
            var n = new DesignNode("box") { Fill = "#fff" };
            Assert.That(Css(n), Does.Not.Contain("border:"));
        }

        [Test]
        public void Stroke_width_without_color_emits_no_border()
        {
            // A weight with no colour is incomplete — the designer must pick a colour.
            var n = new DesignNode("box") { StrokeWidth = 4 };
            Assert.That(Css(n), Does.Not.Contain("border:"));
        }

        [Test]
        public void Unknown_stroke_token_falls_back_to_magenta()
        {
            var n = new DesignNode("box") { Stroke = "{missing}" };
            Assert.That(Css(n), Does.Contain("border: 1px solid var(--color-missing, magenta)"));
        }

        // --- States ---

        [Test]
        public void State_stroke_emits_border_in_pseudo_rule()
        {
            var n = new DesignNode("btn") { Fill = "#333" };
            n.State(InteractionState.Focus).Stroke = "#5b8cff";
            n.State(InteractionState.Focus).StrokeWidth = 2;
            string css = Css(n);
            Assert.That(css, Does.Contain(".w0:focus {"));
            Assert.That(css, Does.Contain("border: 2px solid #5b8cff"));
        }

        [Test]
        public void State_stroke_width_only_emits_border_width()
        {
            var n = new DesignNode("btn") { Stroke = "#222", StrokeWidth = 1 };
            n.State(InteractionState.Hover).StrokeWidth = 3; // thicken on hover, keep base colour
            string css = Css(n);
            Assert.That(css, Does.Contain(".w0:hover {"));
            Assert.That(css, Does.Contain("border-width: 3px"));
        }

        // --- Serializer ---

        [Test]
        public void Stroke_round_trips_through_serializer()
        {
            var root = new DesignNode("box") { Stroke = "{line}", StrokeWidth = 2 };
            root.State(InteractionState.Focus).Stroke = "#5b8cff";
            root.State(InteractionState.Focus).StrokeWidth = 2;
            var doc = new DesignDocument(root);
            doc.Tokens.Color("line", "#3a3a3a");

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Stroke, Is.EqualTo("{line}"));
            Assert.That(reloaded.Root.StrokeWidth, Is.EqualTo(2).Within(1e-9));
            StateStyle focus = reloaded.Root.GetState(InteractionState.Focus);
            Assert.That(focus.Stroke, Is.EqualTo("#5b8cff"));
            Assert.That(focus.StrokeWidth, Is.EqualTo(2).Within(1e-9));

            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Validation ---

        [Test]
        public void Unknown_stroke_token_is_reported()
        {
            var root = new DesignNode("box") { Stroke = "{nope}" };
            List<DesignDiagnostic> diags = DesignValidator.Validate(new DesignDocument(root));
            Assert.That(diags.Exists(d => d.Code == "unknown-color-token" && d.Message.Contains("stroke")), Is.True);
        }

        [Test]
        public void Known_stroke_token_validates_clean()
        {
            var root = new DesignNode("box") { Stroke = "{line}" };
            var doc = new DesignDocument(root);
            doc.Tokens.Color("line", "#222");
            Assert.That(DesignValidator.Validate(doc), Is.Empty);
        }

        // --- Editor (undo) ---

        [Test]
        public void Editor_sets_stroke_with_undo()
        {
            var root = new DesignNode("box");
            var ed = new DocumentEditor(new DesignDocument(root));

            ed.SetStroke(root, "#222");
            ed.SetStrokeWidth(root, 2);
            Assert.That(root.Stroke, Is.EqualTo("#222"));
            Assert.That(root.StrokeWidth, Is.EqualTo(2).Within(1e-9));

            ed.Undo(); // width
            Assert.That(root.StrokeWidth, Is.EqualTo(0).Within(1e-9));
            ed.Undo(); // colour
            Assert.That(root.Stroke, Is.Null);
        }

        [Test]
        public void Editor_sets_state_stroke_with_undo()
        {
            var root = new DesignNode("btn");
            var ed = new DocumentEditor(new DesignDocument(root));

            ed.SetStateStroke(root, InteractionState.Focus, "#5b8cff");
            Assert.That(root.GetState(InteractionState.Focus).Stroke, Is.EqualTo("#5b8cff"));

            ed.Undo();
            Assert.That(root.GetState(InteractionState.Focus), Is.Null);
        }

        // --- Clone ---

        [Test]
        public void Clone_copies_stroke()
        {
            var n = new DesignNode("box") { Stroke = "#222", StrokeWidth = 2 };
            DesignNode c = n.Clone();
            Assert.That(c.Stroke, Is.EqualTo("#222"));
            Assert.That(c.StrokeWidth, Is.EqualTo(2).Within(1e-9));
            c.Stroke = "#999"; // independent
            Assert.That(n.Stroke, Is.EqualTo("#222"));
        }
    }
}
