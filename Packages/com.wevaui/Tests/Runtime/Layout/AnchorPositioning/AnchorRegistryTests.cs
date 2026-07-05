using NUnit.Framework;
using Weva.Layout.AnchorPositioning;
using Weva.Layout.Boxes;

namespace Weva.Tests.Layout.AnchorPositioning {
    public class AnchorRegistryTests {
        static BlockBox NewBox(double x, double y, double w, double h) {
            var b = new BlockBox();
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            return b;
        }

        [Test]
        public void Register_then_resolve_returns_box() {
            var reg = new AnchorRegistry();
            var b = NewBox(10, 20, 30, 40);
            reg.Register("--foo", b);
            Assert.That(reg.TryResolve("--foo", out var entry), Is.True);
            Assert.That(entry.Anchor, Is.SameAs(b));
        }

        [Test]
        public void Resolve_returns_false_for_missing_name() {
            var reg = new AnchorRegistry();
            Assert.That(reg.TryResolve("--missing", out _), Is.False);
        }

        [Test]
        public void Last_registration_with_same_name_wins() {
            var reg = new AnchorRegistry();
            var a = NewBox(0, 0, 10, 10);
            var b = NewBox(50, 50, 10, 10);
            reg.Register("--x", a);
            reg.Register("--x", b);
            reg.TryResolve("--x", out var entry);
            Assert.That(entry.Anchor, Is.SameAs(b));
        }

        [Test]
        public void Clear_empties_registry() {
            var reg = new AnchorRegistry();
            reg.Register("--x", NewBox(0, 0, 10, 10));
            reg.Clear();
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(reg.TryResolve("--x", out _), Is.False);
        }

        [Test]
        public void Empty_or_null_input_is_no_op() {
            var reg = new AnchorRegistry();
            reg.Register(null, NewBox(0, 0, 10, 10));
            reg.Register("--x", null);
            Assert.That(reg.Count, Is.EqualTo(0));
        }
    }
}
