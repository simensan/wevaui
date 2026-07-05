using System.Collections.Generic;

namespace Weva.Paint.Images {
    // Default `IImageRegistry` implementation. Synchronous dictionary
    // lookup; case-sensitive on the handle string. Game code populates it
    // at startup (`Register("ui/heart-icon", textureWrap)`) and hands it
    // to whichever rendering backend consumes the paint list. Registry
    // ownership: callers manage lifetime; the framework holds only a
    // reference.
    //
    // Thread safety: not synchronized. UI runs on the main thread, and
    // assets are typically registered up-front. If background loads are
    // needed, swap in a wrapper that takes a lock.
    public sealed class InMemoryImageRegistry : IVersionedImageRegistry {
        readonly Dictionary<string, IImageSource> map = new();

        public int Count => map.Count;
        public int Version { get; private set; }

        public void Register(string handle, IImageSource source) {
            if (string.IsNullOrEmpty(handle))
                throw new System.ArgumentException("handle must be non-empty", nameof(handle));
            if (source == null)
                throw new System.ArgumentNullException(nameof(source));
            if (map.TryGetValue(handle, out var existing) && SourcesEquivalent(existing, source)) {
                return;
            }
            map[handle] = source;
            Version++;
        }

        public bool Unregister(string handle) {
            if (string.IsNullOrEmpty(handle)) return false;
            bool removed = map.Remove(handle);
            if (removed) Version++;
            return removed;
        }

        public void Clear() {
            if (map.Count == 0) return;
            map.Clear();
            Version++;
        }

        public bool TryResolve(string handle, out IImageSource source) {
            if (string.IsNullOrEmpty(handle)) {
                source = null;
                return false;
            }
            return map.TryGetValue(handle, out source);
        }

        static bool SourcesEquivalent(IImageSource a, IImageSource b) {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
#if UNITY_5_3_OR_NEWER
            if (a is SpriteImageSource sa && b is SpriteImageSource sb) {
                if (ReferenceEquals(sa.Sprite, sb.Sprite)) return true;
                return ReferenceEquals(sa.Texture, sb.Texture)
                       && sa.Width == sb.Width
                       && sa.Height == sb.Height
                       && SameRect(sa.UvRect, sb.UvRect)
                       && sa.NineSlice.Equals(sb.NineSlice);
            }
            if (a is Texture2DImageSource ta && b is Texture2DImageSource tb) {
                return ReferenceEquals(ta.Texture, tb.Texture);
            }
            if (a is RenderTextureImageSource ra && b is RenderTextureImageSource rb) {
                return ReferenceEquals(ra.Texture, rb.Texture);
            }
#endif
            return false;
        }

#if UNITY_5_3_OR_NEWER
        static bool SameRect(UnityEngine.Rect a, UnityEngine.Rect b) {
            return a.x == b.x
                   && a.y == b.y
                   && a.width == b.width
                   && a.height == b.height;
        }
#endif
    }
}
