using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    public class CharacterFallbackTests {
        sealed class FixedProbe : CharacterFallback.IGlyphProbe {
            // Maps (familyOrPath -> set of codepoints the face reports having).
            public readonly Dictionary<string, HashSet<uint>> ByFamily = new();
            public bool HasGlyph(FaceInfo face, uint codepoint) {
                return ByFamily.TryGetValue(face.Family, out var set) && set.Contains(codepoint);
            }
        }

        [SetUp]
        public void Reset() {
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/system/sans.ttf",
                ["Arial"] = "/system/arial.ttf",
                ["Liberation Sans"] = "/system/lib.ttf"
            });
        }

        [Test]
        public void Primary_with_glyph_returned_directly() {
            var probe = new FixedProbe();
            probe.ByFamily["Inter"] = new HashSet<uint> { 'A' };
            var fb = new CharacterFallback(probe);
            var primary = new FaceInfo("Inter", "/p", 400, FaceInfo.StyleNormal);
            var resolved = fb.Resolve(primary, 'A', 400, FontStyle.Normal);
            Assert.That(resolved.Family, Is.EqualTo("Inter"));
        }

        [Test]
        public void Falls_through_to_arial_when_primary_missing() {
            var probe = new FixedProbe();
            // Primary "Inter" knows nothing; Arial knows 'A'.
            probe.ByFamily["Arial"] = new HashSet<uint> { 'A' };
            var fb = new CharacterFallback(probe);
            var primary = new FaceInfo("Inter", "/p", 400, FaceInfo.StyleNormal);
            var resolved = fb.Resolve(primary, 'A', 400, FontStyle.Normal);
            Assert.That(resolved.Family, Is.EqualTo("Arial"));
        }

        [Test]
        public void Returns_primary_when_no_face_in_chain_has_glyph() {
            var probe = new FixedProbe();
            // Nobody has the codepoint.
            var fb = new CharacterFallback(probe);
            var primary = new FaceInfo("Inter", "/p", 400, FaceInfo.StyleNormal);
            var resolved = fb.Resolve(primary, 0x1F600, 400, FontStyle.Normal);
            Assert.That(resolved.Family, Is.EqualTo("Inter"));
        }

        [Test]
        public void Custom_chain_overrides_default() {
            var probe = new FixedProbe();
            probe.ByFamily["Liberation Sans"] = new HashSet<uint> { 'X' };
            var fb = new CharacterFallback(probe).WithChain(new[] { "Liberation Sans" });
            var primary = new FaceInfo("Inter", "/p", 400, FaceInfo.StyleNormal);
            var resolved = fb.Resolve(primary, 'X', 400, FontStyle.Normal);
            Assert.That(resolved.Family, Is.EqualTo("Liberation Sans"));
        }
    }
}
