using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the full token system (M4): spacing / radius / type / shadow tokens
    /// resolve to <c>var(--…)</c>, emit a <c>:root</c> block, round-trip through the
    /// serializer, and coexist with literal px values.
    /// </summary>
    public class DesignTokensTests
    {
        static DesignCompileResult Compile(DesignNode root, System.Action<DesignTokens> tokens)
        {
            var doc = new DesignDocument(root);
            tokens(doc.Tokens);
            return doc.Compile();
        }

        // --- Resolution to var() ---

        [Test]
        public void Spacing_token_resolves_gap_to_var()
        {
            var root = new DesignNode { Layout = LayoutMode.Row, Gap = Dim.Token("md") };
            var r = Compile(root, t => t.Space("md", 16));
            Assert.That(r.Css, Does.Contain("gap: var(--space-md)"));
            Assert.That(r.Css, Does.Contain("--space-md: 16px"));
        }

        [Test]
        public void Spacing_token_resolves_uniform_padding_to_var()
        {
            var root = new DesignNode
            {
                PadTop = Dim.Token("lg"), PadRight = Dim.Token("lg"),
                PadBottom = Dim.Token("lg"), PadLeft = Dim.Token("lg"),
            };
            var r = Compile(root, t => t.Space("lg", 24));
            Assert.That(r.Css, Does.Contain("padding: var(--space-lg)"));
        }

        [Test]
        public void Radius_token_resolves_to_var()
        {
            var root = new DesignNode { Radius = Dim.Token("card") };
            var r = Compile(root, t => t.Radius("card", 12));
            Assert.That(r.Css, Does.Contain("border-radius: var(--radius-card)"));
            Assert.That(r.Css, Does.Contain("--radius-card: 12px"));
        }

        [Test]
        public void Type_token_resolves_font_size_to_var()
        {
            var root = new DesignNode { Text = "Title", FontSize = Dim.Token("h1") };
            var r = Compile(root, t => t.Font("h1", 32));
            Assert.That(r.Css, Does.Contain("font-size: var(--font-h1)"));
            Assert.That(r.Css, Does.Contain("--font-h1: 32px"));
        }

        [Test]
        public void Shadow_token_resolves_to_var()
        {
            var root = new DesignNode { Shadow = "{elevated}" };
            var r = Compile(root, t => t.Shadow("elevated", "0 4px 12px rgba(0,0,0,0.3)"));
            Assert.That(r.Css, Does.Contain("box-shadow: var(--shadow-elevated)"));
            Assert.That(r.Css, Does.Contain("--shadow-elevated: 0 4px 12px"));
        }

        [Test]
        public void Raw_shadow_passes_through()
        {
            var r = Compile(new DesignNode { Shadow = "0 2px 4px black" }, t => { });
            Assert.That(r.Css, Does.Contain("box-shadow: 0 2px 4px black"));
        }

        [Test]
        public void Literal_and_token_dims_coexist_in_padding()
        {
            var root = new DesignNode
            {
                PadTop = Dim.Token("sm"), PadRight = 4, PadBottom = Dim.Token("sm"), PadLeft = 4,
            };
            var r = Compile(root, t => t.Space("sm", 8));
            // symmetric: top==bottom (token), left==right (4px) → two-value shorthand
            Assert.That(r.Css, Does.Contain("padding: var(--space-sm) 4px"));
        }

        // --- Serializer round-trip ---

        [Test]
        public void Token_dims_round_trip_through_serializer()
        {
            var root = new DesignNode("n") { Layout = LayoutMode.Row, Gap = Dim.Token("md"), Radius = Dim.Token("card") };
            var doc = new DesignDocument(root);
            doc.Tokens.Space("md", 16).Radius("card", 12);

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Gap.HasToken, Is.True);
            Assert.That(reloaded.Root.Gap.TokenName, Is.EqualTo("md"));
            Assert.That(reloaded.Root.Radius.TokenName, Is.EqualTo("card"));
        }

        [Test]
        public void Literal_dims_round_trip_as_numbers()
        {
            var root = new DesignNode("n") { Layout = LayoutMode.Row, Gap = 12.5 };
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(new DesignDocument(root)));
            Assert.That(reloaded.Root.Gap.HasToken, Is.False);
            Assert.That(reloaded.Root.Gap.Px, Is.EqualTo(12.5).Within(1e-9));
        }

        [Test]
        public void All_token_tables_round_trip()
        {
            var doc = new DesignDocument(new DesignNode("n"));
            doc.Tokens.Color("primary", "#123456").Space("md", 16).Radius("card", 12)
                      .Font("h1", 32).Shadow("elevated", "0 4px 12px black");

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Tokens.Colors["primary"], Is.EqualTo("#123456"));
            Assert.That(reloaded.Tokens.Spacing["md"], Is.EqualTo(16));
            Assert.That(reloaded.Tokens.Radii["card"], Is.EqualTo(12));
            Assert.That(reloaded.Tokens.FontSizes["h1"], Is.EqualTo(32));
            Assert.That(reloaded.Tokens.Shadows["elevated"], Is.EqualTo("0 4px 12px black"));
        }

        // --- Editor integration ---

        [Test]
        public void Editor_can_set_a_token_dim_and_undo()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Row };
            var ed = new DocumentEditor(new DesignDocument(root));

            ed.SetGap(root, Dim.Token("md"));
            Assert.That(root.Gap.TokenName, Is.EqualTo("md"));

            ed.Undo();
            Assert.That(root.Gap.IsZero, Is.True);
        }
    }
}
