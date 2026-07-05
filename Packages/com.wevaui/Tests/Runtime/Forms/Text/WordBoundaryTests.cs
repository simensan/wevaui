using NUnit.Framework;
using Weva.Forms.Text;

namespace Weva.Tests.Forms.Text {
    // TG-W4-WB — WordBoundary test coverage (W4 phase 1, ROADMAP.md).
    //
    // Boundary rules tested:
    //   1. Skip separator run (spaces, punctuation), then consume word-char run.
    //   2. '_' is a word-char (not a separator).
    //   3. Each CJK flow character is its own word unit.
    //   4. Surrogate-pair codepoints are handled as single codepoints.
    //   5. Edge cases: empty string, at-boundary queries, all-separators.
    [TestFixture]
    public class WordBoundaryTests {
        // ---- PreviousWordBoundary ----

        [Test]
        public void Prev_from_start_returns_0() {
            Assert.That(WordBoundary.PreviousWordBoundary("hello", 0), Is.EqualTo(0));
        }

        [Test]
        public void Prev_from_end_of_word_returns_word_start() {
            // "hello" — from index 5 (end) → 0
            Assert.That(WordBoundary.PreviousWordBoundary("hello", 5), Is.EqualTo(0));
        }

        [Test]
        public void Prev_skips_trailing_space_then_lands_at_word_start() {
            // "hello world " — from index 12 (end, after trailing space)
            // Skip space → land at end of "world" → consume "world" → index 6
            Assert.That(WordBoundary.PreviousWordBoundary("hello world ", 12), Is.EqualTo(6));
        }

        [Test]
        public void Prev_from_mid_word_returns_word_start() {
            // "hello" from index 3 → 0
            Assert.That(WordBoundary.PreviousWordBoundary("hello", 3), Is.EqualTo(0));
        }

        [Test]
        public void Prev_over_punctuation_sequence() {
            // "abc..." from index 6 → skip "..." → "abc" → 0
            Assert.That(WordBoundary.PreviousWordBoundary("abc...", 6), Is.EqualTo(0));
        }

        [Test]
        public void Prev_underscore_is_wordchar() {
            // "foo_bar" from end → 0 (underscore joins the two parts)
            Assert.That(WordBoundary.PreviousWordBoundary("foo_bar", 7), Is.EqualTo(0));
        }

        [Test]
        public void Prev_over_two_words_with_space() {
            // "hello world" from 11 → skip nothing → consume "world" → 6
            Assert.That(WordBoundary.PreviousWordBoundary("hello world", 11), Is.EqualTo(6));
        }

        // ---- NextWordBoundary ----

        [Test]
        public void Next_from_end_returns_length() {
            Assert.That(WordBoundary.NextWordBoundary("hello", 5), Is.EqualTo(5));
        }

        [Test]
        public void Next_from_start_of_word_returns_end_of_word() {
            // "hello world" from 0 → skip nothing → consume "hello" → 5
            Assert.That(WordBoundary.NextWordBoundary("hello world", 0), Is.EqualTo(5));
        }

        [Test]
        public void Next_from_start_of_space_run_skips_space_then_ends_at_word_end() {
            // "hello world" from 5 → skip " " → consume "world" → 11
            Assert.That(WordBoundary.NextWordBoundary("hello world", 5), Is.EqualTo(11));
        }

        [Test]
        public void Next_underscore_is_wordchar() {
            // "foo_bar baz" from 0 → "foo_bar" → 7
            Assert.That(WordBoundary.NextWordBoundary("foo_bar baz", 0), Is.EqualTo(7));
        }

        [Test]
        public void Next_empty_string_returns_0() {
            Assert.That(WordBoundary.NextWordBoundary("", 0), Is.EqualTo(0));
        }

        [Test]
        public void Next_all_separators_returns_length() {
            // "   " from 0 → skip all → length
            Assert.That(WordBoundary.NextWordBoundary("   ", 0), Is.EqualTo(3));
        }

        // ---- CJK word units ----

        [Test]
        public void Cjk_next_advances_one_codepoint_at_a_time() {
            // U+65E5 (日) U+672C (本) U+8A9E (語) — each is its own word unit.
            const string text = "日本語";
            Assert.That(WordBoundary.NextWordBoundary(text, 0), Is.EqualTo(1),
                "First CJK char should be a single word unit");
            Assert.That(WordBoundary.NextWordBoundary(text, 1), Is.EqualTo(2),
                "Second CJK char should be a single word unit");
            Assert.That(WordBoundary.NextWordBoundary(text, 2), Is.EqualTo(3),
                "Third CJK char should be a single word unit");
        }

        [Test]
        public void Cjk_prev_retreats_one_codepoint_at_a_time() {
            const string text = "日本語";
            Assert.That(WordBoundary.PreviousWordBoundary(text, 3), Is.EqualTo(2),
                "Prev from end should land after second CJK char");
            Assert.That(WordBoundary.PreviousWordBoundary(text, 2), Is.EqualTo(1));
            Assert.That(WordBoundary.PreviousWordBoundary(text, 1), Is.EqualTo(0));
        }

        [Test]
        public void Cjk_mixed_with_ascii_stops_at_cjk_boundary() {
            // "abctest日本語" — Ctrl+Right from 0 → "abctest" (7), then next
            // call → "日" (8), etc.
            const string text = "test日本";
            Assert.That(WordBoundary.NextWordBoundary(text, 0), Is.EqualTo(4),
                "ASCII word before CJK ends at the CJK boundary");
            Assert.That(WordBoundary.NextWordBoundary(text, 4), Is.EqualTo(5),
                "First CJK char is one word unit");
            Assert.That(WordBoundary.NextWordBoundary(text, 5), Is.EqualTo(6),
                "Second CJK char is one word unit");
        }

        // ---- IsSeparator helper ----

        [Test]
        public void IsSeparator_space_is_separator() {
            Assert.That(WordBoundary.IsSeparator(' '), Is.True);
        }

        [Test]
        public void IsSeparator_letter_is_not_separator() {
            Assert.That(WordBoundary.IsSeparator('a'), Is.False);
            Assert.That(WordBoundary.IsSeparator('Z'), Is.False);
        }

        [Test]
        public void IsSeparator_underscore_is_not_separator() {
            Assert.That(WordBoundary.IsSeparator('_'), Is.False);
        }

        [Test]
        public void IsSeparator_digit_is_not_separator() {
            Assert.That(WordBoundary.IsSeparator('5'), Is.False);
        }
    }
}
