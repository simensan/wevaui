using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class CascadeShorthandIntegrationTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));
        static OriginatedStylesheet UA(string s) => OriginatedStylesheet.UserAgent(Css(s));

        [Test]
        public void Padding_shorthand_produces_four_longhands_in_computed_style() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { padding: 24px; }") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("24px"));
            Assert.That(cs.Get("padding-right"), Is.EqualTo("24px"));
            Assert.That(cs.Get("padding-bottom"), Is.EqualTo("24px"));
            Assert.That(cs.Get("padding-left"), Is.EqualTo("24px"));
        }

        [Test]
        public void Margin_64px_auto_centers_horizontally() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { margin: 64px auto; }") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("margin-top"), Is.EqualTo("64px"));
            Assert.That(cs.Get("margin-bottom"), Is.EqualTo("64px"));
            Assert.That(cs.Get("margin-left"), Is.EqualTo("auto"));
            Assert.That(cs.Get("margin-right"), Is.EqualTo("auto"));
        }

        [Test]
        public void Border_1px_solid_red_sets_all_twelve_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { border: 1px solid red; }") });
            var cs = engine.Compute(doc.GetElementById("x"));
            foreach (var side in new[] { "top", "right", "bottom", "left" }) {
                Assert.That(cs.Get($"border-{side}-width"), Is.EqualTo("1px"));
                Assert.That(cs.Get($"border-{side}-style"), Is.EqualTo("solid"));
                Assert.That(cs.Get($"border-{side}-color"), Is.EqualTo("red"));
            }
        }

        [Test]
        public void Author_padding_shorthand_overrides_earlier_UA_padding_top() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { padding-top: 0; }"),
                Author("#x { padding: 24px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("24px"));
        }

        [Test]
        public void Important_on_shorthand_propagates_to_all_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding: 8px !important; padding-top: 100px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("8px"));
            Assert.That(cs.Get("padding-right"), Is.EqualTo("8px"));
        }

        [Test]
        public void Initial_value_reset_background_then_explicit_image() {
            // background: red resets background-image to none, then explicit longhand wins.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { background: red; background-image: url(x.png); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("red"));
            Assert.That(cs.Get("background-image"), Is.EqualTo("url(x.png)"));
        }

        [Test]
        public void Border_solid_then_explicit_border_color_wins() {
            // Per CSS spec: shorthand resets color to currentcolor, then explicit override.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { border: solid; border-color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("border-top-color"), Is.EqualTo("red"));
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("solid"));
        }

        [Test]
        public void Border_radius_shorthand_expands_to_four_corner_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { border-radius: 12px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("border-top-left-radius"), Is.EqualTo("12px"));
            Assert.That(cs.Get("border-top-right-radius"), Is.EqualTo("12px"));
            Assert.That(cs.Get("border-bottom-right-radius"), Is.EqualTo("12px"));
            Assert.That(cs.Get("border-bottom-left-radius"), Is.EqualTo("12px"));
        }

        [Test]
        public void Transition_shorthand_expands_to_four_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { transition: opacity 200ms ease-in; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("transition-property"), Is.EqualTo("opacity"));
            Assert.That(cs.Get("transition-duration"), Is.EqualTo("200ms"));
            Assert.That(cs.Get("transition-timing-function"), Is.EqualTo("ease-in"));
            Assert.That(cs.Get("transition-delay"), Is.EqualTo("0s"));
        }

        [Test]
        public void Phase_one_demo_menu_rule_works_with_all_shorthands_restored() {
            var doc = Html("<div id=\"m\" class=\"menu\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".menu { padding: 24px; margin: 64px auto; background: white; border-radius: 12px; }")
            });
            var cs = engine.Compute(doc.GetElementById("m"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("24px"));
            Assert.That(cs.Get("padding-left"), Is.EqualTo("24px"));
            Assert.That(cs.Get("margin-top"), Is.EqualTo("64px"));
            Assert.That(cs.Get("margin-left"), Is.EqualTo("auto"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("white"));
            Assert.That(cs.Get("border-top-left-radius"), Is.EqualTo("12px"));
        }

        [Test]
        public void Custom_property_with_shorthand_name_is_not_expanded() {
            // --padding is a custom property; it must pass through verbatim.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --padding: 8px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--padding"), Is.EqualTo("8px"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("0"));
        }

        [Test]
        public void Var_in_shorthand_value_is_preserved_for_var_resolution() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding: var(--p, 8px 16px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // The shorthand survives var resolution at its own slot, AND the
            // post-resolution expansion populates per-side longhands so layout
            // sees `padding-top: 8px` etc. without needing to read the shorthand.
            Assert.That(cs.Get("padding"), Is.EqualTo("8px 16px"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("8px"));
            Assert.That(cs.Get("padding-right"), Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-bottom"), Is.EqualTo("8px"));
            Assert.That(cs.Get("padding-left"), Is.EqualTo("16px"));
        }

        [Test]
        public void Var_in_background_shorthand_expands_to_background_color_after_resolution() {
            // Regression: `.panel { background: var(--panel) }` from the demo
            // used to leave background-color empty because pre-cascade
            // expansion bails on var()-bearing shorthands. The post-resolution
            // expansion in ComputeFor populates background-color from the
            // resolved color string, restoring visible panel chrome.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":root { --panel: #232730; } #x { background: var(--panel); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("#232730"));
        }

        [Test]
        public void Var_in_background_gradient_shorthand_expands_to_background_image_after_resolution() {
            // match3-endgame uses `background: linear-gradient(... var(...))`.
            // Paint only reads background-image, so late shorthand expansion
            // must preserve the resolved gradient rather than leaving the
            // gradient parked on the shorthand slot.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":root { --pink: #ff4fa3; --gold: #fbbf24; --green: #4ade80; } "
                    + "#x { background: linear-gradient(90deg, var(--pink), var(--gold), var(--green)); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-image"),
                Is.EqualTo("linear-gradient(90deg, #ff4fa3, #fbbf24, #4ade80)"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("transparent"));
        }

        [Test]
        public void Background_shorthand_recognises_lab_lch_color_functions() {
            // ShorthandTokens.IsColorFunction was missing lab()/lch()/color(),
            // so `background: lab(50% 0 0)` etc. went through the pre-expansion
            // skip and the late re-expansion path also rejected (the resolved
            // string is still a lab() / lch() / color(), not a hex). Adding
            // them to the predicate lets these tokens land as background-color.
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#a { background: lab(50% 0 0); } " +
                       "#b { background: lch(50% 50 270); } " +
                       "#c { background: color(display-p3 1 0 0); }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("background-color"),
                Is.EqualTo("lab(50% 0 0)"));
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("background-color"),
                Is.EqualTo("lch(50% 50 270)"));
            Assert.That(engine.Compute(doc.GetElementById("c")).Get("background-color"),
                Is.EqualTo("color(display-p3 1 0 0)"));
        }

        [Test]
        public void Main_menu_panel_var_background_and_border_resolve_through_cascade() {
            // Repro for the AI claim that `background: var(--bg-panel)` and
            // `border: 1px solid var(--border-subtle)` paint nothing in
            // Assets/UI/main-menu.css. The mechanism (IsColor() rejects var())
            // is bypassed by the late re-expansion path in CascadeEngine; this
            // pins that the cascade actually populates the longhands so any
            // remaining visual breakage has to be downstream of cascade.
            var doc = Html("<div class=\"panel\" id=\"p\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    ":root {" +
                    "  --bg-panel: #131826;" +
                    "  --border-subtle: rgba(255, 255, 255, 0.05);" +
                    "  --radius-md: 8px;" +
                    "}" +
                    ".panel {" +
                    "  background: var(--bg-panel);" +
                    "  border: 1px solid var(--border-subtle);" +
                    "  border-radius: var(--radius-md);" +
                    "}")
            });
            var cs = engine.Compute(doc.GetElementById("p"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("#131826"));
            Assert.That(cs.Get("border-top-width"), Is.EqualTo("1px"));
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("solid"));
            Assert.That(cs.Get("border-top-color"), Is.EqualTo("rgba(255, 255, 255, 0.05)"));
            Assert.That(cs.Get("border-right-color"), Is.EqualTo("rgba(255, 255, 255, 0.05)"));
            Assert.That(cs.Get("border-bottom-color"), Is.EqualTo("rgba(255, 255, 255, 0.05)"));
            Assert.That(cs.Get("border-left-color"), Is.EqualTo("rgba(255, 255, 255, 0.05)"));
            Assert.That(cs.Get("border-top-left-radius"), Is.EqualTo("8px"));
        }

        [Test]
        public void Var_in_border_shorthand_expands_to_per_side_color_after_resolution() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":root { --line: #383d4a; } #x { border: 1px solid var(--line); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("border-top-width"), Is.EqualTo("1px"));
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("solid"));
            Assert.That(cs.Get("border-top-color"), Is.EqualTo("#383d4a"));
            Assert.That(cs.Get("border-right-color"), Is.EqualTo("#383d4a"));
        }

        [Test]
        public void Author_var_border_shorthand_overrides_UA_expanded_border_top_color() {
            // Regression: a UA rule `input { border: 1px solid #ccc; }` (no var()) is
            // expanded at parse time into per-side longhands. A more-specific author
            // rule `.search input { border: 1px solid var(--border); }` defers
            // expansion until after var() resolution. The late-expansion path used
            // to skip writing any per-side longhand whose key already had an entry
            // in rawValues, regardless of cascade priority — so UA's per-side colors
            // bled through despite the author shorthand winning the cascade for the
            // shorthand-named slot. Result was a one-pixel light-grey top border
            // with three dark borders. The fix compares cascade priority instead
            // of merely checking key presence.
            var doc = Html("<div class=\"search\"><input id=\"q\" /></div>");
            var engine = new CascadeEngine(new[] {
                UA("input { border: 1px solid #ccc; }"),
                Author(":root { --border: #1f2937; } .search input { border: 1px solid var(--border); }")
            });
            var cs = engine.Compute(doc.GetElementById("q"));
            Assert.That(cs.Get("border-top-color"), Is.EqualTo("#1f2937"));
            Assert.That(cs.Get("border-right-color"), Is.EqualTo("#1f2937"));
            Assert.That(cs.Get("border-bottom-color"), Is.EqualTo("#1f2937"));
            Assert.That(cs.Get("border-left-color"), Is.EqualTo("#1f2937"));
        }

        [Test]
        public void Higher_specificity_explicit_longhand_beats_lower_specificity_var_shorthand() {
            // Inverse: a high-specificity explicit longhand must NOT be clobbered
            // by a lower-specificity shorthand's late expansion.
            var doc = Html("<div id=\"q\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":root { --c: #1f2937; } #q { border-top-color: red; } .x { border: 1px solid var(--c); }")
            });
            var cs = engine.Compute(doc.GetElementById("q"));
            Assert.That(cs.Get("border-top-color"), Is.EqualTo("red"));
            Assert.That(cs.Get("border-right-color"), Is.EqualTo("#1f2937"));
        }
    }
}
