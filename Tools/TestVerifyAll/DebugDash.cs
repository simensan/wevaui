using System;
using System.IO;
using System.Linq;
using Weva.Layout.Boxes;

namespace TestRunner {
    // Scratch probe (invoked via `dotnet run -c Release --debug-dash`):
    // measures advanced-dashboard sections headlessly to re-verify the
    // audit's stale page-height numbers without committing brittle asserts.
    static class DebugDash {
        public static void Run() {
            string root2 = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", ".."));
            string html = File.ReadAllText(Path.Combine(root2, "Assets/UI/advanced-dashboard.html"));
            string cssOrig = File.ReadAllText(Path.Combine(root2, "Assets/UI/advanced-dashboard.css"));

            string Gut(string src, string cls) =>
                System.Text.RegularExpressions.Regex.Replace(src,
                    "(<section class=\"" + cls + "\"[^>]*>).*?(</section>)",
                    "$1<div style=\"height: 500px\"></div>$2",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

            foreach (var (label, html2) in new (string, string)[] {
                ("original", html),
                ("gut-stats", Gut(html, "stats")),
                ("gut-activity", Gut(html, "activity")),
                ("gut-achievements", Gut(html, "achievements")),
                ("gut-all", Gut(Gut(Gut(html, "stats"), "activity"), "achievements")),
            }) {
                var (r2, _, _) = Weva.Tests.Layout.LayoutTestHelpers.Build(html2, cssOrig, viewportWidth: 1434, viewportHeight: 781);
                var d = Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(r2).FirstOrDefault(x =>
                    x.Element != null && !(x is TextRun)
                    && (x.Element.GetAttribute("class") ?? "").Split(' ').Contains("dashboard"));
                var g = Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(r2).FirstOrDefault(x =>
                    x.Element != null && !(x is TextRun) && x.Element.TagName == "main");
                Console.WriteLine($"[{label}] dashboard H={(d != null ? d.Height : -1):F0} main.grid H={(g != null ? g.Height : -1):F0}");
            }

            string css = cssOrig;
            var (root, _, _) = Weva.Tests.Layout.LayoutTestHelpers.Build(html, css, viewportWidth: 1434, viewportHeight: 781);

            void Dump(string cls) {
                var b = Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root).FirstOrDefault(x =>
                    x.Element != null && !(x is TextRun)
                    && (x.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));
                Console.WriteLine(b == null
                    ? $"{cls}: NOT FOUND"
                    : $"{cls}: X={b.X:F0} Y={b.Y:F0} W={b.Width:F0} H={b.Height:F0}");
            }
            Dump("dashboard");
            Dump("grid");
            Dump("achievements");
            Dump("ach-grid");
            Dump("ach");

            // Walk main.grid's direct children: which item inflates the rows?
            var gridBox = Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root).FirstOrDefault(x =>
                x.Element != null && !(x is TextRun)
                && (x.Element.GetAttribute("class") ?? "").Split(' ').Contains("grid")
                && x.Element.TagName == "main");
            if (gridBox != null) {
                Console.WriteLine("--- main.grid children ---");
                foreach (var c in gridBox.Children) {
                    string cls = c.Element != null ? (c.Element.GetAttribute("class") ?? c.Element.TagName) : c.GetType().Name;
                    Console.WriteLine($"  {cls}: Y={c.Y:F0} W={c.Width:F0} H={c.Height:F0}");
                    foreach (var gc in c.Children) {
                        string gcls = gc.Element != null ? (gc.Element.GetAttribute("class") ?? gc.Element.TagName) : gc.GetType().Name;
                        Console.WriteLine($"      {gcls}: Y={gc.Y:F0} H={gc.Height:F0}");
                    }
                }
            }
        }
    }
}
