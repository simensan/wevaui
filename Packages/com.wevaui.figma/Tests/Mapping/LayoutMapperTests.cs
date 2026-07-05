using NUnit.Framework;
using Weva.Figma.Mapping;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Mapping
{
    [TestFixture]
    public class LayoutMapperTests
    {
        static CssBlock Map(FigmaNode n)
        {
            var b = new CssBlock();
            LayoutMapper.Apply(n, b);
            return b;
        }

        const string Bar = @"{
          ""id"":""1:1"", ""name"":""Bar"", ""type"":""FRAME"",
          ""layoutMode"":""HORIZONTAL"", ""primaryAxisAlignItems"":""CENTER"", ""counterAxisAlignItems"":""CENTER"",
          ""itemSpacing"":8, ""paddingLeft"":12, ""paddingRight"":12, ""paddingTop"":6, ""paddingBottom"":6,
          ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":200,""height"":40},
          ""children"":[
            {""id"":""1:2"",""name"":""Grow"",""type"":""FRAME"",""layoutSizingHorizontal"":""FILL"",""layoutSizingVertical"":""FIXED"",
             ""absoluteBoundingBox"":{""x"":12,""y"":6,""width"":100,""height"":28}},
            {""id"":""1:3"",""name"":""Fixed"",""type"":""FRAME"",""layoutSizingHorizontal"":""FIXED"",""layoutSizingVertical"":""FIXED"",
             ""absoluteBoundingBox"":{""x"":120,""y"":6,""width"":68,""height"":28}}
          ]
        }";

        [Test]
        public void AutoLayoutRowMapsToFlex()
        {
            CssBlock b = Map(FigmaNode.Parse(Bar));
            Assert.That(b.TryGet("display"), Is.EqualTo("flex"));
            Assert.That(b.TryGet("flex-direction"), Is.EqualTo("row"));
            Assert.That(b.TryGet("justify-content"), Is.EqualTo("center"));
            Assert.That(b.TryGet("align-items"), Is.EqualTo("center"));
            Assert.That(b.TryGet("gap"), Is.EqualTo("8px"));
            Assert.That(b.TryGet("padding"), Is.EqualTo("6px 12px"));
            Assert.That(b.TryGet("width"), Is.EqualTo("200px")); // root keeps size
        }

        [Test]
        public void FillChildGetsGrowAndCrossFixedHeight()
        {
            FigmaNode grow = FigmaNode.Parse(Bar).Children[0];
            CssBlock b = Map(grow);
            Assert.That(b.TryGet("flex-grow"), Is.EqualTo("1"));
            Assert.That(b.TryGet("flex-basis"), Is.EqualTo("0%"));
            Assert.That(b.TryGet("min-width"), Is.EqualTo("0"));
            Assert.That(b.TryGet("height"), Is.EqualTo("28px"));
            Assert.That(b.Has("width"), Is.False);
        }

        [Test]
        public void FixedChildGetsExplicitSize()
        {
            FigmaNode fixedChild = FigmaNode.Parse(Bar).Children[1];
            CssBlock b = Map(fixedChild);
            Assert.That(b.TryGet("width"), Is.EqualTo("68px"));
            Assert.That(b.TryGet("height"), Is.EqualTo("28px"));
            Assert.That(b.Has("flex-grow"), Is.False);
        }

        [Test]
        public void VerticalAutoLayoutMapsToColumn()
        {
            CssBlock b = Map(FigmaNode.Parse(@"{""id"":""x"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}"));
            Assert.That(b.TryGet("flex-direction"), Is.EqualTo("column"));
        }

        [Test]
        public void SpaceBetweenSuppressesGap()
        {
            CssBlock b = Map(FigmaNode.Parse(@"{""id"":""x"",""type"":""FRAME"",""layoutMode"":""HORIZONTAL"",
                ""primaryAxisAlignItems"":""SPACE_BETWEEN"",""itemSpacing"":8,
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10}}"));
            Assert.That(b.TryGet("justify-content"), Is.EqualTo("space-between"));
            Assert.That(b.Has("gap"), Is.False);
        }

        [Test]
        public void NonAutoLayoutParentPositionsChildAbsolutely()
        {
            const string canvas = @"{
              ""id"":""2:1"",""name"":""Canvas"",""type"":""FRAME"",
              ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":300,""height"":200},
              ""children"":[
                {""id"":""2:2"",""name"":""Badge"",""type"":""FRAME"",
                 ""absoluteBoundingBox"":{""x"":250,""y"":10,""width"":40,""height"":40},
                 ""constraints"":{""horizontal"":""RIGHT"",""vertical"":""TOP""}}
              ]
            }";
            FigmaNode root = FigmaNode.Parse(canvas);
            CssBlock parent = Map(root);
            Assert.That(parent.TryGet("position"), Is.EqualTo("relative"));

            CssBlock badge = Map(root.Children[0]);
            Assert.That(badge.TryGet("position"), Is.EqualTo("absolute"));
            Assert.That(badge.TryGet("right"), Is.EqualTo("10px"));   // 300 - (250+40)
            Assert.That(badge.TryGet("top"), Is.EqualTo("10px"));
            Assert.That(badge.TryGet("width"), Is.EqualTo("40px"));
            Assert.That(badge.Has("left"), Is.False);                  // RIGHT constraint pins the right edge
        }

        [Test]
        public void AbsolutePositioningInsideAutoLayoutRespectsOptOut()
        {
            const string json = @"{
              ""id"":""a"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
              ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":100,""height"":100},
              ""children"":[
                {""id"":""b"",""type"":""FRAME"",""layoutPositioning"":""ABSOLUTE"",
                 ""absoluteBoundingBox"":{""x"":10,""y"":20,""width"":30,""height"":30},
                 ""constraints"":{""horizontal"":""LEFT"",""vertical"":""TOP""}}
              ]
            }";
            FigmaNode root = FigmaNode.Parse(json);
            Assert.That(Map(root).TryGet("position"), Is.EqualTo("relative")); // needs a containing block
            CssBlock child = Map(root.Children[0]);
            Assert.That(child.TryGet("position"), Is.EqualTo("absolute"));
            Assert.That(child.TryGet("left"), Is.EqualTo("10px"));
            Assert.That(child.TryGet("top"), Is.EqualTo("20px"));
        }
    }
}
