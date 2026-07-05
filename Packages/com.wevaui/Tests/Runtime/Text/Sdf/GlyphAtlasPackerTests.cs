using NUnit.Framework;
using Weva.Text.Sdf;

namespace Weva.Tests.Text.Sdf {
    public class GlyphAtlasPackerTests {
        [Test]
        public void Single_glyph_fits_in_fresh_shelf() {
            var packer = new GlyphAtlasPacker(1024, 1024);
            Assert.That(packer.Allocate(32, 32, out int x, out int y), Is.True);
            Assert.That(x, Is.EqualTo(0));
            Assert.That(y, Is.EqualTo(0));
            Assert.That(packer.ShelfCount, Is.EqualTo(1));
        }

        [Test]
        public void Multiple_small_glyphs_share_a_shelf() {
            var packer = new GlyphAtlasPacker(256, 256);
            packer.Allocate(16, 16, out var x0, out var y0);
            packer.Allocate(16, 16, out var x1, out var y1);
            packer.Allocate(16, 16, out var x2, out var y2);
            Assert.That(y0, Is.EqualTo(0));
            Assert.That(y1, Is.EqualTo(0));
            Assert.That(y2, Is.EqualTo(0));
            Assert.That(x1, Is.EqualTo(16));
            Assert.That(x2, Is.EqualTo(32));
            Assert.That(packer.ShelfCount, Is.EqualTo(1));
        }

        [Test]
        public void Larger_glyph_forces_new_shelf() {
            var packer = new GlyphAtlasPacker(64, 256);
            packer.Allocate(8, 8, out _, out _);
            // Same height fits same shelf.
            packer.Allocate(8, 8, out _, out _);
            // Taller glyph cannot reuse the 8-tall shelf — new shelf.
            packer.Allocate(8, 32, out int x, out int y);
            Assert.That(packer.ShelfCount, Is.EqualTo(2));
            Assert.That(y, Is.EqualTo(8));
            Assert.That(x, Is.EqualTo(0));
        }

        [Test]
        public void Atlas_full_signal_fires_when_capacity_exhausted() {
            var packer = new GlyphAtlasPacker(16, 16);
            // Pack one 16x16 — fills the atlas exactly.
            Assert.That(packer.Allocate(16, 16, out _, out _), Is.True);
            // Next allocation: no room.
            Assert.That(packer.Allocate(16, 16, out _, out _), Is.False);
            Assert.That(packer.IsFull, Is.True);
            Assert.That(packer.GrowthTracker.FullSignalCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Allocation_too_large_to_fit_signals_full_immediately() {
            var packer = new GlyphAtlasPacker(64, 64);
            // 128x128 cannot ever fit at 64×64 — signals full without consuming.
            Assert.That(packer.Allocate(128, 128, out _, out _), Is.False);
            Assert.That(packer.IsFull, Is.True);
            Assert.That(packer.GrowthTracker.LastFullRequestedWidth, Is.EqualTo(128));
        }

        [Test]
        public void Grow_doubles_capacity() {
            var packer = new GlyphAtlasPacker(64, 64);
            int beforeW = packer.Width;
            int beforeH = packer.Height;
            packer.Grow();
            Assert.That(packer.Width, Is.EqualTo(beforeW * 2));
            Assert.That(packer.Height, Is.EqualTo(beforeH * 2));
            Assert.That(packer.IsFull, Is.False);
            Assert.That(packer.GrowthTracker.ResizeSignalCount, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void Grow_to_specific_size_resizes_tracker() {
            var packer = new GlyphAtlasPacker(64, 64);
            packer.GrowTo(256, 256);
            Assert.That(packer.Width, Is.EqualTo(256));
            Assert.That(packer.GrowthTracker.CurrentWidth, Is.EqualTo(256));
            Assert.That(packer.GrowthTracker.CurrentHeight, Is.EqualTo(256));
        }

        [Test]
        public void Reset_clears_shelves_and_high_water_mark() {
            var packer = new GlyphAtlasPacker(128, 128);
            packer.Allocate(32, 32, out _, out _);
            packer.Allocate(32, 64, out _, out _);
            Assert.That(packer.HighWaterMarkY, Is.GreaterThan(0));
            packer.Reset();
            Assert.That(packer.ShelfCount, Is.EqualTo(0));
            Assert.That(packer.HighWaterMarkY, Is.EqualTo(0));
            Assert.That(packer.IsFull, Is.False);
        }

        [Test]
        public void High_water_mark_tracks_tallest_used_y() {
            var packer = new GlyphAtlasPacker(128, 128);
            packer.Allocate(32, 16, out _, out _);
            packer.Allocate(32, 24, out _, out _);
            // Both fit in shelf 0 (height = 16), then shelf 1 starts.
            Assert.That(packer.HighWaterMarkY, Is.GreaterThanOrEqualTo(16));
        }

        [Test]
        public void Removed_glyphs_keep_allocated_space() {
            var packer = new GlyphAtlasPacker(64, 64);
            packer.Allocate(16, 16, out _, out _);
            // TryRelease is a v1 no-op (documented).
            Assert.That(packer.TryRelease(0, 0, 16, 16), Is.False);
        }

        [Test]
        public void Growth_tracker_emits_resized_event_on_grow() {
            var packer = new GlyphAtlasPacker(64, 64);
            int seen = 0;
            int sawW = 0;
            int sawH = 0;
            packer.GrowthTracker.Resized += (w, h) => { seen++; sawW = w; sawH = h; };
            packer.Grow();
            Assert.That(seen, Is.GreaterThanOrEqualTo(1));
            Assert.That(sawW, Is.EqualTo(128));
            Assert.That(sawH, Is.EqualTo(128));
        }

        [Test]
        public void Growth_tracker_emits_full_event_with_request_dimensions() {
            var packer = new GlyphAtlasPacker(16, 16);
            int seen = 0;
            int reqW = 0;
            int reqH = 0;
            packer.GrowthTracker.Full += (w, h, _, _) => { seen++; reqW = w; reqH = h; };
            packer.Allocate(16, 16, out _, out _);
            packer.Allocate(8, 8, out _, out _);
            Assert.That(seen, Is.GreaterThanOrEqualTo(1));
            Assert.That(reqW, Is.EqualTo(8));
            Assert.That(reqH, Is.EqualTo(8));
        }

        [Test]
        public void Padding_is_caller_responsibility_packer_does_not_inflate() {
            // Document v1: the packer's PaddingPx is informational. Callers fold
            // padding into the requested rect; the packer reserves only what's asked.
            var packer = new GlyphAtlasPacker(64, 64, paddingPx: 8, growthTracker: null);
            Assert.That(packer.PaddingPx, Is.EqualTo(8));
            Assert.That(packer.Allocate(8, 8, out _, out _), Is.True);
            // Without padding inflation, eight 8×8 glyphs fit on a 64-wide shelf.
            for (int i = 0; i < 7; i++) Assert.That(packer.Allocate(8, 8, out _, out _), Is.True);
            Assert.That(packer.ShelfCount, Is.EqualTo(1));
        }
    }
}
