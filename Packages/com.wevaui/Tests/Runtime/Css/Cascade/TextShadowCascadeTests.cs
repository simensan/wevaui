using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Text Decoration Level 4 §6 — text-shadow cascade coverage.
    //
    // text-shadow is registered in CssProperties as INHERITED with initial "none".
    // (text-shadow: true, "none" — see CssProperties.cs line ~747.)
    //
    // This mirrors box-shadow in value syntax except:
    //   - no `inset` keyword
    //   - no spread value (only offset-x, offset-y, blur-radius, color)
    //   - color may appear before OR after the lengths (spec §6.2)
    //   - the property IS inherited (unlike box-shadow)
    //
    // Paint-side resolver tests live in TextShadowResolverTests.cs.
    // This file exercises the cascade pipeline end-to-end.
    public class TextShadowCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<p id=\"x\">text</p>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"parent\"><span id=\"child\">hi</span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            engine.Compute(doc.GetElementById("parent"));
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── initial value ─────────────────────────────────────────────────

        [Test]
        public void Text_shadow_initial_is_none() {
            // CSS Text Decoration L4 §6: initial value is `none`.
            var cs = Compute("");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("none"));
        }

        // ── inheritance ───────────────────────────────────────────────────

        [Test]
        public void Text_shadow_inherits_from_parent() {
            // CSS Text Decoration L4 §6: text-shadow IS inherited.
            // A child without its own rule sees the parent's value.
            var cs = ComputeChild("#parent { text-shadow: 2px 2px 4px red; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("2px 2px 4px red"),
                "text-shadow is inherited; child must see parent's value");
        }

        [Test]
        public void Text_shadow_none_initial_does_not_inherit_from_parent_none() {
            // When parent also has none, child sees none (trivial but pins the
            // inheritance direction is correct).
            var cs = ComputeChild("#parent { text-shadow: none; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("none"));
        }

        // ── single shadow — offset-x offset-y only ────────────────────────

        [Test]
        public void Text_shadow_two_lengths_round_trips() {
            // Minimum valid form: <offset-x> <offset-y>
            var cs = Compute("#x { text-shadow: 3px 4px; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("3px 4px"));
        }

        // ── single shadow — with blur-radius ──────────────────────────────

        [Test]
        public void Text_shadow_three_lengths_round_trips() {
            // Three-length form: <offset-x> <offset-y> <blur-radius>
            var cs = Compute("#x { text-shadow: 1px 2px 5px; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("1px 2px 5px"));
        }

        // ── color position variants ───────────────────────────────────────

        [Test]
        public void Text_shadow_color_after_lengths_round_trips() {
            // CSS Text Decoration L4 §6.2: color may come after the lengths.
            var cs = Compute("#x { text-shadow: 2px 2px 4px blue; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("2px 2px 4px blue"));
        }

        [Test]
        public void Text_shadow_color_before_lengths_round_trips() {
            // CSS Text Decoration L4 §6.2: color is also valid BEFORE the lengths.
            // Both orderings are spec-legal; cascade must carry the value verbatim.
            var cs = Compute("#x { text-shadow: red 4px 4px 2px; }");
            var got = cs.Get("text-shadow");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("none"),
                "text-shadow: <color> <x> <y> <blur> must be accepted");
            Assert.That(got, Does.Contain("4px"),
                "offset lengths must survive the cascade");
        }

        [Test]
        public void Text_shadow_color_only_with_two_lengths_round_trips() {
            // <color> <offset-x> <offset-y> — blur omitted, color first
            var cs = Compute("#x { text-shadow: green 1px 3px; }");
            var got = cs.Get("text-shadow");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("none"));
            Assert.That(got, Does.Contain("1px"));
        }

        // ── none keyword ──────────────────────────────────────────────────

        [Test]
        public void Text_shadow_none_round_trips() {
            // `none` is the initial and can be explicitly authored.
            var cs = Compute("#x { text-shadow: none; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("none"));
        }

        [Test]
        public void Text_shadow_none_overrides_previous_shadow() {
            // Higher-specificity `none` must win over a less-specific shadow.
            var doc = Html("<p id=\"x\">text</p>");
            var engine = new CascadeEngine(new[] {
                Author("p { text-shadow: 2px 2px red; } #x { text-shadow: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("none"));
        }

        // ── multi-shadow comma lists ──────────────────────────────────────

        [Test]
        public void Text_shadow_two_shadow_list_round_trips() {
            // CSS Text Decoration L4 §6: comma-separated list of shadows.
            var cs = Compute("#x { text-shadow: 1px 1px red, 3px 3px blue; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("1px 1px red, 3px 3px blue"));
        }

        [Test]
        public void Text_shadow_three_shadow_list_round_trips() {
            // Three shadows are common for HUD glow effects (inner, mid, outer).
            var cs = Compute("#x { text-shadow: 0 0 4px white, 1px 1px 2px red, -1px -1px 2px blue; }");
            var got = cs.Get("text-shadow");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("none"));
            Assert.That(got, Does.Contain("white"));
            Assert.That(got, Does.Contain("red"));
            Assert.That(got, Does.Contain("blue"));
        }

        [Test]
        public void Text_shadow_multi_shadow_mixed_color_positions() {
            // Mix of color-before and color-after in different layers.
            var cs = Compute("#x { text-shadow: red 1px 1px, 2px 2px blue; }");
            var got = cs.Get("text-shadow");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("none"));
            Assert.That(got, Does.Contain("red"));
            Assert.That(got, Does.Contain("blue"));
        }

        // ── cascade overriding ────────────────────────────────────────────

        [Test]
        public void Text_shadow_later_rule_overrides_earlier() {
            // Higher specificity wins.
            var doc = Html("<p id=\"x\">text</p>");
            var engine = new CascadeEngine(new[] {
                Author("p { text-shadow: 1px 1px red; } #x { text-shadow: 5px 5px blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("5px 5px blue"));
        }

        // ── negative offsets ──────────────────────────────────────────────

        [Test]
        public void Text_shadow_negative_offsets_round_trip() {
            // Negative offsets are valid (shadows to the upper-left).
            var cs = Compute("#x { text-shadow: -2px -3px 4px black; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("-2px -3px 4px black"));
        }

        // ── zero-offset shadows (glow effect) ────────────────────────────

        [Test]
        public void Text_shadow_zero_offset_glow_round_trips() {
            // Game UIs commonly use 0 0 <blur> <color> for a glow effect.
            var cs = Compute("#x { text-shadow: 0 0 8px gold; }");
            Assert.That(cs.Get("text-shadow"), Is.EqualTo("0 0 8px gold"));
        }
    }
}
