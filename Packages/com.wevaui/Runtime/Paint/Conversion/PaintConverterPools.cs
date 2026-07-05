using System.Collections.Generic;

namespace Weva.Paint.Conversion {
    // Per-converter scratch buffers reused across every Convert() pass. One
    // instance per BoxToPaintConverter; the individual buffers are each
    // `Clear()`-ed by their respective consumers right before use (see
    // `EmitVisibleDecorations` — `pools.ShadowBuffer.Clear()` /
    // `pools.BackgroundLayers.Clear()` / `pools.BorderImageBuffer.Clear()`)
    // so a single shared set of Lists survives across boxes without
    // allocating fresh ones per Visit.
    //
    // Memoization caches (Brush, FontHandle) live for the lifetime of the
    // converter. They are keyed on inputs that stay stable across cache
    // misses (raw color string, family / size / weight / style tuple).
    // Deactivate() empties them when the converter is told to fully
    // invalidate.
    internal sealed class PaintConverterPools {
        public readonly List<BoxShadow> ShadowBuffer = new(4);
        public readonly List<Brush> BackgroundLayers = new(2);
        // Reused buffer for `border-image` 9-slice parts. Capacity 9 fits
        // the standard slice grid; growths happen only when authors omit
        // edges or stack additional resolved parts (corner-only specs).
        public readonly List<BorderImageResolver.BorderImagePart> BorderImageBuffer = new(9);

        public readonly Dictionary<string, Brush> BrushCache = new(16);
        // FontCache: key is the family-string (post-Trim) since the only string
        // alloc inside BuildFont is on Trim. Value is the FontHandle struct — its
        // (size,weight,style) are part of an indexed FontKey we compose on the fly
        // in TextRunResolver to disambiguate same-family different-size hits.
        public readonly Dictionary<FontKey, FontHandle> FontCache = new(8);

        public void Deactivate() {
            BrushCache.Clear();
            FontCache.Clear();
        }
    }

    // Composite key for gradient-Brush memoization. Identity equality on the
    // Gradient ref (BackgroundResolver hands out the same LinearGradient for
    // a given raw text). BackgroundTile is a struct compared by field; the
    // Nullable<> wrapper is sidestepped by storing HasTile + a flat struct.
    internal readonly struct GradientBrushKey : System.IEquatable<GradientBrushKey> {
        public readonly Paint.Gradient Gradient;
        public readonly bool HasTile;
        public readonly BackgroundTile Tile;

        public GradientBrushKey(Paint.Gradient gradient, BackgroundTile? tile) {
            Gradient = gradient;
            HasTile = tile.HasValue;
            Tile = tile.HasValue ? tile.Value : default;
        }

        public bool Equals(GradientBrushKey other) {
            if (!ReferenceEquals(Gradient, other.Gradient)) return false;
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

        public override bool Equals(object obj) => obj is GradientBrushKey k && Equals(k);

        public override int GetHashCode() {
            unchecked {
                int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Gradient);
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

    // Composite key for FontHandle memoization. Reusing FontHandle directly as a
    // key would work (it's already an IEquatable struct), but we want to tie the
    // cache lookup to the four parse-time parameters before BuildFont runs, so we
    // can hit before allocating any intermediate strings.
    internal readonly struct FontKey : System.IEquatable<FontKey> {
        public readonly string Family;
        public readonly double Size;
        public readonly int Weight;
        public readonly FontStyle Style;

        public FontKey(string family, double size, int weight, FontStyle style) {
            Family = family;
            Size = size;
            Weight = weight;
            Style = style;
        }

        public bool Equals(FontKey other) {
            return Family == other.Family
                && Size == other.Size
                && Weight == other.Weight
                && Style == other.Style;
        }

        public override bool Equals(object obj) => obj is FontKey k && Equals(k);

        public override int GetHashCode() {
            unchecked {
                int h = Family != null ? Family.GetHashCode() : 0;
                h = (h * 397) ^ Size.GetHashCode();
                h = (h * 397) ^ Weight;
                h = (h * 397) ^ (int)Style;
                return h;
            }
        }
    }

    // Stateless pop commands have no per-instance fields, so a single shared
    // instance per kind is safe to splice into any PaintList. EmitBoxFromScratch
    // can produce up to four pops per box (clip + opacity + transform + filter)
    // and the previous code allocated a fresh wrapper for each pop on every
    // Convert. Singletons cut that to zero.
    internal static class PaintCommandSingletons {
        public static readonly PopClipCommand PopClip = new();
        public static readonly PopOpacityCommand PopOpacity = new();
        public static readonly PopTransformCommand PopTransform = new();
        public static readonly PopFilterCommand PopFilter = new();
        public static readonly PopClipPathCommand PopClipPath = new();
        public static readonly PopMaskCommand PopMask = new();
        public static readonly PopMixBlendModeCommand PopMixBlendMode = new();
        // CSS Compositing 1 §9 — pops an element-local background-blend scope.
        // The pop always uses the unified batcher PopBackgroundBlend() which
        // restores the blend-entry stack (mode, elementLocal, baseColor) exactly
        // like PopMixBlendMode restores the page-backdrop blend stack.
        public static readonly PopBackgroundBlendCommand PopBackgroundBlend = new();
    }
}
