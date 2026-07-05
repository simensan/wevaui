using System;
using System.IO;
using Weva.Testing.Goldens;

namespace Weva.BaselineGen {
    static class GoldenAssertCheck {
        public static int Run(string root) {
            string snippetsDir = Path.Combine(root, "Packages", "com.wevaui", "Tests", "Runtime", "Goldens", "Snippets");
            string baselinesDir = Path.Combine(root, "Packages", "com.wevaui", "Tests", "Runtime", "Goldens", "Baselines");

            int failures = 0;
            foreach (var html in Directory.GetFiles(snippetsDir, "*.html")) {
                string name = Path.GetFileNameWithoutExtension(html);
                string baseline = Path.Combine(baselinesDir, name + ".png");
                try {
                    GoldenAssert.Match(html, baseline);
                    Console.WriteLine($"PASS  {name}");
                } catch (Exception ex) {
                    Console.WriteLine($"FAIL  {name}: {ex.Message}");
                    failures++;
                }
            }
            Console.WriteLine(failures == 0 ? "OK" : $"FAILED ({failures})");
            return failures;
        }
    }
}
