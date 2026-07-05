using NUnit.Framework;
using Weva.Layout.Scrolling;

namespace Weva.Tests.Layout.Scrolling {
    // Audit L14: ScrollMath.Clamp(NaN, ...) is neither < min nor > max, so a
    // NaN scroll offset (bad delta / animated value) used to pass straight
    // through into scroll state. It now clamps to the start.
    public class ScrollMathClampTests {
        [Test]
        public void Nan_value_clamps_to_min_not_propagates() {
            double r = ScrollMath.Clamp(double.NaN, 0, 500);
            Assert.That(double.IsNaN(r), Is.False, "a NaN scroll offset must not pass through Clamp");
            Assert.That(r, Is.EqualTo(0));
        }

        [Test]
        public void Finite_clamping_unchanged() {
            Assert.That(ScrollMath.Clamp(250, 0, 500), Is.EqualTo(250), "in range");
            Assert.That(ScrollMath.Clamp(-10, 0, 500), Is.EqualTo(0), "below min");
            Assert.That(ScrollMath.Clamp(900, 0, 500), Is.EqualTo(500), "above max");
            Assert.That(ScrollMath.Clamp(50, 100, 0), Is.EqualTo(100), "inverted range collapses to min");
        }
    }
}
