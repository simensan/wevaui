using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class AttrResolverTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        [Test]
        public void Attr_string_resolves_to_attribute_value() {
            var doc = Html("<div id=\"x\" data-label=\"Hello\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-label); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("content"), Is.EqualTo("Hello"));
        }

        [Test]
        public void Attr_with_px_type_resolves_to_pixels() {
            var doc = Html("<div id=\"x\" data-w=\"160\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-w px, 100px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("160px"));
        }

        [Test]
        public void Attr_missing_attribute_uses_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-w px, 100px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("100px"));
        }

        [Test]
        public void Attr_unparseable_for_numeric_type_uses_fallback() {
            var doc = Html("<div id=\"x\" data-w=\"hello\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-w px, 50px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("50px"));
        }

        [Test]
        public void Attr_default_string_type_when_unspecified() {
            var doc = Html("<div id=\"x\" data-name=\"foo\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-name); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("content"), Is.EqualTo("foo"));
        }

        [Test]
        public void Attr_number_type_returns_bare_number() {
            var doc = Html("<div id=\"x\" data-count=\"7\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-count number, 0); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("z-index"), Is.EqualTo("7"));
        }

        [Test]
        public void Attr_em_type_appends_em_unit() {
            var doc = Html("<div id=\"x\" data-size=\"2.5\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { font-size: attr(data-size em, 1em); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("2.5em"));
        }

        [Test]
        public void Attr_percent_type_appends_percent() {
            var doc = Html("<div id=\"x\" data-share=\"75\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-share %, 100%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("75%"));
        }

        [Test]
        public void Attr_used_twice_in_same_property() {
            var doc = Html("<div id=\"x\" data-a=\"hello\" data-b=\"world\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-a) attr(data-b); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Whitespace between resolved tokens is preserved by CssParser's value text.
            Assert.That(cs.Get("content"), Does.Contain("hello"));
            Assert.That(cs.Get("content"), Does.Contain("world"));
        }

        [Test]
        public void Attr_raw_string_type_passes_value_through() {
            var doc = Html("<div id=\"x\" data-r=\"a b c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-r raw-string, fallback); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("content"), Is.EqualTo("a b c"));
        }

        [Test]
        public void Attr_raw_string_missing_uses_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-r raw-string, fallback); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("content"), Is.EqualTo("fallback"));
        }

        [Test]
        public void Attr_color_type_accepts_named_hex_and_function() {
            var doc = Html("<div id=\"a\" data-c=\"red\"></div><div id=\"b\" data-c=\"#3366cc\"></div><div id=\"c\" data-c=\"rgb(10,20,30)\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#a, #b, #c { color: attr(data-c color, black); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("color"), Is.EqualTo("#3366cc"));
            var c = engine.Compute(doc.GetElementById("c")).Get("color");
            Assert.That(c, Does.StartWith("rgb("));
            Assert.That(c, Does.Contain("10"));
            Assert.That(c, Does.Contain("20"));
            Assert.That(c, Does.Contain("30"));
        }

        [Test]
        public void Attr_color_type_invalid_uses_fallback() {
            var doc = Html("<div id=\"x\" data-c=\"notacolor\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: attr(data-c color, black); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Attr_color_type_rejects_bad_hex_length() {
            var doc = Html("<div id=\"x\" data-c=\"#12345\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: attr(data-c color, black); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Attr_dimension_class_keywords_accept_any_unit_of_class() {
            var doc = Html(
                "<div id=\"len\" data-x=\"5em\"></div>" +
                "<div id=\"len_bad\" data-x=\"abc\"></div>" +
                "<div id=\"rot\" data-rot=\"45deg\"></div>" +
                "<div id=\"rot_turn\" data-rot=\"0.5turn\"></div>" +
                "<div id=\"i_ok\" data-i=\"42\"></div>" +
                "<div id=\"i_bad\" data-i=\"3.14\"></div>" +
                "<div id=\"specific\" data-x=\"160\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#len { width: attr(data-x length, 12px); }" +
                    "#len_bad { width: attr(data-x length); }" +
                    "#rot { transform: attr(data-rot angle, 0deg); }" +
                    "#rot_turn { transform: attr(data-rot angle, 0deg); }" +
                    "#i_ok { z-index: attr(data-i integer, 0); }" +
                    "#i_bad { z-index: attr(data-i integer, 0); }" +
                    "#specific { width: attr(data-x px, 12px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("len")).Get("width"), Is.EqualTo("5em"));
            var lenBad = engine.Compute(doc.GetElementById("len_bad")).Get("width");
            Assert.That(lenBad, Is.Not.EqualTo("abc"));
            Assert.That(engine.Compute(doc.GetElementById("rot")).Get("transform"), Is.EqualTo("45deg"));
            Assert.That(engine.Compute(doc.GetElementById("rot_turn")).Get("transform"), Is.EqualTo("0.5turn"));
            Assert.That(engine.Compute(doc.GetElementById("i_ok")).Get("z-index"), Is.EqualTo("42"));
            Assert.That(engine.Compute(doc.GetElementById("i_bad")).Get("z-index"), Is.EqualTo("0"));
            Assert.That(engine.Compute(doc.GetElementById("specific")).Get("width"), Is.EqualTo("160px"));
        }

        [Test]
        public void Attr_changes_re_resolve_on_next_cascade() {
            var doc = Html("<div id=\"x\" data-w=\"100\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-w px, 50px); }")
            });
            var el = doc.GetElementById("x");
            var first = engine.Compute(el);
            Assert.That(first.Get("width"), Is.EqualTo("100px"));
            el.SetAttribute("data-w", "240");
            var second = engine.Compute(el);
            Assert.That(second.Get("width"), Is.EqualTo("240px"));
        }
    }
}
