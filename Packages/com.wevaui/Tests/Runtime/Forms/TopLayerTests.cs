using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;

namespace Weva.Tests.Forms {
    // The TopLayer.IsHost predicate gates `::backdrop` synthesis. v1 promotes
    // exactly two element shapes: modal dialogs (`<dialog data-modal>`) and
    // open popovers (`[popover][data-popover-open]`). Non-modal dialogs and
    // unopened popovers must not promote — they paint normally without a
    // backdrop. The tests here lock in those boundaries before the BoxBuilder
    // path consumes them.
    public class TopLayerTests {
        [Test]
        public void Modal_dialog_is_top_layer_host() {
            var d = new Element("dialog");
            d.SetAttribute("data-modal", "");
            Assert.That(TopLayer.IsHost(d), Is.True);
        }

        [Test]
        public void Non_modal_dialog_is_not_host() {
            var d = new Element("dialog");
            d.SetAttribute("open", "");
            Assert.That(TopLayer.IsHost(d), Is.False);
        }

        [Test]
        public void Bare_dialog_is_not_host() {
            Assert.That(TopLayer.IsHost(new Element("dialog")), Is.False);
        }

        [Test]
        public void Open_popover_is_top_layer_host() {
            var p = new Element("div");
            p.SetAttribute("popover", "auto");
            p.SetAttribute("data-popover-open", "");
            Assert.That(TopLayer.IsHost(p), Is.True);
        }

        [Test]
        public void Closed_popover_is_not_host() {
            var p = new Element("div");
            p.SetAttribute("popover", "auto");
            Assert.That(TopLayer.IsHost(p), Is.False);
        }

        [Test]
        public void Element_with_only_data_popover_open_is_not_host() {
            // Without the popover attribute itself, the data-popover-open
            // marker has no meaning — the element isn't a popover at all.
            var p = new Element("div");
            p.SetAttribute("data-popover-open", "");
            Assert.That(TopLayer.IsHost(p), Is.False);
        }

        [Test]
        public void Null_element_is_not_host() {
            Assert.That(TopLayer.IsHost(null), Is.False);
        }

        [Test]
        public void Modal_attribute_via_dialog_element_promotes_host() {
            // Round-trip: DialogElement.ShowModal() flips data-modal on the
            // backing element; TopLayer.IsHost must observe the change.
            var d = new DialogElement(new Element("dialog"));
            Assert.That(TopLayer.IsHost(d.Element), Is.False);
            d.ShowModal();
            Assert.That(TopLayer.IsHost(d.Element), Is.True);
            d.Close();
            Assert.That(TopLayer.IsHost(d.Element), Is.False);
        }
    }
}
