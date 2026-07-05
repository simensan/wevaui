using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // B1 + B2 — Regression tests for nested @container and @scope full-chain evaluation.
    //
    // Before the fix:
    //   - Nested @container kept ONLY the innermost condition; outer conditions
    //     were dropped. A deeply nested rule would apply even when an outer
    //     @container's condition was false.
    //   - Nested @scope kept ONLY the innermost @scope window; outer windows
    //     were dropped. An element inside only the innermost scope would match
    //     even when it was outside the outer scope.
    //
    // After the fix (CSS Containment L3 §3 / CSS Cascade L6 §7):
    //   - Every condition in a nested @container stack must be satisfied.
    //   - Every scope window in a nested @scope stack must contain the element.
    public class NestedConditionalRuleTests {
        sealed class TestBox : BlockBox { }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ── Fake box-index helpers ───────────────────────────────────────────

        sealed class FakeBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public Box Lookup(Element e) => e != null && map.TryGetValue(e, out var b) ? b : null;
            public void Add(Element e, Box b) { map[e] = b; }
            public Func<Element, Box> AsFunc => Lookup;
        }

        // Builds a two-level container hierarchy:
        //   outerDiv (inline-size container, outerWidth × outerHeight)
        //     innerDiv (inline-size container, innerWidth × innerHeight)
        //       targetSpan (no container-type)
        static (Document doc, Element outerDiv, Element innerDiv, Element target, FakeBoxIndex index)
            BuildTwoLevelDoc(double outerWidth, double outerHeight,
                             double innerWidth, double innerHeight) {
            var doc = Html("<div id='outer'><div id='inner'><span id='target'>x</span></div></div>");
            var outer = doc.GetElementById("outer");
            var inner = doc.GetElementById("inner");
            var target = doc.GetElementById("target");

            var index = new FakeBoxIndex();

            var outerStyle = new ComputedStyle(outer);
            outerStyle.Set("container-type", "inline-size");
            var outerBox = new TestBox { Element = outer, Style = outerStyle, Width = outerWidth, Height = outerHeight };

            var innerStyle = new ComputedStyle(inner);
            innerStyle.Set("container-type", "inline-size");
            var innerBox = new TestBox { Element = inner, Style = innerStyle, Width = innerWidth, Height = innerHeight };
            outerBox.AddChild(innerBox);

            var targetStyle = new ComputedStyle(target);
            var targetBox = new TestBox { Element = target, Style = targetStyle };
            innerBox.AddChild(targetBox);

            index.Add(outer, outerBox);
            index.Add(inner, innerBox);
            index.Add(target, targetBox);
            return (doc, outer, inner, target, index);
        }

        // Builds a three-level container hierarchy:
        //   outerDiv → middleDiv → innerDiv → targetSpan
        static (Document doc, Element outer, Element middle, Element inner, Element target, FakeBoxIndex index)
            BuildThreeLevelDoc(double outerW, double middleW, double innerW) {
            var doc = Html("<div id='outer'><div id='middle'><div id='inner'><span id='target'>x</span></div></div></div>");
            var outer = doc.GetElementById("outer");
            var middle = doc.GetElementById("middle");
            var inner = doc.GetElementById("inner");
            var target = doc.GetElementById("target");

            var index = new FakeBoxIndex();

            var outerStyle = new ComputedStyle(outer);
            outerStyle.Set("container-type", "inline-size");
            var outerBox = new TestBox { Element = outer, Style = outerStyle, Width = outerW };

            var middleStyle = new ComputedStyle(middle);
            middleStyle.Set("container-type", "inline-size");
            var middleBox = new TestBox { Element = middle, Style = middleStyle, Width = middleW };
            outerBox.AddChild(middleBox);

            var innerStyle = new ComputedStyle(inner);
            innerStyle.Set("container-type", "inline-size");
            var innerBox = new TestBox { Element = inner, Style = innerStyle, Width = innerW };
            middleBox.AddChild(innerBox);

            var targetStyle = new ComputedStyle(target);
            var targetBox = new TestBox { Element = target, Style = targetStyle };
            innerBox.AddChild(targetBox);

            index.Add(outer, outerBox);
            index.Add(middle, middleBox);
            index.Add(inner, innerBox);
            index.Add(target, targetBox);
            return (doc, outer, middle, inner, target, index);
        }

        // ── B1: nested @container ────────────────────────────────────────────

        [Test]
        public void Two_level_nested_container_both_conditions_satisfied_rule_applies() {
            // outer=800px satisfies (min-width: 600px)
            // inner=400px satisfies (min-width: 300px)
            var (doc, _, _, target, index) = BuildTwoLevelDoc(800, 600, 400, 300);
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @container (min-width: 600px) {
                        @container (min-width: 300px) {
                            span { color: red; }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.EqualTo("red"),
                "Both container conditions satisfied — rule must apply");
        }

        [Test]
        public void Two_level_nested_container_outer_condition_false_rule_does_not_apply() {
            // outer=400px does NOT satisfy (min-width: 600px)
            // inner=400px satisfies (min-width: 300px)
            var (doc, _, _, target, index) = BuildTwoLevelDoc(400, 600, 400, 300);
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @container (min-width: 600px) {
                        @container (min-width: 300px) {
                            span { color: red; }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.Not.EqualTo("red"),
                "Outer container condition false — rule must NOT apply even if inner is true");
        }

        [Test]
        public void Two_level_nested_container_inner_condition_false_rule_does_not_apply() {
            // outer=800px satisfies (min-width: 600px)
            // inner=100px does NOT satisfy (min-width: 300px)
            var (doc, _, _, target, index) = BuildTwoLevelDoc(800, 600, 100, 300);
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @container (min-width: 600px) {
                        @container (min-width: 300px) {
                            span { color: red; }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.Not.EqualTo("red"),
                "Inner container condition false — rule must NOT apply");
        }

        [Test]
        public void Three_level_nested_container_all_conditions_satisfied_rule_applies() {
            // outer=900px satisfies (min-width: 700px)
            // middle=500px satisfies (min-width: 400px)
            // inner=200px satisfies (min-width: 150px)
            var (doc, _, _, _, target, index) = BuildThreeLevelDoc(900, 500, 200);
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @container (min-width: 700px) {
                        @container (min-width: 400px) {
                            @container (min-width: 150px) {
                                span { color: blue; }
                            }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.EqualTo("blue"),
                "All three container conditions satisfied — rule must apply");
        }

        [Test]
        public void Three_level_nested_container_middle_condition_false_rule_does_not_apply() {
            // outer=900px satisfies (min-width: 700px)
            // middle=100px does NOT satisfy (min-width: 400px)
            // inner=200px satisfies (min-width: 150px)
            var (doc, _, _, _, target, index) = BuildThreeLevelDoc(900, 100, 200);
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @container (min-width: 700px) {
                        @container (min-width: 400px) {
                            @container (min-width: 150px) {
                                span { color: blue; }
                            }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.Not.EqualTo("blue"),
                "Middle container condition false — rule must NOT apply even with outer and inner true");
        }

        [Test]
        public void Nested_container_inside_media_rule_still_evaluates_full_chain() {
            // Inner @container inside @media. The media rule always passes (width=10000).
            // outer=800px satisfies (min-width: 600px)
            // inner=400px satisfies (min-width: 300px)
            var (doc, _, _, target, index) = BuildTwoLevelDoc(800, 600, 400, 300);
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @media (min-width: 1px) {
                        @container (min-width: 600px) {
                            @container (min-width: 300px) {
                                span { color: green; }
                            }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.EqualTo("green"),
                "@media + nested @container with all conditions true — rule must apply");
        }

        [Test]
        public void Single_level_container_unaffected_by_chain_fix() {
            // Regression: single-level @container should still work after the fix.
            var (doc, _, _, target, index) = BuildTwoLevelDoc(800, 600, 400, 300);
            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 300px) { span { color: orange; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.EqualTo("orange"),
                "Single-level @container must still work after the chain fix");
        }

        // ── B2: nested @scope ────────────────────────────────────────────────

        [Test]
        public void Two_level_nested_scope_both_windows_contain_element_rule_applies() {
            // <div class='outer'><div class='inner'><p id='target'>x</p></div></div>
            // outer @scope (.outer) + inner @scope (.inner) — p inside both
            var doc = Html("<div class='outer'><div class='inner'><p id='target'>x</p></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.outer) {
                        @scope (.inner) {
                            p { color: red; }
                        }
                    }
                ")
            });
            Assert.That(engine.Compute(doc.GetElementById("target")).Get("color"), Is.EqualTo("red"),
                "Target is inside both scopes — rule must apply");
        }

        [Test]
        public void Two_level_nested_scope_outer_scope_false_rule_does_not_apply() {
            // p is inside .inner but NOT inside .outer
            var doc = Html("<div class='other'><div class='inner'><p id='target'>x</p></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.outer) {
                        @scope (.inner) {
                            p { color: red; }
                        }
                    }
                ")
            });
            Assert.That(engine.Compute(doc.GetElementById("target")).Get("color"), Is.Not.EqualTo("red"),
                "Target is NOT in outer scope — rule must NOT apply");
        }

        [Test]
        public void Two_level_nested_scope_inner_scope_false_rule_does_not_apply() {
            // p is inside .outer but NOT inside .inner
            var doc = Html("<div class='outer'><div class='other'><p id='target'>x</p></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.outer) {
                        @scope (.inner) {
                            p { color: red; }
                        }
                    }
                ")
            });
            Assert.That(engine.Compute(doc.GetElementById("target")).Get("color"), Is.Not.EqualTo("red"),
                "Target is NOT in inner scope — rule must NOT apply");
        }

        [Test]
        public void Three_level_nested_scope_all_windows_contain_element_rule_applies() {
            var doc = Html(
                "<div class='a'><div class='b'><div class='c'><p id='t'>x</p></div></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.a) {
                        @scope (.b) {
                            @scope (.c) {
                                p { color: blue; }
                            }
                        }
                    }
                ")
            });
            Assert.That(engine.Compute(doc.GetElementById("t")).Get("color"), Is.EqualTo("blue"),
                "Target in all three scopes — rule must apply");
        }

        [Test]
        public void Three_level_nested_scope_middle_scope_false_rule_does_not_apply() {
            // .b is missing from the ancestry
            var doc = Html(
                "<div class='a'><div class='x'><div class='c'><p id='t'>x</p></div></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.a) {
                        @scope (.b) {
                            @scope (.c) {
                                p { color: blue; }
                            }
                        }
                    }
                ")
            });
            Assert.That(engine.Compute(doc.GetElementById("t")).Get("color"), Is.Not.EqualTo("blue"),
                "Middle scope (.b) absent — rule must NOT apply");
        }

        [Test]
        public void Scope_inside_container_both_must_be_satisfied() {
            // @container outer with @scope inside
            var doc = Html("<div id='outer'><div class='card'><p id='t'>x</p></div></div>");
            var outer = doc.GetElementById("outer");
            var card = doc.GetElementById("t");

            var index = new FakeBoxIndex();
            var outerStyle = new ComputedStyle(outer);
            outerStyle.Set("container-type", "inline-size");
            var outerBox = new TestBox { Element = outer, Style = outerStyle, Width = 800 };
            Element cardParent = null;
            foreach (var e in doc.GetElementsByClassName("card")) { cardParent = e; break; }
            var cardParentStyle = new ComputedStyle(cardParent);
            var cardParentBox = new TestBox { Element = cardParent, Style = cardParentStyle };
            outerBox.AddChild(cardParentBox);
            var targetStyle2 = new ComputedStyle(card);
            var targetBox2 = new TestBox { Element = card, Style = targetStyle2 };
            cardParentBox.AddChild(targetBox2);
            index.Add(outer, outerBox);
            index.Add(cardParent, cardParentBox);
            index.Add(card, targetBox2);

            var engine = new CascadeEngine(new[] {
                Author(@"
                    @container (min-width: 600px) {
                        @scope (.card) {
                            p { color: purple; }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(card).Get("color"), Is.EqualTo("purple"),
                "@container satisfied AND inside @scope — rule must apply");
        }

        [Test]
        public void Container_inside_scope_both_must_be_satisfied() {
            // @scope outer with @container inside
            var doc = Html("<div class='wrapper'><div id='outer'><span id='t'>x</span></div></div>");
            var outer = doc.GetElementById("outer");
            var target = doc.GetElementById("t");

            var index = new FakeBoxIndex();
            Element wrapper = null;
            foreach (var e in doc.GetElementsByClassName("wrapper")) { wrapper = e; break; }
            var wrapperStyle = new ComputedStyle(wrapper);
            var wrapperBox = new TestBox { Element = wrapper, Style = wrapperStyle };

            var outerStyle = new ComputedStyle(outer);
            outerStyle.Set("container-type", "inline-size");
            var outerBox = new TestBox { Element = outer, Style = outerStyle, Width = 800 };
            wrapperBox.AddChild(outerBox);

            var targetStyle = new ComputedStyle(target);
            var targetBox = new TestBox { Element = target, Style = targetStyle };
            outerBox.AddChild(targetBox);

            index.Add(wrapper, wrapperBox);
            index.Add(outer, outerBox);
            index.Add(target, targetBox);

            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.wrapper) {
                        @container (min-width: 600px) {
                            span { color: teal; }
                        }
                    }
                ")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(target).Get("color"), Is.EqualTo("teal"),
                "Inside @scope AND @container satisfied — rule must apply");
        }

        [Test]
        public void Single_scope_rule_still_works_after_chain_fix() {
            // Regression: non-nested @scope should not be broken.
            var doc = Html("<div class='card'><p id='t'>x</p></div><p id='out'>y</p>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { p { color: green; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("t")).Get("color"), Is.EqualTo("green"),
                "Single-level @scope must still work after the chain fix");
            Assert.That(engine.Compute(doc.GetElementById("out")).Get("color"), Is.Not.EqualTo("green"),
                "Element outside single-level @scope must not match");
        }
    }
}
