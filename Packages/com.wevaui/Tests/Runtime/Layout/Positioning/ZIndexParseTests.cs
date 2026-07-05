using NUnit.Framework;
using Weva.Layout.Positioning;

namespace Weva.Tests.Layout.Positioning {
    public class ZIndexParseTests {
        [Test]
        public void Default_and_auto_return_null() {
            Assert.That(PositionedExtensions.ParseZIndex(null), Is.Null);
            Assert.That(PositionedExtensions.ParseZIndex(""), Is.Null);
            Assert.That(PositionedExtensions.ParseZIndex("auto"), Is.Null);
            Assert.That(PositionedExtensions.ParseZIndex("AUTO"), Is.Null);
        }

        [Test]
        public void Positive_integer_parses() {
            Assert.That(PositionedExtensions.ParseZIndex("5"), Is.EqualTo(5));
            Assert.That(PositionedExtensions.ParseZIndex("0"), Is.EqualTo(0));
            Assert.That(PositionedExtensions.ParseZIndex("100"), Is.EqualTo(100));
        }

        [Test]
        public void Negative_integer_parses() {
            Assert.That(PositionedExtensions.ParseZIndex("-1"), Is.EqualTo(-1));
            Assert.That(PositionedExtensions.ParseZIndex("-9999"), Is.EqualTo(-9999));
        }

        [Test]
        public void Very_large_integers_clamp_or_accept() {
            // Accept up to int.MaxValue/MinValue range; values beyond clamp.
            Assert.That(PositionedExtensions.ParseZIndex("2147483647"), Is.EqualTo(int.MaxValue));
            Assert.That(PositionedExtensions.ParseZIndex("-2147483648"), Is.EqualTo(int.MinValue));
            Assert.That(PositionedExtensions.ParseZIndex("999999999999"), Is.EqualTo(int.MaxValue));
        }
    }
}
