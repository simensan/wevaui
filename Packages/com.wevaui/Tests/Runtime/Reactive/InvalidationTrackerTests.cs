using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    public class InvalidationTrackerTests {
        [Test]
        public void Empty_tracker_has_no_dirty() {
            var t = new InvalidationTracker();
            Assert.That(t.DirtyCount, Is.EqualTo(0));
            Assert.That(t.AllDirty.Count, Is.EqualTo(0));
            Assert.That(t.HasAny(InvalidationKind.All), Is.False);
        }

        [Test]
        public void MarkDirty_adds_node_with_given_kind() {
            var t = new InvalidationTracker();
            var e = new Element("div");
            t.MarkDirty(e, InvalidationKind.Style);
            Assert.That(t.IsDirty(e, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(e, InvalidationKind.Layout), Is.False);
        }

        [Test]
        public void MarkDirty_twice_ors_kinds() {
            var t = new InvalidationTracker();
            var e = new Element("div");
            t.MarkDirty(e, InvalidationKind.Style);
            t.MarkDirty(e, InvalidationKind.Layout);
            Assert.That(t.IsDirty(e, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(e, InvalidationKind.Layout), Is.True);
            Assert.That(t.IsDirty(e, InvalidationKind.Style | InvalidationKind.Layout), Is.True);
            Assert.That(t.IsDirty(e, InvalidationKind.Paint), Is.False);
        }

        [Test]
        public void IsDirty_requires_all_requested_flags() {
            var t = new InvalidationTracker();
            var e = new Element("div");
            t.MarkDirty(e, InvalidationKind.Style);
            Assert.That(t.IsDirty(e, InvalidationKind.Style | InvalidationKind.Layout), Is.False);
        }

        [Test]
        public void HasAny_respects_flags() {
            var t = new InvalidationTracker();
            var a = new Element("a");
            var b = new Element("b");
            t.MarkDirty(a, InvalidationKind.Style);
            t.MarkDirty(b, InvalidationKind.Layout);
            Assert.That(t.HasAny(InvalidationKind.Style), Is.True);
            Assert.That(t.HasAny(InvalidationKind.Layout), Is.True);
            Assert.That(t.HasAny(InvalidationKind.Paint), Is.False);
            Assert.That(t.HasAny(InvalidationKind.Style | InvalidationKind.Paint), Is.True);
        }

        [Test]
        public void GetDirty_returns_nodes_with_given_kind() {
            var t = new InvalidationTracker();
            var a = new Element("a");
            var b = new Element("b");
            var c = new Element("c");
            t.MarkDirty(a, InvalidationKind.Style);
            t.MarkDirty(b, InvalidationKind.Style | InvalidationKind.Layout);
            t.MarkDirty(c, InvalidationKind.Layout);

            var styled = t.GetDirty(InvalidationKind.Style).ToList();
            Assert.That(styled, Has.Count.EqualTo(2));
            Assert.That(styled, Does.Contain(a));
            Assert.That(styled, Does.Contain(b));

            var layout = t.GetDirty(InvalidationKind.Layout).ToList();
            Assert.That(layout, Has.Count.EqualTo(2));
            Assert.That(layout, Does.Contain(b));
            Assert.That(layout, Does.Contain(c));
        }

        [Test]
        public void Clear_empties_all_dirty() {
            var t = new InvalidationTracker();
            var e = new Element("div");
            t.MarkDirty(e, InvalidationKind.All);
            t.Clear();
            Assert.That(t.DirtyCount, Is.EqualTo(0));
            Assert.That(t.AllDirty.Count, Is.EqualTo(0));
        }

        [Test]
        public void Clear_with_kind_clears_only_that_kind_across_all_nodes() {
            var t = new InvalidationTracker();
            var a = new Element("a");
            var b = new Element("b");
            t.MarkDirty(a, InvalidationKind.Style | InvalidationKind.Layout);
            t.MarkDirty(b, InvalidationKind.Layout);
            t.Clear(InvalidationKind.Layout);
            Assert.That(t.IsDirty(a, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(a, InvalidationKind.Layout), Is.False);
            Assert.That(t.IsDirty(b, InvalidationKind.Layout), Is.False);
            Assert.That(t.DirtyCount, Is.EqualTo(1));
        }

        [Test]
        public void Clear_with_node_removes_only_that_node() {
            var t = new InvalidationTracker();
            var a = new Element("a");
            var b = new Element("b");
            t.MarkDirty(a, InvalidationKind.Style);
            t.MarkDirty(b, InvalidationKind.Style);
            t.Clear(a);
            Assert.That(t.IsDirty(a, InvalidationKind.Style), Is.False);
            Assert.That(t.IsDirty(b, InvalidationKind.Style), Is.True);
        }

        [Test]
        public void MarkSubtreeDirty_walks_descendants() {
            var t = new InvalidationTracker();
            var root = new Element("div");
            var mid = new Element("section");
            var leaf = new Element("span");
            root.AppendChild(mid);
            mid.AppendChild(leaf);
            t.MarkSubtreeDirty(root, InvalidationKind.Style);
            Assert.That(t.IsDirty(root, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(mid, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(leaf, InvalidationKind.Style), Is.True);
        }

        [Test]
        public void Attach_AppendChild_marks_parent_layout_subject_all_ancestors_composite() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var grand = new Element("section");
            doc.AppendChild(grand);
            t.Attach(doc);
            t.Clear();

            var newChild = new Element("span");
            var inner = new Element("em");
            newChild.AppendChild(inner);
            grand.AppendChild(newChild);

            // Parent (grand) gets Layout
            Assert.That(t.IsDirty(grand, InvalidationKind.Layout), Is.True);
            // Subject and its descendants get Structure | Style | Layout | Paint
            var subjectKinds = InvalidationKind.Structure | InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(newChild, subjectKinds), Is.True);
            Assert.That(t.IsDirty(inner, subjectKinds), Is.True);
            // Ancestors get Composite
            Assert.That(t.IsDirty(grand, InvalidationKind.Composite), Is.True);
            Assert.That(t.IsDirty(doc, InvalidationKind.Composite), Is.True);
        }

        [Test]
        public void Attach_SetAttribute_class_marks_target_style_layout_paint_descendants_style_only() {
            // PI7 (Strategy B): the target itself keeps Style|Layout|Paint
            // because its own computed style is what changed; descendants get
            // Style only and rely on LayoutEngine.Apply / BoxToPaintConverter.
            // Apply to drop their layout/paint caches via the Style mark, then
            // re-evaluation through the per-box LayoutCacheKey / PaintCacheKey
            // (both keyed on box.Style.Version) handles the actual narrowing
            // to descendants whose computed style truly flipped.
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);
            t.Attach(doc);
            t.Clear();

            div.SetAttribute("class", "foo");

            var targetKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(div, targetKinds), Is.True);
            Assert.That(t.IsDirty(span, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(span, InvalidationKind.Layout), Is.False);
            Assert.That(t.IsDirty(span, InvalidationKind.Paint), Is.False);
        }

        [Test]
        public void Attach_SetAttribute_id_marks_target_style_layout_paint_descendants_style_only() {
            // Same Strategy B narrowing as the class case above. Test pinned
            // separately because OnMutation treats "class" and "id" via the
            // same branch — symmetric regression coverage so a future split of
            // the two paths can't silently regress one.
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);
            t.Attach(doc);
            t.Clear();

            div.SetAttribute("id", "main");

            var targetKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(div, targetKinds), Is.True);
            Assert.That(t.IsDirty(span, InvalidationKind.Style), Is.True);
            Assert.That(t.IsDirty(span, InvalidationKind.Layout), Is.False);
            Assert.That(t.IsDirty(span, InvalidationKind.Paint), Is.False);
        }

        [Test]
        public void Attach_SetAttribute_style_marks_element_only_not_subtree() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);
            t.Attach(doc);
            t.Clear();

            div.SetAttribute("style", "color: red");

            var elemKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(div, elemKinds), Is.True);
            Assert.That(t.IsDirty(span, InvalidationKind.Style), Is.False);
            Assert.That(t.IsDirty(span, InvalidationKind.Layout), Is.False);
        }

        [Test]
        public void Attach_SetAttribute_generic_marks_element_style_layout_paint_only() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);
            t.Attach(doc);
            t.Clear();

            div.SetAttribute("data-foo", "bar");

            var elemKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(div, elemKinds), Is.True);
            Assert.That(t.IsDirty(span, InvalidationKind.Style), Is.False);
        }

        [Test]
        public void Attach_TextChanged_marks_parent_element_layout_paint_only() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var p = new Element("p");
            var text = new TextNode("hello");
            doc.AppendChild(p);
            p.AppendChild(text);
            t.Attach(doc);
            t.Clear();

            text.Data = "world";

            Assert.That(t.IsDirty(text, InvalidationKind.Layout), Is.False);
            Assert.That(t.IsDirty(text, InvalidationKind.Paint), Is.False);
            Assert.That(t.IsDirty(text, InvalidationKind.Style), Is.False);
            Assert.That(t.IsDirty(p, InvalidationKind.Layout), Is.True);
            Assert.That(t.IsDirty(p, InvalidationKind.Paint), Is.True);
            Assert.That(t.IsDirty(doc, InvalidationKind.Layout), Is.False);
        }

        [Test]
        public void Attach_RemoveChild_marks_parent_layout_paint_subject_all() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            var span = new Element("span");
            doc.AppendChild(div);
            div.AppendChild(span);
            t.Attach(doc);
            t.Clear();

            div.RemoveChild(span);

            Assert.That(t.IsDirty(div, InvalidationKind.Layout), Is.True);
            Assert.That(t.IsDirty(div, InvalidationKind.Paint), Is.True);
            var subjectKinds = InvalidationKind.Structure | InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(span, subjectKinds), Is.True);
        }

        [Test]
        public void Attach_RemoveAttribute_marks_element() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            div.SetAttribute("data-x", "1");
            doc.AppendChild(div);
            t.Attach(doc);
            t.Clear();

            div.RemoveAttribute("data-x");

            var elemKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(div, elemKinds), Is.True);
        }

        [Test]
        public void Detach_stops_further_invalidation() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            doc.AppendChild(div);
            t.Attach(doc);
            t.Clear();
            t.Detach(doc);

            div.SetAttribute("class", "foo");

            Assert.That(t.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Tracker_can_reattach_to_a_different_document() {
            var t = new InvalidationTracker();
            var doc1 = new Document();
            var div1 = new Element("div");
            doc1.AppendChild(div1);
            t.Attach(doc1);
            t.Detach(doc1);

            var doc2 = new Document();
            var div2 = new Element("section");
            doc2.AppendChild(div2);
            t.Attach(doc2);
            t.Clear();

            div2.SetAttribute("class", "x");

            var elemKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(div2, elemKinds), Is.True);

            // doc1 mutations should not be picked up.
            div1.SetAttribute("class", "y");
            Assert.That(t.IsDirty(div1, InvalidationKind.Style), Is.False);
        }

        [Test]
        public void OnMutation_can_be_driven_directly_without_attach() {
            var t = new InvalidationTracker();
            var parent = new Element("div");
            var child = new Element("span");
            t.OnMutation(DomMutation.ChildAdded(parent, child));
            Assert.That(t.IsDirty(parent, InvalidationKind.Layout), Is.True);
            var subjectKinds = InvalidationKind.Structure | InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(t.IsDirty(child, subjectKinds), Is.True);
        }

        // Regression: PseudoClassState (e.g. :hover toggle) must IMPLY Style
        // so cascade/paint downstream consumers that filter by Style observe
        // the change, but must NEVER imply Layout — that's what lets the
        // IncrementalLayoutGate skip relayout on a paint-only :hover flip.
        [Test]
        public void PseudoClassState_implies_Style_but_not_Layout() {
            var t = new InvalidationTracker();
            var btn = new Element("button");
            t.MarkDirty(btn, InvalidationKind.PseudoClassState);
            Assert.That(t.IsDirty(btn, InvalidationKind.PseudoClassState), Is.True);
            Assert.That(t.IsDirty(btn, InvalidationKind.Style), Is.True,
                "PseudoClassState must imply Style for downstream filters");
            Assert.That(t.IsDirty(btn, InvalidationKind.Layout), Is.False,
                "PseudoClassState must never imply Layout (else gate would not skip)");
            Assert.That(t.IsDirty(btn, InvalidationKind.Paint), Is.False);
        }

        [Test]
        public void Double_attach_to_same_document_does_not_double_subscribe() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var div = new Element("div");
            doc.AppendChild(div);
            t.Attach(doc);
            t.Attach(doc);
            t.Clear();

            div.SetAttribute("data-x", "1");

            // Should be marked dirty exactly once (no double-firing in any way that breaks state).
            var kinds = t.GetKinds(div);
            var elemKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(kinds, Is.EqualTo(elemKinds));
        }
    }
}
