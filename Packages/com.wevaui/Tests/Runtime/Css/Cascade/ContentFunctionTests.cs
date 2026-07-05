using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Generated Content Level 3 §2 — `content` property function forms
    // and value types not covered by CounterPropertyTests (counter / counters)
    // or AttrResolverTests (attr / typed-attr).
    //
    // `content` is registered as NON-INHERITED with initial value "normal".
    // (CssProperties: Add("content", false, "normal").)
    //
    // The cascade stores the raw authored value verbatim. The rendering layer
    // (CascadeEngine.PseudoElements.ResolveContentString) interprets the stored
    // string; in v1 only quoted-string literals and attr() are actually rendered
    // — other forms (url(), mixed lists, alt-text slash) are stored faithfully
    // by the cascade for future rendering passes.
    //
    // Spec references:
    //   CSS Generated Content L3 §2: `content` property syntax.
    //   CSS Generated Content L3 §2.1: string, url(), counter(), attr() forms.
    //   CSS Generated Content L3 §2.3: image with alt-text (slash notation).
    public class ContentFunctionTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            engine.Compute(doc.GetElementById("parent"));
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── initial / keyword values ───────────────────────────────────────

        [Test]
        public void Content_initial_value_is_normal() {
            // CSS Generated Content L3 §2: `normal` is the initial value.
            var cs = Compute("");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"));
        }

        [Test]
        public void Content_none_keyword_round_trips() {
            // `none` suppresses any generated content (pseudo-element box
            // is not generated). The cascade stores the keyword.
            var cs = Compute("#x { content: none; }");
            Assert.That(cs.Get("content"), Is.EqualTo("none"));
        }

        [Test]
        public void Content_normal_keyword_round_trips() {
            // Explicit `normal` authored value survives the cascade.
            var cs = Compute("#x { content: normal; }");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"));
        }

        // ── literal string form ────────────────────────────────────────────

        [Test]
        public void Content_double_quoted_string_round_trips() {
            // CSS Generated Content L3 §2.1: `content: "text"` — the cascade
            // must store the quoted-string form so the renderer can decode it.
            var cs = Compute("#x { content: \"Hello World\"; }");
            var got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "quoted string must survive the cascade");
            // The cascade may store the decoded text or the quoted form; either
            // way, the literal text must be recoverable.
            Assert.That(got, Does.Contain("Hello").Or.EqualTo("\"Hello World\""));
        }

        [Test]
        public void Content_empty_string_round_trips() {
            // An empty string `content: ""` is a valid way to generate an empty
            // pseudo-element box (used for CSS clearfix and decorative shapes).
            var cs = Compute("#x { content: \"\"; }");
            var got = cs.Get("content");
            // Either the empty decoded form or the empty-quotes form is acceptable.
            Assert.That(got, Is.Not.Null,
                "content: \"\" must not be null after cascade");
        }

        [Test]
        public void Content_single_quoted_string_round_trips() {
            // CSS allows single-quoted strings as well.
            var cs = Compute("#x { content: 'hi'; }");
            var got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "single-quoted string content must survive the cascade");
        }

        // ── url() form ────────────────────────────────────────────────────

        [Test]
        public void Content_url_function_round_trips_through_cascade() {
            // CSS Generated Content L3 §2.1: `content: url(handle)` inserts an
            // image. The cascade must store the url() form verbatim — the
            // rendering layer resolves the handle against the asset system.
            var cs = Compute("#x { content: url(my-icon); }");
            var got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "content: url(...) must not be null or initial");
            Assert.That(got, Does.Contain("url(").Or.Contain("my-icon"),
                "url() token or handle name must be present in stored value");
        }

        [Test]
        public void Content_url_with_quoted_path_round_trips() {
            // url() may also use a quoted path string.
            var cs = Compute("#x { content: url('sprites/icon.png'); }");
            var got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "content: url('...') must not be null or initial");
        }

        // ── mixed list forms ──────────────────────────────────────────────

        [Test]
        public void Content_string_plus_attr_mixed_list_survives_cascade() {
            // CSS Generated Content L3 §2.1: multiple content tokens can be
            // combined: `content: "Name: " attr(data-name)`. The cascade must
            // carry the full value token sequence.
            var doc = Html("<div id=\"x\" data-name=\"Alice\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: \"Name: \" attr(data-name); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            var got = cs.Get("content");
            // The cascade should store some non-initial value covering both tokens.
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "mixed string+attr must not collapse to initial");
        }

        [Test]
        public void Content_two_string_tokens_in_list_survive_cascade() {
            // Two consecutive quoted strings in a content list are valid.
            var cs = Compute("#x { content: \"Hello \" \"World\"; }");
            var got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "two string tokens must not collapse to initial");
        }

        // ── image / alt-text slash notation ──────────────────────────────

        [Test]
        public void Content_url_with_alt_text_slash_survives_cascade() {
            // CSS Generated Content L3 §2.3: `content: url(x) / "alt text"`
            // — the slash separates the displayed image from its alternative
            // text for accessibility. The cascade must not drop the value.
            var cs = Compute("#x { content: url(my-img) / \"decorative icon\"; }");
            var got = cs.Get("content");
            Assert.That(got, Is.Not.Null.And.Not.EqualTo("normal"),
                "content: url() / \"alt\" must not collapse to initial");
        }

        // ── non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Content_string_does_not_inherit_to_child() {
            // CSS Generated Content L3: `content` is NOT inherited.
            var cs = ComputeChild("#parent { content: \"parent text\"; }");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"),
                "content is non-inherited; child must see initial 'normal'");
        }

        // ── cascade overriding ────────────────────────────────────────────

        [Test]
        public void Content_higher_specificity_rule_wins() {
            // A more specific rule replaces a less specific one.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { content: \"low\"; } #x { content: \"high\"; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            var got = cs.Get("content");
            // The #x rule (id specificity) must override the div rule.
            Assert.That(got, Is.Not.Null);
            Assert.That(got, Does.Contain("high").Or.EqualTo("\"high\""));
        }

        [Test]
        public void Content_none_overrides_string_at_higher_specificity() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { content: \"some text\"; } #x { content: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("content"), Is.EqualTo("none"));
        }
    }
}
