using System.Collections.Generic;

namespace Weva.Css.Values {
    // CSS Fonts Level 4 §5.2 — font face matching algorithm (simplified).
    //
    // This class contains ONLY the pure matching math (no UnityEngine types,
    // no FontResolver refs) so it can live in the headless TestVerifyAll build.
    // FontResolver calls into this after collecting the registered face list.
    //
    // Simplifications vs full §5.2 (documented for the Unity-side reviewer):
    //   1. font-stretch is ignored — we don't ship variable-width faces.
    //   2. Variable-axis (true weight ranges as OTVar axes) are treated as
    //      static: any weight within [WeightMin, WeightMax] is a hit.
    //   3. Only italic/non-italic discrimination; oblique and oblique-angle
    //      ranges are treated as italic for matching purposes.
    //   4. unicode-range filtering is not done here (deferred to subsetter).
    //   5. font-display / loading state is not modelled — we assume all faces
    //      are "ready".
    //
    // Matching order (§5.2 step 4, condensed):
    //   A. Style axis: italic-requested → prefer italic faces, fall back to
    //      normal. Normal-requested → prefer normal faces, fall back to italic.
    //   B. Weight axis on the style-filtered set:
    //      i.  Exact range containment (desired weight ∈ [min, max]).
    //      ii. Nearest weight, directional:
    //          — desired ≥ 600: try heavier-than-desired first, then lighter.
    //          — desired < 600 (including 400): try lighter-than-desired first,
    //            then heavier.  (§5.2 special-cases 400/500 mutual fall-through,
    //            but that only applies to the font-weight *value* matching the
    //            face's declared range, which we approximate by the range
    //            midpoint for the directional comparison.)
    //      iii. If no face survives style filtering, repeat B on the full list
    //           (style fall-back).
    //
    // Thread safety: the algorithm itself is stateless; callers supply the list.
    public static class FontFaceMatcher {
        // A single registered face entry.
        public readonly struct FaceEntry {
            public readonly float WeightMin;
            public readonly float WeightMax;
            public readonly bool   IsItalic; // true for both italic and oblique
            public readonly string Path;

            public FaceEntry(float weightMin, float weightMax, bool isItalic, string path) {
                WeightMin = weightMin;
                WeightMax = weightMax;
                IsItalic  = isItalic;
                Path      = path;
            }

            // Representative weight for directional search — midpoint of range.
            public float RepWeight => (WeightMin + WeightMax) * 0.5f;
        }

        // Find the best matching path for (desiredWeight, wantItalic) from a
        // non-empty list of entries.  Returns null only when the list is empty.
        //
        // CSS Fonts L4 §5.2 — simplified (see class doc).
        public static string Match(IReadOnlyList<FaceEntry> faces, int desiredWeight, bool wantItalic) {
            if (faces == null || faces.Count == 0) return null;

            // Single-face fast path — allocation-free (no list filtering needed).
            if (faces.Count == 1) return faces[0].Path;

            // --- Style-axis filtering (§5.2 step 4a) ---
            // Build a lightweight presence test without allocating a new list
            // when possible. We do two passes: first try on preferred-style set,
            // then fall back to the full set if the preferred-style set is empty.
            bool preferredStylePresent = false;
            for (int i = 0; i < faces.Count; i++) {
                if (faces[i].IsItalic == wantItalic) { preferredStylePresent = true; break; }
            }

            string result = BestWeightMatch(faces, desiredWeight, wantItalic, preferredStylePresent);
            if (result != null) return result;
            // Style fall-back: search across all faces regardless of italic.
            return BestWeightMatch(faces, desiredWeight, wantItalic, false);
        }

        // Find the best-weight face from the list.
        // When filterByStyle==true, only entries whose IsItalic matches wantItalic
        // are considered; when false, all entries are considered.
        //
        // Returns null if no candidate survives the filter (only possible when
        // filterByStyle==true and no face matches).
        static string BestWeightMatch(
            IReadOnlyList<FaceEntry> faces,
            int desiredWeight,
            bool wantItalic,
            bool filterByStyle)
        {
            // Pass 1 — exact range containment.
            // A face covers the desired weight if desiredWeight ∈ [min, max].
            string exact = null;
            float  exactRep = float.MaxValue; // pick lowest rep-weight among ties
            for (int i = 0; i < faces.Count; i++) {
                var f = faces[i];
                if (filterByStyle && f.IsItalic != wantItalic) continue;
                if (desiredWeight >= f.WeightMin && desiredWeight <= f.WeightMax) {
                    float rep = f.RepWeight;
                    if (exact == null || System.Math.Abs(rep - desiredWeight) < System.Math.Abs(exactRep - desiredWeight)) {
                        exact    = f.Path;
                        exactRep = rep;
                    }
                }
            }
            if (exact != null) return exact;

            // Pass 2 — directional nearest-weight search (§5.2 §4.3).
            // desired ≥ 600: prefer heavier (ascending from desired), then lighter.
            // desired < 600:  prefer lighter  (descending from desired), then heavier.
            //
            // "Heavier" = closest face whose RepWeight > desiredWeight.
            // "Lighter"  = closest face whose RepWeight < desiredWeight.
            if (desiredWeight >= 600) {
                string heavier = NearestAbove(faces, desiredWeight, wantItalic, filterByStyle);
                if (heavier != null) return heavier;
                return NearestBelow(faces, desiredWeight, wantItalic, filterByStyle);
            } else {
                string lighter = NearestBelow(faces, desiredWeight, wantItalic, filterByStyle);
                if (lighter != null) return lighter;
                return NearestAbove(faces, desiredWeight, wantItalic, filterByStyle);
            }
        }

        // Nearest face whose RepWeight is strictly greater than desiredWeight,
        // minimising (RepWeight - desiredWeight).  Ties: first in list wins.
        static string NearestAbove(
            IReadOnlyList<FaceEntry> faces, int desiredWeight, bool wantItalic, bool filterByStyle)
        {
            string best   = null;
            float  bestDelta = float.MaxValue;
            for (int i = 0; i < faces.Count; i++) {
                var f = faces[i];
                if (filterByStyle && f.IsItalic != wantItalic) continue;
                float rep = f.RepWeight;
                if (rep > desiredWeight) {
                    float delta = rep - desiredWeight;
                    if (delta < bestDelta) { bestDelta = delta; best = f.Path; }
                }
            }
            return best;
        }

        // Nearest face whose RepWeight is strictly less than desiredWeight,
        // minimising (desiredWeight - RepWeight).  Ties: first in list wins.
        static string NearestBelow(
            IReadOnlyList<FaceEntry> faces, int desiredWeight, bool wantItalic, bool filterByStyle)
        {
            string best   = null;
            float  bestDelta = float.MaxValue;
            for (int i = 0; i < faces.Count; i++) {
                var f = faces[i];
                if (filterByStyle && f.IsItalic != wantItalic) continue;
                float rep = f.RepWeight;
                if (rep < desiredWeight) {
                    float delta = desiredWeight - rep;
                    if (delta < bestDelta) { bestDelta = delta; best = f.Path; }
                }
            }
            return best;
        }
    }
}
