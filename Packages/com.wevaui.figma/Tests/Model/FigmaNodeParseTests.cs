using NUnit.Framework;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Model
{
    [TestFixture]
    public class FigmaNodeParseTests
    {
        const string Frame = @"{
          ""id"":""1:1"", ""name"":""Bar"", ""type"":""FRAME"",
          ""layoutMode"":""HORIZONTAL"", ""primaryAxisAlignItems"":""CENTER"", ""counterAxisAlignItems"":""CENTER"",
          ""itemSpacing"":8, ""paddingLeft"":12, ""paddingTop"":6,
          ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":200,""height"":40},
          ""cornerRadius"":8,
          ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.1,""g"":0.1,""b"":0.1,""a"":1}}],
          ""children"":[
            {""id"":""1:2"",""name"":""Label"",""type"":""TEXT"",""characters"":""Hi"",
             ""style"":{""fontFamily"":""Inter"",""fontSize"":18,""fontWeight"":700},
             ""fills"":[{""type"":""SOLID"",""color"":{""r"":1,""g"":1,""b"":1,""a"":1}}]}
          ]
        }";

        [Test]
        public void ParsesCommonFieldsAndAutoLayout()
        {
            FigmaNode n = FigmaNode.Parse(Frame);
            Assert.That(n.Id, Is.EqualTo("1:1"));
            Assert.That(n.Type, Is.EqualTo("FRAME"));
            Assert.That(n.IsAutoLayout, Is.True);
            Assert.That(n.LayoutMode, Is.EqualTo("HORIZONTAL"));
            Assert.That(n.ItemSpacing, Is.EqualTo(8).Within(1e-9));
            Assert.That(n.PaddingLeft, Is.EqualTo(12).Within(1e-9));
            Assert.That(n.Box.Width, Is.EqualTo(200).Within(1e-9));
            Assert.That(n.CornerRadius, Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void ParsesFillsList()
        {
            FigmaNode n = FigmaNode.Parse(Frame);
            FigmaPaint fill = FigmaNode.FirstVisible(n.Fills);
            Assert.That(fill, Is.Not.Null);
            Assert.That(fill.IsSolid, Is.True);
            Assert.That(fill.Color.R, Is.EqualTo(0.1).Within(1e-9));
        }

        [Test]
        public void ParsesChildrenAndSetsParent()
        {
            FigmaNode n = FigmaNode.Parse(Frame);
            Assert.That(n.HasChildren, Is.True);
            FigmaNode label = n.Children[0];
            Assert.That(label.IsText, Is.True);
            Assert.That(label.Characters, Is.EqualTo("Hi"));
            Assert.That(label.Parent, Is.SameAs(n));
        }

        [Test]
        public void ParsesTextStyle()
        {
            FigmaNode n = FigmaNode.Parse(Frame);
            FigmaTextStyle t = n.Children[0].TextStyle;
            Assert.That(t, Is.Not.Null);
            Assert.That(t.FontFamily, Is.EqualTo("Inter"));
            Assert.That(t.FontSize, Is.EqualTo(18).Within(1e-9));
            Assert.That(t.FontWeight, Is.EqualTo(700).Within(1e-9));
        }

        [Test]
        public void ParsesRectangleCornerRadii()
        {
            FigmaNode n = FigmaNode.Parse(@"{""id"":""x"",""type"":""RECTANGLE"",""rectangleCornerRadii"":[4,8,12,16]}");
            Assert.That(n.RectangleCornerRadii, Is.Not.Null);
            Assert.That(n.RectangleCornerRadii[0], Is.EqualTo(4).Within(1e-9));
            Assert.That(n.RectangleCornerRadii[3], Is.EqualTo(16).Within(1e-9));
        }

        [Test]
        public void DefaultsVisibleTrueAndOpacityOne()
        {
            FigmaNode n = FigmaNode.Parse(@"{""id"":""x"",""type"":""FRAME""}");
            Assert.That(n.Visible, Is.True);
            Assert.That(n.Opacity, Is.EqualTo(1).Within(1e-9));
            Assert.That(n.IsAutoLayout, Is.False);
        }
    }
}
