using System;
using System.Collections.Generic;

namespace Weva.Paint {
    public enum MaskMode {
        MatchSource,
        Alpha,
        Luminance,
    }

    public enum MaskComposite {
        Add,
        Subtract,
        Intersect,
        Exclude,
    }

    public sealed class MaskDefinition {
        public const int MaxRenderedLayers = 4;

        public IReadOnlyList<MaskLayer> Layers { get; }
        public int Count => Layers.Count;
        public bool IsEmpty => Layers.Count == 0;

        public MaskDefinition(IReadOnlyList<MaskLayer> layers) {
            if (layers == null || layers.Count == 0) {
                Layers = new List<MaskLayer>(0).AsReadOnly();
                return;
            }
            var copy = new List<MaskLayer>(layers.Count);
            for (int i = 0; i < layers.Count; i++) {
                if (layers[i] != null) copy.Add(layers[i]);
            }
            Layers = copy.AsReadOnly();
        }

        public static MaskDefinition Single(MaskLayer layer) {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            return new MaskDefinition(new[] { layer });
        }

        public MaskDefinition Translate(double dx, double dy) {
            if (IsEmpty) return this;
            var translated = new List<MaskLayer>(Layers.Count);
            for (int i = 0; i < Layers.Count; i++) {
                translated.Add(Layers[i].Translate(dx, dy));
            }
            return new MaskDefinition(translated);
        }
    }

    public sealed class MaskLayer {
        public Rect Bounds { get; }
        public Brush Brush { get; }
        public MaskMode Mode { get; }
        public MaskComposite Composite { get; }
        public BackgroundTile? Tile { get; }

        // B16 — set to true on the synthetic path-clip coverage layer emitted by
        // BoxToPaintConverter when clip-path: path(...) is present. The software
        // rasterizer (SoftwareRasterizer.SampleMaskLayerAlpha) skips layers with this
        // flag set because the software path already clips via
        // SoftwareRasterizer.PixelPassesGlobalClip (PathClipPathShape.Contains).
        // Without the flag the two paths would multiply — harmless at 0/1 but
        // incorrect at AA edges (intermediate coverage × intermediate coverage ≠
        // intermediate coverage). The GPU path uses this layer normally.
        public bool IsSyntheticClipMask { get; }

        public MaskLayer(Rect bounds, Brush brush, MaskMode mode, MaskComposite composite) {
            this.Bounds = bounds;
            this.Brush = brush;
            this.Mode = mode;
            this.Composite = composite;
            this.Tile = null;
            this.IsSyntheticClipMask = false;
        }

        public MaskLayer(Rect bounds, Brush brush, MaskMode mode, MaskComposite composite, BackgroundTile? tile) {
            Bounds = bounds;
            Brush = brush;
            Mode = mode;
            Composite = composite;
            Tile = tile;
            IsSyntheticClipMask = false;
        }

        // B16 — constructor for synthetic clip mask layers.
        public MaskLayer(Rect bounds, Brush brush, MaskMode mode, MaskComposite composite, BackgroundTile? tile,
                         bool isSyntheticClipMask) {
            Bounds = bounds;
            Brush = brush;
            Mode = mode;
            Composite = composite;
            Tile = tile;
            IsSyntheticClipMask = isSyntheticClipMask;
        }

        public MaskLayer Translate(double dx, double dy) {
            return new MaskLayer(
                new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height),
                Brush,
                Mode,
                Composite,
                Tile,
                IsSyntheticClipMask);
        }
    }
}
