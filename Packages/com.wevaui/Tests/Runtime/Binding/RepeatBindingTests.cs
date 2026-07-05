using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Reactive;
using Weva.Tests.Events;

namespace Weva.Tests.Binding {
    public class RepeatBindingTests {
        sealed class Item {
            public int Id;
            public string Name;
            public bool Selected;
        }

        sealed class Ctx {
            public List<Item> Items = new List<Item>();
            public void OnClick(PointerEvent _) { }
        }

        static Document Html(string s) => HtmlParser.Parse(s);

        // HtmlParser wraps fragments in synthetic `<html><body>` per HTML5;
        // this helper returns the first Element matching `tag` anywhere in
        // the tree (descends through the synthetic wrappers).
        static Element First(Document doc, string tag) {
            return FindTag(doc, tag);
        }
        static Element FindTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Scanner_registers_repeat_template_without_scanning_its_body() {
            var doc = Html("<main><template data-each=\"Items as item\"><p>{{ item.Name }}</p></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            Assert.That(set.RepeatBindings.Count, Is.EqualTo(1));
            Assert.That(set.TextBindings.Count, Is.EqualTo(0));
        }

        [Test]
        public void Repeat_binding_clones_template_body_and_updates_item_scope() {
            var doc = Html("<main><template data-each=\"Items as item\"><p>{{ item.Name }}</p></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor" }
                }
            };

            set.Update(ctx);

            var main = First(doc, "main");
            Assert.That(main.Children.Count, Is.EqualTo(3));
            Assert.That(((TextNode)((Element)main.Children[0]).Children[0]).Data, Is.EqualTo("Forest"));
            Assert.That(((TextNode)((Element)main.Children[1]).Children[0]).Data, Is.EqualTo("Harbor"));
            Assert.That(((Element)main.Children[2]).TagName, Is.EqualTo("template"));
        }

        [Test]
        public void Repeat_binding_reuses_keyed_nodes_when_data_changes() {
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"Id\"><p>{{ item.Name }}</p></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor" }
                }
            };
            set.Update(ctx);
            var main = First(doc, "main");
            var first = main.Children[0];
            var second = main.Children[1];

            ctx.Items = new List<Item> {
                new Item { Id = 2, Name = "Harbor+" },
                new Item { Id = 1, Name = "Forest+" }
            };
            set.Update(ctx);

            Assert.That(main.Children[0], Is.SameAs(second));
            Assert.That(main.Children[1], Is.SameAs(first));
            Assert.That(((TextNode)((Element)main.Children[0]).Children[0]).Data, Is.EqualTo("Harbor+"));
            Assert.That(((TextNode)((Element)main.Children[1]).Children[0]).Data, Is.EqualTo("Forest+"));
        }

        [Test]
        public void Repeat_binding_reorder_preserves_event_listeners_on_moved_nodes() {
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"Id\"><button on-click=\"OnClick\">{{ item.Name }}</button></template></main>");
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor" }
                }
            };
            var set = BindingScanner.Scan(doc, ctx);
            var dispatcher = new EventDispatcher(doc, new FakeHitTester(), new FakeUIClock());
            set.Wire(dispatcher);
            set.Update(ctx);
            var main = First(doc, "main");
            var first = (Element)main.Children[0];
            var second = (Element)main.Children[1];
            Assert.That(dispatcher.ListenersForTests.ContainsKey(first), Is.True);
            Assert.That(dispatcher.ListenersForTests.ContainsKey(second), Is.True);

            ctx.Items = new List<Item> {
                new Item { Id = 2, Name = "Harbor+" },
                new Item { Id = 1, Name = "Forest+" }
            };
            set.Update(ctx);

            Assert.That(main.Children[0], Is.SameAs(second));
            Assert.That(main.Children[1], Is.SameAs(first));
            Assert.That(dispatcher.ListenersForTests.ContainsKey(first), Is.True,
                "moved repeat instance must keep its on-click listener");
            Assert.That(dispatcher.ListenersForTests.ContainsKey(second), Is.True,
                "moved repeat instance must keep its on-click listener");
        }

        [Test]
        public void Repeat_binding_exposes_index_and_parent_scope() {
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"$index\"><p>{{ $index }} {{ Prefix }} {{ item.Name }}</p></template></main>");
            var set = BindingScanner.Scan(doc, new { Prefix = "Stage", Items = new List<Item>() });
            var ctx = new {
                Prefix = "Stage",
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor" }
                }
            };

            set.Update(ctx);

            var main = First(doc, "main");
            Assert.That(((TextNode)((Element)main.Children[0]).Children[0]).Data, Is.EqualTo("0 Stage Forest"));
            Assert.That(((TextNode)((Element)main.Children[1]).Children[0]).Data, Is.EqualTo("1 Stage Harbor"));
        }

        [Test]
        public void Reorder_re_renders_dollar_index_on_moved_instances() {
            // Regression for the reused per-instance BindingScope: a keyed
            // reorder moves an instance to a new index, so its scope's cached
            // $index box must be invalidated and the text re-rendered.
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"Id\"><p>{{ $index }}:{{ item.Name }}</p></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor" }
                }
            };
            set.Update(ctx);
            var main = First(doc, "main");
            Assert.That(((TextNode)((Element)main.Children[0]).Children[0]).Data, Is.EqualTo("0:Forest"));
            Assert.That(((TextNode)((Element)main.Children[1]).Children[0]).Data, Is.EqualTo("1:Harbor"));

            ctx.Items = new List<Item> {
                new Item { Id = 2, Name = "Harbor" },
                new Item { Id = 1, Name = "Forest" }
            };
            set.Update(ctx);

            Assert.That(((TextNode)((Element)main.Children[0]).Children[0]).Data, Is.EqualTo("0:Harbor"));
            Assert.That(((TextNode)((Element)main.Children[1]).Children[0]).Data, Is.EqualTo("1:Forest"));
        }

        [Test]
        public void Items_from_non_ilist_enumerable_still_bind() {
            // ReadItems has an IList fast path; pin that plain IEnumerable
            // sources (yield-return) keep working through the fallback.
            var doc = Html("<main><template data-each=\"Items as item\"><p>{{ item }}</p></template></main>");
            var ctx = new EnumerableCtx();
            var set = BindingScanner.Scan(doc, ctx);
            set.Update(ctx);
            var main = First(doc, "main");
            Assert.That(main.Children.Count, Is.EqualTo(3));
            Assert.That(((TextNode)((Element)main.Children[0]).Children[0]).Data, Is.EqualTo("a"));
            Assert.That(((TextNode)((Element)main.Children[1]).Children[0]).Data, Is.EqualTo("b"));
        }

        sealed class EnumerableCtx {
            public IEnumerable<string> Items {
                get {
                    yield return "a";
                    yield return "b";
                }
            }
        }

        [Test]
        public void Repeat_binding_updates_scoped_class_binding() {
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"Id\"><button class=\"card\" data-class-selected=\"item.Selected\">{{ item.Name }}</button></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest", Selected = false },
                    new Item { Id = 2, Name = "Harbor", Selected = true }
                }
            };

            set.Update(ctx);

            var main = First(doc, "main");
            Assert.That(((Element)main.Children[0]).GetAttribute("class"), Is.EqualTo("card"));
            Assert.That(((Element)main.Children[1]).GetAttribute("class"), Is.EqualTo("card selected"));

            ctx.Items[0].Selected = true;
            ctx.Items[1].Selected = false;
            set.Update(ctx);

            Assert.That(((Element)main.Children[0]).GetAttribute("class"), Is.EqualTo("card selected"));
            Assert.That(((Element)main.Children[1]).GetAttribute("class"), Is.EqualTo("card"));
        }

        [Test]
        public void Repeat_binding_stable_update_does_not_reorder_or_dirty_dom() {
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"Id\"><button class=\"card\" data-class-selected=\"item.Selected\">{{ item.Name }}</button></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest", Selected = false },
                    new Item { Id = 2, Name = "Harbor", Selected = true }
                }
            };
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);

            set.Update(ctx, tracker);
            var main = First(doc, "main");
            var first = main.Children[0];
            var second = main.Children[1];
            long version = main.Version;
            tracker.Clear();

            bool changed = set.Update(ctx, tracker);

            Assert.That(changed, Is.False);
            Assert.That(main.Version, Is.EqualTo(version));
            Assert.That(main.Children[0], Is.SameAs(first));
            Assert.That(main.Children[1], Is.SameAs(second));
            Assert.That(tracker.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Repeat_binding_structure_change_marks_owner_structure_dirty() {
            var doc = Html("<main><template data-each=\"Items as item\" data-key=\"Id\"><p>{{ item.Name }}</p></template></main>");
            var set = BindingScanner.Scan(doc, new Ctx());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor" }
                }
            };

            set.Update(ctx, tracker);

            var main = First(doc, "main");
            Assert.That(tracker.IsDirty(main, InvalidationKind.Structure), Is.True);
            Assert.That(tracker.IsDirty(main, InvalidationKind.Layout), Is.True);
            Assert.That(tracker.IsDirty(main, InvalidationKind.Paint), Is.True);
        }

        [Test]
        public void Data_class_binding_toggles_one_class_without_replacing_static_classes() {
            var doc = Html("<button class=\"card\" data-class-selected=\"{{ Active }}\">x</button>");
            var set = BindingScanner.Scan(doc, new { Active = true });
            var button = First(doc, "button");

            set.Update(new { Active = true });
            Assert.That(button.GetAttribute("class"), Is.EqualTo("card selected"));

            set.Update(new { Active = false });
            Assert.That(button.GetAttribute("class"), Is.EqualTo("card"));
        }

        [Test]
        public void Class_binding_marks_style_dirty() {
            var doc = Html("<button class=\"card\" data-class-selected=\"Active\">x</button>");
            var set = BindingScanner.Scan(doc, new { Active = true });
            var button = First(doc, "button");
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();

            set.Update(new { Active = true }, tracker);

            Assert.That(tracker.IsDirty(button, InvalidationKind.Style), Is.True);
        }
    }
}
