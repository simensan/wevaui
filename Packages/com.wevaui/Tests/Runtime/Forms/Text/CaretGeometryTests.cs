using NUnit.Framework;
using Weva.Forms.Text;
using Weva.Layout.Text;

namespace Weva.Tests.Forms.Text {
    // TG-W4-CG — CaretGeometry test coverage.
    //
    // All tests use MonoFontMetrics (charWidthEm=0.5) unless stated otherwise.
    // At fontSize=16 each ASCII char measures 0.5 × 16 = 8 px exactly, making
    // expected values trivially computable by hand.
    //
    // Spec: W4 phase 1 (ROADMAP.md):
    //   • CaretXForIndex — caret slot → pixel X
    //   • IndexForX      — pixel X    → nearest caret slot
    //   • Surrogate-pair safety (emoji must not be split)
    [TestFixture]
    public class CaretGeometryTests {
        // MonoFontMetrics default: charWidthEm=0.5, so at 16px each char=8px.
        static readonly IFontMetrics Metrics = new MonoFontMetrics();
        const double Px = 16.0; // fontSize

        // ---- CaretXForIndex ----

        [Test]
        public void CaretX_at_index_0_is_zero() {
            Assert.That(CaretGeometry.CaretXForIndex("hello", 0, Px, Metrics),
                        Is.EqualTo(0.0).Within(1e-9));
        }

        [Test]
        public void CaretX_at_end_equals_total_width() {
            // "hello" = 5 chars × 8px = 40px
            Assert.That(CaretGeometry.CaretXForIndex("hello", 5, Px, Metrics),
                        Is.EqualTo(40.0).Within(1e-9));
        }

        [Test]
        public void CaretX_at_mid_index_equals_partial_width() {
            // "hello"[0..2] = "he" = 2 chars × 8px = 16px
            Assert.That(CaretGeometry.CaretXForIndex("hello", 2, Px, Metrics),
                        Is.EqualTo(16.0).Within(1e-9));
        }

        [Test]
        public void CaretX_empty_string_returns_zero() {
            Assert.That(CaretGeometry.CaretXForIndex("", 0, Px, Metrics),
                        Is.EqualTo(0.0).Within(1e-9));
        }

        [Test]
        public void CaretX_null_text_returns_zero() {
            Assert.That(CaretGeometry.CaretXForIndex(null, 0, Px, Metrics),
                        Is.EqualTo(0.0).Within(1e-9));
        }

        [Test]
        public void CaretX_index_beyond_length_clamps_to_end() {
            // "ab" = 16px; requesting index 999 clamps to length=2.
            Assert.That(CaretGeometry.CaretXForIndex("ab", 999, Px, Metrics),
                        Is.EqualTo(16.0).Within(1e-9));
        }

        [Test]
        public void CaretX_different_fontsize_scales_linearly() {
            // At 10px: "abc" = 3 × 0.5 × 10 = 15px
            Assert.That(CaretGeometry.CaretXForIndex("abc", 3, 10.0, Metrics),
                        Is.EqualTo(15.0).Within(1e-9));
        }

        // ---- IndexForX ----

        [Test]
        public void IndexForX_at_zero_returns_0() {
            Assert.That(CaretGeometry.IndexForX("hello", 0.0, Px, Metrics),
                        Is.EqualTo(0));
        }

        [Test]
        public void IndexForX_beyond_end_returns_length() {
            Assert.That(CaretGeometry.IndexForX("hello", 1000.0, Px, Metrics),
                        Is.EqualTo(5));
        }

        [Test]
        public void IndexForX_at_exact_glyph_boundary_picks_right_slot() {
            // "hello": each char = 8px.  X=8 is exactly at the boundary between
            // slot 1 and slot 2.  Mid-point of glyph 1 is at 4px; X=8 is past
            // the mid-point of glyph 1, so slot 2 wins (right side of 'e').
            Assert.That(CaretGeometry.IndexForX("hello", 8.0, Px, Metrics),
                        Is.EqualTo(1));
        }

        [Test]
        public void IndexForX_mid_glyph_rounds_to_closer_side_left() {
            // "hello": glyph 'h' spans [0,8).  X=3 is left of the mid-point (4px).
            Assert.That(CaretGeometry.IndexForX("hello", 3.0, Px, Metrics),
                        Is.EqualTo(0));
        }

        [Test]
        public void IndexForX_mid_glyph_rounds_to_closer_side_right() {
            // "hello": glyph 'h' spans [0,8).  X=5 is right of the mid-point (4px).
            Assert.That(CaretGeometry.IndexForX("hello", 5.0, Px, Metrics),
                        Is.EqualTo(1));
        }

        [Test]
        public void IndexForX_empty_string_returns_0() {
            Assert.That(CaretGeometry.IndexForX("", 100.0, Px, Metrics),
                        Is.EqualTo(0));
        }

        [Test]
        public void IndexForX_null_text_returns_0() {
            Assert.That(CaretGeometry.IndexForX(null, 100.0, Px, Metrics),
                        Is.EqualTo(0));
        }

        // ---- Round-trip invariant ----

        [Test]
        public void RoundTrip_CaretX_then_IndexForX_recovers_index_at_multiple_positions() {
            const string text = "Hello world";
            // Test at every caret position.  Round-trip must recover the original
            // index when we query exactly the caret X (zero slack because the
            // mid-point rule gives us the slot when X == boundary).
            for (int i = 0; i <= text.Length; i++) {
                double x = CaretGeometry.CaretXForIndex(text, i, Px, Metrics);
                // Querying at boundary X lands between two slots; epsilon bias right
                // to ensure the test doesn't straddle exactly the mid-point.
                // We bias left by a tiny delta so the recovered index is always i.
                int recovered = CaretGeometry.IndexForX(text, x > 0.0 ? x - 1e-10 : 0.0, Px, Metrics);
                Assert.That(recovered, Is.EqualTo(i),
                    $"Round-trip failed at caret index {i}: x={x}, recovered={recovered}");
            }
        }

        // ---- Surrogate-pair safety ----

        [Test]
        public void CaretX_surrogate_pair_emoji_not_split() {
            // U+1F44D (THUMBS UP) encodes as 2 UTF-16 code units (surrogate pair).
            // index=2 would split the pair; the method should snap to index=0 instead.
            string emoji = "\U0001F44D"; // 2 code units
            Assert.That(emoji.Length, Is.EqualTo(2)); // sanity: it IS a surrogate pair
            // Index 1 (inside the surrogate pair) must snap to 0, not produce a
            // broken mid-pair X. We verify snapping by checking the returned X
            // equals CaretXForIndex at index 0.
            double xAt0 = CaretGeometry.CaretXForIndex(emoji, 0, Px, Metrics);
            double xAt1 = CaretGeometry.CaretXForIndex(emoji, 1, Px, Metrics);
            Assert.That(xAt1, Is.EqualTo(xAt0).Within(1e-9),
                "Index inside surrogate pair should snap to the start of the pair");
        }

        [Test]
        public void IndexForX_surrogate_pair_never_returns_mid_pair_index() {
            // "A" + U+1F44D + "B" = A(1) + emoji(2) + B(1) = 4 code units.
            // Valid caret slots: 0, 1, 3, 4 (never 2 which would be mid-emoji).
            string text = "A\U0001F44DB";
            Assert.That(text.Length, Is.EqualTo(4));
            // Query at a range of X positions and ensure we never get index 2.
            for (int xInt = 0; xInt <= 100; xInt++) {
                int idx = CaretGeometry.IndexForX(text, xInt * 0.5, Px, Metrics);
                Assert.That(idx, Is.Not.EqualTo(2),
                    $"IndexForX returned mid-surrogate index 2 at x={xInt * 0.5}");
            }
        }

        [Test]
        public void IsValidCaretIndex_rejects_mid_surrogate() {
            string emoji = "\U0001F44D"; // high+low surrogate
            Assert.That(CaretGeometry.IsValidCaretIndex(emoji, 0), Is.True);
            Assert.That(CaretGeometry.IsValidCaretIndex(emoji, 1), Is.False,
                "Index 1 is inside the surrogate pair and must be invalid");
            Assert.That(CaretGeometry.IsValidCaretIndex(emoji, 2), Is.True);
        }

        [Test]
        public void IsValidCaretIndex_accepts_all_positions_in_ascii() {
            string text = "hello";
            for (int i = 0; i <= text.Length; i++) {
                Assert.That(CaretGeometry.IsValidCaretIndex(text, i), Is.True,
                    $"Index {i} should be valid in ASCII text");
            }
        }
    }
}
