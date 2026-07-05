using System;
using System.Collections.Generic;

namespace Weva.Text.Sdf {
    // GlyphAtlasPacker is the v1 shelf-packer used by the production-side
    // AtlasRegistry-backed atlas (distinct from the in-package GlyphAtlas type
    // which integrates packing + LRU + headless). This packer is intentionally
    // narrow:
    //   - Per-face shelves: each Allocate() returns a (x, y) origin or false.
    //   - Padding (PaddingPx) is folded into requests by the caller; the packer
    //     reserves only the rectangle it's given.
    //   - High-water-mark tracking; when the atlas is full, GrowthTracker
    //     fires a growth signal that the AtlasRegistry consumes to recreate
    //     the underlying Texture2D at 2× the previous size.
    //   - Removed glyphs are kept allocated (no compaction) — the Reset()
    //     call is the only way to reclaim space. v1 simplification: the
    //     atlas grows until MaxSize and clients re-bake at that point.
    //
    // Concurrency: single-threaded, mirrors the rest of the text pipeline.
    public sealed class GlyphAtlasPacker {
        public const int DefaultPaddingPx = 8;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int PaddingPx { get; }
        public int ShelfCount => shelves.Count;
        public int HighWaterMarkY { get; private set; }
        public bool IsFull { get; private set; }

        readonly List<Shelf> shelves = new();
        readonly GlyphAtlasGrowthTracker growth;

        public GlyphAtlasGrowthTracker GrowthTracker => growth;

        public GlyphAtlasPacker(int width, int height) : this(width, height, DefaultPaddingPx, null) { }

        public GlyphAtlasPacker(int width, int height, int paddingPx, GlyphAtlasGrowthTracker growthTracker) {
            if (width <= 0) width = 1024;
            if (height <= 0) height = 1024;
            Width = width;
            Height = height;
            PaddingPx = paddingPx < 0 ? 0 : paddingPx;
            growth = growthTracker ?? new GlyphAtlasGrowthTracker();
            growth.NotifyResize(width, height);
        }

        // Allocates a (w, h) rectangle. Returns true with origin coordinates on
        // success; false when the packer is full (the caller decides to grow or
        // evict). Coordinates are in pixel space, top-left origin.
        public bool Allocate(int requestedWidth, int requestedHeight, out int outX, out int outY) {
            outX = 0;
            outY = 0;
            if (requestedWidth <= 0 || requestedHeight <= 0) return false;
            if (requestedWidth > Width || requestedHeight > Height) {
                IsFull = true;
                growth.NotifyFull(requestedWidth, requestedHeight, Width, Height);
                return false;
            }
            // 1. Try existing shelves.
            for (int i = 0; i < shelves.Count; i++) {
                var shelf = shelves[i];
                if (requestedHeight <= shelf.Height && shelf.CursorX + requestedWidth <= Width) {
                    outX = shelf.CursorX;
                    outY = shelf.Y;
                    shelf.CursorX += requestedWidth;
                    shelves[i] = shelf;
                    if (outY + requestedHeight > HighWaterMarkY) HighWaterMarkY = outY + requestedHeight;
                    return true;
                }
            }
            // 2. Start a new shelf below the last one.
            int newY = shelves.Count == 0 ? 0 : shelves[shelves.Count - 1].Y + shelves[shelves.Count - 1].Height;
            if (newY + requestedHeight > Height) {
                IsFull = true;
                growth.NotifyFull(requestedWidth, requestedHeight, Width, Height);
                return false;
            }
            shelves.Add(new Shelf { Y = newY, CursorX = requestedWidth, Height = requestedHeight });
            outX = 0;
            outY = newY;
            if (newY + requestedHeight > HighWaterMarkY) HighWaterMarkY = newY + requestedHeight;
            return true;
        }

        // Grows by doubling each dimension. Existing shelves keep their (x, y);
        // they sit in the top-left of the enlarged page. Coordinates are stable
        // after a grow event so previously-allocated rectangles are still valid.
        public void Grow() {
            int newW = Width * 2;
            int newH = Height * 2;
            Width = newW;
            Height = newH;
            IsFull = false;
            growth.NotifyResize(newW, newH);
        }

        public void GrowTo(int newWidth, int newHeight) {
            if (newWidth < Width || newHeight < Height) return;
            Width = newWidth;
            Height = newHeight;
            IsFull = false;
            growth.NotifyResize(newWidth, newHeight);
        }

        public void Reset() {
            shelves.Clear();
            HighWaterMarkY = 0;
            IsFull = false;
        }

        // Documents the eviction policy: removed glyphs are kept allocated. Reset()
        // is the only way to reclaim. Adding incremental free is a v2 concern.
        public bool TryRelease(int x, int y, int w, int h) {
            return false;
        }

        struct Shelf {
            public int Y;
            public int CursorX;
            public int Height;
        }
    }

    // GlyphAtlasGrowthTracker: emits resize/full notifications for AtlasRegistry +
    // tests. Multiple subscribers can register; the tracker keeps a snapshot of
    // the current dimensions so late subscribers can sync.
    public sealed class GlyphAtlasGrowthTracker {
        public int CurrentWidth { get; private set; }
        public int CurrentHeight { get; private set; }
        public int FullSignalCount { get; private set; }
        public int ResizeSignalCount { get; private set; }
        public int LastFullRequestedWidth { get; private set; }
        public int LastFullRequestedHeight { get; private set; }

        public event Action<int, int> Resized;
        public event Action<int, int, int, int> Full;

        public void NotifyResize(int newWidth, int newHeight) {
            CurrentWidth = newWidth;
            CurrentHeight = newHeight;
            ResizeSignalCount++;
            Resized?.Invoke(newWidth, newHeight);
        }

        public void NotifyFull(int requestedW, int requestedH, int currentW, int currentH) {
            FullSignalCount++;
            LastFullRequestedWidth = requestedW;
            LastFullRequestedHeight = requestedH;
            Full?.Invoke(requestedW, requestedH, currentW, currentH);
        }
    }
}
