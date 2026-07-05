using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for <see cref="DesignNode.TextShadow"/> — the glyph drop-shadow that makes
    /// HUD numbers / titles legible over busy backgrounds. Raw CSS passes through, a
    /// <c>{token}</c> resolves against the shadow table, it gates to text nodes only,
    /// defaults off, round-trips, and is editable with undo. Distinct from box-shadow.
    /// </summary>
    public class DesignTextShadowTests
    {
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;
        static string Css(DesignDocument doc) => doc.Compile().Css;
        static DesignNode Text(string s) => new DesignNode("t") { Text = s };

        [Test]
        public void Raw_text_shadow_passes_through()
        {
            var n = Text("100"); n.TextShadow = "0 1px 2px rgba(0,0,0,.6)";
            Assert.That(Css(n), Does.Contain("text-shadow: 0 1px 2px rgba(0,0,0,.6)"));
        }

        [Test]
        public void Text_shadow_token_resolves_to_var()
        {
            var root = Text("SCORE"); root.TextShadow = "{glow}";
            var doc = new DesignDocument(root);
            doc.Tokens.Shadow("glow", "0 0 6px #0ff");
            string css = Css(doc);
            Assert.That(css, Does.Contain("text-shadow: var(--shadow-glow)"));
            Assert.That(css, Does.Contain("--shadow-glow: 0 0 6px #0ff;"));
        }

        [Test]
        public void Default_emits_no_text_shadow()
        {
            Assert.That(Css(Text("a")), Does.Not.Contain("text-shadow"));
        }

        [Test]
        public void Text_shadow_is_distinct_from_box_shadow()
        {
            // A node can carry both; they must not collide.
            var n = Text("hi");
            n.TextShadow = "1px 1px 0 black";
            n.Shadow = "0 4px 8px rgba(0,0,0,.3)";
            string css = Css(n);
            Assert.That(css, Does.Contain("text-shadow: 1px 1px 0 black"));
            Assert.That(css, Does.Contain("box-shadow: 0 4px 8px rgba(0,0,0,.3)"));
        }

        [Test]
        public void Text_shadow_only_on_text_nodes()
        {
            var box = new DesignNode("box") { Layout = LayoutMode.Row };
            box.TextShadow = "1px 1px 2px black";
            Assert.That(Css(box), Does.Not.Contain("text-shadow"));
        }

        [Test]
        public void Text_shadow_applies_to_bound_text()
        {
            var n = new DesignNode("score");
            n.Bind().Text = "score";
            n.TextShadow = "0 1px 1px black";
            Assert.That(Css(n), Does.Contain("text-shadow: 0 1px 1px black"));
        }

        [Test]
        public void Text_shadow_round_trips_through_serializer()
        {
            var root = Text("BOSS");
            root.TextShadow = "0 2px 4px rgba(0,0,0,.5)";
            var doc = new DesignDocument(root);

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.TextShadow, Is.EqualTo("0 2px 4px rgba(0,0,0,.5)"));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_text_shadow_with_undo()
        {
            var root = Text("a");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetTextShadow(root, "1px 1px 2px black");
            Assert.That(root.TextShadow, Is.EqualTo("1px 1px 2px black"));

            ed.Undo();
            Assert.That(root.TextShadow, Is.Null);

            ed.Redo();
            Assert.That(root.TextShadow, Is.EqualTo("1px 1px 2px black"));
        }

        [Test]
        public void Clone_copies_text_shadow()
        {
            var n = Text("a"); n.TextShadow = "0 0 3px red";
            DesignNode c = n.Clone();
            Assert.That(c.TextShadow, Is.EqualTo("0 0 3px red"));
            // Detached: editing the clone must not touch the original.
            c.TextShadow = "changed";
            Assert.That(n.TextShadow, Is.EqualTo("0 0 3px red"));
        }
    }
}
