#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace Weva.Paint.Images {
    // Wraps a `UnityEngine.Texture2D` for the framework's image pipeline.
    // Renderer backends downcast `IImageSource` → `Texture2DImageSource`
    // to recover the texture for sampling. This adapter is unconditional —
    // any backend that knows how to bind a `Texture2D` (URP material slot,
    // IMGUI `GUI.DrawTexture`) can consume it.
    public sealed class Texture2DImageSource : IImageSource {
        public Texture2D Texture { get; }
        public int Width => Texture != null ? Texture.width : 0;
        public int Height => Texture != null ? Texture.height : 0;

        public Texture2DImageSource(Texture2D texture) {
            Texture = texture;
        }
    }
}
#endif
