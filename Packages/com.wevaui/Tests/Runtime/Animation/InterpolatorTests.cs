using NUnit.Framework;
using Weva.Animation;
using Weva.Paint;

namespace Weva.Tests.Animation {
    public class InterpolatorTests {
        const double Eps = 1e-6;

        [Test]
        public void Lerp_double_endpoints_and_midpoint() {
            Assert.That(Interpolator.Lerp(0, 10, 0), Is.EqualTo(0));
            Assert.That(Interpolator.Lerp(0, 10, 1), Is.EqualTo(10));
            Assert.That(Interpolator.Lerp(0, 10, 0.5), Is.EqualTo(5));
        }

        [Test]
        public void Lerp_double_clamps_at_endpoints() {
            Assert.That(Interpolator.Lerp(0, 10, -1), Is.EqualTo(0));
            Assert.That(Interpolator.Lerp(0, 10, 2), Is.EqualTo(10));
        }

        [Test]
        public void LerpColor_blends_componentwise_in_linear_space() {
            var a = new LinearColor(0, 0, 0, 0);
            var b = new LinearColor(1, 1, 1, 1);
            var mid = Interpolator.LerpColor(a, b, 0.5);
            Assert.That(mid.R, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(mid.G, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(mid.B, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(mid.A, Is.EqualTo(0.5f).Within(Eps));
        }

        [Test]
        public void LerpColor_endpoints_returned_exactly() {
            var a = new LinearColor(0.1f, 0.2f, 0.3f, 0.4f);
            var b = new LinearColor(0.9f, 0.8f, 0.7f, 0.6f);
            Assert.That(Interpolator.LerpColor(a, b, 0), Is.EqualTo(a));
            Assert.That(Interpolator.LerpColor(a, b, 1), Is.EqualTo(b));
        }

        [Test]
        public void LerpRect_blends_each_component() {
            var a = new Rect(0, 0, 10, 10);
            var b = new Rect(20, 30, 40, 50);
            var mid = Interpolator.LerpRect(a, b, 0.5);
            Assert.That(mid.X, Is.EqualTo(10));
            Assert.That(mid.Y, Is.EqualTo(15));
            Assert.That(mid.Width, Is.EqualTo(25));
            Assert.That(mid.Height, Is.EqualTo(30));
        }

        [Test]
        public void LerpArray_lerps_componentwise() {
            var a = new[] { 0.0, 10.0, 100.0 };
            var b = new[] { 10.0, 30.0, 50.0 };
            var mid = Interpolator.LerpArray(a, b, 0.5);
            Assert.That(mid.Length, Is.EqualTo(3));
            Assert.That(mid[0], Is.EqualTo(5));
            Assert.That(mid[1], Is.EqualTo(20));
            Assert.That(mid[2], Is.EqualTo(75));
        }

        [Test]
        public void IInterpolator_implementations_match_static_helpers() {
            Assert.That(Interpolator.Double.Lerp(2, 6, 0.5), Is.EqualTo(4));
            var c = Interpolator.Color.Lerp(LinearColor.Black, LinearColor.White, 0.5);
            Assert.That(c.R, Is.EqualTo(0.5f).Within(1e-6f));
            var r = Interpolator.RectInterpolator.Lerp(new Rect(0, 0, 0, 0), new Rect(10, 10, 10, 10), 0.5);
            Assert.That(r.X, Is.EqualTo(5));
        }
    }
}
