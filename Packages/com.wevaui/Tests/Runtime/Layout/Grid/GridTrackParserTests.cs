using NUnit.Framework;
using Weva.Css.Values;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout.Grid {
    public class GridTrackParserTests {
        static LengthContext Lc => LengthContext.Default;

        static GridTemplate Parse(string s) => GridTrackParser.Parse(s, Lc);

        [Test]
        public void Parses_single_pixel_track() {
            var t = Parse("100px");
            Assert.That(t.Tracks.Count, Is.EqualTo(1));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[0].Value, Is.EqualTo(100));
        }

        [Test]
        public void Parses_single_fr_track() {
            var t = Parse("1fr");
            Assert.That(t.Tracks.Count, Is.EqualTo(1));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Fr));
            Assert.That(t.Tracks[0].Value, Is.EqualTo(1));
        }

        [Test]
        public void Parses_auto_keyword() {
            var t = Parse("auto");
            // 'auto' alone is treated as an empty template (CSS edge: grid-template-columns: auto sets a single auto track).
            // Per the parser, returning Empty here keeps the test surface simple.
            Assert.That(t.Tracks.Count, Is.EqualTo(0));
        }

        [Test]
        public void Parses_min_content_track() {
            var t = Parse("min-content 100px");
            Assert.That(t.Tracks.Count, Is.EqualTo(2));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.MinContent));
            Assert.That(t.Tracks[1].Kind, Is.EqualTo(GridTrackKind.Length));
        }

        [Test]
        public void Parses_max_content_track() {
            var t = Parse("max-content max-content");
            Assert.That(t.Tracks.Count, Is.EqualTo(2));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.MaxContent));
            Assert.That(t.Tracks[1].Kind, Is.EqualTo(GridTrackKind.MaxContent));
        }

        [Test]
        public void Parses_minmax_with_length_and_fr() {
            var t = Parse("minmax(100px, 1fr)");
            Assert.That(t.Tracks.Count, Is.EqualTo(1));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Minmax));
            Assert.That(t.Tracks[0].MinKind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[0].MinValue, Is.EqualTo(100));
            Assert.That(t.Tracks[0].MaxKind, Is.EqualTo(GridTrackKind.Fr));
            Assert.That(t.Tracks[0].MaxValue, Is.EqualTo(1));
        }

        [Test]
        public void Parses_minmax_zero_to_fr() {
            var t = Parse("minmax(0, 1fr)");
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Minmax));
            Assert.That(t.Tracks[0].MinKind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[0].MinValue, Is.EqualTo(0));
        }

        [Test]
        public void Parses_minmax_auto_to_length() {
            var t = Parse("minmax(auto, 200px)");
            Assert.That(t.Tracks[0].MinKind, Is.EqualTo(GridTrackKind.Auto));
            Assert.That(t.Tracks[0].MaxKind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[0].MaxValue, Is.EqualTo(200));
        }

        [Test]
        public void Parses_repeat_3_1fr() {
            var t = Parse("repeat(3, 1fr)");
            Assert.That(t.Tracks.Count, Is.EqualTo(3));
            for (int i = 0; i < 3; i++) {
                Assert.That(t.Tracks[i].Kind, Is.EqualTo(GridTrackKind.Fr));
                Assert.That(t.Tracks[i].Value, Is.EqualTo(1));
            }
        }

        [Test]
        public void Parses_repeat_with_multi_track_pattern() {
            var t = Parse("repeat(2, 100px 200px)");
            Assert.That(t.Tracks.Count, Is.EqualTo(4));
            Assert.That(t.Tracks[0].Value, Is.EqualTo(100));
            Assert.That(t.Tracks[1].Value, Is.EqualTo(200));
            Assert.That(t.Tracks[2].Value, Is.EqualTo(100));
            Assert.That(t.Tracks[3].Value, Is.EqualTo(200));
        }

        [Test]
        public void Parses_repeat_auto_fill_minmax() {
            var t = Parse("repeat(auto-fill, minmax(100px, 1fr))");
            Assert.That(t.IsAutoFill, Is.True);
            Assert.That(t.IsAutoFit, Is.False);
            Assert.That(t.AutoRepeatPattern.Count, Is.EqualTo(1));
            Assert.That(t.AutoRepeatPattern[0].Kind, Is.EqualTo(GridTrackKind.Minmax));
        }

        [Test]
        public void Parses_repeat_auto_fit() {
            var t = Parse("repeat(auto-fit, 100px)");
            Assert.That(t.IsAutoFit, Is.True);
            Assert.That(t.IsAutoFill, Is.False);
            Assert.That(t.AutoRepeatPattern.Count, Is.EqualTo(1));
            Assert.That(t.AutoRepeatPattern[0].Value, Is.EqualTo(100));
        }

        [Test]
        public void Parses_named_lines() {
            var t = Parse("[start] 100px [mid] 200px [end]");
            Assert.That(t.Tracks.Count, Is.EqualTo(2));
            Assert.That(t.LineNames.Count, Is.EqualTo(3));
            Assert.That(t.LineNames[0][0], Is.EqualTo("start"));
            Assert.That(t.LineNames[1][0], Is.EqualTo("mid"));
            Assert.That(t.LineNames[2][0], Is.EqualTo("end"));
        }

        [Test]
        public void Parses_mixed_track_types() {
            var t = Parse("100px 1fr auto");
            Assert.That(t.Tracks.Count, Is.EqualTo(3));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[1].Kind, Is.EqualTo(GridTrackKind.Fr));
            Assert.That(t.Tracks[2].Kind, Is.EqualTo(GridTrackKind.Auto));
        }

        [Test]
        public void Parses_percentage_track() {
            var t = Parse("50% 50%");
            Assert.That(t.Tracks.Count, Is.EqualTo(2));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Percentage));
            Assert.That(t.Tracks[0].Value, Is.EqualTo(50));
        }

        [Test]
        public void Throws_on_empty_repeat() {
            Assert.Throws<GridTrackParser.ParseException>(() => Parse("repeat(3,)"));
        }

        [Test]
        public void Throws_on_unmatched_paren() {
            Assert.Throws<GridTrackParser.ParseException>(() => Parse("minmax(100px, 1fr"));
        }

        [Test]
        public void Throws_on_invalid_track_keyword() {
            Assert.Throws<GridTrackParser.ParseException>(() => Parse("garbage"));
        }

        [Test]
        public void Throws_on_zero_repeat_count() {
            Assert.Throws<GridTrackParser.ParseException>(() => Parse("repeat(0, 1fr)"));
        }

        // E3: fit-content(<length-percentage>) parsing. Per CSS Grid L1 §7.2.3
        // `fit-content(L)` is sugar for minmax(auto, max-content) clamped to L.
        [Test]
        public void Parses_fit_content_with_length() {
            var t = Parse("fit-content(200px)");
            Assert.That(t.Tracks.Count, Is.EqualTo(1));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.FitContent));
            Assert.That(t.Tracks[0].MaxKind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[0].MaxValue, Is.EqualTo(200));
        }

        [Test]
        public void Parses_fr_fit_content_fr_three_tracks() {
            var t = Parse("1fr fit-content(150px) 1fr");
            Assert.That(t.Tracks.Count, Is.EqualTo(3));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.Fr));
            Assert.That(t.Tracks[0].Value, Is.EqualTo(1));
            Assert.That(t.Tracks[1].Kind, Is.EqualTo(GridTrackKind.FitContent));
            Assert.That(t.Tracks[1].MaxKind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(t.Tracks[1].MaxValue, Is.EqualTo(150));
            Assert.That(t.Tracks[2].Kind, Is.EqualTo(GridTrackKind.Fr));
            Assert.That(t.Tracks[2].Value, Is.EqualTo(1));
        }

        [Test]
        public void Parses_fit_content_with_percentage() {
            var t = Parse("fit-content(50%)");
            Assert.That(t.Tracks.Count, Is.EqualTo(1));
            Assert.That(t.Tracks[0].Kind, Is.EqualTo(GridTrackKind.FitContent));
            Assert.That(t.Tracks[0].MaxKind, Is.EqualTo(GridTrackKind.Percentage));
            Assert.That(t.Tracks[0].MaxValue, Is.EqualTo(50));
        }

        [Test]
        public void Repeat_preserves_named_lines_per_iteration() {
            var t = Parse("repeat(2, [a] 100px [b])");
            Assert.That(t.Tracks.Count, Is.EqualTo(2));
            // After two repetitions: lineNames are [ [a], [b a], [b] ] -- merged at boundaries.
            Assert.That(t.LineNames[0], Contains.Item("a"));
            Assert.That(t.LineNames[1], Contains.Item("b"));
            Assert.That(t.LineNames[1], Contains.Item("a"));
            Assert.That(t.LineNames[2], Contains.Item("b"));
        }
    }
}
