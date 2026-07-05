using NUnit.Framework;
using Weva.Forms.Text;
using Weva.Layout.Text;

namespace Weva.Tests.Forms.Text {
    // TG-W4-LN — LineCaretNavigator + MultiLineCaretNavigator tests (W4 phase 1).
    //
    // Uses MonoFontMetrics (charWidthEm=0.5) at fontSize=16 so each ASCII char
    // is exactly 8px.  Line arrays are constructed by hand so there is no
    // dependency on the layout engine.
    //
    // Goal-column scenarios verified:
    //   1. Up/down preserves preferred X across short lines.
    //   2. Preferred X is captured lazily on the first vertical move.
    //   3. Moving up from the first line clamps to start.
    //   4. Moving down from the last line clamps to end.
    //   5. Single-line navigator (LineCaretNavigator) NavigateTo round-trip.
    [TestFixture]
    public class LineCaretNavigatorTests {
        static readonly IFontMetrics Metrics = new MonoFontMetrics();
        const double Px = 16.0;

        // ---- LineCaretNavigator (single navigator, stateful goal X) ----

        [Test]
        public void Navigator_NavigateTo_picks_closest_slot_on_target_line() {
            // Source line: "hello world" (11 chars, 88px).  Caret at index 5 (X=40).
            // Target line: "abc" (3 chars, 24px).  X=40 > line width → clamp to 3.
            var nav = new LineCaretNavigator(Metrics, Px);
            nav.UpdatePreferredX("hello world", 5); // X=40
            int result = nav.NavigateTo("hello world", 5, "abc");
            Assert.That(result, Is.EqualTo(3), "X=40 past end of 'abc' → end of line");
        }

        [Test]
        public void Navigator_NavigateTo_lands_mid_line_when_x_fits() {
            // Source: caret at index 3 of "hello world" → X=24.
            // Target: "hello world" (same line for symmetry) → X=24 → index 3.
            var nav = new LineCaretNavigator(Metrics, Px);
            nav.UpdatePreferredX("hello world", 3); // X=24
            int result = nav.NavigateTo("hello world", 3, "hello world");
            Assert.That(result, Is.EqualTo(3), "Same line, same X should recover same index");
        }

        [Test]
        public void Navigator_preferred_x_captured_lazily_on_first_navigate() {
            // Don't call UpdatePreferredX — the first NavigateTo should capture it.
            var nav = new LineCaretNavigator(Metrics, Px);
            Assert.That(nav.HasPreferredX, Is.False);
            // Source line "hello" caret@2 → X=16.  Target "hi" (2 chars, 16px).
            // X=16 is at the exact end of "hi" → index 2.
            int result = nav.NavigateTo("hello", 2, "hi");
            Assert.That(nav.HasPreferredX, Is.True, "HasPreferredX must be set after first navigate");
            Assert.That(result, Is.EqualTo(2));
        }

        [Test]
        public void Navigator_preferred_x_persists_across_multiple_navigates() {
            var nav = new LineCaretNavigator(Metrics, Px);
            nav.UpdatePreferredX("long line text", 6); // X = 6×8 = 48px
            // Navigate to a short line — gets clamped.
            int res1 = nav.NavigateTo("long line text", 6, "hi");
            Assert.That(res1, Is.EqualTo(2), "Clamps to end of short 'hi'");
            // Navigate back to original line — preferred X (48px) still active.
            int res2 = nav.NavigateTo("hi", 2, "long line text");
            Assert.That(res2, Is.EqualTo(6), "Returns to original goal column");
        }

        [Test]
        public void Navigator_clear_preferred_x_makes_it_stale() {
            var nav = new LineCaretNavigator(Metrics, Px);
            nav.UpdatePreferredX("hello world", 8); // X=64
            Assert.That(nav.HasPreferredX, Is.True);
            nav.ClearPreferredX();
            Assert.That(nav.HasPreferredX, Is.False);
        }

        // ---- MultiLineCaretNavigator ----

        // Helper: build a 3-line layout for "abc\nde\nhello".
        // Offsets: line0="abc" @0, line1="de" @4, line2="hello" @7.
        static LineBox[] ThreeLines() => new[] {
            new LineBox("abc",   0),
            new LineBox("de",    4),   // offset=4 because of the '\n' at index 3
            new LineBox("hello", 7),   // offset=7 because of the '\n' at index 6
        };

        [Test]
        public void MoveDown_from_first_line_lands_on_second() {
            // "abc" caret@2 → X=16.  Move down to "de" (16px = 2chars×8px).
            // X=16 >= width of "de" (16px) → clamp to end = 2.
            // Global index: line1.Offset + 2 = 4 + 2 = 6.
            var nav = new MultiLineCaretNavigator(Metrics, Px);
            var lines = ThreeLines();
            int result = nav.MoveDown(2, lines); // globalIndex=2 is "abc"[2]
            Assert.That(result, Is.EqualTo(6));
        }

        [Test]
        public void MoveUp_from_last_line_lands_on_second_line() {
            // "hello" caret@3 → X=24.  Move up to "de" (2 chars, 16px).
            // X=24 > 16 → clamp to end = 2.
            // Global: line1.Offset + 2 = 6.
            var nav = new MultiLineCaretNavigator(Metrics, Px);
            var lines = ThreeLines();
            // globalIndex 10 = line2.Offset(7) + col(3)
            int result = nav.MoveUp(10, lines);
            Assert.That(result, Is.EqualTo(6));
        }

        [Test]
        public void MoveUp_from_first_line_clamps_to_start() {
            var nav = new MultiLineCaretNavigator(Metrics, Px);
            var lines = ThreeLines();
            // globalIndex=2 is on line0.  Move up = clamp to start of line0.
            int result = nav.MoveUp(2, lines);
            Assert.That(result, Is.EqualTo(0));
        }

        [Test]
        public void MoveDown_from_last_line_clamps_to_end() {
            var nav = new MultiLineCaretNavigator(Metrics, Px);
            var lines = ThreeLines();
            // globalIndex=9 = line2.Offset(7)+col(2).  Move down = clamp to end of line2.
            int result = nav.MoveDown(9, lines);
            Assert.That(result, Is.EqualTo(12)); // 7 + "hello".Length(5)
        }

        [Test]
        public void Goal_column_preserved_through_short_line() {
            // Pattern: down to short line, up back to long line.
            // line0="hello world" (11 chars, 88px) @0
            // line1="hi"          (2 chars,  16px) @12
            // line2="hello world" (11 chars, 88px) @15
            var lines = new[] {
                new LineBox("hello world", 0),
                new LineBox("hi",          12),
                new LineBox("hello world", 15),
            };
            var nav = new MultiLineCaretNavigator(Metrics, Px);
            // Set caret at col 8 of line0 → X=64.
            int step1 = nav.MoveDown(8, lines);   // line1 → X=64 > 16 → end = index 2 → global=14
            Assert.That(step1, Is.EqualTo(14));
            int step2 = nav.MoveDown(step1, lines); // line2 → X=64 → col 8 → global=15+8=23
            Assert.That(step2, Is.EqualTo(23));
        }

        [Test]
        public void InvalidatePreferredX_resets_state() {
            var nav = new MultiLineCaretNavigator(Metrics, Px);
            var lines = ThreeLines();
            nav.MoveDown(2, lines); // sets preferredX = 16
            Assert.That(nav.HasPreferredX, Is.True);
            nav.InvalidatePreferredX();
            Assert.That(nav.HasPreferredX, Is.False);
        }
    }
}
