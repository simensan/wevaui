using NUnit.Framework;
using Weva.Figma.Mapping;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Mapping
{
    [TestFixture]
    public class ExporterTests
    {
        const string Bar = @"{
          ""id"":""1:1"", ""name"":""Bar"", ""type"":""FRAME"",
          ""layoutMode"":""HORIZONTAL"", ""counterAxisAlignItems"":""CENTER"", ""itemSpacing"":8,
          ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":200,""height"":40},
          ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.1,""g"":0.1,""b"":0.1,""a"":1}}],
          ""children"":[
            {""id"":""1:2"",""name"":""Grow"",""type"":""FRAME"",""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""FIXED"",
             ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":28}},
            {""id"":""1:3"",""name"":""Label"",""type"":""TEXT"",""characters"":""Play"",
             ""style"":{""fontFamily"":""Inter"",""fontSize"":14},
             ""fills"":[{""type"":""SOLID"",""color"":{""r"":1,""g"":1,""b"":1,""a"":1}}]}
          ]
        }";

        [Test]
        public void EmitsNestedHtmlWithClassesAndFigmaIds()
        {
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(Bar));
            Assert.That(d.Html, Does.Contain("<div class=\"bar\" data-figma-id=\"1:1\">"));
            Assert.That(d.Html, Does.Contain("<div class=\"grow\" data-figma-id=\"1:2\">"));
            Assert.That(d.Html, Does.Contain("<span class=\"label\" data-figma-id=\"1:3\">Play</span>"));
        }

        [Test]
        public void EmitsResetAndRules()
        {
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(Bar));
            Assert.That(d.Css, Does.Contain("* {"));
            Assert.That(d.Css, Does.Contain("box-sizing: border-box;"));
            Assert.That(d.Css, Does.Contain(".bar {"));
            Assert.That(d.Css, Does.Contain("display: flex;"));
            Assert.That(d.Css, Does.Contain(".label {"));
            Assert.That(d.Css, Does.Contain("font-family: Inter;"));
        }

        [Test]
        public void OutputIsDeterministic()
        {
            string a = FigmaDocumentExporter.Export(FigmaNode.Parse(Bar)).Html
                       + "\n----\n" + FigmaDocumentExporter.Export(FigmaNode.Parse(Bar)).Css;
            string b = FigmaDocumentExporter.Export(FigmaNode.Parse(Bar)).Html
                       + "\n----\n" + FigmaDocumentExporter.Export(FigmaNode.Parse(Bar)).Css;
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void ClassPrefixApplied()
        {
            var opts = new ExportOptions { ClassPrefix = "menu" };
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(Bar), opts);
            Assert.That(d.Html, Does.Contain("class=\"menu-bar\""));
            Assert.That(d.Css, Does.Contain(".menu-bar {"));
        }

        [Test]
        public void DuplicateNamesDeduplicate()
        {
            const string json = @"{
              ""id"":""r"",""name"":""Row"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
              ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10},
              ""children"":[
                {""id"":""a"",""name"":""Item"",""type"":""FRAME"",""layoutSizingHorizontal"":""HUG"",""layoutSizingVertical"":""HUG"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":4,""height"":4}},
                {""id"":""b"",""name"":""Item"",""type"":""FRAME"",""layoutSizingHorizontal"":""HUG"",""layoutSizingVertical"":""HUG"",""absoluteBoundingBox"":{""x"":0,""y"":4,""width"":4,""height"":4}}
              ]
            }";
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(json));
            Assert.That(d.Html, Does.Contain("class=\"item\" data-figma-id=\"a\""));
            Assert.That(d.Html, Does.Contain("class=\"item-2\" data-figma-id=\"b\""));
        }

        [Test]
        public void ImageFillRecordsRasterRequestAndUrl()
        {
            const string json = @"{""id"":""i"",""name"":""Hero"",""type"":""RECTANGLE"",
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10},
                ""fills"":[{""type"":""IMAGE"",""imageRef"":""abc123"",""scaleMode"":""FILL""}]}";
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(json));
            Assert.That(d.Css, Does.Contain("url(\"images/abc123.png\")"));
            Assert.That(d.Css, Does.Contain("background-size: cover;"));
            Assert.That(d.RasterRequests.Count, Is.EqualTo(1));
            Assert.That(d.RasterRequests[0].Kind, Is.EqualTo(RasterKind.ImageFill));
            Assert.That(d.RasterRequests[0].FileName, Is.EqualTo("images/abc123.png"));
        }

        [Test]
        public void VectorRecordsRasterRequestAndEmptyElement()
        {
            const string json = @"{""id"":""v"",""name"":""Icon"",""type"":""VECTOR"",
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":24,""height"":24}}";
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(json));
            Assert.That(d.RasterRequests.Count, Is.EqualTo(1));
            Assert.That(d.RasterRequests[0].Kind, Is.EqualTo(RasterKind.Vector));
            Assert.That(d.Css, Does.Contain("url(\"images/icon-v.png\")"));
            Assert.That(d.Html, Does.Contain("<div class=\"icon\" data-figma-id=\"v\"></div>"));
        }

        [Test]
        public void InvisibleNodesAreSkipped()
        {
            const string json = @"{
              ""id"":""r"",""name"":""Root"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
              ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10},
              ""children"":[
                {""id"":""hid"",""name"":""Hidden"",""type"":""FRAME"",""visible"":false,""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":4,""height"":4}}
              ]
            }";
            ExportedDocument d = FigmaDocumentExporter.Export(FigmaNode.Parse(json));
            Assert.That(d.Html, Does.Not.Contain("Hidden"));
            Assert.That(d.Html, Does.Not.Contain("data-figma-id=\"hid\""));
        }
    }
}
