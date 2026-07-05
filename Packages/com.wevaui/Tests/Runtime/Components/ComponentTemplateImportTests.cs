using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Weva.Documents;
using Weva.Dom;

namespace Weva.Tests.Components {
    public class ComponentTemplateImportTests {
        string tempDir;

        [SetUp]
        public void SetUp() {
            tempDir = Path.Combine(Path.GetTempPath(), "weva-template-imports-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown() {
            if (tempDir != null && Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Test]
        public void Builder_imports_external_template_definition_before_component_registration() {
            string mainPath = Path.Combine(tempDir, "menu.html");
            File.WriteAllText(Path.Combine(tempDir, "stage-card.html"),
                "<template id=\"stage-card\"><article class=\"card\"><slot></slot></article></template>");

            var state = new UIDocumentBuilder {
                DocumentSource = "<main><template src=\"stage-card.html\"></template><stage-card><span>Forest</span></stage-card></main>",
                DocumentPath = mainPath
            }.Build();

            Assert.That(state.Components.Contains("stage-card"), Is.True);
            // HtmlParser wraps fragments in synthetic <html><body>, so reach
            // for the <main> by document-order search rather than indexing
            // doc.Children directly.
            var main = state.Doc.GetElementsByTagName("main").Single();
            var host = (Element)main.Children[0];
            Assert.That(host.TagName, Is.EqualTo("stage-card"));
            Assert.That(((Element)host.Children[0]).TagName, Is.EqualTo("article"));
            Assert.That(((Element)((Element)host.Children[0]).Children[0]).TagName, Is.EqualTo("span"));
        }

        [Test]
        public void Builder_fills_named_template_from_external_fragment() {
            string mainPath = Path.Combine(tempDir, "menu.html");
            File.WriteAllText(Path.Combine(tempDir, "stage-card.html"),
                "<article class=\"card\"><slot></slot></article>");

            var state = new UIDocumentBuilder {
                DocumentSource = "<main><template id=\"stage-card\" src=\"stage-card.html\"></template><stage-card>Harbor</stage-card></main>",
                DocumentPath = mainPath
            }.Build();

            Assert.That(state.Components.Contains("stage-card"), Is.True);
            var main = state.Doc.GetElementsByTagName("main").Single();
            var host = (Element)main.Children[0];
            Assert.That(((Element)host.Children[0]).TagName, Is.EqualTo("article"));
            Assert.That(((TextNode)((Element)host.Children[0]).Children[0]).Data, Is.EqualTo("Harbor"));
        }
    }
}
