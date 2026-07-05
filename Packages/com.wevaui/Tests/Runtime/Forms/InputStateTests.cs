using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;

namespace Weva.Tests.Forms {
    public class InputStateTests {
        [Test]
        public void New_input_state_reads_value_from_attribute() {
            var e = new Element("input");
            e.SetAttribute("value", "hi");
            var s = new InputState(e);
            Assert.That(s.Value, Is.EqualTo("hi"));
            Assert.That(s.CursorIndex, Is.EqualTo(2));
        }

        [Test]
        public void Setting_value_bumps_version() {
            var e = new Element("input");
            var s = new InputState(e);
            long v0 = s.Version;
            s.Value = "abc";
            Assert.That(s.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void SetCaret_clamps_within_value_length() {
            var e = new Element("input");
            var s = new InputState(e) { Value = "abc" };
            s.SetCaret(99);
            Assert.That(s.CursorIndex, Is.EqualTo(3));
            s.SetCaret(-5);
            Assert.That(s.CursorIndex, Is.EqualTo(0));
        }

        [Test]
        public void Setting_selection_marks_HasSelection() {
            var e = new Element("input");
            var s = new InputState(e) { Value = "hello" };
            s.SetSelection(1, 4);
            Assert.That(s.HasSelection, Is.True);
            Assert.That(s.SelectionStart, Is.EqualTo(1));
            Assert.That(s.SelectionEnd, Is.EqualTo(4));
        }

        [Test]
        public void TrackModel_syncs_text_back_into_state() {
            var e = new Element("input");
            var s = new InputState(e);
            var model = new TextEditModel("");
            s.TrackModel(model);
            model.Insert("hi");
            Assert.That(s.Value, Is.EqualTo("hi"));
            Assert.That(s.CursorIndex, Is.EqualTo(2));
        }

        [Test]
        public void Setting_value_clamps_existing_caret() {
            var e = new Element("input");
            var s = new InputState(e) { Value = "longvalue" };
            s.SetCaret(7);
            s.Value = "abc";
            Assert.That(s.CursorIndex, Is.EqualTo(3));
        }

        [Test]
        public void Disabled_round_trips_independently_from_DOM_attr() {
            var e = new Element("input");
            var s = new InputState(e);
            Assert.That(s.Disabled, Is.False);
            s.Disabled = true;
            Assert.That(s.Disabled, Is.True);
        }
    }
}
