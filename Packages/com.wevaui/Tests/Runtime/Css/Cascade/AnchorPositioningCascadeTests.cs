using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Anchor Positioning L1 — cascade coverage for the three core
    // properties: `anchor-name`, `position-anchor`, and
    // `position-try-fallbacks`. All three are registered in
    // CssProperties (lines 1019-1021) as NON-INHERITED. The visual
    // positioning side lives in `AnchorPositioning*` runtime code and
    // is already exercised by AnchorIntegrationTests / AnchorV2Tests;
    // these tests pin the parse → cascade → Get round-trip alone.
    //
    // Spec references:
    //   anchor-name             — CSS Anchor Positioning §3.1
    //                             (initial=none, not inherited)
    //   position-anchor         — CSS Anchor Positioning §3.2
    //                             (initial=auto, not inherited)
    //   position-try-fallbacks  — CSS Anchor Positioning §6.1
    //                             (initial=none, not inherited)
    //
    // These three properties were only sized by InheritanceFlagSweep
    // (a meta-test enforcing the inherits/initial table); their full
    // !important / cascade-keyword / cross-property behaviour wasn't
    // covered.
    public class AnchorPositioningCascadeTests {
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

        // ─── anchor-name ──────────────────────────────────────────────────

        [Test]
        public void AnchorName_initial_is_none() {
            // §3.1: initial = `none` (no anchor identifier).
            var cs = Compute("");
            Assert.That(cs.Get("anchor-name"), Is.EqualTo("none"));
        }

        [Test]
        public void AnchorName_none_round_trips() {
            var cs = Compute("#x { anchor-name: none; }");
            Assert.That(cs.Get("anchor-name"), Is.EqualTo("none"));
        }

        [Test]
        public void AnchorName_dashed_ident_round_trips() {
            // §3.1: value is `<dashed-ident>#` — one or more --foo names.
            var cs = Compute("#x { anchor-name: --hero-anchor; }");
            Assert.That(cs.Get("anchor-name"), Is.EqualTo("--hero-anchor"));
        }

        [Test]
        public void AnchorName_comma_list_round_trips() {
            // §3.1: multiple anchor names assign the same element to all.
            var cs = Compute("#x { anchor-name: --a, --b; }");
            var v = cs.Get("anchor-name");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("--a"));
            Assert.That(v, Does.Contain("--b"));
        }

        [Test]
        public void AnchorName_does_not_inherit() {
            // §3.1: anchor-name is NOT inherited — every anchored element
            // must declare its own identifier.
            var child = ComputeChild("#p { anchor-name: --hero; }");
            Assert.That(child.Get("anchor-name"), Is.EqualTo("none"));
        }

        [Test]
        public void AnchorName_important_wins_cascade() {
            var cs = Compute("#x { anchor-name: --a !important; anchor-name: --b; }");
            Assert.That(cs.Get("anchor-name"), Is.EqualTo("--a"));
        }

        [Test]
        public void AnchorName_initial_keyword_resets_to_none() {
            var cs = Compute("#x { anchor-name: --hero; anchor-name: initial; }");
            Assert.That(cs.Get("anchor-name"), Is.EqualTo("none"));
        }

        [Test]
        public void AnchorName_unset_on_non_inherited_acts_as_initial() {
            // CSS Cascade L5 §7.3 — unset on a non-inherited property =
            // initial. So even if a parent has --p, the child resolves
            // to `none` not `--p`.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { anchor-name: --p; } #c { anchor-name: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("anchor-name"), Is.EqualTo("none"));
        }

        [Test]
        public void AnchorName_inherit_keyword_explicitly_pulls_parent() {
            // CSS Cascade L5 §7.2 — explicit `inherit` always works,
            // even on non-inherited properties.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { anchor-name: --p; } #c { anchor-name: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("anchor-name"), Is.EqualTo("--p"));
        }

        // ─── position-anchor ──────────────────────────────────────────────

        [Test]
        public void PositionAnchor_initial_is_auto() {
            // §3.2: initial = `auto` (engine picks default anchor).
            var cs = Compute("");
            Assert.That(cs.Get("position-anchor"), Is.EqualTo("auto"));
        }

        [Test]
        public void PositionAnchor_auto_round_trips() {
            var cs = Compute("#x { position-anchor: auto; }");
            Assert.That(cs.Get("position-anchor"), Is.EqualTo("auto"));
        }

        [Test]
        public void PositionAnchor_dashed_ident_round_trips() {
            // §3.2: value is a `<dashed-ident>` referencing an
            // anchor-named ancestor or named anchor.
            var cs = Compute("#x { position-anchor: --tooltip-anchor; }");
            Assert.That(cs.Get("position-anchor"), Is.EqualTo("--tooltip-anchor"));
        }

        [Test]
        public void PositionAnchor_does_not_inherit() {
            // §3.2: not inherited — each positioned element resolves
            // its own anchor reference.
            var child = ComputeChild("#p { position-anchor: --p; }");
            Assert.That(child.Get("position-anchor"), Is.EqualTo("auto"));
        }

        [Test]
        public void PositionAnchor_important_wins_cascade() {
            var cs = Compute("#x { position-anchor: --a !important; position-anchor: --b; }");
            Assert.That(cs.Get("position-anchor"), Is.EqualTo("--a"));
        }

        [Test]
        public void PositionAnchor_initial_keyword_resets_to_auto() {
            var cs = Compute("#x { position-anchor: --hero; position-anchor: initial; }");
            Assert.That(cs.Get("position-anchor"), Is.EqualTo("auto"));
        }

        [Test]
        public void PositionAnchor_inherit_keyword_explicitly_pulls_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { position-anchor: --p; } #c { position-anchor: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("position-anchor"), Is.EqualTo("--p"));
        }

        // ─── position-try-fallbacks ──────────────────────────────────────

        [Test]
        public void PositionTryFallbacks_initial_is_none() {
            // §6.1: initial = `none`.
            var cs = Compute("");
            Assert.That(cs.Get("position-try-fallbacks"), Is.EqualTo("none"));
        }

        [Test]
        public void PositionTryFallbacks_none_round_trips() {
            var cs = Compute("#x { position-try-fallbacks: none; }");
            Assert.That(cs.Get("position-try-fallbacks"), Is.EqualTo("none"));
        }

        [Test]
        public void PositionTryFallbacks_keyword_round_trips() {
            // §6.1: value is `[ [<dashed-ident> | <try-tactic> | inset(...) ] ... ]#`.
            // The flip-block keyword is the simplest validated form.
            var cs = Compute("#x { position-try-fallbacks: flip-block; }");
            Assert.That(cs.Get("position-try-fallbacks"), Is.EqualTo("flip-block"));
        }

        [Test]
        public void PositionTryFallbacks_does_not_inherit() {
            // §6.1: not inherited.
            var child = ComputeChild("#p { position-try-fallbacks: flip-block; }");
            Assert.That(child.Get("position-try-fallbacks"), Is.EqualTo("none"));
        }

        [Test]
        public void PositionTryFallbacks_important_wins_cascade() {
            var cs = Compute("#x { position-try-fallbacks: flip-block !important; position-try-fallbacks: none; }");
            Assert.That(cs.Get("position-try-fallbacks"), Is.EqualTo("flip-block"));
        }

        [Test]
        public void PositionTryFallbacks_initial_keyword_resets_to_none() {
            var cs = Compute("#x { position-try-fallbacks: flip-block; position-try-fallbacks: initial; }");
            Assert.That(cs.Get("position-try-fallbacks"), Is.EqualTo("none"));
        }

        // ─── Cross-property independence ──────────────────────────────────

        [Test]
        public void Anchor_positioning_longhands_are_independent() {
            // Setting one anchor-* property must NOT bleed into the
            // other two. The cascade keeps them in distinct slots.
            var cs = Compute("#x { anchor-name: --hero; }");
            Assert.That(cs.Get("anchor-name"), Is.EqualTo("--hero"));
            Assert.That(cs.Get("position-anchor"), Is.EqualTo("auto"),
                "position-anchor must remain at initial when only anchor-name is set");
            Assert.That(cs.Get("position-try-fallbacks"), Is.EqualTo("none"),
                "position-try-fallbacks must remain at initial when only anchor-name is set");

            var cs2 = Compute("#x { position-anchor: --target; }");
            Assert.That(cs2.Get("anchor-name"), Is.EqualTo("none"),
                "anchor-name must remain at initial when only position-anchor is set");
        }
    }
}
