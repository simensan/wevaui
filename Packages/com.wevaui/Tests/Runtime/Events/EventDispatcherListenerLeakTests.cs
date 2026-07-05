using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    // MS1 regression suite. Before the fix, EventListeners.map kept a hard
    // reference to every Element that had ever been registered, even after
    // the element was removed from the DOM tree — every <button on-click>
    // (and every addEventListener-attached element) leaked its Element +
    // listener closures for the lifetime of the dispatcher. The fix wires
    // EventDispatcher to Document.Mutated and compacts the map on
    // ChildRemoved, walking the entire removed subtree.
    public class EventDispatcherListenerLeakTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        static EventDispatcher Build(Document doc) {
            return new EventDispatcher(doc, new FakeHitTester(), new FakeUIClock());
        }

        [Test]
        public void Removing_a_leaf_with_a_listener_compacts_the_map() {
            var doc = Html("<section id=\"root\"><div id=\"leaf\"></div></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");
            var d = Build(doc);

            d.AddEventListener(leaf, EventKind.Click, _ => { });
            Assert.That(d.ListenersForTests.ContainsKey(leaf), Is.True,
                "precondition: listener was registered against leaf");

            root.RemoveChild(leaf);

            Assert.That(d.ListenersForTests.ContainsKey(leaf), Is.False,
                "MS1: leaf entry must be evicted from EventListeners.map on DOM removal");
        }

        [Test]
        public void Removing_a_subtree_root_evicts_every_listener_bearing_descendant() {
            // section
            //   subtreeRoot          <-- removed
            //     mid (listener)
            //       leaf (listener)
            //     sibling (listener)
            var doc = Html(
                "<section id=\"root\">" +
                  "<div id=\"sub\">" +
                    "<div id=\"mid\">" +
                      "<span id=\"leaf\"></span>" +
                    "</div>" +
                    "<span id=\"sibling\"></span>" +
                  "</div>" +
                "</section>");
            var root = doc.GetElementById("root");
            var sub = doc.GetElementById("sub");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var sibling = doc.GetElementById("sibling");
            var d = Build(doc);

            d.AddEventListener(mid, EventKind.Click, _ => { });
            d.AddEventListener(leaf, EventKind.PointerDown, _ => { });
            d.AddEventListener(sibling, EventKind.Focus, _ => { });
            // Also a listener on a node OUTSIDE the removed subtree to prove
            // we only evict the subtree, not the whole document.
            d.AddEventListener(root, EventKind.Click, _ => { });

            Assert.That(d.ListenersForTests.Count, Is.EqualTo(4),
                "precondition: four listener-bearing elements registered");

            root.RemoveChild(sub);

            Assert.That(d.ListenersForTests.ContainsKey(mid), Is.False,
                "MS1 subtree: descendant `mid` must be evicted with its ancestor");
            Assert.That(d.ListenersForTests.ContainsKey(leaf), Is.False,
                "MS1 subtree: deep descendant `leaf` must be evicted with its ancestor");
            Assert.That(d.ListenersForTests.ContainsKey(sibling), Is.False,
                "MS1 subtree: sibling descendant `sibling` must be evicted with its ancestor");
            Assert.That(d.ListenersForTests.ContainsKey(root), Is.True,
                "elements OUTSIDE the removed subtree must keep their listeners");
        }

        [Test]
        public void Removing_an_element_with_no_listener_is_a_silent_no_op() {
            var doc = Html("<section id=\"root\"><div id=\"a\"></div><div id=\"b\"></div></section>");
            var root = doc.GetElementById("root");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = Build(doc);

            // Only `a` has a listener — `b` is registration-free.
            d.AddEventListener(a, EventKind.Click, _ => { });
            int before = d.ListenersForTests.Count;

            Assert.DoesNotThrow(() => root.RemoveChild(b),
                "MS1: removing a listener-free element must not throw");

            Assert.That(d.ListenersForTests.Count, Is.EqualTo(before),
                "the listener-bearing sibling's entry must be untouched");
            Assert.That(d.ListenersForTests.ContainsKey(a), Is.True);
        }

        [Test]
        public void Moving_an_element_within_the_same_parent_preserves_its_listeners() {
            var doc = Html("<section id=\"root\"><button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button></section>");
            var root = doc.GetElementById("root");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = Build(doc);

            d.AddEventListener(b, EventKind.Click, _ => { });
            Assert.That(d.ListenersForTests.ContainsKey(b), Is.True,
                "precondition: listener was registered before the move");

            root.InsertBefore(b, a);

            Assert.That(root.Children[0], Is.SameAs(b));
            Assert.That(d.ListenersForTests.ContainsKey(b), Is.True,
                "same-parent DOM moves must not look like detach/removal to the event dispatcher");
        }

        [Test]
        public void Disposing_dispatcher_and_creating_a_new_one_on_same_document_stays_clean() {
            var doc = Html("<section id=\"root\"><div id=\"leaf\"></div></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");

            // First dispatcher: register, then dispose.
            var first = Build(doc);
            first.AddEventListener(leaf, EventKind.Click, _ => { });
            first.Dispose();
            // Double-dispose must be a no-op (idempotent), matching the
            // FormControlsRegistry / BindingSet teardown contract.
            Assert.DoesNotThrow(() => first.Dispose());

            // After disposal the FIRST dispatcher must NOT compact on
            // subsequent mutations — its handler is unsubscribed, so the
            // map snapshot from before is preserved. We only assert this
            // indirectly by checking the new dispatcher's behaviour below.

            // Second dispatcher on the same document: re-subscribes cleanly,
            // sees the next removal, and compacts its own (fresh) map.
            var second = Build(doc);
            Assert.That(second.ListenersForTests.Count, Is.EqualTo(0),
                "a fresh dispatcher starts with an empty listener map");

            // Register against the still-attached leaf, then remove it.
            second.AddEventListener(leaf, EventKind.PointerDown, _ => { });
            Assert.That(second.ListenersForTests.ContainsKey(leaf), Is.True);

            Assert.DoesNotThrow(() => root.RemoveChild(leaf),
                "MS1: removing an element after dispatcher re-creation must not throw");
            Assert.That(second.ListenersForTests.ContainsKey(leaf), Is.False,
                "MS1: the re-created dispatcher must compact its own map on removal");

            second.Dispose();
        }
    }
}
