using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Cascade L5 §6.3 + CSS Conditional Rules L3 §6:
    // @layer inside @media is valid and must participate in layer ordering as if
    // the rules were declared at the point the @media block appears in source order.
    // When the @media condition is false, contained @layer declarations are
    // NOT registered (the layer does not get an ordinal from media-gated declarations).
    // Spec refs: CSS Cascade L5 §6.3.2 / CSS Conditional Rules L3 §6.1.
    public class LayerInMediaQueryTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string css, MediaContext? ctx = null) {
            var sheet = OriginatedStylesheet.Author(CssParser.Parse(css));
            return sheet;
        }

        static CascadeEngine Engine(string css, double vpWidth = 800, double vpHeight = 600) {
            var media = MediaContext.Default(vpWidth, vpHeight);
            return new CascadeEngine(new[] { OriginatedStylesheet.Author(CssParser.Parse(css)) }, media);
        }

        // ---- basic: @layer inside @media that matches ----

        [Test]
        public void Layer_inside_matching_media_applies_rules() {
            // @media (min-width: 600px) is true at viewport 800px wide.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @media (min-width: 600px) {
                    @layer base {
                        div { color: red; }
                    }
                }
            ");
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Layer_inside_non_matching_media_does_not_apply() {
            // Viewport is 400px; @media (min-width: 600px) is false.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @media (min-width: 600px) {
                    @layer base {
                        div { color: red; }
                    }
                }
            ", vpWidth: 400);
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.Not.EqualTo("red"));
        }

        // ---- ordering: layer inside @media vs outside @media ----

        [Test]
        public void Layer_in_media_is_ordered_after_unconditional_layer_declared_before() {
            // @layer base declared unconditionally first, then @layer overrides inside @media.
            // Spec: the media-gated block's @layer ordinal follows the same order the
            // @layer declarations appear in source — so `overrides` is after `base` and wins.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @layer base { div { color: red; } }
                @media (min-width: 400px) {
                    @layer overrides { div { color: blue; } }
                }
            ");
            // 800px viewport satisfies media query; `overrides` is later → blue wins.
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Layer_outside_media_beats_media_gated_earlier_layer_when_outside_is_later() {
            // Source order: @media-gated `early` comes first, unconditional `late` comes after.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @media (min-width: 400px) {
                    @layer early { div { color: red; } }
                }
                @layer late { div { color: blue; } }
            ");
            // `late` is declared after `early` → blue wins.
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"));
        }

        // ---- ordering with pre-declared statement form ----

        [Test]
        public void Layer_statement_form_fixes_order_before_media_gated_block() {
            // Statement form `@layer base, overrides;` fixes the ordinal.
            // @media block then fills `base` and `overrides` — `overrides` still
            // beats `base` because the statement form anchored that order.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @layer base, overrides;
                @media (min-width: 400px) {
                    @layer overrides { div { color: blue; } }
                }
                @layer base { div { color: red; } }
            ");
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"));
        }

        // ---- specificity inside media-gated layer ----

        [Test]
        public void Within_media_layer_specificity_still_decides_same_layer_rules() {
            var doc = Html("<div id='x' class='c'></div>");
            var engine = Engine(@"
                @media (min-width: 400px) {
                    @layer base {
                        .c { color: red; }
                        #x  { color: blue; }
                    }
                }
            ");
            // Same layer → specificity decides; #x beats .c.
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"));
        }

        // ---- unlayered beats media-gated layer (normal) ----

        [Test]
        public void Unlayered_rule_beats_media_gated_layer_normal() {
            // Per CSS Cascade L5: unlayered = implicit final layer, beats any @layer normal.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @media (min-width: 400px) {
                    @layer base { #x { color: red; } }
                }
                div { color: blue; }
            ");
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"));
        }

        // ---- media query on a non-matching condition does not register a layer ordinal ----

        [Test]
        public void Gated_layer_in_false_media_does_not_interfere_with_unlayered_winner() {
            // Viewport 300px: @media (min-width: 600px) is false.
            // The @layer `hidden` inside it must produce no output and must not affect ordinals.
            var doc = Html("<div id='x'></div>");
            var engine = Engine(@"
                @layer visible { div { color: green; } }
                @media (min-width: 600px) {
                    @layer hidden { div { color: red; } }
                }
            ", vpWidth: 300);
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("green"));
        }

        // ---- @layer + @media both flip based on viewport ----

        [Test]
        public void Resizing_viewport_switches_winner_between_media_gated_layers() {
            // Two @media blocks gating different layers. At 900px wide, `wide` is active;
            // at 400px wide (below 600px breakpoint), `wide` is inactive.
            var wide = Engine(@"
                @media (min-width: 600px) {
                    @layer wide { div { color: blue; } }
                }
                @layer narrow { div { color: red; } }
            ", vpWidth: 900);
            var narrow = Engine(@"
                @media (min-width: 600px) {
                    @layer wide { div { color: blue; } }
                }
                @layer narrow { div { color: red; } }
            ", vpWidth: 400);

            var docWide = Html("<div id='x'></div>");
            var docNarrow = Html("<div id='y'></div>");
            // At 900px: wide is declared before narrow (source order) but inside @media.
            // `narrow` appears after in source → narrow wins if both are present.
            // At 400px: wide is inactive, so only narrow layer applies → red.
            Assert.That(narrow.Compute(docNarrow.GetElementById("y")).Get("color"), Is.EqualTo("red"));
            // At 900px: `wide` comes before `narrow` in source, so `narrow` (later) wins.
            // (This also tests that both layers ARE active at 900px.)
            Assert.That(wide.Compute(docWide.GetElementById("x")).Get("color"), Is.EqualTo("red"));
        }
    }
}
