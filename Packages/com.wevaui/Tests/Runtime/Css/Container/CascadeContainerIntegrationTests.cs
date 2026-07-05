using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Container;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Css.Container {
    public class CascadeContainerIntegrationTests {
        sealed class TestBox : BlockBox { }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // Given a Document and a per-element { containerType, containerName, width, height }
        // map, builds a flat box index that mirrors the DOM hierarchy and registers it on
        // the cascade engine. Boxes only need Style + Width/Height + parent linkage for
        // ContainerResolver to walk; layout doesn't actually run in these tests.
        sealed class FakeBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public Box Lookup(Element e) => e != null && map.TryGetValue(e, out var b) ? b : null;
            public void Add(Element e, Box b) { map[e] = b; }
            public System.Func<Element, Box> AsFunc => Lookup;
        }

        static FakeBoxIndex BuildBoxes(Document doc, Dictionary<Element, (string type, string name, double w, double h)> info) {
            var index = new FakeBoxIndex();
            Box rootBox = null;
            BuildRecursive(null, doc, info, index, ref rootBox);
            return index;
        }

        static void BuildRecursive(Box parent, Node node, Dictionary<Element, (string type, string name, double w, double h)> info, FakeBoxIndex index, ref Box outRoot) {
            if (node is Element e) {
                var style = new ComputedStyle(e);
                if (info.TryGetValue(e, out var i)) {
                    if (i.type != null) style.Set("container-type", i.type);
                    if (i.name != null) style.Set("container-name", i.name);
                    var box = new TestBox { Element = e, Style = style, Width = i.w, Height = i.h };
                    if (parent != null) parent.AddChild(box); else outRoot ??= box;
                    index.Add(e, box);
                    foreach (var c in e.Children) BuildRecursive(box, c, info, index, ref outRoot);
                } else {
                    var box = new TestBox { Element = e, Style = style };
                    if (parent != null) parent.AddChild(box); else outRoot ??= box;
                    index.Add(e, box);
                    foreach (var c in e.Children) BuildRecursive(box, c, info, index, ref outRoot);
                }
                return;
            }
            foreach (var c in node.Children) BuildRecursive(parent, c, info, index, ref outRoot);
        }

        [Test]
        public void Inline_size_container_with_min_width_applies_when_wide() {
            var doc = Html("<div id=\"p\"><span id=\"x\">y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { p, ("inline-size", null, 800, 600) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            var cs = engine.Compute(x);
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Inline_size_container_with_min_width_does_not_apply_when_narrow() {
            var doc = Html("<div id=\"p\"><span id=\"x\">y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { p, ("inline-size", null, 300, 600) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            var cs = engine.Compute(x);
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Named_query_finds_named_ancestor() {
            var doc = Html("<div id=\"outer\"><div id=\"card\"><span id=\"x\">y</span></div></div>");
            var outer = doc.GetElementById("outer");
            var card = doc.GetElementById("card");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { outer, ("inline-size", "outer", 1200, 800) },
                { card, ("inline-size", "card", 400, 300) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container card (min-width: 300px) { #x { color: green; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            // 'card' is 400px wide -> matches min-width: 300px.
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Nested_containers_resolve_to_nearest_unnamed_ancestor() {
            var doc = Html("<div id=\"outer\"><div id=\"inner\"><span id=\"x\">y</span></div></div>");
            var outer = doc.GetElementById("outer");
            var inner = doc.GetElementById("inner");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { outer, ("inline-size", null, 1200, 800) },
                { inner, ("inline-size", null, 200, 100) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 1000px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            // Nearest container is `inner` at 200px wide; the `outer` 1200px is not seen.
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Element_with_no_container_ancestor_never_matches() {
            var doc = Html("<div id=\"p\"><span id=\"x\">y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { p, (null, null, 800, 600) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 0px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void No_box_lookup_means_container_rules_never_apply() {
            // Documents the v1 chicken-and-egg: until layout has produced a box index and
            // wired it to ElementToBoxLookup, every @container rule is a no-op. This is
            // the reason a fresh container-query addition takes 1-2 frames to settle.
            var doc = Html("<div id=\"p\"><span id=\"x\">y</span></div>");
            var x = doc.GetElementById("x");

            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 0px) { #x { color: red; } }")
            });
            // No ElementToBoxLookup wired.
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Container_rule_combines_with_default_style_rule() {
            var doc = Html("<div id=\"p\"><span id=\"x\">y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { p, ("inline-size", null, 800, 600) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; font-size: 12px; }" +
                    "@container (min-width: 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            var cs = engine.Compute(x);
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("12px"));
        }

        [Test]
        public void Container_query_uses_named_container_size() {
            // Audit regression: `@container <name> (...)` MUST resolve against the
            // NAMED ancestor's size, not the nearest ancestor. Outer is wide enough
            // to satisfy the unnamed query but the named "card" is narrow enough to
            // satisfy a max-width condition; only the named lookup picks up "card".
            var doc = Html("<div id=\"outer\"><div id=\"card\"><span id=\"x\">y</span></div></div>");
            var outer = doc.GetElementById("outer");
            var card = doc.GetElementById("card");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { outer, ("inline-size", "outer", 1200, 800) },
                { card, ("inline-size", "card", 200, 300) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container card (max-width: 250px) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            // 'card' is 200px wide -> matches max-width: 250px even though the nearest
            // unnamed ancestor 'outer' is 1200px and would not match.
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Resizing_container_flips_rule_after_invalidation() {
            // Models the v1 layout-after-previous-cascade settle: change the box's width,
            // call InvalidateContainerResolutions + InvalidateAll, recompute. This is
            // exactly what UIDocumentLifecycle does between frames when Layout fires.
            var doc = Html("<div id=\"p\"><span id=\"x\">y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var info = new Dictionary<Element, (string, string, double, double)> {
                { p, ("inline-size", null, 300, 600) },
                { x, (null, null, 0, 0) }
            };
            var index = BuildBoxes(doc, info);

            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("black"));

            // Simulate the parent box's layout pass producing a new width.
            var parentBox = (TestBox)index.Lookup(p);
            parentBox.Width = 800;
            engine.InvalidateContainerResolutions();
            engine.InvalidateAll();
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("red"));
        }
    }
}
