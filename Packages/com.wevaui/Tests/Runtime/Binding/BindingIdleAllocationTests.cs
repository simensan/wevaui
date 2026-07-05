using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Binding.Generated;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Binding {
    // End-to-end pin for the idle-frame binding poll: a BindingSet covering
    // repeat + text + class + attribute bindings must allocate NOTHING when
    // the controller's data hasn't changed. Contexts implement
    // IBindingAccessor with cached boxes, mirroring the [UIBind] generated
    // fast path (the reflection fallback boxes value types and is exempt
    // from the zero-alloc guarantee).
    public class BindingIdleAllocationTests {
        sealed class Item : IBindingAccessor {
            public int Id;
            public string Name;
            public bool Selected;
            object idBoxed;
            object selectedBoxed;

            static readonly string[] members = { "Id", "Name", "Selected" };
            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => members;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings =>
                Array.Empty<ElementBindingDescriptor>();

            bool IBindingAccessor.TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "Id": value = idBoxed ?? (idBoxed = Id); return true;
                    case "Name": value = Name; return true;
                    case "Selected": value = selectedBoxed ?? (selectedBoxed = Selected); return true;
                    default: value = null; return false;
                }
            }

            bool IBindingAccessor.TrySet(string memberName, object value) => false;
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        sealed class Ctx : IBindingAccessor {
            public List<Item> Items = new();
            public string Title = "Shop";

            static readonly string[] members = { "Items", "Title" };
            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => members;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings =>
                Array.Empty<ElementBindingDescriptor>();

            bool IBindingAccessor.TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "Items": value = Items; return true;
                    case "Title": value = Title; return true;
                    default: value = null; return false;
                }
            }

            bool IBindingAccessor.TrySet(string memberName, object value) => false;
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        static (Document doc, BindingSet set, Ctx ctx) BuildScene() {
            var doc = HtmlParser.Parse(
                "<main>" +
                "<h1 title=\"{{ Title }}\">{{ Title }}</h1>" +
                "<template data-each=\"Items as item\" data-key=\"item.Id\">" +
                "<p data-class-selected=\"item.Selected\" title=\"row {{ $index }}\">{{ item.Name }} #{{ item.Id }}</p>" +
                "</template>" +
                "</main>");
            var ctx = new Ctx {
                Items = new List<Item> {
                    new Item { Id = 1, Name = "Forest" },
                    new Item { Id = 2, Name = "Harbor", Selected = true },
                    new Item { Id = 3, Name = "Summit" }
                }
            };
            var set = BindingScanner.Scan(doc, ctx);
            return (doc, set, ctx);
        }

        [Test]
        public void Idle_updates_return_false_and_leave_dom_untouched() {
            var (doc, set, ctx) = BuildScene();
            Assert.That(set.Update(ctx), Is.True, "first update populates the DOM");

            var main = FindTag(doc, "main");
            long version = main.Version;
            for (int i = 0; i < 10; i++) {
                Assert.That(set.Update(ctx), Is.False, $"idle update #{i} reported a change");
            }
            Assert.That(main.Version, Is.EqualTo(version), "idle updates must not mutate the DOM");
        }

#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [Test]
        public void Idle_updates_allocate_nothing() {
            var (_, set, ctx) = BuildScene();
            set.Update(ctx);

            // Warm thread-static render buffers and resolver caches.
            for (int i = 0; i < 10; i++) set.Update(ctx);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) {
                set.Update(ctx);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(delta, Is.EqualTo(0),
                $"500 idle BindingSet.Update calls allocated {delta} bytes; expected 0");
        }
#endif

        static Element FindTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }
    }
}
