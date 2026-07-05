using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class DropShadowFilterTests {
        [Test]
        public void Construction_stores_all_fields() {
            var f = new DropShadowFilter(2, 4, 8, LinearColor.Black);
            Assert.That(f.OffsetX, Is.EqualTo(2));
            Assert.That(f.OffsetY, Is.EqualTo(4));
            Assert.That(f.BlurRadius, Is.EqualTo(8));
            Assert.That(f.Color, Is.EqualTo(LinearColor.Black));
        }

        [Test]
        public void Negative_blur_clamped_to_zero() {
            var f = new DropShadowFilter(2, 4, -3, LinearColor.Black);
            Assert.That(f.BlurRadius, Is.EqualTo(0));
        }

        [Test]
        public void Equality_all_fields_match() {
            var a = new DropShadowFilter(2, 4, 8, LinearColor.Black);
            var b = new DropShadowFilter(2, 4, 8, LinearColor.Black);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Equality_color_difference_breaks_equality() {
            var a = new DropShadowFilter(2, 4, 8, LinearColor.Black);
            var b = new DropShadowFilter(2, 4, 8, LinearColor.White);
            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void ToText_includes_offsets_and_blur() {
            var f = new DropShadowFilter(2, 4, 8, LinearColor.Black);
            string text = f.ToText();
            Assert.That(text, Does.StartWith("drop-shadow("));
            Assert.That(text, Does.Contain("2"));
            Assert.That(text, Does.Contain("4"));
            Assert.That(text, Does.Contain("8"));
        }

        [Test]
        public void Kind_is_drop_shadow() {
            Assert.That(new DropShadowFilter(0, 0, 0, LinearColor.Black).Kind, Is.EqualTo(FilterKind.DropShadow));
        }
    }
}
