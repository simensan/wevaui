using System.Collections.Generic;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Serialization;
using Weva.Designer.Validation;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for gradient fills: a node's Fill can name a gradient token (picked like a
    /// colour) which emits a --gradient-* custom property + var() background; raw gradient
    /// strings still pass through; the token table round-trips; states can use gradients;
    /// and the validator accepts a gradient token on fill (but still flags unknown ones).
    /// </summary>
    public class DesignGradientTests
    {
        static DesignCompileResult Compile(DesignNode root, System.Action<DesignTokens> tokens)
        {
            var doc = new DesignDocument(root);
            tokens(doc.Tokens);
            return doc.Compile();
        }

        [Test]
        public void Gradient_token_fill_resolves_to_var_and_emits_root()
        {
            var root = new DesignNode("hero") { Fill = "{brand}" };
            var r = Compile(root, t => t.Gradient("brand", "linear-gradient(90deg, #5b8cff, #a855f7)"));
            Assert.That(r.Css, Does.Contain("background: var(--gradient-brand)"));
            Assert.That(r.Css, Does.Contain("--gradient-brand: linear-gradient(90deg, #5b8cff, #a855f7)"));
        }

        [Test]
        public void Raw_gradient_string_passes_through()
        {
            var r = Compile(new DesignNode("hero") { Fill = "linear-gradient(0deg, red, blue)" }, t => { });
            Assert.That(r.Css, Does.Contain("background: linear-gradient(0deg, red, blue)"));
        }

        [Test]
        public void Colour_token_still_resolves_to_color_var()
        {
            // A token that exists as a colour (not a gradient) must still resolve to --color-*.
            var r = Compile(new DesignNode("box") { Fill = "{primary}" }, t => t.Color("primary", "#5b8cff"));
            Assert.That(r.Css, Does.Contain("background: var(--color-primary)"));
        }

        [Test]
        public void Gradient_token_wins_when_name_collides_with_nothing_else()
        {
            // Gradient is checked before colour fallback, so a gradient-only token resolves to gradient.
            var r = Compile(new DesignNode("box") { Fill = "{g}" }, t => t.Gradient("g", "radial-gradient(circle, #fff, #000)"));
            Assert.That(r.Css, Does.Contain("background: var(--gradient-g)"));
        }

        [Test]
        public void Unknown_fill_token_falls_back_to_magenta()
        {
            var r = Compile(new DesignNode("box") { Fill = "{nope}" }, t => { });
            Assert.That(r.Css, Does.Contain("background: var(--color-nope, magenta)"));
        }

        [Test]
        public void State_fill_supports_gradient_token()
        {
            var n = new DesignNode("btn") { Fill = "#333" };
            n.State(InteractionState.Hover).Fill = "{brand}";
            var r = Compile(n, t => t.Gradient("brand", "linear-gradient(90deg, #5b8cff, #a855f7)"));
            Assert.That(r.Css, Does.Contain(".w0:hover {"));
            Assert.That(r.Css, Does.Contain("background: var(--gradient-brand)"));
        }

        [Test]
        public void Gradient_table_round_trips_through_serializer()
        {
            var doc = new DesignDocument(new DesignNode("hero") { Fill = "{brand}" });
            doc.Tokens.Gradient("brand", "linear-gradient(90deg, #5b8cff, #a855f7)");
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Tokens.Gradients["brand"], Is.EqualTo("linear-gradient(90deg, #5b8cff, #a855f7)"));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Validation ---

        [Test]
        public void Known_gradient_token_on_fill_validates_clean()
        {
            var doc = new DesignDocument(new DesignNode("hero") { Fill = "{brand}" });
            doc.Tokens.Gradient("brand", "linear-gradient(90deg, red, blue)");
            Assert.That(DesignValidator.Validate(doc), Is.Empty);
        }

        [Test]
        public void Unknown_fill_token_is_reported_as_color_or_gradient()
        {
            var doc = new DesignDocument(new DesignNode("hero") { Fill = "{nope}" });
            List<DesignDiagnostic> diags = DesignValidator.Validate(doc);
            Assert.That(diags.Exists(d => d.Code == "unknown-color-token" && d.Message.Contains("gradient")), Is.True);
        }
    }
}
