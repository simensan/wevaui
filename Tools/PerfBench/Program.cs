using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Weva.PerfBench {
    static class Program {
        static int Main(string[] args) {
            string mode = args.Length > 0 ? args[0] : "all";
            string baselineFile = null;
            string outFile = null;
            bool slow = false;
            int stressFrames = 5000;
            bool stressAnim = true;
            bool stressVerbose = false;
            for (int i = 1; i < args.Length; i++) {
                if (args[i] == "--baseline" && i + 1 < args.Length) baselineFile = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--slow") slow = true;
                else if (args[i] == "--frames" && i + 1 < args.Length) int.TryParse(args[++i], out stressFrames);
                else if (args[i] == "--no-anim") stressAnim = false;
                else if (args[i] == "--verbose") stressVerbose = true;
            }

            if (mode == "stress") {
                StressRunner.Run(stressFrames, stressAnim, stressVerbose);
                return 0;
            }

            var results = new List<BenchResult>();

            if (mode == "all" || mode == "cascade") BenchRunner.RunCascade(results, slow);
            if (mode == "all" || mode == "layout") BenchRunner.RunLayout(results, slow);
            if (mode == "all" || mode == "kernels" || mode == "native") BenchRunner.RunLayoutKernels(results, slow);
            if (mode == "all" || mode == "paint") BenchRunner.RunPaint(results, slow);
            if (mode == "all" || mode == "endtoend") BenchRunner.RunEndToEnd(results);

            Dictionary<string, BenchResult> baseline = null;
            if (baselineFile != null && File.Exists(baselineFile)) {
                baseline = LoadBaseline(baselineFile);
            }

            string md = FormatMarkdown(results, baseline);
            Console.WriteLine(md);
            if (outFile != null) {
                File.WriteAllText(outFile, md);
                Console.WriteLine($"Wrote markdown to {outFile}");
            }

            // Dump the canonical JSON baseline only for full runs. Focused
            // modes are often exploratory and should not clobber baselines.
            if (outFile == null && mode == "all") {
                string defaultBaseline = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "baselines.json");
                try {
                    File.WriteAllText(defaultBaseline,
                        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                } catch { /* best-effort */ }
            }

            return 0;
        }

        static Dictionary<string, BenchResult> LoadBaseline(string path) {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<BenchResult>>(json);
            var d = new Dictionary<string, BenchResult>();
            if (list != null) foreach (var r in list) d[r.Name] = r;
            return d;
        }

        static string FormatMarkdown(List<BenchResult> results, Dictionary<string, BenchResult> baseline) {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("| bench | scale | median (ms) | p95 (ms) | allocs/call | vs baseline | notes |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
            foreach (var r in results) {
                string vsb = "—";
                if (baseline != null && baseline.TryGetValue(r.Name, out var b) && b.MedianMs > 0) {
                    double ratio = r.MedianMs / b.MedianMs;
                    vsb = ratio < 1.0 ? $"{(1 - ratio) * 100:F0}% faster" : $"{(ratio - 1) * 100:F0}% slower";
                }
                string allocs = r.BytesPerCall < 0 ? "—" : FormatBytes(r.BytesPerCall);
                sb.AppendLine($"| {r.Name} | {r.Scale} | {r.MedianMs:F3} | {r.P95Ms:F3} | {allocs} | {vsb} | {r.Notes ?? ""} |");
            }
            return sb.ToString();
        }

        static string FormatBytes(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
        }
    }

    sealed class BenchResult {
        public string Name { get; set; }
        public string Scale { get; set; }
        public double MedianMs { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public long BytesPerCall { get; set; } = -1;
        public string Notes { get; set; }
    }
}
