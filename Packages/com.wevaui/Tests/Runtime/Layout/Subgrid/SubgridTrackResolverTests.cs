using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Grid;
using Weva.Layout.Subgrid;

namespace Weva.Tests.Layout.Subgrid {
    public class SubgridTrackResolverTests {
        [Test]
        public void IsSubgridKeyword_recognises_canonical_form() {
            Assert.That(SubgridTrackResolver.IsSubgridKeyword("subgrid"), Is.True);
            Assert.That(SubgridTrackResolver.IsSubgridKeyword(" SUBGRID "), Is.True);
            Assert.That(SubgridTrackResolver.IsSubgridKeyword("auto"), Is.False);
            Assert.That(SubgridTrackResolver.IsSubgridKeyword(""), Is.False);
            Assert.That(SubgridTrackResolver.IsSubgridKeyword(null), Is.False);
        }

        [Test]
        public void Slice_returns_contiguous_subset_of_parent_tracks() {
            var parent = new GridTrackSize[] {
                GridTrackSize.Length(100),
                GridTrackSize.Length(200),
                GridTrackSize.Length(300),
                GridTrackSize.Length(400)
            };
            var sliced = SubgridTrackResolver.SliceParentTracks(parent, 1, 3);
            Assert.That(sliced.Tracks.Count, Is.EqualTo(2));
            Assert.That(sliced.Tracks[0].Value, Is.EqualTo(200));
            Assert.That(sliced.Tracks[1].Value, Is.EqualTo(300));
        }

        [Test]
        public void Slice_clamps_indices_to_track_count() {
            var parent = new GridTrackSize[] {
                GridTrackSize.Length(50),
                GridTrackSize.Length(100)
            };
            var sliced = SubgridTrackResolver.SliceParentTracks(parent, 0, 99);
            Assert.That(sliced.Tracks.Count, Is.EqualTo(2));
        }

        [Test]
        public void Slice_with_invalid_range_returns_empty_template() {
            var parent = new GridTrackSize[] { GridTrackSize.Length(50) };
            var sliced = SubgridTrackResolver.SliceParentTracks(parent, 5, 2);
            Assert.That(sliced.Tracks.Count, Is.EqualTo(0));
        }

        [Test]
        public void ResolveAxis_uses_full_range_when_placement_is_auto() {
            var parent = new GridTrackSize[] {
                GridTrackSize.Length(50),
                GridTrackSize.Length(100),
                GridTrackSize.Length(150)
            };
            var sliced = SubgridTrackResolver.ResolveAxis(parent, 0, 0);
            Assert.That(sliced.Tracks.Count, Is.EqualTo(3));
        }

        [Test]
        public void ResolveAxis_emits_lineNames_with_proper_count() {
            var parent = new GridTrackSize[] {
                GridTrackSize.Length(50), GridTrackSize.Length(100)
            };
            var sliced = SubgridTrackResolver.SliceParentTracks(parent, 0, 2);
            Assert.That(sliced.LineNames.Count, Is.EqualTo(3));
        }

        [Test]
        public void Empty_parent_returns_empty_template() {
            var sliced = SubgridTrackResolver.SliceParentTracks(new GridTrackSize[0], 0, 0);
            Assert.That(sliced.Tracks.Count, Is.EqualTo(0));
        }
    }
}
