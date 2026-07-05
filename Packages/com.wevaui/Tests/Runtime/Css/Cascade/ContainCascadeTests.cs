using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Containment Module Level 2 §3 — `contain` cascade.
    //
    // `contain` is a containment shorthand that lets authors impose isolation
    // on an element's subtree. It accepts keyword tokens that can be combined
    // (except the special `none` / `strict` / `content` single-token forms).
    //
    // Spec: https://www.w3.org/TR/css-contain-2/#contain-property
    //   Initial:    none
    //   Applies to: all elements
    //   Inherited:  NO
    //   Animatable: no
    //   Value:      none | strict | content | [ size || layout || style || paint ]
    //
    // Weva registers `contain` as a string-passthrough non-inherited
    // property (containment layout is not yet fully implemented). The cascade
    // carries the authored value verbatim so that `CreatesStackingContext`
    // can read it for `paint` / `layout` / `strict` / `content` containment.
    //
    // These tests pin the PARSE → CASCADE → GET round-trip only.
    public class ContainCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            engine.Compute(doc.GetElementById("p"));
            return engine.Compute(doc.GetElementById("c"));
        }

        // ── Registration ───────────────────────────────────────────────────

        [Test]
        public void Contain_is_registered() {
            Assert.That(CssProperties.GetId("contain"), Is.GreaterThanOrEqualTo(0));
        }

        // ── Initial value ──────────────────────────────────────────────────

        [Test]
        public void Contain_initial_is_none() {
            // CSS Containment L2 §3: initial value is `none` (no containment).
            var cs = Compute("");
            Assert.That(cs.Get("contain"), Is.EqualTo("none"));
        }

        // ── Single-keyword round-trips ─────────────────────────────────────

        [Test]
        public void Contain_none_round_trips() {
            var cs = Compute("#x { contain: none; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("none"));
        }

        [Test]
        public void Contain_strict_round_trips() {
            // `strict` equals `size layout style paint` per spec §3.
            var cs = Compute("#x { contain: strict; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("strict"));
        }

        [Test]
        public void Contain_content_round_trips() {
            // `content` equals `layout style paint` per spec §3.
            var cs = Compute("#x { contain: content; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("content"));
        }

        [Test]
        public void Contain_size_round_trips() {
            // Size containment: element's intrinsic size ignores subtree.
            var cs = Compute("#x { contain: size; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("size"));
        }

        [Test]
        public void Contain_layout_round_trips() {
            // Layout containment: element is an independent formatting context.
            var cs = Compute("#x { contain: layout; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("layout"));
        }

        [Test]
        public void Contain_style_round_trips() {
            // Style containment: counters / quotes scoped to subtree.
            var cs = Compute("#x { contain: style; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("style"));
        }

        [Test]
        public void Contain_paint_round_trips() {
            // Paint containment: element creates a stacking context and
            // acts as a containing block for abs-pos descendants.
            var cs = Compute("#x { contain: paint; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("paint"));
        }

        // ── Multi-keyword forms ────────────────────────────────────────────

        [Test]
        public void Contain_layout_paint_round_trips() {
            // The spec permits any combination of the four atom keywords.
            var cs = Compute("#x { contain: layout paint; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("layout paint"));
        }

        [Test]
        public void Contain_size_layout_style_paint_round_trips() {
            // All four atoms together is equivalent to `strict`.
            var cs = Compute("#x { contain: size layout style paint; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("size layout style paint"));
        }

        // ── Non-inheritance ────────────────────────────────────────────────

        [Test]
        public void Contain_does_not_inherit() {
            // CSS Containment L2 §3: Inherited: no.
            var cs = ComputeChild("div { contain: paint; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("none"),
                "contain is non-inherited; child must see initial 'none'");
        }

        // ── CSS-wide keywords ──────────────────────────────────────────────

        [Test]
        public void Contain_initial_keyword_resolves_to_none() {
            var cs = Compute("#x { contain: paint; } " +
                             "#x { contain: initial; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("none"),
                "initial keyword must resolve to spec initial 'none'");
        }

        [Test]
        public void Contain_unset_on_non_inherited_resolves_to_initial() {
            var cs = Compute("#x { contain: strict; } " +
                             "#x { contain: unset; }");
            Assert.That(cs.Get("contain"), Is.EqualTo("none"),
                "unset on non-inherited must yield 'none'");
        }

        // ── Cascade mechanics ──────────────────────────────────────────────

        [Test]
        public void Contain_higher_specificity_wins() {
            var doc = Html("<div id=\"x\" class=\"a\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { contain: layout; } " +
                       "#x  { contain: paint; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("contain"), Is.EqualTo("paint"),
                "#x (id) specificity must beat div (type)");
        }

        [Test]
        public void Contain_important_beats_higher_specificity_normal() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { contain: strict !important; } " +
                       "#x  { contain: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("contain"), Is.EqualTo("strict"),
                "!important on low-specificity rule must win over higher-specificity normal");
        }
    }
}
