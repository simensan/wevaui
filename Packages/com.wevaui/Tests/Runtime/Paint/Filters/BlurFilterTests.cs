using NUnit.Framework;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class BlurFilterTests {
        [Test]
        public void Construction_stores_radius() {
            var b = new BlurFilter(5);
            Assert.That(b.RadiusPx, Is.EqualTo(5).Within(1e-9));
        }

        [Test]
        public void Negative_radius_clamped_to_zero() {
            var b = new BlurFilter(-3);
            Assert.That(b.RadiusPx, Is.EqualTo(0));
        }

        [Test]
        public void Equality_same_radius() {
            Assert.That(new BlurFilter(5), Is.EqualTo(new BlurFilter(5)));
            Assert.That(new BlurFilter(5).GetHashCode(), Is.EqualTo(new BlurFilter(5).GetHashCode()));
        }

        [Test]
        public void Equality_different_radius_not_equal() {
            Assert.That(new BlurFilter(5), Is.Not.EqualTo(new BlurFilter(6)));
        }

        [Test]
        public void ToText_round_trip() {
            Assert.That(new BlurFilter(5).ToText(), Is.EqualTo("blur(5px)"));
            Assert.That(new BlurFilter(2.5).ToText(), Is.EqualTo("blur(2.5px)"));
        }

        [Test]
        public void Kind_is_blur() {
            Assert.That(new BlurFilter(0).Kind, Is.EqualTo(FilterKind.Blur));
        }
    }
}
