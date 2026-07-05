using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // CSS Fonts L4 §5.2 — FontFaceMatcher algorithm tests.
    //
    // These tests exercise the pure matching math in isolation from FontResolver
    // and UnityEngine, so they run under the headless TestVerifyAll suite.
    [TestFixture]
    public class FontFaceMatcherTests {

        // Convenience builder: (min, max, italic?, path).
        static FontFaceMatcher.FaceEntry E(float min, float max, bool italic, string path) =>
            new FontFaceMatcher.FaceEntry(min, max, italic, path);

        // --- single-face fast path -------------------------------------------

        [Test]
        public void Single_face_always_returned_regardless_of_weight_or_style() {
            var faces = new List<FontFaceMatcher.FaceEntry> { E(400, 400, false, "/r.ttf") };
            Assert.That(FontFaceMatcher.Match(faces, 700, true),  Is.EqualTo("/r.ttf"));
            Assert.That(FontFaceMatcher.Match(faces, 100, false), Is.EqualTo("/r.ttf"));
        }

        [Test]
        public void Empty_list_returns_null() {
            Assert.That(FontFaceMatcher.Match(new List<FontFaceMatcher.FaceEntry>(), 400, false), Is.Null);
        }

        // --- exact range containment -----------------------------------------

        [Test]
        public void Exact_range_hit_returns_matching_face() {
            // Regular covers 100-600, Bold covers 700-900.
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 600, false, "/regular.ttf"),
                E(700, 900, false, "/bold.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, false), Is.EqualTo("/regular.ttf"));
            Assert.That(FontFaceMatcher.Match(faces, 700, false), Is.EqualTo("/bold.ttf"));
            Assert.That(FontFaceMatcher.Match(faces, 900, false), Is.EqualTo("/bold.ttf"));
        }

        [Test]
        public void Exact_boundary_weight_min_is_inclusive() {
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(700, 900, false, "/bold.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 700, false), Is.EqualTo("/bold.ttf"));
        }

        [Test]
        public void Exact_boundary_weight_max_is_inclusive() {
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(700, 900, false, "/bold.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 900, false), Is.EqualTo("/bold.ttf"));
        }

        // --- directional nearest weight: desired >= 600 ----------------------

        [Test]
        public void Weight_ge_600_prefers_heavier_then_lighter() {
            // Desired = 650, faces at 400 and 800. Should prefer 800 (heavier).
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(400, 400, false, "/light.ttf"),
                E(800, 800, false, "/heavy.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 650, false), Is.EqualTo("/heavy.ttf"));
        }

        [Test]
        public void Weight_ge_600_falls_back_to_lighter_when_no_heavier_exists() {
            // Desired = 700, only a 400-weight face available (no Bold TTF).
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(400, 400, false, "/regular.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 700, false), Is.EqualTo("/regular.ttf"));
        }

        [Test]
        public void Weight_ge_600_nearest_heavier_wins_over_farther_heavier() {
            // Desired = 650, faces at 700 and 900. 700 is nearest above 650.
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(700, 700, false, "/bold.ttf"),
                E(900, 900, false, "/black.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 650, false), Is.EqualTo("/bold.ttf"));
        }

        // --- directional nearest weight: desired < 600 -----------------------

        [Test]
        public void Weight_lt_600_prefers_lighter_then_heavier() {
            // Desired = 500, faces at 300 and 700. Should prefer 300 (lighter).
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(300, 300, false, "/light.ttf"),
                E(700, 700, false, "/bold.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 500, false), Is.EqualTo("/light.ttf"));
        }

        [Test]
        public void Weight_lt_600_falls_back_to_heavier_when_no_lighter_exists() {
            // Desired = 300, only a 700-weight face available.
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(700, 700, false, "/bold.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 300, false), Is.EqualTo("/bold.ttf"));
        }

        // --- italic preference + fallback ------------------------------------

        [Test]
        public void Italic_requested_prefers_italic_face() {
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 900, false, "/regular.ttf"),
                E(100, 900, true,  "/italic.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, true), Is.EqualTo("/italic.ttf"));
        }

        [Test]
        public void Normal_requested_prefers_normal_face() {
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 900, false, "/regular.ttf"),
                E(100, 900, true,  "/italic.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, false), Is.EqualTo("/regular.ttf"));
        }

        [Test]
        public void Italic_requested_falls_back_to_normal_when_no_italic_exists() {
            // Only a normal face is registered — italic should still resolve.
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 1000, false, "/regular.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, true), Is.EqualTo("/regular.ttf"));
        }

        [Test]
        public void Normal_requested_falls_back_to_italic_when_no_normal_exists() {
            // Only an italic face is registered — normal should still resolve.
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 1000, true, "/italic.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, false), Is.EqualTo("/italic.ttf"));
        }

        // --- combined weight + italic matching --------------------------------

        [Test]
        public void Bold_italic_resolves_from_matching_bold_italic_face() {
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 600, false, "/regular.ttf"),
                E(700, 900, false, "/bold.ttf"),
                E(100, 600, true,  "/italic.ttf"),
                E(700, 900, true,  "/bold-italic.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 700, true), Is.EqualTo("/bold-italic.ttf"));
        }

        [Test]
        public void Regular_normal_resolves_correctly_with_all_four_faces() {
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(100, 600, false, "/regular.ttf"),
                E(700, 900, false, "/bold.ttf"),
                E(100, 600, true,  "/italic.ttf"),
                E(700, 900, true,  "/bold-italic.ttf"),
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, false), Is.EqualTo("/regular.ttf"));
        }

        // --- registration order ties -----------------------------------------

        [Test]
        public void Exact_range_tie_broken_by_proximity_of_rep_weight_to_desired() {
            // Two faces both cover weight 400 exactly. Rep-weights are 300 and 400.
            // The one with rep 400 is closer to desired 400.
            var faces = new List<FontFaceMatcher.FaceEntry> {
                E(200, 400, false, "/wide.ttf"),  // rep = 300
                E(400, 400, false, "/exact.ttf"), // rep = 400
            };
            Assert.That(FontFaceMatcher.Match(faces, 400, false), Is.EqualTo("/exact.ttf"));
        }
    }
}
