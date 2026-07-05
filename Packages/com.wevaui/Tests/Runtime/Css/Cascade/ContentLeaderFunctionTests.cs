using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Lists L3 §3.2 — `content: leader(...)` function audit.
    //
    // The `leader()` function generates a fill-character run used in tables
    // of contents: e.g. `content: "Chapter 1" leader(".") counter(page)`.
    // The engine does not implement the ToC leader rendering path in v1 —
    // game UI doesn't require it.
    //
    // What these tests verify:
    //   1. The cascade stores `leader(".")` verbatim — it must not DROP the
    //      declaration entirely (forward-compat: authors mixing full specs
    //      should not have their values silently zeroed by the cascade).
    //   2. ResolveContentString returns null for a leader() value (no text
    //      is rendered — the pseudo-element box is suppressed in v1).
    //   3-4. A few flavours of the leader() call (dot, space, custom) all
    //      survive the cascade.
    //
    // v1 state (2026-05-30):
    //   - The cascade stores all non-custom `content` values verbatim (the
    //     property is Add("content", false, "normal") with no grammar
    //     validation). `leader(".")` is kept as-is.
    //   - CascadeEngine.PseudoElements.ResolveContentString does NOT
    //     recognise leader(); it returns null (no generated content).
    //
    // GAME_UI_COVERAGE_PLAN §13 verdict: 🚫 for v1.
    // ToC-leader rendering is out of scope for game UI.
    public class ContentLeaderFunctionTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── 1. Cascade storage (forward-compat) ───────────────────────────

        [Test]
        public void Content_leader_dot_survives_cascade() {
            // The cascade must NOT drop `content: leader(".")` — it stores
            // all content values verbatim. This is a forward-compat pin: the
            // declaration should not collapse to "normal" / initial.
            var cs = Compute("#x { content: leader(\".\"); }");
            string got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "content: leader(\".\") must not collapse to initial value after cascade");
        }

        [Test]
        public void Content_leader_space_survives_cascade() {
            // `leader(" ")` uses a space fill character. Also forward-compat.
            var cs = Compute("#x { content: leader(\" \"); }");
            string got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "content: leader(\" \") must not collapse to initial value after cascade");
        }

        [Test]
        public void Content_leader_dotted_survives_cascade() {
            // dotted / solid are the predefined keyword forms in the spec.
            // The cascade stores them the same way it stores any unknown
            // function — verbatim.
            var cs = Compute("#x { content: leader(dotted); }");
            string got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "content: leader(dotted) must not collapse to initial value after cascade");
        }

        // ── 2. Rendering suppression in v1 ────────────────────────────────

        [Test]
        public void ResolveContentString_returns_null_for_leader_function() {
            // CascadeEngine.PseudoElements.ResolveContentString: leader() is
            // not a quoted string or attr() — the function returns null,
            // which suppresses the pseudo-element box. This is the correct
            // v1 behaviour (no ToC leader rendered).
            string leaderValue = "leader(\".\")";
            string resolved = CascadeEngine.ResolveContentString(leaderValue);
            Assert.That(resolved, Is.Null,
                "v1: ResolveContentString must return null for leader() " +
                "(no content rendered; box suppressed)");
        }

        // NOTE: CSS Lists L3 §3.2 `content: leader(".")` (ToC fill-character
        // runs that stretch to the next inline element) is intentionally out of
        // v1 scope for game UI. The v1 behaviour — leader() suppresses the
        // pseudo (ResolveContentString returns null) — is pinned by the test
        // above. A v2 implementation would live in InlineLayout as a fill-run
        // that expands to consume the available inline space.
    }
}
