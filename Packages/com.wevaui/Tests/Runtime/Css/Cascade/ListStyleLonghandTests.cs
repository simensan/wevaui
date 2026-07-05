using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Lists Module Level 3 §3 — `list-style-type`, `list-style-position`,
    // `list-style-image` longhand cascade coverage.
    //
    // The three longhands are all INHERITED per CSS Lists L3 §6, registered
    // in CssProperties.cs:963-981 with initial values:
    //   list-style-type:     "disc"
    //   list-style-position: "outside"
    //   list-style-image:    "none"
    //
    // ListStyleShorthandTests (Shorthands/) covers the shorthand expander
    // grammar. This file pins the longhands directly:
    //   - spec initial value when no rule applies
    //   - keyword / value round-trips for the CSS Counter Styles 3 §6 set
    //   - INHERITED: parent value propagates to child that has no own value
    //   - author value takes precedence over inherited parent value
    //
    // Counter-styles that ListMarkerStyle.MarkerText does not yet handle
    // (lower-greek, disclosure-open, disclosure-closed) are NOT in the cascade
    // layer test scope — the cascade stores and echoes any keyword as a string;
    // the layout layer may substitute a fallback glyph. The cascade round-trip
    // must pass regardless of rendering support.
    //
    // The `list-style` shorthand interaction is exercised by
    // ListStyleShorthandTests; this file avoids duplicating those tests.
    public class ListStyleLonghandTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string parentCss) {
            // Child is a span nested inside the div that holds the parent rule.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(parentCss) });
            engine.Compute(doc.GetElementById("parent"));
            return engine.Compute(doc.GetElementById("child"));
        }

        // ══════════════════════════════════════════════════════════════════
        // list-style-type — CSS Lists L3 §3.2
        // ══════════════════════════════════════════════════════════════════

        // ── initial value ────────────────────────────────────────────────

        [Test]
        public void List_style_type_initial_is_disc() {
            // CSS Lists L3 §3.2: initial value is `disc`. The UA stylesheet
            // may override this for <ol> (decimal) but the raw registered
            // initial must be disc.
            var cs = Compute("");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("disc"));
        }

        // ── bullet shapes (CSS Counter Styles 3 §6 symbolic) ────────────

        [Test]
        public void List_style_type_disc_round_trips() {
            var cs = Compute("#x { list-style-type: disc; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("disc"));
        }

        [Test]
        public void List_style_type_circle_round_trips() {
            // CSS Counter Styles 3 §6: `circle` is a predefined symbolic
            // counter style (hollow bullet).
            var cs = Compute("#x { list-style-type: circle; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("circle"));
        }

        [Test]
        public void List_style_type_square_round_trips() {
            // CSS Counter Styles 3 §6: `square` is a predefined symbolic
            // counter style (filled square).
            var cs = Compute("#x { list-style-type: square; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("square"));
        }

        // ── numeric counter styles ───────────────────────────────────────

        [Test]
        public void List_style_type_decimal_round_trips() {
            // §6: `decimal` — Arabic numerals 1, 2, 3 … (default for <ol>
            // via the UA stylesheet).
            var cs = Compute("#x { list-style-type: decimal; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("decimal"));
        }

        [Test]
        public void List_style_type_decimal_leading_zero_round_trips() {
            // §6: `decimal-leading-zero` — 01, 02, … 09, 10, 11, …
            var cs = Compute("#x { list-style-type: decimal-leading-zero; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("decimal-leading-zero"));
        }

        // ── roman numeral counter styles ────────────────────────────────

        [Test]
        public void List_style_type_lower_roman_round_trips() {
            // §6: `lower-roman` — i, ii, iii, iv, …
            var cs = Compute("#x { list-style-type: lower-roman; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("lower-roman"));
        }

        [Test]
        public void List_style_type_upper_roman_round_trips() {
            // §6: `upper-roman` — I, II, III, IV, …
            var cs = Compute("#x { list-style-type: upper-roman; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("upper-roman"));
        }

        // ── alphabetic counter styles ────────────────────────────────────

        [Test]
        public void List_style_type_lower_alpha_round_trips() {
            // §6: `lower-alpha` — a, b, c, …, z, aa, ab, …
            var cs = Compute("#x { list-style-type: lower-alpha; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("lower-alpha"));
        }

        [Test]
        public void List_style_type_upper_alpha_round_trips() {
            // §6: `upper-alpha` — A, B, C, …
            var cs = Compute("#x { list-style-type: upper-alpha; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("upper-alpha"));
        }

        [Test]
        public void List_style_type_lower_latin_round_trips() {
            // §6: `lower-latin` is an alias for `lower-alpha` in the spec.
            // The cascade stores whatever the author wrote, so the stored
            // value is "lower-latin", not "lower-alpha".
            var cs = Compute("#x { list-style-type: lower-latin; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("lower-latin"));
        }

        [Test]
        public void List_style_type_upper_latin_round_trips() {
            // §6: `upper-latin` is an alias for `upper-alpha`.
            var cs = Compute("#x { list-style-type: upper-latin; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("upper-latin"));
        }

        // ── greek counter style ──────────────────────────────────────────

        [Test]
        public void List_style_type_lower_greek_round_trips() {
            // CSS Counter Styles 3 §6: `lower-greek` uses lowercase Greek
            // letters. The cascade must store and echo the keyword even
            // though ListMarkerStyle.MarkerText falls back to disc for
            // unrecognised types. Cascade ≠ rendering.
            var cs = Compute("#x { list-style-type: lower-greek; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("lower-greek"));
        }

        // ── none ─────────────────────────────────────────────────────────

        [Test]
        public void List_style_type_none_round_trips() {
            // §3.2: `none` suppresses the marker entirely. Distinct from the
            // `list-style: none` shorthand form — here only the type is none.
            var cs = Compute("#x { list-style-type: none; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("none"));
        }

        // ── disclosure counter styles ────────────────────────────────────

        [Test]
        public void List_style_type_disclosure_open_round_trips() {
            // CSS Counter Styles 3 §6: `disclosure-open` / `disclosure-closed`
            // are used for disclosure widgets (details/summary). The cascade
            // stores them as-is; rendering may fall back.
            var cs = Compute("#x { list-style-type: disclosure-open; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("disclosure-open"));
        }

        [Test]
        public void List_style_type_disclosure_closed_round_trips() {
            var cs = Compute("#x { list-style-type: disclosure-closed; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("disclosure-closed"));
        }

        // ── <string> counter style ───────────────────────────────────────

        [Test]
        public void List_style_type_string_literal_round_trips() {
            // CSS Lists L3 §3.2: list-style-type accepts a <string> token
            // (e.g. "→ " or "-"). The cascade stores it verbatim including
            // the surrounding quotes.
            var cs = Compute("#x { list-style-type: \"-\"; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("\"-\""));
        }

        // ── inheritance (CSS Lists L3 §6: all three are inherited) ──────

        [Test]
        public void List_style_type_is_inherited_by_child() {
            // CSS Lists L3 §6: list-style-type INHERITED=yes. A parent with
            // an explicit value must propagate it to a child with no own rule.
            var cs = ComputeChild("#parent { list-style-type: decimal; }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("decimal"),
                "list-style-type is inherited; child must see parent's decimal, not initial disc");
        }

        [Test]
        public void List_style_type_child_own_value_overrides_inherited() {
            // Author sets circle on the child; parent has decimal. The child's
            // own rule wins and inheritance is NOT applied.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { list-style-type: decimal; } #child { list-style-type: circle; }")
            });
            engine.Compute(doc.GetElementById("parent"));
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("circle"),
                "child's own rule must win over inherited parent value");
        }

        // ══════════════════════════════════════════════════════════════════
        // list-style-position — CSS Lists L3 §3.1
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void List_style_position_initial_is_outside() {
            // CSS Lists L3 §3.1: initial value `outside`. The marker box
            // is placed in the left margin, outside the principal box.
            var cs = Compute("");
            Assert.That(cs.Get("list-style-position"), Is.EqualTo("outside"));
        }

        [Test]
        public void List_style_position_inside_round_trips() {
            // §3.1: `inside` — the marker participates in the normal inline
            // flow of the list item.
            var cs = Compute("#x { list-style-position: inside; }");
            Assert.That(cs.Get("list-style-position"), Is.EqualTo("inside"));
        }

        [Test]
        public void List_style_position_outside_round_trips() {
            var cs = Compute("#x { list-style-position: outside; }");
            Assert.That(cs.Get("list-style-position"), Is.EqualTo("outside"));
        }

        [Test]
        public void List_style_position_is_inherited_by_child() {
            // CSS Lists L3 §6: list-style-position INHERITED=yes.
            var cs = ComputeChild("#parent { list-style-position: inside; }");
            Assert.That(cs.Get("list-style-position"), Is.EqualTo("inside"),
                "list-style-position is inherited; child must see parent's inside value");
        }

        // ══════════════════════════════════════════════════════════════════
        // list-style-image — CSS Lists L3 §3.3
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void List_style_image_initial_is_none() {
            // CSS Lists L3 §3.3: initial value `none`. No image marker
            // is used; list-style-type controls the glyph instead.
            var cs = Compute("");
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("none"));
        }

        [Test]
        public void List_style_image_none_round_trips() {
            var cs = Compute("#x { list-style-image: none; }");
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("none"));
        }

        [Test]
        public void List_style_image_url_round_trips() {
            // §3.3: `url(...)` specifies an image resource to use as the
            // marker. The cascade stores it verbatim; BoxBuilder.MaybeInject-
            // ListMarker reads the value and sets background-image on the
            // marker box.
            var cs = Compute("#x { list-style-image: url(bullet.png); }");
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("url(bullet.png)"));
        }

        [Test]
        public void List_style_image_gradient_round_trips() {
            // CSS Images 4 allows gradient functions as image values.
            // The cascade stores the author string; rendering is speculative.
            var cs = Compute("#x { list-style-image: linear-gradient(red, blue); }");
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("linear-gradient(red, blue)"));
        }

        [Test]
        public void List_style_image_is_inherited_by_child() {
            // CSS Lists L3 §6: list-style-image INHERITED=yes.
            var cs = ComputeChild("#parent { list-style-image: url(b.png); }");
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("url(b.png)"),
                "list-style-image is inherited; child must see parent's url() value");
        }

        [Test]
        public void List_style_image_child_none_overrides_inherited_url() {
            // Author explicitly resets list-style-image to none on the child.
            // The child's explicit none must override the inherited url().
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { list-style-image: url(b.png); } #child { list-style-image: none; }")
            });
            engine.Compute(doc.GetElementById("parent"));
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("none"),
                "child's own none must override the inherited url()");
        }

        // ══════════════════════════════════════════════════════════════════
        // Combined: cascade specificity between list-style longhands
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void All_three_longhands_coexist_in_one_rule() {
            // Author sets all three independently; all three must survive the
            // cascade independently with their own values. This guards against
            // a shorthand-only code path that would ignore longhands.
            var cs = Compute(
                "#x { list-style-type: circle; list-style-position: inside; list-style-image: url(b.png); }");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("circle"));
            Assert.That(cs.Get("list-style-position"), Is.EqualTo("inside"));
            Assert.That(cs.Get("list-style-image"), Is.EqualTo("url(b.png)"));
        }
    }
}
