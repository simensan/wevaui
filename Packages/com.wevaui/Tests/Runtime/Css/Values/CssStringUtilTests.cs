using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // Hot-path string helpers. The win is allocation-free predicates that
    // replace the pattern `s.Trim().ToLowerInvariant() == "literal"` which
    // allocates two strings per call. These tests lock in the case-folding
    // and trimming semantics so resolvers can't regress.
    public class CssStringUtilTests {
        [Test]
        public void EqualsIgnoreCase_matches_same_case() {
            Assert.That(CssStringUtil.EqualsIgnoreCase("none", "none"), Is.True);
        }

        [Test]
        public void EqualsIgnoreCase_matches_mixed_case() {
            Assert.That(CssStringUtil.EqualsIgnoreCase("None", "none"), Is.True);
            Assert.That(CssStringUtil.EqualsIgnoreCase("NONE", "none"), Is.True);
            Assert.That(CssStringUtil.EqualsIgnoreCase("nOnE", "none"), Is.True);
        }

        [Test]
        public void EqualsIgnoreCase_rejects_different_string() {
            Assert.That(CssStringUtil.EqualsIgnoreCase("solid", "none"), Is.False);
        }

        [Test]
        public void EqualsIgnoreCase_rejects_substring() {
            Assert.That(CssStringUtil.EqualsIgnoreCase("nones", "none"), Is.False);
            Assert.That(CssStringUtil.EqualsIgnoreCase("non", "none"), Is.False);
        }

        [Test]
        public void EqualsIgnoreCase_handles_null() {
            Assert.That(CssStringUtil.EqualsIgnoreCase(null, "none"), Is.False);
            Assert.That(CssStringUtil.EqualsIgnoreCase("none", null), Is.False);
            Assert.That(CssStringUtil.EqualsIgnoreCase(null, null), Is.True);
        }

        [Test]
        public void EqualsIgnoreCaseTrimmed_strips_leading_and_trailing_whitespace() {
            Assert.That(CssStringUtil.EqualsIgnoreCaseTrimmed("  none  ", "none"), Is.True);
            Assert.That(CssStringUtil.EqualsIgnoreCaseTrimmed("\tnone\n", "none"), Is.True);
            Assert.That(CssStringUtil.EqualsIgnoreCaseTrimmed("None ", "none"), Is.True);
        }

        [Test]
        public void EqualsIgnoreCaseTrimmed_rejects_with_internal_whitespace() {
            // "no ne" trimmed is "no ne" — different length than "none".
            Assert.That(CssStringUtil.EqualsIgnoreCaseTrimmed("no ne", "none"), Is.False);
        }

        [Test]
        public void StartsWithIgnoreCase_matches_prefix() {
            Assert.That(CssStringUtil.StartsWithIgnoreCase("rgba(255,0,0,1)", "rgba("), Is.True);
            Assert.That(CssStringUtil.StartsWithIgnoreCase("RGB(0,0,0)", "rgb("), Is.True);
        }

        [Test]
        public void StartsWithIgnoreCase_rejects_when_too_short() {
            Assert.That(CssStringUtil.StartsWithIgnoreCase("rg", "rgb("), Is.False);
        }

        [Test]
        public void IsCssColorFunctionPrefix_recognizes_rgb_and_friends() {
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("rgb(0,0,0)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("rgba(0,0,0,1)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("hsl(0deg, 0%, 0%)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("hsla(0deg, 0%, 0%, 1)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("oklab(50% 0 0)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("oklch(50% 0 0)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("color(srgb 1 0 0)"), Is.True);
        }

        [Test]
        public void IsCssColorFunctionPrefix_recognizes_hex() {
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("#ff0000"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("#fff"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("  #abc"), Is.True);
        }

        [Test]
        public void IsCssColorFunctionPrefix_rejects_named_colors_and_keywords() {
            // Named colors and keywords go through CssColor.TryFromName, not
            // through this prefix check.
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("red"), Is.False);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("transparent"), Is.False);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("currentcolor"), Is.False);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("12px"), Is.False);
        }

        [Test]
        public void IsCssColorFunctionPrefix_handles_case_insensitivity() {
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("RGBA(0,0,0,1)"), Is.True);
            Assert.That(CssStringUtil.IsCssColorFunctionPrefix("Rgb(0,0,0)"), Is.True);
        }

        [Test]
        public void ToLowerAscii_handles_ascii_only() {
            Assert.That(CssStringUtil.ToLowerAscii('A'), Is.EqualTo('a'));
            Assert.That(CssStringUtil.ToLowerAscii('Z'), Is.EqualTo('z'));
            Assert.That(CssStringUtil.ToLowerAscii('a'), Is.EqualTo('a'));
            Assert.That(CssStringUtil.ToLowerAscii('1'), Is.EqualTo('1'));
            Assert.That(CssStringUtil.ToLowerAscii('-'), Is.EqualTo('-'));
        }

        [Test]
        public void IsAsciiWhitespace_recognizes_css_whitespace_set() {
            Assert.That(CssStringUtil.IsAsciiWhitespace(' '), Is.True);
            Assert.That(CssStringUtil.IsAsciiWhitespace('\t'), Is.True);
            Assert.That(CssStringUtil.IsAsciiWhitespace('\n'), Is.True);
            Assert.That(CssStringUtil.IsAsciiWhitespace('\r'), Is.True);
            Assert.That(CssStringUtil.IsAsciiWhitespace('\f'), Is.True);
            Assert.That(CssStringUtil.IsAsciiWhitespace('a'), Is.False);
            Assert.That(CssStringUtil.IsAsciiWhitespace('0'), Is.False);
        }

        [Test]
        public void ToLowerInvariantOrSame_returns_same_instance_when_already_lower() {
            // Critical for the alloc-free fast path: if the input is
            // already-lowercase, ToLowerInvariantOrSame must return the
            // SAME reference, not an equal copy.
            string input = "block";
            Assert.That(CssStringUtil.ToLowerInvariantOrSame(input), Is.SameAs(input));
        }

        [Test]
        public void ToLowerInvariantOrSame_lowercases_when_uppercase_present() {
            Assert.That(CssStringUtil.ToLowerInvariantOrSame("Block"), Is.EqualTo("block"));
            Assert.That(CssStringUtil.ToLowerInvariantOrSame("BLOCK"), Is.EqualTo("block"));
            Assert.That(CssStringUtil.ToLowerInvariantOrSame("BlOcK"), Is.EqualTo("block"));
        }

        [Test]
        public void ToLowerInvariantOrSame_passes_through_non_ascii_uppercase_unchanged() {
            // Non-ASCII chars are NOT detected as uppercase; the function
            // returns the same instance. This matches the existing CSS
            // keyword semantics (all CSS keywords are ASCII).
            string input = "ÄÖÜ";
            Assert.That(CssStringUtil.ToLowerInvariantOrSame(input), Is.SameAs(input));
        }

        [Test]
        public void ToLowerInvariantOrSame_passes_through_digits_and_punctuation() {
            string input = "1.5em";
            Assert.That(CssStringUtil.ToLowerInvariantOrSame(input), Is.SameAs(input));
            input = "rgba(0,0,0,1)";
            Assert.That(CssStringUtil.ToLowerInvariantOrSame(input), Is.SameAs(input));
        }

        [Test]
        public void ToLowerInvariantOrSame_handles_null_and_empty() {
            Assert.That(CssStringUtil.ToLowerInvariantOrSame(null), Is.Null);
            string empty = "";
            Assert.That(CssStringUtil.ToLowerInvariantOrSame(empty), Is.SameAs(empty));
        }
    }
}
