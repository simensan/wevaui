using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Generated Content Level 3 §3 — `quotes` property + open-quote /
    // close-quote / no-open-quote / no-close-quote content keywords.
    //
    // REGISTRATION STATE (as of this file):
    //   `quotes` is NOT registered in CssProperties.cs.
    //   CssProperties.GetId("quotes") returns -1, so ComputedStyle.Get("quotes")
    //   returns null when authored.
    //
    //   This is a known gap. Tests requiring a registered property are marked
    //   [Ignore("found regression — quotes not registered in CssProperties")] so
    //   they appear as skipped rather than failing.
    //
    //   Tests that ARE runnable:
    //     - Pin GetId returns -1 (guards against accidental silent registration).
    //     - `content: open-quote` / `close-quote` cascade round-trip — `content`
    //       IS registered (initial "normal", non-inherited). The cascade must
    //       carry the raw keyword token for the rendering layer to interpret.
    //     - `content: no-open-quote` / `no-close-quote` round-trip.
    //     - `content: normal` / `content: none` initial/explicit behaviors.
    //
    // Spec references:
    //   CSS Generated Content L3 §3: `quotes` property syntax and inheritance.
    //   CSS Generated Content L3 §3.1: `open-quote` / `close-quote` /
    //     `no-open-quote` / `no-close-quote` as `content` values.
    //   CSS2 §12.4: generated content overview.
    public class QuotesAndQuoteContentTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<q id=\"x\">text</q>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<blockquote id=\"parent\"><q id=\"child\">text</q></blockquote>");
            var engine = new CascadeEngine(new[] { Author(css) });
            engine.Compute(doc.GetElementById("parent"));
            return engine.Compute(doc.GetElementById("child"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Registration state — assert quotes is registered with the spec-
        // correct flags so any future drift is caught immediately.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Quotes_is_registered_inherited_initial_auto() {
            int id = CssProperties.GetId("quotes");
            Assert.That(id, Is.GreaterThanOrEqualTo(0),
                "quotes must be registered per CSS Generated Content L3 §3");
            var prop = CssProperties.Get(id);
            Assert.That(prop.IsInherited, Is.True,
                "quotes is inherited per CSS Generated Content L3 §3");
            Assert.That(prop.InitialValue, Is.EqualTo("auto"),
                "quotes initial value is 'auto' per CSS Generated Content L3 §3");
        }

        // ══════════════════════════════════════════════════════════════════
        // `content` property — carries open-quote / close-quote keywords
        // verbatim through the cascade (content IS registered, non-inherited,
        // initial "normal").
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Content_open_quote_keyword_round_trips_through_cascade() {
            // CSS Generated Content L3 §3.1: `content: open-quote` tells the
            // rendering layer to insert the appropriate opening quotation mark
            // (determined by the `quotes` property at render time). The cascade
            // must carry the raw "open-quote" token faithfully.
            var cs = Compute("#x { content: open-quote; }");
            Assert.That(cs.Get("content"), Is.EqualTo("open-quote"));
        }

        [Test]
        public void Content_close_quote_keyword_round_trips_through_cascade() {
            // CSS Generated Content L3 §3.1: `content: close-quote` for the
            // matching closing quotation mark.
            var cs = Compute("#x { content: close-quote; }");
            Assert.That(cs.Get("content"), Is.EqualTo("close-quote"));
        }

        [Test]
        public void Content_no_open_quote_keyword_round_trips_through_cascade() {
            // CSS Generated Content L3 §3.1: `content: no-open-quote` increments
            // the nesting depth without emitting a quote character. Used for
            // `<q>` elements styled to show no actual quote marks but still
            // maintain the depth counter.
            var cs = Compute("#x { content: no-open-quote; }");
            Assert.That(cs.Get("content"), Is.EqualTo("no-open-quote"));
        }

        [Test]
        public void Content_no_close_quote_keyword_round_trips_through_cascade() {
            // CSS Generated Content L3 §3.1: `content: no-close-quote` decrements
            // depth without emitting a character.
            var cs = Compute("#x { content: no-close-quote; }");
            Assert.That(cs.Get("content"), Is.EqualTo("no-close-quote"));
        }

        [Test]
        public void Content_normal_is_initial_and_round_trips() {
            // CSS Generated Content L3 §2: initial value `normal`, which computes
            // to `none` for ::before / ::after. Explicit authoring of `normal`
            // must also survive the cascade.
            var cs = Compute("#x { content: normal; }");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"));
        }

        [Test]
        public void Content_none_keyword_round_trips() {
            // `content: none` explicitly suppresses pseudo-element generation
            // even when a previous rule established a content value.
            var cs = Compute("#x { content: none; }");
            Assert.That(cs.Get("content"), Is.EqualTo("none"));
        }

        [Test]
        public void Content_is_not_inherited() {
            // CSS Generated Content L3: `content` is NOT inherited.
            // A child without its own rule sees the initial value `normal`,
            // not the parent's `open-quote`.
            var cs = ComputeChild("#parent { content: open-quote; }");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"),
                "content is non-inherited; child must see initial 'normal', not parent's open-quote");
        }

        [Test]
        public void Content_quote_keyword_overrides_none_higher_specificity() {
            // Specificity: #x wins over q; quote keyword overrides none.
            var doc = Html("<q id=\"x\">text</q>");
            var engine = new CascadeEngine(new[] {
                Author("q { content: none; } #x { content: open-quote; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("content"), Is.EqualTo("open-quote"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Spec-required behaviour once `quotes` is registered.
        // Marked [Ignore] so the test suite shows them as skipped.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Quotes_initial_is_auto() {
            // CSS Generated Content L3 §3: initial value is `auto`, which means
            // the UA provides language-appropriate defaults.
            var cs = Compute("");
            Assert.That(cs.Get("quotes"), Is.EqualTo("auto"));
        }

        [Test]
        public void Quotes_none_round_trips() {
            // `quotes: none` disables all generated quote marks; the open-quote /
            // close-quote content values produce empty strings.
            var cs = Compute("#x { quotes: none; }");
            Assert.That(cs.Get("quotes"), Is.EqualTo("none"));
        }

        [Test]
        public void Quotes_auto_round_trips() {
            // `quotes: auto` restores UA-defined quotation mark selection.
            var cs = Compute("#x { quotes: auto; }");
            Assert.That(cs.Get("quotes"), Is.EqualTo("auto"));
        }

        [Test]
        public void Quotes_single_level_pair_round_trips() {
            // CSS Generated Content L3 §3: one string-pair sets the outer quote.
            // Canonical form: `quotes: '"' '"'` (open close).
            var cs = Compute("#x { quotes: '\"' '\"'; }");
            Assert.That(cs.Get("quotes"), Is.Not.Null,
                "quotes with one pair must not be null");
        }

        [Test]
        public void Quotes_two_level_pairs_round_trips() {
            // Two pairs: outer level + nested level.
            // `quotes: '"' '"' "'" "'"` — outer double, inner single.
            var cs = Compute("#x { quotes: '\"' '\"' \"'\" \"'\"; }");
            Assert.That(cs.Get("quotes"), Is.Not.Null,
                "quotes with two pairs must survive the cascade");
        }

        [Test]
        public void Quotes_is_inherited() {
            // CSS Generated Content L3 §3: `quotes` IS inherited.
            // A nested <q> must see the outer element's quotes value.
            var cs = ComputeChild("#parent { quotes: '\"' '\"' \"'\" \"'\"; }");
            Assert.That(cs.Get("quotes"), Is.Not.Null,
                "quotes is inherited; nested element must see parent's value");
        }

        [Test]
        public void Quotes_none_overrides_inherited_value_on_child() {
            // Explicit `quotes: none` on a descendant suppresses its own marks
            // even when the parent has a value.
            var cs = ComputeChild("#parent { quotes: '\"' '\"'; } #child { quotes: none; }");
            Assert.That(cs.Get("quotes"), Is.EqualTo("none"));
        }
    }
}
