using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS UI Level 4 §3 — outline longhand cascade coverage.
    //
    // The four outline longhands (outline-color, outline-style, outline-width,
    // outline-offset) are non-inherited, per-element ring decoration that paints
    // OUTSIDE the border edge and does NOT affect layout (i.e. it does not
    // contribute to the element's used width/height or displace siblings).
    //
    // Spec-mandated initial values:
    //   outline-color:  invert  (UA approximates with currentColor in v1)
    //   outline-style:  none
    //   outline-width:  medium
    //   outline-offset: 0
    //
    // All four longhands are non-inherited — a parent rule must NOT leak to a
    // child without an explicit rule.
    //
    // The `outline` shorthand and its expander are covered in
    // Shorthands/OutlineShorthandTests.cs. This file covers only the
    // longhand parse → cascade → Get round-trips.
    public class OutlineLonghandTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── outline-color ─────────────────────────────────────────────────

        [Test]
        public void Outline_color_initial_is_invert() {
            // CSS UI 4 §3.4: initial value is `invert`.
            var cs = Compute("");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("invert"));
        }

        [Test]
        public void Outline_color_named_color_round_trips() {
            var cs = Compute("#x { outline-color: red; }");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Outline_color_hex_round_trips() {
            var cs = Compute("#x { outline-color: #3a7bd5; }");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("#3a7bd5"));
        }

        [Test]
        public void Outline_color_currentcolor_round_trips() {
            // CSS UI 4 §3.4: `currentcolor` is a valid value; paints the ring
            // in the element's inherited `color` value at paint time.
            var cs = Compute("#x { outline-color: currentcolor; }");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Outline_color_oklch_round_trips() {
            var cs = Compute("#x { outline-color: oklch(60% 0.15 250); }");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("oklch(60% 0.15 250)"));
        }

        [Test]
        public void Outline_color_invert_keyword_round_trips() {
            // `invert` is the unique spec-defined initial for outline-color and
            // is valid only on this property (not on border-color etc.).
            var cs = Compute("#x { outline-color: invert; }");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("invert"));
        }

        [Test]
        public void Outline_color_does_not_inherit() {
            // CSS UI 4 §3.4: outline-color is non-inherited.
            var cs = ComputeChild("#p { outline-color: blue; }");
            Assert.That(cs.Get("outline-color"), Is.EqualTo("invert"),
                "outline-color is non-inherited; child sees initial value");
        }

        // ── outline-style ─────────────────────────────────────────────────

        [Test]
        public void Outline_style_initial_is_none() {
            // CSS UI 4 §3.3: initial value `none`.
            var cs = Compute("");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("none"));
        }

        [Test]
        public void Outline_style_auto_round_trips() {
            // CSS UI 4 §3.3: `auto` is the value used by UA-drawn focus rings;
            // it is NOT a valid value on border-style but IS valid on
            // outline-style. This is the most common value for accessibility
            // focus indicators.
            var cs = Compute("#x { outline-style: auto; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("auto"));
        }

        [Test]
        public void Outline_style_solid_round_trips() {
            var cs = Compute("#x { outline-style: solid; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("solid"));
        }

        [Test]
        public void Outline_style_dashed_round_trips() {
            var cs = Compute("#x { outline-style: dashed; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("dashed"));
        }

        [Test]
        public void Outline_style_dotted_round_trips() {
            var cs = Compute("#x { outline-style: dotted; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("dotted"));
        }

        [Test]
        public void Outline_style_double_round_trips() {
            var cs = Compute("#x { outline-style: double; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("double"));
        }

        [Test]
        public void Outline_style_groove_round_trips() {
            var cs = Compute("#x { outline-style: groove; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("groove"));
        }

        [Test]
        public void Outline_style_ridge_round_trips() {
            var cs = Compute("#x { outline-style: ridge; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("ridge"));
        }

        [Test]
        public void Outline_style_inset_round_trips() {
            var cs = Compute("#x { outline-style: inset; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("inset"));
        }

        [Test]
        public void Outline_style_outset_round_trips() {
            var cs = Compute("#x { outline-style: outset; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("outset"));
        }

        [Test]
        public void Outline_style_hidden_round_trips() {
            // CSS UI 4 §3.3 lists `hidden` as a valid outline-style value
            // (same as none for outlines; unlike border-style, `hidden` is not
            // special for table-border conflict resolution here).
            var cs = Compute("#x { outline-style: hidden; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("hidden"));
        }

        [Test]
        public void Outline_style_does_not_inherit() {
            var cs = ComputeChild("#p { outline-style: solid; }");
            Assert.That(cs.Get("outline-style"), Is.EqualTo("none"),
                "outline-style is non-inherited; child sees initial none");
        }

        // ── outline-width ─────────────────────────────────────────────────

        [Test]
        public void Outline_width_initial_is_medium() {
            // CSS UI 4 §3.2: initial value `medium`.
            var cs = Compute("");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("medium"));
        }

        [Test]
        public void Outline_width_thin_keyword_round_trips() {
            // CSS2.1 §8.5.1: `thin` maps to a UA-defined value < medium.
            var cs = Compute("#x { outline-width: thin; }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("thin"));
        }

        [Test]
        public void Outline_width_medium_keyword_round_trips() {
            var cs = Compute("#x { outline-width: medium; }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("medium"));
        }

        [Test]
        public void Outline_width_thick_keyword_round_trips() {
            // `thick` maps to a UA-defined value > medium.
            var cs = Compute("#x { outline-width: thick; }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("thick"));
        }

        [Test]
        public void Outline_width_px_length_round_trips() {
            var cs = Compute("#x { outline-width: 3px; }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("3px"));
        }

        [Test]
        public void Outline_width_em_length_round_trips() {
            var cs = Compute("#x { outline-width: 0.125em; }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("0.125em"));
        }

        [Test]
        public void Outline_width_calc_round_trips() {
            var cs = Compute("#x { outline-width: calc(1px + 1em); }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("calc(1px + 1em)"));
        }

        [Test]
        public void Outline_width_does_not_inherit() {
            var cs = ComputeChild("#p { outline-width: 5px; }");
            Assert.That(cs.Get("outline-width"), Is.EqualTo("medium"),
                "outline-width is non-inherited; child sees initial medium");
        }

        // ── outline-offset ────────────────────────────────────────────────

        [Test]
        public void Outline_offset_initial_is_zero() {
            // CSS UI 4 §3.5: initial value `0`.
            var cs = Compute("");
            Assert.That(cs.Get("outline-offset"), Is.EqualTo("0"));
        }

        [Test]
        public void Outline_offset_positive_px_round_trips() {
            // Positive offset pushes the ring away from the border edge.
            var cs = Compute("#x { outline-offset: 4px; }");
            Assert.That(cs.Get("outline-offset"), Is.EqualTo("4px"));
        }

        [Test]
        public void Outline_offset_negative_px_round_trips() {
            // Negative offset draws the ring inside the border edge.
            // CSS UI 4 §3.5 explicitly permits negative values.
            var cs = Compute("#x { outline-offset: -2px; }");
            Assert.That(cs.Get("outline-offset"), Is.EqualTo("-2px"));
        }

        [Test]
        public void Outline_offset_em_round_trips() {
            var cs = Compute("#x { outline-offset: 0.25em; }");
            Assert.That(cs.Get("outline-offset"), Is.EqualTo("0.25em"));
        }

        [Test]
        public void Outline_offset_calc_round_trips() {
            var cs = Compute("#x { outline-offset: calc(2px + 0.5em); }");
            Assert.That(cs.Get("outline-offset"), Is.EqualTo("calc(2px + 0.5em)"));
        }

        [Test]
        public void Outline_offset_does_not_inherit() {
            var cs = ComputeChild("#p { outline-offset: 8px; }");
            Assert.That(cs.Get("outline-offset"), Is.EqualTo("0"),
                "outline-offset is non-inherited; child sees initial 0");
        }

        // ── spec contract: outline does not affect layout ──────────────────

        [Test]
        public void Outline_longhands_are_all_non_inherited_in_registry() {
            // Verify CssProperties.IsInherited returns false for all four
            // outline longhands — the flag drives FillInherited and the
            // inherited-mask bitset.
            Assert.That(CssProperties.IsInherited("outline-color"), Is.False,
                "outline-color must be non-inherited per CSS UI 4 §3.4");
            Assert.That(CssProperties.IsInherited("outline-style"), Is.False,
                "outline-style must be non-inherited per CSS UI 4 §3.3");
            Assert.That(CssProperties.IsInherited("outline-width"), Is.False,
                "outline-width must be non-inherited per CSS UI 4 §3.2");
            Assert.That(CssProperties.IsInherited("outline-offset"), Is.False,
                "outline-offset must be non-inherited per CSS UI 4 §3.5");
        }

        [Test]
        public void Outline_cascade_priority_higher_specificity_wins() {
            // Two rules both targeting the same element; higher-specificity rule wins.
            var doc = Html("<div id=\"x\" class=\"ring\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".ring { outline-color: blue; } #x { outline-color: gold; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("outline-color"), Is.EqualTo("gold"),
                "id selector (#x) has higher specificity than class (.ring)");
        }
    }
}
