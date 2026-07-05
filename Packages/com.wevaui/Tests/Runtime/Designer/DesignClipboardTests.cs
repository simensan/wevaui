using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for deep-clone, clipboard (copy/cut/paste) and duplicate. Verifies
    /// clones are fully detached, paste/cut/duplicate are single undo steps, and
    /// repeated pastes never alias shared state.
    /// </summary>
    public class DesignClipboardTests
    {
        static (DocumentEditor ed, DesignNode root) NewEditor()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            return (new DocumentEditor(new DesignDocument(root)), root);
        }

        // --- Deep clone ---

        [Test]
        public void Clone_copies_all_fields_and_children()
        {
            var n = new DesignNode("card") { Layout = LayoutMode.Row, Gap = 8, Fill = "#abc", Radius = 4 };
            n.SetFixedSize(100, 50);
            n.Add(new DesignNode("label") { Text = "hi", FontSize = 12 });

            DesignNode c = n.Clone();
            Assert.That(c.Name, Is.EqualTo("card"));
            Assert.That(c.Layout, Is.EqualTo(LayoutMode.Row));
            Assert.That(c.Gap.Px, Is.EqualTo(8));
            Assert.That(c.Fill, Is.EqualTo("#abc"));
            Assert.That(c.Width, Is.EqualTo(100));
            Assert.That(c.Children, Has.Count.EqualTo(1));
            Assert.That(c.Children[0].Text, Is.EqualTo("hi"));
        }

        [Test]
        public void Clone_is_fully_detached()
        {
            var n = new DesignNode("a") { Fill = "#111" };
            n.Add(new DesignNode("child"));
            DesignNode c = n.Clone();

            c.Fill = "#999";
            c.Children[0].Name = "renamed";
            c.Children.Add(new DesignNode("extra"));

            Assert.That(n.Fill, Is.EqualTo("#111"));
            Assert.That(n.Children[0].Name, Is.EqualTo("child"));
            Assert.That(n.Children, Has.Count.EqualTo(1));
            Assert.That(c.Children[0], Is.Not.SameAs(n.Children[0]));
        }

        // --- Clipboard ---

        [Test]
        public void Copy_paste_inserts_an_independent_copy()
        {
            var (ed, root) = NewEditor();
            var card = new DesignNode("card") { Fill = "#abc" };
            ed.AppendChild(root, card);

            var clip = new DesignClipboard();
            clip.Copy(card);
            DesignNode pasted = clip.PasteInto(ed, root);

            Assert.That(root.Children, Has.Count.EqualTo(2));
            Assert.That(pasted, Is.Not.SameAs(card));
            pasted.Fill = "#000";
            Assert.That(card.Fill, Is.EqualTo("#abc")); // original unaffected
        }

        [Test]
        public void Copy_is_a_snapshot_unaffected_by_later_source_edits()
        {
            var (ed, root) = NewEditor();
            var card = new DesignNode("card") { Fill = "#abc" };
            ed.AppendChild(root, card);

            var clip = new DesignClipboard();
            clip.Copy(card);
            card.Fill = "#changed"; // edit source after copying

            DesignNode pasted = clip.PasteInto(ed, root);
            Assert.That(pasted.Fill, Is.EqualTo("#abc")); // snapshot, not live
        }

        [Test]
        public void Paste_is_a_single_undo_step()
        {
            var (ed, root) = NewEditor();
            var card = new DesignNode("card");
            ed.AppendChild(root, card);

            var clip = new DesignClipboard();
            clip.Copy(card);
            clip.PasteInto(ed, root);
            Assert.That(root.Children, Has.Count.EqualTo(2));

            ed.Undo();
            Assert.That(root.Children, Has.Count.EqualTo(1));
        }

        [Test]
        public void Multiple_pastes_are_independent()
        {
            var (ed, root) = NewEditor();
            var card = new DesignNode("card");
            ed.AppendChild(root, card);

            var clip = new DesignClipboard();
            clip.Copy(card);
            DesignNode p1 = clip.PasteInto(ed, root);
            DesignNode p2 = clip.PasteInto(ed, root);

            Assert.That(p1, Is.Not.SameAs(p2));
            Assert.That(root.Children, Has.Count.EqualTo(3));
        }

        [Test]
        public void Paste_with_empty_clipboard_is_a_noop()
        {
            var (ed, root) = NewEditor();
            var clip = new DesignClipboard();
            Assert.That(clip.HasContent, Is.False);
            DesignNode pasted = clip.PasteInto(ed, root);
            Assert.That(pasted, Is.Null);
            Assert.That(ed.CanUndo, Is.False);
        }

        [Test]
        public void Cut_copies_then_removes_in_one_undo_step()
        {
            var (ed, root) = NewEditor();
            var a = new DesignNode("a");
            var b = new DesignNode("b");
            ed.AppendChild(root, a);
            ed.AppendChild(root, b);

            var clip = new DesignClipboard();
            clip.Cut(ed, root, a);

            Assert.That(root.Children, Has.Count.EqualTo(1));
            Assert.That(clip.HasContent, Is.True);

            ed.Undo(); // restores the cut node
            Assert.That(root.Children, Has.Count.EqualTo(2));

            DesignNode pasted = clip.PasteInto(ed, root);
            Assert.That(pasted.Name, Is.EqualTo("a"));
        }

        // --- Duplicate ---

        [Test]
        public void Duplicate_inserts_a_copy_right_after_the_original()
        {
            var (ed, root) = NewEditor();
            var a = new DesignNode("a");
            var b = new DesignNode("b");
            ed.AppendChild(root, a);
            ed.AppendChild(root, b);

            DesignNode dup = ed.Duplicate(root, a);
            Assert.That(root.Children, Has.Count.EqualTo(3));
            Assert.That(root.Children[1], Is.SameAs(dup)); // right after 'a'
            Assert.That(root.Children[2], Is.SameAs(b));
            Assert.That(dup, Is.Not.SameAs(a));
        }

        [Test]
        public void Duplicate_is_undoable()
        {
            var (ed, root) = NewEditor();
            var a = new DesignNode("a");
            ed.AppendChild(root, a);

            ed.Duplicate(root, a);
            Assert.That(root.Children, Has.Count.EqualTo(2));

            ed.Undo();
            Assert.That(root.Children, Has.Count.EqualTo(1));
        }
    }
}
