using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    // Regression coverage for the runtime DOM-mutation API surface that authors
    // touch (SetAttribute, AppendChild, TextNode.Data, BindingSet.Update). These
    // assert the end-to-end pipeline: a mutation on the DOM bubbles to the
    // attached InvalidationTracker and marks the right kinds dirty so the
    // cascade / layout / paint passes pick the change up on the next frame.
    public class DomMutationApiPipelineTests {
        class Ctx {
            public string Label = "hello";
        }

        [Test]
        public void SetAttribute_class_marks_subtree_Style_through_tracker() {
            var doc = new Document();
            var root = new Element("div");
            var child = new Element("span");
            doc.AppendChild(root);
            root.AppendChild(child);

            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear(); // ignore the AppendChild dirties from setup.

            root.SetAttribute("class", "active");

            // PI7 (Strategy B): class/id changes mark the TARGET with
            // Style|Layout|Paint and DESCENDANTS with Style only. Descendants'
            // layout & paint caches drop via the Style mask in
            // LayoutEngine.Apply / BoxToPaintConverter.Apply; subsequent
            // per-box cache evaluation (LayoutCacheKey / PaintCacheKey both
            // key on box.Style.Version) narrows actual recompute to the
            // descendants whose computed style truly flipped.
            var targetKinds = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(tracker.IsDirty(root, targetKinds), Is.True);
            Assert.That(tracker.IsDirty(child, InvalidationKind.Style), Is.True);
            Assert.That(tracker.IsDirty(child, InvalidationKind.Layout), Is.False);
            Assert.That(tracker.IsDirty(child, InvalidationKind.Paint), Is.False);
        }

        [Test]
        public void AppendChild_marks_new_subtree_Structure_Style_Layout_Paint() {
            var doc = new Document();
            var root = new Element("div");
            doc.AppendChild(root);

            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();

            var inserted = new Element("p");
            var grandchild = new Element("em");
            inserted.AppendChild(grandchild);
            root.AppendChild(inserted);

            // Parent only needs re-layout; the new subtree needs everything.
            Assert.That(tracker.IsDirty(root, InvalidationKind.Layout), Is.True);
            var all = InvalidationKind.Structure | InvalidationKind.Style
                    | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(tracker.IsDirty(inserted, all), Is.True);
            Assert.That(tracker.IsDirty(grandchild, all), Is.True);
        }

        [Test]
        public void BindingSet_Update_flowed_through_tracker_marks_attribute_target() {
            var doc = new Document();
            var el = new Element("div");
            el.SetAttribute("title", "{{ Label }}");
            doc.AppendChild(el);

            var ctx = new Ctx { Label = "hello" };
            var set = BindingScanner.Scan(doc, ctx);
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);

            set.Update(ctx, tracker);
            // First update writes title="hello" through SetAttribute, which
            // marks the element itself Style+Layout+Paint dirty.
            Assert.That(el.GetAttribute("title"), Is.EqualTo("hello"));
            Assert.That(tracker.IsDirty(el, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint), Is.True);

            tracker.Clear();
            ctx.Label = "world";
            set.Update(ctx, tracker);
            Assert.That(el.GetAttribute("title"), Is.EqualTo("world"));
            Assert.That(tracker.IsDirty(el, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint), Is.True);
        }
    }
}
