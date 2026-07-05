using System;
using System.IO;
using Weva.Testing.Goldens;

namespace Weva.BaselineGen {
    static class Program {
        static int Main(string[] args) {
            // First arg is repo root or "selfcheck"; "selfcheck <root>" runs the rasterizer
            // primitive smoke tests and exits.
            if (args.Length > 0 && args[0] == "selfcheck") {
                string root2 = args.Length > 1 ? args[1] : ".";
                int failures = TestSelfCheck.Run(root2);
                Console.WriteLine(failures == 0 ? "OK" : $"FAILED ({failures})");
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "verify") {
                string root2 = args.Length > 1 ? args[1] : ".";
                int failures = GoldenAssertCheck.Run(root2);
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "layoutalloc") {
                int failures = LayoutAllocCheck.Run();
                Console.WriteLine(failures == 0 ? "OK" : $"FAILED ({failures})");
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "inline-split-check") {
                int failures = InlineSplittingCheck.Run();
                Console.WriteLine(failures == 0 ? "OK" : $"FAILED ({failures})");
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "layout-build-bench") {
                return LayoutBuildBench.Run();
            }
            if (args.Length > 0 && args[0] == "reactive-paint-check") {
                return ReactivePaintCheck.Run();
            }
            if (args.Length > 0 && args[0] == "layout-dump") {
                return LayoutDump.Run(args);
            }
            string root = args.Length > 0
                ? args[0]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string snippetsDir = Path.Combine(root, "Packages", "com.wevaui", "Tests", "Runtime", "Goldens", "Snippets");
            string baselinesDir = Path.Combine(root, "Packages", "com.wevaui", "Tests", "Runtime", "Goldens", "Baselines");
            Directory.CreateDirectory(baselinesDir);
            int width = 800, height = 600;

            int count = 0;
            foreach (var htmlPath in Directory.GetFiles(snippetsDir, "*.html")) {
                string name = Path.GetFileNameWithoutExtension(htmlPath);
                string cssPath = Path.Combine(snippetsDir, name + ".css");
                string html = File.ReadAllText(htmlPath);
                string css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
                Console.WriteLine($"Rendering {name} ({width}x{height})...");
                byte[] png = GoldenRunner.RenderToPng(html, css, width, height);
                string outPath = Path.Combine(baselinesDir, name + ".png");
                File.WriteAllBytes(outPath, png);
                Console.WriteLine($"  -> {outPath} ({png.Length} bytes)");
                count++;
            }
            Console.WriteLine($"Wrote {count} baselines.");
            return 0;
        }
    }
}
