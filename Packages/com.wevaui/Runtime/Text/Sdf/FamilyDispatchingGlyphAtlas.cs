#if UNITY_2023_1_OR_NEWER
using System;
using System.Collections.Generic;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.Tmp;

namespace Weva.Text.Sdf {
    // Per-element font-family router for the glyph-shaping path.
    //
    // The TMP/ATG fast path is built around a single global face. This wrapper
    // restores per-element `font-family`: for each DrawTextCommand it resolves
    // the run's family to a registered TMP face and delegates to a per-family
    // adapter (built lazily, cached) whose own atlas carries a distinct atlasId
    // so the batcher binds the right texture. Generic keywords, the default
    // face, and unregistered families fall through to the default adapter (the
    // ATG-wrapped stack) — so existing single-font docs are unchanged.
    //
    // Layout metrics are kept in sync by UIDocumentDefaults.FamilyMetricsResolver
    // (see SdfBootstrap.ResolveFamilyMetrics): a run measured with face X is
    // also painted with face X.
    public sealed class FamilyDispatchingGlyphAtlas
        : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy, IGlyphAtlasShapeSource {

        readonly IGlyphAtlasWithId defaultAdapter;
        readonly TmpFontAssetSource defaultSource;
        readonly Func<TmpFontAssetSource, IReadOnlyList<TmpFontAssetSource>, IGlyphAtlasWithId> builder;

        // family (quote-stripped, case-insensitive) -> adapter, or null meaning
        // "use the default adapter for this family". Negative entries are cached
        // too so the registry walk runs once per distinct family.
        readonly Dictionary<string, IGlyphAtlasWithId> byFamily =
            new(StringComparer.OrdinalIgnoreCase);
        readonly List<IGlyphAtlasWithId> built = new();

        public FamilyDispatchingGlyphAtlas(
            IGlyphAtlasWithId defaultAdapter,
            TmpFontAssetSource defaultSource,
            Func<TmpFontAssetSource, IReadOnlyList<TmpFontAssetSource>, IGlyphAtlasWithId> builder) {
            this.defaultAdapter = defaultAdapter;
            this.defaultSource = defaultSource;
            this.builder = builder;
        }

        IGlyphAtlasWithId Resolve(DrawTextCommand command) {
            string raw = command?.Font.Family;
            if (string.IsNullOrEmpty(raw) || builder == null) return defaultAdapter;

            int i = 0, n = raw.Length;
            while (i < n) {
                while (i < n && (raw[i] == ',' || char.IsWhiteSpace(raw[i]))) i++;
                if (i >= n) break;
                int start = i;
                while (i < n && raw[i] != ',') i++;
                string fam = StripQuotes(raw.Substring(start, i - start).Trim());
                if (fam.Length == 0) continue;

                if (byFamily.TryGetValue(fam, out var cached)) {
                    if (cached != null) return cached;   // a custom face for this token
                    return defaultAdapter;               // resolved-to-default token reached → stop
                }

                if (TmpFontAssetRegistry.TryGet(fam, out var src) && src != null && src.Asset != null) {
                    if (ReferenceEquals(src, defaultSource) || (defaultSource != null && src.Asset == defaultSource.Asset)) {
                        byFamily[fam] = null;            // this family IS the default face
                        return defaultAdapter;
                    }
                    var adapter = builder(src, TmpFontAssetRegistry.GetChain(fam));
                    byFamily[fam] = adapter;
                    if (adapter != null) { built.Add(adapter); return adapter; }
                    return defaultAdapter;
                }

                if (IsGenericFamily(fam)) {
                    byFamily[fam] = null;                // sans-serif etc. → default face
                    return defaultAdapter;
                }
                // Unregistered named family: not available — try the next entry.
                byFamily[fam] = null;
            }
            return defaultAdapter;
        }

        // The adapter that served the most recent TryShape — lets the
        // IGlyphAtlasShapeSource passthrough below report whether that shape
        // fell to the secondary SDF face (renderer skips snapshot pinning).
        IGlyphAtlasWithId lastShapeAdapter;

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
            var a = Resolve(command);
            lastShapeAdapter = a;
            return a.TryShape(command, output);
        }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
            var a = Resolve(command);
            lastShapeAdapter = a;
            return a.TryShape(command, output, out atlasId);
        }

        public bool LastShapeUsedSecondaryFallback =>
            lastShapeAdapter is IGlyphAtlasShapeSource src && src.LastShapeUsedSecondaryFallback;

        public void BeginPrepareText() {
            if (defaultAdapter is IGlyphAtlasPreparer d) d.BeginPrepareText();
            for (int i = 0; i < built.Count; i++)
                if (built[i] is IGlyphAtlasPreparer p) p.BeginPrepareText();
        }

        public void PrepareText(DrawTextCommand command) {
            var a = Resolve(command);
            if (a is IGlyphAtlasPreparer p) p.PrepareText(command);
        }

        public void EndPrepareText() {
            if (defaultAdapter is IGlyphAtlasPreparer d) d.EndPrepareText();
            for (int i = 0; i < built.Count; i++)
                if (built[i] is IGlyphAtlasPreparer p) p.EndPrepareText();
        }

        public long Version {
            get {
                long v = defaultAdapter is IGlyphAtlasVersioned dv ? dv.Version : 0;
                for (int i = 0; i < built.Count; i++)
                    if (built[i] is IGlyphAtlasVersioned bv) v = (v * 397) ^ bv.Version;
                return v;
            }
        }

        public bool UseTextRunSnapshots => true;

        static string StripQuotes(string s) {
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                return s.Substring(1, s.Length - 2);
            return s;
        }

        static bool IsGenericFamily(string fam) {
            switch (fam.ToLowerInvariant()) {
                case "sans-serif": case "serif": case "monospace":
                case "cursive": case "fantasy": case "system-ui":
                case "ui-sans-serif": case "ui-serif": case "ui-monospace":
                case "ui-rounded": case "math": case "emoji": case "fangsong":
                case "-apple-system": case "blinkmacsystemfont":
                    return true;
                default: return false;
            }
        }
    }
}
#endif
