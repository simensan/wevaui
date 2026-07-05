namespace Weva.Paint {
    public enum BackgroundRepeatMode {
        Repeat,
        NoRepeat,
        // Round and Space are CSS spec values that aren't fully honoured
        // in v1 — they collapse to Repeat at the renderer. Documented as
        // a v1 simplification; the parser still maps them to distinct
        // enum values so author intent isn't lost when the shader gets
        // updated.
        Space,
        Round,
    }

    // Resolved background-image painting parameters. When a `Brush.Image`
    // carries a non-null `Tile`, the renderer interprets it as:
    //
    //   * The image source (in atlas-relative UVs) is the brush's
    //     `ImageSourceRect`.
    //   * One copy of that source paints into a `(TileWidth, TileHeight)`
    //     pixel-sized rectangle whose top-left lives at
    //     `(OriginX, OriginY)` measured from the box's border-box top-left.
    //   * `RepeatX`/`RepeatY` decide whether tiles repeat in each axis.
    //
    // Null `Tile` retains the legacy v1 behaviour: stretch the source
    // across the full box (= `background-size: 100% 100%`).
    public readonly struct BackgroundTile {
        public double TileWidth { get; }
        public double TileHeight { get; }
        public double OriginX { get; }
        public double OriginY { get; }
        public BackgroundRepeatMode RepeatX { get; }
        public BackgroundRepeatMode RepeatY { get; }
        // Per-axis gap pixels between consecutive tiles. CSS `space` keyword
        // resolves to non-zero gaps so first/last tiles touch container edges
        // and remaining space is distributed evenly. `repeat`/`round`/
        // `no-repeat` resolve to 0 gap. The renderer reads `Stride = Tile + Gap`
        // and discards fragments inside the gap region.
        public double GapX { get; }
        public double GapY { get; }

        public BackgroundTile(double tileWidth, double tileHeight, double originX, double originY,
                              BackgroundRepeatMode repeatX, BackgroundRepeatMode repeatY)
            : this(tileWidth, tileHeight, originX, originY, repeatX, repeatY, 0, 0) { }

        public BackgroundTile(double tileWidth, double tileHeight, double originX, double originY,
                              BackgroundRepeatMode repeatX, BackgroundRepeatMode repeatY,
                              double gapX, double gapY) {
            TileWidth = tileWidth;
            TileHeight = tileHeight;
            OriginX = originX;
            OriginY = originY;
            RepeatX = repeatX;
            RepeatY = repeatY;
            GapX = gapX;
            GapY = gapY;
        }
    }
}
