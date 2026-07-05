using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Regression coverage for CODE_AUDIT_FINDINGS.md MC1:
    //   BackgroundResolver.gradientNoCache was an unbounded HashSet<string>
    //   that grew on every unique uncacheable (currentcolor-containing OR
    //   parse-failing) gradient raw string ever seen. The fix bounds the set
    //   at Cap (256) entries with the drop-new-on-overflow
    //   policy used by the sibling gradientCache / gradientBrushCache.
    //
    // We exercise the negative cache via `currentcolor`-bearing gradients —
    // each unique raw string forces a NoCache insert because
    // ContainsCurrentColor(raw) short-circuits the positive-cache path.
    // Linear gradients are bounds-independent so the (Width, Height) slot
    // of the key collapses to (0, 0) and the test doesn't need to vary the
    // paint bounds to manufacture distinct entries.
    public class BackgroundResolverGradientNoCacheTests {
        // Read the live cap off the resolver so the test tracks the source
        // of truth (rather than mirroring 256 here).
        static int Cap => BackgroundResolver.GradientNoCacheCap_TestOnly;

        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds() => new Rect(0, 0, 100, 50);

        [SetUp]
        public void ResetCaches() {
            // Every test relies on the post-reset cache state, so the order
            // of cases in a single run can't leak into another's count math.
            BackgroundResolver.ResetCaches_TestOnly();
        }

        // Builds a linear-gradient whose raw text is unique per `i` AND that
        // mentions `currentcolor` — the second token routes through the
        // ContainsCurrentColor branch and so populates gradientNoCache rather
        // than gradientCache. The unique color stop on the right keeps each
        // raw string distinct (raw text == the dictionary key).
        static string GradientWithCurrentColor(int i) {
            // Stable two-digit hex so every string is the same length; the
            // value cycles through the 256-color RGB space so no two indices
            // collide.
            byte r = (byte)(i & 0xFF);
            byte g = (byte)((i >> 8) & 0xFF);
            return "linear-gradient(to right, currentcolor, #" +
                r.ToString("x2") + g.ToString("x2") + "00)";
        }

        static void ResolveOne(string raw) {
            var s = Style();
            s.Set("color", "red");
            s.Set("background-image", raw);
            // Drive the parser; the result Brush isn't asserted here — these
            // tests focus on cache occupancy. Resolution must succeed (>= 2
            // stops, valid currentcolor binding) for the NoCache insert to
            // fire on the success branch.
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null,
                "Gradient should resolve to a Brush even with currentcolor stops.");
        }

        [Test]
        public void Under_cap_every_unique_currentcolor_gradient_is_recorded() {
            // Sanity baseline: with the cap at 256, inserting 100 distinct
            // raw strings should land all 100 in the negative cache.
            const int n = 100;
            for (int i = 0; i < n; i++) {
                ResolveOne(GradientWithCurrentColor(i));
            }
            Assert.That(BackgroundResolver.GradientNoCacheCount_TestOnly(),
                Is.EqualTo(n),
                "All sub-cap distinct currentcolor gradients should land in the negative cache.");
        }

        [Test]
        public void Repeated_lookups_do_not_grow_negative_cache() {
            // Pin the hit path: resolving the SAME raw string many times
            // adds exactly one entry, not one per call. Regression guard
            // against accidentally moving the Add outside the existing
            // duplicate-suppression a HashSet provides.
            string raw = GradientWithCurrentColor(0);
            for (int i = 0; i < 50; i++) {
                ResolveOne(raw);
            }
            Assert.That(BackgroundResolver.GradientNoCacheCount_TestOnly(),
                Is.EqualTo(1),
                "Repeated resolves of the same raw must collapse to one HashSet entry.");
        }

        [Test]
        public void At_cap_negative_cache_stops_growing_but_new_gradients_still_resolve() {
            // Push past the cap and verify (a) the set stays bounded at
            // Cap and (b) the (cap+1)th distinct gradient
            // still parses to a valid Brush — drop-new-on-overflow only
            // refuses to memoize the skip-decision; it must NOT swallow
            // the gradient itself.
            const int extra = 10;
            int total = Cap + extra;
            for (int i = 0; i < total; i++) {
                ResolveOne(GradientWithCurrentColor(i));
            }
            Assert.That(BackgroundResolver.GradientNoCacheCount_TestOnly(),
                Is.LessThanOrEqualTo(Cap),
                "Negative cache must respect its soft cap.");

            // The (cap+1)th onward also resolve cleanly — re-run the last
            // few to confirm they didn't silently drop on the overflow path.
            for (int i = Cap; i < total; i++) {
                var s = Style();
                s.Set("color", "red");
                s.Set("background-image", GradientWithCurrentColor(i));
                var brush = BackgroundResolver.ResolveBackground(s, Bounds());
                Assert.That(brush, Is.Not.Null,
                    "Post-cap gradients must still parse to a Brush; the cache cap is on the skip-set, not the parse path.");
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient),
                    "Post-cap result should remain a gradient brush.");
            }
        }

        [Test]
        public void Reset_clears_all_caches() {
            // Populate, then reset, then re-populate one entry — the count
            // must reflect only the post-reset insert. Pins ResetCaches_TestOnly
            // as a real Clear (not a no-op stub).
            for (int i = 0; i < 20; i++) {
                ResolveOne(GradientWithCurrentColor(i));
            }
            Assume.That(BackgroundResolver.GradientNoCacheCount_TestOnly(),
                Is.GreaterThan(0));

            BackgroundResolver.ResetCaches_TestOnly();
            Assert.That(BackgroundResolver.GradientNoCacheCount_TestOnly(),
                Is.EqualTo(0),
                "ResetCaches_TestOnly must empty the negative cache.");

            ResolveOne(GradientWithCurrentColor(0));
            Assert.That(BackgroundResolver.GradientNoCacheCount_TestOnly(),
                Is.EqualTo(1),
                "Insert path must work normally after reset.");
        }

        [Test]
        public void Cached_gradient_returns_same_instance_on_repeat() {
            // Regression pin for the POSITIVE cache hit path (sibling to MC1
            // but on gradientCache, not gradientNoCache). A non-currentcolor
            // gradient resolved twice should return the same Gradient instance
            // — proving the positive cache still serves cache hits after the
            // MC1 changes added the negative-cache cap branch.
            string raw = "linear-gradient(45deg, red, blue)";
            var s1 = Style();
            s1.Set("color", "black");
            s1.Set("background-image", raw);
            var b1 = BackgroundResolver.ResolveBackground(s1, Bounds());

            var s2 = Style();
            s2.Set("color", "black");
            s2.Set("background-image", raw);
            var b2 = BackgroundResolver.ResolveBackground(s2, Bounds());

            Assert.That(b1, Is.Not.Null);
            Assert.That(b2, Is.Not.Null);
            // Same inner Gradient ref — the Brush wrapper may differ (those
            // are cached separately via gradientBrushCache keyed on tile),
            // but the gradient itself is a process-static singleton per raw.
            Assert.That(b2.GradientValue, Is.SameAs(b1.GradientValue),
                "Identical raw gradient strings must share the cached Gradient instance.");
        }
    }
}
