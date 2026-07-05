using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Nesting L1 §3 — cascade integration tests.  The NestingTests file
    // (under Css/Parsing) pins the PARSER expansion.  This file pins that the
    // EXPANDED selectors actually cascade correctly: the right computed values
    // reach the right elements, specificity is honoured, and nested @media
    // conditions gate their inner rules properly.
    //
    // Reference: CSS Nesting §3 "Nesting Style Rules" and §4 "Nesting
    // Other At-Rules" (CSS WD 2023-02-09).  Nesting is implemented via
    // NestingExpander; tests assume the expander flattens to standard
    // style rules before cascade evaluation.
    public class NestingCascadeIntegrationTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ---- Basic cascade application of nested rules ----------------------

        [Test]
        public void Nested_ampersand_pseudo_applies_hover_color_cascade() {
            // .btn:hover { color: blue } via .btn { &:hover { color: blue } }
            // Without hover state, the nested rule is still parsed; the cascade
            // should only apply when the state matches. In headless mode we
            // can test the non-hover path: .btn gets its own color, not
            // the nested hover color.
            var doc = Html("<button id=\"b\" class=\"btn\">X</button>");
            var engine = new CascadeEngine(new[] {
                Author(@".btn { color: red; &:hover { color: blue; } }")
            });
            var cs = engine.Compute(doc.GetElementById("b"));
            // Without hover state, the nested rule doesn't apply.
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Nested_descendant_applies_to_child_element() {
            // .parent { .child { color: red; } } → .parent .child { color: red; }
            var doc = Html("<div class=\"parent\"><span id=\"c\" class=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author(@".parent { color: black; .child { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("c"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Nested_descendant_does_not_affect_non_children() {
            // .parent .child must not match an element outside .parent.
            var doc = Html("<div class=\"parent\"></div><span id=\"lone\" class=\"child\"></span>");
            var engine = new CascadeEngine(new[] {
                Author(@".parent { .child { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("lone"));
            // Initial value for color is black (not red).
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Nested_direct_child_combinator_is_specific() {
            // .btn > .icon { color: blue } via .btn { & > .icon { color: blue } }
            var doc = Html(
                "<div class=\"btn\">" +
                "<span id=\"direct\" class=\"icon\"></span>" +
                "<div><span id=\"nested\" class=\"icon\"></span></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author(@".btn { & > .icon { color: blue; } }")
            });
            var direct = engine.Compute(doc.GetElementById("direct"));
            var nested = engine.Compute(doc.GetElementById("nested"));
            // Direct child matches; deeply-nested child doesn't.
            Assert.That(direct.Get("color"), Is.EqualTo("blue"));
            Assert.That(nested.Get("color"), Is.EqualTo("black"));
        }

        // ---- Specificity of nested rules ------------------------------------

        [Test]
        public void Nested_rule_specificity_equals_expanded_specificity() {
            // CSS Nesting §3.1: "The specificity of the nested rule's selector
            // is the specificity of the expanded selector." A nested .a .b
            // selector has specificity 0,2,0; an outer #x selector has 1,0,0.
            // The id rule must still win.
            var doc = Html("<div id=\"x\" class=\"a\"><span id=\"s\" class=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    .a { .b { color: red; } }
                    #s { color: blue; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("s"));
            // #s specificity (1,0,0) beats .a .b (0,2,0).
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Nested_rule_loses_to_later_same_specificity_rule() {
            // Source order tiebreak: the inline .b rule after the nesting wins.
            var doc = Html("<div class=\"a\"><span id=\"s\" class=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    .a { .b { color: red; } }
                    .a .b { color: blue; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("s"));
            // Both have specificity 0,2,0; the later rule wins.
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        // ---- Nested @media inside a rule ------------------------------------

        [Test]
        public void Nested_media_fires_when_viewport_matches() {
            // .card { @media (min-width: 100px) { & { color: red; } } }
            // With a viewport of 800px the @media matches; color is red.
            var doc = Html("<div id=\"c\" class=\"card\"></div>");
            var engine = new CascadeEngine(
                new[] {
                    Author(@".card { @media (min-width: 100px) { & { color: red; } } }")
                },
                MediaContext.Default(800, 600));
            var cs = engine.Compute(doc.GetElementById("c"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Nested_media_does_not_fire_when_viewport_too_narrow() {
            // Same structure but viewport is 50px — @media (min-width: 100px) fails.
            var doc = Html("<div id=\"c\" class=\"card\"></div>");
            var engine = new CascadeEngine(
                new[] {
                    Author(@".card { color: black; @media (min-width: 100px) { & { color: red; } } }")
                },
                MediaContext.Default(50, 600));
            var cs = engine.Compute(doc.GetElementById("c"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        // ---- Multiple levels of nesting -------------------------------------

        [Test]
        public void Three_level_nesting_resolves_descendant_chain() {
            // .a { .b { .c { color: red; } } } → .a .b .c { color: red; }
            var doc = Html("<div class=\"a\"><div class=\"b\"><span id=\"t\" class=\"c\"></span></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@".a { .b { .c { color: red; } } }")
            });
            var cs = engine.Compute(doc.GetElementById("t"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Parent_declarations_are_independent_of_nested_child() {
            // .parent { color: black; .child { color: red; } }
            // The parent element gets `color: black`; a child element gets
            // `color: red` via the nested rule. Neither bled into the other.
            var doc = Html("<div id=\"p\" class=\"parent\"><span id=\"c\" class=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author(@".parent { color: black; .child { color: red; } }")
            });
            var parent = engine.Compute(doc.GetElementById("p"));
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(parent.Get("color"), Is.EqualTo("black"));
            Assert.That(child.Get("color"), Is.EqualTo("red"));
        }

        // ---- Nesting combined with @supports --------------------------------

        [Test]
        public void Nested_supports_fires_when_property_known() {
            // .x { @supports (display: flex) { & { color: red; } } }
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@".x { @supports (display: flex) { & { color: red; } } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Nested_supports_skips_when_property_unknown() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@".x { color: black; @supports (foo: bar) { & { color: red; } } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }
    }
}
