namespace Weva.Testing.Goldens {
    public sealed class GoldenComparison {
        public bool Passed { get; }
        public int DifferingPixels { get; }
        public double MaxPixelError { get; }
        public double RmsError { get; }
        public byte[] DiffImage { get; }
        public int Width { get; }
        public int Height { get; }
        public string FailureReason { get; }

        public GoldenComparison(bool passed, int differingPixels, double maxPixelError,
                                double rmsError, byte[] diffImage, int width, int height,
                                string failureReason) {
            Passed = passed;
            DifferingPixels = differingPixels;
            MaxPixelError = maxPixelError;
            RmsError = rmsError;
            DiffImage = diffImage;
            Width = width;
            Height = height;
            FailureReason = failureReason;
        }
    }
}
