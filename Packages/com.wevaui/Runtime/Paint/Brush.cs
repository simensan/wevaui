using System;

namespace Weva.Paint {
    public enum BrushKind {
        SolidColor,
        Gradient,
        Image
    }

    public sealed class Brush {
        public BrushKind Kind { get; }
        public LinearColor Color { get; }
        public Gradient GradientValue { get; }
        public string ImageHandle { get; }
        public Rect ImageSourceRect { get; }
        // Resolved value of the CSS `image-rendering` property. Backends
        // translate this into their texture sampler state. Solid-color and
        // gradient brushes ignore it — sampler state is meaningless for
        // pure color fills. Defaults to Auto for all brush kinds.
        public ImageRenderingMode ImageRendering { get; }
        // When non-null, the renderer paints the image as one or more
        // tiles per the resolved background-position/size/repeat values.
        // Null retains the v1 stretch behaviour (image fills box).
        public BackgroundTile? Tile { get; }
        // B-9SLICE-SNAP: 9-slice parts ask the backend to round ALL FOUR dest
        // edges to device pixels. The 9 parts are separate quads sharing
        // boundaries; the default image snap (SnapSampledFillToPixels) rounds
        // only width/height and keeps the fractional origin, so abutting
        // parts AA independently at fractional shared boundaries — a ~1px
        // dim band (browsers snap border-image part boundaries; parts never
        // AA against each other, CSS Backgrounds 3 §6.2). Identical pre-snap
        // boundary coordinates round identically, so shared edges stay
        // shared. Only set by ImageSlicePart; plain <img>/background fills
        // keep the origin-preserving snap (see the icon-jump note at
        // SnapSampledFillToPixels).
        public bool SnapEdgesToDevicePixels { get; }
        // Per-layer alpha multiplier for cross-fade() compositing. The emitter
        // wraps the FillRect in PushOpacity/PopOpacity when this is < 1.
        // 1.0f (the default) is a no-op — all existing brushes are unaffected.
        public float LayerAlpha { get; }

        Brush(BrushKind kind, LinearColor color, Gradient gradient, string imageHandle, Rect imageSourceRect, ImageRenderingMode imageRendering, BackgroundTile? tile, bool snapEdgesToDevicePixels = false, float layerAlpha = 1f) {
            Kind = kind;
            Color = color;
            GradientValue = gradient;
            ImageHandle = imageHandle;
            ImageSourceRect = imageSourceRect;
            ImageRendering = imageRendering;
            Tile = tile;
            SnapEdgesToDevicePixels = snapEdgesToDevicePixels;
            LayerAlpha = layerAlpha;
        }

        // Returns a copy of this brush with the given per-layer alpha. Used by
        // cross-fade() to attach the blend weight without touching any other
        // brush property. 1.0f is a no-op identity.
        public Brush WithLayerAlpha(float alpha) {
            return new Brush(Kind, Color, GradientValue, ImageHandle, ImageSourceRect,
                ImageRendering, Tile, SnapEdgesToDevicePixels, alpha);
        }

        public static Brush SolidColor(LinearColor color) {
            return new Brush(BrushKind.SolidColor, color, null, null, default, ImageRenderingMode.Auto, null);
        }

        public static Brush Gradient(Gradient gradient) {
            return Gradient(gradient, null);
        }

        // Tile-aware overload for layered backgrounds. When the cascade resolves a
        // per-layer `<position> / <size>` for a gradient layer, the renderer needs
        // those values to clip/tile the gradient inside the box (otherwise the
        // gradient fills the whole element regardless of the layer rect). Null
        // tile retains the legacy "fill the whole box" behaviour.
        public static Brush Gradient(Gradient gradient, BackgroundTile? tile) {
            if (gradient == null) throw new ArgumentNullException(nameof(gradient));
            return new Brush(BrushKind.Gradient, default, gradient, null, default, ImageRenderingMode.Auto, tile);
        }

        public static Brush Image(string handleId, Rect sourceRect) {
            return Image(handleId, sourceRect, ImageRenderingMode.Auto, null);
        }

        public static Brush Image(string handleId, Rect sourceRect, ImageRenderingMode imageRendering) {
            return Image(handleId, sourceRect, imageRendering, null);
        }

        public static Brush Image(string handleId, Rect sourceRect, ImageRenderingMode imageRendering, BackgroundTile? tile) {
            if (handleId == null) throw new ArgumentNullException(nameof(handleId));
            return new Brush(BrushKind.Image, default, null, handleId, sourceRect, imageRendering, tile);
        }

        // 9-slice part brush (B-9SLICE-SNAP): identical to Image but flags
        // the backend to round all four dest edges to device pixels so
        // abutting parts can never AA against each other at a fractional
        // shared boundary. See SnapEdgesToDevicePixels.
        public static Brush ImageSlicePart(string handleId, Rect sourceRect, ImageRenderingMode imageRendering) {
            if (handleId == null) throw new ArgumentNullException(nameof(handleId));
            return new Brush(BrushKind.Image, default, null, handleId, sourceRect, imageRendering, null, snapEdgesToDevicePixels: true);
        }

        // No-tile image brush cache. The common <img> path (and the gradient/
        // border-image fallback) constructs `Brush.Image(handle, (0,0,1,1),
        // rendering)` every cache miss — fully determined by (handle, rendering).
        // Pure-handle reuse is safe because Brush is immutable.
        const int ImageBrushCacheCap = 128;
        static readonly System.Collections.Generic.Dictionary<(string, ImageRenderingMode), Brush> imageBrushCache
            = new System.Collections.Generic.Dictionary<(string, ImageRenderingMode), Brush>();

        // Cached factory for the common <img>-element case: full source rect
        // (0,0,1,1), no tile. Callers that need a custom source rect or tile
        // use ImageTiled below.
        public static Brush ImageFullRect(string handleId, ImageRenderingMode imageRendering) {
            if (handleId == null) throw new ArgumentNullException(nameof(handleId));
            var key = (handleId, imageRendering);
            if (imageBrushCache.TryGetValue(key, out var cached)) return cached;
            var brush = new Brush(BrushKind.Image, default, null, handleId, new Rect(0, 0, 1, 1), imageRendering, null);
            if (imageBrushCache.Count < ImageBrushCacheCap) imageBrushCache[key] = brush;
            return brush;
        }

        // Tiled-image brush cache. Used by `border-image` 9-slice emission:
        // every part has a stable (handle, sourceRect, rendering, tile) tuple
        // that doesn't change frame to frame, so freshly allocating a Brush
        // per part per cache miss was pure waste. Authors with a typical
        // 9-slice border-image emit ~9 Brushes per box per miss — the cache
        // dedupes across boxes too when they share the same border-image
        // declaration.
        const int TiledImageBrushCacheCap = 128;
        readonly struct TiledImageBrushKey : IEquatable<TiledImageBrushKey> {
            public readonly string Handle;
            public readonly Rect SourceRect;
            public readonly ImageRenderingMode Rendering;
            public readonly bool HasTile;
            public readonly BackgroundTile Tile;

            public TiledImageBrushKey(string handle, Rect sourceRect, ImageRenderingMode rendering, BackgroundTile? tile) {
                Handle = handle;
                SourceRect = sourceRect;
                Rendering = rendering;
                HasTile = tile.HasValue;
                Tile = tile.HasValue ? tile.Value : default;
            }

            public bool Equals(TiledImageBrushKey other) {
                if (Handle != other.Handle) return false;
                if (Rendering != other.Rendering) return false;
                if (!SourceRect.Equals(other.SourceRect)) return false;
                if (HasTile != other.HasTile) return false;
                if (!HasTile) return true;
                return Tile.TileWidth == other.Tile.TileWidth
                    && Tile.TileHeight == other.Tile.TileHeight
                    && Tile.OriginX == other.Tile.OriginX
                    && Tile.OriginY == other.Tile.OriginY
                    && Tile.RepeatX == other.Tile.RepeatX
                    && Tile.RepeatY == other.Tile.RepeatY
                    && Tile.GapX == other.Tile.GapX
                    && Tile.GapY == other.Tile.GapY;
            }

            public override bool Equals(object obj) => obj is TiledImageBrushKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Handle != null ? Handle.GetHashCode() : 0;
                    h = (h * 397) ^ SourceRect.GetHashCode();
                    h = (h * 397) ^ (int)Rendering;
                    if (!HasTile) return h;
                    h = (h * 397) ^ Tile.TileWidth.GetHashCode();
                    h = (h * 397) ^ Tile.TileHeight.GetHashCode();
                    h = (h * 397) ^ Tile.OriginX.GetHashCode();
                    h = (h * 397) ^ Tile.OriginY.GetHashCode();
                    h = (h * 397) ^ (int)Tile.RepeatX;
                    h = (h * 397) ^ (int)Tile.RepeatY;
                    return h;
                }
            }
        }
        static readonly System.Collections.Generic.Dictionary<TiledImageBrushKey, Brush> tiledImageBrushCache
            = new System.Collections.Generic.Dictionary<TiledImageBrushKey, Brush>();

        // Cached factory for image brushes with a custom source rect and/or
        // tile. Each (handle, sourceRect, rendering, tile) tuple resolves to
        // exactly one Brush instance for the process lifetime (up to the cap).
        public static Brush ImageTiled(string handleId, Rect sourceRect, ImageRenderingMode imageRendering, BackgroundTile? tile) {
            if (handleId == null) throw new ArgumentNullException(nameof(handleId));
            var key = new TiledImageBrushKey(handleId, sourceRect, imageRendering, tile);
            if (tiledImageBrushCache.TryGetValue(key, out var cached)) return cached;
            var brush = new Brush(BrushKind.Image, default, null, handleId, sourceRect, imageRendering, tile);
            if (tiledImageBrushCache.Count < TiledImageBrushCacheCap) tiledImageBrushCache[key] = brush;
            return brush;
        }

        // Tiled border-image part (an edge or center-fill that repeats/rounds).
        // Same as ImageTiled but flags SnapEdgesToDevicePixels so the part's
        // OUTER edges round to device pixels exactly like the stretched corner
        // parts (ImageSlicePart). Without this, tiled edges took the floor/ceil
        // ENCLOSING snap while corners ROUNDED — so at a shared corner↔edge
        // boundary the same pre-snap coordinate snapped to different integers and
        // the enclosed edge jutted past the corner (visible only with
        // border-image-repeat:round/repeat, where edges actually tile; stretch
        // panels are all-corner-snap and stay flush). Uncached — border-image
        // parts are few per frame and the tile rect varies. See
        // PixelSnapping.SnapSlicePartEdges.
        public static Brush BorderImageTiledPart(string handleId, Rect sourceRect, ImageRenderingMode imageRendering, BackgroundTile? tile) {
            if (handleId == null) throw new ArgumentNullException(nameof(handleId));
            return new Brush(BrushKind.Image, default, null, handleId, sourceRect, imageRendering, tile, snapEdgesToDevicePixels: true);
        }
    }
}
