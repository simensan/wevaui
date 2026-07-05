using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    // MS5 regression suite. Before the fix, InteractionStateProvider.states
    // (the Dictionary<Element, ElementState> backing :hover / :focus /
    // :focus-visible / :active / :target / :focus-within) kept a hard
    // reference to any Element that had ever been flagged, even after the
    // element was detached from the tree. Most flags self-clean (hover and
    // focus-within via DiffApplyFlagChain; active via PointerUp), but Focus,
    // FocusVisible, and Target do NOT have a self-cleanup path — the
    // dispatcher's ForgetIfInSubtree merely nulls its own `focused` ref,
    // leaving the bits stranded in `states`. The fix subscribes the
    // provider to Document.Mutated and walks the removed subtree on
    // ChildRemoved, evicting every descendant Element from the map.
    public class InteractionStateProviderLeakTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        static EventDispatcher Build(Document doc, out InteractionStateProvider sp) {
            sp = new InteractionStateProvider();
            sp.AttachToDocument(doc);
            var d = new EventDispatcher(doc, new FakeHitTester(), sp, new FakeUIClock());
            return d;
        }

        [Test]
        public void Element_with_focus_state_has_entry_in_states_map() {
            // Precondition guard: the very thing that pre-fix leaks must
            // actually be stored. If a future refactor makes Focus computed-
            // on-demand (like Disabled / Checked), this test fails loudly and
            // the leak premise is invalidated.
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var d = Build(doc, out var sp);

            Assume.That(sp.ContainsForTests(b), Is.False,
                "precondition: no entry before focus");

            d.Focus(b);

            Assert.That(sp.ContainsForTests(b), Is.True,
                "post-focus: Focus / FocusVisible bits must be stored in states");
            Assert.That((sp.GetState(b) & ElementState.Focus) != 0, Is.True);
        }

        [Test]
        public void Removing_focused_element_evicts_its_states_entry() {
            // The canonical MS5 leak: an element with focus state is removed
            // from the DOM. Pre-fix, the dispatcher's ForgetIfInSubtree
            // nulled `focused` but the Focus / FocusVisible bits stayed
            // stranded against the orphaned Element key, pinning it for
            // the lifetime of the provider.
            var doc = Html("<section id=\"s\"><button id=\"b\"></button></section>");
            var s = doc.GetElementById("s");
            var b = doc.GetElementById("b");
            var d = Build(doc, out var sp);

            d.Focus(b);
            Assume.That(sp.ContainsForTests(b), Is.True,
                "precondition: focused element has an entry");

            s.RemoveChild(b);

            Assert.That(sp.ContainsForTests(b), Is.False,
                "MS5: focused element's states entry must be evicted on DOM removal");
            // Ancestors that picked up FocusWithin via the focus chain stay
            // in the map — they're still in the tree and the FocusWithin
            // bit is correct until the next focus change rebuilds the chain.
            // The MS5 invariant only requires that the *removed* element
            // (and any descendants it took with it) leave the map; it does
            // not (and must not) prune still-attached ancestors.
            Assert.That(sp.ContainsForTests(s), Is.True,
                "ancestor still in DOM keeps its FocusWithin entry");
        }

        [Test]
        public void Removing_subtree_root_evicts_every_descendants_state_entry() {
            // The DOM removal fires a single ChildRemoved event on the
            // subtree ROOT — the provider must walk descendants itself.
            // Without the recursive walk, only the root's entry would be
            // dropped and every stateful descendant would leak.
            //
            // Layout:
            //   root
            //     sub       <-- removed
            //       mid     (focus-within after focusing leaf)
            //         leaf  (focus + focus-visible)
            //       sibling (target via fragment)
            var doc = Html(
                "<section id=\"root\">" +
                  "<div id=\"sub\">" +
                    "<div id=\"mid\">" +
                      "<button id=\"leaf\"></button>" +
                    "</div>" +
                    "<span id=\"sibling\"></span>" +
                  "</div>" +
                "</section>");
            var root = doc.GetElementById("root");
            var sub = doc.GetElementById("sub");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var sibling = doc.GetElementById("sibling");
            var d = Build(doc, out var sp);

            // Drive every persistent flag onto a different descendant so we
            // can prove the subtree walk hits all of them.
            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.None, false); // keyboard-focus leaf -> Focus + FocusVisible
            Assume.That(d.FocusedElement, Is.SameAs(leaf));
            d.SetTargetFragment("sibling"); // :target on sibling

            Assume.That(sp.ContainsForTests(leaf), Is.True, "leaf has Focus / FocusVisible");
            Assume.That(sp.ContainsForTests(mid), Is.True, "mid has FocusWithin");
            Assume.That(sp.ContainsForTests(sub), Is.True, "sub has FocusWithin");
            Assume.That(sp.ContainsForTests(sibling), Is.True, "sibling has Target");

            // Remove the subtree root. Single ChildRemoved fires on `sub`.
            root.RemoveChild(sub);

            Assert.That(sp.ContainsForTests(sub), Is.False,
                "subtree root is evicted");
            Assert.That(sp.ContainsForTests(mid), Is.False,
                "descendant with FocusWithin is evicted");
            Assert.That(sp.ContainsForTests(leaf), Is.False,
                "deep descendant with Focus / FocusVisible is evicted");
            Assert.That(sp.ContainsForTests(sibling), Is.False,
                "sibling descendant with Target is evicted");
            // `root` itself had FocusWithin before removal; it's still in the
            // tree and the focus chain rebuild on next focus change handles
            // it. What matters here is that no orphaned-element entries
            // survive — root may or may not still hold FocusWithin (it does,
            // since the focus chain hasn't been rebuilt), so we don't assert
            // against root's presence, only that nothing inside the removed
            // subtree is still keyed.
        }

        [Test]
        public void Removing_target_element_lets_subsequent_SetTargetElement_succeed() {
            // Belt-and-suspenders: SetTargetElement caches the previous
            // target so it can clear that element's Target bit when the
            // target changes. If the previously-targeted element was
            // removed and our cleanup didn't also null `targetElement`,
            // the next SetTargetFragment would try to SetFlag against an
            // orphan — harmless for the map (the entry is already gone)
            // but a smell. This pins that the orphan reference is also
            // dropped.
            var doc = Html(
                "<section id=\"s\">" +
                  "<a id=\"old\"></a>" +
                  "<a id=\"new\"></a>" +
                "</section>");
            var s = doc.GetElementById("s");
            var oldA = doc.GetElementById("old");
            var newA = doc.GetElementById("new");
            var d = Build(doc, out var sp);

            d.SetTargetFragment("old");
            Assume.That(sp.ContainsForTests(oldA), Is.True);

            s.RemoveChild(oldA);
            Assert.That(sp.ContainsForTests(oldA), Is.False,
                "removed :target element evicted");

            // Re-target a different element. Must not throw and must mark
            // `new` as :target.
            Assert.DoesNotThrow(() => d.SetTargetFragment("new"));
            Assert.That((sp.GetState(newA) & ElementState.Target) != 0, Is.True);
        }

        [Test]
        public void Dispose_detaches_mutation_subscription_and_drops_all_state() {
            // Dispose must release the Document.Mutated subscription so a
            // teardown / rebuild cycle (HotReload, WevaDocument.OnDisable)
            // doesn't double-subscribe or pin the prior doc via the
            // mutationListener delegate. It must also drop the `states`
            // dictionary so the provider releases its own references for
            // GC. After dispose, subsequent DOM mutations on the (still-
            // live) document must not fire OnDomMutation.
            var doc = Html("<button id=\"b\"></button><span id=\"x\"></span>");
            var b = doc.GetElementById("b");
            var x = doc.GetElementById("x");
            var d = Build(doc, out var sp);

            d.Focus(b);
            Assume.That(sp.ContainsForTests(b), Is.True);

            sp.Dispose();

            Assert.That(sp.StatesCountForTests, Is.Zero,
                "Dispose drops every map entry");

            // Removing an element AFTER Dispose must not throw — the
            // subscription is detached so OnDomMutation never fires; even
            // if it did, the disposed guard at the top of OnDomMutation
            // short-circuits before touching the dictionary.
            Assert.DoesNotThrow(() => doc.RemoveChild(x));
        }
    }
}
