namespace Weva.Text.TextCore {
    // AtlasResizePolicy describes how the GlyphAtlas reacts when a glyph
    // request cannot fit in the current atlas.
    //
    // v1 strategy: GROW THEN LRU.
    //   - Atlas starts at InitialSize (default 512x512).
    //   - On overflow we double both dimensions, capped at MaxSize (default 2048).
    //   - Once we hit MaxSize, we evict glyphs in least-recently-used order to
    //     make room. LRU is preferred over LFU for v1 simplicity; in practice
    //     UI text is highly localized in time (one screen worth of glyphs at
    //     once) so LRU performs comparably well.
    //
    // Tradeoff: growing requires copying the existing atlas pixels into a new
    // larger Texture2D, which is a single Graphics.CopyTexture call — cheaper
    // than re-rasterizing every glyph. The cost of growing is paid at most twice
    // (512→1024→2048) over the lifetime of the atlas.
    public sealed class AtlasResizePolicy {
        public int InitialSize { get; }
        public int MaxSize { get; }
        public PolicyMode Mode { get; }

        public enum PolicyMode {
            GrowThenLru,
            LruOnly,
            FailOnFull
        }

        public AtlasResizePolicy() : this(512, 2048, PolicyMode.GrowThenLru) { }

        public AtlasResizePolicy(int initialSize, int maxSize, PolicyMode mode) {
            InitialSize = initialSize <= 0 ? 512 : initialSize;
            MaxSize = maxSize <= 0 ? 2048 : maxSize;
            if (MaxSize < InitialSize) MaxSize = InitialSize;
            Mode = mode;
        }

        public static AtlasResizePolicy Default => new AtlasResizePolicy();
    }
}
