#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using UnityEngine;

namespace Weva.Rendering.URP {
    // Static registry that maps an int "atlas id" to the concrete
    // Texture2D for image batches. Mirrors AtlasRegistry for SDF glyphs:
    // the batcher hands out a stable id per Texture2D it sees during a
    // frame, packs that id into UIBatchKey, and the RenderGraph pass
    // uses GetTextureById(id) to bind the correct texture before each
    // DrawMeshInstanced call.
    //
    // Why static: Texture2D registration is global by nature — multiple
    // UIDocuments in the same scene can reference the same sprite, and
    // we want them to coalesce into one atlas binding. The dictionary
    // grows for the session; ClearAll() resets between tests.
    //
    // Id 0 is reserved for "no image" so an unset AtlasId on a non-text
    // batch falls back to the magenta path.
    //
    // Thread safety (RC1): mutations are guarded by `gate`. The expected
    // caller is the Unity main thread (UIBatcher.SubmitFillRect path) but
    // image registration can plausibly arrive from an Addressables /
    // sprite-atlas completion continuation on a worker thread depending on
    // user configuration, so we mirror UIPaintSourceRegistry's lock pattern
    // rather than relying purely on a main-thread convention. Read-only
    // probes (`GetTextureById`) also enter the lock — the dict can be
    // mutated under us by a concurrent `Register`, and a `TryGetValue` on
    // a struct enumerator-touching dict is not safe under contention.
    public static class UIImageAtlasRegistry {
        static readonly Dictionary<Texture2D, int> textureToId = new();
        static readonly Dictionary<int, Texture2D> idToTexture = new();
        static readonly object gate = new object();
        static int nextId = 1;

        public static int Register(Texture2D texture) {
            if (texture == null) return 0;
            lock (gate) {
                if (textureToId.TryGetValue(texture, out var existing)) return existing;
                int id = nextId++;
                textureToId[texture] = id;
                idToTexture[id] = texture;
                return id;
            }
        }

        public static Texture2D GetTextureById(int id) {
            if (id <= 0) return null;
            lock (gate) {
                return idToTexture.TryGetValue(id, out var tex) ? tex : null;
            }
        }

        public static void ClearAll() {
            lock (gate) {
                textureToId.Clear();
                idToTexture.Clear();
                nextId = 1;
            }
        }
    }
}
#endif
