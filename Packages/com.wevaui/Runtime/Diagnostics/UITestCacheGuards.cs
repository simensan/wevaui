using Weva.Binding;
using Weva.Css.Values;
#if !NET8_0_OR_GREATER
using Weva.Rendering.URP;
using Weva.Text.Sdf;
// TmpFontAssetRegistry only exists when the TMP glyph-data backend compiles
// (audit AR1 class): gate the using on the same symbols as its definition,
// or the package fails to compile in a project without TextMesh Pro.
#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using Weva.Text.Tmp;
#endif
#endif

namespace Weva.Diagnostics {
    // One call that resets every process-static cache the engine holds.
    // Test harnesses should invoke this in `[SetUp]` (or a shared base
    // class's `[SetUp]`) to keep state from leaking across cases —
    // without it, the TextRunSnapshotCache + CssValue parseCache +
    // BindingResolver reflection cache + font registries all accumulate
    // entries across the whole test run, and a test whose expectations
    // depend on a fresh cache becomes order-dependent and intermittently
    // flaky.
    //
    // Production code should NOT call this — the caches are intentional
    // optimizations on the hot path.
    public static class UITestCacheGuards {
        public static void ResetAll() {
            // Glyph quad replay snapshots. The cache is visually keyed
            // (text + font + color + decoration + blur + letter-spacing),
            // so on its own it doesn't leak across tests with different
            // inputs — but a test that swaps the SdfTextRendering.Atlas
            // implementation mid-suite needs the cache cleared so old
            // atlas entries don't replay.
#if NET8_0_OR_GREATER
            ClearOptionalCache("Weva.Rendering.URP.TextRunSnapshotCache", "Clear");
#else
            TextRunSnapshotCache.Clear();
#endif
            // Parsed-value cache for raw CSS strings; keyed by raw text
            // so a test that registers a new property name then re-parses
            // an old value would otherwise see the stale parse.
            CssValue.ClearCachesForTests();
            // Reflection-discovered MemberInfo cache for x:Bind paths.
            BindingResolver.ClearCacheForTests();
            // Family -> [font asset chain] registry.
#if NET8_0_OR_GREATER
            ClearOptionalCache("Weva.Text.Tmp.TmpFontAssetRegistry", "Clear");
#elif UNITY_2023_1_OR_NEWER && WEVA_TMP
            TmpFontAssetRegistry.Clear();
#endif
            // SDF atlas id assignments; texture references survive Unity
            // domain reloads but the int ids are session-scoped.
#if NET8_0_OR_GREATER
            ClearOptionalCache("Weva.Text.Sdf.AtlasRegistry", "Clear");
#else
            AtlasRegistry.Clear();
#endif
        }

#if NET8_0_OR_GREATER
        static void ClearOptionalCache(string typeName, string methodName) {
            var type = typeof(UITestCacheGuards).Assembly.GetType(typeName);
            if (type == null) return;

            var method = type.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            method?.Invoke(null, null);
        }
#endif
    }
}
