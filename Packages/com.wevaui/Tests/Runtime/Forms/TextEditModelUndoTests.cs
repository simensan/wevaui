using NUnit.Framework;
using Weva.Forms;

namespace Weva.Tests.Forms {
    // Undo/redo (input/selection audit follow-up). Chrome grouping model:
    // consecutive plain typing coalesces into one undo entry; any other edit
    // or a deliberate caret/selection move breaks the run; external value
    // rewrites invalidate the whole history.
    public class TextEditModelUndoTests {
        [Test]
        public void Typing_run_undoes_as_one_group() {
            var m = new TextEditModel();
            m.Insert("a"); m.Insert("b"); m.Insert("c");
            Assert.That(m.Text, Is.EqualTo("abc"));
            Assert.That(m.Undo(), Is.True);
            Assert.That(m.Text, Is.EqualTo(""), "a typing run is ONE undo step");
            Assert.That(m.CanUndo, Is.False);
        }

        [Test]
        public void Caret_move_breaks_the_typing_group() {
            var m = new TextEditModel();
            m.Insert("a"); m.Insert("b");
            m.SetCaret(1);
            m.Insert("X");
            Assert.That(m.Text, Is.EqualTo("aXb"));
            m.Undo();
            Assert.That(m.Text, Is.EqualTo("ab"), "the post-move character is its own group");
            m.Undo();
            Assert.That(m.Text, Is.EqualTo(""));
        }

        [Test]
        public void Undo_restores_the_selection_a_replacement_destroyed() {
            var m = new TextEditModel();
            m.Insert("hello world");
            m.SetSelection(0, 5);
            m.Insert("X"); // types over the selection
            Assert.That(m.Text, Is.EqualTo("X world"));
            m.Undo();
            Assert.That(m.Text, Is.EqualTo("hello world"));
            Assert.That((m.Selection.Start, m.Selection.End), Is.EqualTo((0, 5)),
                "undo restores the pre-edit selection");
        }

        [Test]
        public void Redo_reapplies_and_a_new_edit_clears_it() {
            var m = new TextEditModel();
            m.Insert("abc");
            m.Undo();
            Assert.That(m.Redo(), Is.True);
            Assert.That(m.Text, Is.EqualTo("abc"));
            m.Undo();
            m.Insert("z");
            Assert.That(m.CanRedo, Is.False, "a fresh edit invalidates the redo branch");
        }

        [Test]
        public void Deletes_are_their_own_groups() {
            var m = new TextEditModel();
            m.Insert("abcd");
            m.DeleteBackward();
            m.DeleteBackward();
            Assert.That(m.Text, Is.EqualTo("ab"));
            m.Undo();
            Assert.That(m.Text, Is.EqualTo("abc"), "each delete is a separate step");
            m.Undo();
            Assert.That(m.Text, Is.EqualTo("abcd"));
        }

        [Test]
        public void External_SetText_clears_the_history() {
            var m = new TextEditModel();
            m.Insert("abc");
            m.SetText("external");
            Assert.That(m.CanUndo, Is.False);
            Assert.That(m.Undo(), Is.False);
            Assert.That(m.Text, Is.EqualTo("external"));
        }

        [Test]
        public void Rejected_maxlength_insert_pushes_nothing() {
            var m = new TextEditModel { MaxLength = 3 };
            m.Insert("abc");
            Assert.That(m.Insert("d"), Is.False);
            m.Undo();
            Assert.That(m.Text, Is.EqualTo(""), "the no-op insert must not create an undo entry");
        }
    }
}
