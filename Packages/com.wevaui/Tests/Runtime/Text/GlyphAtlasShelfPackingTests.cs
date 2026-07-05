using NUnit.Framework;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    public class GlyphAtlasShelfPackingTests {
        static FaceInfo Face(string family = "test") => new FaceInfo(family, "/stub", 400, FaceInfo.StyleNormal);

        static GlyphAtlas MakeAtlas(int initial = 64, int max = 256, AtlasResizePolicy.PolicyMode mode = AtlasResizePolicy.PolicyMode.GrowThenLru) {
            return new GlyphAtlas(new AtlasResizePolicy(initial, max, mode));
        }

        sealed class FixedSizeBackend : ITextCoreBackend {
            public int Width;
            public int Height;
            public int RasterCalls;

            public FixedSizeBackend(int w, int h) { Width = w; Height = h; }

            public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
                metrics = new FaceMetrics(1024, 800, 200, 0, 1024);
                return true;
            }

            public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
                advancePx = Width;
                return true;
            }

            public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
                RasterCalls++;
                byte[] pixels = new byte[Width * Height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((codepoint + i) & 0xFF);
                glyph = new RasterizedGlyph(pixels, Width, Height, 0, new GlyphMetrics(Width, 0, Height, Width, Height));
                return true;
            }
        }

        [Test]
        public void Empty_atlas_has_zero_glyphs_and_zero_shelves() {
            var atlas = MakeAtlas();
            Assert.That(atlas.GlyphCount, Is.EqualTo(0));
            Assert.That(atlas.ShelfCount, Is.EqualTo(0));
            Assert.That(atlas.Width, Is.EqualTo(64));
            Assert.That(atlas.Height, Is.EqualTo(64));
        }

        [Test]
        public void Adding_one_glyph_packs_at_origin() {
            var atlas = MakeAtlas();
            var backend = new FixedSizeBackend(8, 8);
            Assert.That(atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out var rect, out _), Is.True);
            Assert.That(rect.U0, Is.EqualTo(0).Within(1e-6));
            Assert.That(rect.V0, Is.EqualTo(0).Within(1e-6));
            Assert.That(rect.U1, Is.EqualTo(8.0f / 64).Within(1e-6));
            Assert.That(rect.V1, Is.EqualTo(8.0f / 64).Within(1e-6));
            Assert.That(atlas.ShelfCount, Is.EqualTo(1));
            Assert.That(atlas.GlyphCount, Is.EqualTo(1));
        }

        [Test]
        public void Multiple_glyphs_fill_one_shelf_horizontally() {
            var atlas = MakeAtlas(initial: 64);
            var backend = new FixedSizeBackend(8, 8);
            for (uint cp = 'A'; cp < 'A' + 8; cp++) {
                Assert.That(atlas.RequestGlyphHeadless(backend, Face(), cp, 16, out _, out _), Is.True);
            }
            Assert.That(atlas.ShelfCount, Is.EqualTo(1));
            Assert.That(atlas.GlyphCount, Is.EqualTo(8));
        }

        [Test]
        public void Glyph_too_wide_starts_new_shelf() {
            var atlas = MakeAtlas(initial: 32);
            var backend = new FixedSizeBackend(16, 8);
            // Two 16-wide glyphs fit on one 32-wide shelf, third starts a new shelf.
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out _, out _);
            atlas.RequestGlyphHeadless(backend, Face(), 'B', 16, out _, out _);
            atlas.RequestGlyphHeadless(backend, Face(), 'C', 16, out _, out _);
            Assert.That(atlas.ShelfCount, Is.EqualTo(2));
        }

        [Test]
        public void Taller_glyph_starts_new_shelf() {
            var atlas = MakeAtlas(initial: 64);
            var backend1 = new FixedSizeBackend(8, 8);
            var backend2 = new FixedSizeBackend(8, 16);
            atlas.RequestGlyphHeadless(backend1, Face(), 'A', 16, out _, out _);
            // 16-tall glyph cannot reuse the 8-tall shelf.
            atlas.RequestGlyphHeadless(backend2, Face(), 'B', 16, out _, out _);
            Assert.That(atlas.ShelfCount, Is.EqualTo(2));
        }

        [Test]
        public void Atlas_grows_when_full() {
            var atlas = MakeAtlas(initial: 16, max: 64);
            var backend = new FixedSizeBackend(8, 8);
            for (uint cp = 0; cp < 16; cp++) {
                Assert.That(atlas.RequestGlyphHeadless(backend, Face(), cp + 'A', 16, out _, out _), Is.True);
            }
            Assert.That(atlas.GlyphCount, Is.EqualTo(16));
            Assert.That(atlas.GrowCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(atlas.Width, Is.GreaterThan(16));
        }

        [Test]
        public void Atlas_at_max_size_evicts_lru() {
            var atlas = MakeAtlas(initial: 16, max: 16);
            var backend = new FixedSizeBackend(8, 8);
            // 16x16 at 8x8 holds 4 glyphs.
            for (uint cp = 0; cp < 4; cp++) {
                Assert.That(atlas.RequestGlyphHeadless(backend, Face(), cp + 'A', 16, out _, out _), Is.True);
            }
            Assert.That(atlas.GlyphCount, Is.EqualTo(4));
            Assert.That(atlas.GrowCount, Is.EqualTo(0));
            // Touch the second glyph to push it to MRU.
            atlas.RequestGlyphHeadless(backend, Face(), 'B', 16, out _, out _);
            // Insert a 5th — must evict.
            Assert.That(atlas.RequestGlyphHeadless(backend, Face(), 'E', 16, out _, out _), Is.True);
            Assert.That(atlas.EvictionCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(atlas.GlyphCount, Is.LessThanOrEqualTo(4));
        }

        [Test]
        public void Eviction_frees_space_for_new() {
            var atlas = MakeAtlas(initial: 16, max: 16);
            var backend = new FixedSizeBackend(8, 8);
            for (uint cp = 0; cp < 4; cp++) {
                atlas.RequestGlyphHeadless(backend, Face(), cp + 'A', 16, out _, out _);
            }
            int before = atlas.EvictionCount;
            atlas.RequestGlyphHeadless(backend, Face(), 'Z', 16, out var rect, out _);
            Assert.That(atlas.EvictionCount, Is.GreaterThan(before));
            Assert.That(rect.Width, Is.GreaterThan(0));
        }

        [Test]
        public void Repeated_request_returns_same_uv_and_skips_rasterize() {
            var atlas = MakeAtlas();
            var backend = new FixedSizeBackend(8, 8);
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out var first, out _);
            int rasterAfterFirst = backend.RasterCalls;
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out var second, out _);
            Assert.That(second, Is.EqualTo(first));
            Assert.That(backend.RasterCalls, Is.EqualTo(rasterAfterFirst));
        }

        [Test]
        public void Different_sizes_pack_separately() {
            var atlas = MakeAtlas();
            var backend = new FixedSizeBackend(8, 8);
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out var rect16, out _);
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 24, out var rect24, out _);
            Assert.That(atlas.GlyphCount, Is.EqualTo(2));
            Assert.That(rect16, Is.Not.EqualTo(rect24));
        }

        [Test]
        public void Cached_rect_lookup_works() {
            var atlas = MakeAtlas();
            var backend = new FixedSizeBackend(8, 8);
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out var rect, out _);
            Assert.That(atlas.TryGetCachedRect(Face(), 'A', 16, out var cached, out _), Is.True);
            Assert.That(cached, Is.EqualTo(rect));
            Assert.That(atlas.TryGetCachedRect(Face(), 'B', 16, out _, out _), Is.False);
        }

        [Test]
        public void Padding_is_threaded_from_raster_through_atlas_lookup() {
            // Bug #1 regression: a glyph rasterized with padding=N exposes N
            // when re-queried via TryGetCachedRect / RequestGlyph, so the baker
            // can inflate its quad by the EXACT padding the rasterizer used.
            var atlas = MakeAtlas();
            var backend = new PaddedBackend(8, 8, paddingPx: 7);
            Assert.That(atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out _, out _), Is.True);
            Assert.That(atlas.TryGetCachedRect(Face(), 'A', 16, out _, out _, out int pad), Is.True);
            Assert.That(pad, Is.EqualTo(7));
        }

        [Test]
        public void RequestGlyph_overload_returns_padding_from_raster() {
            // Bug #1 regression: the padding-aware RequestGlyph overload
            // surfaces RasterizedGlyph.Padding so callers don't have to
            // hard-code a value.
            var atlas = MakeAtlas();
            var backend = new PaddedBackend(8, 8, paddingPx: 11);
            Assert.That(atlas.RequestGlyph(backend, Face(), 'A', 16, out _, out _, out int pad), Is.True);
            Assert.That(pad, Is.EqualTo(11));
        }

        [Test]
        public void Fractional_size_differences_share_a_cache_slot() {
            // Bug #7 regression: the GlyphKey used to hash the raw double font
            // size, so 16 and 16.0000000001 missed the cache and produced
            // duplicate slots. Now keys quantize to integer pixels.
            var atlas = MakeAtlas();
            var backend = new FixedSizeBackend(8, 8);
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16, out var rectA, out _);
            int rasterAfterFirst = backend.RasterCalls;
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 16.0000000001, out var rectB, out _);
            atlas.RequestGlyphHeadless(backend, Face(), 'A', 15.9999999999, out var rectC, out _);
            Assert.That(backend.RasterCalls, Is.EqualTo(rasterAfterFirst), "Fractional differences must hit the cache");
            Assert.That(rectB, Is.EqualTo(rectA));
            Assert.That(rectC, Is.EqualTo(rectA));
            Assert.That(atlas.GlyphCount, Is.EqualTo(1));
        }

        sealed class PaddedBackend : ITextCoreBackend {
            public int Width;
            public int Height;
            public int PaddingPx;

            public PaddedBackend(int w, int h, int paddingPx) {
                Width = w; Height = h; PaddingPx = paddingPx;
            }

            public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
                metrics = new FaceMetrics(1024, 800, 200, 0, 1024);
                return true;
            }

            public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
                advancePx = Width;
                return true;
            }

            public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
                byte[] pixels = new byte[Width * Height];
                glyph = new RasterizedGlyph(pixels, Width, Height, PaddingPx, new GlyphMetrics(Width, 0, Height, Width, Height));
                return true;
            }
        }

        // Variable-size backend for the defrag stress: each codepoint maps to
        // a deterministic (w, h) so re-rasterization after eviction reproduces
        // the same dimensions a real glyph would.
        sealed class VariableSizeBackend : ITextCoreBackend {
            public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
                metrics = new FaceMetrics(1024, 800, 200, 0, 1024);
                return true;
            }

            public static int WidthFor(uint cp) => 4 + (int)(cp * 7 % 37);
            public static int HeightFor(uint cp) => 4 + (int)(cp * 11 % 29);

            public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
                advancePx = WidthFor(codepoint);
                return true;
            }

            public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
                int w = WidthFor(codepoint);
                int h = HeightFor(codepoint);
                byte[] pixels = new byte[w * h];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((codepoint + i) & 0xFF);
                glyph = new RasterizedGlyph(pixels, w, h, 0, new GlyphMetrics(w, 0, h, w, h));
                return true;
            }
        }

        // Regression for the defrag pixel/coordinate desync: DefragmentShelves
        // used to reassign slot coordinates after eviction WITHOUT moving any
        // pixels, and a slot whose re-pack failed silently kept stale
        // coordinates while other slots were packed on top of them. Once an
        // atlas hit MaxSize, every surviving glyph rendered garbled. The
        // observable invariant: after any amount of mixed-size eviction churn,
        // live slots are in-bounds and pairwise disjoint.
        [Test]
        public void Eviction_defrag_keeps_live_slots_in_bounds_and_disjoint() {
            var atlas = MakeAtlas(initial: 64, max: 64);
            var backend = new VariableSizeBackend();
            var face = Face();
            var inserted = new System.Collections.Generic.List<uint>();

            for (uint cp = 100; cp < 300; cp++) {
                if (!atlas.RequestGlyphHeadless(backend, face, cp, 16, out _, out _)) continue;
                inserted.Add(cp);

                // Recover every live slot's pixel rect from its UVs and check
                // the disjointness invariant after each insert (evictions and
                // defrags happen mid-stream).
                var live = new System.Collections.Generic.List<(uint cp, int x, int y, int w, int h)>();
                foreach (var prev in inserted) {
                    if (!atlas.TryGetCachedRect(face, prev, 16, out var uv, out _)) continue;
                    int x = (int)System.Math.Round(uv.U0 * atlas.Width);
                    int y = (int)System.Math.Round(uv.V0 * atlas.Height);
                    int x1 = (int)System.Math.Round(uv.U1 * atlas.Width);
                    int y1 = (int)System.Math.Round(uv.V1 * atlas.Height);
                    Assert.That(x, Is.GreaterThanOrEqualTo(0));
                    Assert.That(y, Is.GreaterThanOrEqualTo(0));
                    Assert.That(x1, Is.LessThanOrEqualTo(atlas.Width),
                        $"cp {prev} right edge out of bounds after inserting {inserted.Count} glyphs");
                    Assert.That(y1, Is.LessThanOrEqualTo(atlas.Height),
                        $"cp {prev} bottom edge out of bounds after inserting {inserted.Count} glyphs");
                    Assert.That(x1 - x, Is.EqualTo(VariableSizeBackend.WidthFor(prev)),
                        $"cp {prev} width drifted");
                    Assert.That(y1 - y, Is.EqualTo(VariableSizeBackend.HeightFor(prev)),
                        $"cp {prev} height drifted");
                    live.Add((prev, x, y, x1 - x, y1 - y));
                }
                for (int i = 0; i < live.Count; i++) {
                    for (int j = i + 1; j < live.Count; j++) {
                        bool overlap = live[i].x < live[j].x + live[j].w
                            && live[j].x < live[i].x + live[i].w
                            && live[i].y < live[j].y + live[j].h
                            && live[j].y < live[i].y + live[i].h;
                        Assert.That(overlap, Is.False,
                            $"slots for cp {live[i].cp} and cp {live[j].cp} overlap after defrag " +
                            $"({live[i].x},{live[i].y},{live[i].w}x{live[i].h}) vs " +
                            $"({live[j].x},{live[j].y},{live[j].w}x{live[j].h})");
                    }
                }
            }

            Assert.That(atlas.EvictionCount, Is.GreaterThan(0),
                "stress never triggered eviction — sizes need adjusting for the invariant to mean anything");
        }

        // The original defrag bug: coordinates were reassigned after eviction
        // but NO pixel move was ever requested, so every surviving glyph's
        // UVs pointed at stale/foreign texels once the atlas began evicting.
        // Contract under test: any slot whose coordinates change during a
        // defrag must have a matching pixel-move recorded.
        [Test]
        public void Defrag_requests_a_pixel_move_for_every_slot_whose_coordinates_changed() {
            var atlas = MakeAtlas(initial: 64, max: 64);
            var backend = new FixedSizeBackend(30, 30);
            var face = Face();

            // Four 30x30 glyphs fill the 64x64 page (2 shelves x 2 slots).
            for (uint cp = 'A'; cp <= 'D'; cp++) {
                Assert.That(atlas.RequestGlyphHeadless(backend, face, cp, 16, out _, out _), Is.True);
            }
            var before = new System.Collections.Generic.Dictionary<uint, (int x, int y)>();
            for (uint cp = 'A'; cp <= 'D'; cp++) {
                Assert.That(atlas.TryGetCachedRect(face, cp, 16, out var uv, out _), Is.True);
                before[cp] = ((int)System.Math.Round(uv.U0 * atlas.Width),
                              (int)System.Math.Round(uv.V0 * atlas.Height));
            }

            // Fifth glyph forces eviction of 'A' + defrag of the survivors.
            Assert.That(atlas.RequestGlyphHeadless(backend, face, 'E', 16, out _, out _), Is.True);
            Assert.That(atlas.EvictionCount, Is.GreaterThan(0));

            int movedSlots = 0;
            for (uint cp = 'B'; cp <= 'D'; cp++) {
                if (!atlas.TryGetCachedRect(face, cp, 16, out var uv, out _)) continue;
                int nx = (int)System.Math.Round(uv.U0 * atlas.Width);
                int ny = (int)System.Math.Round(uv.V0 * atlas.Height);
                var (ox, oy) = before[cp];
                if (nx == ox && ny == oy) continue;
                movedSlots++;
                bool moveRecorded = false;
                foreach (var m in atlas.LastDefragMoves) {
                    if (m.SrcX == ox && m.SrcY == oy && m.DstX == nx && m.DstY == ny
                        && m.W == 30 && m.H == 30) {
                        moveRecorded = true;
                        break;
                    }
                }
                Assert.That(moveRecorded, Is.True,
                    $"cp {cp} moved ({ox},{oy})->({nx},{ny}) but no pixel move was requested — " +
                    "its UVs now point at stale texels");
            }
            Assert.That(movedSlots, Is.GreaterThan(0),
                "defrag moved nothing — the contract assertion was vacuous");
        }

        [Test]
        public void CopyBlock_moves_rows_between_positions_in_a_strided_buffer() {
            const int stride = 16;
            var src = new byte[stride * 16];
            // 3x2 block at (4, 5) with distinct bytes.
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++)
                    src[(5 + row) * stride + 4 + col] = (byte)(100 + row * 3 + col);

            var dst = new byte[stride * 16];
            GlyphAtlas.CopyBlock(src, dst, stride, new GlyphAtlas.SlotMove(4, 5, 9, 1, 3, 2));

            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++)
                    Assert.That(dst[(1 + row) * stride + 9 + col], Is.EqualTo((byte)(100 + row * 3 + col)));
            // Nothing else written.
            int sum = 0;
            foreach (var b in dst) sum += b;
            Assert.That(sum, Is.EqualTo(100 + 101 + 102 + 103 + 104 + 105));
        }

        // Audit T2: a prepare pass that rasterizes N glyphs used to do N full
        // (~4 MB at 2048²) GPU texture uploads — one Apply per glyph. The
        // Begin/EndUploadBatch window coalesces them into ONE. TextureUploadCount
        // is the dispatch-count seam (the Unity Apply itself can't run
        // headlessly, but the coalescing decision lives in the pure core).
        [Test]
        public void Upload_batch_coalesces_many_pixel_changes_into_one_dispatch() {
            var atlas = MakeAtlas();
            long before = atlas.TextureUploadCount;

            atlas.BeginUploadBatch();
            for (int i = 0; i < 10; i++) atlas.NotifyPixelsChanged();
            // Nothing dispatched yet — all deferred to the flush.
            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before));
            atlas.EndUploadBatch();

            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before + 1),
                "10 glyph writes inside one window must coalesce to a single upload");
        }

        [Test]
        public void Pixel_change_outside_a_window_uploads_immediately() {
            var atlas = MakeAtlas();
            long before = atlas.TextureUploadCount;
            atlas.NotifyPixelsChanged();
            atlas.NotifyPixelsChanged();
            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before + 2),
                "outside a batch each change uploads immediately (ad-hoc layout-measurement glyph requests)");
        }

        [Test]
        public void Upload_batch_nesting_flushes_only_at_outer_end() {
            var atlas = MakeAtlas();
            long before = atlas.TextureUploadCount;
            atlas.BeginUploadBatch();
            atlas.BeginUploadBatch();
            atlas.NotifyPixelsChanged();
            atlas.EndUploadBatch();
            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before),
                "inner EndUploadBatch must not flush while the outer window is open");
            atlas.EndUploadBatch();
            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before + 1));
        }

        [Test]
        public void Empty_upload_batch_dispatches_nothing() {
            var atlas = MakeAtlas();
            long before = atlas.TextureUploadCount;
            atlas.BeginUploadBatch();
            atlas.EndUploadBatch();
            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before),
                "a window with no pixel changes must not dispatch a wasted upload");
        }

        [Test]
        public void Unbalanced_end_upload_batch_is_a_safe_noop() {
            var atlas = MakeAtlas();
            long before = atlas.TextureUploadCount;
            // End without Begin must not underflow or dispatch.
            atlas.EndUploadBatch();
            atlas.NotifyPixelsChanged();
            Assert.That(atlas.TextureUploadCount, Is.EqualTo(before + 1),
                "a stray EndUploadBatch must leave the immediate-upload path intact");
        }

        [Test]
        public void Clear_resets_state() {
            var atlas = MakeAtlas();
            var backend = new FixedSizeBackend(8, 8);
            for (uint cp = 0; cp < 4; cp++) {
                atlas.RequestGlyphHeadless(backend, Face(), cp + 'A', 16, out _, out _);
            }
            atlas.Clear();
            Assert.That(atlas.GlyphCount, Is.EqualTo(0));
            Assert.That(atlas.ShelfCount, Is.EqualTo(0));
            Assert.That(atlas.Width, Is.EqualTo(atlas.Policy.InitialSize));
        }
    }
}
