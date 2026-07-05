using NUnit.Framework;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class BrightnessFilterTests {
        [Test]
        public void Construction_stores_amount() {
            var f = new BrightnessFilter(1.5);
            Assert.That(f.Amount, Is.EqualTo(1.5).Within(1e-9));
        }

        [Test]
        public void Negative_clamped_to_zero() {
            Assert.That(new BrightnessFilter(-0.5).Amount, Is.EqualTo(0));
        }

        [Test]
        public void Above_one_allowed() {
            Assert.That(new BrightnessFilter(2.0).Amount, Is.EqualTo(2.0));
        }

        [Test]
        public void Equality_same_amount() {
            Assert.That(new BrightnessFilter(0.5), Is.EqualTo(new BrightnessFilter(0.5)));
        }

        [Test]
        public void ToText_round_trip() {
            Assert.That(new BrightnessFilter(0.5).ToText(), Is.EqualTo("brightness(0.5)"));
        }

        [Test]
        public void Kind_is_brightness() {
            Assert.That(new BrightnessFilter(1.0).Kind, Is.EqualTo(FilterKind.Brightness));
        }
    }
}
