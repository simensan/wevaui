using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Masking Module Level 1 §3-§6 — multi-layer mask cascade tests.
    //
    // CSS Masking 1 allows comma-separated multi-layer values for mask
    // longhands (§3.1-§6.1). Each layer has its own mask-image, mask-mode,
    // mask-composite, mask-repeat, etc.  The longhands store comma-joined
    // lists.  The `mask` shorthand expander (MaskShorthandExpander.cs) splits
    // on commas and round-trips via JoinPerLayer.
    //
    // MaskLonghandTests covers single-layer round-trips.  This file covers:
    //   - Two-layer and three-layer comma-list values via direct longhand
    //   - Shorthand → longhand expansion for multi-layer masks
    //   - Differing per-layer values for mask-mode and mask-composite
    //   - Cascade override: later rule replaces earlier multi-layer value
    //   - Non-inheritance: multi-layer parent value stays on parent
    //
    // Spec refs: CSS Masking 1 §3, §6; CSS Values 4 §2.1 (list notation)
    public class MaskMultiLayerCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── Two-layer mask-mode (comma list) via longhand ─────────────────

        [Test]
        public void Mask_mode_two_layer_round_trips() {
            // CSS Masking 1 §3.2: mask-mode can list per-layer values.
            // Two layers: first alpha, second luminance.
            var cs = Compute("#x { mask-mode: alpha, luminance; }");
            var val = cs.Get("mask-mode");
            Assert.That(val, Does.Contain("alpha"),
                "first layer must be alpha");
            Assert.That(val, Does.Contain("luminance"),
                "second layer must be luminance");
        }

        [Test]
        public void Mask_composite_two_layer_round_trips() {
            // CSS Masking 1 §6.1: mask-composite accepts per-layer values.
            var cs = Compute("#x { mask-composite: add, subtract; }");
            var val = cs.Get("mask-composite");
            Assert.That(val, Does.Contain("add"),
                "first layer must be add");
            Assert.That(val, Does.Contain("subtract"),
                "second layer must be subtract");
        }

        [Test]
        public void Mask_mode_three_layer_round_trips() {
            // Three layers: alpha, luminance, match-source.
            var cs = Compute("#x { mask-mode: alpha, luminance, match-source; }");
            var val = cs.Get("mask-mode");
            Assert.That(val, Does.Contain("alpha"),
                "first layer must be alpha");
            Assert.That(val, Does.Contain("luminance"),
                "second layer must be luminance");
            Assert.That(val, Does.Contain("match-source"),
                "third layer must be match-source");
        }

        // ── Shorthand → multi-layer longhand expansion ─────────────────────

        [Test]
        public void Mask_shorthand_two_layers_expands_to_comma_joined_longhands() {
            // The `mask` shorthand with two comma-separated layers must expand
            // to longhands that store comma-joined values.
            var cs = Compute(
                "#x { mask: url(a.png) alpha add, url(b.png) luminance subtract; }");
            // mask-image: two layers
            var img = cs.Get("mask-image");
            Assert.That(img, Does.Contain("url(a.png)"), "first layer image");
            Assert.That(img, Does.Contain("url(b.png)"), "second layer image");
            // mask-mode: two layers
            var mode = cs.Get("mask-mode");
            Assert.That(mode, Does.Contain("alpha"),   "first layer mode");
            Assert.That(mode, Does.Contain("luminance"), "second layer mode");
            // mask-composite: two layers
            var comp = cs.Get("mask-composite");
            Assert.That(comp, Does.Contain("add"),      "first layer composite");
            Assert.That(comp, Does.Contain("subtract"), "second layer composite");
        }

        // ── Later rule overrides entire multi-layer value ─────────────────

        [Test]
        public void Later_mask_mode_rule_replaces_earlier_multi_layer_value() {
            // CSS cascade: same-specificity later rule wins.
            // First rule sets two layers; second rule sets a single layer.
            var cs = Compute(
                "#x { mask-mode: alpha, luminance; } " +
                "#x { mask-mode: match-source; }");
            var val = cs.Get("mask-mode");
            // The second rule's value wins: single-layer match-source.
            Assert.That(val, Is.EqualTo("match-source"),
                "later same-specificity rule must replace the entire multi-layer value");
        }

        // ── Multi-layer mask-composite with all four keywords ─────────────

        [Test]
        public void Mask_composite_four_layers_all_keywords() {
            // §6.1 lists four composite operations: add, subtract, intersect, exclude.
            var cs = Compute(
                "#x { mask-composite: add, subtract, intersect, exclude; }");
            var val = cs.Get("mask-composite");
            Assert.That(val, Does.Contain("add"),       "layer 1: add");
            Assert.That(val, Does.Contain("subtract"),  "layer 2: subtract");
            Assert.That(val, Does.Contain("intersect"), "layer 3: intersect");
            Assert.That(val, Does.Contain("exclude"),   "layer 4: exclude");
        }

        // ── Non-inheritance of multi-layer values ─────────────────────────

        [Test]
        public void Multi_layer_mask_mode_does_not_inherit() {
            // mask-mode is non-inherited; child must NOT see the parent's
            // two-layer alpha,luminance value — it must revert to initial.
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mask-mode: alpha, luminance; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("mask-mode"), Is.EqualTo("match-source"),
                "multi-layer mask-mode must not leak to child; child sees initial match-source");
        }

        [Test]
        public void Multi_layer_mask_composite_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mask-composite: add, subtract; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("mask-composite"), Is.EqualTo("add"),
                "multi-layer mask-composite must not inherit; child sees initial add");
        }

        // ── Single-layer longhand overrides shorthand-expanded multi-layer ─

        [Test]
        public void Longhand_mask_mode_overrides_shorthand_multi_layer() {
            // Shorthand sets two layers (alpha, luminance); subsequent longhand
            // override must replace both layers with a single value.
            // Higher specificity (#x.cls) beats id (#x alone).
            var doc = Html("<div id=\"x\" class=\"cls\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { mask: url(a.png) alpha, url(b.png) luminance; } " +
                    "#x.cls { mask-mode: match-source; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("mask-mode"), Is.EqualTo("match-source"),
                "higher-specificity longhand must win over shorthand-expanded two-layer value");
        }
    }
}
