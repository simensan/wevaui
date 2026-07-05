using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class CascadeLayerTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        [Test]
        public void Layer_statement_form_parses() {
            var sheet = Css("@layer reset, base, components;");
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
            var lr = (LayerRule)sheet.Rules[0];
            Assert.That(lr.IsBlock, Is.False);
            Assert.That(lr.Names, Is.EqualTo(new[] { "reset", "base", "components" }));
        }

        [Test]
        public void Layer_block_form_parses_with_inner_rules() {
            var sheet = Css("@layer base { .a { color: red; } }");
            var lr = (LayerRule)sheet.Rules[0];
            Assert.That(lr.IsBlock, Is.True);
            Assert.That(lr.Names, Has.Count.EqualTo(1));
            Assert.That(lr.Names[0], Is.EqualTo("base"));
            Assert.That(lr.Rules, Has.Count.EqualTo(1));
        }

        [Test]
        public void Layer_anonymous_block_has_null_name() {
            var sheet = Css("@layer { .a { color: red; } }");
            var lr = (LayerRule)sheet.Rules[0];
            Assert.That(lr.IsBlock, Is.True);
            Assert.That(lr.Names[0], Is.Null);
        }

        [Test]
        public void Later_layer_beats_earlier_layer_normal() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base, overrides;
                @layer base { #x { color: red; } }
                @layer overrides { div { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Despite #x having higher specificity, `overrides` is declared after `base`
            // so the lower-specificity rule in `overrides` wins.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Unlayered_rule_beats_layered_rule_normal() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { #x { color: red; } }
                div { color: blue; }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Unlayered counts as the implicit-final layer per CSS Cascade L5;
            // unlayered `div` (lower specificity) wins over `#x` in `base`.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Important_reverses_layer_order() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base, overrides;
                @layer base { div { color: red !important; } }
                @layer overrides { div { color: blue !important; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // !important inverts layer order — earliest layer wins.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Important_layered_beats_important_unlayered() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { div { color: red !important; } }
                div { color: blue !important; }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Important: layered beats unlayered (the inverse of normal).
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Anonymous_layer_ordered_after_named_in_declaration_order() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base;
                @layer base { div { color: red; } }
                @layer { div { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Anonymous block declared after `base` — anonymous wins.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Same_layer_falls_through_to_specificity() {
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base {
                    .c { color: red; }
                    #x { color: blue; }
                }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Same layer → specificity decides. #x wins.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Layer_ordering_pre_declared_via_statement_form() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer reset, base, overrides;
                @layer overrides { div { color: blue; } }
                @layer base { div { color: red; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Statement form fixed `overrides` AFTER `base`, so `overrides` wins
            // even though the block for `overrides` appears textually first.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Sub_layer_treated_as_flat_distinct_layer() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { div { color: red; } }
                @layer base.utilities { div { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // v1: `base.utilities` is a separate layer declared AFTER `base`,
            // so it wins normally.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Inline_style_bypasses_layer_axis_and_wins_over_layered_normal() {
            // CompareForCascade explicitly skips the layer-ordering step when
            // exactly one side is inline (CascadeEngine.cs §776–786). The inline
            // declaration is treated as a same-origin author rule that wins on
            // the post-layer "inline" axis regardless of which layer the
            // selector-based rule sits in. This test pins that bypass.
            var doc = Html("<div id=\"x\" style=\"color: green;\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base, overrides;
                @layer overrides { #x { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Inline normal beats a layered (high-specificity) selector rule.
            Assert.That(cs.Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Inline_normal_loses_to_layered_important() {
            // Inline normal still loses to !important from any layered author
            // rule because the importance axis dominates the inline-bypass.
            var doc = Html("<div id=\"x\" style=\"color: green;\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { div { color: red !important; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Nested_layer_block_flattens_to_dotted_parent_child_name() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { @layer utilities { div { color: red; } } }
                @layer base.utilities { div { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
            Assert.That(engine.TryGetLayerOrdinal("base.utilities", out int dotted), Is.True);
            Assert.That(engine.TryGetLayerOrdinal("utilities", out _), Is.False);
            int firstOrdinal = dotted;
            Assert.That(engine.LayerOrdinals["base.utilities"], Is.EqualTo(firstOrdinal),
                "Nested-block and dotted-statement forms must resolve to the same layer ordinal.");
        }

        [Test]
        public void Dotted_layer_statement_form_regression_preserved() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base.utilities { div { color: red; } }
                @layer base.utilities { div { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
            Assert.That(engine.TryGetLayerOrdinal("base.utilities", out _), Is.True);
        }

        [Test]
        public void Nested_layer_statement_form_inherits_parent_prefix() {
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { @layer x, y; }
            ") });
            Assert.That(engine.TryGetLayerOrdinal("base.x", out int baseX), Is.True);
            Assert.That(engine.TryGetLayerOrdinal("base.y", out int baseY), Is.True);
            Assert.That(engine.TryGetLayerOrdinal("x", out _), Is.False);
            Assert.That(engine.TryGetLayerOrdinal("y", out _), Is.False);
            Assert.That(baseX, Is.LessThan(baseY));

            var topLevel = new CascadeEngine(new[] { Author(@"
                @layer x, y;
            ") });
            Assert.That(topLevel.TryGetLayerOrdinal("x", out int x), Is.True);
            Assert.That(topLevel.TryGetLayerOrdinal("y", out int y), Is.True);
            Assert.That(topLevel.TryGetLayerOrdinal("base.x", out _), Is.False);
            Assert.That(x, Is.LessThan(y));

            var combined = new CascadeEngine(new[] { Author(@"
                @layer base { @layer x, y; @layer utilities { } }
            ") });
            Assert.That(combined.TryGetLayerOrdinal("base.x", out int cx), Is.True);
            Assert.That(combined.TryGetLayerOrdinal("base.y", out int cy), Is.True);
            Assert.That(combined.TryGetLayerOrdinal("base.utilities", out int cu), Is.True);
            Assert.That(cx, Is.LessThan(cy));
            Assert.That(cy, Is.LessThan(cu));
        }

        // ----- A9 regression: inline !important must lose to layered !important. -----
        // CSS Cascade L5 §6.4.1 step 5 says unlayered !important (which includes
        // inline !important since inline declarations carry UnlayeredOrdinal)
        // loses to layered !important. The inline-bypass for the layer axis only
        // applies to NORMAL declarations.

        [Test]
        public void Inline_important_loses_to_layered_important_A9() {
            // Inline `color: red !important` vs `@layer base { #x { color: blue !important } }`
            // → blue wins. Layered !important outranks unlayered (inline) !important.
            var doc = Html("<div id=\"x\" style=\"color: red !important;\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { #x { color: blue !important; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Inline_normal_loses_to_layered_important_A9() {
            // Inline `color: red` (normal) vs layered `color: blue !important`
            // → blue wins. Any !important beats any normal regardless of inline-ness.
            var doc = Html("<div id=\"x\" style=\"color: red;\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { #x { color: blue !important; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Inline_normal_beats_layered_normal_A9() {
            // Inline `color: red` (normal) vs layered `color: blue` (normal)
            // → red wins. Inline bypasses the layer axis for normals; the
            // post-layer inline tiebreak hands the win to inline.
            var doc = Html("<div id=\"x\" style=\"color: red;\"></div>");
            var engine = new CascadeEngine(new[] { Author(@"
                @layer base { #x { color: blue; } }
            ") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Engine_exposes_layer_ordinals_for_inspection() {
            var engine = new CascadeEngine(new[] { Author(@"
                @layer reset, base, components;
                @layer base { .a { color: red; } }
            ") });
            Assert.That(engine.TryGetLayerOrdinal("reset", out int reset), Is.True);
            Assert.That(engine.TryGetLayerOrdinal("base", out int @base), Is.True);
            Assert.That(engine.TryGetLayerOrdinal("components", out int components), Is.True);
            Assert.That(reset, Is.LessThan(@base));
            Assert.That(@base, Is.LessThan(components));
        }
    }
}
