using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Parsing;

namespace Weva.Tests.Css {
    public class UserAgentStylesheetTests {
        [Test]
        public void Source_parses_without_errors() {
            Assert.DoesNotThrow(() => CssParser.Parse(UserAgentStylesheet.Source));
        }

        [Test]
        public void Parse_returns_user_agent_origin() {
            var ua = UserAgentStylesheet.Parse();
            Assert.That(ua.Origin, Is.EqualTo(DeclarationOrigin.UserAgent));
            Assert.That(ua.Stylesheet, Is.Not.Null);
            Assert.That(ua.Stylesheet.Rules, Is.Not.Empty);
        }

        [Test]
        public void Defines_block_display_for_div() {
            var ua = UserAgentStylesheet.Parse();
            var rules = ua.Stylesheet.Rules.OfType<StyleRule>().ToList();
            Assert.That(
                rules.Any(r => r.Selectors.Any(s => s.Contains("div")) && r.Declarations.Any(d => d.Property == "display" && d.ValueText.Trim() == "block")),
                Is.True);
        }

        [Test]
        public void Defines_inline_display_for_span() {
            var ua = UserAgentStylesheet.Parse();
            var rules = ua.Stylesheet.Rules.OfType<StyleRule>().ToList();
            Assert.That(
                rules.Any(r => r.Selectors.Any(s => s.Contains("span")) && r.Declarations.Any(d => d.Property == "display" && d.ValueText.Trim() == "inline")),
                Is.True);
        }

        [Test]
        public void Html_background_defaults_to_transparent() {
            var doc = HtmlParser.Parse("<html><body></body></html>");
            var html = doc.GetElementsByTagName("html").First();
            var styles = new CascadeEngine(new[] { UserAgentStylesheet.Parse() }).ComputeAll(doc);

            Assert.That(styles[html].Get("background-color"), Is.EqualTo("transparent"));
        }

        [Test]
        public void Defines_bold_for_strong() {
            var ua = UserAgentStylesheet.Parse();
            var rules = ua.Stylesheet.Rules.OfType<StyleRule>().ToList();
            Assert.That(
                rules.Any(r => r.Selectors.Any(s => s.Contains("strong")) && r.Declarations.Any(d => d.Property == "font-weight")),
                Is.True);
        }

        [Test]
        public void Hidden_attribute_selector_present() {
            var ua = UserAgentStylesheet.Parse();
            var rules = ua.Stylesheet.Rules.OfType<StyleRule>().ToList();
            Assert.That(rules.Any(r => r.Selectors.Any(s => s.Contains("[hidden]"))), Is.True);
        }

        [Test]
        public void Heading_levels_have_decreasing_font_size() {
            var ua = UserAgentStylesheet.Parse();
            var rules = ua.Stylesheet.Rules.OfType<StyleRule>().ToList();
            string SizeOf(string heading) {
                var rule = rules.FirstOrDefault(r => r.Selectors.Any(s => s.Trim() == heading));
                var decl = rule?.Declarations.FirstOrDefault(d => d.Property == "font-size");
                return decl?.ValueText.Trim();
            }
            Assert.That(SizeOf("h1"), Is.EqualTo("2em"));
            Assert.That(SizeOf("h2"), Is.EqualTo("1.5em"));
            Assert.That(SizeOf("h3"), Is.EqualTo("1.17em"));
        }
    }
}
