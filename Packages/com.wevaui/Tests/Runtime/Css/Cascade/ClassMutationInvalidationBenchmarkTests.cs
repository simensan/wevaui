using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Class-mutation perf budget. Flipping a class on one card (the
    // ClassBinding path a real game exercises every click) currently
    // refills the DomSnapshot and re-walks the ancestor closure. We
    // want the cascade to visit ONLY the card's subtree plus its
    // ancestor closure cache hits — sibling cards in the same section
    // should never be re-cascaded.
    //
    // This pins the budget so a Stage 3 / RuleFeatureSet class-feature
    // pass can be verified locally without round-tripping through Unity.
    public class ClassMutationInvalidationBenchmarkTests {
        const int CardCount = 40;

        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Document BuildSectionWithCards() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"sec\">");
            for (int i = 0; i < CardCount; i++) {
                sb.Append("<div class=\"card\" id=\"c").Append(i).Append("\">");
                sb.Append("<div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">Card ").Append(i).Append("</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">").Append(i).Append("</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return HtmlParser.Parse(sb.ToString());
        }

        static OriginatedStylesheet StandardSheet() {
            return Author(
                ".card { background: #222; padding: 8px; }" +
                ".card.selected { background: #444; }" +
                ".card .name { font-size: 14px; }" +
                ".card.selected .name { color: yellow; }" +
                ".card .badge { color: #aaa; }"
            );
        }

        [Test]
        public void Class_flip_on_one_card_doesnt_recascade_others() {
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            engine.ComputeAll(doc);
            long primingHits = engine.CacheHits;
            long primingMisses = engine.CacheMisses;

            // Flip "selected" class on card c5 — exact analog of
            // ClassBinding firing on click. The element's SetAttribute
            // bumps Version, fires DomMutation, sets snapshotDirty.
            var c5 = doc.GetElementById("c5");
            c5.SetAttribute("class", "card selected");

            // Hint c5 — what UIDocumentLifecycle would feed in.
            engine.ComputeAllIncremental(doc, null, new[] { c5 });

            long passHits = engine.CacheHits - primingHits;
            long passMisses = engine.CacheMisses - primingMisses;
            long totalVisits = passHits + passMisses;

            // Expected post-fix:
            //   - c5 and its 5 descendants: cache MISS (their digest
            //     includes element.Version which bumped, or for descendants,
            //     parent style change).
            //   - section / body / html (ancestor closure): cache HIT
            //     (their classes didn't change, no rule with .selected in
            //     their subject features).
            //   - 39 sibling cards: untouched.
            //
            // Currently (Stage 1 + 2 only, no class feature plumbing):
            //   Snapshot refills. Incremental walks ancestor closure.
            //   But because the snapshot was refilled, the SelectorIndex
            //   might need rebuilding... actually no, the index is
            //   separate from snapshot. Should still be incremental.
            //
            // Acceptance ceiling: 10 visits.
            Assert.That(totalVisits, Is.LessThanOrEqualTo(10),
                $"class flip on c5 visited {totalVisits} elements; ceiling is 10. " +
                "Pre-Stage-3 baseline measurement needed if this fires.");
            Assert.That(passMisses, Is.LessThanOrEqualTo(6),
                $"only c5's 6-element subtree should re-cascade; got {passMisses}");

            // Verify the class flip actually applied:
            var c5Style = engine.ResultMap[c5];
            Assert.That(c5Style.Get(CssProperties.BackgroundColorId), Does.Contain("44"),
                "c5 must have re-cascaded with .card.selected rule applied");

            // Sibling kept default:
            var c10Style = engine.ResultMap[doc.GetElementById("c10")];
            Assert.That(c10Style.Get(CssProperties.BackgroundColorId), Does.Contain("22"),
                "c10 must keep the default .card background");
        }

        [Test]
        public void Class_flip_then_revert_returns_to_baseline_style() {
            // Round-trip: select, then deselect. Both passes should be
            // tightly bounded.
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            engine.ComputeAll(doc);

            var c5 = doc.GetElementById("c5");

            // Select.
            c5.SetAttribute("class", "card selected");
            long beforeSelect = engine.CacheHits + engine.CacheMisses;
            engine.ComputeAllIncremental(doc, null, new[] { c5 });
            long selectVisits = (engine.CacheHits + engine.CacheMisses) - beforeSelect;

            // Deselect.
            c5.SetAttribute("class", "card");
            long beforeDeselect = engine.CacheHits + engine.CacheMisses;
            engine.ComputeAllIncremental(doc, null, new[] { c5 });
            long deselectVisits = (engine.CacheHits + engine.CacheMisses) - beforeDeselect;

            Assert.That(selectVisits, Is.LessThanOrEqualTo(10), $"select pass visited {selectVisits}");
            Assert.That(deselectVisits, Is.LessThanOrEqualTo(10), $"deselect pass visited {deselectVisits}");

            // Verify deselect produces baseline style.
            var c5Style = engine.ResultMap[c5];
            Assert.That(c5Style.Get(CssProperties.BackgroundColorId), Does.Contain("22"),
                "c5 should return to baseline background after deselect");
        }

        [Test]
        public void Multiple_simultaneous_class_flips_visit_each_subtree_once() {
            // Two clicks queued in one frame: flip class on c5 and c10.
            // Each subtree (~6 elements) should re-cascade; sibling cards
            // should not. Combined budget: ~6 + ~6 + ancestor closure = ~15.
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            engine.ComputeAll(doc);
            long primingHits = engine.CacheHits;
            long primingMisses = engine.CacheMisses;

            var c5 = doc.GetElementById("c5");
            var c10 = doc.GetElementById("c10");
            c5.SetAttribute("class", "card selected");
            c10.SetAttribute("class", "card selected");

            engine.ComputeAllIncremental(doc, null, new[] { c5, c10 });

            long totalVisits = (engine.CacheHits - primingHits) + (engine.CacheMisses - primingMisses);
            Assert.That(totalVisits, Is.LessThanOrEqualTo(18),
                $"two simultaneous flips visited {totalVisits} elements; ceiling 18 (two 6-subtrees + ancestor closure overlap)");
        }
    }
}
