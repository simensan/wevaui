using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Fonts L4 §3 / §6 — cascade coverage for keyword-driven font
    // properties that are registered as string-passthroughs and whose
    // visual effect is partially or fully deferred to a later engine
    // backend pass. The tests pin parse → cascade → Get round-trip and
    // !important / initial / inherit / unset semantics.
    //
    // Properties covered (registration verified in CssProperties):
    //   font-kerning           inherited=true  initial="auto"     §6.6
    //   font-optical-sizing    inherited=true  initial="auto"     §6.10
    //   font-stretch           inherited=true  initial="normal"   §6.3
    //   font-synthesis-position inherited=true initial="auto"     §6.5
    //
    // Visual-side wiring lives in TextRunResolverTests (kerning),
    // FontVariationResolverTests (stretch %), and FontShorthandTests
    // (optical-sizing via shorthand). This file pins the *cascade*
    // round-trip alone — which has never had dedicated coverage.
    public class FontMetricKeywordCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ─── font-kerning ──────────────────────────────────────────────────

        [Test]
        public void FontKerning_initial_is_auto() {
            // §6.6 — initial value MUST be `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("font-kerning"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontKerning_auto_round_trips() {
            var cs = Compute("#x { font-kerning: auto; }");
            Assert.That(cs.Get("font-kerning"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontKerning_normal_round_trips() {
            var cs = Compute("#x { font-kerning: normal; }");
            Assert.That(cs.Get("font-kerning"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontKerning_none_round_trips() {
            var cs = Compute("#x { font-kerning: none; }");
            Assert.That(cs.Get("font-kerning"), Is.EqualTo("none"));
        }

        [Test]
        public void FontKerning_inherits_from_parent() {
            var child = ComputeChild("#p { font-kerning: none; }");
            Assert.That(child.Get("font-kerning"), Is.EqualTo("none"));
        }

        [Test]
        public void FontKerning_child_overrides_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-kerning: none; } #c { font-kerning: normal; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-kerning"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontKerning_important_wins_cascade() {
            var cs = Compute("#x { font-kerning: none !important; font-kerning: normal; }");
            Assert.That(cs.Get("font-kerning"), Is.EqualTo("none"));
        }

        [Test]
        public void FontKerning_initial_keyword_resets_to_auto() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-kerning: none; } #c { font-kerning: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-kerning"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontKerning_inherit_keyword_pulls_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-kerning: none; } #c { font-kerning: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-kerning"), Is.EqualTo("none"));
        }

        [Test]
        public void FontKerning_unset_on_inherited_acts_as_inherit() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-kerning: none; } #c { font-kerning: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-kerning"), Is.EqualTo("none"));
        }

        // ─── font-optical-sizing ──────────────────────────────────────────

        [Test]
        public void FontOpticalSizing_initial_is_auto() {
            // §6.10 — initial value `auto` (the UA drives the `opsz` axis).
            var cs = Compute("");
            Assert.That(cs.Get("font-optical-sizing"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontOpticalSizing_auto_round_trips() {
            var cs = Compute("#x { font-optical-sizing: auto; }");
            Assert.That(cs.Get("font-optical-sizing"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontOpticalSizing_none_round_trips() {
            var cs = Compute("#x { font-optical-sizing: none; }");
            Assert.That(cs.Get("font-optical-sizing"), Is.EqualTo("none"));
        }

        [Test]
        public void FontOpticalSizing_inherits_from_parent() {
            var child = ComputeChild("#p { font-optical-sizing: none; }");
            Assert.That(child.Get("font-optical-sizing"), Is.EqualTo("none"));
        }

        [Test]
        public void FontOpticalSizing_important_wins_cascade() {
            var cs = Compute("#x { font-optical-sizing: none !important; font-optical-sizing: auto; }");
            Assert.That(cs.Get("font-optical-sizing"), Is.EqualTo("none"));
        }

        [Test]
        public void FontOpticalSizing_initial_keyword_resets_to_auto() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-optical-sizing: none; } #c { font-optical-sizing: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-optical-sizing"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontOpticalSizing_unset_acts_as_inherit_on_inherited_property() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-optical-sizing: none; } #c { font-optical-sizing: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-optical-sizing"), Is.EqualTo("none"));
        }

        // ─── font-stretch ─────────────────────────────────────────────────

        [Test]
        public void FontStretch_initial_is_normal() {
            // §6.3 — initial value `normal`.
            var cs = Compute("");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontStretch_keyword_normal_round_trips() {
            var cs = Compute("#x { font-stretch: normal; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontStretch_keyword_ultra_condensed_round_trips() {
            var cs = Compute("#x { font-stretch: ultra-condensed; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("ultra-condensed"));
        }

        [Test]
        public void FontStretch_keyword_extra_condensed_round_trips() {
            var cs = Compute("#x { font-stretch: extra-condensed; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("extra-condensed"));
        }

        [Test]
        public void FontStretch_keyword_condensed_round_trips() {
            var cs = Compute("#x { font-stretch: condensed; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("condensed"));
        }

        [Test]
        public void FontStretch_keyword_semi_condensed_round_trips() {
            var cs = Compute("#x { font-stretch: semi-condensed; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("semi-condensed"));
        }

        [Test]
        public void FontStretch_keyword_semi_expanded_round_trips() {
            var cs = Compute("#x { font-stretch: semi-expanded; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("semi-expanded"));
        }

        [Test]
        public void FontStretch_keyword_expanded_round_trips() {
            var cs = Compute("#x { font-stretch: expanded; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("expanded"));
        }

        [Test]
        public void FontStretch_keyword_extra_expanded_round_trips() {
            var cs = Compute("#x { font-stretch: extra-expanded; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("extra-expanded"));
        }

        [Test]
        public void FontStretch_keyword_ultra_expanded_round_trips() {
            var cs = Compute("#x { font-stretch: ultra-expanded; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("ultra-expanded"));
        }

        [Test]
        public void FontStretch_percentage_round_trips() {
            // §6.3 — percentage is the canonical (post-L4) form. 100% == normal.
            var cs = Compute("#x { font-stretch: 110%; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("110%"));
        }

        [Test]
        public void FontStretch_fractional_percentage_round_trips() {
            var cs = Compute("#x { font-stretch: 87.5%; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("87.5%"));
        }

        [Test]
        public void FontStretch_inherits_from_parent() {
            var child = ComputeChild("#p { font-stretch: condensed; }");
            Assert.That(child.Get("font-stretch"), Is.EqualTo("condensed"));
        }

        [Test]
        public void FontStretch_initial_keyword_resets_to_normal() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-stretch: condensed; } #c { font-stretch: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-stretch"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontStretch_important_wins_cascade() {
            var cs = Compute("#x { font-stretch: condensed !important; font-stretch: expanded; }");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("condensed"));
        }

        // ─── font-synthesis-position ──────────────────────────────────────

        [Test]
        public void FontSynthesisPosition_initial_is_auto() {
            // §6.5 — registered initial = `auto` (UA may synthesize sub/super).
            var cs = Compute("");
            Assert.That(cs.Get("font-synthesis-position"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontSynthesisPosition_auto_round_trips() {
            var cs = Compute("#x { font-synthesis-position: auto; }");
            Assert.That(cs.Get("font-synthesis-position"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontSynthesisPosition_none_round_trips() {
            var cs = Compute("#x { font-synthesis-position: none; }");
            Assert.That(cs.Get("font-synthesis-position"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisPosition_inherits_from_parent() {
            var child = ComputeChild("#p { font-synthesis-position: none; }");
            Assert.That(child.Get("font-synthesis-position"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisPosition_initial_keyword_resets_to_auto() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-synthesis-position: none; } #c { font-synthesis-position: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-synthesis-position"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontSynthesisPosition_important_wins_cascade() {
            var cs = Compute("#x { font-synthesis-position: none !important; font-synthesis-position: auto; }");
            Assert.That(cs.Get("font-synthesis-position"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisPosition_unset_acts_as_inherit_on_inherited_property() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-synthesis-position: none; } #c { font-synthesis-position: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-synthesis-position"), Is.EqualTo("none"));
        }

        // ─── Cross-property independence ──────────────────────────────────

        [Test]
        public void Font_metric_longhands_are_independent_of_each_other() {
            // Setting one font-* metric must NOT bleed into adjacent metric
            // longhands. They share the "font-" prefix but live in distinct
            // cascade slots; the L4 shorthand `font` only resets a defined
            // subset (size, family, style, weight, stretch, line-height, variant).
            // font-kerning / font-optical-sizing / font-synthesis-* are NOT
            // touched by the `font` shorthand reset path.
            var cs = Compute("#x { font-kerning: none; }");
            Assert.That(cs.Get("font-optical-sizing"), Is.EqualTo("auto"),
                "font-optical-sizing must remain at initial when only font-kerning is set");
            Assert.That(cs.Get("font-stretch"), Is.EqualTo("normal"),
                "font-stretch must remain at initial when only font-kerning is set");
            Assert.That(cs.Get("font-synthesis-position"), Is.EqualTo("auto"),
                "font-synthesis-position must remain at initial when only font-kerning is set");
        }
    }
}
