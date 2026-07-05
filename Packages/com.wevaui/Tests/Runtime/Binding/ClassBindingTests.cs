using System;
using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Tests.Binding {
    // ClassBinding toggles a single CSS class on an Element based on the
    // truthiness of a path-resolved value. These tests pin: ctor validation,
    // truthiness semantics (bool / string / numeric), idempotency once the
    // class is already in the desired state, and InvalidationTracker
    // notification on actual mutations.
    public class ClassBindingTests {
        class Vm {
            public bool Active = false;
            public string Mode = "on";
            public int Score = 0;
            public string Empty = "";
        }

        [Test]
        public void Constructor_rejects_null_target() {
            Assert.That(() => new ClassBinding(null, "selected", BindingPath.Parse("Active")),
                Throws.InstanceOf<ArgumentNullException>());
        }

        [Test]
        public void Constructor_rejects_empty_class_name() {
            var el = new Element("div");
            Assert.That(() => new ClassBinding(el, "   ", BindingPath.Parse("Active")),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void Constructor_trims_class_name_and_records_path() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "  selected  ", BindingPath.Parse("Active"));
            Assert.That(binding.ClassName, Is.EqualTo("selected"));
            Assert.That(binding.Target, Is.SameAs(el));
            Assert.That(binding.Path, Is.EqualTo(BindingPath.Parse("Active")));
        }

        [Test]
        public void Update_with_true_value_adds_the_class() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var vm = new Vm { Active = true };
            bool changed = binding.Update(vm);
            Assert.That(changed, Is.True);
            Assert.That(el.GetAttribute("class"), Is.EqualTo("selected"));
        }

        [Test]
        public void Update_with_false_value_does_not_add_the_class() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var vm = new Vm { Active = false };
            bool changed = binding.Update(vm);
            // First-ever update with a falsy value: HasClass already false,
            // SetClass short-circuits and no DOM mutation occurs.
            Assert.That(changed, Is.False);
            Assert.That(el.GetAttribute("class") ?? string.Empty, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Update_removes_the_class_when_value_flips_to_false() {
            var el = new Element("div");
            el.SetAttribute("class", "selected other");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var vm = new Vm { Active = false };
            bool changed = binding.Update(vm);
            Assert.That(changed, Is.True);
            Assert.That(el.GetAttribute("class"), Is.EqualTo("other"));
        }

        [Test]
        public void Update_is_idempotent_on_repeat_call_with_same_value() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var vm = new Vm { Active = true };
            Assert.That(binding.Update(vm), Is.True);
            // Second call with same state: no mutation, returns false.
            Assert.That(binding.Update(vm), Is.False);
            Assert.That(el.GetAttribute("class"), Is.EqualTo("selected"));
        }

        [Test]
        public void Update_treats_string_false_and_zero_as_falsy() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Mode"));
            // First seed the class to true so we can observe it being removed.
            var vm = new Vm { Mode = "yes" };
            binding.Update(vm);
            Assert.That(el.GetAttribute("class"), Is.EqualTo("selected"));

            vm.Mode = "false";
            Assert.That(binding.Update(vm), Is.True);
            Assert.That(el.GetAttribute("class") ?? string.Empty, Is.EqualTo(string.Empty));

            // Re-add via "0" path - "0" is also falsy.
            vm.Mode = "yes";
            binding.Update(vm);
            vm.Mode = "0";
            Assert.That(binding.Update(vm), Is.True);
            Assert.That(el.GetAttribute("class") ?? string.Empty, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Update_treats_nonzero_numeric_as_truthy_and_zero_as_falsy() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "scored", BindingPath.Parse("Score"));
            var vm = new Vm { Score = 0 };
            // Numeric zero -> falsy -> no class added.
            Assert.That(binding.Update(vm), Is.False);
            Assert.That(el.GetAttribute("class") ?? string.Empty, Is.EqualTo(string.Empty));

            vm.Score = 5;
            Assert.That(binding.Update(vm), Is.True);
            Assert.That(el.GetAttribute("class"), Is.EqualTo("scored"));
        }

        [Test]
        public void Update_does_not_duplicate_class_if_already_present() {
            var el = new Element("div");
            el.SetAttribute("class", "selected");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var vm = new Vm { Active = true };
            // Already present and value truthy: SetClass returns false, no change.
            Assert.That(binding.Update(vm), Is.False);
            Assert.That(el.GetAttribute("class"), Is.EqualTo("selected"));
        }

        [Test]
        public void Update_marks_target_dirty_on_tracker_when_class_changes() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var tracker = new InvalidationTracker();
            var vm = new Vm { Active = true };
            binding.Update(vm, tracker);
            // Structure is NOT marked by ClassBinding (REACT-1). The cascade
            // engine (CascadeEngine.ComputeOrHit / ApplyLayoutInvalidation)
            // marks Structure if the class flip causes a display: none ↔ shown
            // transition — that path runs before Layout() is called and will
            // add Structure to the tracker if needed. Preemptively marking
            // Structure here blocked TryLayoutSubtree unconditionally, even for
            // paint-only class changes like border-color / box-shadow toggles.
            Assert.That(tracker.IsDirty(el, InvalidationKind.Structure), Is.False);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Style), Is.True);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Layout), Is.True);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Paint), Is.True);
        }

        [Test]
        public void Update_does_not_dirty_tracker_when_state_unchanged() {
            var el = new Element("div");
            var binding = new ClassBinding(el, "selected", BindingPath.Parse("Active"));
            var vm = new Vm { Active = true };
            binding.Update(vm); // seed
            var tracker = new InvalidationTracker();
            // Second call is a no-op; tracker should NOT see a dirty mark.
            binding.Update(vm, tracker);
            Assert.That(tracker.DirtyCount, Is.EqualTo(0));
        }
    }
}
