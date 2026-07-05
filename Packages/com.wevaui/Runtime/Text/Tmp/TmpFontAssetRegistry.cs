#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using System.Collections.Generic;
using TMPro;
using Weva.Paint;

namespace Weva.Text.Tmp {
    // TmpFontAssetRegistry is the static API authors call at app startup to
    // associate a generic CSS family name ("sans-serif", "serif", etc.) with a
    // pre-baked TMP_FontAsset. SdfBootstrap.PickBest checks this registry
    // before constructing SdfFontMetrics; if a TMP source is registered for
    // the requested family, the bootstrap returns a TmpFontMetrics instead so
    // glyph queries are served from the TMP atlas + kerning table.
    //
    // Empty registry => transparent fall-through to the existing SDF rasterizer
    // path. Authors who don't want TMP integration simply never call
    // RegisterFontAsset.
    //
    // Key normalization: family names are case-insensitive. "Sans-Serif" and
    // "sans-serif" map to the same source.
    //
    // Fallback chains: each family stores a list of TmpFontAssetSources rather
    // than a single one. The element at index 0 is the PRIMARY face (the one
    // returned by TryGet — preserves the v1 contract). AddFallback appends to
    // the chain so the shaper can walk past the primary when a codepoint isn't
    // present in its character table. Use case: register Liberation Sans as
    // primary plus Segoe UI Emoji as a fallback, and ShapeTmp will resolve
    // emoji codepoints from the second asset's atlas while keeping ASCII on
    // the first.
    //
    // Thread safety: single-threaded (Unity main thread), matching the rest of
    // the UI engine.
    public static class TmpFontAssetRegistry {
        static readonly Dictionary<string, List<TmpFontAssetSource>> chains =
            new(System.StringComparer.OrdinalIgnoreCase);

        // Register a TMP_FontAsset under the given family name as the PRIMARY
        // face. Replaces any existing primary AND clears its fallback chain —
        // re-registering a family is treated as a fresh start. Subsequent
        // SdfBootstrap.PickBest calls for this family route through the
        // TMP-backed metrics. Pass a null asset to remove the registration
        // (and discard the entire chain).
        public static void RegisterFontAsset(string familyName, TMP_FontAsset asset) {
            if (string.IsNullOrEmpty(familyName)) return;
            if (asset == null) {
                chains.Remove(familyName);
                return;
            }
            ReplacePrimaryKeepFallbacks(familyName, new TmpFontAssetSource(asset));
        }

        // Register a pre-built TmpFontAssetSource as the PRIMARY face. Useful
        // for tests that need to inject a mocked source without going through
        // TMP_FontAsset. Replaces the primary slot but preserves any fallbacks
        // already on the chain (same as RegisterFontAsset).
        public static void RegisterSource(string familyName, TmpFontAssetSource source) {
            if (string.IsNullOrEmpty(familyName)) return;
            if (source == null) {
                chains.Remove(familyName);
                return;
            }
            ReplacePrimaryKeepFallbacks(familyName, source);
        }

        // Replaces slot 0 (the primary face) for `familyName`, keeping any
        // fallback entries already in the chain. Without this, re-registering
        // the primary (e.g. a play-mode controller pinning Segoe UI SDF over
        // SdfBootstrap's editor auto-register) wiped the emoji/symbol
        // fallbacks SdfBootstrap had wired up, and non-ASCII codepoints like
        // ✕ (U+2715), — (U+2014), and the dual-presentation emoji set
        // (↩ ⏸ ⚠ etc.) silently dropped to placeholder glyphs.
        static void ReplacePrimaryKeepFallbacks(string familyName, TmpFontAssetSource newPrimary) {
            if (chains.TryGetValue(familyName, out var existing) && existing != null && existing.Count > 0) {
                existing[0] = newPrimary;
            } else {
                chains[familyName] = new List<TmpFontAssetSource> { newPrimary };
            }
        }

        // Append a TMP_FontAsset to the family's fallback chain. The shaper
        // tries the primary face first; on codepoint miss it walks through
        // each fallback in registration order. No-op if no primary is
        // registered for the family yet (callers must RegisterFontAsset first
        // — order matters because the primary anchors the metrics + line
        // height for the run).
        public static void AddFallback(string familyName, TMP_FontAsset asset) {
            if (string.IsNullOrEmpty(familyName) || asset == null) return;
            if (!chains.TryGetValue(familyName, out var chain) || chain == null || chain.Count == 0) return;
            // Idempotent: a controller that calls AddFallback from both
            // OnEnable and Start (the EnsureWired pattern) would otherwise
            // duplicate the entry every play-mode entry, slowing the
            // character-fallback walk and inflating allocation in tests that
            // hot-restart the document.
            for (int i = 0; i < chain.Count; i++) {
                if (chain[i]?.Asset == asset) return;
            }
            chain.Add(new TmpFontAssetSource(asset));
        }

        // Append a pre-built TmpFontAssetSource to the family's fallback chain.
        public static void AddFallback(string familyName, TmpFontAssetSource source) {
            if (string.IsNullOrEmpty(familyName) || source == null) return;
            if (!chains.TryGetValue(familyName, out var chain) || chain == null || chain.Count == 0) return;
            for (int i = 0; i < chain.Count; i++) {
                if (ReferenceEquals(chain[i], source)) return;
                if (chain[i]?.Asset != null && chain[i].Asset == source.Asset) return;
            }
            chain.Add(source);
        }

        public static bool Unregister(string familyName) {
            if (string.IsNullOrEmpty(familyName)) return false;
            return chains.Remove(familyName);
        }

        // Returns the PRIMARY face for the family. Preserves the v1 contract:
        // callers that don't care about fallbacks see the same shape they
        // always did.
        public static bool TryGet(string familyName, out TmpFontAssetSource source) {
            source = null;
            if (string.IsNullOrEmpty(familyName)) return false;
            if (!chains.TryGetValue(familyName, out var chain) || chain == null || chain.Count == 0) return false;
            source = chain[0];
            return source != null;
        }

        // Returns the primary + fallback chain for the family in registration
        // order. Empty enumerable when the family is not registered. Callers
        // should NOT cache the returned list — it is the live registry list
        // and may be mutated by subsequent AddFallback calls.
        public static IReadOnlyList<TmpFontAssetSource> GetChain(string familyName) {
            if (string.IsNullOrEmpty(familyName)) return System.Array.Empty<TmpFontAssetSource>();
            if (!chains.TryGetValue(familyName, out var chain) || chain == null) return System.Array.Empty<TmpFontAssetSource>();
            return chain;
        }

        // Returns the depth of the fallback chain for the family (1 = primary
        // only, 0 = unregistered). Useful for diagnostics + tests.
        public static int ChainLength(string familyName) {
            if (string.IsNullOrEmpty(familyName)) return 0;
            return chains.TryGetValue(familyName, out var chain) && chain != null ? chain.Count : 0;
        }

        public static bool IsRegistered(string familyName) {
            return !string.IsNullOrEmpty(familyName)
                && chains.TryGetValue(familyName, out var chain)
                && chain != null
                && chain.Count > 0;
        }

        public static int Count => chains.Count;

        public static void Clear() {
            chains.Clear();
            lookupMisses = null;
            lookupMissList = null;
            emojiMisses = null;
        }

        // Snapshot record for a single recorded variant miss. Returned by
        // GetVariantMisses so editor tooling can rebuild the (family, weight,
        // style) tuple — the internal dedupe key is hashed and lossy.
        public readonly struct VariantMiss {
            public readonly string Family;
            public readonly int Weight;
            public readonly FontStyle Style;
            public VariantMiss(string family, int weight, FontStyle style) {
                Family = family;
                Weight = weight;
                Style = style;
            }
        }

        // Returns a snapshot of currently-recorded variant misses (one entry
        // per unique tuple seen this session). The list is a copy — callers
        // can iterate and mutate the registry without enumeration hazards.
        public static System.Collections.Generic.IReadOnlyList<VariantMiss> GetVariantMisses() {
            if (lookupMissList == null || lookupMissList.Count == 0)
                return System.Array.Empty<VariantMiss>();
            return lookupMissList.ToArray();
        }

        // Returns a snapshot of currently-recorded emoji codepoint misses.
        public static System.Collections.Generic.IReadOnlyList<uint> GetEmojiMisses() {
            if (emojiMisses == null || emojiMisses.Count == 0)
                return System.Array.Empty<uint>();
            var arr = new uint[emojiMisses.Count];
            int i = 0;
            foreach (var cp in emojiMisses) arr[i++] = cp;
            return arr;
        }

        // Editor tooling calls this after baking + registering a new variant
        // so the audit window can drop it from the displayed list. Removes
        // both the hashed dedupe entry (so future misses re-fire normally)
        // and the readable record.
        public static void ForgetVariantMiss(string familyName, int weight, FontStyle style) {
            if (string.IsNullOrEmpty(familyName) || lookupMissList == null) return;
            for (int i = lookupMissList.Count - 1; i >= 0; i--) {
                var m = lookupMissList[i];
                if (m.Family == familyName && m.Weight == weight && m.Style == style) {
                    lookupMissList.RemoveAt(i);
                }
            }
            if (lookupMisses != null) {
                long key = ((long)familyName.GetHashCode() << 16) ^ ((long)weight << 4) ^ (long)style;
                lookupMisses.Remove(key);
            }
        }

        public static void ForgetEmojiMiss(uint codepoint) {
            emojiMisses?.Remove(codepoint);
        }

        // ----- Phase 1 font-variant diagnostics ---------------------------
        //
        // When CSS requests a font-weight or font-style for which no exact-
        // match TmpFontAssetSource is registered, the shaping path silently
        // falls through to the primary face — producing faux-bold or upright
        // output that doesn't match what the author asked for. Authors have
        // no signal that the variant wasn't picked up.
        //
        // ReportLookup is called from the shaping path with the resolved
        // (family, weight, style) tuple. If no source in the chain matches
        // the requested variant, we emit ONE Debug.LogWarning per unique
        // tuple per session via UICssDiagnostics. The dedupe HashSet is
        // lazily allocated so registries that never see a miss don't pay
        // the allocation. Misses are skipped silently when:
        //   - weight is 400 AND style is Normal (the expected path).
        //   - the chain is empty (unrelated — no TMP path active).
        //   - the variant is already present in the chain.
        static HashSet<long> lookupMisses;
        // Parallel list of structured records (Family/Weight/Style) for the
        // hashed dedupe set above. Lets editor tooling enumerate the misses
        // without having to reverse the hash. Kept in sync with lookupMisses.
        static List<VariantMiss> lookupMissList;

        public static void ReportLookup(string familyName, int weight, FontStyle style) {
            if (string.IsNullOrEmpty(familyName)) return;
            // Expected path: regular weight + upright style. No warning.
            bool isItalic = style == FontStyle.Italic || style == FontStyle.Oblique;
            if (weight <= 400 && !isItalic) return;
            if (!chains.TryGetValue(familyName, out var chain) || chain == null || chain.Count == 0) return;
            // Check if any source's face matches the requested variant.
            int requestedStyleFlag = isItalic
                ? Weva.Text.TextCore.FaceInfo.StyleItalic
                : Weva.Text.TextCore.FaceInfo.StyleNormal;
            for (int i = 0; i < chain.Count; i++) {
                var s = chain[i];
                if (s == null) continue;
                var face = s.Face;
                bool weightOk = weight <= 400 ? face.Weight <= 400 : face.Weight >= 700;
                bool styleOk = face.StyleFlags == requestedStyleFlag;
                if (weightOk && styleOk) return;
            }
            // Dedupe key packs family hash + weight + style flag.
            long key = ((long)familyName.GetHashCode() << 16) ^ ((long)weight << 4) ^ (long)style;
            lookupMisses ??= new HashSet<long>();
            if (!lookupMisses.Add(key)) return;
            lookupMissList ??= new List<VariantMiss>();
            lookupMissList.Add(new VariantMiss(familyName, weight, style));
            string detail = isItalic
                ? "font-style: italic for '" + familyName + "' has no matching TMP_FontAsset — falling back to upright"
                : "font-weight: " + weight + " for '" + familyName + "' has no matching TMP_FontAsset — falling back to faux-bold from weight 400";
            Weva.Diagnostics.UICssDiagnostics.Warn("tmp-font-variant", detail);
        }

        // Emoji codepoint miss reporting. Called from the shaping path when
        // a codepoint is not present in any face of the chain (neither the
        // primary text face nor any color/SDF emoji fallback). We dedupe per
        // codepoint per session via a lazily-allocated HashSet — emoji are
        // sparse in typical content so the set stays small.
        //
        // Filter: only emoji-plane codepoints qualify. Skipping ASCII /
        // Latin-1 keeps the noise floor low — a missing 'A' is a far more
        // serious problem than a missing 'astonished face' and a separate
        // diagnostic surface would be more appropriate (font load failure).
        static HashSet<uint> emojiMisses;
        public static void ReportEmojiMiss(uint codepoint) {
            // Heuristic: BMP symbol/dingbat range + supplementary multilingual
            // plane emoji blocks. Skip text-plane codepoints (< U+2000).
            bool isEmojiRange =
                (codepoint >= 0x2000 && codepoint <= 0x2BFF) || // dingbats, misc symbols, arrows
                (codepoint >= 0x1F000 && codepoint <= 0x1FBFF); // SMP emoji blocks
            if (!isEmojiRange) return;
            emojiMisses ??= new HashSet<uint>();
            if (!emojiMisses.Add(codepoint)) return;
            string hex = codepoint.ToString("X4");
            Weva.Diagnostics.UICssDiagnostics.Warn("tmp-emoji-miss",
                "emoji U+" + hex + " not present in any registered TMP atlas (neither COLOR nor SDF)");
        }

        // Returns the primary source for the first registered family, or null
        // if the registry is empty. Used by integration tests that don't care
        // which family was registered, only that one was.
        public static TmpFontAssetSource Any() {
            foreach (var kv in chains) {
                if (kv.Value != null && kv.Value.Count > 0) return kv.Value[0];
            }
            return null;
        }
    }
}
#endif
