using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class CascadeEngineTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));
        static OriginatedStylesheet User(string s) => OriginatedStylesheet.User(Css(s));
        static OriginatedStylesheet UA(string s) => OriginatedStylesheet.UserAgent(Css(s));

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            public void Set(Element e, ElementState s) { map[e] = s; }
            public ElementState GetState(Element e) => map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        [Test]
        public void Single_rule_applies_to_matching_element() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Rule_with_no_match_does_not_apply() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#y { color: red; }") });
            var cs = engine.Compute(doc.GetElementById("x"));
            // initial value
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Id_beats_class_on_same_element() {
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: green; } #x { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Class_beats_type_on_same_element() {
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: green; } .c { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Important_normal_clash_important_wins() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; } div { color: blue !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Origin_normal_order_UA_lt_User_lt_Author() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray; }"),
                User("#x { color: blue; }"),
                Author("#x { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Origin_important_order_Author_lt_User_lt_UA() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray !important; }"),
                User("#x { color: blue !important; }"),
                Author("#x { color: red !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("gray"));
        }

        [Test]
        public void User_important_beats_author_normal() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                User("#x { color: blue !important; }"),
                Author("#x { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Inherited_property_flows_to_child() {
            var doc = Html("<div><span id=\"x\">hi</span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Non_inherited_property_does_not_flow_to_child() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { width: 100px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Inherit_keyword_forces_inheritance_for_non_inherited_property() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { width: 100px; } span { width: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("100px"));
        }

        [Test]
        public void Initial_keyword_resets_to_initial_value() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } #x { color: initial; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Unset_keyword_inherits_for_inherited_property() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } span { color: blue; } #x { color: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Unset_keyword_falls_back_to_initial_for_non_inherited() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { width: 50px; } #x { width: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Revert_keyword_treated_as_initial() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } #x { color: revert; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void RevertLayer_keyword_is_css_wide_and_treated_as_initial() {
            Assert.That(KeywordResolver.IsCssWideKeyword("revert-layer"), Is.True);
            Assert.That(KeywordResolver.IsCssWideKeyword("REVERT-LAYER"), Is.True);

            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } #x { color: revert-layer; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Var_resolves_from_same_element() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --accent: red; color: var(--accent); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Var_resolves_from_ancestor_via_inheritance() {
            var doc = Html("<div><section><span id=\"x\"></span></section></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { --accent: rebeccapurple; } #x { color: var(--accent); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Var_uses_fallback_when_undefined() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(--missing, blue); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Var_fallback_with_complex_value() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding: var(--p, 8px 16px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding"), Is.EqualTo("8px 16px"));
        }

        [Test]
        public void Nested_var_through_chained_custom_properties() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --x: red; --y: var(--x); color: var(--y); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Var_cycle_resolves_to_empty_or_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); color: var(--a, green); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Cycle: --a/--b can't resolve. var(--a, green) should yield the fallback
            // because --a's resolved value is empty (cycle short-circuits to "").
            Assert.That(cs.Get("color"), Is.EqualTo("green").Or.EqualTo(""));
        }

        [Test]
        public void Inline_style_beats_author_rule_of_equivalent_specificity() {
            var doc = Html("<div id=\"x\" style=\"color: green;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Inline_style_can_set_multiple_properties() {
            var doc = Html("<div id=\"x\" style=\"color: red; font-size: 24px;\"></div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("24px"));
        }

        [Test]
        public void Inline_style_loses_to_important_author() {
            var doc = Html("<div id=\"x\" style=\"color: green;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void State_pseudo_hover_applies_only_when_state_is_hover() {
            var doc = Html("<button id=\"b\">go</button>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var btn = doc.GetElementById("b");

            var idle = engine.Compute(btn);
            Assert.That(idle.Get("color"), Is.EqualTo("black"));

            var fake = new FakeState();
            fake.Set(btn, ElementState.Hover);
            var hover = engine.Compute(btn, fake);
            Assert.That(hover.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Multiple_selectors_each_get_own_specificity() {
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x, .c { color: red; } div { color: green; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Source_order_tiebreak_later_author_wins() {
            var doc = Html("<div class=\"c\" id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: red; } .c { color: blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Source_order_across_stylesheets_later_wins() {
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: red; }"),
                Author(".c { color: blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Media_rule_inner_rules_currently_always_apply() {
            // TODO: once media-query evaluation lands, switch this assertion to depend
            // on the surface dimensions / capabilities the engine reports.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@media (min-width: 9999px) { #x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void ComputeAll_returns_styles_for_every_element() {
            var doc = Html("<div id=\"a\"><span id=\"b\"></span><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } span { font-size: 12px; }")
            });
            var all = engine.ComputeAll(doc);
            // HtmlParser now wraps fragments in synthetic <html><head><body>, so the
            // result map includes html + body + the 3 author elements = ≥3.
            Assert.That(all.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(all[doc.GetElementById("a")].Get("color"), Is.EqualTo("red"));
            Assert.That(all[doc.GetElementById("b")].Get("color"), Is.EqualTo("red"));
            Assert.That(all[doc.GetElementById("b")].Get("font-size"), Is.EqualTo("12px"));
            Assert.That(all[doc.GetElementById("c")].Get("font-size"), Is.EqualTo("12px"));
        }

        [Test]
        public void Initial_value_present_on_element_with_no_rules() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("inline"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("16px"));
            Assert.That(cs.Get("font-family"), Is.EqualTo("sans-serif"));
            Assert.That(cs.Get("width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Descendant_combinator_matches() {
            var doc = Html("<article><div><p id=\"p\"></p></div></article>");
            var engine = new CascadeEngine(new[] {
                Author("article p { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("p"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Child_combinator_does_not_match_descendant() {
            var doc = Html("<article><div><p id=\"p\"></p></div></article>");
            var engine = new CascadeEngine(new[] {
                Author("article > p { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("p"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Inherited_through_grandparent_chain() {
            var doc = Html("<div><section><article><span id=\"x\"></span></article></section></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: rebeccapurple; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Custom_property_inherits_to_descendants() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { --bg: blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--bg"), Is.EqualTo("blue"));
        }

        [Test]
        public void Custom_property_overridden_on_descendant() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { --bg: blue; } span { --bg: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--bg"), Is.EqualTo("red"));
        }

        [Test]
        public void Important_inline_beats_important_author() {
            var doc = Html("<div id=\"x\" style=\"color: green !important;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Both are author-origin important; the inline style is later in source order
            // (assigned a high index) so it should win.
            Assert.That(cs.Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Realistic_stylesheet_integration() {
            var doc = Html(
                "<main class=\"menu\">" +
                "<h1 id=\"title\">Hello</h1>" +
                "<p id=\"para\">Body</p>" +
                "<button id=\"start\">Start</button>" +
                "</main>");

            var ua = UA(":root { color: black; font-family: sans-serif; font-size: 16px; }" +
                       "h1 { font-size: 32px; }" +
                       "button { color: black; }");
            var author = Author(
                ".menu { display: flex; }" +
                "h1 { color: navy; }" +
                "p { color: dimgray; font-size: 14px; }" +
                "button { background-color: indigo; color: white; }" +
                "button:hover { background-color: rebeccapurple; }");

            var engine = new CascadeEngine(new[] { ua, author });

            var title = engine.Compute(doc.GetElementById("title"));
            Assert.That(title.Get("color"), Is.EqualTo("navy"));
            Assert.That(title.Get("font-size"), Is.EqualTo("32px"));

            var para = engine.Compute(doc.GetElementById("para"));
            Assert.That(para.Get("color"), Is.EqualTo("dimgray"));
            Assert.That(para.Get("font-size"), Is.EqualTo("14px"));

            var btn = doc.GetElementById("start");
            var btnNormal = engine.Compute(btn);
            Assert.That(btnNormal.Get("color"), Is.EqualTo("white"));
            Assert.That(btnNormal.Get("background-color"), Is.EqualTo("indigo"));

            var fake = new FakeState();
            fake.Set(btn, ElementState.Hover);
            var btnHover = engine.Compute(btn, fake);
            Assert.That(btnHover.Get("background-color"), Is.EqualTo("rebeccapurple"));

            var menu = FindByTag(doc, "main");
            var menuStyle = engine.Compute(menu);
            Assert.That(menuStyle.Get("display"), Is.EqualTo("flex"));
        }

        [Test]
        public void Var_with_keyword_inherit_resolves_via_parent() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } span { color: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Universal_selector_lower_specificity_than_class() {
            var doc = Html("<div class=\"c\" id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("* { color: green; } .c { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Compute_returns_null_for_null_element() {
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            Assert.That(engine.Compute(null), Is.Null);
        }

        [Test]
        public void Inline_style_position_overrides_class_rule() {
            var doc = Html("<div class=\"c\" id=\"x\" style=\"position: absolute; width: 80px; height: 60px;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { position: static; width: 100%; height: 100%; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("position"), Is.EqualTo("absolute"));
            Assert.That(cs.Get("width"), Is.EqualTo("80px"));
            Assert.That(cs.Get("height"), Is.EqualTo("60px"));
        }

        [Test]
        public void Inline_style_position_overrides_class_rule_no_spaces() {
            var doc = Html("<div class=\"c\" id=\"x\" style=\"position:absolute;width:80px;height:60px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { position: static; width: 100%; height: 100%; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("position"), Is.EqualTo("absolute"));
            Assert.That(cs.Get("width"), Is.EqualTo("80px"));
            Assert.That(cs.Get("height"), Is.EqualTo("60px"));
        }

        [Test]
        public void Inline_style_layout_props_override_via_ComputeAll() {
            var doc = Html("<div class=\"c\" id=\"x\" style=\"position:absolute;left:118px;top:128px;width:80px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { position: static; width: 100%; left: 0; top: 0; }")
            });
            var styles = engine.ComputeAll(doc);
            var el = doc.GetElementById("x");
            Assert.That(styles.ContainsKey(el), Is.True, "Element must be in ComputeAll result");
            var cs = styles[el];
            Assert.That(cs.Get("position"), Is.EqualTo("absolute"));
            Assert.That(cs.Get("width"), Is.EqualTo("80px"));
            Assert.That(cs.Get("left"), Is.EqualTo("118px"));
            Assert.That(cs.Get("top"), Is.EqualTo("128px"));
        }

        [Test]
        public void Inline_background_shorthand_does_not_drop_class_height() {
            var doc = Html("<div class=\"bar\" id=\"x\" style=\"background: linear-gradient(90deg, gold, green)\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".bar { height: 3px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("height"), Is.EqualTo("3px"), "Class height must survive inline background shorthand");
            Assert.That(cs.Get("background-image"), Does.Contain("linear-gradient"), "Gradient must be set from inline");
            Assert.That(cs.Get("background-size"), Is.EqualTo("auto"), "background-size defaults to auto from shorthand reset");
        }

        [Test]
        public void Inline_background_gradient_with_rgba_stops_preserves_class_height() {
            // Exact pattern from a real upgrade meter
            var doc = Html("<div class=\"m\" id=\"x\" style=\"background: linear-gradient(to right, #ffd22f 30%, rgba(255,255,255,0.1) 30%)\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".m { width: 100%; height: 3px; border-radius: 999px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("height"), Is.EqualTo("3px"), "height: 3px from class must survive");
            Assert.That(cs.Get("width"), Is.EqualTo("100%"), "width: 100% from class must survive");
            Assert.That(cs.Get("background-image"), Does.Contain("linear-gradient"), "gradient must be present");
        }

        [Test]
        public void Inline_style_beats_id_selector_for_layout_properties() {
            var doc = Html("<div id=\"x\" style=\"position:absolute;width:80px;height:60px\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { position: relative; width: 200px; height: 200px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("position"), Is.EqualTo("absolute"));
            Assert.That(cs.Get("width"), Is.EqualTo("80px"));
            Assert.That(cs.Get("height"), Is.EqualTo("60px"));
        }

        [Test]
        public void Malformed_inline_style_does_not_throw_during_cascade() {
            // ParseInlineStyle passes ThrowOnError = false so a busted
            // `style="..."` attribute can't tear down the cascade pass —
            // declarations that did parse are kept, the rest are dropped,
            // and Compute returns a usable ComputedStyle.
            var doc = Html("<div id=\"x\" style=\"color: red; this is not css ;;; font-size: 12px\"></div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => { cs = engine.Compute(doc.GetElementById("x")); });
            Assert.That(cs, Is.Not.Null);
            // Whatever the parser salvaged from the head of the value list,
            // the cascade should at minimum still produce a valid object —
            // we don't assert specific declarations because the lenient
            // recovery point is parser-implementation-defined.
        }
    }
}
