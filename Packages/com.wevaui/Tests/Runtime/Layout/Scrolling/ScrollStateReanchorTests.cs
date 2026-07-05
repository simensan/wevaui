using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // The final mechanism of the typing-scrolls-to-top hunt, pinned at the
    // unit level. Scroll states are keyed by BOX INSTANCE and boxes are
    // POOLED; the live state-table dumps showed the scrolled .page box being
    // recycled AND re-rented as an anonymous wrapper within the same rebuild:
    //
    //   box=#D7058050 elem=<anon.>    ScrollY=1360   <- old .page instance, re-rented
    //   box=#CD86B264 elem=<div.page> ScrollY=0      <- fresh .page box
    //
    // A liveness-only re-anchor skips that entry (the instance IS live), and
    // the generation-validated Get() refuses it — simultaneously too alive to
    // re-anchor and too stale to read. ScrollContainer.ReanchorOrphans must
    // treat generation mismatch as orphaned and resolve the rescue target by
    // the ELEMENT captured at link time, never by the stale box's current
    // Element.
    public class ScrollStateReanchorTests {
        static Box[] BuildChildBoxes(int n) {
            var sb = new System.Text.StringBuilder("<div>");
            for (int i = 0; i < n; i++) sb.Append("<div></div>");
            sb.Append("</div>");
            var (root, _, _) = Build(sb.ToString());
            var content = ContentRoot(root);
            var outer = content.Children[0];
            var boxes = new Box[n];
            for (int i = 0; i < n; i++) boxes[i] = outer.Children[i];
            return boxes;
        }

        [Test]
        public void Recycled_and_rerented_live_box_still_reanchors_to_element() {
            // THE live mechanism: A carries the scroll for element P, then A
            // is recycled (generation bump) and re-rented as a different box
            // that IS in the live set. The scroll must land on B, the live
            // box that now represents P.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(3);
            var a = boxes[0]; var b = boxes[1];
            var p = a.Element;
            Assert.That(p, Is.Not.Null);

            var st = sc.GetOrCreate(a);
            st.ScrollY = 600;
            Assert.That(st.OwnerElement, Is.SameAs(p), "link-time element capture");

            a.ResetForPool();                 // recycled: generation bumps
            a.Element = boxes[2].Element;     // re-rented as a DIFFERENT box
            var live = new HashSet<Box> { a, b };

            sc.ReanchorOrphans(live, el => ReferenceEquals(el, p) ? b : null);

            var rescued = sc.Get(b);
            Assert.That(rescued, Is.Not.Null, "the live box for P must have adopted the state");
            Assert.That(rescued.ScrollY, Is.EqualTo(600).Within(0.001));
            Assert.That(sc.Get(a), Is.Null, "the stale entry must be gone, not resurrected");
        }

        [Test]
        public void Not_live_orphan_reanchors_by_link_time_element() {
            // The classic replacement shape (box replaced, old instance not
            // live) must keep working through the same path.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            var a = boxes[0]; var b = boxes[1];
            var p = a.Element;

            sc.GetOrCreate(a).ScrollY = 240;
            var live = new HashSet<Box> { b };

            sc.ReanchorOrphans(live, el => ReferenceEquals(el, p) ? b : null);

            Assert.That(sc.Get(b), Is.Not.Null);
            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(240).Within(0.001));
        }

        [Test]
        public void Reanchor_does_not_clobber_target_with_its_own_scroll() {
            // If the live target already carries a meaningful offset (e.g. an
            // input write landed after the rebuild), the orphan must not
            // overwrite it.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            var a = boxes[0]; var b = boxes[1];
            var p = a.Element;

            sc.GetOrCreate(a).ScrollY = 600;
            sc.GetOrCreate(b).ScrollY = 42;
            var live = new HashSet<Box> { b };

            sc.ReanchorOrphans(live, el => ReferenceEquals(el, p) ? b : null);

            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(42).Within(0.001),
                "the target's own scroll wins over the rescued orphan");
            Assert.That(sc.Get(a), Is.Null);
        }

        [Test]
        public void Generation_mismatched_live_entry_is_removed_even_when_unscrolled() {
            // RetainOnly keeps live boxes and Get() refuses mismatched
            // generations, so without this sweep an unscrolled stale entry on
            // a re-rented live instance would pile up as unreadable dead
            // weight forever.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            var a = boxes[0];

            sc.GetOrCreate(a); // ScrollY stays 0
            a.ResetForPool();
            a.Element = boxes[1].Element;
            var live = new HashSet<Box> { a };

            sc.ReanchorOrphans(live, el => null);

            Assert.That(sc.Count, Is.EqualTo(0), "the dead entry must be swept");
        }

        [Test]
        public void Generation_only_sweep_rescues_without_a_live_set() {
            // The incremental subtree path has no live set (lastRoot survives
            // in place); ResetForPool bumps the generation whether or not the
            // instance is re-rented, so a generation-only sweep still catches
            // every recycled scroll container.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(3);
            var a = boxes[0]; var b = boxes[1];
            var p = a.Element;

            sc.GetOrCreate(a).ScrollY = 300;
            a.ResetForPool();
            a.Element = boxes[2].Element; // re-rented as a different box

            sc.ReanchorStaleGenerations(el => ReferenceEquals(el, p) ? b : null);

            Assert.That(sc.Get(b), Is.Not.Null);
            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(300).Within(0.001));
            Assert.That(sc.Get(a), Is.Null);
        }

        [Test]
        public void Generation_only_sweep_leaves_valid_entries_alone() {
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            var a = boxes[0]; var b = boxes[1];

            sc.GetOrCreate(a).ScrollY = 300;

            sc.ReanchorStaleGenerations(el => b);

            Assert.That(sc.Get(a), Is.Not.Null, "gen-valid entry must not be touched");
            Assert.That(sc.Get(a).ScrollY, Is.EqualTo(300).Within(0.001));
            Assert.That(sc.Get(b), Is.Null);
        }

        [Test]
        public void Anonymous_scroll_container_captures_nearest_ancestor_element_and_rescues() {
            // Anonymous boxes CAN be scroll containers (a wrapper carrying an
            // element's overflow style — the live tables showed an elementless
            // twin with exactly .page's metrics). Link-time capture must fall
            // back to the nearest ancestor element or the state is
            // permanently unrescuable when its box is replaced.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            var a = boxes[0]; var b = boxes[1];
            var parentEl = a.Parent?.Element;
            Assert.That(parentEl, Is.Not.Null, "harness sanity: parent carries an element");
            a.Element = null; // simulate the anonymous wrapper

            var st = sc.GetOrCreate(a);
            Assert.That(st.OwnerElement, Is.SameAs(parentEl),
                "anonymous container must capture the nearest ancestor element");
            st.ScrollY = 1480;

            var live = new HashSet<Box> { b };
            sc.ReanchorOrphans(live, el => ReferenceEquals(el, parentEl) ? b : null);

            Assert.That(sc.Get(b), Is.Not.Null);
            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(1480).Within(0.001));
            Assert.That(sc.Get(a), Is.Null);
        }

        [Test]
        public void Elementless_scrolled_orphan_is_left_for_the_root_transfer() {
            // The viewport root's state has no OwnerElement — its rescue is
            // the explicit lastRoot->survivor TransferScrollPosition in
            // LayoutEngine, which must run BEFORE EndPass bumps the old
            // root's generation. ReanchorOrphans must not eat a gen-VALID
            // not-live elementless entry (RetainOnly prunes it after the
            // root transfer had its chance).
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            var a = boxes[0]; var b = boxes[1];

            var st = sc.GetOrCreate(a);
            st.ScrollY = 500;
            st.OwnerElement = null; // simulate the anonymous viewport root
            var live = new HashSet<Box> { b };

            sc.ReanchorOrphans(live, el => b);

            Assert.That(sc.Get(a), Is.Not.Null,
                "gen-valid elementless orphan stays until the root transfer / RetainOnly");
            Assert.That(sc.Get(a).ScrollY, Is.EqualTo(500).Within(0.001));

            // And the root transfer itself, at a valid generation, carries it.
            sc.TransferScrollPosition(a, b);
            Assert.That(sc.Get(b), Is.Not.Null);
            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(500).Within(0.001));
        }
    }
}
