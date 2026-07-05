using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // H17b: CSS Values L5 §5 <attr-type> dimension/numeric class keywords.
    // Covers the five keywords added by H17b:
    //   length, angle, time, flex, integer
    // For each keyword:
    //   - valid value (single representative unit) passes through
    //   - valid value (alternate unit) passes through
    //   - bare number (no unit) falls back — these keywords REQUIRE a unit token
    //   - non-numeric / text value falls back
    //   - missing attribute falls back
    //   - no-fallback + invalid => guaranteed-invalid => property drops to initial
    //   - end-to-end inside a real property declaration (via CascadeEngine)
    public class AttrDimensionTypeKeywordTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        // ===================================================================
        // length
        // ===================================================================

        [Test]
        public void Length_px_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-v=\"100px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 50px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("100px"));
        }

        [Test]
        public void Length_em_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-v=\"2.5em\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { font-size: attr(data-v length, 1em); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("font-size"),
                Is.EqualTo("2.5em"));
        }

        [Test]
        public void Length_rem_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-v=\"3rem\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 1rem); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("3rem"));
        }

        [Test]
        public void Length_vw_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-v=\"50vw\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 100px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("50vw"));
        }

        [Test]
        public void Length_bare_number_falls_back() {
            // `length` type requires a unit; "42" alone is not a valid length token
            var doc = Html("<div id=\"x\" data-v=\"42\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 20px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("20px"));
        }

        [Test]
        public void Length_non_numeric_falls_back() {
            var doc = Html("<div id=\"x\" data-v=\"hello\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 30px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("30px"));
        }

        [Test]
        public void Length_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 10px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("10px"));
        }

        [Test]
        public void Length_wrong_class_unit_falls_back() {
            // "45deg" is not a length unit — should fail and use fallback
            var doc = Html("<div id=\"x\" data-v=\"45deg\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length, 5px); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("width"),
                Is.EqualTo("5px"));
        }

        [Test]
        public void Length_in_shorthand_resolves_to_longhands() {
            var doc = Html("<div id=\"x\" data-p=\"16px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding: attr(data-p length, 0px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-right"), Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-left"), Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-bottom"), Is.EqualTo("16px"));
        }

        // ===================================================================
        // angle
        // ===================================================================

        [Test]
        public void Angle_deg_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-r=\"45deg\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 0deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("45deg"));
        }

        [Test]
        public void Angle_turn_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-r=\"0.25turn\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 0deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("0.25turn"));
        }

        [Test]
        public void Angle_rad_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-r=\"1.5708rad\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 0deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("1.5708rad"));
        }

        [Test]
        public void Angle_grad_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-r=\"100grad\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 0deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("100grad"));
        }

        [Test]
        public void Angle_bare_number_falls_back() {
            // `angle` type requires a unit; "90" alone is not a valid angle token
            var doc = Html("<div id=\"x\" data-r=\"90\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 0deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("0deg"));
        }

        [Test]
        public void Angle_non_numeric_falls_back() {
            var doc = Html("<div id=\"x\" data-r=\"quarter\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 90deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("90deg"));
        }

        [Test]
        public void Angle_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 180deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("180deg"));
        }

        [Test]
        public void Angle_wrong_class_unit_falls_back() {
            // "10px" is a length, not an angle — must fall back
            var doc = Html("<div id=\"x\" data-r=\"10px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 45deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("45deg"));
        }

        [Test]
        public void Angle_negative_deg_passes_through() {
            var doc = Html("<div id=\"x\" data-r=\"-30deg\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle, 0deg); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transform"),
                Is.EqualTo("-30deg"));
        }

        // ===================================================================
        // time
        // ===================================================================

        [Test]
        public void Time_ms_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-d=\"500ms\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 0s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("500ms"));
        }

        [Test]
        public void Time_s_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-d=\"1.5s\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 0s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("1.5s"));
        }

        [Test]
        public void Time_bare_number_falls_back() {
            // `time` type requires ms or s; bare "300" is invalid
            var doc = Html("<div id=\"x\" data-d=\"300\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 0.5s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("0.5s"));
        }

        [Test]
        public void Time_non_numeric_falls_back() {
            var doc = Html("<div id=\"x\" data-d=\"fast\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 1s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("1s"));
        }

        [Test]
        public void Time_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 2s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("2s"));
        }

        [Test]
        public void Time_wrong_class_unit_falls_back() {
            // "100px" is a length — must fall back for `time` type
            var doc = Html("<div id=\"x\" data-d=\"100px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 0.3s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("0.3s"));
        }

        [Test]
        public void Time_zero_ms_passes_through() {
            var doc = Html("<div id=\"x\" data-d=\"0ms\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition-duration: attr(data-d time, 1s); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("0ms"));
        }

        // ===================================================================
        // flex
        // ===================================================================

        [Test]
        public void Flex_fr_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-v=\"2fr\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { grid-template-columns: attr(data-v flex, 1fr); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("grid-template-columns"),
                Is.EqualTo("2fr"));
        }

        [Test]
        public void Flex_fractional_fr_unit_passes_through() {
            var doc = Html("<div id=\"x\" data-v=\"1.5fr\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { grid-template-columns: attr(data-v flex, 1fr); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("grid-template-columns"),
                Is.EqualTo("1.5fr"));
        }

        [Test]
        public void Flex_bare_number_falls_back() {
            // `flex` type requires the `fr` unit; bare "3" is invalid
            var doc = Html("<div id=\"x\" data-v=\"3\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { grid-template-columns: attr(data-v flex, 1fr); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("grid-template-columns"),
                Is.EqualTo("1fr"));
        }

        [Test]
        public void Flex_non_numeric_falls_back() {
            var doc = Html("<div id=\"x\" data-v=\"auto\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { grid-template-columns: attr(data-v flex, 2fr); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("grid-template-columns"),
                Is.EqualTo("2fr"));
        }

        [Test]
        public void Flex_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { grid-template-columns: attr(data-v flex, 1fr); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("grid-template-columns"),
                Is.EqualTo("1fr"));
        }

        [Test]
        public void Flex_wrong_class_unit_falls_back() {
            // "100px" is a length, not flex — must fall back for `flex` type
            var doc = Html("<div id=\"x\" data-v=\"100px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { grid-template-columns: attr(data-v flex, 3fr); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("grid-template-columns"),
                Is.EqualTo("3fr"));
        }

        // ===================================================================
        // integer
        // ===================================================================

        [Test]
        public void Integer_positive_passes_through() {
            var doc = Html("<div id=\"x\" data-z=\"10\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 0); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("10"));
        }

        [Test]
        public void Integer_zero_passes_through() {
            var doc = Html("<div id=\"x\" data-z=\"0\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 5); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("0"));
        }

        [Test]
        public void Integer_negative_passes_through() {
            var doc = Html("<div id=\"x\" data-z=\"-3\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 0); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("-3"));
        }

        [Test]
        public void Integer_float_falls_back() {
            // 3.14 contains a decimal — not a valid integer token
            var doc = Html("<div id=\"x\" data-z=\"3.14\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 0); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("0"));
        }

        [Test]
        public void Integer_non_numeric_falls_back() {
            var doc = Html("<div id=\"x\" data-z=\"high\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 1); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("1"));
        }

        [Test]
        public void Integer_missing_attribute_falls_back() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 99); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("99"));
        }

        [Test]
        public void Integer_with_unit_falls_back() {
            // "5px" is a dimension, not a bare integer
            var doc = Html("<div id=\"x\" data-z=\"5px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 0); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("0"));
        }

        [Test]
        public void Integer_large_value_passes_through() {
            var doc = Html("<div id=\"x\" data-z=\"1000000\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 0); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("z-index"),
                Is.EqualTo("1000000"));
        }

        // ===================================================================
        // Cross-keyword: mismatch with no fallback => guaranteed-invalid
        // (property drops to initial value, not an empty or garbage string)
        // ===================================================================

        [Test]
        public void Length_no_fallback_invalid_attr_drops_to_initial() {
            // No fallback and invalid attribute => the property should not
            // contain the garbage value; the cascade drops the declaration.
            var doc = Html("<div id=\"x\" data-v=\"notlength\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-v length); }")
            });
            var w = engine.Compute(doc.GetElementById("x")).Get("width");
            // The value must not be the raw attribute text
            Assert.That(w, Is.Not.EqualTo("notlength"));
        }

        [Test]
        public void Integer_no_fallback_float_drops_to_initial() {
            var doc = Html("<div id=\"x\" data-z=\"1.5\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer); }")
            });
            var z = engine.Compute(doc.GetElementById("x")).Get("z-index");
            Assert.That(z, Is.Not.EqualTo("1.5"));
        }

        [Test]
        public void Angle_no_fallback_bare_number_drops_to_initial() {
            var doc = Html("<div id=\"x\" data-r=\"45\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transform: attr(data-r angle); }")
            });
            var t = engine.Compute(doc.GetElementById("x")).Get("transform");
            Assert.That(t, Is.Not.EqualTo("45"));
        }

        // ===================================================================
        // End-to-end: attr() with dimension type inside a real property,
        // including attribute mutation re-resolution
        // ===================================================================

        [Test]
        public void Length_re_resolves_after_attribute_change() {
            var doc = Html("<div id=\"x\" data-w=\"100px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: attr(data-w length, 50px); }")
            });
            var el = doc.GetElementById("x");
            Assert.That(engine.Compute(el).Get("width"), Is.EqualTo("100px"));
            el.SetAttribute("data-w", "200px");
            Assert.That(engine.Compute(el).Get("width"), Is.EqualTo("200px"));
        }

        [Test]
        public void Integer_re_resolves_after_attribute_change() {
            var doc = Html("<div id=\"x\" data-z=\"5\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { z-index: attr(data-z integer, 0); }")
            });
            var el = doc.GetElementById("x");
            Assert.That(engine.Compute(el).Get("z-index"), Is.EqualTo("5"));
            el.SetAttribute("data-z", "20");
            Assert.That(engine.Compute(el).Get("z-index"), Is.EqualTo("20"));
        }

        [Test]
        public void Time_via_custom_property_and_var_resolves() {
            // Dimension type through custom property + var() indirection
            var doc = Html("<div id=\"x\" data-d=\"300ms\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --dur: attr(data-d time, 0s); transition-duration: var(--dur); }")
            });
            Assert.That(
                engine.Compute(doc.GetElementById("x")).Get("transition-duration"),
                Is.EqualTo("300ms"));
        }
    }
}
