using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CascadeEngine.SubjectMatchAffectedByStateBit is the gate
    // InteractionStateProvider uses to decide whether a pseudo-state flip
    // on an element actually warrants marking that element dirty for
    // re-cascade. These tests pin the four observable contracts:
    //
    //   1. Stylesheet has no selectors targeting `bit` in any subject
    //      compound → false for every element.
    //   2. Stylesheet has subject selectors for `bit` but the element
    //      doesn't carry the matching features → false.
    //   3. Stylesheet has matching subject selectors AND the element
    //      carries the features → true.
    //   4. Bit appears ONLY in non-subject (descendant) compounds → false
    //      for the element whose state flipped (we'd need to mark its
    //      descendants instead — handled by a separate plumbing step).
    //
    // The contract matters for a real game's :active chain: clicking a card
    // walks card → ul → section → body → html, and pre-fix every chain
    // ancestor got a PseudoClassState mark even though no rule targets
    // body / html / section on :active. Each mark turned into a Walk
    // fallback in the incremental cascade.
    public class SubjectMatchAffectedByStateBitTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static (CascadeEngine engine, Document doc) Setup(string css, string html) {
            var doc = HtmlParser.Parse(html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css) });
            // Prime so the feature set is built.
            engine.ComputeAll(doc);
            return (engine, doc);
        }

        [Test]
        public void Returns_false_when_no_subject_selector_tests_the_bit() {
            // Stylesheet has no :active rule anywhere. Clicking should not
            // mark anything dirty for :active.
            var (engine, doc) = Setup(
                ".card { color: red; }",
                "<div class=\"card\" id=\"a\">card</div>");
            var a = doc.GetElementById("a");
            Assert.That(engine.SubjectMatchAffectedByStateBit(a, ElementState.Active), Is.False);
        }

        [Test]
        public void Returns_false_when_element_lacks_matching_subject_features() {
            // .card:active exists but the body element doesn't carry .card,
            // so flipping :active on body can't affect any subject rule.
            // This is the chain-ancestor case for a real game — body/html
            // are in the :active chain but no rule targets them.
            var (engine, doc) = Setup(
                ".card:active { transform: scale(0.98); }",
                "<body><div class=\"card\" id=\"c\">card</div></body>");
            var c = doc.GetElementById("c");
            Assert.That(engine.SubjectMatchAffectedByStateBit(c, ElementState.Active), Is.True,
                "the .card element itself should still need re-cascade on :active");
            // body lacks .card → no rule could flip on its :active.
            var body = c.Parent as Element;
            Assert.That(body, Is.Not.Null);
            Assert.That(engine.SubjectMatchAffectedByStateBit(body, ElementState.Active), Is.False,
                "body without .card class should be filtered out — no :active rule could match it");
        }

        [Test]
        public void Returns_true_when_subject_features_match_and_bit_is_tested() {
            // Direct match: rule and element share the class.
            var (engine, doc) = Setup(
                ".btn:hover { background: blue; }",
                "<button class=\"btn\" id=\"b\">click</button>");
            var b = doc.GetElementById("b");
            Assert.That(engine.SubjectMatchAffectedByStateBit(b, ElementState.Hover), Is.True);
        }

        [Test]
        public void Returns_false_for_descendant_only_state_pseudo() {
            // ".parent:hover .child" puts :hover in the ANCESTOR compound.
            // When :hover flips on a .parent element, the parent's own
            // subject match against `.child` doesn't change. The right
            // thing to do is mark descendants — that's a separate path
            // not handled by this gate yet. The gate must return false
            // so the test pins the "subject-only" scope explicitly.
            var (engine, doc) = Setup(
                ".parent:hover .child { color: red; }",
                "<div class=\"parent\" id=\"p\"><div class=\"child\" id=\"c\">x</div></div>");
            var p = doc.GetElementById("p");
            // .parent has :hover in descendant bucket only; subject bucket
            // for Hover is empty → SubjectMatchAffectedByStateBit returns
            // false. The test documents the limitation.
            Assert.That(engine.SubjectMatchAffectedByStateBit(p, ElementState.Hover), Is.False);
        }

        [Test]
        public void Mixed_subject_and_descendant_rules_use_subject_path() {
            // Both ".card:active { ... }" (subject) and ".outer:active .x { ... }"
            // (descendant) exist. For element with .card, the subject rule
            // matches → return true. For element with .outer (no .card),
            // only descendant rule references :active for it → return false
            // (descendant path is separate).
            var (engine, doc) = Setup(
                ".card:active { transform: scale(0.98); }" +
                ".outer:active .x { color: red; }",
                "<div class=\"card\" id=\"c\"></div><div class=\"outer\" id=\"o\"><div class=\"x\" id=\"x\"></div></div>");
            var card = doc.GetElementById("c");
            var outer = doc.GetElementById("o");
            Assert.That(engine.SubjectMatchAffectedByStateBit(card, ElementState.Active), Is.True);
            Assert.That(engine.SubjectMatchAffectedByStateBit(outer, ElementState.Active), Is.False,
                ".outer's :active match would affect .x descendants only — gate is subject-only");
        }

        [Test]
        public void Empty_stylesheet_returns_true() {
            // No rules → no info. Returning true preserves the legacy
            // "always mark" path so unattached test scenarios still
            // dirty the tracker.
            var doc = HtmlParser.Parse("<div id=\"d\"></div>");
            var engine = new CascadeEngine(new List<OriginatedStylesheet>());
            engine.ComputeAll(doc);
            var d = doc.GetElementById("d");
            Assert.That(engine.SubjectMatchAffectedByStateBit(d, ElementState.Active), Is.True);
        }

        [Test]
        public void Null_element_returns_false() {
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(".x:hover{}") });
            Assert.That(engine.SubjectMatchAffectedByStateBit(null, ElementState.Hover), Is.False);
        }

        [Test]
        public void Class_matching_handles_multi_class_attribute() {
            // Element has multiple classes; rule matches via one of them.
            // The whitespace-tokenization in the gate's class lookup
            // shouldn't false-negative when the target class isn't the
            // first token.
            var (engine, doc) = Setup(
                ".card:active { transform: scale(0.98); }",
                "<div class=\"item highlighted card selected\" id=\"x\"></div>");
            var x = doc.GetElementById("x");
            Assert.That(engine.SubjectMatchAffectedByStateBit(x, ElementState.Active), Is.True);
        }

        [Test]
        public void Chain_ancestor_with_no_matching_rule_cache_hits_after_state_flip() {
            // End-to-end pin: when :active flips on a card whose ancestor
            // chain includes body / html / a section, the section's own
            // ResolveStateDigest must NOT shift (no `.section:active`
            // rule exists). If it shifted, the cache miss in
            // WalkIncremental would bump Version and fan out to a full
            // Walk over every other card in the section.
            //
            // We can't reach into ResolveStateDigest directly (private),
            // so we observe the symptom: after a state flip, the
            // ancestor's cache entry stays a HIT.
            var doc = HtmlParser.Parse(
                "<section id=\"s\">" +
                "  <div class=\"card\" id=\"c1\">first</div>" +
                "  <div class=\"card\" id=\"c2\">second</div>" +
                "</section>");
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                Author(".card:active { transform: scale(0.98); }")
            });
            var stateProvider = new FakeFlagStateProvider();
            engine.ComputeAll(doc, stateProvider);
            long primingHits = engine.CacheHits;
            long primingMisses = engine.CacheMisses;

            // Flip :active on c1. State chain semantically includes c1 +
            // section + (html / body — HtmlParser injects them). The
            // section has no :active rule targeting it.
            var c1 = doc.GetElementById("c1");
            var section = doc.GetElementById("s");
            stateProvider.SetFlag(c1, ElementState.Active, true);
            stateProvider.SetFlag(section, ElementState.Active, true);

            // Run incremental cascade — both c1 and section as dirty hints.
            engine.ComputeAllIncremental(doc, stateProvider, new[] { c1, section });

            long passHits = engine.CacheHits - primingHits;
            long passMisses = engine.CacheMisses - primingMisses;

            // Section must HIT — no `.section:active` rule, no
            // `<section>:active` rule, per-element mask filters Active
            // out of section's digest → digest unchanged → cache hit.
            //
            // c1 must MISS — `.card:active` is a real subject dep, its
            // digest shifts, re-cascade produces the scale transform.
            //
            // c2 must HIT — not in the dirty closure at all; pre-fix this
            // was a leak when section's Version bumped, post-fix it's
            // truly clean.
            Assert.That(passMisses, Is.LessThanOrEqualTo(2),
                "section's per-element mask should filter out Active; only c1 (and maybe its descendants) should miss");
            Assert.That(passHits, Is.GreaterThanOrEqualTo(1),
                "at least one of {section, c2} must hit — neither has a matching :active rule");
        }

        sealed class FakeFlagStateProvider : IElementStateProvider {
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
        public void Class_matching_doesnt_substring_match() {
            // ".card" must NOT match an element with class "supercard" or
            // "carded". Regression guard against a substring-based class
            // lookup that would over-include.
            var (engine, doc) = Setup(
                ".card:active { transform: scale(0.98); }",
                "<div class=\"supercard carded\" id=\"x\"></div>");
            var x = doc.GetElementById("x");
            Assert.That(engine.SubjectMatchAffectedByStateBit(x, ElementState.Active), Is.False);
        }

        // ────────────────────────────────────────────────────────────────
        // Audit CX1: StateBitAffectsElement — the gate the state provider
        // ACTUALLY calls. The subject-only method above returns false for
        // descendant/sibling-position state (its documented contract); the
        // provider gate must NOT, or the flip produces zero marks, no
        // cascade pass runs, and `.parent:hover .child` styles are
        // permanently dead in production. These pins run the REAL wiring:
        // InteractionStateProvider + InvalidationTracker + AttachCascade.
        // ────────────────────────────────────────────────────────────────

        static (CascadeEngine engine, Document doc,
                Weva.Events.InteractionStateProvider provider,
                Weva.Reactive.InvalidationTracker tracker) SetupWired(string css, string html) {
            var doc = HtmlParser.Parse(html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css) });
            var provider = new Weva.Events.InteractionStateProvider();
            var tracker = new Weva.Reactive.InvalidationTracker();
            tracker.Attach(doc);
            provider.Tracker = tracker;
            provider.AttachCascade(engine);
            engine.ComputeAll(doc, provider); // prime caches + feature set
            return (engine, doc, provider, tracker);
        }

        [Test]
        public void Descendant_position_hover_flip_marks_and_restyles_CX1() {
            var (engine, doc, provider, tracker) = SetupWired(
                ".parent:hover .child { color: red; }",
                "<div class=\"parent\" id=\"p\"><div class=\"child\" id=\"c\">t</div></div>");
            var p = doc.GetElementById("p");
            var c = doc.GetElementById("c");
            Assert.That(engine.Compute(c, provider).Get("color"), Is.EqualTo("black"), "sanity: not hovered yet");

            provider.SetFlag(p, ElementState.Hover, true);
            Assert.That(tracker.DirtyCount, Is.GreaterThan(0),
                "hovering .parent must mark — pre-CX1 the subject-only gate dropped the flip " +
                "entirely and `.parent:hover .child` never applied in production");

            // End-to-end: the re-cascade a lifecycle pass would run must see red.
            engine.ComputeAll(doc, provider);
            Assert.That(engine.Compute(c, provider).Get("color"), Is.EqualTo("red"),
                ".child must restyle after the marked flip (digest fold via DescendantStateMask)");

            provider.SetFlag(p, ElementState.Hover, false);
            engine.ComputeAll(doc, provider);
            Assert.That(engine.Compute(c, provider).Get("color"), Is.EqualTo("black"), "un-hover restores");
        }

        [Test]
        public void Sibling_position_hover_flip_marks_and_restyles_CX1() {
            var (engine, doc, provider, tracker) = SetupWired(
                ".a:hover + .b { color: red; }",
                "<div><div class=\"a\" id=\"a\">a</div><div class=\"b\" id=\"b\">b</div></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            Assert.That(engine.Compute(b, provider).Get("color"), Is.EqualTo("black"), "sanity");

            provider.SetFlag(a, ElementState.Hover, true);
            Assert.That(tracker.DirtyCount, Is.GreaterThan(0),
                "hovering .a must mark — the sibling-position bit was dropped pre-CX1");

            engine.ComputeAll(doc, provider);
            Assert.That(engine.Compute(b, provider).Get("color"), Is.EqualTo("red"),
                ".b must restyle (RequiresGlobalFallback full walk re-resolves the sibling)");
        }

        [Test]
        public void Pseudo_element_only_hover_flip_still_marks_CX5() {
            // `:hover` appears ONLY inside a ::after rule — pseudo-element
            // rules are routed to side buckets and never enter
            // compiledSelectors, so pre-CX5 the feature set couldn't see the
            // hover dependency at all: zero marks, tooltip never appears.
            // The sheet must also contain a NORMAL rule: with zero compiled
            // selectors the gate's "no info" safe-default marks everything
            // and would mask the bug (any real page has normal rules).
            var (engine, doc, provider, tracker) = SetupWired(
                ".other { color: blue; } .btn:hover::after { content: \"tip\"; }",
                "<div class=\"btn\" id=\"b\">go</div><div class=\"other\" id=\"o\">x</div>");
            var b = doc.GetElementById("b");
            provider.SetFlag(b, ElementState.Hover, true);
            Assert.That(tracker.DirtyCount, Is.GreaterThan(0),
                "hover on the ::after host must mark (audit CX5) — the classic CSS tooltip");
        }

        [Test]
        public void Gate_stays_tight_for_unrelated_elements_CX1() {
            // The gate's original purpose: hover-chain flips on elements no
            // rule's state compound could match must stay unmarked. `.other`
            // has neither class `parent` (descendant rule) nor `btn`
            // (pseudo-element rule) — flipping it must produce zero marks.
            var (engine, doc, provider, tracker) = SetupWired(
                ".parent:hover .child { color: red; } .btn:hover::after { content: \"t\"; }",
                "<div class=\"parent\"><div class=\"child\">t</div></div><div class=\"other\" id=\"o\">x</div>");
            var o = doc.GetElementById("o");
            provider.SetFlag(o, ElementState.Hover, true);
            Assert.That(tracker.DirtyCount, Is.EqualTo(0),
                "no selector's :hover compound can match .other — the tight gate must skip the mark " +
                "(hover chains on body/html must not fan whole-document re-cascades)");
        }

        [Test]
        public void StateBitAffectsElement_subject_position_still_true() {
            // The new gate is a superset of the subject-only check.
            var (engine, doc) = Setup(
                ".card:active { transform: scale(0.98); }",
                "<div class=\"card\" id=\"x\"></div>");
            var x = doc.GetElementById("x");
            Assert.That(engine.StateBitAffectsElement(x, ElementState.Active), Is.True);
            Assert.That(engine.StateBitAffectsElement(x, ElementState.Hover), Is.False,
                "no :hover anywhere in the sheet");
        }

        [Test]
        public void StateBitAffectsElement_left_compound_concrete_mismatch_is_false() {
            // `.parent:hover .child`: the state-carrying compound requires
            // class `parent`. An element with class `child` (or anything
            // else) can't be the flipping target of that compound.
            var (engine, doc) = Setup(
                ".parent:hover .child { color: red; }",
                "<div class=\"parent\" id=\"p\"><div class=\"child\" id=\"c\">t</div></div>");
            Assert.That(engine.StateBitAffectsElement(doc.GetElementById("c"), ElementState.Hover), Is.False,
                ".child is the SUBJECT, not the state carrier — its own hover flips nothing");
            Assert.That(engine.StateBitAffectsElement(doc.GetElementById("p"), ElementState.Hover), Is.True);
        }
    }
}
