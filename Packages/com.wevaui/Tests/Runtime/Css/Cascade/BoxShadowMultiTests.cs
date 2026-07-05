using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Backgrounds and Borders Level 3 §9 — box-shadow cascade tests.
    //
    // BoxShadowResolverTests (Paint/Conversion/) covers the resolution side
    // (shadow struct population, color resolution, inset flag). This file covers:
    //
    //   - cascade initial value (`none`)
    //   - non-inheritance
    //   - comma-separated multi-shadow lists surviving the cascade
    //   - all six legal value positions: inset? <offset-x> <offset-y> <blur>?
    //     <spread>? <color>?  — with and without optional components
    //   - inset keyword in various positions within the shadow value
    //   - cascade overriding: later rule wins
    //
    // The paint-side resolver (BoxShadowResolver) is exercised via the Resolver
    // tests; here we only check that ComputedStyle.Get("box-shadow") returns the
    // authored string after the cascade.
    public class BoxShadowMultiTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── initial value ─────────────────────────────────────────────────

        [Test]
        public void Box_shadow_initial_is_none() {
            // CSS Backgrounds 3 §9: initial value `none`.
            var cs = Compute("");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("none"));
        }

        // ── non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Box_shadow_does_not_inherit() {
            // CSS Backgrounds 3 §9: box-shadow is NOT inherited.
            var cs = ComputeChild("div { box-shadow: 2px 2px red; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("none"),
                "box-shadow is non-inherited; child sees initial none");
        }

        // ── single shadow — value positions ──────────────────────────────

        [Test]
        public void Box_shadow_offset_xy_only_round_trips() {
            // Minimum form: <offset-x> <offset-y>
            var cs = Compute("#x { box-shadow: 2px 4px; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("2px 4px"));
        }

        [Test]
        public void Box_shadow_with_blur_round_trips() {
            // Three-length form: <offset-x> <offset-y> <blur>
            var cs = Compute("#x { box-shadow: 1px 2px 3px; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("1px 2px 3px"));
        }

        [Test]
        public void Box_shadow_with_blur_and_spread_round_trips() {
            // Four-length form: <offset-x> <offset-y> <blur> <spread>
            var cs = Compute("#x { box-shadow: 1px 2px 3px 4px; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("1px 2px 3px 4px"));
        }

        [Test]
        public void Box_shadow_with_color_round_trips() {
            // Two-length + color: <offset-x> <offset-y> <color>
            var cs = Compute("#x { box-shadow: 2px 4px red; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("2px 4px red"));
        }

        [Test]
        public void Box_shadow_full_six_position_form_round_trips() {
            // All six positions: inset <offset-x> <offset-y> <blur> <spread> <color>
            var cs = Compute("#x { box-shadow: inset 1px 2px 3px 4px rgba(0,0,0,0.5); }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("inset 1px 2px 3px 4px rgba(0,0,0,0.5)"));
        }

        [Test]
        public void Box_shadow_inset_at_end_round_trips() {
            // Spec allows `inset` to appear at either the start or end of each layer.
            var cs = Compute("#x { box-shadow: 3px 3px 6px black inset; }");
            // The cascade carries the authored form verbatim.
            var got = cs.Get("box-shadow");
            Assert.That(got, Is.Not.Null);
            Assert.That(got, Does.Contain("inset"));
            Assert.That(got, Does.Contain("3px"));
        }

        // ── multi-shadow comma lists ───────────────────────────────────────

        [Test]
        public void Box_shadow_two_shadow_list_round_trips() {
            // Comma-separated list: first shadow wins (painted last = topmost).
            var cs = Compute("#x { box-shadow: 1px 1px red, 3px 3px blue; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("1px 1px red, 3px 3px blue"));
        }

        [Test]
        public void Box_shadow_three_shadow_list_round_trips() {
            var cs = Compute("#x { box-shadow: 1px 1px red, 2px 2px green, 3px 3px blue; }");
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("1px 1px red, 2px 2px green, 3px 3px blue"));
        }

        [Test]
        public void Box_shadow_inset_in_multi_shadow_list() {
            // inset can appear in some layers but not others.
            var cs = Compute("#x { box-shadow: inset 2px 2px 4px black, 4px 4px 8px rgba(0,0,255,0.5); }");
            Assert.That(cs.Get("box-shadow"),
                Is.EqualTo("inset 2px 2px 4px black, 4px 4px 8px rgba(0,0,255,0.5)"));
        }

        [Test]
        public void Box_shadow_multi_shadow_resolver_sees_correct_count() {
            // Regression: multi-shadow lists must not be partially dropped.
            // Cross-checks cascade value against the resolver's array length.
            var s = new ComputedStyle(new Element("div"));
            s.Set("color", "black");
            s.Set("box-shadow", "1px 1px red, 2px 2px blue, 3px 3px green");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, LengthContext.Default);
            Assert.That(arr.Length, Is.EqualTo(3),
                "Three comma-separated shadows must each yield one shadow struct");
        }

        [Test]
        public void Box_shadow_inset_resolved_in_multi_list() {
            // Verify resolver correctly identifies the inset shadow in a multi list.
            var s = new ComputedStyle(new Element("div"));
            s.Set("color", "black");
            s.Set("box-shadow", "inset 1px 1px 4px black, 2px 2px 4px blue");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, LengthContext.Default);
            Assert.That(arr.Length, Is.EqualTo(2));
            Assert.That(arr[0].Inset, Is.True);
            Assert.That(arr[1].Inset, Is.False);
        }

        // ── cascade overriding ────────────────────────────────────────────

        [Test]
        public void Box_shadow_later_rule_overrides_earlier() {
            // Higher-specificity rule replaces the lower-specificity one.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { box-shadow: 1px 1px red; } #x { box-shadow: 5px 5px blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // #x has higher specificity than div; blue shadow wins.
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("5px 5px blue"));
        }

        [Test]
        public void Box_shadow_none_overrides_previous_shadow() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { box-shadow: 4px 4px red; } #x { box-shadow: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("box-shadow"), Is.EqualTo("none"));
        }
    }
}
