using NUnit.Framework;
using Weva.Figma.Mapping;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Demo
{
    /// <summary>
    /// Guards the shipped sample (Samples~/FigmaImportDemo/hero-card.figma.json).
    /// The JSON here is kept identical to that file; this asserts the generated
    /// document so the demo can't silently drift as the mappers evolve.
    /// </summary>
    [TestFixture]
    public class HeroCardDemoTests
    {
        const string HeroCard = @"{
          ""id"":""10:0"",""name"":""Hero Card"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
          ""primaryAxisAlignItems"":""MIN"",""counterAxisAlignItems"":""CENTER"",""itemSpacing"":12,
          ""paddingLeft"":20,""paddingRight"":20,""paddingTop"":20,""paddingBottom"":20,
          ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":280,""height"":180},""cornerRadius"":16,
          ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.11,""g"":0.14,""b"":0.21,""a"":1}}],
          ""children"":[
            {""id"":""10:1"",""name"":""Title {{ HeroName }}"",""type"":""TEXT"",""characters"":""Aptus"",
             ""layoutSizingHorizontal"":""HUG"",""layoutSizingVertical"":""HUG"",
             ""style"":{""fontFamily"":""Inter"",""fontSize"":24,""fontWeight"":700,""textAlignHorizontal"":""CENTER""},
             ""fills"":[{""type"":""SOLID"",""color"":{""r"":1,""g"":1,""b"":1,""a"":1}}]},
            {""id"":""10:2"",""name"":""Subtitle"",""type"":""TEXT"",""characters"":""Solar Warden"",
             ""layoutSizingHorizontal"":""HUG"",""layoutSizingVertical"":""HUG"",
             ""style"":{""fontFamily"":""Inter"",""fontSize"":13,""textCase"":""UPPER"",""letterSpacing"":1},
             ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.6,""g"":0.64,""b"":0.7,""a"":1}}]},
            {""id"":""10:3"",""name"":""Play <button> @click=OnPlay #play"",""type"":""FRAME"",""layoutMode"":""HORIZONTAL"",
             ""primaryAxisAlignItems"":""CENTER"",""counterAxisAlignItems"":""CENTER"",
             ""paddingLeft"":24,""paddingRight"":24,""paddingTop"":10,""paddingBottom"":10,
             ""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""HUG"",""cornerRadius"":8,
             ""absoluteBoundingBox"":{""x"":20,""y"":120,""width"":240,""height"":40},
             ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.13,""g"":0.77,""b"":0.37,""a"":1}}],
             ""children"":[
               {""id"":""10:4"",""name"":""Label"",""type"":""TEXT"",""characters"":""PLAY"",
                ""layoutSizingHorizontal"":""HUG"",""layoutSizingVertical"":""HUG"",
                ""style"":{""fontFamily"":""Inter"",""fontSize"":16,""fontWeight"":900,""letterSpacing"":2},
                ""fills"":[{""type"":""SOLID"",""color"":{""r"":1,""g"":1,""b"":1,""a"":1}}]}
             ]}
          ]
        }";

        static ExportedDocument Export() => FigmaDocumentExporter.Export(FigmaNode.Parse(HeroCard));

        [Test]
        public void RootCardIsAColumnFlexContainer()
        {
            string css = Export().Css;
            Assert.That(css, Does.Contain(".hero-card {"));
            Assert.That(css, Does.Contain("display: flex;"));
            Assert.That(css, Does.Contain("flex-direction: column;"));
            Assert.That(css, Does.Contain("gap: 12px;"));
            Assert.That(css, Does.Contain("padding: 20px;"));
            Assert.That(css, Does.Contain("border-radius: 16px;"));
            Assert.That(css, Does.Contain("background-color: rgb(28, 36, 54);"));
        }

        [Test]
        public void TitleIsBoundText()
        {
            Assert.That(Export().Html, Does.Contain(">{{ HeroName }}</span>"));
        }

        [Test]
        public void SubtitleIsUppercased()
        {
            string css = Export().Css;
            Assert.That(css, Does.Contain(".subtitle {"));
            Assert.That(css, Does.Contain("text-transform: uppercase;"));
            Assert.That(Export().Html, Does.Contain(">Solar Warden</span>"));
        }

        [Test]
        public void PlayButtonHasTagIdEventAndStretches()
        {
            ExportedDocument d = Export();
            Assert.That(d.Html, Does.Contain("<button class=\"play\" id=\"play\" data-figma-id=\"10:3\" on-click=\"OnPlay\">"));
            Assert.That(d.Html, Does.Contain(">PLAY</span>"));
            // FILL on the horizontal (cross) axis of a column → align-self: stretch
            Assert.That(d.Css, Does.Contain(".play {"));
            Assert.That(d.Css, Does.Contain("align-self: stretch;"));
        }
    }
}
