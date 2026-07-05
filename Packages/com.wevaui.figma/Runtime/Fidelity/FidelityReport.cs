namespace Weva.Figma.Fidelity
{
    public enum FidelityVerdict { Pass, Warn, Fail }

    public sealed class FidelityThresholds
    {
        /// <summary>A pixel counts as "differing" if any channel delta exceeds this (0..255).</summary>
        public int ChannelThreshold = 6;
        /// <summary>Differing fraction above this → Warn.</summary>
        public double WarnFraction = 0.01;
        /// <summary>Differing fraction above this → Fail.</summary>
        public double FailFraction = 0.05;
    }

    /// <summary>The outcome of comparing an engine-rendered frame to its Figma reference.</summary>
    public sealed class FidelityReport
    {
        public bool SizeMismatch;
        public int Width, Height;
        public long TotalPixels;
        public long DifferingPixels;
        public double DiffFraction;     // 0..1
        public int MaxChannelDelta;     // 0..255
        public double MeanChannelDelta; // 0..255, averaged over all channels
        public byte[] Heatmap;          // RGBA w*h*4, or null

        public FidelityVerdict Classify(FidelityThresholds t = null)
        {
            t = t ?? new FidelityThresholds();
            if (SizeMismatch) return FidelityVerdict.Fail;
            if (DiffFraction > t.FailFraction) return FidelityVerdict.Fail;
            if (DiffFraction > t.WarnFraction) return FidelityVerdict.Warn;
            return FidelityVerdict.Pass;
        }

        public override string ToString()
        {
            if (SizeMismatch) return "fidelity: SIZE MISMATCH";
            return $"fidelity: {DiffFraction:P2} differing ({DifferingPixels}/{TotalPixels}px), "
                   + $"max Δ {MaxChannelDelta}, mean Δ {MeanChannelDelta:F2}";
        }
    }
}
