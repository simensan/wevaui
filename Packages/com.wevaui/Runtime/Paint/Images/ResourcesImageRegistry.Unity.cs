#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using UnityEngine;

namespace Weva.Paint.Images {
    // Resources/-folder backed image registry. Author writes
    // `<img src="ui/heart">` → file at `Assets/Resources/ui/heart.png`.
    // Loads synchronously on first lookup; caches the result so repeat
    // resolves don't re-hit `Resources.Load`.
    //
    // Trade-offs vs Addressables:
    //   * EVERYTHING under `Resources/` ships in the player whether
    //     referenced or not — bloat for large projects.
    //   * No async loading; first frame that touches a sprite stalls.
    //   * No dependency tracking, no remote loading.
    //
    // Use this for prototypes, demos, and small games where bundle
    // size doesn't matter. For production, write a custom
    // `IImageRegistry` over Unity Addressables.
    //
    // Tries `Sprite` first, falling back to `Texture2D` so authors can
    // ship either type under the same handle. The framework only cares
    // about `IImageSource`; backends downcast to the concrete type they
    // know how to sample.
    public sealed class ResourcesImageRegistry : IImageRegistry {
        readonly Dictionary<string, IImageSource> cache = new();

        public bool TryResolve(string handle, out IImageSource source) {
            if (string.IsNullOrEmpty(handle)) {
                source = null;
                return false;
            }
            if (cache.TryGetValue(handle, out source)) {
                // Cached null means "we already looked and it doesn't
                // exist" — don't re-hit Resources.Load on every paint.
                return source != null;
            }

            var sprite = Resources.Load<Sprite>(handle);
            if (sprite != null) {
                source = new SpriteImageSource(sprite);
                cache[handle] = source;
                return true;
            }
            var texture = Resources.Load<Texture2D>(handle);
            if (texture != null) {
                source = new Texture2DImageSource(texture);
                cache[handle] = source;
                return true;
            }
            cache[handle] = null;
            source = null;
            // Once-per-handle warning so authors can tell why an `<img src=>`
            // is rendering nothing. ResourcesImageRegistry only finds assets
            // under `Assets/Resources/<handle>.{png,jpg,...}` — typical
            // mistake is leaving the image at `Assets/UI/...` outside Resources/.
            Weva.Diagnostics.UICssDiagnostics.Warn("image-load",
                "ResourcesImageRegistry: handle '" + handle + "' not found. " +
                "Expected asset at Assets/Resources/" + handle + ".{png,jpg,jpeg,tga} " +
                "as a Sprite or Texture2D. Move the asset under Resources/ OR " +
                "assign a different IImageRegistry to WevaDocument.ImageRegistry " +
                "(e.g. AddressablesImageRegistry for Addressables-based projects).");
            return false;
        }

        // Drops cached lookups so next access re-hits `Resources.Load`.
        // Useful when authors hot-swap an asset under `Resources/` and
        // want the registry to pick up the new instance without a
        // domain reload.
        public void ClearCache() {
            cache.Clear();
        }
    }
}
#endif
