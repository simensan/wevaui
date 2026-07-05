using NUnit.Framework;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    public class InvalidationKindTests {
        [Test]
        public void None_is_zero() {
            Assert.That((int)InvalidationKind.None, Is.EqualTo(0));
        }

        [Test]
        public void Flag_values_are_distinct_powers_of_two() {
            Assert.That((int)InvalidationKind.Structure, Is.EqualTo(1));
            Assert.That((int)InvalidationKind.Style, Is.EqualTo(2));
            Assert.That((int)InvalidationKind.Layout, Is.EqualTo(4));
            Assert.That((int)InvalidationKind.Paint, Is.EqualTo(8));
            Assert.That((int)InvalidationKind.Composite, Is.EqualTo(16));
        }

        [Test]
        public void All_includes_every_individual_flag() {
            var all = InvalidationKind.All;
            Assert.That(all.HasFlag(InvalidationKind.Structure), Is.True);
            Assert.That(all.HasFlag(InvalidationKind.Style), Is.True);
            Assert.That(all.HasFlag(InvalidationKind.Layout), Is.True);
            Assert.That(all.HasFlag(InvalidationKind.Paint), Is.True);
            Assert.That(all.HasFlag(InvalidationKind.Composite), Is.True);
        }

        [Test]
        public void Compose_via_or() {
            var combined = InvalidationKind.Style | InvalidationKind.Layout;
            Assert.That(combined.HasFlag(InvalidationKind.Style), Is.True);
            Assert.That(combined.HasFlag(InvalidationKind.Layout), Is.True);
            Assert.That(combined.HasFlag(InvalidationKind.Paint), Is.False);
        }

        [Test]
        public void And_extracts_specific_flag() {
            var combined = InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint;
            Assert.That(combined & InvalidationKind.Layout, Is.EqualTo(InvalidationKind.Layout));
            Assert.That(combined & InvalidationKind.Composite, Is.EqualTo(InvalidationKind.None));
        }

        [Test]
        public void Not_clears_specific_flag() {
            var combined = InvalidationKind.All & ~InvalidationKind.Composite;
            Assert.That(combined.HasFlag(InvalidationKind.Composite), Is.False);
            Assert.That(combined.HasFlag(InvalidationKind.Structure), Is.True);
            Assert.That(combined.HasFlag(InvalidationKind.Style), Is.True);
            Assert.That(combined.HasFlag(InvalidationKind.Layout), Is.True);
            Assert.That(combined.HasFlag(InvalidationKind.Paint), Is.True);
        }
    }
}
