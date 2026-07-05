#if WEVA_URP
using System;
using System.IO;

namespace Weva.Testing.Goldens {
    // GPU-side golden assert. Same contract as GoldenAssert but renders through
    // GpuGoldenRunner (the real BatchedURPRenderBackend + Hidden/Weva/Quad shader)
    // and writes baselines to a separate Baselines.GPU/ directory so software and
    // GPU baselines do not collide.
    //
    // Workflow:
    //   1. First run: no baseline exists → the actual render is written as the baseline.
    //      Inspect the seeded PNG visually before committing it.
    //   2. Subsequent runs: actual render is compared against the saved baseline.
    //      If WEVA_REGENERATE_GOLDENS=1 the baseline is overwritten unconditionally.
    //   3. On failure: <name>.actual.png and <name>.diff.png are written to Out.GPU/
    //      next to the Baselines.GPU/ directory.
    //
    // Pixel diff math is delegated to GoldenRunner.Compare (the same PNG decoder +
    // per-pixel RMS used by the software goldens) so tolerance semantics are identical.
    public static class GpuGoldenAssert {
        const string RegenerateEnvVar = "WEVA_REGENERATE_GOLDENS";

        // Match the GPU render of snippetPath against the PNG at baselinePath.
        //
        // snippetPath  — absolute path to the .html file (sibling .css is auto-found).
        // baselinePath — absolute path inside Baselines.GPU/ (auto-created).
        // w, h         — render dimensions; should match the snippet's intended viewport.
        // tolerance    — per-pixel RMS tolerance (0..1). 0.02 is standard for game-UI goldens.
        public static void Match(string snippetPath, string baselinePath,
                                 int width = 800, int height = 600, double tolerance = 0.02) {
            if (snippetPath  == null) throw new ArgumentNullException(nameof(snippetPath));
            if (baselinePath == null) throw new ArgumentNullException(nameof(baselinePath));
            if (!File.Exists(snippetPath)) {
                throw new FileNotFoundException(
                    "GPU golden snippet HTML missing: " + snippetPath, snippetPath);
            }

            string html = File.ReadAllText(snippetPath);
            string css  = string.Empty;
            string cssPath = Path.ChangeExtension(snippetPath, ".css");
            if (File.Exists(cssPath)) css = File.ReadAllText(cssPath);

            byte[] actualPng = GpuGoldenRunner.RenderToPng(html, css, width, height);

            if (string.Equals(
                    Environment.GetEnvironmentVariable(RegenerateEnvVar), "1",
                    StringComparison.Ordinal)) {
                WriteBaseline(baselinePath, actualPng);
                return;
            }

            if (!File.Exists(baselinePath)) {
                // First-run: seed the baseline so the author can visually inspect it
                // and then commit it.  Subsequent runs verify.
                WriteBaseline(baselinePath, actualPng);
                return;
            }

            byte[] expectedPng = File.ReadAllBytes(baselinePath);
            var result = GoldenRunner.Compare(actualPng, expectedPng, tolerance);
            if (!result.Passed) {
                // Write failure artifacts next to the baselines directory.
                string outDir = Path.Combine(
                    Path.GetDirectoryName(baselinePath) ?? ".", "..", "Out.GPU");
                outDir = Path.GetFullPath(outDir);
                Directory.CreateDirectory(outDir);
                string name = Path.GetFileNameWithoutExtension(baselinePath);
                string actualPath = Path.Combine(outDir, name + ".actual.png");
                string diffPath   = Path.Combine(outDir, name + ".diff.png");
                File.WriteAllBytes(actualPath, actualPng);
                if (result.DiffImage != null) {
                    byte[] diffPng = PngWriter.Encode(result.DiffImage, result.Width, result.Height);
                    File.WriteAllBytes(diffPath, diffPng);
                }
                throw new GoldenMismatchException(
                    $"GPU golden mismatch for '{Path.GetFileName(snippetPath)}': {result.FailureReason}\n" +
                    $"  baseline: {baselinePath}\n" +
                    $"  actual:   {actualPath}\n" +
                    $"  diff:     {diffPath}\n" +
                    $"  set {RegenerateEnvVar}=1 to overwrite the baseline");
            }
        }

        static void WriteBaseline(string baselinePath, byte[] png) {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath));
            File.WriteAllBytes(baselinePath, png);
        }
    }
}
#endif
