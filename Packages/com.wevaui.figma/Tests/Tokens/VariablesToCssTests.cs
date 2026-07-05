using NUnit.Framework;
using Weva.Figma.Tokens;

namespace Weva.Figma.Tests.Tokens
{
    [TestFixture]
    public class VariablesToCssTests
    {
        // A single-mode collection with one color, one float (px), one float
        // (opacity), one string, and one alias.
        const string SingleMode = @"{
          ""meta"": {
            ""variableCollections"": {
              ""C"": { ""id"":""C"", ""name"":""Core"", ""defaultModeId"":""m"",
                       ""modes"":[{""modeId"":""m"",""name"":""Value""}],
                       ""variableIds"":[""V1"",""V2"",""V3"",""V4"",""V5""] }
            },
            ""variables"": {
              ""V1"": { ""id"":""V1"", ""name"":""color/bg"", ""resolvedType"":""COLOR"",
                        ""variableCollectionId"":""C"", ""valuesByMode"":{ ""m"":{""r"":0.2,""g"":0.4,""b"":0.6,""a"":1} } },
              ""V2"": { ""id"":""V2"", ""name"":""radius/md"", ""resolvedType"":""FLOAT"", ""scopes"":[""CORNER_RADIUS""],
                        ""variableCollectionId"":""C"", ""valuesByMode"":{ ""m"":16 } },
              ""V3"": { ""id"":""V3"", ""name"":""opacity/disabled"", ""resolvedType"":""FLOAT"", ""scopes"":[""OPACITY""],
                        ""variableCollectionId"":""C"", ""valuesByMode"":{ ""m"":0.5 } },
              ""V4"": { ""id"":""V4"", ""name"":""font/family/base"", ""resolvedType"":""STRING"",
                        ""variableCollectionId"":""C"", ""valuesByMode"":{ ""m"":""Inter"" } },
              ""V5"": { ""id"":""V5"", ""name"":""color/button/bg"", ""resolvedType"":""COLOR"",
                        ""variableCollectionId"":""C"", ""valuesByMode"":{ ""m"":{""type"":""VARIABLE_ALIAS"",""id"":""V1""} } }
            }
          }
        }";

        [Test]
        public void EmitsRootBlockWithColorFloatStringAndAlias()
        {
            TokenCssResult r = VariablesToCss.Build(SingleMode);
            Assert.That(r.Css, Does.Contain(":root {"));
            Assert.That(r.Css, Does.Contain("--color-bg: rgb(51, 102, 153);"));
            Assert.That(r.Css, Does.Contain("--radius-md: 16px;"));
            Assert.That(r.Css, Does.Contain("--opacity-disabled: 0.5;"));
            Assert.That(r.Css, Does.Contain("--font-family-base: Inter;"));
            Assert.That(r.Css, Does.Contain("--color-button-bg: var(--color-bg);"));
        }

        [Test]
        public void FloatUnitsInferFromScope()
        {
            TokenCssResult r = VariablesToCss.Build(SingleMode);
            // CORNER_RADIUS -> px, OPACITY -> unitless.
            Assert.That(r.Css, Does.Contain("--radius-md: 16px;"));
            Assert.That(r.Css, Does.Contain("--opacity-disabled: 0.5;"));
            Assert.That(r.Css, Does.Not.Contain("0.5px"));
        }

        [Test]
        public void OpaqueColorUsesRgbAndAlphaUsesRgba()
        {
            const string json = @"{ ""meta"": { ""variableCollections"": {
                ""C"":{""id"":""C"",""name"":""C"",""defaultModeId"":""m"",""modes"":[{""modeId"":""m"",""name"":""v""}],""variableIds"":[""A"",""B""]} },
              ""variables"": {
                ""A"":{""id"":""A"",""name"":""solid"",""resolvedType"":""COLOR"",""variableCollectionId"":""C"",""valuesByMode"":{""m"":{""r"":1,""g"":0,""b"":0,""a"":1}}},
                ""B"":{""id"":""B"",""name"":""ghost"",""resolvedType"":""COLOR"",""variableCollectionId"":""C"",""valuesByMode"":{""m"":{""r"":0,""g"":0,""b"":0,""a"":0.25}}}
              } } }";
            TokenCssResult r = VariablesToCss.Build(json);
            Assert.That(r.Css, Does.Contain("--solid: rgb(255, 0, 0);"));
            Assert.That(r.Css, Does.Contain("--ghost: rgba(0, 0, 0, 0.25);"));
        }

        [Test]
        public void MultiModeEmitsRootMediaAndThemeBlocks()
        {
            const string json = @"{ ""meta"": {
              ""variableCollections"": {
                ""C1"": { ""id"":""C1"", ""name"":""Theme"", ""defaultModeId"":""m1"",
                          ""modes"":[{""modeId"":""m1"",""name"":""Dark""},{""modeId"":""m2"",""name"":""Light""}],
                          ""variableIds"":[""V1""] } },
              ""variables"": {
                ""V1"": { ""id"":""V1"", ""name"":""color/bg"", ""resolvedType"":""COLOR"", ""variableCollectionId"":""C1"",
                          ""valuesByMode"":{ ""m1"":{""r"":0,""g"":0,""b"":0,""a"":1}, ""m2"":{""r"":1,""g"":1,""b"":1,""a"":1} } } }
            } }";
            TokenCssResult r = VariablesToCss.Build(json);

            // :root carries the default (Dark) mode.
            Assert.That(r.Css, Does.Contain(":root {"));
            Assert.That(r.Css, Does.Contain("--color-bg: rgb(0, 0, 0);"));
            // Light mode is non-default and named "Light" -> prefers-color-scheme.
            Assert.That(r.Css, Does.Contain("@media (prefers-color-scheme: light)"));
            // Explicit per-mode selectors.
            Assert.That(r.Css, Does.Contain("[data-theme=\"dark\"] {"));
            Assert.That(r.Css, Does.Contain("[data-theme=\"light\"] {"));
            // The light value appears.
            Assert.That(r.Css, Does.Contain("--color-bg: rgb(255, 255, 255);"));
        }

        [Test]
        public void ThemeBlocksComeAfterMediaSoTheyWin()
        {
            const string json = @"{ ""meta"": {
              ""variableCollections"": {
                ""C1"": { ""id"":""C1"", ""name"":""Theme"", ""defaultModeId"":""m1"",
                          ""modes"":[{""modeId"":""m1"",""name"":""Dark""},{""modeId"":""m2"",""name"":""Light""}],
                          ""variableIds"":[""V1""] } },
              ""variables"": {
                ""V1"": { ""id"":""V1"", ""name"":""c"", ""resolvedType"":""COLOR"", ""variableCollectionId"":""C1"",
                          ""valuesByMode"":{ ""m1"":{""r"":0,""g"":0,""b"":0,""a"":1}, ""m2"":{""r"":1,""g"":1,""b"":1,""a"":1} } } }
            } }";
            TokenCssResult r = VariablesToCss.Build(json);
            int media = r.Css.IndexOf("@media", System.StringComparison.Ordinal);
            int theme = r.Css.IndexOf("[data-theme=\"light\"]", System.StringComparison.Ordinal);
            Assert.That(media, Is.GreaterThan(0));
            Assert.That(theme, Is.GreaterThan(media), "explicit theme selectors must follow @media to win on source order");
        }

        [Test]
        public void BooleanIsSkippedWithWarning()
        {
            const string json = @"{ ""meta"": {
              ""variableCollections"": { ""C"":{""id"":""C"",""name"":""C"",""defaultModeId"":""m"",""modes"":[{""modeId"":""m"",""name"":""v""}],""variableIds"":[""B""]} },
              ""variables"": { ""B"":{""id"":""B"",""name"":""flag/on"",""resolvedType"":""BOOLEAN"",""variableCollectionId"":""C"",""valuesByMode"":{""m"":true}} }
            } }";
            TokenCssResult r = VariablesToCss.Build(json);
            Assert.That(r.Css, Does.Not.Contain("--flag-on"));
            Assert.That(r.SkippedCount, Is.EqualTo(1));
            Assert.That(r.Warnings.Count, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void CustomPropertyPrefixApplied()
        {
            var opts = new TokenCssOptions { CustomPropertyPrefix = "fig" };
            TokenCssResult r = VariablesToCss.Build(SingleMode, opts);
            Assert.That(r.Css, Does.Contain("--fig-color-bg:"));
            Assert.That(r.Css, Does.Contain("var(--fig-color-bg)"));
        }

        [Test]
        public void OutputIsDeterministicAcrossRuns()
        {
            string a = VariablesToCss.Build(SingleMode).Css;
            string b = VariablesToCss.Build(SingleMode).Css;
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void VariableIdsOrderIsPreserved()
        {
            TokenCssResult r = VariablesToCss.Build(SingleMode);
            int bg = r.Css.IndexOf("--color-bg", System.StringComparison.Ordinal);
            int radius = r.Css.IndexOf("--radius-md", System.StringComparison.Ordinal);
            int family = r.Css.IndexOf("--font-family-base", System.StringComparison.Ordinal);
            Assert.That(bg, Is.LessThan(radius));
            Assert.That(radius, Is.LessThan(family));
        }

        [Test]
        public void FloatUnitOverrideWins()
        {
            var opts = new TokenCssOptions();
            opts.FloatUnitOverrides = new System.Collections.Generic.Dictionary<string, string> { { "radius-md", "rem" } };
            TokenCssResult r = VariablesToCss.Build(SingleMode, opts);
            Assert.That(r.Css, Does.Contain("--radius-md: 16rem;"));
        }
    }
}
