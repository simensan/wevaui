using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Cascade.Shorthands;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class FontShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new FontShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Size_and_family_only_resets_other_longhands_to_initial() {
            var d = Expand("16px Arial");
            Assert.That(d["font-size"], Is.EqualTo("16px"));
            Assert.That(d["font-family"], Is.EqualTo("Arial"));
            Assert.That(d["font-style"], Is.EqualTo("normal"));
            Assert.That(d["font-variant"], Is.EqualTo("normal"));
            Assert.That(d["font-weight"], Is.EqualTo("normal"));
            Assert.That(d["line-height"], Is.EqualTo("normal"));
        }

        [Test]
        public void Bold_then_size_then_family() {
            var d = Expand("bold 16px serif");
            Assert.That(d["font-weight"], Is.EqualTo("bold"));
            Assert.That(d["font-size"], Is.EqualTo("16px"));
            Assert.That(d["font-family"], Is.EqualTo("serif"));
        }

        [Test]
        public void Italic_bold_size_lineheight_family() {
            var d = Expand("italic bold 14px/1.5 sans-serif");
            Assert.That(d["font-style"], Is.EqualTo("italic"));
            Assert.That(d["font-weight"], Is.EqualTo("bold"));
            Assert.That(d["font-size"], Is.EqualTo("14px"));
            Assert.That(d["line-height"], Is.EqualTo("1.5"));
            Assert.That(d["font-family"], Is.EqualTo("sans-serif"));
        }

        [Test]
        public void Family_with_quoted_string() {
            var d = Expand("16px \"Helvetica Neue\"");
            Assert.That(d["font-family"], Is.EqualTo("\"Helvetica Neue\""));
        }

        [Test]
        public void Family_list_with_commas() {
            var d = Expand("16px Arial, sans-serif");
            Assert.That(d["font-family"], Is.EqualTo("Arial, sans-serif"));
        }

        [Test]
        public void Numeric_font_weight_is_recognised() {
            var d = Expand("700 16px Arial");
            Assert.That(d["font-weight"], Is.EqualTo("700"));
        }

        [Test]
        public void Small_caps_variant_works() {
            var d = Expand("small-caps 16px Arial");
            Assert.That(d["font-variant"], Is.EqualTo("small-caps"));
        }

        [Test]
        public void Absolute_size_keyword_is_accepted() {
            var d = Expand("medium serif");
            Assert.That(d["font-size"], Is.EqualTo("medium"));
            Assert.That(d["font-family"], Is.EqualTo("serif"));
        }

        [Test]
        public void Slash_with_unitless_line_height_number() {
            var d = Expand("16px/2 Arial");
            Assert.That(d["line-height"], Is.EqualTo("2"));
        }

        [Test]
        public void Missing_family_yields_no_longhands() {
            var d = Expand("16px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_yields_nothing() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_font() {
            Assert.That(ShorthandRegistry.IsShorthand("font"), Is.True);
        }

        [Test]
        public void System_font_keywords_expand_to_ua_defaults() {
            var caption = Expand("caption");
            Assert.That(caption["font-size"], Is.EqualTo("13px"));
            Assert.That(caption["font-family"], Does.Contain("system-ui"));
            Assert.That(caption["font-style"], Is.EqualTo("normal"));
            Assert.That(caption["font-weight"], Is.EqualTo("normal"));
            Assert.That(caption["line-height"], Is.EqualTo("normal"));

            var small = Expand("small-caption");
            Assert.That(small["font-size"], Is.EqualTo("11px"));
            Assert.That(small["font-family"], Does.Contain("system-ui"));

            foreach (var kw in new[] { "caption", "icon", "menu", "message-box", "small-caption", "status-bar" }) {
                var d = Expand(kw);
                Assert.That(d.ContainsKey("font-family"), Is.True, $"{kw} should expand to longhands");
                Assert.That(d.ContainsKey("font-size"), Is.True, $"{kw} should set font-size");
            }

            // Regression: explicit form unchanged.
            var explicitDecl = Expand("16px Arial");
            Assert.That(explicitDecl["font-size"], Is.EqualTo("16px"));
            Assert.That(explicitDecl["font-family"], Is.EqualTo("Arial"));
        }

        [Test]
        public void Shorthand_resets_inherited_font_variant_numeric_to_initial() {
            // CSS Fonts L4 §17.7: the `font` shorthand resets every font-* subproperty
            // to its initial value before assigning size/family. Without this, an
            // inherited tabular-nums (or any other registered font-* longhand) on a
            // parent would leak through a child's `font: 16px serif`.
            var doc = HtmlParser.Parse("<div id=\"parent\"><div id=\"child\"></div></div>");
            var sheet = OriginatedStylesheet.Author(CssParser.Parse(
                "#parent { font-variant-numeric: tabular-nums; "
                + "font-variation-settings: \"wght\" 600; "
                + "font-feature-settings: \"smcp\"; "
                + "font-optical-sizing: none; } "
                + "#child { font: 16px serif; }"));
            var engine = new CascadeEngine(new[] { sheet });

            var parent = engine.Compute(doc.GetElementById("parent"));
            Assert.That(parent.Get("font-variant-numeric"), Is.EqualTo("tabular-nums"));

            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("font-size"), Is.EqualTo("16px"));
            Assert.That(child.Get("font-family"), Is.EqualTo("serif"));
            Assert.That(child.Get("font-variant-numeric"), Is.EqualTo("normal"));
            Assert.That(child.Get("font-variation-settings"), Is.EqualTo("normal"));
            Assert.That(child.Get("font-feature-settings"), Is.EqualTo("normal"));
            Assert.That(child.Get("font-optical-sizing"), Is.EqualTo("auto"));
        }
    }
}
