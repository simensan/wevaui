using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;

namespace Weva.Tests.Forms {
    public class DialogElementTests {
        static Element NewDialog() => new Element("dialog");

        [Test]
        public void ShowModal_sets_open_and_modal_flags() {
            var d = new DialogElement(NewDialog());
            d.ShowModal();
            Assert.That(d.Open, Is.True);
            Assert.That(d.IsModal, Is.True);
        }

        [Test]
        public void Show_sets_open_only() {
            var d = new DialogElement(NewDialog());
            d.Show();
            Assert.That(d.Open, Is.True);
            Assert.That(d.IsModal, Is.False);
        }

        [Test]
        public void Close_clears_open_and_modal() {
            var d = new DialogElement(NewDialog());
            d.ShowModal();
            d.Close();
            Assert.That(d.Open, Is.False);
            Assert.That(d.IsModal, Is.False);
        }

        [Test]
        public void Close_fires_closed_event_when_was_open() {
            var d = new DialogElement(NewDialog());
            int closedCount = 0;
            d.Closed += _ => closedCount++;
            d.Show();
            d.Close();
            Assert.That(closedCount, Is.EqualTo(1));
        }

        [Test]
        public void Close_does_not_fire_when_already_closed() {
            var d = new DialogElement(NewDialog());
            int closedCount = 0;
            d.Closed += _ => closedCount++;
            d.Close();
            Assert.That(closedCount, Is.EqualTo(0));
        }

        [Test]
        public void Close_with_return_value_records_it() {
            var d = new DialogElement(NewDialog());
            d.Show();
            d.Close("OK");
            Assert.That(d.ReturnValue, Is.EqualTo("OK"));
        }

        [Test]
        public void Cancel_fires_cancelled_then_closed() {
            var d = new DialogElement(NewDialog());
            d.Show();
            string sequence = "";
            d.Cancelled += _ => sequence += "C";
            d.Closed += _ => sequence += "X";
            d.Cancel();
            Assert.That(sequence, Is.EqualTo("CX"));
        }

        [Test]
        public void Constructor_rejects_non_dialog_element() {
            Assert.Throws<System.ArgumentException>(() => new DialogElement(new Element("div")));
        }

        [Test]
        public void Open_property_round_trips_via_attribute() {
            var e = NewDialog();
            var d = new DialogElement(e);
            d.Open = true;
            Assert.That(e.HasAttribute("open"), Is.True);
            d.Open = false;
            Assert.That(e.HasAttribute("open"), Is.False);
        }

        [Test]
        public void ShowModal_marks_dialog_as_top_layer_host() {
            var d = new DialogElement(NewDialog());
            Assert.That(TopLayer.IsHost(d.Element), Is.False);
            d.ShowModal();
            Assert.That(TopLayer.IsHost(d.Element), Is.True);
            // Non-modal Show() should NOT promote to the top layer.
            d.Close();
            d.Show();
            Assert.That(TopLayer.IsHost(d.Element), Is.False);
        }
    }
}
