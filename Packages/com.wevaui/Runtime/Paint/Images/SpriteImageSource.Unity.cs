#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace Weva.Paint.Images {
    // Wraps a `UnityEngine.Sprite` (atlas-aware texture handle) for the
    // image pipeline. The renderer reads `Texture` for binding and
    // `UvRect` for the in-atlas sub-region; non-atlased sprites still
    // come through as `(0,0,1,1)` UVs so consumers can treat the source
    // uniformly.
    //
    // Why both `Sprite` and `Texture2D` adapters: a Sprite knows its
    // sub-region inside a packed atlas, so `UvRect` is precise; a raw
    // Texture2D is always the full image. Authors and registry
    // implementers pick whichever fits their asset pipeline.
    public sealed class SpriteImageSource : IImageSource, IImageNineSliceSource {
        public Sprite Sprite { get; }
        public Texture2D Texture { get; }

        public int Width { get; }
        public int Height { get; }
        public ImageNineSlice NineSlice { get; }

        // UV rect inside `Texture`. (0,0,1,1) for non-atlased sprites.
        // Renderer multiplies the brush's source-rect (from CSS) against
        // this to get the final sampled region.
        public UnityEngine.Rect UvRect { get; }

        public SpriteImageSource(Sprite sprite) {
            Sprite = sprite;
            if (sprite == null) {
                Texture = null;
                Width = 0;
                Height = 0;
                UvRect = new UnityEngine.Rect(0, 0, 1, 1);
                NineSlice = default;
                return;
            }
            Texture = sprite.texture;
            Width = (int)sprite.rect.width;
            Height = (int)sprite.rect.height;
            // Sprite.textureRect / sprite.texture.width gives the UV
            // sub-region in 0..1 normalized atlas space.
            float tw = Texture != null ? Texture.width : 1f;
            float th = Texture != null ? Texture.height : 1f;
            var tr = sprite.textureRect;
            UvRect = new UnityEngine.Rect(tr.x / tw, tr.y / th, tr.width / tw, tr.height / th);
            // Unity Sprite.Create documents border as X=left, Y=bottom, Z=right, W=top.
            var b = sprite.border;
            NineSlice = new ImageNineSlice(b.w, b.z, b.y, b.x);
        }

        public bool TryGetNineSlice(out ImageNineSlice slice) {
            slice = NineSlice;
            return !slice.IsEmpty;
        }
    }
}
#endif
