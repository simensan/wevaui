using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // B4 audit: typed attr() forms in property-value contexts outside the
    // content/cascade path. Covers:
    //   - percentage keyword type
    //   - ident keyword type
    //   - attr() in shorthand (border, padding, margin, background)
    //   - attr() inside calc()
    //   - attr() through var() indirection (custom property carries attr())
    //   - legacy untyped attr() in non-content property (Chrome: drops to initial)
    //   - fallback on missing attr, fallback on type-mismatch
    //   - attribute mutation re-resolves on next cascade pass
    // Harness: HtmlParser.Parse + CascadeEngine.Compute, same as AttrResolverTests.
    public class AttrTypedFormEdgeCaseTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ---- percentage type keyword ----

        [Test]
        public void Attr_percentage_type_appends_percent_sign() {
            // attr(data-x percentage) on a number attribute => "75%"
            var doc = Html("<div id=\"x\" data-x=\"75\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-x percentage, 100%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("75%"));
        }

        [Test]
        public void Attr_percentage_type_with_float_value() {
            var doc = Html("<div id=\"x\" data-x=\"33.5\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-x percentage, 50%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Should produce "33.5%"
            Assert.That(cs.Get("width"), Does.EndWith("%"));
            Assert.That(cs.Get("width"), Does.Contain("33.5"));
        }

        [Test]
        public void Attr_percentage_type_non_numeric_falls_back() {
            var doc = Html("<div id=\"x\" data-x=\"hello\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-x percentage, 40%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("40%"));
        }

        [Test]
        public void Attr_percentage_type_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { opacity: attr(data-opacity percentage, 100%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("opacity"), Is.EqualTo("100%"));
        }

        // ---- ident type keyword ----

        [Test]
        public void Attr_ident_type_passes_value_as_keyword() {
            // attr(data-display ident) lets the attribute supply a CSS ident
            // like "flex" or "block" for a property that accepts keywords.
            var doc = Html("<div id=\"x\" data-display=\"flex\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { display: attr(data-display ident, block); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("flex"));
        }

        [Test]
        public void Attr_ident_type_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { display: attr(data-display ident, block); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Attr_ident_type_empty_attribute_falls_back() {
            var doc = Html("<div id=\"x\" data-display=\"\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { display: attr(data-display ident, block); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Empty attribute => ident type returns false => fallback
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Attr_ident_type_whitespace_trimmed_to_keyword() {
            // Whitespace around the attribute value should be trimmed
            var doc = Html("<div id=\"x\" data-pos=\"  relative  \"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { position: attr(data-pos ident, static); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("position"), Is.EqualTo("relative"));
        }

        // ---- attr() inside calc() ----

        [Test]
        public void Attr_px_type_inside_calc_produces_resolvable_value() {
            // AttrResolver substitutes the string first; the result
            // "calc(160px + 10px)" is stored verbatim by the cascade.
            var doc = Html("<div id=\"x\" data-base=\"160\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: calc(attr(data-base px) + 10px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // After substitution the value should contain "160px"
            string w = cs.Get("width");
            Assert.That(w, Does.Contain("160px"));
        }

        [Test]
        public void Attr_inside_calc_missing_uses_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: calc(attr(data-base px, 100px) + 10px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string w = cs.Get("width");
            Assert.That(w, Does.Contain("100px"));
        }

        [Test]
        public void Attr_percentage_type_inside_calc() {
            var doc = Html("<div id=\"x\" data-share=\"50\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: calc(attr(data-share percentage) + 10px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string w = cs.Get("width");
            Assert.That(w, Does.Contain("50%"));
        }

        // ---- attr() through var() indirection ----

        [Test]
        public void Attr_via_custom_property_then_var_resolves_color() {
            // Custom property carries attr(), then non-custom property uses var()
            // to consume it. The resolution chain must produce the final color.
            var doc = Html("<div id=\"x\" data-c=\"red\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --my-color: attr(data-c color, black); color: var(--my-color); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Attr_via_custom_property_then_var_fallback_on_missing() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --my-color: attr(data-c color, blue); color: var(--my-color); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Attr_via_custom_property_then_var_resolves_length() {
            var doc = Html("<div id=\"x\" data-w=\"200\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --w: attr(data-w px, 50px); width: var(--w); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("200px"));
        }

        // ---- attr() in shorthand expansion ----

        [Test]
        public void Attr_color_in_border_shorthand_resolves_to_longhand() {
            // border: 1px solid attr(data-c color) must be deferred past
            // attr() substitution before the shorthand expands. After
            // resolution the expander receives "1px solid red" and emits
            // the per-side longhands.
            var doc = Html("<div id=\"x\" data-c=\"red\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { border: 1px solid attr(data-c color, black); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // border-color longhands should reflect "red"
            string topColor = cs.Get("border-top-color");
            Assert.That(topColor, Is.EqualTo("red"));
        }

        [Test]
        public void Attr_color_in_border_shorthand_fallback_on_missing() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { border: 2px solid attr(data-c color, blue); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string topColor = cs.Get("border-top-color");
            Assert.That(topColor, Is.EqualTo("blue"));
        }

        [Test]
        public void Attr_px_in_padding_shorthand_resolves_to_longhands() {
            var doc = Html("<div id=\"x\" data-pad=\"20\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding: attr(data-pad px, 0px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("20px"));
            Assert.That(cs.Get("padding-right"), Is.EqualTo("20px"));
            Assert.That(cs.Get("padding-bottom"), Is.EqualTo("20px"));
            Assert.That(cs.Get("padding-left"), Is.EqualTo("20px"));
        }

        [Test]
        public void Attr_px_in_margin_shorthand_resolves_to_longhands() {
            var doc = Html("<div id=\"x\" data-m=\"8\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { margin: attr(data-m px, 0px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("margin-top"), Is.EqualTo("8px"));
            Assert.That(cs.Get("margin-right"), Is.EqualTo("8px"));
            Assert.That(cs.Get("margin-bottom"), Is.EqualTo("8px"));
            Assert.That(cs.Get("margin-left"), Is.EqualTo("8px"));
        }

        // ---- fallback / mismatch behaviors ----

        [Test]
        public void Attr_percentage_type_falls_back_on_non_numeric() {
            var doc = Html("<div id=\"x\" data-x=\"notanumber\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-x percentage, 60%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("60%"));
        }

        [Test]
        public void Attr_ident_non_content_property_uses_attribute_ident() {
            // Unlike the legacy untyped form, typed `ident` is allowed on
            // non-content properties per CSS Values L4.
            var doc = Html("<div id=\"x\" data-vis=\"hidden\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { visibility: attr(data-vis ident, visible); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("visibility"), Is.EqualTo("hidden"));
        }

        // ---- attribute mutation re-resolution ----

        [Test]
        public void Attr_percentage_re_resolves_after_attribute_change() {
            var doc = Html("<div id=\"x\" data-x=\"50\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-x percentage, 0%); }")
            });
            var el = doc.GetElementById("x");
            var first = engine.Compute(el);
            Assert.That(first.Get("width"), Is.EqualTo("50%"));
            el.SetAttribute("data-x", "80");
            var second = engine.Compute(el);
            Assert.That(second.Get("width"), Is.EqualTo("80%"));
        }

        [Test]
        public void Attr_ident_re_resolves_after_attribute_change() {
            var doc = Html("<div id=\"x\" data-display=\"block\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { display: attr(data-display ident, block); }")
            });
            var el = doc.GetElementById("x");
            var first = engine.Compute(el);
            Assert.That(first.Get("display"), Is.EqualTo("block"));
            el.SetAttribute("data-display", "flex");
            var second = engine.Compute(el);
            Assert.That(second.Get("display"), Is.EqualTo("flex"));
        }

        [Test]
        public void Attr_shorthand_re_resolves_after_attribute_change() {
            var doc = Html("<div id=\"x\" data-c=\"red\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { border: 1px solid attr(data-c color, black); }")
            });
            var el = doc.GetElementById("x");
            var first = engine.Compute(el);
            Assert.That(first.Get("border-top-color"), Is.EqualTo("red"));
            el.SetAttribute("data-c", "blue");
            var second = engine.Compute(el);
            Assert.That(second.Get("border-top-color"), Is.EqualTo("blue"));
        }

        // ---- additional type coverage ----

        [Test]
        public void Attr_percentage_type_and_percent_alias_are_equivalent() {
            // `percentage` keyword and `%` shorthand alias must produce the same output
            var doc = Html("<div id=\"a\" data-x=\"70\"></div><div id=\"b\" data-x=\"70\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#a { width: attr(data-x percentage, 0%); }" +
                    "#b { width: attr(data-x %, 0%); }")
            });
            var csA = engine.Compute(doc.GetElementById("a"));
            var csB = engine.Compute(doc.GetElementById("b"));
            Assert.That(csA.Get("width"), Is.EqualTo(csB.Get("width")));
        }

        [Test]
        public void Attr_length_type_with_em_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-size=\"3em\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { font-size: attr(data-size length, 1em); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("3em"));
        }

        [Test]
        public void Attr_length_type_rejects_bare_number_falls_back() {
            // `length` type requires a unit; bare "10" is not valid
            var doc = Html("<div id=\"x\" data-size=\"10\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { font-size: attr(data-size length, 2em); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("2em"));
        }
    }
}
