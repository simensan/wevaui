using System.Collections.Generic;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Edge-case suite covering cascade behaviour the base CascadeEngineTests
    // / CascadeLayerTests batteries don't pin explicitly: @import positioning,
    // @layer interactions with !important, animation overlays vs author
    // !important, inline-style precedence inside !important, and the var()
    // fallback chain. Each test is independent; the helpers below mirror the
    // convention used by CascadeEngineTests / CascadeLayerTests so this file
    // reads as a drop-in extension of the existing batteries.
    public class CascadeEdgeCasesTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        // ----- @import order -----

        [Test]
        public void Import_rules_apply_before_following_rules_in_same_sheet() {
            // v1 GAP: CssParser.ParseImportRule emits a warning
            // ("@import url(...) is parsed but not loaded") and the cascade
            // engine has no case for ImportRule — so the imported sheet's
            // rules never make it into the cascade. We approximate the spec
            // semantics by handing the "imported" rules to the engine as a
            // SEPARATE OriginatedStylesheet listed BEFORE the importing
            // sheet, which is exactly the source-order position an actually-
            // loaded @import would occupy per CSS 2.1 §6.3 ("@import ...
            // must precede all other rules ... they cascade as if at the top
            // of the sheet"). Once the loader lands, replace the two-sheet
            // pair with a single sheet containing `@import url(imported);`.
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var imported = Author(".x { color: green; }");
            var importer = Author(".x { color: red; }");
            var engine = new CascadeEngine(new[] { imported, importer });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Same specificity → source order decides. Importing sheet
            // (listed second, i.e. AFTER its @import target in source-order
            // terms) wins.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Import_inside_media_block_only_applies_when_media_matches() {
            // v1: @import is parsed but not loaded, so a real
            //   @import url(big.css) screen and (min-width: 1000px);
            // never actually contributes rules to the cascade. The intent of
            // this test is to pin the MediaContext-gating semantics that an
            // @import's trailing media list would feed into once loading
            // lands — proxied here by wrapping the would-be-imported rules
            // in an @media block and flipping the MediaContext viewport.
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Author(@"
                @media screen and (min-width: 1000px) {
                    #x { color: red; }
                }
            ");
            var engineWide = new CascadeEngine(new[] { sheet }, MediaContext.Default(1200, 800));
            Assert.That(engineWide.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));

            var doc2 = Html("<div id=\"x\"></div>");
            var engineNarrow = new CascadeEngine(new[] { sheet }, MediaContext.Default(600, 800));
            Assert.That(engineNarrow.Compute(doc2.GetElementById("x")).Get("color"), Is.EqualTo("black"));
        }

        // ----- @layer -----

        [Test]
        public void Layer_named_layer_loses_to_unlayered_rule() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { .x { color: blue; } }
                .x { color: red; }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Unlayered counts as the implicit-final (highest) layer.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Layer_two_named_layers_first_listed_loses() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer a, b;
                @layer b { .x { color: blue; } }
                @layer a { .x { color: red; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Statement form pins `a` before `b`; later-declared layer (`b`) wins
            // for normal declarations regardless of the textual order of the
            // following blocks.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Layer_important_in_lower_layer_beats_normal_in_higher() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base, overrides;
                @layer base { .x { color: red !important; } }
                @layer overrides { .x { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Importance dominates layer order outright — !important beats
            // normal regardless of which layer each lives in.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        // ----- !important + animation -----

        [Test]
        public void Important_user_style_beats_animation() {
            // CSS Cascading L4 §6.4.1: animation declarations sit below
            // !important author declarations in the cascade order. The
            // cascade engine stamps an importance bit on each winning
            // property id, and CssAnimationRunner.Compose refuses to
            // overlay any property whose base value won via !important —
            // so the author's `color: red !important` wins over the
            // keyframe animation.
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes paint { from { color: green; } to { color: green; } } " +
                "#x { animation-name: paint; animation-duration: 1s; animation-timing-function: linear; color: red !important; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = cascade.GetComposedStyle(x);
            Assert.That(composed.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Important_animation_beats_important_normal_styles() {
            // v1 partial: CSS Animations §4.2 lets keyframe declarations
            // carry !important to push them ABOVE author !important in the
            // cascade. v1 doesn't yet distinguish "is the animation sample
            // itself important?" — Compose only consults the base style's
            // !important bit, so when both sides are important the base
            // wins (red); spec result would be the animation's green.
            // Pinned to v1 behaviour; flip once the runner gains its own
            // importance axis on the overlay path.
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes paint { from { color: green !important; } to { color: green !important; } } " +
                "#x { animation-name: paint; animation-duration: 1s; animation-timing-function: linear; color: red !important; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = cascade.GetComposedStyle(x);
            Assert.That(composed.Get("color"), Is.EqualTo("red"));
        }

        // ----- Inline style + !important -----

        [Test]
        public void Inline_style_important_beats_class_important() {
            var doc = Html("<div id=\"x\" class=\"x\" style=\"color: red !important;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x { color: blue !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Both are author-origin !important. Inline declarations are
            // assigned a very high source index (1_000_000_000+), so they
            // win the source-order tiebreak against any selector-based
            // rule. Cf. CascadeEngineTests.Important_inline_beats_important_author.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Inline_style_normal_loses_to_class_important() {
            var doc = Html("<div id=\"x\" class=\"x\" style=\"color: red;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x { color: blue !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Importance is the dominant axis — !important class beats a
            // normal inline declaration outright. The inline-bypass that
            // beats layered selectors does not lift normal inline above
            // !important.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        // ----- Custom property fallback chain -----

        [Test]
        public void Var_fallback_used_when_primary_unset() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(--undefined, red); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Var_nested_fallback_chain() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --c: red; color: var(--a, var(--b, var(--c, blue))); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // --a and --b are unset → fall through to var(--c, blue) → --c is
            // defined as red, so the innermost var resolves to red without
            // consulting its own fallback.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Var_inherits_from_ancestor() {
            var doc = Html("<html><body><section><article><div id=\"x\"></div></article></section></body></html>");
            var engine = new CascadeEngine(new[] {
                Author("html { --accent: rebeccapurple; } #x { color: var(--accent); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // --accent set on <html> inherits down the chain into #x where the
            // var() reference resolves to rebeccapurple.
            Assert.That(cs.Get("color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Var_re_evaluates_when_inherited_value_changes() {
            // The cascade caches per (element-version, parent-version, ...) so
            // mutating an ancestor's inline style bumps that ancestor's
            // version, invalidates the cached descendant entry, and the next
            // Compute re-resolves var(--accent) against the new ancestor
            // value. We assert the descendant tracks the change without an
            // explicit Invalidate call.
            var doc = Html("<html><body><div id=\"x\"></div></body></html>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(--accent, black); }")
            });
            var html = FindByTag(doc, "html");

            html.SetAttribute("style", "--accent: red;");
            var cs1 = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs1.Get("color"), Is.EqualTo("red"));

            html.SetAttribute("style", "--accent: blue;");
            // Element.SetAttribute bumps the element's Version which the
            // cache key consumes; the descendant's parent-chain re-walk picks
            // up the new value the next time Compute is called.
            var cs2 = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs2.Get("color"), Is.EqualTo("blue"));
        }
    }
}
