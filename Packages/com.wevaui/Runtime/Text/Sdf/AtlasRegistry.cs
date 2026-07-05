using System.Collections.Generic;
using Weva.Text.TextCore;
#if UNITY_2023_1_OR_NEWER
using UnityEngine;
#endif

namespace Weva.Text.Sdf {
    // AtlasRegistry maps a (face -> GlyphAtlas) pair so the URP backend can bind
    // the right Texture2D for the SDF text shader without dragging the whole
    // SdfFontMetrics surface across the seam.
    //
    // Two registration paths:
    //   1. RegisterAtlas(face, atlas) — pure-C# (headless) registration that
    //      stores the GlyphAtlas instance. The Texture2D accessor returns the
    //      atlas's underlying Unity texture when running in Unity (the Unity-
    //      partial of GlyphAtlas owns it), null in headless tests.
    //   2. The renderer can also RegisterAtlas(face, GlyphAtlas) and later
    //      query GetAtlas(face) to walk the slot table directly.
    //
    // v1 is single-page-per-face. CharacterFallback returns the page that
    // actually contains the glyph, so a multi-font run produces multiple
    // GlyphQuad batches keyed by face.
    //
    // AtlasIds: a stable integer identifier per registered GlyphAtlas. Used by
    // UIBatcher.SubmitGlyphQuads to disambiguate distinct atlases inside a
    // single frame so the renderer's _GlyphAtlas binding is correct per draw.
    // ID 0 is reserved for "no atlas / fallback".
    //
    // Thread safety (RC2): mutations and reads are guarded by `gate`. Atlas
    // registration is plausibly reachable from off-main-thread paths — a font
    // asset loaded via Addressables can fire its completion continuation on a
    // worker thread depending on configuration, and that path eventually walks
    // back through SdfBootstrap → RegisterAtlas. The lock is uncontended in
    // steady state (the per-frame paint path only Read s — `GetAtlasId` and
    // `GetTextureById` — and they enter and leave the same gate cheaply) so
    // the cost is a negligible interlocked check. The mark-as-color /
    // mark-as-coverage paths also enter the lock because TMP_FontAsset
    // registration can interleave with paint.
    public static class AtlasRegistry {
        static readonly Dictionary<FaceInfo, GlyphAtlas> atlases = new();
        static readonly Dictionary<GlyphAtlas, int> atlasIds = new();
        static readonly Dictionary<int, GlyphAtlas> atlasById = new();
        // Atlases whose underlying texture is a 4-channel RGBA color image
        // (e.g. a TMP COLOR-mode emoji font). The renderer keys the
        // _TEXT_COLOR shader variant off membership in this set so the
        // SDF-coverage threshold is replaced with a direct RGBA sample.
        static readonly HashSet<int> colorAtlasIds = new();
        // Atlases that store already-rasterized alpha coverage rather than an
        // SDF distance field. Small hinted TextCore atlases use the same R8 /
        // Alpha8 formats as SDF atlases, so the renderer must key this off an
        // explicit registration flag instead of texture format.
        static readonly HashSet<int> coverageAtlasIds = new();
        static readonly object gate = new object();
        static int nextAtlasId = 1;

        // NG4: returns true when the (face, atlas) pair is recorded; false when
        // the inputs are degenerate (invalid face or null atlas). Previously
        // void — callers had no signal that their atlas binding was silently
        // skipped (e.g. a stale FaceInfo from a font that failed to load). The
        // void→bool change is source-compatible because the previous overload
        // returned no value; existing callers that don't read the result keep
        // working unchanged.
        public static bool RegisterAtlas(FaceInfo face, GlyphAtlas atlas) {
            if (!face.IsValid || atlas == null) return false;
            lock (gate) {
                atlases[face] = atlas;
                if (!atlasIds.ContainsKey(atlas)) {
                    int id = nextAtlasId++;
                    atlasIds[atlas] = id;
                    atlasById[id] = atlas;
                }
                return true;
            }
        }

        public static bool UnregisterAtlas(FaceInfo face) {
            lock (gate) {
                if (!atlases.TryGetValue(face, out var atlas)) return false;
                atlases.Remove(face);
                // Don't remove the id mapping — atlas may still be registered under another face.
                // Tests that need fully reset state call Clear().
                return true;
            }
        }

        public static GlyphAtlas GetAtlas(FaceInfo face) {
            lock (gate) {
                return atlases.TryGetValue(face, out var atlas) ? atlas : null;
            }
        }

        public static bool TryGetAtlas(FaceInfo face, out GlyphAtlas atlas) {
            lock (gate) {
                return atlases.TryGetValue(face, out atlas);
            }
        }

        public static int GetAtlasId(GlyphAtlas atlas) {
            if (atlas == null) return 0;
            lock (gate) {
                return atlasIds.TryGetValue(atlas, out var id) ? id : 0;
            }
        }

        public static GlyphAtlas GetAtlasById(int id) {
            lock (gate) {
                return atlasById.TryGetValue(id, out var atlas) ? atlas : null;
            }
        }

        public static int Count {
            get {
                lock (gate) { return atlases.Count; }
            }
        }
        public static int DistinctAtlasCount {
            get {
                lock (gate) { return atlasIds.Count; }
            }
        }

        // Mark a previously-registered atlas as color (RGBA-sampled). Caller
        // must have called RegisterAtlas first so the atlas has an id; passing
        // an unregistered atlas is a silent no-op. The flag is read by the URP
        // renderer to choose between the SDF and RGBA shader variants.
        public static void MarkColorAtlas(GlyphAtlas atlas) {
            if (atlas == null) return;
            lock (gate) {
                if (!atlasIds.TryGetValue(atlas, out var id)) return;
                colorAtlasIds.Add(id);
            }
        }

        public static void MarkColorAtlasById(int id) {
            if (id == 0) return;
            lock (gate) {
                colorAtlasIds.Add(id);
            }
        }

        public static bool IsColorAtlasId(int id) {
            if (id == 0) return false;
            lock (gate) {
                return colorAtlasIds.Contains(id);
            }
        }

        public static void MarkCoverageAtlas(GlyphAtlas atlas) {
            if (atlas == null) return;
            lock (gate) {
                if (!atlasIds.TryGetValue(atlas, out var id)) return;
                coverageAtlasIds.Add(id);
            }
        }

        public static void MarkCoverageAtlasById(int id) {
            if (id == 0) return;
            lock (gate) {
                coverageAtlasIds.Add(id);
            }
        }

        public static bool IsCoverageAtlasId(int id) {
            if (id == 0) return false;
            lock (gate) {
                return coverageAtlasIds.Contains(id);
            }
        }

        public static void Clear() {
            lock (gate) {
                atlases.Clear();
                atlasIds.Clear();
                atlasById.Clear();
                colorAtlasIds.Clear();
                coverageAtlasIds.Clear();
                nextAtlasId = 1;
            }
        }

#if UNITY_2023_1_OR_NEWER
        public static Texture2D GetTexture(FaceInfo face) {
            lock (gate) {
                return atlases.TryGetValue(face, out var atlas) ? atlas.Texture : null;
            }
        }

        public static Texture2D GetTextureById(int id) {
            // GetAtlasById already enters the lock; the Texture access itself
            // is a property read on a GlyphAtlas instance whose Texture field
            // is only assigned at construction in the Unity partial.
            var atlas = GetAtlasById(id);
            return atlas?.Texture;
        }
#endif

        // Read-only snapshot. The underlying dict is mutated only under `gate`,
        // so callers iterating the enumeration concurrently with a registration
        // would observe partial state — copy into a list before iterating if
        // that matters. The existing call sites only invoke this from main-
        // thread debug / test contexts so the contention risk is academic.
        public static IEnumerable<FaceInfo> RegisteredFaces {
            get {
                lock (gate) {
                    // Copy to a list under the lock so the caller can safely
                    // enumerate even if another thread registers in the
                    // meantime. The cost is bounded by registered-face count
                    // (typically O(1)–O(10)) so this is negligible.
                    return new List<FaceInfo>(atlases.Keys);
                }
            }
        }
    }
}
