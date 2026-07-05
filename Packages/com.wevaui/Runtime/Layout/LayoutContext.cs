using System.Collections.Generic;
using Weva.Compiled;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.AnchorPositioning;
using Weva.Layout.Text;

namespace Weva.Layout {
    public sealed class LayoutContext {
        // Document-scoped anchor registry. PositioningPass populates this on
        // every layout pass, then PositioningPass and AnchorResolver consult
        // it when resolving anchor() function values. CSS Anchor Positioning
        // (2024) — see AnchorRegistry.cs for v1 simplifications.
        public AnchorRegistry Anchors { get; } = new AnchorRegistry();

        // Containment boundary for FlexLayout's block-flow height-delta
        // propagation (BlockFlowAdjuster). Set by the incremental subtree
        // splice (and the scroll-graft correction path) to the subtree root
        // being re-laid: the splice contract guarantees the root's OUTER
        // geometry is unchanged, so a (pre-flex stacked → flexed) height
        // delta computed INSIDE the fresh subtree must never walk out into
        // the stale ancestor chain. Without the boundary, a spliced row-flex
        // whose block-stacked height differs from its flexed height
        // subtracted that delta from already-correct ancestors on EVERY
        // warm-flip frame — content below crept upward cumulatively until
        // the next full layout snapped it back. Null on the full tower
        // (ancestors are being laid fresh; propagation keeps them consistent
        // there).
        internal Weva.Layout.Boxes.Box HeightPropagationBoundary;

        public double ViewportWidthPx { get; set; } = 1920;
        public double ViewportHeightPx { get; set; } = 1080;
        public double RootFontSizePx { get; set; } = 16;
        // Resolved line-height of the document root (`<html>` post-cascade).
        // Populated by LayoutEngine before any layout work runs, alongside
        // RootFontSizePx, so descendants' `rlh` lengths resolve against the
        // author-overridden root per CSS Values L4 §6.2. Zero means "unset";
        // CssLength.ToPixels falls back to RootFontSizePx * 1.2 in that case.
        public double RootLineHeightPx { get; set; } = 0;
        public double DpiPixelsPerInch { get; set; } = 96;

        public IFontMetrics DefaultFontMetrics { get; set; }

        // Optional snapshot the cascade already built this frame. When set,
        // LayoutEngine reuses it instead of walking Document.Children: BoxBuilder
        // walks NodeId arrays directly which is dramatically more cache-friendly
        // than dereferencing the managed DOM tree. UIDocumentLifecycle wires
        // CascadeEngine.LastSnapshot into here right after ComputeAll runs.
        // When null, layout falls back to the managed Element-tree walk.
        internal DomSnapshot Snapshot { get; set; }

        // Optional NodeId-indexed styles paired with Snapshot. UIDocumentLifecycle
        // wires CascadeEngine.Styles here after ComputeAll so SnapshotBoxBuilder
        // can read computed styles by NodeId without calling back through
        // Cascade.GetComposedStyle for every element during a resize/layout pass.
        public StyleArray SnapshotStyles { get; set; }

        readonly Dictionary<string, IFontMetrics> byFamily = new();

        // P19 — Per-call hot path on `font-family: "Inter", system-ui, sans-serif`
        // previously substring+Trim'd one head per comma on EVERY GetMetrics call
        // (and ~15 Layout sites call this per layout pass). Cache the resolved
        // metrics keyed on the RAW (pre-normalised) family string so repeat
        // queries skip the parse + the dictionary walk entirely.
        //
        // Cap matches the ValueInterpolator.transformFnCache / IdentityListCache
        // convention (drop-one-on-overflow). 256 covers the realistic working set
        // — typical apps have at most a few dozen distinct font-family declarations
        // across all rules; the cap protects against pathological generated CSS.
        readonly Dictionary<string, IFontMetrics> familyResolveCache = new();
        const int FamilyResolveCacheCap = 256;

        // Per-family metrics resolver (see UIDocumentDefaults.FamilyMetricsResolver).
        // Consulted in the font-family stack walk so per-element font-family is
        // honoured at layout time. Null => only byFamily + DefaultFontMetrics.
        public System.Func<string, IFontMetrics> FamilyMetricsResolver;

        public LayoutContext() { }

        public LayoutContext(IFontMetrics defaultMetrics) {
            DefaultFontMetrics = defaultMetrics;
        }

        public void RegisterFont(string fontFamily, IFontMetrics metrics) {
            if (string.IsNullOrEmpty(fontFamily) || metrics == null) return;
            byFamily[NormalizeFamily(fontFamily)] = metrics;
            // A new registration can change what an existing raw family string
            // resolves to (e.g. previously fell through to DefaultFontMetrics,
            // now hits the registered head). Invalidate the resolve cache so
            // stale answers don't outlive the registration.
            familyResolveCache.Clear();
        }

        public IFontMetrics GetMetrics(string fontFamily) {
            if (string.IsNullOrEmpty(fontFamily)) return DefaultFontMetrics;
            if (familyResolveCache.TryGetValue(fontFamily, out var cached)) return cached;

            var resolved = ResolveFamilyUncached(fontFamily);

            // Drop-one-on-overflow eviction (same policy as ValueInterpolator's
            // transformFnCache — see CODE_AUDIT_FINDINGS MS3 / P19). The cap is
            // sized for the realistic working set so eviction is the cold path;
            // when it does fire, an arbitrary entry yields room for the newcomer.
            if (familyResolveCache.Count >= FamilyResolveCacheCap) {
                string victim = null;
                foreach (var k in familyResolveCache.Keys) { victim = k; break; }
                if (victim != null) familyResolveCache.Remove(victim);
            }
            familyResolveCache[fontFamily] = resolved;
            return resolved;
        }

        IFontMetrics ResolveFamilyUncached(string fontFamily) {
            var key = NormalizeFamily(fontFamily);
            if (byFamily.TryGetValue(key, out var m)) return m;
            // Walk the comma-separated stack. For each candidate try an explicit
            // RegisterFont mapping first, then the per-family resolver (TMP/SDF
            // backend). First hit wins — matching CSS font-family fallback order.
            string rest = key;
            while (true) {
                int comma = rest.IndexOf(',');
                string head = StripFamilyQuotes((comma >= 0 ? rest.Substring(0, comma) : rest).Trim());
                if (head.Length > 0) {
                    if (byFamily.TryGetValue(head, out var hm)) return hm;
                    if (FamilyMetricsResolver != null) {
                        var rm = FamilyMetricsResolver(head);
                        if (rm != null) return rm;
                    }
                }
                if (comma < 0) break;
                rest = rest.Substring(comma + 1);
            }
            return DefaultFontMetrics;
        }

        static string StripFamilyQuotes(string s) {
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) {
                return s.Substring(1, s.Length - 2);
            }
            return s;
        }

        // Test-only — caches are instance-scoped here, but mirroring the
        // ValueInterpolator pattern keeps the NUnit fixture pattern uniform
        // across the cache regressions in CODE_AUDIT_FINDINGS.
        internal void ResetCaches_TestOnly() {
            familyResolveCache.Clear();
        }

        internal int FamilyResolveCacheCount_TestOnly => familyResolveCache.Count;
        internal int FamilyResolveCacheCap_TestOnly => FamilyResolveCacheCap;

        public LengthContext ToLengthContext(double baseFontSizePx, double? basisPx = null, double lineHeightPx = 0) {
            return new LengthContext {
                BaseFontSizePx = baseFontSizePx,
                RootFontSizePx = RootFontSizePx,
                ViewportWidthPx = ViewportWidthPx,
                ViewportHeightPx = ViewportHeightPx,
                DpiPixelsPerInch = DpiPixelsPerInch,
                BasisPixels = basisPx,
                LineHeightPx = lineHeightPx,
                RootLineHeightPx = RootLineHeightPx
            };
        }

        static string NormalizeFamily(string s) {
            s = CssStringUtil.ToLowerInvariantOrSame(s.Trim());
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) {
                s = s.Substring(1, s.Length - 2);
            }
            return s;
        }
    }
}
