using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Scrolling.Smooth;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Scroll Behavior Module Level 1 §3 — `scroll-behavior` cascade tests.
    //
    // `scroll-behavior` is registered lazily in SmoothScrollProperties.cs.
    // InheritanceFlagSweepTests already pins that the property is NOT inherited.
    // These tests cover:
    //   - Registration (property exists, initial value, non-inherited)
    //   - Keyword round-trips: auto, smooth (instant is spec-allowed as v2 addition)
    //   - Cascade specificity override
    //   - CSS-wide keywords: unset/initial revert to auto
    //   - Non-inheritance: parent scroll-behavior: smooth must not reach child
    //   - var() custom property fallback
    //
    // Spec ref: CSS Scroll Behavior 1 §3
    public class ScrollBehaviorCascadeTests {
        static ScrollBehaviorCascadeTests() { SmoothScrollProperties.EnsureRegistered(); }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── Registration ─────────────────────────────────────────────────

        [Test]
        public void Scroll_behavior_is_registered() {
            // Property must exist in the registry.
            Assert.That(CssProperties.TryGet("scroll-behavior", out _), Is.True,
                "scroll-behavior must be registered (SmoothScrollProperties.EnsureRegistered)");
        }

        [Test]
        public void Scroll_behavior_initial_is_auto() {
            // CSS Scroll Behavior 1 §3: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scroll_behavior_is_not_inherited() {
            // §3: scroll-behavior is NOT inherited.
            Assert.That(CssProperties.IsInherited("scroll-behavior"), Is.False,
                "scroll-behavior must be non-inherited");
        }

        // ── Keyword round-trips ───────────────────────────────────────────

        [Test]
        public void Scroll_behavior_smooth_round_trips() {
            // §3: `smooth` triggers animated scrolling.
            var cs = Compute("#x { scroll-behavior: smooth; }");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("smooth"));
        }

        [Test]
        public void Scroll_behavior_auto_explicit_round_trips() {
            var cs = Compute("#x { scroll-behavior: auto; }");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Scroll_behavior_initial_keyword_restores_auto() {
            // `initial` must force the property back to its initial value `auto`.
            var cs = Compute("#x { scroll-behavior: smooth; } #x { scroll-behavior: initial; }");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"),
                "initial keyword must restore scroll-behavior to auto");
        }

        [Test]
        public void Scroll_behavior_unset_equals_initial_for_non_inherited_property() {
            // `unset` on a non-inherited property acts as `initial`.
            var cs = Compute("#x { scroll-behavior: smooth; } #x { scroll-behavior: unset; }");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"),
                "unset on non-inherited property must be equivalent to initial");
        }

        // ── Cascade specificity ───────────────────────────────────────────

        [Test]
        public void Higher_specificity_wins_over_lower() {
            // Class selector (0,1,0) beats tag selector (0,0,1).
            var doc = Html("<div id=\"x\" class=\"scroller\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { scroll-behavior: smooth; } .scroller { scroll-behavior: auto; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"),
                "higher-specificity class rule must override lower-specificity tag rule");
        }

        // ── Non-inheritance: parent value must not reach child ────────────

        [Test]
        public void Scroll_behavior_smooth_on_parent_does_not_reach_child() {
            // Non-inherited; child must see the initial value (auto), not parent's smooth.
            var doc = Html("<div class=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(".parent { scroll-behavior: smooth; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"),
                "scroll-behavior must not be inherited; child must see initial auto");
        }

        // ── var() substitution ────────────────────────────────────────────

        [Test]
        public void Scroll_behavior_accepts_var_substitution() {
            // var() referencing a custom property that holds 'smooth'.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --sb: smooth; scroll-behavior: var(--sb); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("smooth"),
                "var() substitution must resolve to the referenced custom property value");
        }
    }
}
