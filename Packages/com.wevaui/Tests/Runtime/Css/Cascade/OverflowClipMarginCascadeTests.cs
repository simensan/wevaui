using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Overflow L4 §6 — `overflow-clip-margin` cascade coverage.
    //
    // The shorthand and four physical longhands are registered as non-inherited
    // properties with an initial value of `0px`. Per-side longhands override the
    // shorthand at paint time via OverflowResolver. The logical-side aliases
    // (`-inline-start/-end`, `-block-start/-end`) are wired through
    // CascadeEngine.Logical.AliasSide.
    //
    // This file covers:
    //   - Initial values for shorthand and longhands.
    //   - Round-trip: explicit length values survive cascade → Get.
    //   - Per-side longhand overrides shorthand (cascade-level; paint resolution
    //     by OverflowResolver is tested separately).
    //   - Non-inheritance: child does not see parent's overflow-clip-margin.
    //   - Specificity / source-order mechanics.
    //   - Logical-axis aliases resolve to physical sides in LTR writing mode.
    //
    // Spec: CSS Overflow Module Level 4 §6.
    public class OverflowClipMarginCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css, string id = "x") {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById(id));
        }

        // ── Initial values ───────────────────────────────────────────────────

        [Test]
        public void Overflow_clip_margin_shorthand_initial_is_zero() {
            // CSS Overflow L4 §6: initial value is `0px` (no inflation).
            var cs = Compute("");
            Assert.That(cs.Get("overflow-clip-margin"), Is.EqualTo("0px"));
        }

        [Test]
        public void Overflow_clip_margin_top_initial_is_zero() {
            var cs = Compute("");
            Assert.That(cs.Get("overflow-clip-margin-top"), Is.EqualTo("0px"));
        }

        [Test]
        public void Overflow_clip_margin_right_initial_is_zero() {
            var cs = Compute("");
            Assert.That(cs.Get("overflow-clip-margin-right"), Is.EqualTo("0px"));
        }

        [Test]
        public void Overflow_clip_margin_bottom_initial_is_zero() {
            var cs = Compute("");
            Assert.That(cs.Get("overflow-clip-margin-bottom"), Is.EqualTo("0px"));
        }

        [Test]
        public void Overflow_clip_margin_left_initial_is_zero() {
            var cs = Compute("");
            Assert.That(cs.Get("overflow-clip-margin-left"), Is.EqualTo("0px"));
        }

        // ── Round-trip cascade ───────────────────────────────────────────────

        [Test]
        public void Overflow_clip_margin_shorthand_length_round_trips() {
            // A pixel value must survive parse → cascade → Get.
            var cs = Compute("#x { overflow: clip; overflow-clip-margin: 8px; }");
            Assert.That(cs.Get("overflow-clip-margin"), Is.EqualTo("8px"));
        }

        [Test]
        public void Overflow_clip_margin_top_longhand_round_trips() {
            var cs = Compute("#x { overflow-clip-margin-top: 12px; }");
            Assert.That(cs.Get("overflow-clip-margin-top"), Is.EqualTo("12px"));
        }

        [Test]
        public void Overflow_clip_margin_right_longhand_round_trips() {
            var cs = Compute("#x { overflow-clip-margin-right: 6px; }");
            Assert.That(cs.Get("overflow-clip-margin-right"), Is.EqualTo("6px"));
        }

        [Test]
        public void Overflow_clip_margin_bottom_longhand_round_trips() {
            var cs = Compute("#x { overflow-clip-margin-bottom: 4px; }");
            Assert.That(cs.Get("overflow-clip-margin-bottom"), Is.EqualTo("4px"));
        }

        [Test]
        public void Overflow_clip_margin_left_longhand_round_trips() {
            var cs = Compute("#x { overflow-clip-margin-left: 16px; }");
            Assert.That(cs.Get("overflow-clip-margin-left"), Is.EqualTo("16px"));
        }

        // ── Per-side longhands are independent from each other ───────────────

        [Test]
        public void Per_side_longhands_are_independent() {
            // Setting one side must not affect the others.
            var cs = Compute("#x { overflow-clip-margin-top: 20px; }");
            Assert.That(cs.Get("overflow-clip-margin-top"),    Is.EqualTo("20px"));
            Assert.That(cs.Get("overflow-clip-margin-right"),  Is.EqualTo("0px"));
            Assert.That(cs.Get("overflow-clip-margin-bottom"), Is.EqualTo("0px"));
            Assert.That(cs.Get("overflow-clip-margin-left"),   Is.EqualTo("0px"));
        }

        // ── Non-inheritance ──────────────────────────────────────────────────

        [Test]
        public void Overflow_clip_margin_does_not_inherit() {
            // CSS Overflow L4 §6: not inherited. Child must not see parent value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { overflow: clip; overflow-clip-margin: 10px; }")
            });
            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("overflow-clip-margin"), Is.EqualTo("0px"),
                "overflow-clip-margin is not inherited; child must see the initial 0px");
        }

        [Test]
        public void Overflow_clip_margin_top_does_not_inherit() {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { overflow-clip-margin-top: 5px; }")
            });
            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("overflow-clip-margin-top"), Is.EqualTo("0px"),
                "overflow-clip-margin-top must not inherit");
        }

        // ── Specificity / source-order ───────────────────────────────────────

        [Test]
        public void Higher_specificity_id_wins_over_class_rule() {
            // CSS Cascade §6: specificity governs the winning declaration.
            var doc = Html("<div id=\"x\" class=\"clip\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".clip { overflow-clip-margin: 4px; } #x { overflow-clip-margin: 12px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("overflow-clip-margin"), Is.EqualTo("12px"),
                "id selector has higher specificity and must override class rule");
        }

        [Test]
        public void Later_rule_wins_when_specificity_is_equal() {
            var cs = Compute("#x { overflow-clip-margin: 3px; } #x { overflow-clip-margin: 9px; }");
            Assert.That(cs.Get("overflow-clip-margin"), Is.EqualTo("9px"),
                "last rule in source order wins when specificity is equal");
        }

        // ── Logical-axis aliases ─────────────────────────────────────────────

        [Test]
        public void Overflow_clip_margin_inline_start_aliases_to_left_in_ltr() {
            // In horizontal-tb / LTR: inline-start = left.
            // CascadeEngine.Logical.AliasSide maps inline-start → left for the
            // horizontal writing mode (LTR default).
            var cs = Compute("#x { overflow-clip-margin-inline-start: 7px; }");
            // The alias must land the value on overflow-clip-margin-left.
            Assert.That(cs.Get("overflow-clip-margin-left"), Is.EqualTo("7px"),
                "overflow-clip-margin-inline-start must alias to left in LTR writing mode");
        }

        [Test]
        public void Overflow_clip_margin_block_start_aliases_to_top_in_horizontal_writing() {
            // In horizontal-tb: block-start = top.
            var cs = Compute("#x { overflow-clip-margin-block-start: 11px; }");
            Assert.That(cs.Get("overflow-clip-margin-top"), Is.EqualTo("11px"),
                "overflow-clip-margin-block-start must alias to top in horizontal-tb");
        }
    }
}
