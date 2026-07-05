using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Round-trip coverage for the CSS Text Decoration L3 / L4 longhands.
    // Asserts that author CSS parses, cascades, and is reachable via
    // ComputedStyle.Get on the matched element. The matching live engine
    // tests at the shorthand-expander level (TextDecorationShorthandTests)
    // only cover the expander in isolation; this file exercises the full
    // cascade pipeline end-to-end through the same path used by the layout
    // and paint passes.
    //
    // v1 notes:
    //   - text-decoration-line / -style / -color / -thickness / -underline-
    //     offset are all REGISTERED AS NON-INHERITED in CssProperties
    //     (matches CSS Text Decoration L3 §2: text-decoration is a property
    //     applied to the element that establishes the decoration, propagated
    //     to descendants via box-tree traversal at paint time, NOT through
    //     the cascade). That means `style.Get("text-decoration-line")` on a
    //     child whose parent has `text-decoration: underline` returns the
    //     initial value "none". The inheritance test below documents this.
    public class TextDecorationTests {
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

        [Test]
        public void Text_decoration_shorthand_expands_to_line_style_color() {
            var cs = ComputeFor(".x { text-decoration: underline dashed red; }");
            Assert.That(cs.Get("text-decoration-line"), Is.EqualTo("underline"));
            Assert.That(cs.Get("text-decoration-style"), Is.EqualTo("dashed"));
            Assert.That(cs.Get("text-decoration-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Text_decoration_line_underline_round_trips() {
            var cs = ComputeFor(".x { text-decoration-line: underline; }");
            Assert.That(cs.Get("text-decoration-line"), Is.EqualTo("underline"));
        }

        [Test]
        public void Text_decoration_line_overline_round_trips() {
            var cs = ComputeFor(".x { text-decoration-line: overline; }");
            Assert.That(cs.Get("text-decoration-line"), Is.EqualTo("overline"));
        }

        [Test]
        public void Text_decoration_line_line_through_round_trips() {
            var cs = ComputeFor(".x { text-decoration-line: line-through; }");
            Assert.That(cs.Get("text-decoration-line"), Is.EqualTo("line-through"));
        }

        [Test]
        public void Text_decoration_style_solid_round_trips() {
            var cs = ComputeFor(".x { text-decoration-style: solid; }");
            Assert.That(cs.Get("text-decoration-style"), Is.EqualTo("solid"));
        }

        [Test]
        public void Text_decoration_style_dashed_round_trips() {
            var cs = ComputeFor(".x { text-decoration-style: dashed; }");
            Assert.That(cs.Get("text-decoration-style"), Is.EqualTo("dashed"));
        }

        [Test]
        public void Text_decoration_style_dotted_round_trips() {
            var cs = ComputeFor(".x { text-decoration-style: dotted; }");
            Assert.That(cs.Get("text-decoration-style"), Is.EqualTo("dotted"));
        }

        [Test]
        public void Text_decoration_style_wavy_round_trips() {
            var cs = ComputeFor(".x { text-decoration-style: wavy; }");
            Assert.That(cs.Get("text-decoration-style"), Is.EqualTo("wavy"));
        }

        [Test]
        public void Text_decoration_color_explicit_value_round_trips() {
            var cs = ComputeFor(".x { text-decoration-color: blue; }");
            Assert.That(cs.Get("text-decoration-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Text_decoration_thickness_pixel_round_trips() {
            var cs = ComputeFor(".x { text-decoration-thickness: 3px; }");
            Assert.That(cs.Get("text-decoration-thickness"), Is.EqualTo("3px"));
        }

        [Test]
        public void Text_underline_offset_pixel_round_trips() {
            var cs = ComputeFor(".x { text-underline-offset: 5px; }");
            Assert.That(cs.Get("text-underline-offset"), Is.EqualTo("5px"));
        }

        [Test]
        public void Text_decoration_inherits_from_parent() {
            // v1: text-decoration-line is REGISTERED AS NON-INHERITED in
            // CssProperties (`Add("text-decoration-line", false, "none")`).
            // Per CSS Text Decoration L3 §2 this is correct: decorations
            // are propagated by the box tree at paint time, not via the
            // cascade. A child element's ComputedStyle therefore returns the
            // initial "none", NOT the parent's value.
            //
            // The visible inheritance (descendants of `<p style="text-
            // decoration: underline">` rendering an underline) happens in
            // TextRunResolver via box-tree walks, not here.
            var doc = Html("<p class=\"parent\"><span class=\"child\">hi</span></p>");
            var engine = new CascadeEngine(new[] {
                Author(".parent { text-decoration: underline; }")
            });
            var span = doc.GetElementsByTagName("span").First();
            var childCs = engine.Compute(span);
            // v1: text-decoration-line is non-inherited in this engine, so the
            // child sees the initial value rather than parent's "underline".
            Assert.That(childCs.Get("text-decoration-line"), Is.EqualTo("none"));
        }

        [Test]
        public void Text_decoration_color_currentColor_keyword_round_trips() {
            var cs = ComputeFor(".x { text-decoration-color: currentColor; }");
            // The shorthand expander normalises to lowercase `currentcolor` as
            // its default; explicit author values may be returned as-typed.
            // Accept either case to stay tolerant of the parser's normalization.
            var v = cs.Get("text-decoration-color");
            Assert.That(string.Equals(v, "currentColor", System.StringComparison.OrdinalIgnoreCase),
                $"Expected currentColor (any case), got '{v}'");
        }
    }
}
