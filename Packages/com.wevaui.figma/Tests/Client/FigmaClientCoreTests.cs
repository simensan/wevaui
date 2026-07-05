using System.Collections.Generic;
using NUnit.Framework;
using Weva.Figma.Client;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Client
{
    [TestFixture]
    public class FigmaApiRoutesTests
    {
        [Test]
        public void FileRoute()
            => Assert.That(FigmaApiRoutes.File("ABC123"), Is.EqualTo("https://api.figma.com/v1/files/ABC123"));

        [Test]
        public void NodesRoute()
            => Assert.That(FigmaApiRoutes.FileNodes("ABC", new[] { "1:2", "3:4" }),
                Is.EqualTo("https://api.figma.com/v1/files/ABC/nodes?ids=1:2,3:4"));

        [Test]
        public void RenderImagesRouteWithScale()
            => Assert.That(FigmaApiRoutes.RenderImages("ABC", new[] { "1:2" }, "png", 2),
                Is.EqualTo("https://api.figma.com/v1/images/ABC?ids=1:2&format=png&scale=2"));

        [Test]
        public void RenderImagesRouteDefaultScaleOmitsParam()
            => Assert.That(FigmaApiRoutes.RenderImages("ABC", new[] { "1:2" }),
                Is.EqualTo("https://api.figma.com/v1/images/ABC?ids=1:2&format=png"));

        [Test]
        public void VariablesAndImageFillRoutes()
        {
            Assert.That(FigmaApiRoutes.Variables("ABC"), Is.EqualTo("https://api.figma.com/v1/files/ABC/variables/local"));
            Assert.That(FigmaApiRoutes.ImageFills("ABC"), Is.EqualTo("https://api.figma.com/v1/files/ABC/images"));
        }
    }

    [TestFixture]
    public class FigmaUrlTests
    {
        [Test]
        public void ParsesDesignUrlWithNode()
        {
            FigmaTarget t = FigmaUrl.Parse("https://www.figma.com/design/abc123/My-App?node-id=12-345&t=x");
            Assert.That(t.FileKey, Is.EqualTo("abc123"));
            Assert.That(t.NodeId, Is.EqualTo("12:345"));
            Assert.That(t.HasNode, Is.True);
        }

        [Test]
        public void ParsesFileUrlWithoutNode()
        {
            FigmaTarget t = FigmaUrl.Parse("https://www.figma.com/file/KEY99/Whatever");
            Assert.That(t.FileKey, Is.EqualTo("KEY99"));
            Assert.That(t.HasNode, Is.False);
        }

        [Test]
        public void BareKeyPassesThrough()
        {
            FigmaTarget t = FigmaUrl.Parse("justAKey123");
            Assert.That(t.FileKey, Is.EqualTo("justAKey123"));
            Assert.That(t.HasNode, Is.False);
        }
    }

    [TestFixture]
    public class FigmaResponsesTests
    {
        [Test]
        public void ParseFileDocumentAndName()
        {
            const string json = @"{""name"":""My File"",""document"":{""id"":""0:0"",""type"":""DOCUMENT"",
                ""children"":[{""id"":""0:1"",""type"":""CANVAS"",""name"":""Page 1"",
                  ""children"":[{""id"":""1:2"",""type"":""FRAME"",""name"":""Home""}]}]}}";
            FigmaNode doc = FigmaResponses.ParseFileDocument(json);
            Assert.That(doc.Type, Is.EqualTo("DOCUMENT"));
            Assert.That(FigmaResponses.ParseFileName(json), Is.EqualTo("My File"));
            List<FigmaNode> frames = FigmaNodeQuery.CollectExportableFrames(doc);
            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(frames[0].Name, Is.EqualTo("Home"));
        }

        [Test]
        public void ParseNodesMapsIdToDocument()
        {
            const string json = @"{""nodes"":{
                ""1:2"":{""document"":{""id"":""1:2"",""type"":""FRAME"",""name"":""Bar""}},
                ""3:4"":{""document"":{""id"":""3:4"",""type"":""TEXT"",""name"":""Label"",""characters"":""Hi""}}
            }}";
            Dictionary<string, FigmaNode> nodes = FigmaResponses.ParseNodes(json);
            Assert.That(nodes.Count, Is.EqualTo(2));
            Assert.That(nodes["1:2"].Name, Is.EqualTo("Bar"));
            Assert.That(nodes["3:4"].IsText, Is.True);
        }

        [Test]
        public void ParseRenderedImageUrlsSkipsNulls()
        {
            const string json = @"{""err"":null,""images"":{""1:2"":""https://cdn/x.png"",""3:4"":null}}";
            Dictionary<string, string> urls = FigmaResponses.ParseRenderedImageUrls(json);
            Assert.That(urls.Count, Is.EqualTo(1));
            Assert.That(urls["1:2"], Is.EqualTo("https://cdn/x.png"));
        }

        [Test]
        public void ParseImageFillUrlsReadsMeta()
        {
            const string json = @"{""error"":false,""meta"":{""images"":{""abc"":""https://cdn/abc.png""}}}";
            Dictionary<string, string> urls = FigmaResponses.ParseImageFillUrls(json);
            Assert.That(urls["abc"], Is.EqualTo("https://cdn/abc.png"));
        }
    }

    [TestFixture]
    public class FigmaNodeQueryTests
    {
        const string Doc = @"{""id"":""0:0"",""type"":""DOCUMENT"",""children"":[
            {""id"":""0:1"",""type"":""CANVAS"",""name"":""Page"",""children"":[
              {""id"":""1:2"",""type"":""FRAME"",""name"":""Home"",""children"":[
                {""id"":""1:3"",""type"":""TEXT"",""name"":""Title""}]},
              {""id"":""1:4"",""type"":""COMPONENT"",""name"":""Button""}]}]}";

        [Test]
        public void FindByNameDepthFirst()
        {
            FigmaNode doc = FigmaNode.Parse(Doc);
            Assert.That(FigmaNodeQuery.FindByName(doc, "Title").Id, Is.EqualTo("1:3"));
        }

        [Test]
        public void FindById()
        {
            FigmaNode doc = FigmaNode.Parse(Doc);
            Assert.That(FigmaNodeQuery.FindById(doc, "1:4").Name, Is.EqualTo("Button"));
        }

        [Test]
        public void CollectExportableFramesFromDocument()
        {
            FigmaNode doc = FigmaNode.Parse(Doc);
            List<FigmaNode> frames = FigmaNodeQuery.CollectExportableFrames(doc);
            Assert.That(frames.Count, Is.EqualTo(2)); // Home (FRAME) + Button (COMPONENT)
        }

        [Test]
        public void CollectFromSingleNodeReturnsItself()
        {
            FigmaNode frame = FigmaNode.Parse(@"{""id"":""1:2"",""type"":""FRAME"",""name"":""Solo""}");
            List<FigmaNode> frames = FigmaNodeQuery.CollectExportableFrames(frame);
            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(frames[0].Name, Is.EqualTo("Solo"));
        }
    }
}
