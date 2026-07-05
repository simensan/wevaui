namespace Weva.Figma.Fidelity
{
    /// <summary>
    /// A self-contained RGBA image diff. Both buffers are tightly-packed RGBA
    /// (length = width·height·4) and must share an orientation (top-down). Kept
    /// in the portable core so the package owns its fidelity check and stays
    /// engine-independent for everything except the actual render.
    /// </summary>
    public static class ImageDiff
    {
        public static FidelityReport Compare(
            byte[] a, int widthA, int heightA,
            byte[] b, int widthB, int heightB,
            FidelityThresholds thresholds = null, bool buildHeatmap = false)
        {
            thresholds = thresholds ?? new FidelityThresholds();
            var report = new FidelityReport { Width = widthA, Height = heightA };

            bool mismatched = a == null || b == null
                || widthA != widthB || heightA != heightB
                || a.Length != widthA * heightA * 4
                || b.Length != widthB * heightB * 4;
            if (mismatched)
            {
                report.SizeMismatch = true;
                report.TotalPixels = (long)widthA * heightA;
                report.DifferingPixels = report.TotalPixels;
                report.DiffFraction = 1.0;
                report.MaxChannelDelta = 255;
                report.MeanChannelDelta = 255;
                return report;
            }

            long total = (long)widthA * heightA;
            long differing = 0;
            long sumDelta = 0;
            int maxDelta = 0;
            byte[] hm = buildHeatmap ? new byte[a.Length] : null;

            for (int i = 0; i < a.Length; i += 4)
            {
                int dr = Abs(a[i] - b[i]);
                int dg = Abs(a[i + 1] - b[i + 1]);
                int db = Abs(a[i + 2] - b[i + 2]);
                int da = Abs(a[i + 3] - b[i + 3]);
                int pxMax = Max(dr, dg, db, da);
                sumDelta += dr + dg + db + da;
                if (pxMax > maxDelta) maxDelta = pxMax;

                bool diff = pxMax > thresholds.ChannelThreshold;
                if (diff) differing++;

                if (hm != null)
                {
                    if (diff) { hm[i] = 255; hm[i + 1] = 0; hm[i + 2] = 0; hm[i + 3] = 255; }
                    else { hm[i] = (byte)(b[i] / 4); hm[i + 1] = (byte)(b[i + 1] / 4); hm[i + 2] = (byte)(b[i + 2] / 4); hm[i + 3] = 255; }
                }
            }

            report.TotalPixels = total;
            report.DifferingPixels = differing;
            report.DiffFraction = total > 0 ? (double)differing / total : 0.0;
            report.MaxChannelDelta = maxDelta;
            report.MeanChannelDelta = total > 0 ? (double)sumDelta / (total * 4) : 0.0;
            report.Heatmap = hm;
            return report;
        }

        static int Abs(int v) => v < 0 ? -v : v;

        static int Max(int a, int b, int c, int d)
        {
            int m = a;
            if (b > m) m = b;
            if (c > m) m = c;
            if (d > m) m = d;
            return m;
        }
    }
}
