using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Container;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.Tests.Css.Container {
    public class ContainerResolverTests {
        sealed class TestBox : BlockBox { }

        static TestBox MakeBox(Element e, ComputedStyle style, double w, double h) {
            var box = new TestBox { Element = e, Style = style, Width = w, Height = h };
            return box;
        }

        static ComputedStyle Style(Element e, params (string, string)[] props) {
            var s = new ComputedStyle(e);
            foreach (var (k, v) in props) s.Set(k, v);
            return s;
        }

        static System.Func<Element, Box> Lookup(Dictionary<Element, Box> map) {
            return e => e != null && map.TryGetValue(e, out var b) ? b : null;
        }

        [Test]
        public void Walks_up_to_nearest_inline_size_ancestor() {
            var grand = new Element("div");
            var parent = new Element("div");
            var child = new Element("div");
            var grandStyle = Style(grand, ("container-type", "inline-size"));
            var parentStyle = Style(parent);
            var childStyle = Style(child);

            var grandBox = MakeBox(grand, grandStyle, 800, 600);
            var parentBox = MakeBox(parent, parentStyle, 400, 300);
            var childBox = MakeBox(child, childStyle, 100, 50);
            grandBox.AddChild(parentBox);
            parentBox.AddChild(childBox);

            var map = new Dictionary<Element, Box> { { grand, grandBox }, { parent, parentBox }, { child, childBox } };

            var ctx = ContainerResolver.Resolve(child, null, Lookup(map));
            Assert.That(ctx.Type, Is.EqualTo(ContainerType.InlineSize));
            Assert.That(ctx.InlineSizePx, Is.EqualTo(800));
        }

        [Test]
        public void Skips_ancestors_with_normal_container_type() {
            var grand = new Element("div");
            var parent = new Element("div");
            var child = new Element("div");
            var grandBox = MakeBox(grand, Style(grand, ("container-type", "inline-size")), 800, 600);
            var parentBox = MakeBox(parent, Style(parent), 400, 300);
            var childBox = MakeBox(child, Style(child), 100, 50);
            grandBox.AddChild(parentBox);
            parentBox.AddChild(childBox);
            var map = new Dictionary<Element, Box> { { grand, grandBox }, { parent, parentBox }, { child, childBox } };

            var ctx = ContainerResolver.Resolve(child, null, Lookup(map));
            // Skipped over `parent` (no container-type) to reach `grand`.
            Assert.That(ctx.InlineSizePx, Is.EqualTo(800));
        }

        [Test]
        public void Self_is_not_its_own_container() {
            var self = new Element("div");
            var selfBox = MakeBox(self, Style(self, ("container-type", "inline-size")), 500, 400);
            var map = new Dictionary<Element, Box> { { self, selfBox } };

            // No ancestor box at all.
            var ctx = ContainerResolver.Resolve(self, null, Lookup(map));
            Assert.That(ctx.Type, Is.EqualTo(ContainerType.None));
        }

        [Test]
        public void Matches_by_name_when_specified() {
            var grand = new Element("div");
            var parent = new Element("div");
            var child = new Element("div");
            var grandBox = MakeBox(grand,
                Style(grand, ("container-type", "inline-size"), ("container-name", "outer")), 1000, 800);
            var parentBox = MakeBox(parent,
                Style(parent, ("container-type", "inline-size"), ("container-name", "card")), 500, 400);
            var childBox = MakeBox(child, Style(child), 100, 50);
            grandBox.AddChild(parentBox);
            parentBox.AddChild(childBox);
            var map = new Dictionary<Element, Box> { { grand, grandBox }, { parent, parentBox }, { child, childBox } };

            var ctxByCard = ContainerResolver.Resolve(child, "card", Lookup(map));
            Assert.That(ctxByCard.InlineSizePx, Is.EqualTo(500));
            Assert.That(ctxByCard.Name, Is.EqualTo("card"));

            var ctxByOuter = ContainerResolver.Resolve(child, "outer", Lookup(map));
            Assert.That(ctxByOuter.InlineSizePx, Is.EqualTo(1000));
            Assert.That(ctxByOuter.Name, Is.EqualTo("outer"));
        }

        [Test]
        public void Returns_none_when_named_ancestor_not_present() {
            var parent = new Element("div");
            var child = new Element("div");
            var parentBox = MakeBox(parent,
                Style(parent, ("container-type", "inline-size"), ("container-name", "card")), 500, 400);
            var childBox = MakeBox(child, Style(child), 100, 50);
            parentBox.AddChild(childBox);
            var map = new Dictionary<Element, Box> { { parent, parentBox }, { child, childBox } };

            var ctx = ContainerResolver.Resolve(child, "missing", Lookup(map));
            Assert.That(ctx.Type, Is.EqualTo(ContainerType.None));
        }

        [Test]
        public void Returns_empty_when_no_ancestor_has_container_type() {
            var parent = new Element("div");
            var child = new Element("div");
            var parentBox = MakeBox(parent, Style(parent), 500, 400);
            var childBox = MakeBox(child, Style(child), 100, 50);
            parentBox.AddChild(childBox);
            var map = new Dictionary<Element, Box> { { parent, parentBox }, { child, childBox } };

            var ctx = ContainerResolver.Resolve(child, null, Lookup(map));
            Assert.That(ctx.Type, Is.EqualTo(ContainerType.None));
            Assert.That(ctx.IsEmpty, Is.True);
        }

        [Test]
        public void Size_container_exposes_block_size() {
            var parent = new Element("div");
            var child = new Element("div");
            var parentBox = MakeBox(parent, Style(parent, ("container-type", "size")), 600, 400);
            var childBox = MakeBox(child, Style(child), 100, 50);
            parentBox.AddChild(childBox);
            var map = new Dictionary<Element, Box> { { parent, parentBox }, { child, childBox } };

            var ctx = ContainerResolver.Resolve(child, null, Lookup(map));
            Assert.That(ctx.Type, Is.EqualTo(ContainerType.Size));
            Assert.That(ctx.InlineSizePx, Is.EqualTo(600));
            Assert.That(ctx.BlockSizePx, Is.EqualTo(400));
        }

        [Test]
        public void Container_name_with_multiple_names_matches_any() {
            var parent = new Element("div");
            var child = new Element("div");
            var parentBox = MakeBox(parent,
                Style(parent, ("container-type", "inline-size"), ("container-name", "card primary")), 500, 400);
            var childBox = MakeBox(child, Style(child), 100, 50);
            parentBox.AddChild(childBox);
            var map = new Dictionary<Element, Box> { { parent, parentBox }, { child, childBox } };

            Assert.That(ContainerResolver.Resolve(child, "card", Lookup(map)).InlineSizePx, Is.EqualTo(500));
            Assert.That(ContainerResolver.Resolve(child, "primary", Lookup(map)).InlineSizePx, Is.EqualTo(500));
            Assert.That(ContainerResolver.Resolve(child, "missing", Lookup(map)).Type, Is.EqualTo(ContainerType.None));
        }
    }
}
