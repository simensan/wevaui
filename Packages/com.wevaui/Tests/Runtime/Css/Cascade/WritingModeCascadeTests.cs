using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Writing Modes L4 §3 — cascade integration for `writing-mode`.
    //
    // `writing-mode` is registered as inherited with initial value `horizontal-tb`.
    // In v1 the renderer is horizontal-only; the cascade carries the value and
    // the layout engine consults it for logical-axis mapping. These tests pin:
    //   - all four recognized keyword values round-trip correctly
    //   - initial value is `horizontal-tb` when no rule applies
    //   - the property inherits from parent to child
    //   - an explicit child rule overrides the inherited value
    //   - `inherit`, `initial`, and `unset` CSS-wide keywords work correctly
    //   - higher-specificity rule wins when two rules conflict
    //   - source-order tiebreak applies for same-specificity declarations
    //   - `text-orientation` (a companion inherited property) is independent
    //   - `direction` is independent from `writing-mode`
    //
    // Spec: CSS Writing Modes L4 §3 (https://www.w3.org/TR/css-writing-modes-4/)
    public class WritingModeCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css, string id = "x") {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById(id));
        }

        // ---- Initial value ----

        [Test]
        public void Writing_mode_initial_value_is_horizontal_tb() {
            // CSS Writing Modes L4 §3.2: `writing-mode` initial = `horizontal-tb`.
            var cs = Compute("");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("horizontal-tb"));
        }

        // ---- Keyword round-trips ----

        [Test]
        public void Writing_mode_vertical_rl_round_trips() {
            var cs = Compute("#x { writing-mode: vertical-rl; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("vertical-rl"));
        }

        [Test]
        public void Writing_mode_vertical_lr_round_trips() {
            var cs = Compute("#x { writing-mode: vertical-lr; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("vertical-lr"));
        }

        [Test]
        public void Writing_mode_sideways_rl_round_trips() {
            var cs = Compute("#x { writing-mode: sideways-rl; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("sideways-rl"));
        }

        [Test]
        public void Writing_mode_sideways_lr_round_trips() {
            var cs = Compute("#x { writing-mode: sideways-lr; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("sideways-lr"));
        }

        [Test]
        public void Writing_mode_horizontal_tb_explicit_round_trips() {
            var cs = Compute("#x { writing-mode: horizontal-tb; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("horizontal-tb"));
        }

        // ---- Inheritance ----

        [Test]
        public void Writing_mode_inherits_from_parent() {
            // CSS Writing Modes L4 §3.2: `writing-mode` is inherited.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { writing-mode: vertical-rl; }")
            });
            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("writing-mode"), Is.EqualTo("vertical-rl"),
                "writing-mode must inherit from the parent element");
        }

        [Test]
        public void Writing_mode_child_overrides_inherited_value() {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { writing-mode: vertical-rl; } #child { writing-mode: horizontal-tb; }")
            });
            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("writing-mode"), Is.EqualTo("horizontal-tb"),
                "explicit child rule must override the inherited writing-mode");
        }

        // ---- CSS-wide keywords ----

        [Test]
        public void Writing_mode_initial_keyword_resets_to_horizontal_tb() {
            var cs = Compute("#x { writing-mode: vertical-rl; } #x { writing-mode: initial; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("horizontal-tb"),
                "`writing-mode: initial` must revert to the spec initial value horizontal-tb");
        }

        [Test]
        public void Writing_mode_inherit_keyword_pulls_from_parent() {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { writing-mode: vertical-lr; } #child { writing-mode: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("writing-mode"), Is.EqualTo("vertical-lr"),
                "`writing-mode: inherit` on child must pull the parent's vertical-lr value");
        }

        // ---- Specificity and source-order ----

        [Test]
        public void Writing_mode_higher_specificity_wins() {
            // #x (0,1,0) beats div (0,0,1): ID rule must take precedence.
            var cs = Compute("div { writing-mode: vertical-rl; } #x { writing-mode: horizontal-tb; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("horizontal-tb"),
                "ID selector has higher specificity and must win");
        }

        [Test]
        public void Writing_mode_source_order_tiebreak() {
            // Same specificity; later source must win.
            var cs = Compute("#x { writing-mode: vertical-rl; } #x { writing-mode: vertical-lr; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("vertical-lr"),
                "later same-specificity rule wins via source-order tiebreak");
        }

        // ---- Independence from companion properties ----

        [Test]
        public void Writing_mode_does_not_affect_direction() {
            // `direction` and `writing-mode` are distinct registered properties.
            var cs = Compute("#x { writing-mode: vertical-rl; direction: rtl; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("vertical-rl"));
            Assert.That(cs.Get("direction"), Is.EqualTo("rtl"),
                "`direction` must be stored independently from `writing-mode`");
        }

        [Test]
        public void Text_orientation_is_independent_from_writing_mode() {
            // `text-orientation` is a separate inherited property; setting
            // `writing-mode` must not alter the stored `text-orientation` value.
            var cs = Compute("#x { writing-mode: vertical-rl; text-orientation: sideways; }");
            Assert.That(cs.Get("writing-mode"), Is.EqualTo("vertical-rl"));
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("sideways"),
                "`text-orientation` must be stored independently from `writing-mode`");
        }
    }
}
