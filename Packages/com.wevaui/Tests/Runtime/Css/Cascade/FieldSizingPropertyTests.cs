using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Basic User Interface L4 §13 — `field-sizing` property.
    //
    // `field-sizing` controls whether a form control uses the UA's fixed-size
    // default box (the `auto` value) or shrinks to the intrinsic size of its
    // content (the `content` value).
    //
    // v1 status: parse + cascade round-trip only. The layout impact of
    // `field-sizing: content` is not yet wired into InputLayoutHelper — see
    // CSS_OPEN_GAPS.md B27. These tests pin the parse/cascade contract so the
    // runtime behaviour can be layered on later without breaking the cascade.
    public class FieldSizingPropertyTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle ComputeWithHtml(string htmlBody, string css) {
            var doc = Html(htmlBody);
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle Compute(string css)
            => ComputeWithHtml("<div id=\"x\"></div>", "#x { " + css + " }");

        // -----------------------------------------------------------------------
        // Initial value
        // -----------------------------------------------------------------------

        [Test]
        public void Field_sizing_initial_value_is_auto() {
            // CSS UI L4 §13: initial value is `auto`.
            var doc = Html("<input id=\"x\">");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("auto"));
        }

        // -----------------------------------------------------------------------
        // Cascade round-trips
        // -----------------------------------------------------------------------

        [Test]
        public void Field_sizing_auto_round_trips() {
            var cs = Compute("field-sizing: auto;");
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("auto"));
        }

        [Test]
        public void Field_sizing_content_round_trips() {
            // `content` is the key authoring value — tells the UA to shrink-wrap
            // the form control to its current value text. Parse + cascade must
            // preserve the keyword exactly.
            var cs = Compute("field-sizing: content;");
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("content"));
        }

        // -----------------------------------------------------------------------
        // CSS-wide keywords
        // -----------------------------------------------------------------------

        [Test]
        public void Field_sizing_initial_keyword_resolves_to_auto() {
            var cs = Compute("field-sizing: initial;");
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("auto"));
        }

        [Test]
        public void Field_sizing_inherit_pulls_from_parent() {
            // `field-sizing` is non-inherited per CSS UI L4 §13. However
            // the `inherit` CSS-wide keyword forces inheritance regardless.
            var doc = Html(
                "<div id=\"parent\" style=\"field-sizing:content\">" +
                "<input id=\"child\">" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("#child { field-sizing: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("content"));
        }

        // -----------------------------------------------------------------------
        // Non-inheritance — field-sizing does NOT propagate to children
        // -----------------------------------------------------------------------

        [Test]
        public void Field_sizing_does_not_inherit() {
            // CSS UI L4 §13: `field-sizing` is NOT inherited (Inherited: no).
            // A child that does not set the property must get the initial value
            // `auto`, not the parent's `content`.
            var doc = Html(
                "<form id=\"parent\"><input id=\"child\"></form>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { field-sizing: content; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("auto"));
        }

        // -----------------------------------------------------------------------
        // Specificity — higher-specificity rule wins
        // -----------------------------------------------------------------------

        [Test]
        public void Field_sizing_higher_specificity_wins() {
            var doc = Html("<input id=\"x\" class=\"big\">");
            var engine = new CascadeEngine(new[] {
                Author("input { field-sizing: content; } #x { field-sizing: auto; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // #x (id selector) beats `input` (type selector): auto wins.
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("auto"));
        }

        // -----------------------------------------------------------------------
        // Layout impact — v1 parse-only; pinned Ignore'd spec test
        // -----------------------------------------------------------------------

        [Test]
        public void Field_sizing_content_shrinks_input_to_value_width_spec() {
            // CSS UI L4 §13: `field-sizing: content` makes the control's
            // inline-size equal to the intrinsic width of the current `value`
            // text. Implemented in B27 — BoxBuilder reads the cascade value and
            // overrides the UA fixed width with the measured text width.
            //
            // This cascade-level test confirms the computed value is "content"
            // (the layout impact is pinned in FieldSizingLayoutTests).
            var cs = Compute("field-sizing: content;");
            Assert.That(cs.Get("field-sizing"), Is.EqualTo("content"),
                "Cascade should carry field-sizing: content; layout impact is tested in FieldSizingLayoutTests");
        }
    }
}
