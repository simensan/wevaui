#if UNITY_2023_1_OR_NEWER
using System.IO;
using NUnit.Framework;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    // Verifies the SDF/FontEngine kerning path (UnityFontEngineBackend reads the
    // font's GPOS/kern pair adjustments, wired into SdfFontMetrics.GetKern so
    // both layout and paint kern). Uses a system font with a known kern table;
    // skips gracefully on platforms/CI without one.
    public class FontEngineKerningTests {
        static string FindKerningFont() {
            string[] candidates = {
                @"C:\Windows\Fonts\arial.ttf",
                @"C:\Windows\Fonts\times.ttf",
                @"C:\Windows\Fonts\georgia.ttf",
                "/Library/Fonts/Arial.ttf",
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            };
            foreach (var p in candidates) if (File.Exists(p)) return p;
            return null;
        }

        [Test]
        public void Backend_reads_pair_kerning_from_a_kerning_font() {
            var path = FindKerningFont();
            if (path == null) Assert.Ignore("no known kerning font on this platform");

            var be = new UnityFontEngineBackend();
            var face = new FaceInfo("KernTest", path, 400, FaceInfo.StyleNormal);

            // A/V is a canonical negative-kern pair in virtually every text font.
            bool ok = be.TryGetKernAdvance(face, 'A', 'V', 40.0, out double kern);
            Assert.That(ok, Is.True, "A/V should resolve a pair-kern record");
            Assert.That(kern, Is.LessThan(0.0), "A/V kern should pull the pair tighter (negative)");

            // Kerning scales linearly with font size.
            be.TryGetKernAdvance(face, 'A', 'V', 80.0, out double kern80);
            Assert.That(kern80, Is.EqualTo(kern * 2.0).Within(0.5), "kern scales with size");

            // A non-kerning pair (or absent record) yields zero.
            bool ok0 = be.TryGetKernAdvance(face, 'x', 'x', 40.0, out double k0);
            Assert.That(ok0 ? k0 : 0.0, Is.EqualTo(0.0).Within(0.001));
        }
    }
}
#endif
