using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Persistence coverage: the on-disk format round-trips losslessly, is
    /// deterministic, tolerates unknown/missing keys (forward-compat), and a
    /// reloaded document compiles byte-for-byte identically to the original.
    /// </summary>
    public class DesignSerializerTests
    {
        static DesignDocument SampleDoc()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column, Gap = 16 };
            root.SetFixedSize(390, 844);
            root.SetPadding(24);

            var header = new DesignNode("Header") { Layout = LayoutMode.Row, MainAlign = MainAlign.SpaceBetween };
            header.SetSize(SizeMode.Fill, SizeMode.Hug);
            header.Add(new DesignNode("Title") { Text = "Inventory", TextColor = "{text/primary}", FontSize = 20 });
            header.Add(new DesignNode("Coins") { Text = "1,250", TextColor = "#ffcc00", FontSize = 16 });

            var body = new DesignNode("Body") { Layout = LayoutMode.Column, Gap = 8, Fill = "{surface}", Radius = 12 };
            body.SetSize(SizeMode.Fill, SizeMode.Fill);
            body.Opacity = 0.95;

            root.Add(header).Add(body);

            var doc = new DesignDocument(root);
            doc.Tokens.Color("text/primary", "#111111").Color("surface", "#1e1e28");
            return doc;
        }

        [Test]
        public void Round_trip_is_stable()
        {
            DesignDocument doc = SampleDoc();
            string text1 = DesignSerializer.Serialize(doc);
            DesignDocument doc2 = DesignSerializer.Deserialize(text1);
            string text2 = DesignSerializer.Serialize(doc2);
            Assert.That(text2, Is.EqualTo(text1));
        }

        [Test]
        public void Serialization_is_deterministic()
        {
            DesignDocument doc = SampleDoc();
            Assert.That(DesignSerializer.Serialize(doc), Is.EqualTo(DesignSerializer.Serialize(doc)));
        }

        [Test]
        public void Reloaded_document_compiles_identically()
        {
            DesignDocument doc = SampleDoc();
            string text = DesignSerializer.Serialize(doc);
            DesignDocument reloaded = DesignSerializer.Deserialize(text);

            DesignCompileResult a = doc.Compile();
            DesignCompileResult b = reloaded.Compile();
            Assert.That(b.Css, Is.EqualTo(a.Css));
            Assert.That(b.Html, Is.EqualTo(a.Html));
        }

        [Test]
        public void Output_includes_version_header()
        {
            string text = DesignSerializer.Serialize(SampleDoc());
            Assert.That(text, Does.Contain("\"version\": 1"));
        }

        [Test]
        public void Reconstructs_node_fields()
        {
            DesignDocument doc = DesignSerializer.Deserialize(DesignSerializer.Serialize(SampleDoc()));
            DesignNode root = doc.Root;
            Assert.That(root.Name, Is.EqualTo("Screen"));
            Assert.That(root.Layout, Is.EqualTo(LayoutMode.Column));
            Assert.That(root.Gap.Px, Is.EqualTo(16));
            Assert.That(root.Width, Is.EqualTo(390));
            Assert.That(root.PadLeft.Px, Is.EqualTo(24));

            DesignNode header = root.Children[0];
            Assert.That(header.MainAlign, Is.EqualTo(MainAlign.SpaceBetween));
            Assert.That(header.WidthMode, Is.EqualTo(SizeMode.Fill));

            DesignNode title = header.Children[0];
            Assert.That(title.Text, Is.EqualTo("Inventory"));
            Assert.That(title.FontSize.Px, Is.EqualTo(20));

            DesignNode body = root.Children[1];
            Assert.That(body.Opacity, Is.EqualTo(0.95).Within(1e-9));
            Assert.That(body.Radius.Px, Is.EqualTo(12));
        }

        [Test]
        public void Reconstructs_color_tokens()
        {
            DesignDocument doc = DesignSerializer.Deserialize(DesignSerializer.Serialize(SampleDoc()));
            Assert.That(doc.Tokens.Colors["text/primary"], Is.EqualTo("#111111"));
            Assert.That(doc.Tokens.Colors["surface"], Is.EqualTo("#1e1e28"));
        }

        // --- Forward / backward compatibility ---

        [Test]
        public void Unknown_keys_are_ignored()
        {
            string json = "{ \"version\": 2, \"futureFeature\": {\"x\": 1}, " +
                          "\"root\": { \"name\": \"r\", \"layout\": \"row\", \"unknownNodeKey\": 99 } }";
            DesignDocument doc = DesignSerializer.Deserialize(json);
            Assert.That(doc.Root.Name, Is.EqualTo("r"));
            Assert.That(doc.Root.Layout, Is.EqualTo(LayoutMode.Row));
        }

        [Test]
        public void Missing_keys_fall_back_to_defaults()
        {
            DesignDocument doc = DesignSerializer.Deserialize("{ \"root\": { } }");
            DesignNode n = doc.Root;
            Assert.That(n.Layout, Is.EqualTo(LayoutMode.None));
            Assert.That(n.WidthMode, Is.EqualTo(SizeMode.Hug));
            Assert.That(n.CrossAlign, Is.EqualTo(CrossAlign.Start));
            Assert.That(n.Opacity, Is.EqualTo(1));
            Assert.That(n.Text, Is.Null);
        }

        [Test]
        public void Unknown_enum_value_falls_back_to_default()
        {
            DesignDocument doc = DesignSerializer.Deserialize("{ \"root\": { \"layout\": \"masonry\" } }");
            Assert.That(doc.Root.Layout, Is.EqualTo(LayoutMode.None));
        }

        // --- Strings / numbers ---

        [Test]
        public void Special_characters_in_text_round_trip()
        {
            var root = new DesignNode("n") { Text = "Quote \" Back\\slash\nNewline\tTab <tag>" };
            string text = DesignSerializer.Serialize(new DesignDocument(root));
            DesignDocument doc = DesignSerializer.Deserialize(text);
            Assert.That(doc.Root.Text, Is.EqualTo("Quote \" Back\\slash\nNewline\tTab <tag>"));
        }

        [Test]
        public void Fractional_values_round_trip()
        {
            var root = new DesignNode("n") { Gap = 12.5, Opacity = 0.33 };
            DesignDocument doc = DesignSerializer.Deserialize(DesignSerializer.Serialize(new DesignDocument(root)));
            Assert.That(doc.Root.Gap.Px, Is.EqualTo(12.5).Within(1e-9));
            Assert.That(doc.Root.Opacity, Is.EqualTo(0.33).Within(1e-9));
        }

        [Test]
        public void Empty_document_round_trips()
        {
            string text = DesignSerializer.Serialize(new DesignDocument());
            DesignDocument doc = DesignSerializer.Deserialize(text);
            Assert.That(doc.Root, Is.Null);
            Assert.That(doc.Tokens.Colors.Count, Is.EqualTo(0));
        }
    }
}
