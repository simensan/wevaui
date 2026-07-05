using NUnit.Framework;
using Weva.Figma.Mapping;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Mapping
{
    [TestFixture]
    public class AnnotationExportTests
    {
        static ExportedDocument Export(string json)
            => FigmaDocumentExporter.Export(FigmaNode.Parse(json));

        [Test]
        public void TextBindingReplacesCharacters()
        {
            ExportedDocument d = Export(@"{""id"":""t"",""name"":""Coins {{ CoinCount }}"",""type"":""TEXT"",""characters"":""3,840""}");
            Assert.That(d.Html, Does.Contain(">{{ CoinCount }}</span>"));
            Assert.That(d.Html, Does.Not.Contain("3,840"));
            Assert.That(d.Html, Does.Contain("class=\"coins\""));
        }

        [Test]
        public void TagOverrideIdAndEvent()
        {
            ExportedDocument d = Export(@"{""id"":""b"",""name"":""Play <button> @click=OnPlay #play-btn"",""type"":""FRAME"",
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}");
            Assert.That(d.Html, Does.Contain("<button class=\"play\" id=\"play-btn\" data-figma-id=\"b\" on-click=\"OnPlay\">"));
        }

        [Test]
        public void ClassToggleEmitsDataClassAttribute()
        {
            ExportedDocument d = Export(@"{""id"":""c"",""name"":""Card .selected?IsSelected"",""type"":""FRAME"",
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}");
            Assert.That(d.Html, Does.Contain("data-class-selected=\"IsSelected\""));
            Assert.That(d.Html, Does.Contain("class=\"card\""));
        }

        const string StageList = @"{
          ""id"":""list"",""name"":""Stages *each=Stages:stage"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
          ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":300},
          ""children"":[
            {""id"":""c1"",""name"":""Card"",""type"":""FRAME"",""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""HUG"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":80}},
            {""id"":""c2"",""name"":""Card"",""type"":""FRAME"",""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""HUG"",""absoluteBoundingBox"":{""x"":0,""y"":90,""width"":100,""height"":80}},
            {""id"":""c3"",""name"":""Card"",""type"":""FRAME"",""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""HUG"",""absoluteBoundingBox"":{""x"":0,""y"":180,""width"":100,""height"":80}}
          ]
        }";

        [Test]
        public void EachWrapsFirstChildInTemplateAndDropsDuplicates()
        {
            ExportedDocument d = Export(StageList);
            Assert.That(d.Html, Does.Contain("<template data-each=\"Stages as stage\">"));
            Assert.That(d.Html, Does.Contain("data-figma-id=\"c1\""));
            Assert.That(d.Html, Does.Not.Contain("data-figma-id=\"c2\""));
            Assert.That(d.Html, Does.Not.Contain("data-figma-id=\"c3\""));
        }

        [Test]
        public void EachEmitsSinglePrototypeCssRule()
        {
            ExportedDocument d = Export(StageList);
            // Only one ".card" rule — duplicates were dropped, so no ".card-2".
            Assert.That(d.Css, Does.Contain(".card {"));
            Assert.That(d.Css, Does.Not.Contain(".card-2"));
        }

        [Test]
        public void EachWithKeyEmitsDataKey()
        {
            ExportedDocument d = Export(@"{
              ""id"":""list"",""name"":""Rows *each=Leaderboard:row:Rank"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
              ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":100},
              ""children"":[{""id"":""r"",""name"":""Row"",""type"":""FRAME"",""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""HUG"",""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":20}}]
            }");
            Assert.That(d.Html, Does.Contain("data-each=\"Leaderboard as row\" data-key=\"Rank\""));
        }
    }
}
