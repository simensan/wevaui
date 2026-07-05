using Weva.Layout.Boxes;

namespace Weva.Layout.Incremental {
    // PA10: changed from `sealed class` to `readonly struct`. The Dictionary
    // <Element, LayoutCacheEntry> in LayoutEngine writes a new entry on
    // every box stamp — ~5 cacheable boxes per warm flip × ~40B per
    // class instance = ~200B of GC garbage per click. Struct semantics
    // make cache[element] = entry an in-place value copy, no allocation.
    // All fields are init-only so there's no aliasing risk.
    public readonly struct LayoutCacheEntry {
        public LayoutCacheKey Key { get; }
        public Box BoxResult { get; }
        public long BoxVersion { get; }

        public LayoutCacheEntry(LayoutCacheKey key, Box boxResult, long boxVersion) {
            Key = key;
            BoxResult = boxResult;
            BoxVersion = boxVersion;
        }
    }
}
