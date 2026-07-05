using NUnit.Framework;
using Weva.Figma.Mapping;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Mapping
{
    [TestFixture]
    public class StyleMapperTests
    {
        static CssBlock Map(string json)
        {
            var b = new CssBlock();
            StyleMapper.Apply(FigmaNode.Parse(json), b);
            return b;
        }

        [Test]
        public void SolidFillBecomesBackgroundColor()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""fills"":[{""type"":""SOLID"",""color"":{""r"":1,""g"":0,""b"":0,""a"":1}}]}");
            Assert.That(b.TryGet("background-color"), Is.EqualTo("rgb(255, 0, 0)"));
        }

        [Test]
        public void PaintOpacityFoldsIntoAlpha()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""fills"":[{""type"":""SOLID"",""opacity"":0.5,""color"":{""r"":0,""g"":0,""b"":0,""a"":1}}]}");
            Assert.That(b.TryGet("background-color"), Is.EqualTo("rgba(0, 0, 0, 0.5)"));
        }

        [Test]
        public void LinearGradientComputesAngleAndStops()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""fills"":[{
                ""type"":""GRADIENT_LINEAR"",
                ""gradientHandlePositions"":[{""x"":0,""y"":0},{""x"":0,""y"":1}],
                ""gradientStops"":[{""position"":0,""color"":{""r"":0,""g"":0,""b"":0,""a"":1}},{""position"":1,""color"":{""r"":1,""g"":1,""b"":1,""a"":1}}]
            }]}");
            Assert.That(b.TryGet("background-image"),
                Is.EqualTo("linear-gradient(180deg, rgb(0, 0, 0) 0%, rgb(255, 255, 255) 100%)"));
        }

        [Test]
        public void StrokeBecomesBorderShorthand()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""strokeWeight"":2,""strokes"":[{""type"":""SOLID"",""color"":{""r"":0,""g"":0,""b"":0,""a"":1}}]}");
            Assert.That(b.TryGet("border"), Is.EqualTo("2px solid rgb(0, 0, 0)"));
        }

        [Test]
        public void UniformCornerRadius()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""cornerRadius"":8}");
            Assert.That(b.TryGet("border-radius"), Is.EqualTo("8px"));
        }

        [Test]
        public void PerCornerRadius()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""rectangleCornerRadii"":[4,8,12,16]}");
            Assert.That(b.TryGet("border-radius"), Is.EqualTo("4px 8px 12px 16px"));
        }

        [Test]
        public void DropShadowBecomesBoxShadow()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""effects"":[{
                ""type"":""DROP_SHADOW"",""offset"":{""x"":0,""y"":4},""radius"":8,""color"":{""r"":0,""g"":0,""b"":0,""a"":0.5}}]}");
            Assert.That(b.TryGet("box-shadow"), Is.EqualTo("0px 4px 8px rgba(0, 0, 0, 0.5)"));
        }

        [Test]
        public void InnerShadowGetsInsetAndSpread()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""RECTANGLE"",""effects"":[{
                ""type"":""INNER_SHADOW"",""offset"":{""x"":1,""y"":2},""radius"":3,""spread"":4,""color"":{""r"":0,""g"":0,""b"":0,""a"":1}}]}");
            Assert.That(b.TryGet("box-shadow"), Is.EqualTo("inset 1px 2px 3px 4px rgb(0, 0, 0)"));
        }

        [Test]
        public void BackgroundBlurBecomesBackdropFilter()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""FRAME"",""effects"":[{""type"":""BACKGROUND_BLUR"",""radius"":12}]}");
            Assert.That(b.TryGet("backdrop-filter"), Is.EqualTo("blur(12px)"));
        }

        [Test]
        public void OpacityEmitted()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""FRAME"",""opacity"":0.85}");
            Assert.That(b.TryGet("opacity"), Is.EqualTo("0.85"));
        }

        [Test]
        public void TextNodeMapsColorAndTypography()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""TEXT"",""characters"":""Hi"",
                ""fills"":[{""type"":""SOLID"",""color"":{""r"":1,""g"":1,""b"":1,""a"":1}}],
                ""style"":{""fontFamily"":""Inter"",""fontSize"":18,""fontWeight"":700,""textAlignHorizontal"":""CENTER"",""textCase"":""UPPER""}}");
            Assert.That(b.TryGet("color"), Is.EqualTo("rgb(255, 255, 255)"));
            Assert.That(b.TryGet("font-family"), Is.EqualTo("Inter"));
            Assert.That(b.TryGet("font-size"), Is.EqualTo("18px"));
            Assert.That(b.TryGet("font-weight"), Is.EqualTo("700"));
            Assert.That(b.TryGet("text-align"), Is.EqualTo("center"));
            Assert.That(b.TryGet("text-transform"), Is.EqualTo("uppercase"));
        }

        [Test]
        public void EllipseBecomesFullyRounded()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""ELLIPSE"",""cornerRadius"":4}");
            Assert.That(b.TryGet("border-radius"), Is.EqualTo("50%"));
        }

        [Test]
        public void ClipsContentBecomesOverflowHidden()
        {
            CssBlock b = Map(@"{""id"":""x"",""type"":""FRAME"",""clipsContent"":true}");
            Assert.That(b.TryGet("overflow"), Is.EqualTo("hidden"));
        }
    }
}
