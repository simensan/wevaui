using NUnit.Framework;
using Weva.Css.Values;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout.Grid {
    // CSS Grid L1 §7.2.2.3: `repeat(auto-fill, <track-list>)` / `auto-fit`
    // compute the number of repetitions from the available inline size divided
    // by the track size (plus gaps). When the track size is a percentage of
    // the container's inline size, the percentage MUST be resolved before the
    // division — historically it was treated as 0, which yielded reps=1 for
    // every percentage-only pattern. Tracks E4 in CSS_COMPLIANCE_ISSUES.md.
    public class GridAutoFillPercentTests {
        static GridTemplate Parse(string s) => GridTrackParser.Parse(s, LengthContext.Default);

        [Test]
        public void Auto_fill_25pct_in_400px_container_produces_four_tracks_E4() {
            var template = Parse("repeat(auto-fill, 25%)");
            var (tracks, _) = GridTrackSizing.MaterializeAutoRepeat(template, containerSize: 400, gap: 0);
            Assert.That(tracks.Length, Is.EqualTo(4));
            // Each track is still a percentage track (size resolution happens
            // later); we only assert the repetition count here.
            for (int i = 0; i < tracks.Length; i++) {
                Assert.That(tracks[i].Kind, Is.EqualTo(GridTrackKind.Percentage));
                Assert.That(tracks[i].Value, Is.EqualTo(25));
            }
        }

        [Test]
        public void Auto_fill_33pct_in_300px_container_produces_three_tracks_E4() {
            var template = Parse("repeat(auto-fill, 33%)");
            var (tracks, _) = GridTrackSizing.MaterializeAutoRepeat(template, containerSize: 300, gap: 0);
            // 33% of 300 = 99; floor((300 + 0) / (99 + 0)) = 3.
            Assert.That(tracks.Length, Is.EqualTo(3));
        }

        [Test]
        public void Auto_fill_25pct_in_indefinite_container_produces_one_track_E4_regression() {
            // When the container's inline size is indefinite (containerSize <= 0
            // by convention in this engine), CSS Grid L1 §7.2.2.3 says
            // auto-fill must produce a single repetition. This pins the
            // existing fallback so the E4 fix doesn't regress it.
            var template = Parse("repeat(auto-fill, 25%)");
            var (tracks, _) = GridTrackSizing.MaterializeAutoRepeat(template, containerSize: 0, gap: 0);
            Assert.That(tracks.Length, Is.EqualTo(1));
        }
    }
}
