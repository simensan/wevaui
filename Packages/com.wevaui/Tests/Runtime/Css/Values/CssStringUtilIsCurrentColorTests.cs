using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // D8 (CODE_AUDIT_FINDINGS.md): the `currentcolor` token check was duplicated
    // across ShorthandTokens, RawValueParser, BackgroundResolver, ColorResolver
    // (and was the source of the DC2 dead-clause bug). These tests pin the
    // case-insensitivity contract and the exact-match (no substring) contract
    // so a future tweak to the helper can't silently change behaviour at any
    // of the routed sites.
    public class CssStringUtilIsCurrentColorTests {
        [Test]
        public void Matches_lowercase() {
            Assert.That(CssStringUtil.IsCurrentColor("currentcolor"), Is.True);
        }

        [Test]
        public void Matches_camel_case_currentColor() {
            // The historical authored form — and the only one the DC2-removed
            // dead clause covered. Must keep working post-consolidation.
            Assert.That(CssStringUtil.IsCurrentColor("currentColor"), Is.True);
        }

        [Test]
        public void Matches_title_case_CurrentColor() {
            Assert.That(CssStringUtil.IsCurrentColor("CurrentColor"), Is.True);
        }

        [Test]
        public void Matches_uppercase_CURRENTCOLOR() {
            Assert.That(CssStringUtil.IsCurrentColor("CURRENTCOLOR"), Is.True);
        }

        [Test]
        public void Rejects_misspelling_currentcolour() {
            Assert.That(CssStringUtil.IsCurrentColor("currentcolour"), Is.False);
        }

        [Test]
        public void Rejects_hyphenated_current_dash_color() {
            Assert.That(CssStringUtil.IsCurrentColor("current-color"), Is.False);
        }

        [Test]
        public void Rejects_null() {
            Assert.That(CssStringUtil.IsCurrentColor(null), Is.False);
        }

        [Test]
        public void Rejects_empty_string() {
            Assert.That(CssStringUtil.IsCurrentColor(""), Is.False);
        }

        [Test]
        public void Rejects_substring_prefix() {
            // Exact-match — `currentcolor-bg` is not a match even though
            // `currentcolor` is its prefix. (Substring detection lives in
            // BackgroundResolver.ContainsCurrentColor — distinct semantic.)
            Assert.That(CssStringUtil.IsCurrentColor("currentcolor-bg"), Is.False);
        }

        [Test]
        public void Rejects_with_surrounding_whitespace() {
            // Exact-match — callers needing whitespace tolerance use
            // EqualsIgnoreCaseTrimmed (e.g. ColorResolver.TryResolve).
            Assert.That(CssStringUtil.IsCurrentColor(" currentcolor"), Is.False);
            Assert.That(CssStringUtil.IsCurrentColor("currentcolor "), Is.False);
        }
    }
}
