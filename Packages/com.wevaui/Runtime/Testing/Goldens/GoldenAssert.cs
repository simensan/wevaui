using System;
using System.IO;

namespace Weva.Testing.Goldens {
    public static class GoldenAssert {
        const string RegenerateEnvVar = "WEVA_REGENERATE_GOLDENS";

        public static void Match(string snippetPath, string baselinePath,
                                 int width = 800, int height = 600, double tolerance = 0.005) {
            if (snippetPath == null) throw new ArgumentNullException(nameof(snippetPath));
            if (baselinePath == null) throw new ArgumentNullException(nameof(baselinePath));
            if (!File.Exists(snippetPath)) {
                throw new FileNotFoundException("Golden snippet HTML missing: " + snippetPath, snippetPath);
            }

            string html = File.ReadAllText(snippetPath);
            string css = "";
            string cssPath = Path.ChangeExtension(snippetPath, ".css");
            if (File.Exists(cssPath)) {
                css = File.ReadAllText(cssPath);
            }

            byte[] actualPng = GoldenRunner.RenderToPng(html, css, width, height);

            if (string.Equals(Environment.GetEnvironmentVariable(RegenerateEnvVar), "1", StringComparison.Ordinal)) {
                Directory.CreateDirectory(Path.GetDirectoryName(baselinePath));
                File.WriteAllBytes(baselinePath, actualPng);
                return;
            }

            if (!File.Exists(baselinePath)) {
                // Seed the committable artifact, but do NOT report success: a
                // comparison against nothing has verified nothing, and a test
                // that goes green on its first run gives a newly authored
                // snippet a free pass forever after.
                Directory.CreateDirectory(Path.GetDirectoryName(baselinePath));
                File.WriteAllBytes(baselinePath, actualPng);
                throw new GoldenNotVerifiedException(
                    $"Golden baseline seeded, not verified, for '{Path.GetFileName(snippetPath)}'.\n" +
                    $"  baseline: {baselinePath}\n" +
                    $"  Inspect that PNG and commit it. The next run compares against it.");
            }

            byte[] expectedPng = File.ReadAllBytes(baselinePath);
            var result = GoldenRunner.Compare(actualPng, expectedPng, tolerance);
            if (!result.Passed) {
                string outDir = Path.Combine(Path.GetDirectoryName(baselinePath) ?? ".", "..", "Out");
                outDir = Path.GetFullPath(outDir);
                Directory.CreateDirectory(outDir);
                string name = Path.GetFileNameWithoutExtension(baselinePath);
                string actualPath = Path.Combine(outDir, name + ".actual.png");
                string diffPath = Path.Combine(outDir, name + ".diff.png");
                File.WriteAllBytes(actualPath, actualPng);
                if (result.DiffImage != null) {
                    byte[] diffPng = PngWriter.Encode(result.DiffImage, result.Width, result.Height);
                    File.WriteAllBytes(diffPath, diffPng);
                }
                throw new GoldenMismatchException(
                    $"Golden mismatch for '{Path.GetFileName(snippetPath)}': {result.FailureReason}\n" +
                    $"  baseline: {baselinePath}\n" +
                    $"  actual:   {actualPath}\n" +
                    $"  diff:     {diffPath}\n" +
                    $"  set {RegenerateEnvVar}=1 to overwrite the baseline");
            }
        }
    }

    public sealed class GoldenMismatchException : Exception {
        public GoldenMismatchException(string message) : base(message) { }
    }

    /// <summary>
    /// No baseline existed, so the render was compared against nothing. This
    /// is not a mismatch and not a pass — the test reached no verdict. Callers
    /// in a test assembly should turn it into an inconclusive result.
    /// </summary>
    public sealed class GoldenNotVerifiedException : Exception {
        public GoldenNotVerifiedException(string message) : base(message) { }
    }
}
