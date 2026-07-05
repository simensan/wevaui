using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Audit item (inventory): `html, body { height: 100% }` →
    // `main { height: 100% }` must resolve the percent chain down from the
    // viewport (CSS2 §10.5: percentage heights resolve against a parent with
    // a DEFINITE height; html's percentage resolves against the initial
    // containing block). The audit measured main.inventory at h=120
    // (content-collapsed) while Chrome gives the full viewport height, with
    // the flex:1 .inv-body overflowing to 1203 instead of filling 661.
    public class PercentHeightChainTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Full_doc_percent_height_chain_reaches_main() {
            const string css =
                "html, body { width: 100%; height: 100%; margin: 0; padding: 0; }" +
                ".inventory { width: 100%; height: 100%; box-sizing: border-box; padding: 24px; " +
                "             display: flex; flex-direction: column; gap: 16px; }" +
                ".inv-header { height: 48px; flex: none; }" +
                ".inv-body { flex: 1; display: grid; grid-template-columns: 1fr 320px; " +
                "            gap: 16px; min-height: 0; }" +
                ".grid { } .side { }";
            const string html =
                "<!DOCTYPE html><html><head></head><body>" +
                "<main class='inventory'>" +
                "<header class='inv-header'></header>" +
                "<div class='inv-body'><div class='grid'></div><aside class='side'></aside></div>" +
                "</main></body></html>";
            var (root, _, _) = Build(html, css, viewportWidth: 1434, viewportHeight: 781);

            var main = FirstWithClass(root, "inventory");
            var body = FirstWithClass(root, "inv-body");
            Assert.That(main, Is.Not.Null);
            // height:100% through html→body→main = the 781px viewport.
            Assert.That(main.Height, Is.EqualTo(781).Within(1.0),
                $"main height:100%% must reach the viewport, got {main.Height:F1}");
            // flex:1 body fills what's left: 781 − 2×24 pad − 48 header − 16 gap = 669.
            Assert.That(body.Height, Is.EqualTo(669).Within(1.0),
                $".inv-body flex:1 must fill the remaining height, got {body.Height:F1}");
        }

        [Test]
        public void Real_inventory_sample_main_fills_viewport() {
            // Load the actual sample files — the simplified repro above passes,
            // so whatever collapsed main.inventory to 120 in the live audit
            // hides in the real content.
            string root2 = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
            string html = System.IO.File.ReadAllText(System.IO.Path.Combine(root2, "Assets/UI/inventory.html"));
            string css = System.IO.File.ReadAllText(System.IO.Path.Combine(root2, "Assets/UI/inventory.css"));
            var (root, _, _) = Build(html, css, viewportWidth: 1434, viewportHeight: 781);

            var main = FirstWithClass(root, "inventory");
            var body = FirstWithClass(root, "inv-body");
            Assert.That(main, Is.Not.Null);
            System.Console.WriteLine($"main H={main.Height:F1} body H={(body != null ? body.Height : -1):F1}");
            Assert.That(main.Height, Is.EqualTo(781).Within(2.0),
                $"main.inventory height:100%% must fill the viewport, got {main.Height:F1}");
        }
    }
}
