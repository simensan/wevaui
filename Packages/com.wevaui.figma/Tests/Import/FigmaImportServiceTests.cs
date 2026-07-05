using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Figma.Client;
using Weva.Figma.Import;

namespace Weva.Figma.Tests.Import
{
    [TestFixture]
    public class FigmaImportServiceTests
    {
        sealed class FakeHttp : IFigmaHttp
        {
            public string FileJson, NodesJson, VariablesJson, ImageFillsJson, RenderJson;
            public readonly Dictionary<string, byte[]> Cdn = new Dictionary<string, byte[]>();

            public bool TryGetString(string url, string token, out string body, out string error)
            {
                error = null;
                if (url.Contains("/nodes?")) return Give(NodesJson, out body, out error);
                if (url.Contains("/variables/local")) return Give(VariablesJson, out body, out error);
                if (url.Contains("/v1/images/")) return Give(RenderJson, out body, out error);
                if (url.EndsWith("/images")) return Give(ImageFillsJson, out body, out error);
                return Give(FileJson, out body, out error);
            }

            public bool TryGetBytes(string url, string token, out byte[] data, out string error)
            {
                error = null;
                foreach (var kv in Cdn)
                    if (url.Contains(kv.Key)) { data = kv.Value; return true; }
                data = null; error = "404"; return false;
            }

            static bool Give(string s, out string body, out string error)
            {
                if (s != null) { body = s; error = null; return true; }
                body = null; error = "missing"; return false;
            }
        }

        sealed class FakeSink : IExportSink
        {
            public readonly Dictionary<string, string> Texts = new Dictionary<string, string>();
            public readonly Dictionary<string, byte[]> Bytes = new Dictionary<string, byte[]>();
            public void WriteText(string p, string t) => Texts[p] = t;
            public void WriteBytes(string p, byte[] d) => Bytes[p] = d;
        }

        [Test]
        public void ImportsByNodeIdAndWritesHtmlAndCss()
        {
            var http = new FakeHttp
            {
                NodesJson = @"{""nodes"":{""1:2"":{""document"":{""id"":""1:2"",""name"":""Card"",""type"":""FRAME"",
                    ""layoutMode"":""VERTICAL"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":50},
                    ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.1,""g"":0.1,""b"":0.1,""a"":1}}]}}}}",
            };
            var sink = new FakeSink();
            var req = new FigmaImportRequest { FileKey = "K", NodeId = "1:2", OutputName = "card" };

            FigmaImportResult r = FigmaImportService.Import(req, http, sink, "tok");

            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.WrittenFiles, Does.Contain("card.html"));
            Assert.That(r.WrittenFiles, Does.Contain("card.css"));
            Assert.That(sink.Texts["card.html"], Does.Contain("<!DOCTYPE html>"));
            Assert.That(sink.Texts["card.html"], Does.Contain("href=\"card.css\""));
            Assert.That(sink.Texts["card.html"], Does.Contain("class=\"card\""));
            Assert.That(sink.Texts["card.css"], Does.Contain(".card {"));
            Assert.That(r.Lint, Is.Not.Null);
        }

        [Test]
        public void ImportsTokensWhenVariablesAvailableAndLinksFirst()
        {
            var http = new FakeHttp
            {
                NodesJson = @"{""nodes"":{""1:2"":{""document"":{""id"":""1:2"",""name"":""Card"",""type"":""FRAME"",
                    ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}}}}",
                VariablesJson = @"{""meta"":{""variableCollections"":{""C"":{""id"":""C"",""name"":""C"",""defaultModeId"":""m"",
                    ""modes"":[{""modeId"":""m"",""name"":""v""}],""variableIds"":[""V""]}},
                    ""variables"":{""V"":{""id"":""V"",""name"":""color/bg"",""resolvedType"":""COLOR"",""variableCollectionId"":""C"",
                    ""valuesByMode"":{""m"":{""r"":0,""g"":0,""b"":0,""a"":1}}}}}}",
            };
            var sink = new FakeSink();
            var req = new FigmaImportRequest { FileKey = "K", NodeId = "1:2", OutputName = "card" };

            FigmaImportResult r = FigmaImportService.Import(req, http, sink, "tok");

            Assert.That(r.WrittenFiles, Does.Contain("tokens.css"));
            Assert.That(sink.Texts["tokens.css"], Does.Contain("--color-bg:"));
            // tokens.css must be linked before card.css so it cascades first
            string html = sink.Texts["card.html"];
            Assert.That(html.IndexOf("tokens.css"), Is.LessThan(html.IndexOf("card.css")));
        }

        [Test]
        public void DownloadsImageFillBitmaps()
        {
            var http = new FakeHttp
            {
                NodesJson = @"{""nodes"":{""1:2"":{""document"":{""id"":""1:2"",""name"":""Hero"",""type"":""FRAME"",
                    ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":100},
                    ""children"":[{""id"":""1:3"",""name"":""Pic"",""type"":""RECTANGLE"",
                      ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":100},
                      ""fills"":[{""type"":""IMAGE"",""imageRef"":""abc"",""scaleMode"":""FILL""}]}]}}}}",
                ImageFillsJson = @"{""meta"":{""images"":{""abc"":""https://cdn.figma/abc.png""}}}",
            };
            http.Cdn["abc.png"] = new byte[] { 1, 2, 3, 4 };
            var sink = new FakeSink();
            var req = new FigmaImportRequest { FileKey = "K", NodeId = "1:2", OutputName = "hero" };

            FigmaImportResult r = FigmaImportService.Import(req, http, sink, "tok");

            Assert.That(sink.Bytes.ContainsKey("images/abc.png"), Is.True);
            Assert.That(sink.Bytes["images/abc.png"].Length, Is.EqualTo(4));
            Assert.That(r.WrittenFiles, Does.Contain("images/abc.png"));
        }

        [Test]
        public void MissingNodeReturnsError()
        {
            var http = new FakeHttp { NodesJson = @"{""nodes"":{}}" };
            var req = new FigmaImportRequest { FileKey = "K", NodeId = "9:9" };
            FigmaImportResult r = FigmaImportService.Import(req, http, new FakeSink(), "tok");
            Assert.That(r.Success, Is.False);
            Assert.That(r.Error, Does.Contain("9:9"));
        }

        [Test]
        public void WholeFileImportPicksFirstFrame()
        {
            var http = new FakeHttp
            {
                FileJson = @"{""name"":""F"",""document"":{""id"":""0:0"",""type"":""DOCUMENT"",""children"":[
                    {""id"":""0:1"",""type"":""CANVAS"",""name"":""Page"",""children"":[
                      {""id"":""1:2"",""type"":""FRAME"",""name"":""First"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}]}]}}",
            };
            var sink = new FakeSink();
            var req = new FigmaImportRequest { FileKey = "K", NodeId = null, OutputName = "out", ImportTokens = false };

            FigmaImportResult r = FigmaImportService.Import(req, http, sink, "tok");

            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(sink.Texts["out.html"], Does.Contain("class=\"first\""));
        }

        [Test]
        public void ImportLocalWritesFilesAndTokensWithoutHttp()
        {
            Weva.Figma.Model.FigmaNode node = Weva.Figma.Model.FigmaNode.Parse(
                @"{""id"":""1:2"",""name"":""Card"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
                  ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}");
            string vars = @"{""meta"":{""variableCollections"":{""C"":{""id"":""C"",""name"":""C"",""defaultModeId"":""m"",
                ""modes"":[{""modeId"":""m"",""name"":""v""}],""variableIds"":[""V""]}},
                ""variables"":{""V"":{""id"":""V"",""name"":""color/bg"",""resolvedType"":""COLOR"",""variableCollectionId"":""C"",
                ""valuesByMode"":{""m"":{""r"":0,""g"":0,""b"":0,""a"":1}}}}}}";
            var sink = new FakeSink();
            var req = new FigmaImportRequest { OutputName = "card", ImportTokens = true };

            FigmaImportResult r = FigmaImportService.ImportLocal(node, vars, req, sink);

            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(sink.Texts.ContainsKey("card.html"), Is.True);
            Assert.That(sink.Texts.ContainsKey("card.css"), Is.True);
            Assert.That(sink.Texts["tokens.css"], Does.Contain("--color-bg:"));
        }
    }
}
