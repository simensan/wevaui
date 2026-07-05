using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // End-to-end cascade round-trips for the text/font longhands that the
    // engine supports but only had unit-level parser coverage:
    //   - font-variation-settings        (CSS Fonts 4 §6.10)
    //   - font-feature-settings          (CSS Fonts 4 §6.11)   — v1: NOT REGISTERED
    //   - font-variant-numeric           (CSS Fonts 4 §6.7)
    //   - letter-spacing / word-spacing  (CSS Text 3 §10)
    //   - text-transform                 (CSS Text 3 §3)
    //   - text-shadow (multi-shadow)     (CSS Text Decoration 4 §13)
    //   - -webkit-text-stroke            (CSS Text Decoration 4 §10)
    //
    // Mirrors the TextDecorationTests pattern: author CSS via CssParser.Parse,
    // build a tiny DOM via HtmlParser.Parse, compute the cascade with
    // CascadeEngine, then assert via ComputedStyle.Get(string). The point is
    // to lock the FULL cascade pipeline (not just the parser-in-isolation
    // tests under Tests/Runtime/Css/Parsing) for these properties.
    public class TextPropertyIntegrationTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet ParseCss(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(ParseCss(s));

        // Builds a `<p class="x">text</p>` document, computes the cascade,
        // and returns the ComputedStyle for the matched element.
        static ComputedStyle ComputeFor(string css, string className = "x") {
            var doc = Html($"<p class=\"{className}\">text</p>");
            var engine = new CascadeEngine(new[] { Author(css) });
            var p = doc.GetElementsByTagName("p").First();
            return engine.Compute(p);
        }

        // ------------------------------------------------------------------
        // font-variation-settings
        // ------------------------------------------------------------------

        [Test]
        public void Font_variation_settings_axis_value_round_trips() {
            var cs = ComputeFor(".x { font-variation-settings: \"wght\" 700; }");
            var v = cs.Get("font-variation-settings");
            Assert.That(v, Is.Not.Null.And.Not.Empty);
            Assert.That(v, Does.Contain("wght"));
            Assert.That(v, Does.Contain("700"));
        }

        [Test]
        public void Font_variation_settings_multiple_axes() {
            var cs = ComputeFor(".x { font-variation-settings: \"wght\" 700, \"opsz\" 14; }");
            var v = cs.Get("font-variation-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("wght"));
            Assert.That(v, Does.Contain("700"));
            Assert.That(v, Does.Contain("opsz"));
            Assert.That(v, Does.Contain("14"));
        }

        [Test]
        public void Font_variation_settings_inherits() {
            // font-variation-settings is registered as inherited (CSS Fonts 4
            // §6.10): a child element's ComputedStyle should report the
            // parent's value when the child has no rule of its own.
            var doc = Html("<p class=\"parent\"><span class=\"child\">hi</span></p>");
            var engine = new CascadeEngine(new[] {
                Author(".parent { font-variation-settings: \"wght\" 600; }")
            });
            var span = doc.GetElementsByTagName("span").First();
            var childCs = engine.Compute(span);
            var v = childCs.Get("font-variation-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("wght"));
            Assert.That(v, Does.Contain("600"));
        }

        // ------------------------------------------------------------------
        // font-feature-settings
        // ------------------------------------------------------------------

        [Test]
        public void Font_feature_settings_round_trips() {
            // v1: font-feature-settings is NOT in the CssProperties registry
            // (see CssProperties.cs ~line 667 where neighbouring forward-compat
            // props like font-variant-numeric ARE registered). ComputedStyle.Set
            // spills unrecognised bare-name properties to the customProps side
            // dictionary, so Get(string) still returns the author value — but
            // the cascade emits a one-shot "unknown property" warning. The
            // engine reads OpenType features through SDF/TMP font asset config
            // rather than this property today.
            var cs = ComputeFor(".x { font-feature-settings: \"liga\" 1, \"kern\" 0; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null, "expected unknown-property spill to preserve value");
            Assert.That(v, Does.Contain("liga"));
            Assert.That(v, Does.Contain("kern"));
        }

        [Test]
        public void Font_feature_settings_normal_keyword() {
            // v1: with font-feature-settings unregistered, an explicit
            // `normal` declaration still spills to customProps. When NOT
            // authored at all, Get returns null (no initial-fill since the
            // property isn't in the registry).
            var cs = ComputeFor(".x { font-feature-settings: normal; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.EqualTo("normal"));
        }

        // ------------------------------------------------------------------
        // font-variant-numeric
        // ------------------------------------------------------------------

        [Test]
        public void Font_variant_numeric_tabular_nums_round_trips() {
            var cs = ComputeFor(".x { font-variant-numeric: tabular-nums; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("tabular-nums"));
        }

        [Test]
        public void Font_variant_numeric_proportional_nums_round_trips() {
            var cs = ComputeFor(".x { font-variant-numeric: proportional-nums; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("proportional-nums"));
        }

        [Test]
        public void Font_variant_numeric_normal_keyword() {
            var cs = ComputeFor(".x { font-variant-numeric: normal; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("normal"));
        }

        // ------------------------------------------------------------------
        // letter-spacing / word-spacing
        // ------------------------------------------------------------------

        [Test]
        public void Letter_spacing_pixel_value_round_trips() {
            var cs = ComputeFor(".x { letter-spacing: 2px; }");
            Assert.That(cs.Get("letter-spacing"), Is.EqualTo("2px"));
        }

        [Test]
        public void Letter_spacing_normal_keyword() {
            var cs = ComputeFor(".x { letter-spacing: normal; }");
            Assert.That(cs.Get("letter-spacing"), Is.EqualTo("normal"));
        }

        [Test]
        public void Letter_spacing_em_value_round_trips() {
            // v1: the cascade carries the author string as-is; em→px resolution
            // happens at consumer time (TextRunResolver) via the typed parse
            // path. So Get(string) must still report "0.1em".
            var cs = ComputeFor(".x { letter-spacing: 0.1em; }");
            Assert.That(cs.Get("letter-spacing"), Is.EqualTo("0.1em"));
        }

        [Test]
        public void Word_spacing_pixel_value_round_trips() {
            var cs = ComputeFor(".x { word-spacing: 4px; }");
            Assert.That(cs.Get("word-spacing"), Is.EqualTo("4px"));
        }

        // ------------------------------------------------------------------
        // text-transform
        // ------------------------------------------------------------------

        [Test]
        public void Text_transform_uppercase_round_trips() {
            var cs = ComputeFor(".x { text-transform: uppercase; }");
            Assert.That(cs.Get("text-transform"), Is.EqualTo("uppercase"));
        }

        [Test]
        public void Text_transform_lowercase_round_trips() {
            var cs = ComputeFor(".x { text-transform: lowercase; }");
            Assert.That(cs.Get("text-transform"), Is.EqualTo("lowercase"));
        }

        [Test]
        public void Text_transform_capitalize_round_trips() {
            var cs = ComputeFor(".x { text-transform: capitalize; }");
            Assert.That(cs.Get("text-transform"), Is.EqualTo("capitalize"));
        }

        [Test]
        public void Text_transform_none_keyword() {
            var cs = ComputeFor(".x { text-transform: none; }");
            Assert.That(cs.Get("text-transform"), Is.EqualTo("none"));
        }

        // ------------------------------------------------------------------
        // text-shadow (single + multi-shadow)
        // ------------------------------------------------------------------

        [Test]
        public void Text_shadow_x_y_blur_color_round_trips() {
            var cs = ComputeFor(".x { text-shadow: 1px 2px 3px red; }");
            var v = cs.Get("text-shadow");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("1px"));
            Assert.That(v, Does.Contain("2px"));
            Assert.That(v, Does.Contain("3px"));
            Assert.That(v, Does.Contain("red"));
        }

        [Test]
        public void Text_shadow_multi_shadow_round_trips() {
            // Comma-separated list per CSS Text Decoration 4 §13. The cascade
            // is expected to preserve the comma list verbatim — the
            // TextShadowResolver splits + materialises individual shadows at
            // paint time.
            var cs = ComputeFor(".x { text-shadow: 1px 1px black, 2px 2px white; }");
            var v = cs.Get("text-shadow");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain(","), "multi-shadow comma list should round-trip");
            Assert.That(v, Does.Contain("black"));
            Assert.That(v, Does.Contain("white"));
        }

        [Test]
        public void Text_shadow_none_keyword() {
            var cs = ComputeFor(".x { text-shadow: none; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("none"));
        }

        // ------------------------------------------------------------------
        // -webkit-text-stroke (longhands + shorthand expander)
        // ------------------------------------------------------------------

        [Test]
        public void Webkit_text_stroke_width_round_trips() {
            var cs = ComputeFor(".x { -webkit-text-stroke-width: 2px; }");
            Assert.That(cs.Get("-webkit-text-stroke-width"), Is.EqualTo("2px"));
        }

        [Test]
        public void Webkit_text_stroke_color_round_trips() {
            var cs = ComputeFor(".x { -webkit-text-stroke-color: red; }");
            Assert.That(cs.Get("-webkit-text-stroke-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Webkit_text_stroke_shorthand_expands() {
            // TextStrokeShorthandExpander splits `1px red` into the two
            // longhands. Both should be reachable via the cascade.
            var cs = ComputeFor(".x { -webkit-text-stroke: 1px red; }");
            Assert.That(cs.Get("-webkit-text-stroke-width"), Is.EqualTo("1px"));
            Assert.That(cs.Get("-webkit-text-stroke-color"), Is.EqualTo("red"));
        }
    }
}
