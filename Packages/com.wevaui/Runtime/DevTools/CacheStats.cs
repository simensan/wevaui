using System.Globalization;
using System.Text;
using Weva.Paint.Conversion;

namespace Weva.DevTools {
    // Reads BoxToPaintConverter's cumulative hit / miss counters and tracks
    // per-frame deltas. The converter never resets these between frames on
    // its own (callers can opt in via ResetCacheStats), so we sample on every
    // RecordFrame and diff against the prior sample to compute "this frame".
    public sealed class CacheStats {
        long lastHits;
        long lastMisses;
        long thisFrameHits;
        long thisFrameMisses;
        long totalHits;
        long totalMisses;

        public long ThisFrameHits => thisFrameHits;
        public long ThisFrameMisses => thisFrameMisses;
        public long TotalHits => totalHits;
        public long TotalMisses => totalMisses;

        public double HitRatio {
            get {
                long denom = totalHits + totalMisses;
                if (denom <= 0) return 0;
                return (double)totalHits / denom;
            }
        }

        public double FrameHitRatio {
            get {
                long denom = thisFrameHits + thisFrameMisses;
                if (denom <= 0) return 0;
                return (double)thisFrameHits / denom;
            }
        }

        public void RecordFrame(BoxToPaintConverter converter) {
            if (converter == null) return;
            long h = converter.CacheHits;
            long m = converter.CacheMisses;
            thisFrameHits = h - lastHits;
            thisFrameMisses = m - lastMisses;
            // Both deltas can be negative if the caller called ResetCacheStats
            // mid-frame; clamp so we don't flash impossible values.
            if (thisFrameHits < 0) thisFrameHits = 0;
            if (thisFrameMisses < 0) thisFrameMisses = 0;
            totalHits += thisFrameHits;
            totalMisses += thisFrameMisses;
            lastHits = h;
            lastMisses = m;
        }

        public void Reset() {
            lastHits = 0;
            lastMisses = 0;
            thisFrameHits = 0;
            thisFrameMisses = 0;
            totalHits = 0;
            totalMisses = 0;
        }

        public string Format() {
            var sb = new StringBuilder(64);
            sb.Append("paint cache: ");
            sb.Append(thisFrameHits.ToString(CultureInfo.InvariantCulture));
            sb.Append(" hit / ");
            sb.Append(thisFrameMisses.ToString(CultureInfo.InvariantCulture));
            sb.Append(" miss  (");
            sb.Append((HitRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture));
            sb.Append("%)");
            return sb.ToString();
        }
    }
}
