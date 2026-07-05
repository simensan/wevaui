using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class LightDarkResolverTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static CascadeEngine BuildEngine(string css, ColorScheme scheme) {
            var sheets = new[] { Author(css) };
            var media = MediaContext.Default(800, 600).WithColorScheme(scheme);
            return new CascadeEngine(sheets, media);
        }

        [Test]
        public void Light_dark_picks_first_when_scheme_is_light() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { color: light-dark(white, black); }", ColorScheme.Light);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("white"));
        }

        [Test]
        public void Light_dark_picks_second_when_scheme_is_dark() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { color: light-dark(white, black); }", ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Light_dark_with_hex_colors_in_light() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { background-color: light-dark(#f9fafb, #1f2937); }", ColorScheme.Light);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("#f9fafb"));
        }

        [Test]
        public void Light_dark_with_hex_colors_in_dark() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { background-color: light-dark(#f9fafb, #1f2937); }", ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("#1f2937"));
        }

        [Test]
        public void Light_dark_nested_in_dark_resolves_inner() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "#x { color: light-dark(rgb(255, 0, 0), light-dark(green, blue)); }",
                ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Light_dark_combines_with_var_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "#x { color: light-dark(var(--accent, red), var(--accent, blue)); }",
                ColorScheme.Light);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Light_dark_changes_when_media_context_swapped() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { color: light-dark(beige, navy); }", ColorScheme.Light);
            var first = engine.Compute(doc.GetElementById("x"));
            Assert.That(first.Get("color"), Is.EqualTo("beige"));

            engine.MediaContext = engine.MediaContext.WithColorScheme(ColorScheme.Dark);
            engine.InvalidateAll();
            var second = engine.Compute(doc.GetElementById("x"));
            Assert.That(second.Get("color"), Is.EqualTo("navy"));
        }
    }
}
