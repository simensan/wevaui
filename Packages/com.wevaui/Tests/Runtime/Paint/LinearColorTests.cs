using NUnit.Framework;
using Weva.Css.Values;
using Weva.Paint;

namespace Weva.Tests.Paint {
    public class LinearColorTests {
        [Test]
        public void Construction_stores_components() {
            var c = new LinearColor(0.1f, 0.2f, 0.3f, 0.4f);
            Assert.That(c.R, Is.EqualTo(0.1f));
            Assert.That(c.G, Is.EqualTo(0.2f));
            Assert.That(c.B, Is.EqualTo(0.3f));
            Assert.That(c.A, Is.EqualTo(0.4f));
        }

        [Test]
        public void Equality_is_componentwise() {
            var a = new LinearColor(0.1f, 0.2f, 0.3f, 0.4f);
            var b = new LinearColor(0.1f, 0.2f, 0.3f, 0.4f);
            var c = new LinearColor(0.1f, 0.2f, 0.3f, 0.5f);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a, Is.Not.EqualTo(c));
            Assert.That(a != c, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void ToString_is_invariant_culture() {
            var c = new LinearColor(0.5f, 0.25f, 0.125f, 1f);
            var s = c.ToString();
            Assert.That(s, Does.Contain("0.5"));
            Assert.That(s, Does.Contain("0.25"));
        }

        [Test]
        public void FromCssColor_black_is_origin() {
            var c = LinearColor.FromCssColor(CssColor.FromHex("000", 0));
            Assert.That(c.R, Is.EqualTo(0f));
            Assert.That(c.G, Is.EqualTo(0f));
            Assert.That(c.B, Is.EqualTo(0f));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void FromCssColor_white_is_unit() {
            var c = LinearColor.FromCssColor(CssColor.FromHex("fff", 0));
            Assert.That(c.R, Is.EqualTo(1f));
            Assert.That(c.G, Is.EqualTo(1f));
            Assert.That(c.B, Is.EqualTo(1f));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void FromCssColor_mid_grey_is_in_linear_space() {
            // sRGB 128/255 -> linear ~0.2159 via the IEC 61966-2-1 piecewise curve.
            var c = LinearColor.FromCssColor(CssColor.FromHex("808080", 0));
            Assert.That(c.R, Is.EqualTo(0.2159f).Within(0.005f));
            Assert.That(c.G, Is.EqualTo(0.2159f).Within(0.005f));
            Assert.That(c.B, Is.EqualTo(0.2159f).Within(0.005f));
        }

        [Test]
        public void FromCssColor_alpha_is_not_gamma_affected() {
            // Alpha is a coverage value, not a colorimetric one — it must pass through untouched.
            var src = CssColor.FromRgb(128, 128, 128, 0.5, false, "rgba(128,128,128,0.5)");
            var c = LinearColor.FromCssColor(src);
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test]
        public void Premultiplied_multiplies_rgb_by_alpha_and_keeps_alpha() {
            var c = new LinearColor(1f, 0.5f, 0.25f, 0.5f);
            var p = c.Premultiplied();
            Assert.That(p.R, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(p.G, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(p.B, Is.EqualTo(0.125f).Within(1e-6f));
            Assert.That(p.A, Is.EqualTo(0.5f));
        }

        [Test]
        public void Premultiplied_with_zero_alpha_is_zero() {
            var c = new LinearColor(1f, 1f, 1f, 0f);
            var p = c.Premultiplied();
            Assert.That(p.R, Is.EqualTo(0f));
            Assert.That(p.G, Is.EqualTo(0f));
            Assert.That(p.B, Is.EqualTo(0f));
            Assert.That(p.A, Is.EqualTo(0f));
        }

        [Test]
        public void Static_constants_match_construction() {
            Assert.That(LinearColor.Black, Is.EqualTo(new LinearColor(0, 0, 0, 1)));
            Assert.That(LinearColor.White, Is.EqualTo(new LinearColor(1, 1, 1, 1)));
            Assert.That(LinearColor.Transparent, Is.EqualTo(new LinearColor(0, 0, 0, 0)));
        }
    }
}
