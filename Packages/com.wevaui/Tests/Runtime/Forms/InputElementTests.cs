using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;
using Weva.Reactive;

namespace Weva.Tests.Forms {
    public class InputElementTests {
        static Element NewInput() => new Element("input");

        [Test]
        public void Type_defaults_to_text_when_attribute_missing() {
            var ie = new InputElement(NewInput());
            Assert.That(ie.Type, Is.EqualTo("text"));
        }

        [Test]
        public void Type_round_trips_via_attribute() {
            var e = NewInput();
            var ie = new InputElement(e);
            ie.Type = "password";
            Assert.That(e.GetAttribute("type"), Is.EqualTo("password"));
            Assert.That(ie.Type, Is.EqualTo("password"));
        }

        [Test]
        public void Value_round_trips_through_SetAttribute() {
            var e = NewInput();
            var ie = new InputElement(e);
            ie.Value = "hello";
            Assert.That(e.GetAttribute("value"), Is.EqualTo("hello"));
            Assert.That(ie.Value, Is.EqualTo("hello"));
        }

        [Test]
        public void Placeholder_round_trips() {
            var e = NewInput();
            var ie = new InputElement(e);
            ie.Placeholder = "Search...";
            Assert.That(ie.Placeholder, Is.EqualTo("Search..."));
        }

        [Test]
        public void Disabled_toggles_attribute_presence() {
            var e = NewInput();
            var ie = new InputElement(e);
            Assert.That(ie.Disabled, Is.False);
            ie.Disabled = true;
            Assert.That(e.HasAttribute("disabled"), Is.True);
            ie.Disabled = false;
            Assert.That(e.HasAttribute("disabled"), Is.False);
        }

        [Test]
        public void ReadOnly_toggles_attribute_presence() {
            var ie = new InputElement(NewInput());
            ie.ReadOnly = true;
            Assert.That(ie.ReadOnly, Is.True);
            ie.ReadOnly = false;
            Assert.That(ie.ReadOnly, Is.False);
        }

        [Test]
        public void Required_toggles_attribute_presence() {
            var ie = new InputElement(NewInput());
            ie.Required = true;
            Assert.That(ie.Required, Is.True);
            ie.Required = false;
            Assert.That(ie.Required, Is.False);
        }

        [Test]
        public void Checked_round_trips_for_checkbox() {
            var e = NewInput();
            e.SetAttribute("type", "checkbox");
            var ie = new InputElement(e);
            Assert.That(ie.Checked, Is.False);
            ie.Checked = true;
            Assert.That(e.HasAttribute("checked"), Is.True);
            ie.Checked = false;
            Assert.That(e.HasAttribute("checked"), Is.False);
        }

        [Test]
        public void SetAttribute_fires_mutation_event() {
            var e = NewInput();
            DomMutation last = default;
            int count = 0;
            e.Mutated += m => { last = m; count++; };
            var ie = new InputElement(e);
            ie.Value = "abc";
            Assert.That(count, Is.EqualTo(1));
            Assert.That(last.Kind, Is.EqualTo(DomMutationKind.AttributeAdded));
            Assert.That(last.AttributeName, Is.EqualTo("value"));
            Assert.That(last.NewValue, Is.EqualTo("abc"));
        }

        [Test]
        public void SetAttribute_changing_value_fires_AttributeChanged() {
            var e = NewInput();
            e.SetAttribute("value", "first");
            DomMutation last = default;
            int count = 0;
            e.Mutated += m => { last = m; count++; };
            new InputElement(e).Value = "second";
            Assert.That(count, Is.EqualTo(1));
            Assert.That(last.Kind, Is.EqualTo(DomMutationKind.AttributeChanged));
            Assert.That(last.OldValue, Is.EqualTo("first"));
            Assert.That(last.NewValue, Is.EqualTo("second"));
        }

        [Test]
        public void Setting_same_value_does_not_fire_mutation() {
            var e = NewInput();
            e.SetAttribute("value", "same");
            int count = 0;
            e.Mutated += _ => count++;
            new InputElement(e).Value = "same";
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void Wrapping_non_input_throws() {
            var div = new Element("div");
            Assert.Throws<System.ArgumentException>(() => new InputElement(div));
        }

        [Test]
        public void MaxLength_round_trips() {
            var ie = new InputElement(NewInput());
            ie.MaxLength = 12;
            Assert.That(ie.MaxLength, Is.EqualTo(12));
            ie.MaxLength = null;
            Assert.That(ie.Element.HasAttribute("maxlength"), Is.False);
        }

        [Test]
        public void IsTextual_recognises_textual_types() {
            var ie = new InputElement(NewInput());
            ie.Type = "text";    Assert.That(ie.IsTextual, Is.True);
            ie.Type = "email";   Assert.That(ie.IsTextual, Is.True);
            ie.Type = "number";  Assert.That(ie.IsTextual, Is.True);
            ie.Type = "checkbox"; Assert.That(ie.IsTextual, Is.False);
            ie.Type = "range";    Assert.That(ie.IsTextual, Is.False);
        }
    }
}
