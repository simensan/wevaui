using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the richer text properties: letter-spacing (incl. negative/tighten),
    /// text-transform (UPPERCASE button labels) and text-decoration (link underline,
    /// struck-out price). They compile onto text nodes only, default off, round-trip and
    /// are editable with undo.
    /// </summary>
    public class DesignTextStyleTests
    {
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;
        static DesignNode Text(string s) => new DesignNode("t") { Text = s };

        [Test]
        public void Letter_spacing_emits_px()
        {
            var n = Text("WIDE"); n.LetterSpacing = 2;
            Assert.That(Css(n), Does.Contain("letter-spacing: 2px"));
        }

        [Test]
        public void Negative_letter_spacing_tightens()
        {
            var n = Text("tight"); n.LetterSpacing = -0.5;
            Assert.That(Css(n), Does.Contain("letter-spacing: -0.5px"));
        }

        [Test]
        public void Zero_letter_spacing_emits_nothing()
        {
            Assert.That(Css(Text("a")), Does.Not.Contain("letter-spacing"));
        }

        [Test]
        public void Uppercase_transform_emits()
        {
            var n = Text("button"); n.TextTransform = TextTransform.Uppercase;
            Assert.That(Css(n), Does.Contain("text-transform: uppercase"));
        }

        [Test]
        public void Capitalize_transform_emits()
        {
            var n = Text("title"); n.TextTransform = TextTransform.Capitalize;
            Assert.That(Css(n), Does.Contain("text-transform: capitalize"));
        }

        [Test]
        public void Underline_and_line_through_emit()
        {
            var u = Text("link"); u.TextDecoration = TextDecoration.Underline;
            var s = Text("$9"); s.TextDecoration = TextDecoration.LineThrough;
            Assert.That(Css(u), Does.Contain("text-decoration: underline"));
            Assert.That(Css(s), Does.Contain("text-decoration: line-through"));
        }

        [Test]
        public void Defaults_emit_nothing()
        {
            var n = Text("a");
            string css = Css(n);
            Assert.That(css, Does.Not.Contain("text-transform"));
            Assert.That(css, Does.Not.Contain("text-decoration"));
        }

        [Test]
        public void Text_props_only_on_text_nodes()
        {
            var box = new DesignNode("box") { Layout = LayoutMode.Row };
            box.TextTransform = TextTransform.Uppercase;
            box.LetterSpacing = 2;
            string css = Css(box);
            Assert.That(css, Does.Not.Contain("text-transform"));
            Assert.That(css, Does.Not.Contain("letter-spacing"));
        }

        [Test]
        public void Text_props_round_trip_through_serializer()
        {
            var root = Text("BUY NOW");
            root.LetterSpacing = 1.5;
            root.TextTransform = TextTransform.Uppercase;
            root.TextDecoration = TextDecoration.Underline;
            var doc = new DesignDocument(root);

            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.LetterSpacing, Is.EqualTo(1.5).Within(1e-9));
            Assert.That(reloaded.Root.TextTransform, Is.EqualTo(TextTransform.Uppercase));
            Assert.That(reloaded.Root.TextDecoration, Is.EqualTo(TextDecoration.Underline));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_text_props_with_undo()
        {
            var root = Text("a");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetLetterSpacing(root, 2);
            ed.SetTextTransform(root, TextTransform.Uppercase);
            ed.SetTextDecoration(root, TextDecoration.Underline);
            Assert.That(root.LetterSpacing, Is.EqualTo(2).Within(1e-9));
            Assert.That(root.TextTransform, Is.EqualTo(TextTransform.Uppercase));
            Assert.That(root.TextDecoration, Is.EqualTo(TextDecoration.Underline));

            ed.Undo(); ed.Undo(); ed.Undo();
            Assert.That(root.LetterSpacing, Is.EqualTo(0).Within(1e-9));
            Assert.That(root.TextTransform, Is.EqualTo(TextTransform.None));
            Assert.That(root.TextDecoration, Is.EqualTo(TextDecoration.None));
        }

        [Test]
        public void Clone_copies_text_props()
        {
            var n = Text("a");
            n.LetterSpacing = 1; n.TextTransform = TextTransform.Lowercase; n.TextDecoration = TextDecoration.LineThrough;
            DesignNode c = n.Clone();
            Assert.That(c.LetterSpacing, Is.EqualTo(1).Within(1e-9));
            Assert.That(c.TextTransform, Is.EqualTo(TextTransform.Lowercase));
            Assert.That(c.TextDecoration, Is.EqualTo(TextDecoration.LineThrough));
        }
    }
}
