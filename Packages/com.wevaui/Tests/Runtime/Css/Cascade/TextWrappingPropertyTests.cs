using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Text Module L3 §3-§5 + CSS Text Module L4 — text wrapping /
    // breaking / hyphenation property cascade.
    //
    // CssProperties registers white-space, word-break, overflow-wrap, and
    // hyphens (CssProperties.cs:74-85, 745-830) as INHERITED with spec-
    // mandated initial values. These four properties form the wrapping/
    // breaking control surface and the engine's line-breaking path
    // depends on each one's cascaded value. They had no direct cascade
    // round-trip / inheritance tests.
    //
    // Spec references:
    //   white-space    — CSS Text 3 §3 (collapsing/wrapping mode)
    //   word-break     — CSS Text 3 §5.1 (break opportunities inside words)
    //   overflow-wrap  — CSS Text 3 §5.2 (emergency break for unbreakable
    //                    content overflowing its line box)
    //   hyphens        — CSS Text 4 §6   (soft-hyphen + auto hyphenation)
    public class TextWrappingPropertyTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── white-space §3 ───────────────────────────────────────────────

        [Test]
        public void White_space_initial_is_normal() {
            // CSS Text 3 §3: initial = `normal`. Collapses whitespace,
            // wraps at break opportunities.
            var cs = Compute("");
            Assert.That(cs.Get("white-space"), Is.EqualTo("normal"));
        }

        [Test]
        public void White_space_pre_round_trips() {
            // §3 — `pre`: preserve all whitespace, no wrap.
            var cs = Compute("#x { white-space: pre; }");
            Assert.That(cs.Get("white-space"), Is.EqualTo("pre"));
        }

        [Test]
        public void White_space_nowrap_round_trips() {
            // §3 — `nowrap`: collapse whitespace, no wrap.
            var cs = Compute("#x { white-space: nowrap; }");
            Assert.That(cs.Get("white-space"), Is.EqualTo("nowrap"));
        }

        [Test]
        public void White_space_pre_wrap_round_trips() {
            // §3 — `pre-wrap`: preserve whitespace, wrap at opportunities.
            var cs = Compute("#x { white-space: pre-wrap; }");
            Assert.That(cs.Get("white-space"), Is.EqualTo("pre-wrap"));
        }

        [Test]
        public void White_space_pre_line_round_trips() {
            // §3 — `pre-line`: collapse spaces / tabs, preserve newlines,
            // wrap at opportunities.
            var cs = Compute("#x { white-space: pre-line; }");
            Assert.That(cs.Get("white-space"), Is.EqualTo("pre-line"));
        }

        [Test]
        public void White_space_break_spaces_round_trips() {
            // §3 — `break-spaces`: preserve and never collapse all
            // whitespace; each preserved space is a soft-wrap opportunity.
            // Newer keyword (CSS Text 3 §3); pinned so a future cascade
            // refactor doesn't silently drop it.
            var cs = Compute("#x { white-space: break-spaces; }");
            Assert.That(cs.Get("white-space"), Is.EqualTo("break-spaces"));
        }

        [Test]
        public void White_space_inherits() {
            // CSS Text 3 §3: white-space is inherited. Setting it on a
            // wrapper applies to inline descendants.
            var cs = ComputeChild("div { white-space: pre; }");
            Assert.That(cs.Get("white-space"), Is.EqualTo("pre"),
                "white-space is inherited; child must see parent's `pre`");
        }

        // ── word-break §5.1 ──────────────────────────────────────────────

        [Test]
        public void Word_break_initial_is_normal() {
            // CSS Text 3 §5.1: initial = `normal`. Wraps only at standard
            // soft-wrap opportunities (mostly spaces).
            var cs = Compute("");
            Assert.That(cs.Get("word-break"), Is.EqualTo("normal"));
        }

        [Test]
        public void Word_break_break_all_round_trips() {
            // §5.1 — `break-all`: lines may break between any two
            // typographic letter units. Used to wrap long monospace
            // identifiers (URLs, hex strings).
            var cs = Compute("#x { word-break: break-all; }");
            Assert.That(cs.Get("word-break"), Is.EqualTo("break-all"));
        }

        [Test]
        public void Word_break_keep_all_round_trips() {
            // §5.1 — `keep-all`: prohibits breaks within CJK words.
            // Equivalent to `normal` for Latin scripts.
            var cs = Compute("#x { word-break: keep-all; }");
            Assert.That(cs.Get("word-break"), Is.EqualTo("keep-all"));
        }

        [Test]
        public void Word_break_inherits() {
            var cs = ComputeChild("div { word-break: break-all; }");
            Assert.That(cs.Get("word-break"), Is.EqualTo("break-all"),
                "word-break is inherited per CSS Text 3 §5.1");
        }

        // ── overflow-wrap §5.2 ──────────────────────────────────────────

        [Test]
        public void Overflow_wrap_initial_is_normal() {
            // CSS Text 3 §5.2: initial = `normal`. Only breaks at standard
            // soft-wrap opportunities, even if content overflows.
            var cs = Compute("");
            Assert.That(cs.Get("overflow-wrap"), Is.EqualTo("normal"));
        }

        [Test]
        public void Overflow_wrap_break_word_round_trips() {
            // §5.2 — `break-word`: emergency break inside otherwise
            // unbreakable strings if no other break opportunity fits.
            var cs = Compute("#x { overflow-wrap: break-word; }");
            Assert.That(cs.Get("overflow-wrap"), Is.EqualTo("break-word"));
        }

        [Test]
        public void Overflow_wrap_anywhere_round_trips() {
            // §5.2 — `anywhere`: like break-word but also affects min-
            // content sizing (the shortest possible inline content is
            // the size of one grapheme cluster). Newer CSS Text 3 value.
            var cs = Compute("#x { overflow-wrap: anywhere; }");
            Assert.That(cs.Get("overflow-wrap"), Is.EqualTo("anywhere"));
        }

        [Test]
        public void Overflow_wrap_inherits() {
            var cs = ComputeChild("div { overflow-wrap: break-word; }");
            Assert.That(cs.Get("overflow-wrap"), Is.EqualTo("break-word"),
                "overflow-wrap is inherited per CSS Text 3 §5.2");
        }

        // ── hyphens §6 ───────────────────────────────────────────────────

        [Test]
        public void Hyphens_initial_is_manual() {
            // CSS Text 4 §6: initial = `manual`. Only the explicit soft
            // hyphen (U+00AD `&shy;`) triggers a break opportunity; the
            // UA does no automatic hyphenation.
            // (Note: WPT and Chrome ship with `manual` as the initial;
            // the engine's audit confirms only `manual` is honoured —
            // auto requires dictionary support which v1 doesn't ship.)
            var cs = Compute("");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("manual"));
        }

        [Test]
        public void Hyphens_none_round_trips() {
            // §6 — `none`: even soft hyphens are ignored for line
            // breaking purposes.
            var cs = Compute("#x { hyphens: none; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"));
        }

        [Test]
        public void Hyphens_auto_round_trips_at_parse_level() {
            // §6 — `auto`: dictionary-driven hyphenation. The audit
            // documents this as parser-supported but not implemented in
            // v1 (no dictionaries shipped). Pin the parse / cascade
            // round-trip so the keyword survives the cascade — runtime
            // honouring is a separate v2 follow-up.
            var cs = Compute("#x { hyphens: auto; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("auto"));
        }

        [Test]
        public void Hyphens_inherits() {
            var cs = ComputeChild("div { hyphens: none; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"),
                "hyphens is inherited per CSS Text 4 §6");
        }

        // ── Cross-property: all four inherit together ────────────────────

        [Test]
        public void All_four_text_wrapping_properties_are_inherited() {
            // Belt-and-braces: a single declaration on the parent that
            // sets all four must propagate to descendants. Catches a
            // regression where one of the longhands accidentally gets
            // marked non-inherited in the registry.
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { white-space: pre-wrap; word-break: break-all; " +
                       "overflow-wrap: anywhere; hyphens: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("white-space"), Is.EqualTo("pre-wrap"));
            Assert.That(cs.Get("word-break"), Is.EqualTo("break-all"));
            Assert.That(cs.Get("overflow-wrap"), Is.EqualTo("anywhere"));
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"));
        }
    }
}
