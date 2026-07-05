using System;
using System.Collections.Generic;

namespace Weva.Text.TextCore {
    // GlyphAtlas owns the SDF glyph cache. Two responsibilities:
    //   1. Shelf-pack glyph rasters into a single R8 texture page.
    //   2. Track glyph metadata (rect, metrics, LRU position) for fast
    //      lookup and LRU eviction. LRU is maintained as a LinkedList that
    //      moves the touched node to the tail on every cache hit.
    //
    // Shelf packing algorithm (v1):
    //   - The atlas is a stack of horizontal "shelves". Each shelf has a
    //     fixed height equal to the tallest glyph that started it.
    //   - Glyphs are appended left-to-right within the current shelf until
    //     they no longer fit horizontally.
    //   - When a glyph either exceeds the remaining width OR is taller than
    //     the current shelf, a new shelf is started below. Wasted vertical
    //     space inside a shelf is the cost of simplicity.
    //   - When the current shelf cursor exceeds atlas height, the resize
    //     policy decides whether to grow or evict (see AtlasResizePolicy).
    //
    // The pure-C# packing layer (this file) does NOT touch UnityEngine. The
    // Unity-bound texture upload lives in GlyphAtlas.Unity.cs under
    // #if UNITY_2023_1_OR_NEWER. Tests exercise packing through the headless
    // surface (RequestGlyphHeadless) and never need a real Texture2D.
    public sealed partial class GlyphAtlas {
        public AtlasResizePolicy Policy { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int GlyphCount => slots.Count;
        public int ShelfCount => shelves.Count;
        public int GrowCount { get; private set; }
        public int EvictionCount { get; private set; }
        public long Revision { get; private set; }

        // Deferred-upload window: inside a Begin/EndUploadBatch pair, the Unity
        // texture partial writes changed texels into cpuBuffer but the
        // (full-texture) GPU upload is coalesced to ONE Apply at the batch
        // flush instead of one per glyph. A cold page inserting N glyphs used
        // to transfer N * (Width*Height) bytes (~4 MB each at 2048²) and stall
        // per glyph; now it transfers once. Nestable; driven by
        // SdfGlyphAtlasAdapter's prepare window.
        int uploadBatchDepth;
        bool pendingTextureUpload;
        // Test/diagnostic seam: number of real GPU texture uploads dispatched.
        // Coalescing N writes in one batch into 1 upload is observable here
        // (the Unity flush itself can't run headlessly).
        internal long TextureUploadCount { get; private set; }

        readonly Dictionary<GlyphKey, Slot> slots = new();
        readonly LinkedList<GlyphKey> lru = new();
        readonly List<Shelf> shelves = new();

        public GlyphAtlas() : this(AtlasResizePolicy.Default) { }

        public GlyphAtlas(AtlasResizePolicy policy) {
            Policy = policy ?? AtlasResizePolicy.Default;
            Width = Policy.InitialSize;
            Height = Policy.InitialSize;
            InitializeBackingStore();
        }

        public readonly struct GlyphKey : IEquatable<GlyphKey> {
            public readonly FaceInfo Face;
            public readonly uint Codepoint;
            // Bug #7: previously stored raw double fontSize. Fractional
            // differences (16 vs 16.0000000001 from clamp() evaluation) missed
            // the cache and re-rasterized into duplicate slots. We now quantize
            // to integer pixels at construction (matching the rasterizer, which
            // calls FontEngine.SetFaceSize with an int sizePx anyway), so any
            // two requests within ±0.5 px share a slot.
            public readonly int SizePx;

            // Surfaced as a double so existing callers / tests that read .FontSize
            // continue to compile and see the quantized value as a numeric size.
            public double FontSize => SizePx;

            public GlyphKey(FaceInfo face, uint codepoint, double fontSize) {
                Face = face;
                Codepoint = codepoint;
                SizePx = (int)System.Math.Max(1, System.Math.Round(fontSize));
            }

            public bool Equals(GlyphKey other) => Face.Equals(other.Face) && Codepoint == other.Codepoint && SizePx == other.SizePx;
            public override bool Equals(object obj) => obj is GlyphKey k && Equals(k);
            public override int GetHashCode() {
                unchecked {
                    int h = Face.GetHashCode();
                    h = (h * 397) ^ (int)Codepoint;
                    h = (h * 397) ^ SizePx;
                    return h;
                }
            }
        }

        sealed class Slot {
            public GlyphKey Key;
            public int X;
            public int Y;
            public int W;
            public int H;
            public int Padding;
            public GlyphMetrics Metrics;
            public LinkedListNode<GlyphKey> LruNode;
        }

        struct Shelf {
            public int Y;
            public int CursorX;
            public int Height;
        }

        public bool RequestGlyphHeadless(ITextCoreBackend backend, FaceInfo face, uint codepoint, double fontSize, out GlyphRect uvRect, out GlyphMetrics metrics) {
            return RequestGlyphInternal(backend, face, codepoint, fontSize, uploadTexture: false, out uvRect, out metrics, out _);
        }

        public bool RequestGlyph(ITextCoreBackend backend, FaceInfo face, uint codepoint, double fontSize, out GlyphRect uvRect, out GlyphMetrics metrics) {
            return RequestGlyphInternal(backend, face, codepoint, fontSize, uploadTexture: true, out uvRect, out metrics, out _);
        }

        // Variant exposing the per-glyph SDF padding (Bug #1: the baker needs the
        // exact padding the rasterizer used so its quad inflation matches the
        // raster footprint instead of relying on a hard-coded constant).
        public bool RequestGlyph(ITextCoreBackend backend, FaceInfo face, uint codepoint, double fontSize, out GlyphRect uvRect, out GlyphMetrics metrics, out int paddingPx) {
            return RequestGlyphInternal(backend, face, codepoint, fontSize, uploadTexture: true, out uvRect, out metrics, out paddingPx);
        }

        bool RequestGlyphInternal(ITextCoreBackend backend, FaceInfo face, uint codepoint, double fontSize, bool uploadTexture, out GlyphRect uvRect, out GlyphMetrics metrics, out int paddingPx) {
            var key = new GlyphKey(face, codepoint, fontSize);
            if (slots.TryGetValue(key, out var existing)) {
                Touch(existing);
                uvRect = ToUv(existing.X, existing.Y, existing.W, existing.H);
                metrics = existing.Metrics;
                paddingPx = existing.Padding;
                return true;
            }
            if (backend == null || !backend.RasterizeGlyph(face, codepoint, fontSize, out var raster) || raster.IsEmpty) {
                uvRect = GlyphRect.Empty;
                metrics = GlyphMetrics.Zero;
                paddingPx = 0;
                return false;
            }
            if (!TryPack(raster.Width, raster.Height, out int sx, out int sy)) {
                uvRect = GlyphRect.Empty;
                metrics = GlyphMetrics.Zero;
                paddingPx = 0;
                return false;
            }
            var slot = new Slot {
                Key = key,
                X = sx,
                Y = sy,
                W = raster.Width,
                H = raster.Height,
                Padding = raster.Padding,
                Metrics = raster.Metrics
            };
            slots[key] = slot;
            slot.LruNode = lru.AddLast(key);
            if (uploadTexture) {
                UploadPixels(sx, sy, raster);
            }
            uvRect = ToUv(sx, sy, raster.Width, raster.Height);
            metrics = raster.Metrics;
            paddingPx = raster.Padding;
            return true;
        }

        public bool TryGetCachedRect(FaceInfo face, uint codepoint, double fontSize, out GlyphRect uvRect, out GlyphMetrics metrics) {
            return TryGetCachedRect(face, codepoint, fontSize, out uvRect, out metrics, out _);
        }

        public bool TryGetCachedRect(FaceInfo face, uint codepoint, double fontSize, out GlyphRect uvRect, out GlyphMetrics metrics, out int paddingPx) {
            var key = new GlyphKey(face, codepoint, fontSize);
            if (slots.TryGetValue(key, out var slot)) {
                uvRect = ToUv(slot.X, slot.Y, slot.W, slot.H);
                metrics = slot.Metrics;
                paddingPx = slot.Padding;
                return true;
            }
            uvRect = GlyphRect.Empty;
            metrics = GlyphMetrics.Zero;
            paddingPx = 0;
            return false;
        }

        // TryUploadRaster: external rasterizer path. When the SdfGlyphRasterizer
        // produces a raster outside the backend.RasterizeGlyph flow (the v1
        // FontEngine.TryRenderGlyphsToTexture reflection path), we splice the
        // bytes directly into the atlas without re-invoking the backend.
        // Returns true on success; false if the raster doesn't fit even after
        // the resize policy completes.
        public bool TryUploadRaster(FaceInfo face, uint codepoint, double fontSize, RasterizedGlyph raster) {
            if (raster.IsEmpty) return false;
            var key = new GlyphKey(face, codepoint, fontSize);
            if (slots.ContainsKey(key)) {
                Touch(slots[key]);
                return true;
            }
            if (!TryPack(raster.Width, raster.Height, out int sx, out int sy)) return false;
            var slot = new Slot {
                Key = key,
                X = sx,
                Y = sy,
                W = raster.Width,
                H = raster.Height,
                Padding = raster.Padding,
                Metrics = raster.Metrics
            };
            slots[key] = slot;
            slot.LruNode = lru.AddLast(key);
            UploadPixels(sx, sy, raster);
            return true;
        }

        public void Clear() {
            slots.Clear();
            lru.Clear();
            shelves.Clear();
            Width = Policy.InitialSize;
            Height = Policy.InitialSize;
            GrowCount = 0;
            EvictionCount = 0;
            uploadBatchDepth = 0;
            pendingTextureUpload = false;
            Revision++;
            InitializeBackingStore();
        }

        // Opens a deferred-upload window (see uploadBatchDepth). Nestable;
        // every BeginUploadBatch must be paired with an EndUploadBatch (the
        // SdfTextRendering prepare driver pairs them in a try/finally).
        public void BeginUploadBatch() { uploadBatchDepth++; }

        public void EndUploadBatch() {
            if (uploadBatchDepth == 0) return; // unbalanced End — ignore
            if (--uploadBatchDepth == 0 && pendingTextureUpload) {
                pendingTextureUpload = false;
                DispatchTextureUpload();
            }
        }

        // Called by the Unity texture partial after it writes changed texels
        // into cpuBuffer. Inside a window the page is just marked dirty;
        // outside one it uploads immediately (so ad-hoc RequestGlyph calls
        // during layout measurement stay correct).
        internal void NotifyPixelsChanged() {
            if (uploadBatchDepth > 0) { pendingTextureUpload = true; return; }
            DispatchTextureUpload();
        }

        void DispatchTextureUpload() {
            TextureUploadCount++;
            FlushTextureUploadToGpu();
        }

        partial void FlushTextureUploadToGpu();

        bool TryPack(int w, int h, out int outX, out int outY) {
            if (w <= 0 || h <= 0 || w > Policy.MaxSize || h > Policy.MaxSize) {
                outX = 0; outY = 0; return false;
            }
            if (TryPackOnce(w, h, out outX, out outY)) return true;
            while (CanGrow()) {
                Grow();
                if (TryPackOnce(w, h, out outX, out outY)) return true;
            }
            if (Policy.Mode == AtlasResizePolicy.PolicyMode.FailOnFull) {
                outX = 0; outY = 0; return false;
            }
            while (slots.Count > 0) {
                EvictOnce();
                if (TryPackOnce(w, h, out outX, out outY)) return true;
            }
            outX = 0; outY = 0;
            return false;
        }

        bool TryPackOnce(int w, int h, out int outX, out int outY) {
            for (int i = 0; i < shelves.Count; i++) {
                var shelf = shelves[i];
                if (h <= shelf.Height && shelf.CursorX + w <= Width) {
                    outX = shelf.CursorX;
                    outY = shelf.Y;
                    shelf.CursorX += w;
                    shelves[i] = shelf;
                    return true;
                }
            }
            int newShelfY = shelves.Count == 0 ? 0 : shelves[shelves.Count - 1].Y + shelves[shelves.Count - 1].Height;
            if (newShelfY + h <= Height && w <= Width) {
                var newShelf = new Shelf { Y = newShelfY, CursorX = w, Height = h };
                shelves.Add(newShelf);
                outX = 0;
                outY = newShelfY;
                return true;
            }
            outX = 0;
            outY = 0;
            return false;
        }

        bool CanGrow() {
            if (Policy.Mode != AtlasResizePolicy.PolicyMode.GrowThenLru) return false;
            return Width < Policy.MaxSize || Height < Policy.MaxSize;
        }

        void Grow() {
            int newW = Math.Min(Width * 2, Policy.MaxSize);
            int newH = Math.Min(Height * 2, Policy.MaxSize);
            int oldW = Width;
            int oldH = Height;
            Width = newW;
            Height = newH;
            GrowCount++;
            Revision++;
            // Existing shelves keep their (x,y) coordinates; they sit in the
            // top-left of the enlarged page. UVs change because the divisor
            // (atlas dimensions) changed — callers must re-fetch UVs after
            // a grow event. The cached metrics + slot positions stay valid.
            ResizeBackingStore(oldW, oldH, newW, newH);
        }

        void EvictOnce() {
            if (lru.Count == 0) return;
            var oldestKey = lru.First.Value;
            lru.RemoveFirst();
            if (slots.TryGetValue(oldestKey, out var slot)) {
                slots.Remove(oldestKey);
                EvictionCount++;
                Revision++;
                // Shelf cursors don't move on eviction; we always defragment so
                // a freed slot's space is immediately available to the next
                // pack attempt. Rebuilding shelves from N live slots is O(N)
                // and only happens at MaxSize where N is bounded.
                DefragmentShelves();
            }
        }

        // Repacks all live slots tightly after an eviction. Two phases:
        // assign every slot its new position first (collecting moves), then
        // relocate the backing-store pixels in one pass (RelocatePixels reads
        // from a snapshot of the pre-defrag buffer so overlapping moves can't
        // clobber each other). A slot that no longer fits under the new shelf
        // geometry is EVICTED — keeping its old coordinates would leave its
        // UVs pointing at pixels another slot is about to be packed over.
        // Before this, the defrag moved slot coordinates WITHOUT moving any
        // pixels, so once an atlas hit MaxSize and began evicting, every
        // surviving glyph's UVs pointed at stale/foreign pixels and text
        // rendered permanently garbled.
        void DefragmentShelves() {
            var liveSlots = new List<Slot>(slots.Values);
            liveSlots.Sort((a, b) => b.H.CompareTo(a.H));
            shelves.Clear();
            LastDefragMoves.Clear();
            List<SlotMove> moves = null;
            foreach (var s in liveSlots) {
                if (TryPackOnce(s.W, s.H, out int nx, out int ny)) {
                    if (nx != s.X || ny != s.Y) {
                        moves ??= new List<SlotMove>(liveSlots.Count);
                        moves.Add(new SlotMove(s.X, s.Y, nx, ny, s.W, s.H));
                        LastDefragMoves.Add(new SlotMove(s.X, s.Y, nx, ny, s.W, s.H));
                        s.X = nx;
                        s.Y = ny;
                    }
                } else {
                    slots.Remove(s.Key);
                    if (s.LruNode != null) {
                        lru.Remove(s.LruNode);
                        s.LruNode = null;
                    }
                    EvictionCount++;
                }
            }
            if (moves != null) RelocatePixels(moves);
        }

        // Test seam: headless builds have no pixel backing store, so tests
        // assert the "coordinates moved ⇒ pixel move requested" contract via
        // the moves the last defrag recorded here (the original defrag bug was
        // exactly this contract violated: coordinates reassigned, pixels
        // never moved).
        internal readonly List<SlotMove> LastDefragMoves = new();

        internal readonly struct SlotMove {
            public readonly int SrcX;
            public readonly int SrcY;
            public readonly int DstX;
            public readonly int DstY;
            public readonly int W;
            public readonly int H;
            public SlotMove(int srcX, int srcY, int dstX, int dstY, int w, int h) {
                SrcX = srcX;
                SrcY = srcY;
                DstX = dstX;
                DstY = dstY;
                W = w;
                H = h;
            }
        }

        // Pure row-block copy shared by the Unity backing-store partial and
        // unit tests. `src` must be a snapshot of the pre-move buffer.
        internal static void CopyBlock(byte[] src, byte[] dst, int stride, in SlotMove m) {
            for (int row = 0; row < m.H; row++) {
                Buffer.BlockCopy(
                    src, (m.SrcY + row) * stride + m.SrcX,
                    dst, (m.DstY + row) * stride + m.DstX,
                    m.W);
            }
        }

        partial void RelocatePixels(List<SlotMove> moves);

        void Touch(Slot slot) {
            if (slot.LruNode != null) {
                lru.Remove(slot.LruNode);
                slot.LruNode = lru.AddLast(slot.Key);
            }
        }

        GlyphRect ToUv(int x, int y, int w, int h) {
            float u0 = (float)x / Width;
            float v0 = (float)y / Height;
            float u1 = (float)(x + w) / Width;
            float v1 = (float)(y + h) / Height;
            return new GlyphRect(u0, v0, u1, v1);
        }

        partial void InitializeBackingStore();
        partial void ResizeBackingStore(int oldW, int oldH, int newW, int newH);
        partial void UploadPixels(int dstX, int dstY, RasterizedGlyph raster);
    }
}
