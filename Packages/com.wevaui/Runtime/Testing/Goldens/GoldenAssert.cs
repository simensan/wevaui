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
                // First-time baseline write so the very first test run after authoring a
                // snippet produces the committable artifact. Subsequent runs verify against
                // it; setting WEVA_REGENERATE_GOLDENS=1 forces overwrite.
                Directory.CreateDirectory(Path.GetDirectoryName(baselinePath));
                File.WriteAllBytes(baselinePath, actualPng);
                return;
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
}
