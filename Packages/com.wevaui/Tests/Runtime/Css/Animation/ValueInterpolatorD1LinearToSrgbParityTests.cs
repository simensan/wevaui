using System;
using System.Reflection;
using NUnit.Framework;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // D1 (CODE_AUDIT_FINDINGS.md): the sRGB transfer function was inlined in
    // two places — the canonical CssColor.LinearToSrgb(double) and a private
    // ValueInterpolator.LinearToSrgbByte(float) wrapper. After consolidation
    // the wrapper routes through CssColor with a round+clamp; these tests pin
    // that the float-input wrapper still agrees with CssColor.LinearToSrgb × 255
    // at canonical sample points (0, the piecewise breakpoint, 0.5, 1.0) so a
    // future drift in either side fails loudly.
    public class ValueInterpolatorD1LinearToSrgbParityTests {
        static MethodInfo s_LinearToSrgbByte;

        static byte LinearToSrgbByte(float l) {
            if (s_LinearToSrgbByte == null) {
                s_LinearToSrgbByte = typeof(ValueInterpolator).GetMethod(
                    "LinearToSrgbByte",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(s_LinearToSrgbByte, Is.Not.Null,
                    "LinearToSrgbByte renamed or removed — D1 consolidation site moved.");
            }
            return (byte)s_LinearToSrgbByte.Invoke(null, new object[] { l });
        }

        static byte CanonicalByteOf(double linear) {
            double encoded = CssColor.LinearToSrgb(linear) * 255.0;
            int b = (int)Math.Round(encoded);
            if (b < 0) b = 0; if (b > 255) b = 255;
            return (byte)b;
        }

        [Test]
        public void Linear_to_srgb_byte_matches_CssColor_at_zero() {
            Assert.That(LinearToSrgbByte(0f), Is.EqualTo((byte)0));
            Assert.That(LinearToSrgbByte(0f), Is.EqualTo(CanonicalByteOf(0.0)));
        }

        [Test]
        public void Linear_to_srgb_byte_matches_CssColor_at_one() {
            Assert.That(LinearToSrgbByte(1f), Is.EqualTo((byte)255));
            Assert.That(LinearToSrgbByte(1f), Is.EqualTo(CanonicalByteOf(1.0)));
        }

        [Test]
        public void Linear_to_srgb_byte_matches_CssColor_at_half() {
            // Linear 0.5 → sRGB ≈ 0.7354 → byte 188.
            byte fromWrapper = LinearToSrgbByte(0.5f);
            byte fromCanonical = CanonicalByteOf(0.5);
            Assert.That(fromWrapper, Is.EqualTo(fromCanonical));
            Assert.That(fromWrapper, Is.InRange((byte)185, (byte)190));
        }

        [Test]
        public void Linear_to_srgb_byte_matches_CssColor_at_piecewise_breakpoint() {
            // Just below and above the 0.0031308 piecewise split — exercises both
            // legs of the canonical CssColor.LinearToSrgb.
            Assert.That(LinearToSrgbByte(0.003f), Is.EqualTo(CanonicalByteOf(0.003)));
            Assert.That(LinearToSrgbByte(0.004f), Is.EqualTo(CanonicalByteOf(0.004)));
        }

        [Test]
        public void Linear_to_srgb_byte_clamps_out_of_range_inputs() {
            // CssColor.LinearToSrgb already clamps [0,1]; the wrapper's byte
            // clamp is defensive but verified here so a future refactor that
            // drops one of the two layers still produces in-range bytes.
            Assert.That(LinearToSrgbByte(-1f), Is.EqualTo((byte)0));
            Assert.That(LinearToSrgbByte(2f), Is.EqualTo((byte)255));
        }
    }
}
