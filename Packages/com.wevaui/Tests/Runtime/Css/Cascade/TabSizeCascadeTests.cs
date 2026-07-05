using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Text L3 §6.1 — `tab-size` cascade.
    //
    // `tab-size` controls the rendered width of U+0009 CHARACTER TABULATION.
    // It is INHERITED. Two value forms are accepted:
    //
    //   <number>   — count of space advances (integer ≥ 0; initial = 8)
    //   <length>   — absolute pixel width of a single tab stop
    //
    // Weva registers `tab-size` as a string-passthrough inherited property.
    // The layout layer (StyleResolver.TabSizeSpaces) reads the cascaded string
    // and converts it to a space-count at box-measurement time.
    //
    // These tests pin the PARSE → CASCADE → GET round-trip; layout-level
    // tests live in StyleResolverParsedValueTests.cs.
    public class TabSizeCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── Initial value ─────────────────────────────────────────────────

        [Test]
        public void Tab_size_initial_value_is_8() {
            // CSS Text 3 §6.1: initial = 8 (space count).
            var cs = Compute("");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("8"));
        }

        // ── Number form ───────────────────────────────────────────────────

        [Test]
        public void Tab_size_number_4_round_trips() {
            var cs = Compute("#x { tab-size: 4; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("4"));
        }

        [Test]
        public void Tab_size_number_2_round_trips() {
            var cs = Compute("#x { tab-size: 2; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("2"));
        }

        [Test]
        public void Tab_size_number_0_round_trips() {
            // `0` is a valid tab-size (collapses tab to nothing); spec says ≥ 0.
            var cs = Compute("#x { tab-size: 0; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("0"));
        }

        // ── Length form ───────────────────────────────────────────────────

        [Test]
        public void Tab_size_px_length_round_trips() {
            // CSS Text 3 §6.1: length values specify absolute tab-stop width.
            var cs = Compute("#x { tab-size: 32px; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("32px"));
        }

        [Test]
        public void Tab_size_em_length_round_trips() {
            var cs = Compute("#x { tab-size: 2em; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("2em"));
        }

        // ── Inheritance ───────────────────────────────────────────────────

        [Test]
        public void Tab_size_inherits_from_parent() {
            // Inherited property — child picks up parent's tab-size automatically.
            var cs = ComputeChild("#parent { tab-size: 4; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("4"),
                "tab-size must be inherited per CSS Text 3 §6.1");
        }

        [Test]
        public void Tab_size_child_can_override_inherited_value() {
            var cs = ComputeChild(
                "#parent { tab-size: 4; } " +
                "#child  { tab-size: 2; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("2"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Tab_size_initial_keyword_restores_8() {
            var cs = Compute("#x { tab-size: initial; }");
            Assert.That(cs.Get("tab-size"), Is.EqualTo("8"));
        }

        [Test]
        public void Tab_size_inherit_keyword_propagates_parent_value() {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { tab-size: 4; } #child { tab-size: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("tab-size"), Is.EqualTo("4"));
        }

        [Test]
        public void Tab_size_unset_on_inherited_property_acts_as_inherit() {
            // `unset` on an inherited property = `inherit`.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { tab-size: 2; } #child { tab-size: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("tab-size"), Is.EqualTo("2"));
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Tab_size_id_beats_element_selector() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { tab-size: 4; } #x { tab-size: 2; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("tab-size"), Is.EqualTo("2"));
        }
    }
}
