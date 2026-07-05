using System;
using System.Collections.Generic;

namespace Weva.Css.Values {
    // Per-thread pool of CssLength / CssNumber / CssPercentage instances. The
    // CssValueParser routes new* calls through Rent* so a layout pass that
    // re-parses every property re-uses the same wrappers across calls.
    //
    // LIFETIME CONTRACT: a rented value is owned by the parser's caller for
    // the duration of the active CssValuePoolScope. After the scope's Dispose,
    // the same instance MAY be Reset() with a different (value, unit) and
    // handed to a different caller. Callers that retain a reference past the
    // scope receive UNDEFINED data on next access.
    //
    // Pooling is OPT-IN: when no CssValuePoolScope is active, Rent* allocates
    // fresh instances without recording them. Code paths that call
    // CssValueParser.Parse() outside a layout/cascade pass therefore observe
    // the pre-pool behaviour and can safely retain values long-term.
    public static class CssValuePool {
        // 4096 is a generous ceiling: a cold layout of 1000 elements parses
        // ~50 layout-affecting properties × ~3 length-leaves per declaration
        // ≈ 150k rents. The hwm-based scope returns them all at the end of
        // each pass; the 4096 cap only matters if a single pass holds more
        // than that simultaneously, which would require scoped recursion.
        // Beyond the cap, Rent* falls back to fresh allocations that are not
        // returned — bounding the pool footprint at ~4096 wrappers per type.
        const int MaxPoolSize = 4096;

        [ThreadStatic] static Stack<CssLength> lengthPool;
        [ThreadStatic] static Stack<CssNumber> numberPool;
        [ThreadStatic] static Stack<CssPercentage> percentPool;
        // Per-pass live list: tracks values handed out during the active scope
        // so Dispose can return them all at once. The scope captures the
        // "high-water mark" (count) on entry and trims back on exit; nested
        // scopes thus return only what they themselves rented.
        [ThreadStatic] static List<CssLength> liveLengths;
        [ThreadStatic] static List<CssNumber> liveNumbers;
        [ThreadStatic] static List<CssPercentage> livePercents;
        [ThreadStatic] static int activeScopeDepth;
        // Per-scope memoization: parsing the same string repeatedly during a
        // layout pass (every "0", "auto", "16px" appears thousands of times)
        // returns the cached CssValue. The cache is cleared on scope exit.
        // CssValueParser.Parse checks here before tokenizing.
        [ThreadStatic] static Dictionary<string, CssValue> parseCache;

        internal static bool ScopeActive => activeScopeDepth > 0;

        // Parse-cache hooks. CssValueParser.Parse checks TryGetCachedParse
        // before tokenizing; on a miss it parses normally and registers the
        // result via CachePutParsed. Cache is alive only while a scope is open
        // and is cleared on scope dispose.
        internal static bool TryGetCachedParse(string text, out CssValue value) {
            if (activeScopeDepth == 0 || parseCache == null) {
                value = null;
                return false;
            }
            return parseCache.TryGetValue(text, out value);
        }

        internal static void CachePutParsed(string text, CssValue value) {
            if (activeScopeDepth == 0) return;
            (parseCache ??= new Dictionary<string, CssValue>(64))[text] = value;
        }

        public static CssLength RentLength(double value, CssLengthUnit unit, string raw) {
            // Try interned constants first (no allocation, no pool churn).
            var interned = CssLength.TryIntern(value, unit);
            if (interned != null && (raw == null || raw == interned.Raw || RawMatchesInterned(interned, value, unit, raw))) {
                return interned;
            }
            if (activeScopeDepth == 0) {
                return new CssLength(value, unit, raw);
            }
            var pool = lengthPool;
            CssLength inst;
            if (pool != null && pool.Count > 0) {
                inst = pool.Pop();
                inst.Reset(value, unit, raw);
            } else {
                inst = new CssLength(value, unit, raw);
            }
            (liveLengths ??= new List<CssLength>(256)).Add(inst);
            return inst;
        }

        public static CssLength RentLength(double value, CssLengthUnit unit) {
            // Match the no-raw constructor's behaviour: build the canonical raw
            // form so callers reading `.Raw` see the same string they would
            // from `new CssLength(value, unit)`.
            string raw = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + LengthUnitSuffix(unit);
            return RentLength(value, unit, raw);
        }

        static string LengthUnitSuffix(CssLengthUnit u) {
            switch (u) {
                case CssLengthUnit.Px: return "px";
                case CssLengthUnit.Em: return "em";
                case CssLengthUnit.Rem: return "rem";
                case CssLengthUnit.Percent: return "%";
                case CssLengthUnit.Vh: return "vh";
                case CssLengthUnit.Vw: return "vw";
                case CssLengthUnit.Vmin: return "vmin";
                case CssLengthUnit.Vmax: return "vmax";
                case CssLengthUnit.Pt: return "pt";
                case CssLengthUnit.Pc: return "pc";
                case CssLengthUnit.In: return "in";
                case CssLengthUnit.Cm: return "cm";
                case CssLengthUnit.Mm: return "mm";
                case CssLengthUnit.Ch: return "ch";
                case CssLengthUnit.Ex: return "ex";
            }
            return "";
        }

        public static CssNumber RentNumber(double value, string raw) {
            var interned = CssNumber.TryIntern(value);
            if (interned != null && (raw == null || raw == interned.Raw)) {
                return interned;
            }
            if (activeScopeDepth == 0) {
                return new CssNumber(value, raw);
            }
            var pool = numberPool;
            CssNumber inst;
            if (pool != null && pool.Count > 0) {
                inst = pool.Pop();
                inst.Reset(value, raw);
            } else {
                inst = new CssNumber(value, raw);
            }
            (liveNumbers ??= new List<CssNumber>(256)).Add(inst);
            return inst;
        }

        public static CssNumber RentNumber(double value) {
            string raw = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return RentNumber(value, raw);
        }

        public static CssPercentage RentPercentage(double value, string raw) {
            var interned = CssPercentage.TryIntern(value);
            if (interned != null && (raw == null || raw == interned.Raw)) {
                return interned;
            }
            if (activeScopeDepth == 0) {
                return new CssPercentage(value, raw);
            }
            var pool = percentPool;
            CssPercentage inst;
            if (pool != null && pool.Count > 0) {
                inst = pool.Pop();
                inst.Reset(value, raw);
            } else {
                inst = new CssPercentage(value, raw);
            }
            (livePercents ??= new List<CssPercentage>(256)).Add(inst);
            return inst;
        }

        public static CssPercentage RentPercentage(double value) {
            string raw = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "%";
            return RentPercentage(value, raw);
        }

        public static void ReturnLength(CssLength value) {
            if (value == null) return;
            if (IsInternedLength(value)) return;
            var pool = lengthPool ??= new Stack<CssLength>(64);
            if (pool.Count < MaxPoolSize) pool.Push(value);
        }

        public static void ReturnNumber(CssNumber value) {
            if (value == null) return;
            if (IsInternedNumber(value)) return;
            var pool = numberPool ??= new Stack<CssNumber>(64);
            if (pool.Count < MaxPoolSize) pool.Push(value);
        }

        public static void ReturnPercentage(CssPercentage value) {
            if (value == null) return;
            if (IsInternedPercentage(value)) return;
            var pool = percentPool ??= new Stack<CssPercentage>(64);
            if (pool.Count < MaxPoolSize) pool.Push(value);
        }

        // Captured by CssValuePoolScope.Dispose to return everything rented
        // during that scope back to the free stacks.
        internal static PoolHwm BeginScope() {
            activeScopeDepth++;
            return new PoolHwm(
                liveLengths != null ? liveLengths.Count : 0,
                liveNumbers != null ? liveNumbers.Count : 0,
                livePercents != null ? livePercents.Count : 0
            );
        }

        internal static void EndScope(PoolHwm hwm) {
            // Return everything rented since the matching BeginScope. The
            // 4096-cap fallback path didn't track its instance in the live
            // list (it was allocated as overflow), so those simply get GC'd
            // when the caller releases their reference — exactly the bound we
            // want.
            if (liveLengths != null) {
                var pool = lengthPool ??= new Stack<CssLength>(64);
                for (int i = liveLengths.Count - 1; i >= hwm.Lengths; i--) {
                    var inst = liveLengths[i];
                    if (!IsInternedLength(inst) && pool.Count < MaxPoolSize) {
                        pool.Push(inst);
                    }
                }
                liveLengths.RemoveRange(hwm.Lengths, liveLengths.Count - hwm.Lengths);
            }
            if (liveNumbers != null) {
                var pool = numberPool ??= new Stack<CssNumber>(64);
                for (int i = liveNumbers.Count - 1; i >= hwm.Numbers; i--) {
                    var inst = liveNumbers[i];
                    if (!IsInternedNumber(inst) && pool.Count < MaxPoolSize) {
                        pool.Push(inst);
                    }
                }
                liveNumbers.RemoveRange(hwm.Numbers, liveNumbers.Count - hwm.Numbers);
            }
            if (livePercents != null) {
                var pool = percentPool ??= new Stack<CssPercentage>(64);
                for (int i = livePercents.Count - 1; i >= hwm.Percents; i--) {
                    var inst = livePercents[i];
                    if (!IsInternedPercentage(inst) && pool.Count < MaxPoolSize) {
                        pool.Push(inst);
                    }
                }
                livePercents.RemoveRange(hwm.Percents, livePercents.Count - hwm.Percents);
            }
            // Clear the parse-cache only at the OUTERMOST scope. A nested
            // scope's rentals were returned above; their cached parse results
            // would now point at recycled instances, so the cache must be
            // invalidated whenever we drop back to depth 0.
            if (activeScopeDepth == 1 && parseCache != null) {
                parseCache.Clear();
            }
            activeScopeDepth--;
        }

        public static CssValuePoolScope PassScope() {
            return new CssValuePoolScope(BeginScope());
        }

        // Diagnostics for tests + bench reports.
        internal static int LengthPoolCount => lengthPool != null ? lengthPool.Count : 0;
        internal static int NumberPoolCount => numberPool != null ? numberPool.Count : 0;
        internal static int PercentPoolCount => percentPool != null ? percentPool.Count : 0;

        public static void ClearAll() {
            lengthPool?.Clear();
            numberPool?.Clear();
            percentPool?.Clear();
            liveLengths?.Clear();
            liveNumbers?.Clear();
            livePercents?.Clear();
            parseCache?.Clear();
            activeScopeDepth = 0;
        }

        static bool IsInternedLength(CssLength v) {
            if (ReferenceEquals(v, CssLength.Zero) || ReferenceEquals(v, CssLength.Empty)) return true;
            // Integer-px cache identity check via TryIntern.
            var maybe = CssLength.TryIntern(v.Value, v.Unit);
            return ReferenceEquals(maybe, v);
        }

        static bool IsInternedNumber(CssNumber v) {
            return ReferenceEquals(v, CssNumber.Zero) || ReferenceEquals(v, CssNumber.One);
        }

        static bool IsInternedPercentage(CssPercentage v) {
            return ReferenceEquals(v, CssPercentage.Zero)
                || ReferenceEquals(v, CssPercentage.Hundred)
                || ReferenceEquals(v, CssPercentage.Fifty);
        }

        // The interned 0..256 px cache stores Raw as a default-formatted
        // string. When the parser passes a raw token like "16px" we want to
        // preserve the spelling; an exact match against the cached Raw still
        // returns the cached instance, but a different spelling (e.g. "16.0px")
        // forces a fresh wrapper so Raw round-trips correctly.
        static bool RawMatchesInterned(CssLength interned, double value, CssLengthUnit unit, string raw) {
            return raw == interned.Raw;
        }

        internal readonly struct PoolHwm {
            public readonly int Lengths;
            public readonly int Numbers;
            public readonly int Percents;
            public PoolHwm(int l, int n, int p) { Lengths = l; Numbers = n; Percents = p; }
        }
    }
}
