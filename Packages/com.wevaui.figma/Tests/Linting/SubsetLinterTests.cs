using NUnit.Framework;
using Weva.Figma.Linting;
using Weva.Figma.Model;

namespace Weva.Figma.Tests.Linting
{
    [TestFixture]
    public class SubsetLinterTests
    {
        static LintReport Lint(string json, LintOptions o = null)
            => SubsetLinter.Lint(FigmaNode.Parse(json), o);

        [Test]
        public void CleanFrameHasNoDiagnostics()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
                ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10},
                ""fills"":[{""type"":""SOLID"",""color"":{""r"":0.1,""g"":0.1,""b"":0.1,""a"":1}}]}");
            Assert.That(r.IsClean, Is.True, r.Format());
        }

        [Test]
        public void VectorReportedAsInfoByDefault()
        {
            LintReport r = Lint(@"{""id"":""v"",""name"":""Icon"",""type"":""VECTOR""}");
            var diags = new System.Collections.Generic.List<LintDiagnostic>(r.WithCode("vector-rasterized"));
            Assert.That(diags.Count, Is.EqualTo(1));
            Assert.That(diags[0].Severity, Is.EqualTo(LintSeverity.Info));
        }

        [Test]
        public void VectorReportedAsWarningWhenConfigured()
        {
            LintReport r = Lint(@"{""id"":""v"",""type"":""VECTOR""}", new LintOptions { RasterAsInfo = false });
            Assert.That(r.WarningCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void BlendModeIsWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""FRAME"",""blendMode"":""MULTIPLY""}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("blend-mode-unsupported")), Is.EqualTo(1));
        }

        [Test]
        public void MultipleVisibleFillsWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""RECTANGLE"",""fills"":[
                {""type"":""SOLID"",""color"":{""r"":1,""g"":0,""b"":0,""a"":1}},
                {""type"":""SOLID"",""color"":{""r"":0,""g"":1,""b"":0,""a"":1}}]}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("multi-fill-flattened")), Is.EqualTo(1));
        }

        [Test]
        public void AngularGradientWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""RECTANGLE"",""fills"":[{""type"":""GRADIENT_ANGULAR"",""gradientStops"":[]}]}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("gradient-type-approximated")), Is.EqualTo(1));
        }

        [Test]
        public void GradientStrokeWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""RECTANGLE"",""strokeWeight"":2,
                ""strokes"":[{""type"":""GRADIENT_LINEAR"",""gradientStops"":[]}]}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("stroke-not-solid")), Is.EqualTo(1));
        }

        [Test]
        public void RotationWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""FRAME"",""rotation"":0.5}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("rotation-not-exported")), Is.EqualTo(1));
        }

        [Test]
        public void MaskWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""FRAME"",""isMask"":true}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("mask-unsupported")), Is.EqualTo(1));
        }

        [Test]
        public void TextMixedStylesWarned()
        {
            LintReport r = Lint(@"{""id"":""t"",""type"":""TEXT"",""characters"":""Hi"",
                ""styleOverrideTable"":{""1"":{""fontWeight"":700}}}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("text-mixed-styles-flattened")), Is.EqualTo(1));
        }

        [Test]
        public void UnsupportedNodeTypeWarned()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""STICKY""}");
            Assert.That(System.Linq.Enumerable.Count(r.WithCode("node-type-unsupported")), Is.EqualTo(1));
        }

        [Test]
        public void WalksChildrenInDocumentOrder()
        {
            LintReport r = Lint(@"{
              ""id"":""root"",""type"":""FRAME"",""layoutMode"":""VERTICAL"",
              ""absoluteBoundingBox"":{""x"":0,""y"":0,""width"":10,""height"":10},
              ""children"":[
                {""id"":""a"",""type"":""FRAME"",""blendMode"":""MULTIPLY""},
                {""id"":""b"",""type"":""VECTOR""}
              ]
            }");
            Assert.That(r.Diagnostics[0].NodeId, Is.EqualTo("a"));
            Assert.That(r.Diagnostics[r.Diagnostics.Count - 1].NodeId, Is.EqualTo("b"));
        }

        [Test]
        public void CountsAreTracked()
        {
            LintReport r = Lint(@"{""id"":""x"",""type"":""FRAME"",""blendMode"":""SCREEN"",""isMask"":true}");
            Assert.That(r.WarningCount, Is.EqualTo(2));
            Assert.That(r.HasWarnings, Is.True);
            Assert.That(r.HasErrors, Is.False);
        }
    }
}
