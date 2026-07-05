using NUnit.Framework;
using Weva.Dom;
using Weva.HotReload;

namespace Weva.Tests.HotReload {
    // Direct unit tests for DomDiffer's identity-preservation, attribute-diff,
    // child-reorder and text-mutation invariants documented in
    // Packages/com.wevaui/Runtime/HotReload/DomDiffer.cs file header.
    //
    // HotReloadCoordinatorTests covers the queue end-to-end; these tests pin
    // the differ in isolation against hand-built Document trees so an
    // invariant regression surfaces without bringing the full UIDocumentBuilder
    // into the loop.
    public class DomDifferDirectTests {

        // --- helpers -----------------------------------------------------

        static Document BuildDoc(params Node[] roots) {
            var d = new Document();
            foreach (var r in roots) d.AppendChild(r);
            return d;
        }

        static Element El(string tag, params (string name, string value)[] attrs) {
            var e = new Element(tag);
            foreach (var (n, v) in attrs) e.SetAttribute(n, v);
            return e;
        }

        static Element ElWithChildren(string tag, params Node[] children) {
            var e = new Element(tag);
            foreach (var c in children) e.AppendChild(c);
            return e;
        }

        static Element ElIdWithChildren(string tag, string id, params Node[] children) {
            var e = new Element(tag);
            e.SetAttribute("id", id);
            foreach (var c in children) e.AppendChild(c);
            return e;
        }

        // --- identity preservation --------------------------------------

        [Test]
        public void Same_id_preserves_existing_element_instance() {
            var liveDiv = El("div", ("id", "x"));
            var live = BuildDoc(liveDiv);

            var fresh = BuildDoc(El("div", ("id", "x")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children.Count, Is.EqualTo(1));
            Assert.That(live.Children[0], Is.SameAs(liveDiv),
                "live <div id=x> identity must survive a no-op diff");
        }

        [Test]
        public void Same_data_key_preserves_existing_element_instance() {
            var liveRow = El("li", ("data-key", "row-42"));
            var live = BuildDoc(liveRow);

            var fresh = BuildDoc(El("li", ("data-key", "row-42")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children.Count, Is.EqualTo(1));
            Assert.That(live.Children[0], Is.SameAs(liveRow),
                "data-key match must preserve the live Element instance");
        }

        [Test]
        public void Same_tag_at_same_position_without_key_preserves_identity() {
            var liveDiv = El("div");
            var live = BuildDoc(liveDiv);

            var fresh = BuildDoc(El("div"));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children.Count, Is.EqualTo(1));
            Assert.That(live.Children[0], Is.SameAs(liveDiv),
                "tag-only positional match must preserve the live Element");
        }

        [Test]
        public void Tag_change_at_same_position_creates_new_element() {
            var liveSpan = El("span");
            var live = BuildDoc(liveSpan);

            var freshDiv = El("div");
            var fresh = BuildDoc(freshDiv);

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children.Count, Is.EqualTo(1));
            Assert.That(live.Children[0], Is.Not.SameAs(liveSpan),
                "tag change must replace the Element, not reuse the live one");
            Assert.That(live.Children[0], Is.SameAs(freshDiv),
                "the fresh element should now be attached to live");
            Assert.That(liveSpan.Parent, Is.Null,
                "the displaced live <span> must be detached");
        }

        // --- attribute diff ---------------------------------------------

        [Test]
        public void Attribute_value_change_mutates_attribute_on_preserved_element() {
            var liveDiv = El("div", ("id", "x"), ("class", "a"));
            var live = BuildDoc(liveDiv);

            var fresh = BuildDoc(El("div", ("id", "x"), ("class", "b")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children[0], Is.SameAs(liveDiv),
                "preserved-element invariant should hold across attr change");
            Assert.That(liveDiv.GetAttribute("class"), Is.EqualTo("b"),
                "class attribute should be updated to fresh value");
        }

        [Test]
        public void Removed_attribute_is_dropped_from_preserved_element() {
            var liveDiv = El("div", ("id", "x"), ("class", "a"));
            var live = BuildDoc(liveDiv);

            var fresh = BuildDoc(El("div", ("id", "x")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children[0], Is.SameAs(liveDiv));
            Assert.That(liveDiv.HasAttribute("class"), Is.False,
                "class attribute should be removed when fresh side has none");
        }

        [Test]
        public void Added_attribute_is_set_on_preserved_element() {
            var liveDiv = El("div", ("id", "x"));
            var live = BuildDoc(liveDiv);

            var fresh = BuildDoc(El("div", ("id", "x"), ("data-flag", "on")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children[0], Is.SameAs(liveDiv));
            Assert.That(liveDiv.GetAttribute("data-flag"), Is.EqualTo("on"),
                "data-flag should be added from fresh side");
        }

        // --- child reorder ----------------------------------------------

        [Test]
        public void Reordered_keyed_children_preserve_identities_via_data_key() {
            var rowA = El("li", ("data-key", "a"));
            var rowB = El("li", ("data-key", "b"));
            var rowC = El("li", ("data-key", "c"));
            var liveList = ElWithChildren("ul", rowA, rowB, rowC);
            var live = BuildDoc(liveList);

            // Fresh: order C, A, B
            var fresh = BuildDoc(ElWithChildren("ul",
                El("li", ("data-key", "c")),
                El("li", ("data-key", "a")),
                El("li", ("data-key", "b"))));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(liveList.Children.Count, Is.EqualTo(3));
            Assert.That(liveList.Children[0], Is.SameAs(rowC),
                "keyed reorder must place rowC first");
            Assert.That(liveList.Children[1], Is.SameAs(rowA),
                "keyed reorder must place rowA second");
            Assert.That(liveList.Children[2], Is.SameAs(rowB),
                "keyed reorder must place rowB third");
        }

        [Test]
        public void Inserted_sibling_creates_new_element_and_preserves_surrounding() {
            var rowA = El("li", ("data-key", "a"));
            var rowB = El("li", ("data-key", "b"));
            var liveList = ElWithChildren("ul", rowA, rowB);
            var live = BuildDoc(liveList);

            // Fresh: insert new middle row "m" between a and b.
            var fresh = BuildDoc(ElWithChildren("ul",
                El("li", ("data-key", "a")),
                El("li", ("data-key", "m")),
                El("li", ("data-key", "b"))));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(liveList.Children.Count, Is.EqualTo(3));
            Assert.That(liveList.Children[0], Is.SameAs(rowA),
                "rowA identity must survive insertion");
            Assert.That(liveList.Children[2], Is.SameAs(rowB),
                "rowB identity must survive insertion");
            var middle = liveList.Children[1] as Element;
            Assert.That(middle, Is.Not.Null);
            Assert.That(middle.GetAttribute("data-key"), Is.EqualTo("m"),
                "inserted sibling should carry the new data-key");
            Assert.That(middle, Is.Not.SameAs(rowA));
            Assert.That(middle, Is.Not.SameAs(rowB));
        }

        [Test]
        public void Removed_sibling_leaves_remaining_identities_intact() {
            var rowA = El("li", ("data-key", "a"));
            var rowB = El("li", ("data-key", "b"));
            var rowC = El("li", ("data-key", "c"));
            var liveList = ElWithChildren("ul", rowA, rowB, rowC);
            var live = BuildDoc(liveList);

            // Fresh: drop rowB.
            var fresh = BuildDoc(ElWithChildren("ul",
                El("li", ("data-key", "a")),
                El("li", ("data-key", "c"))));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(liveList.Children.Count, Is.EqualTo(2));
            Assert.That(liveList.Children[0], Is.SameAs(rowA),
                "rowA must survive sibling removal");
            Assert.That(liveList.Children[1], Is.SameAs(rowC),
                "rowC must survive sibling removal");
            Assert.That(rowB.Parent, Is.Null,
                "removed rowB must be detached from its previous parent");
        }

        // --- text-node mutation -----------------------------------------

        [Test]
        public void Text_node_data_change_mutates_in_place_preserving_instance() {
            var liveText = new TextNode("hi");
            var liveP = ElWithChildren("p", liveText);
            var live = BuildDoc(liveP);

            var fresh = BuildDoc(ElWithChildren("p", new TextNode("bye")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(liveP.Children.Count, Is.EqualTo(1));
            Assert.That(liveP.Children[0], Is.SameAs(liveText),
                "TextNode identity must survive a Data-only mutation");
            Assert.That(liveText.Data, Is.EqualTo("bye"),
                "Data must be updated to the fresh value");
        }

        [Test]
        public void Element_with_only_text_child_survives_text_only_changes() {
            var liveText = new TextNode("v1");
            var liveP = ElIdWithChildren("p", "msg", liveText);
            var live = BuildDoc(liveP);

            var fresh = BuildDoc(ElIdWithChildren("p", "msg", new TextNode("v2")));

            DomDiffer.ApplyDocumentDiff(live, fresh);

            Assert.That(live.Children.Count, Is.EqualTo(1));
            Assert.That(live.Children[0], Is.SameAs(liveP),
                "the <p id=msg> Element instance must not be replaced");
            Assert.That(liveP.Children.Count, Is.EqualTo(1));
            Assert.That(liveP.Children[0], Is.SameAs(liveText),
                "the inner TextNode instance must not be replaced");
            Assert.That(liveText.Data, Is.EqualTo("v2"));
        }
    }
}
