using NUnit.Framework;
using Weva.Forms;

namespace Weva.Tests.Forms {
    public class TextEditModelTests {
        [Test]
        public void Insert_at_caret_advances_caret_and_changes_text() {
            var m = new TextEditModel("");
            m.Insert("hello");
            Assert.That(m.Text, Is.EqualTo("hello"));
            Assert.That(m.Selection.Start, Is.EqualTo(5));
            Assert.That(m.Selection.End, Is.EqualTo(5));
        }

        [Test]
        public void Insert_at_middle_caret() {
            var m = new TextEditModel("hello");
            m.SetCaret(2);
            m.Insert("XX");
            Assert.That(m.Text, Is.EqualTo("heXXllo"));
            Assert.That(m.Selection.Start, Is.EqualTo(4));
        }

        [Test]
        public void Backspace_deletes_char_before_caret() {
            var m = new TextEditModel("abc");
            m.SetCaret(3);
            m.DeleteBackward();
            Assert.That(m.Text, Is.EqualTo("ab"));
            Assert.That(m.Selection.Start, Is.EqualTo(2));
        }

        [Test]
        public void Backspace_at_start_is_noop() {
            var m = new TextEditModel("abc");
            m.SetCaret(0);
            Assert.That(m.DeleteBackward(), Is.False);
            Assert.That(m.Text, Is.EqualTo("abc"));
        }

        [Test]
        public void DeleteForward_removes_char_at_caret() {
            var m = new TextEditModel("abc");
            m.SetCaret(1);
            m.DeleteForward();
            Assert.That(m.Text, Is.EqualTo("ac"));
            Assert.That(m.Selection.Start, Is.EqualTo(1));
        }

        [Test]
        public void Range_delete_removes_selected_text() {
            var m = new TextEditModel("hello world");
            m.SetSelection(0, 5);
            m.DeleteBackward();
            Assert.That(m.Text, Is.EqualTo(" world"));
            Assert.That(m.Selection.Start, Is.EqualTo(0));
            Assert.That(m.Selection.End, Is.EqualTo(0));
        }

        [Test]
        public void MoveCaretLeft_within_bounds() {
            var m = new TextEditModel("abc");
            m.SetCaret(2);
            m.MoveCaretLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(1));
            m.SetCaret(0);
            m.MoveCaretLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(0));
        }

        [Test]
        public void MoveCaretRight_within_bounds() {
            var m = new TextEditModel("abc");
            m.SetCaret(0);
            m.MoveCaretRight();
            Assert.That(m.Selection.Start, Is.EqualTo(1));
            m.SetCaret(3);
            m.MoveCaretRight();
            Assert.That(m.Selection.Start, Is.EqualTo(3));
        }

        [Test]
        public void Shift_movement_extends_selection() {
            var m = new TextEditModel("abcdef");
            m.SetCaret(2);
            m.MoveCaretRight(extendSelection: true);
            m.MoveCaretRight(extendSelection: true);
            Assert.That(m.Selection.Start, Is.EqualTo(2));
            Assert.That(m.Selection.End, Is.EqualTo(4));
            Assert.That(m.Selection.IsCollapsed, Is.False);
        }

        [Test]
        public void Shift_left_creates_backward_selection() {
            var m = new TextEditModel("abcdef");
            m.SetCaret(4);
            m.MoveCaretLeft(extendSelection: true);
            m.MoveCaretLeft(extendSelection: true);
            Assert.That(m.Selection.Start, Is.EqualTo(2));
            Assert.That(m.Selection.End, Is.EqualTo(4));
            Assert.That(m.Selection.Direction, Is.EqualTo(SelectionDirection.Backward));
        }

        [Test]
        public void Home_and_End_move_to_line_start_and_end() {
            var m = new TextEditModel("abc");
            m.SetCaret(1);
            m.MoveToHome();
            Assert.That(m.Selection.Start, Is.EqualTo(0));
            m.MoveToEnd();
            Assert.That(m.Selection.Start, Is.EqualTo(3));
        }

        [Test]
        public void SelectAll_selects_entire_text() {
            var m = new TextEditModel("hello");
            m.SetCaret(2);
            m.SelectAll();
            Assert.That(m.Selection.Start, Is.EqualTo(0));
            Assert.That(m.Selection.End, Is.EqualTo(5));
        }

        [Test]
        public void Insert_with_active_selection_replaces_selection() {
            var m = new TextEditModel("hello world");
            m.SetSelection(0, 5);
            m.Insert("hi");
            Assert.That(m.Text, Is.EqualTo("hi world"));
            Assert.That(m.Selection.Start, Is.EqualTo(2));
        }

        [Test]
        public void IME_BeginUpdateCommit_inserts_committed_text() {
            var m = new TextEditModel("abc");
            m.SetCaret(2);
            m.BeginComposition();
            m.UpdateComposition("X");
            Assert.That(m.IsComposing, Is.True);
            Assert.That(m.CompositionString, Is.EqualTo("X"));
            m.CommitComposition("XY");
            Assert.That(m.IsComposing, Is.False);
            Assert.That(m.Text, Is.EqualTo("abXYc"));
        }

        [Test]
        public void IME_cancel_reverts_to_no_composition() {
            var m = new TextEditModel("abc");
            m.SetCaret(2);
            m.BeginComposition();
            m.UpdateComposition("hello");
            m.CancelComposition();
            Assert.That(m.IsComposing, Is.False);
            Assert.That(m.CompositionString, Is.EqualTo(""));
            Assert.That(m.Text, Is.EqualTo("abc"));
        }

        [Test]
        public void Multiline_vertical_caret_movement() {
            var m = new TextEditModel("hello\nworld\nfoo", multiline: true);
            m.SetCaret(2); // on first line at col 2 ("he|llo")
            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(8)); // "wo|rld" -> index 8
            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(14)); // "fo|o" -> index 14
        }

        [Test]
        public void Word_step_jumps_to_word_boundary() {
            var m = new TextEditModel("hello world foo");
            m.SetCaret(0);
            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(5));
            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(11));
            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(6));
        }

        [Test]
        public void Multiline_disallows_newline_via_Insert_when_singleline() {
            var m = new TextEditModel("", multiline: false);
            m.Insert("ab\ncd");
            Assert.That(m.Text, Is.EqualTo("abcd"));
        }

        [Test]
        public void MaxLength_clamps_inserts() {
            var m = new TextEditModel("", maxLength: 5);
            m.Insert("hello world");
            Assert.That(m.Text, Is.EqualTo("hello"));
            m.Insert("X");
            Assert.That(m.Text, Is.EqualTo("hello"));
        }

        // Audit FM6: the maxlength truncation used a raw Substring(0, allowed),
        // which could cut between the high and low surrogate of an astral
        // character — storing an orphan surrogate into the model and the value
        // attribute (corrupt string for serialization, measurement, paint).
        [Test]
        public void MaxLength_truncation_never_splits_a_surrogate_pair() {
            // "😀" is U+1F600 — two UTF-16 code units. One unit of budget left:
            // the whole character must be dropped, not halved.
            var m = new TextEditModel("abcd", maxLength: 5);
            m.SetCaret(4);
            m.Insert("😀");
            Assert.That(m.Text, Is.EqualTo("abcd"),
                "1 unit of budget cannot hold a 2-unit astral char — drop it whole");
            foreach (char c in m.Text)
                Assert.That(char.IsSurrogate(c), Is.False, "no orphan surrogate halves");
        }

        [Test]
        public void MaxLength_truncating_mixed_input_keeps_whole_characters() {
            // Budget 3, inserting "a😀b": 'a' fits, the emoji needs 2 more
            // (exactly fits), 'b' is dropped -> "a😀".
            var m = new TextEditModel("", maxLength: 3);
            m.Insert("a😀b");
            Assert.That(m.Text, Is.EqualTo("a😀"));

            // Budget 2, inserting "a😀": 'a' fits, halving the emoji is
            // forbidden -> only 'a' lands.
            var m2 = new TextEditModel("", maxLength: 2);
            m2.Insert("a😀");
            Assert.That(m2.Text, Is.EqualTo("a"));
            foreach (char c in m2.Text)
                Assert.That(char.IsSurrogate(c), Is.False);
        }

        [Test]
        public void MaxLength_astral_char_fitting_exactly_is_kept() {
            var m = new TextEditModel("", maxLength: 2);
            m.Insert("😀");
            Assert.That(m.Text, Is.EqualTo("😀"));
        }

        [Test]
        public void Changed_event_fires_on_text_mutation() {
            var m = new TextEditModel("");
            int changed = 0;
            m.Changed += () => changed++;
            m.Insert("abc");
            Assert.That(changed, Is.EqualTo(1));
            m.DeleteBackward();
            Assert.That(changed, Is.EqualTo(2));
        }
    }
}
