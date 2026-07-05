using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Regression coverage for the incremental cascade path. ComputeAllIncremental
    // is meant to re-walk only the ancestor closure of elements the tracker
    // flagged dirty (plus any subtree whose root's ComputedStyle.Version bumped
    // as a result). These tests pin down four contracts:
    //
    //  1. Incremental output equals full-cascade output element-by-element. The
    //     gains from skipping clean subtrees can't come at the cost of stale
    //     entries leaking through resultMap.
    //  2. The clean subtree IS actually skipped — measured via the
    //     ComputeOrHit cache-hit counter, which advances exactly once per
    //     visited element.
    //  3. A subtree whose ancestor re-cascade bumps ComputedStyle.Version
    //     falls back to the full Walk so the descendants observe the new
    //     ParentStyleVersion.
    //  4. The "preconditions fail → call ComputeAll" fallback is observable:
    //     resultMap is populated for ALL elements after an initial-pass
    //     incremental call (which falls through because resultMap was empty).
    public class CascadeIncrementalWalkTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Document BuildDoc() {
            // Two-section document with leaf elements. Re-cascading one leaf
            // should leave the other section's leaves untouched.
            return HtmlParser.Parse(
                "<section id=\"left\" class=\"side\">" +
                "  <ul><li id=\"l1\" class=\"item\">A</li><li id=\"l2\" class=\"item\">B</li></ul>" +
                "</section>" +
                "<section id=\"right\" class=\"side\">" +
                "  <ul><li id=\"r1\" class=\"item\">C</li><li id=\"r2\" class=\"item\">D</li></ul>" +
                "</section>"
            );
        }

        static OriginatedStylesheet BuildSheet() {
            return Author(
                ".side { background: #111; }" +
                ".item { color: red; }" +
                ".item.flagged { color: blue; }"
            );
        }

        [Test]
        public void Incremental_output_matches_full_cascade() {
            var doc = BuildDoc();
            var engineA = new CascadeEngine(new List<OriginatedStylesheet> { BuildSheet() });
            var engineB = new CascadeEngine(new List<OriginatedStylesheet> { BuildSheet() });

            // Initial pass — both engines do a full cascade. resultMap is
            // empty, so ComputeAllIncremental falls back to ComputeAll.
            var resultA = engineA.ComputeAll(doc);
            var resultB = engineB.ComputeAllIncremental(doc, null, new[] { doc.GetElementById("l1") });

            Assert.That(resultB.Count, Is.EqualTo(resultA.Count));
            foreach (var kv in resultA) {
                Assert.That(resultB.TryGetValue(kv.Key, out var bs), Is.True, "missing element after fallback");
                Assert.That(bs.Get(CssProperties.ColorId), Is.EqualTo(kv.Value.Get(CssProperties.ColorId)));
            }
        }

        [Test]
        public void Incremental_skips_clean_subtrees() {
            var doc = BuildDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildSheet() });

            // Prime the cache with a full pass.
            engine.ComputeAll(doc);
            long primingHits = engine.CacheHits;
            long primingMisses = engine.CacheMisses;

            // Mark a single leaf in the LEFT section as dirty; everything in
            // the right section is clean and should be skipped.
            var l1 = doc.GetElementById("l1");
            engine.ComputeAllIncremental(doc, null, new[] { l1 });

            long passHits = engine.CacheHits - primingHits;
            long passMisses = engine.CacheMisses - primingMisses;

            // The closure is {l1, l1's ul, l1's section, root}. Four
            // elements visited; each is a cache HIT because nothing
            // actually changed (state digest, parent style, etc. are
            // unchanged). The right section's section/ul/li1/li2 are
            // skipped entirely.
            //
            // Full cascade would visit all 9 elements (2 sections + 2 ULs
            // + 4 LIs + the implicit document root). We want strictly less.
            Assert.That(passHits, Is.LessThan(9), "incremental visited every element — closure pruning didn't fire");
            Assert.That(passHits, Is.GreaterThanOrEqualTo(1), "incremental visited zero elements — walkSet build failed");
            Assert.That(passMisses, Is.EqualTo(0), "clean closure shouldn't produce cache misses");
        }

        [Test]
        public void Incremental_falls_back_to_full_when_resultMap_empty() {
            var doc = BuildDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildSheet() });

            // First call with hints; resultMap is empty, so incremental
            // CANNOT short-circuit (no prior state to skip against). The
            // method should detect this and call ComputeAll under the hood.
            var l1 = doc.GetElementById("l1");
            var result = engine.ComputeAllIncremental(doc, null, new[] { l1 });

            // Every element should be present in the result — proof the
            // full cascade ran, not a one-element subtree walk.
            var ids = new[] { "left", "l1", "l2", "right", "r1", "r2" };
            foreach (var id in ids) {
                var e = doc.GetElementById(id);
                Assert.That(result.ContainsKey(e), Is.True, "id=" + id + " missing after initial-pass fallback");
            }
        }

        [Test]
        public void Incremental_recurses_into_descendants_when_ancestor_style_bumps() {
            // State change on `left` bumps its own state digest (the cache
            // key shifts), which fires a cache miss and re-cascades the
            // section's style. The new style differs (`.side:hover { color:
            // blue }` overrides `.side { color: red }`), so the
            // section's ComputedStyle.Version bumps. Descendants' cache
            // keys all carry ParentStyleVersion, so they must re-cascade
            // too even though they're NOT in the dirty hint set.
            // WalkIncremental detects the version bump and switches to the
            // full Walk for the changed element's children — this test
            // pins that fallback so a future "always skip clean siblings"
            // mis-optimization doesn't silently break inheritance.
            var doc = BuildDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                Author(
                    ".side { color: red; }" +
                    ".side:hover { color: blue; }"
                )
            });

            var hoverProvider = new FakeHoverStateProvider();
            engine.ComputeAll(doc, hoverProvider);
            long primingHits = engine.CacheHits;
            long primingMisses = engine.CacheMisses;

            // Flip :hover state on the LEFT section, then run incremental.
            // The state-only change doesn't touch the DOM (snapshotDirty
            // stays false), so the incremental path actually runs.
            var left = doc.GetElementById("left");
            hoverProvider.SetHovered(left, true);

            engine.ComputeAllIncremental(doc, hoverProvider, new[] { left });

            long passHits = engine.CacheHits - primingHits;
            long passMisses = engine.CacheMisses - primingMisses;

            // `left`'s state digest changed → cache miss → re-cascade.
            // The cascade-produced style now has `color: blue` instead
            // of `red`, so ComputedStyle.Version bumped. Descendants'
            // cache keys (which include parentStyle.Version) no longer
            // match, so the walk falls through to the full Walk for
            // each descendant of `left`. That branch covers `left`'s
            // ul + two LIs = 3 elements.
            //
            // Plus `left` itself: 1 element re-cascaded (miss).
            //
            // The right section is untouched — its section, ul, and
            // both leaves stay cached and aren't visited.
            Assert.That(passMisses, Is.GreaterThanOrEqualTo(1),
                "left's own state change should miss the cache");
            Assert.That(passHits + passMisses, Is.GreaterThanOrEqualTo(4),
                "ancestor style bump should fan out re-eval to descendants");
            // Right section's 4 elements should be left alone. Total visits
            // are bounded by the left half (4 content elements) plus the
            // walkSet's implicit html/body wrappers (HtmlParser injects
            // both around any fragment-style input), so ≤ 6.
            Assert.That(passHits + passMisses, Is.LessThanOrEqualTo(6),
                "right-section descendants should not have been visited");
        }

        [Test]
        public void Incremental_runs_after_attribute_mutation_without_falling_back() {
            // Click-cascade scenario: a class attribute on one card flips
            // (e.g. .card → .card.selected). Pre-fix, OnDocumentMutated set
            // snapshotDirty=true for ANY mutation, which made the incremental
            // path bail to a full ComputeAll — visiting every element in the
            // tree. This test pins the new behavior: attribute mutations
            // still refill the snapshot but the walk stays incremental, so
            // sibling cards' cache entries survive.
            var doc = BuildDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildSheet() });

            // Prime.
            engine.ComputeAll(doc);
            long primingHits = engine.CacheHits;
            long primingMisses = engine.CacheMisses;

            // Flip a class on l1 — the actual SetAttribute call routes
            // through Document.Mutated, which the engine subscribes to.
            // Without the fix, this set snapshotDirty=true unconditionally
            // and the next incremental call would land in ComputeAll.
            var l1 = doc.GetElementById("l1");
            l1.SetAttribute("class", "item flagged");

            engine.ComputeAllIncremental(doc, null, new[] { l1 });

            long passHits = engine.CacheHits - primingHits;
            long passMisses = engine.CacheMisses - primingMisses;
            long totalVisits = passHits + passMisses;

            // Visit count must be much smaller than the full tree (~9
            // elements counting wrappers). With the fix, only the ancestor
            // closure of l1 is walked: l1 itself, its ul, its section, the
            // body, the html. ≤ 6 visits.
            Assert.That(totalVisits, Is.LessThanOrEqualTo(6),
                "attribute mutation triggered full-cascade fallback instead of staying incremental");

            // The class change adds `.item.flagged` matches → l1's style
            // shifts to color: blue. Verify the actual style updated.
            var l1Style = engine.ResultMap[l1];
            Assert.That(l1Style.Get(CssProperties.ColorId), Is.EqualTo("blue"),
                "l1's style didn't reflect the new .flagged match — incremental walk skipped the dirty element");

            // Sibling l2 was not in the dirty closure and its class didn't
            // change, so its style should remain red AND its cache entry
            // should not have been refreshed.
            var l2 = doc.GetElementById("l2");
            var l2Style = engine.ResultMap[l2];
            Assert.That(l2Style.Get(CssProperties.ColorId), Is.EqualTo("red"),
                "l2's style drifted — incremental walk leaked re-cascade to siblings");
        }

        [Test]
        public void Tree_shape_mutation_still_falls_back_to_full_cascade() {
            // ChildAdded/ChildRemoved invalidate nth-child / sibling
            // combinators that the per-element cache key can't track. The
            // engine must drop to a full cascade in this case. Pin it
            // explicitly so a future "all mutations are incremental" tweak
            // doesn't silently break selector correctness.
            var doc = BuildDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                Author(".item:nth-child(2) { color: green; }")
            });
            engine.ComputeAll(doc);

            // l2 is the 2nd child → matches :nth-child(2) → green.
            // r2 same.
            var l2 = doc.GetElementById("l2");
            Assert.That(engine.ResultMap[l2].Get(CssProperties.ColorId), Is.EqualTo("green"));

            // Remove l1. Now l2 is the 1st child → :nth-child(2) no longer
            // matches → color should fall back to default (the .item rule
            // doesn't set one in this stylesheet).
            var l1 = doc.GetElementById("l1");
            l1.Parent.RemoveChild(l1);

            // Incremental call hinting just l2 — full cascade should fire
            // anyway because tree shape changed.
            var result = engine.ComputeAllIncremental(doc, null, new[] { l2 });

            Assert.That(result[l2].Get(CssProperties.ColorId), Is.Not.EqualTo("green"),
                "tree-shape change didn't trigger full-cascade fallback; nth-child match leaked stale state");
        }

        // Minimal IElementStateProvider for tests: tracks one hovered
        // element, returns ElementState.Hover for it, ElementState.None
        // for everything else, and bumps a monotone version on each
        // SetHovered call so cache keys notice the change.
        sealed class FakeHoverStateProvider : Weva.Css.Selectors.IElementStateProvider {
            Element hovered;
            long version;
            public long Version => version;
            public Weva.Css.Selectors.ElementState GetState(Element e) {
                if (e != null && e == hovered) return Weva.Css.Selectors.ElementState.Hover;
                return Weva.Css.Selectors.ElementState.None;
            }
            public void SetHovered(Element e, bool on) {
                hovered = on ? e : null;
                version++;
            }
        }
    }
}
