#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace Weva.Paint.Images {
    // Wraps a RenderTexture so game code can register live camera/portrait
    // buffers in the same IImageRegistry used by Texture2D and Sprite assets.
    public sealed class RenderTextureImageSource : IImageSource {
        public RenderTexture Texture { get; }
        public int Width => Texture != null ? Texture.width : 0;
        public int Height => Texture != null ? Texture.height : 0;

        public RenderTextureImageSource(RenderTexture texture) {
            Texture = texture;
        }
    }
}
#endif
