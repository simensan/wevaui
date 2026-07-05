using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Synthetic real-game-like fixture: a section containing N "cards",
    // each with a few descendants. Clicking one card flips :active on
    // the click chain (card → section → body → html). We measure how
    // many elements the incremental cascade re-cascades on the next
    // pass — pre-fix this was every-card-in-the-section because the
    // section's Version bumped, post-fix it should be card-subtree only.
    //
    // These tests are PINS for the perf work, not micro-benchmarks. They
    // assert ceilings on the number of cache misses an incremental pass
    // should produce for a state flip, so a future regression in the
    // gate / per-element-digest path can't silently re-introduce the
    // Walk fanout. If you tighten the implementation further, lower the
    // ceilings.
    public class ActiveChainInvalidationBenchmarkTests {
        const int CardCount = 40;
        const int DescendantsPerCard = 4;

        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Document BuildSectionWithCards() {
            // Cards have a couple of nested elements each — mirrors a
            // typical challenge / skill / inventory card.
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
            // Realistic real-game-like rules: card has :hover and :active
            // styles. Section / body / html have NO state-driven rules.
            return Author(
                ".card { background: #222; padding: 8px; }" +
                ".card:hover { background: #333; }" +
                ".card:active { transform: scale(0.98); }" +
                ".card .name { font-size: 14px; }" +
                ".card .badge { color: #aaa; }" +
                ".card .icon { width: 32px; height: 32px; }"
            );
        }

        // Minimal state provider that lets the test stamp arbitrary state
        // bits on elements without going through the full
        // InteractionStateProvider (which would also do focus / tab
        // management we don't care about here).
        sealed class FakeStateProvider : IElementStateProvider {
            readonly Dictionary<Element, ElementState> states = new();
            long version;
            public long Version => version;
            public ElementState GetState(Element e) {
                if (e == null) return ElementState.None;
                return states.TryGetValue(e, out var v) ? v : ElementState.None;
            }
            public void SetFlag(Element e, ElementState bit, bool on) {
                var cur = states.TryGetValue(e, out var v) ? v : ElementState.None;
                var next = on ? (cur | bit) : (cur & ~bit);
                if (next == cur) return;
                if (next == ElementState.None) states.Remove(e);
                else states[e] = next;
                version++;
            }
        }

        [Test]
        public void Active_flip_on_card_doesnt_recascade_other_cards() {
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();
            // Prime.
            engine.ComputeAll(doc, state);
            long primingMisses = engine.CacheMisses;
            long primingHits = engine.CacheHits;

            // Simulate the :active chain for clicking card c5: per CSS L4
            // §11.4.1 every ancestor up to the document root gets :active.
            // Mirror that — but only the card and its ancestors with a
            // matching :active subject selector should produce digest
            // changes (which is "just .card c5" given the stylesheet).
            var c5 = doc.GetElementById("c5");
            var section = doc.GetElementById("sec");
            // Walk up the parent chain stamping Active.
            var chain = new List<Element>();
            for (Element n = c5; n != null; n = n.Parent as Element) chain.Add(n);
            foreach (var e in chain) state.SetFlag(e, ElementState.Active, true);

            // Hint only the click target — the cascade should observe via
            // its per-element digest that section / body / html aren't
            // really affected.
            engine.ComputeAllIncremental(doc, state, new[] { c5 });

            long passMisses = engine.CacheMisses - primingMisses;
            long passHits = engine.CacheHits - primingHits;

            // The card's own subtree re-cascades: c5 + 3 direct children
            // (icon, body, footer) + 2 inner spans = 6 elements. Plus
            // c5's ancestor closure visited via WalkIncremental: section,
            // body, html — 3 cache hits.
            //
            // Other 39 cards' subtrees should be COMPLETELY untouched —
            // not visited, not cache-checked. Their elements: 39 * 6 = 234
            // elements. Pre-fix the section's Version bump pulled all of
            // them into a Walk fallback (~239 misses).
            //
            // We assert a generous ceiling: ≤ 20 cache lookups total on
            // the dirty pass. Anything close to 234 means the per-element
            // digest refinement regressed.
            long totalVisits = passHits + passMisses;
            // Measured: 9 visits (3 ancestor closure cache-HITS for
            // section / body / html + 6 card-subtree cache-MISSES for
            // c5 + its 5 descendants). 39 sibling cards × 6 elements each
            // = 234 elements left untouched. Pre-fix this number was
            // ~240+ because section's Version bumped on its irrelevant
            // :active digest shift. Allow tiny slack; tighten if it stays
            // smaller across future changes.
            Assert.That(totalVisits, Is.LessThanOrEqualTo(10),
                $"incremental pass visited {totalVisits} elements; baseline is 9. " +
                "If this assertion fires after a perf change, the per-element state digest probably regressed.");
            Assert.That(passMisses, Is.LessThanOrEqualTo(6),
                $"only the dirty card's 6-element subtree should re-cascade; got {passMisses} misses");

            // Verify the work that DID happen actually produced the new
            // :active style — guards against "optimized to skip the card
            // too" silently breaking the feature.
            var c5Style = engine.ResultMap[c5];
            Assert.That(c5Style.Get(CssProperties.TransformId), Does.Contain("scale"),
                "c5 must have re-cascaded with .card:active rule applied");

            // Sibling card's style must still reflect the default (no
            // :active state) — guards against "siblings got the active
            // style smeared in".
            var c10 = doc.GetElementById("c10");
            var c10Style = engine.ResultMap[c10];
            Assert.That(c10Style.Get(CssProperties.TransformId), Is.Not.EqualTo(c5Style.Get(CssProperties.TransformId)),
                "c10 must not have inherited c5's :active transform");
        }

        [Test]
        public void Hover_flip_on_card_doesnt_recascade_other_cards() {
            // Same shape as Active test but for Hover. Hover changes more
            // often (every pointer move can shift the hovered element)
            // so the regression cost would be larger if this ever broke.
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();
            engine.ComputeAll(doc, state);
            long primingMisses = engine.CacheMisses;
            long primingHits = engine.CacheHits;

            var c5 = doc.GetElementById("c5");
            var chain = new List<Element>();
            for (Element n = c5; n != null; n = n.Parent as Element) chain.Add(n);
            foreach (var e in chain) state.SetFlag(e, ElementState.Hover, true);

            engine.ComputeAllIncremental(doc, state, new[] { c5 });

            long totalVisits = (engine.CacheHits - primingHits) + (engine.CacheMisses - primingMisses);
            Assert.That(totalVisits, Is.LessThanOrEqualTo(10),
                $"hover flip visited {totalVisits} elements; baseline is 9");
        }

        [Test]
        public void Active_then_hover_then_release_stays_proportional_to_dirty_subtree() {
            // Realistic press / release cycle. Three passes, none should
            // touch siblings.
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();
            engine.ComputeAll(doc, state);

            var c5 = doc.GetElementById("c5");
            var chain = new List<Element>();
            for (Element n = c5; n != null; n = n.Parent as Element) chain.Add(n);

            // Pass 1: hover.
            foreach (var e in chain) state.SetFlag(e, ElementState.Hover, true);
            long beforePass1 = engine.CacheHits + engine.CacheMisses;
            engine.ComputeAllIncremental(doc, state, new[] { c5 });
            long pass1Visits = (engine.CacheHits + engine.CacheMisses) - beforePass1;

            // Pass 2: active (still hovering).
            foreach (var e in chain) state.SetFlag(e, ElementState.Active, true);
            long beforePass2 = engine.CacheHits + engine.CacheMisses;
            engine.ComputeAllIncremental(doc, state, new[] { c5 });
            long pass2Visits = (engine.CacheHits + engine.CacheMisses) - beforePass2;

            // Pass 3: release active + hover.
            foreach (var e in chain) {
                state.SetFlag(e, ElementState.Active, false);
                state.SetFlag(e, ElementState.Hover, false);
            }
            long beforePass3 = engine.CacheHits + engine.CacheMisses;
            engine.ComputeAllIncremental(doc, state, new[] { c5 });
            long pass3Visits = (engine.CacheHits + engine.CacheMisses) - beforePass3;

            // Each pass should be tightly bounded.
            Assert.That(pass1Visits, Is.LessThanOrEqualTo(10), $"pass 1 (hover) visited {pass1Visits}");
            Assert.That(pass2Visits, Is.LessThanOrEqualTo(10), $"pass 2 (active) visited {pass2Visits}");
            Assert.That(pass3Visits, Is.LessThanOrEqualTo(10), $"pass 3 (release) visited {pass3Visits}");
        }

        [Test]
        public void Baseline_cold_pass_cascades_every_element() {
            // Sanity check — without my fixes this would silently rely on
            // a cold pass being roughly N * (descendants + chrome).
            // Failing this means a future change broke initial-pass
            // behavior, not the incremental optimization.
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();

            long beforeMisses = engine.CacheMisses;
            engine.ComputeAll(doc, state);
            long passMisses = engine.CacheMisses - beforeMisses;

            // Each card has 1 (card) + 3 (icon, body, footer) + 2 (name,
            // badge inner spans) = 6 elements; + section + html + body
            // wrappers. 40 cards * 6 + ~3 chrome = ~243. Sanity-bound
            // both ways.
            Assert.That(passMisses, Is.GreaterThan(200), $"cold pass should re-cascade all ~243 elements, got {passMisses}");
            Assert.That(passMisses, Is.LessThan(400), $"cold pass shouldn't double-cascade, got {passMisses}");
        }
    }
}
