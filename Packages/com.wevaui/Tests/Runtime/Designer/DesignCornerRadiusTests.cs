using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for per-corner radius: a node can round individual corners (rounded-top
    /// tabs, cards with one rounded corner) via the 4-value border-radius shorthand, with
    /// unset corners falling back to the uniform Radius. Tokens resolve; round-trips; undo.
    /// </summary>
    public class DesignCornerRadiusTests
    {
        static string Css(DesignNode root, System.Action<DesignTokens> tokens = null)
        {
            var doc = new DesignDocument(root);
            tokens?.Invoke(doc.Tokens);
            return doc.Compile().Css;
        }

        [Test]
        public void Uniform_radius_unchanged_when_no_per_corner()
        {
            var n = new DesignNode("box") { Radius = 8 };
            string css = Css(n);
            Assert.That(css, Does.Contain("border-radius: 8px"));
            Assert.That(css, Does.Not.Contain("8px 8px")); // single value, not shorthand
        }

        [Test]
        public void Top_only_rounded_emits_four_value_shorthand()
        {
            // A tab: top corners rounded, bottom square.
            var n = new DesignNode("tab") { RadiusTopLeft = Dim.Of(8), RadiusTopRight = Dim.Of(8) };
            Assert.That(Css(n), Does.Contain("border-radius: 8px 8px 0px 0px"));
        }

        [Test]
        public void Unset_corners_fall_back_to_uniform_radius()
        {
            // Uniform 4, override just bottom-right to 16 → TL/TR/BL inherit 4.
            var n = new DesignNode("box") { Radius = 4, RadiusBottomRight = Dim.Of(16) };
            Assert.That(Css(n), Does.Contain("border-radius: 4px 4px 16px 4px"));
        }

        [Test]
        public void Corner_radius_resolves_tokens()
        {
            var n = new DesignNode("box") { RadiusTopLeft = Dim.Token("lg") };
            string css = Css(n, t => t.Radius("lg", 12));
            Assert.That(css, Does.Contain("border-radius: var(--radius-lg) 0px 0px 0px"));
        }

        [Test]
        public void Per_corner_round_trips_through_serializer()
        {
            var root = new DesignNode("tab") { Radius = 4, RadiusTopLeft = Dim.Of(8), RadiusTopRight = Dim.Token("lg") };
            var doc = new DesignDocument(root);
            doc.Tokens.Radius("lg", 12);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.RadiusTopLeft, Is.EqualTo((Dim?)Dim.Of(8)));
            Assert.That(reloaded.Root.RadiusTopRight, Is.EqualTo((Dim?)Dim.Token("lg")));
            Assert.That(reloaded.Root.RadiusBottomLeft, Is.Null);
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_corner_radii_with_undo()
        {
            var root = new DesignNode("tab");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetCornerRadii(root, Dim.Of(8), Dim.Of(8), null, null);
            Assert.That(root.RadiusTopLeft, Is.EqualTo((Dim?)Dim.Of(8)));
            Assert.That(root.RadiusBottomRight, Is.Null);
            ed.Undo();
            Assert.That(root.RadiusTopLeft, Is.Null);
            Assert.That(root.HasPerCornerRadius, Is.False);
        }

        [Test]
        public void Clone_copies_per_corner_radius()
        {
            var n = new DesignNode("tab") { RadiusTopLeft = Dim.Of(8), RadiusTopRight = Dim.Of(8) };
            DesignNode c = n.Clone();
            Assert.That(c.RadiusTopLeft, Is.EqualTo((Dim?)Dim.Of(8)));
            Assert.That(c.RadiusBottomLeft, Is.Null);
        }
    }
}
