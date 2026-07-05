using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // Pins the DC2 contract: IsCurrentColor must match every casing of
    // "currentcolor" via the single OrdinalIgnoreCase clause. The previous
    // second Ordinal("currentColor") clause was unreachable; this test
    // guards against any future regression that would re-introduce a
    // case-sensitive mismatch.
    public class ShorthandTokensCurrentColorTests {
        [Test]
        public void IsCurrentColor_matches_lowercase() {
            Assert.That(ShorthandTokens.IsCurrentColor("currentcolor"), Is.True);
        }

        [Test]
        public void IsCurrentColor_matches_camelcase() {
            Assert.That(ShorthandTokens.IsCurrentColor("currentColor"), Is.True);
        }

        [Test]
        public void IsCurrentColor_matches_uppercase() {
            Assert.That(ShorthandTokens.IsCurrentColor("CURRENTCOLOR"), Is.True);
        }

        [Test]
        public void IsCurrentColor_matches_titlecase() {
            Assert.That(ShorthandTokens.IsCurrentColor("CurrentColor"), Is.True);
        }

        [Test]
        public void IsCurrentColor_matches_mixed_case() {
            Assert.That(ShorthandTokens.IsCurrentColor("CurReNtCoLoR"), Is.True);
        }

        [Test]
        public void IsCurrentColor_rejects_british_spelling() {
            Assert.That(ShorthandTokens.IsCurrentColor("currentcolour"), Is.False);
        }

        [Test]
        public void IsCurrentColor_rejects_space_separated() {
            Assert.That(ShorthandTokens.IsCurrentColor("current color"), Is.False);
        }

        [Test]
        public void IsCurrentColor_rejects_unrelated_color() {
            Assert.That(ShorthandTokens.IsCurrentColor("red"), Is.False);
        }

        [Test]
        public void IsCurrentColor_rejects_empty_and_null() {
            Assert.That(ShorthandTokens.IsCurrentColor(""), Is.False);
            Assert.That(ShorthandTokens.IsCurrentColor(null), Is.False);
        }
    }
}
