using System.Collections.Generic;
using NUnit.Framework;
using Weva.Figma.Mapping;
using Weva.Figma.Model;
using Weva.Figma.RoundTrip;

namespace Weva.Figma.Tests.RoundTrip
{
    [TestFixture]
    public class OverlayTests
    {
        static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);

        [Test]
        public void JsonRoundTripPreservesFields()
        {
            var overlay = new FigmaOverlay();
            var n = new NodeOverride { Tag = "button", Id = "play", Text = "{{ Label }}" };
            n.Attributes.Add(KV("on-click", "OnPlay"));
            n.Attributes.Add(KV("aria-label", "Play"));
            overlay.ByFigmaId["1:2"] = n;

            FigmaOverlay back = FigmaOverlay.Parse(overlay.ToJson());
            Assert.That(back.TryGet("1:2", out NodeOverride o), Is.True);
            Assert.That(o.Tag, Is.EqualTo("button"));
            Assert.That(o.Id, Is.EqualTo("play"));
            Assert.That(o.Text, Is.EqualTo("{{ Label }}"));
            Assert.That(o.Attributes.Count, Is.EqualTo(2));
        }

        [Test]
        public void ToJsonIsDeterministic()
        {
            var overlay = new FigmaOverlay();
            var n = new NodeOverride();
            n.Attributes.Add(KV("on-click", "A"));
            overlay.ByFigmaId["z"] = n;
            overlay.ByFigmaId["a"] = new NodeOverride { Id = "x" };
            Assert.That(overlay.ToJson(), Is.EqualTo(overlay.ToJson()));
            // 'a' sorts before 'z'
            Assert.That(overlay.ToJson().IndexOf("\"a\"", System.StringComparison.Ordinal),
                Is.LessThan(overlay.ToJson().IndexOf("\"z\"", System.StringComparison.Ordinal)));
        }

        static ExportedDocument Export(string json, FigmaOverlay overlay)
            => FigmaDocumentExporter.Export(FigmaNode.Parse(json), new ExportOptions { Overlay = overlay });

        [Test]
        public void OverlayAppliedDuringExport()
        {
            var overlay = new FigmaOverlay();
            var o = new NodeOverride { Tag = "button", Id = "play" };
            o.Attributes.Add(KV("on-click", "OnPlay"));
            overlay.ByFigmaId["b"] = o;

            ExportedDocument d = Export(@"{""id"":""b"",""type"":""FRAME"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}", overlay);
            Assert.That(d.Html, Does.Contain("<button class=\"frame\" id=\"play\" data-figma-id=\"b\" on-click=\"OnPlay\">"));
        }

        [Test]
        public void OverlayOverridesAnnotationWithoutDuplicateAttribute()
        {
            var overlay = new FigmaOverlay();
            var o = new NodeOverride();
            o.Attributes.Add(KV("on-click", "NewHandler"));
            overlay.ByFigmaId["b"] = o;

            ExportedDocument d = Export(@"{""id"":""b"",""name"":""Btn @click=OldHandler"",""type"":""FRAME"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}", overlay);
            Assert.That(d.Html, Does.Contain("on-click=\"NewHandler\""));
            Assert.That(d.Html, Does.Not.Contain("OldHandler"));
            // exactly one on-click attribute
            Assert.That(d.Html.IndexOf("on-click", System.StringComparison.Ordinal),
                Is.EqualTo(d.Html.LastIndexOf("on-click", System.StringComparison.Ordinal)));
        }

        [Test]
        public void ExtractCapturesDynamicLayer()
        {
            const string html =
                "<button class=\"play\" id=\"p\" data-figma-id=\"b\" on-click=\"OnPlay\" aria-label=\"Play\"></button>\n" +
                "<span class=\"label\" data-figma-id=\"t\">{{ Label }}</span>\n" +
                "<div class=\"plain\" data-figma-id=\"d\">Static text</div>\n";
            FigmaOverlay overlay = OverlayExtractor.Extract(html);

            Assert.That(overlay.TryGet("b", out NodeOverride b), Is.True);
            Assert.That(b.Tag, Is.EqualTo("button"));
            Assert.That(b.Id, Is.EqualTo("p"));
            Assert.That(Get(b, "on-click"), Is.EqualTo("OnPlay"));
            Assert.That(Get(b, "aria-label"), Is.EqualTo("Play"));

            Assert.That(overlay.TryGet("t", out NodeOverride t), Is.True);
            Assert.That(t.Text, Is.EqualTo("{{ Label }}"));

            // A plain div with only static text contributes nothing.
            Assert.That(overlay.TryGet("d", out _), Is.False);
        }

        [Test]
        public void ExtractThenReapplyReproducesDynamicAttributes()
        {
            // Annotated design → HTML.
            ExportedDocument first = FigmaDocumentExporter.Export(FigmaNode.Parse(
                @"{""id"":""b"",""name"":""Play <button> @click=OnPlay #play"",""type"":""FRAME"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}"));

            // Capture the dynamic layer, then re-export a *plain* version of the same node.
            FigmaOverlay overlay = OverlayExtractor.Extract(first.Html);
            ExportedDocument second = Export(@"{""id"":""b"",""name"":""Renamed"",""type"":""FRAME"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}", overlay);

            Assert.That(second.Html, Does.Contain("<button"));
            Assert.That(second.Html, Does.Contain("id=\"play\""));
            Assert.That(second.Html, Does.Contain("on-click=\"OnPlay\""));
        }

        static string Get(NodeOverride o, string name)
        {
            foreach (var a in o.Attributes) if (a.Key == name) return a.Value;
            return null;
        }
    }
}
