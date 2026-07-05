using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for typography style (M4 Style panel "text"): font weight, italic, text
    /// alignment and line-height compile onto text nodes, default cleanly (nothing emitted
    /// at the neutral value), round-trip through the serializer, and are editable with undo.
    /// </summary>
    public class DesignTypographyTests
    {
        static string Css(DesignNode root, System.Action<DesignTokens> tokens = null)
        {
            var doc = new DesignDocument(root);
            tokens?.Invoke(doc.Tokens);
            return doc.Compile().Css;
        }

        static DesignNode Text(string s) => new DesignNode("t") { Text = s };

        // --- Compilation ---

        [Test]
        public void Bold_emits_font_weight_700()
        {
            var n = Text("Hi"); n.FontWeight = FontWeight.Bold;
            Assert.That(Css(n), Does.Contain("font-weight: 700"));
        }

        [Test]
        public void Medium_and_semibold_map_to_500_600()
        {
            var m = Text("a"); m.FontWeight = FontWeight.Medium;
            var s = Text("a"); s.FontWeight = FontWeight.SemiBold;
            Assert.That(Css(m), Does.Contain("font-weight: 500"));
            Assert.That(Css(s), Does.Contain("font-weight: 600"));
        }

        [Test]
        public void Normal_weight_emits_no_font_weight()
        {
            Assert.That(Css(Text("a")), Does.Not.Contain("font-weight"));
        }

        [Test]
        public void Italic_emits_font_style()
        {
            var n = Text("a"); n.Italic = true;
            Assert.That(Css(n), Does.Contain("font-style: italic"));
        }

        [Test]
        public void Text_align_center_and_justify_emit()
        {
            var c = Text("a"); c.TextAlign = TextAlign.Center;
            var j = Text("a"); j.TextAlign = TextAlign.Justify;
            Assert.That(Css(c), Does.Contain("text-align: center"));
            Assert.That(Css(j), Does.Contain("text-align: justify"));
        }

        [Test]
        public void Text_align_start_is_default_and_emits_nothing()
        {
            var n = Text("a"); n.TextAlign = TextAlign.Start;
            Assert.That(Css(n), Does.Not.Contain("text-align"));
        }

        [Test]
        public void Line_height_emits_unitless_multiplier()
        {
            var n = Text("a"); n.LineHeight = 1.5;
            Assert.That(Css(n), Does.Contain("line-height: 1.5"));
        }

        [Test]
        public void Typography_only_emits_on_text_nodes()
        {
            // A pure container (no Text, no text-binding) carries no typography.
            var box = new DesignNode("box") { Layout = LayoutMode.Column };
            box.FontWeight = FontWeight.Bold;
            box.TextAlign = TextAlign.Center;
            string css = Css(box);
            Assert.That(css, Does.Not.Contain("font-weight"));
            Assert.That(css, Does.Not.Contain("text-align"));
        }

        [Test]
        public void Typography_applies_to_text_binding_nodes()
        {
            var n = new DesignNode("label");
            n.Bind().Text = "user.name";
            n.FontWeight = FontWeight.Bold;
            Assert.That(Css(n), Does.Contain("font-weight: 700"));
        }

        // --- Serializer ---

        [Test]
        public void Typography_round_trips_through_serializer()
        {
            var root = Text("Title");
            root.FontWeight = FontWeight.SemiBold;
            root.Italic = true;
            root.TextAlign = TextAlign.Center;
            root.LineHeight = 1.25;
            var doc = new DesignDocument(root);

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.FontWeight, Is.EqualTo(FontWeight.SemiBold));
            Assert.That(reloaded.Root.Italic, Is.True);
            Assert.That(reloaded.Root.TextAlign, Is.EqualTo(TextAlign.Center));
            Assert.That(reloaded.Root.LineHeight, Is.EqualTo(1.25).Within(1e-9));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Editor ---

        [Test]
        public void Editor_sets_typography_with_undo()
        {
            var root = Text("a");
            var ed = new DocumentEditor(new DesignDocument(root));

            ed.SetFontWeight(root, FontWeight.Bold);
            ed.SetItalic(root, true);
            ed.SetTextAlign(root, TextAlign.End);
            ed.SetLineHeight(root, 1.4);
            Assert.That(root.FontWeight, Is.EqualTo(FontWeight.Bold));
            Assert.That(root.Italic, Is.True);
            Assert.That(root.TextAlign, Is.EqualTo(TextAlign.End));
            Assert.That(root.LineHeight, Is.EqualTo(1.4).Within(1e-9));

            ed.Undo(); // line height
            ed.Undo(); // align
            ed.Undo(); // italic
            ed.Undo(); // weight
            Assert.That(root.FontWeight, Is.EqualTo(FontWeight.Normal));
            Assert.That(root.Italic, Is.False);
            Assert.That(root.TextAlign, Is.EqualTo(TextAlign.Start));
            Assert.That(root.LineHeight, Is.EqualTo(0).Within(1e-9));
        }

        // --- Clone ---

        [Test]
        public void Clone_copies_typography()
        {
            var n = Text("a");
            n.FontWeight = FontWeight.Bold; n.Italic = true; n.TextAlign = TextAlign.Center; n.LineHeight = 1.5;
            DesignNode c = n.Clone();
            Assert.That(c.FontWeight, Is.EqualTo(FontWeight.Bold));
            Assert.That(c.Italic, Is.True);
            Assert.That(c.TextAlign, Is.EqualTo(TextAlign.Center));
            Assert.That(c.LineHeight, Is.EqualTo(1.5).Within(1e-9));
        }
    }
}
