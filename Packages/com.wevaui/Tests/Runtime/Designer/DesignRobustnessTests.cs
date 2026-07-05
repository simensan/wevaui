using System.Diagnostics;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// M9 hardening: edge cases (deep/wide trees, component cycles, malformed input,
    /// unicode) and large-document performance. These prove the model layer degrades
    /// gracefully and stays fast — production-readiness, not just feature coverage.
    /// </summary>
    public class DesignRobustnessTests
    {
        // --- Structural extremes ---

        [Test]
        public void Deeply_nested_tree_compiles()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            DesignNode cur = root;
            for (int i = 0; i < 300; i++)
            {
                var child = new DesignNode("n" + i) { Layout = LayoutMode.Column };
                cur.Add(child);
                cur = child;
            }
            cur.Add(new DesignNode { Text = "DEEP" });

            DesignCompileResult r = new DesignDocument(root).Compile();
            Assert.That(r.Html, Does.Contain("DEEP"));
        }

        [Test]
        public void Wide_tree_compiles()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            for (int i = 0; i < 1000; i++)
                root.Add(new DesignNode("c" + i) { Text = "item " + i });

            DesignCompileResult r = new DesignDocument(root).Compile();
            Assert.That(r.Html, Does.Contain("item 999"));
        }

        // --- Component cycles (must terminate, not hang/overflow) ---

        [Test]
        public void Self_referential_component_terminates()
        {
            var tpl = new DesignNode("A") { Layout = LayoutMode.Column };
            tpl.Add(new DesignNode { ComponentRef = "A" }); // A contains an instance of A
            var comp = new DesignComponent("A", tpl);

            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "A" });
            var doc = new DesignDocument(root);
            doc.AddComponent(comp);

            Assert.That(() => doc.Compile(), Throws.Nothing);
            Assert.That(doc.Compile().Html, Is.Not.Empty);
        }

        [Test]
        public void Mutually_recursive_components_terminate()
        {
            var aTpl = new DesignNode("A") { Layout = LayoutMode.Column };
            aTpl.Add(new DesignNode { ComponentRef = "B" });
            var bTpl = new DesignNode("B") { Layout = LayoutMode.Column };
            bTpl.Add(new DesignNode { ComponentRef = "A" });

            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode { ComponentRef = "A" });
            var doc = new DesignDocument(root);
            doc.AddComponent(new DesignComponent("A", aTpl));
            doc.AddComponent(new DesignComponent("B", bTpl));

            Assert.That(() => doc.Compile(), Throws.Nothing);
        }

        // --- Malformed / edge input ---

        [Test]
        public void Malformed_json_throws_rather_than_crashing()
        {
            Assert.That(() => DesignSerializer.Deserialize("{ not valid"), Throws.Exception);
            Assert.That(() => DesignSerializer.Deserialize("[1,2,"), Throws.Exception);
        }

        [Test]
        public void Empty_json_object_deserializes_to_empty_document()
        {
            DesignDocument doc = DesignSerializer.Deserialize("{}");
            Assert.That(doc.Root, Is.Null);
            Assert.That(doc.Tokens.Colors.Count, Is.EqualTo(0));
            Assert.That(doc.Components.Count, Is.EqualTo(0));
        }

        [Test]
        public void Unicode_text_round_trips()
        {
            const string text = "héllo 世界 🎮 — ok";
            var root = new DesignNode("n") { Text = text };
            DesignDocument doc = DesignSerializer.Deserialize(DesignSerializer.Serialize(new DesignDocument(root)));
            Assert.That(doc.Root.Text, Is.EqualTo(text));
        }

        [Test]
        public void Token_name_with_punctuation_sanitizes_to_valid_css_var()
        {
            var n = new DesignNode { Fill = "{brand/primary.50}" };
            var doc = new DesignDocument(n);
            doc.Tokens.Color("brand/primary.50", "#123");
            string css = doc.Compile().Css;
            Assert.That(css, Does.Contain("--color-brand-primary-50: #123"));
            Assert.That(css, Does.Contain("background: var(--color-brand-primary-50)"));
        }

        // --- Large document: round-trip + performance budget ---

        static DesignDocument LargeDoc(int rows)
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column, Gap = 8 };
            root.SetFixedSize(390, 844);
            for (int i = 0; i < rows; i++)
            {
                var row = new DesignNode("row" + i) { Layout = LayoutMode.Row, Gap = 8, Fill = "{surface}", Radius = 8 };
                row.SetSize(SizeMode.Fill, SizeMode.Hug);
                row.SetPadding(12);
                row.Add(new DesignNode("label") { Text = "Setting " + i, TextColor = "{text}", FontSize = 16 });
                row.Add(new DesignNode("value") { Text = "On", TextColor = "{muted}", FontSize = 16 });
                root.Add(row);
            }
            var doc = new DesignDocument(root);
            doc.Tokens.Color("surface", "#1e1e28").Color("text", "#fff").Color("muted", "#999");
            return doc;
        }

        [Test]
        public void Large_document_round_trips_losslessly()
        {
            DesignDocument doc = LargeDoc(500);
            string text = DesignSerializer.Serialize(doc);
            DesignDocument reloaded = DesignSerializer.Deserialize(text);
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
            Assert.That(reloaded.Compile().Html, Is.EqualTo(doc.Compile().Html));
        }

        [Test]
        public void Large_document_compiles_within_budget()
        {
            DesignDocument doc = LargeDoc(1000); // ~3000 nodes
            var sw = Stopwatch.StartNew();
            DesignCompileResult r = doc.Compile();
            sw.Stop();

            Assert.That(r.Html, Does.Contain("Setting 999"));
            // Generous ceiling — only a pathological blow-up (e.g. accidental O(n^2)) trips it.
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000),
                $"compile of ~3000 nodes took {sw.ElapsedMilliseconds}ms");
        }

        [Test]
        public void Large_document_serialize_round_trip_within_budget()
        {
            DesignDocument doc = LargeDoc(1000);
            var sw = Stopwatch.StartNew();
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            sw.Stop();
            Assert.That(reloaded.Root.Children, Has.Count.EqualTo(1000));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000),
                $"serialize round-trip of ~3000 nodes took {sw.ElapsedMilliseconds}ms");
        }
    }
}
