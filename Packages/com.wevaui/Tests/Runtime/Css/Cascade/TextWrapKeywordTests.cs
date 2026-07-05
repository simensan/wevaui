using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Text Module L4 §3 — text-wrap cascade and keyword round-trips.
    //
    // `text-wrap` is the shorthand for `text-wrap-mode` + `text-wrap-style`
    // (CSS Text L4 §3.4). In v1 the engine treats it as a single inherited
    // property with initial value `wrap`. The following keywords are specified:
    //
    //   wrap     — normal soft-wrap (initial)
    //   nowrap   — no soft-wrap (equivalent to white-space: nowrap on the wrap axis)
    //   balance  — UA balances lines to minimize rag (short lines allowed)
    //   pretty   — UA picks aesthetically pleasant breaks (spec-defined preference)
    //   stable   — stable reflow preference (for editing UIs)
    //
    // The engine honours `nowrap` in the line-breaking path via `TextWrapId`;
    // `balance` / `pretty` / `stable` parse and cascade correctly but v1 does not
    // implement the special multi-pass balance or aesthetic algorithms — they are
    // treated as `wrap` by the line-layout path. This file pins the parse / cascade
    // / inheritance surface so that a future implementation can add behavioral tests
    // without first hunting for round-trip regressions.
    //
    // Spec reference: CSS Text L4 §3.4, CSS Cascade L5 §4.1.
    public class TextWrapKeywordTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string parentCss) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(parentCss) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ── Initial value ────────────────────────────────────────────────────

        [Test]
        public void Text_wrap_initial_is_wrap() {
            // CSS Text L4 §3.4: initial value of `text-wrap` is `wrap`.
            var cs = Compute("");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("wrap"),
                "text-wrap initial value must be 'wrap'");
        }

        // ── Keyword round-trips ──────────────────────────────────────────────

        [Test]
        public void Text_wrap_nowrap_round_trips() {
            // `nowrap` prevents soft wrapping; equiv to white-space:nowrap on the wrap axis.
            var cs = Compute("#x { text-wrap: nowrap; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("nowrap"));
        }

        [Test]
        public void Text_wrap_wrap_explicit_round_trips() {
            // Explicit `wrap` must cascade to exactly `wrap`, not disappear.
            var cs = Compute("#x { text-wrap: wrap; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("wrap"));
        }

        [Test]
        public void Text_wrap_balance_round_trips() {
            // `balance` is a v1 parse-only value; the cascade must round-trip it
            // so authors can author it without silent drop, even though the
            // line-layout path treats it like `wrap` for v1.
            var cs = Compute("#x { text-wrap: balance; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("balance"),
                "balance must survive parse → cascade → Get round-trip");
        }

        [Test]
        public void Text_wrap_pretty_round_trips() {
            // `pretty` signals aesthetic line-breaking preference (no v1 implementation).
            var cs = Compute("#x { text-wrap: pretty; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("pretty"),
                "pretty must survive parse → cascade → Get round-trip");
        }

        [Test]
        public void Text_wrap_stable_round_trips() {
            // `stable` is intended for editing UIs to avoid reflow on cursor moves.
            var cs = Compute("#x { text-wrap: stable; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("stable"),
                "stable must survive parse → cascade → Get round-trip");
        }

        // ── Inheritance ──────────────────────────────────────────────────────

        [Test]
        public void Text_wrap_is_inherited() {
            // CSS Text L4 §3.4: text-wrap is inherited — a parent value propagates
            // to descendants that don't specify their own value.
            var cs = ComputeChild("#p { text-wrap: nowrap; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("nowrap"),
                "text-wrap must be inherited by child");
        }

        [Test]
        public void Text_wrap_balance_inherits_to_descendants() {
            var cs = ComputeChild("#p { text-wrap: balance; }");
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("balance"),
                "balance value must propagate to child via inheritance");
        }

        [Test]
        public void Text_wrap_child_overrides_inherited_value() {
            // Child with an explicit `text-wrap` beats the inherited value.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-wrap: nowrap; } #c { text-wrap: balance; }")
            });
            var parent = engine.Compute(doc.GetElementById("p"));
            var child  = engine.Compute(doc.GetElementById("c"));
            Assert.That(parent.Get("text-wrap"), Is.EqualTo("nowrap"));
            Assert.That(child.Get("text-wrap"), Is.EqualTo("balance"),
                "explicit child declaration must override inherited 'nowrap'");
        }

        [Test]
        public void Text_wrap_unset_on_inherited_property_restores_inherited_value() {
            // `unset` on an inherited property resolves to the inherited value.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-wrap: nowrap; } #c { text-wrap: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-wrap"), Is.EqualTo("nowrap"),
                "'unset' on inherited text-wrap must resolve to the inherited 'nowrap'");
        }

        // ── Non-inheritance guard ────────────────────────────────────────────

        [Test]
        public void Text_wrap_does_not_leak_across_independent_branches() {
            // Verify that setting text-wrap on one branch does not affect sibling.
            var doc = Html("<div><div id=\"a\"></div><div id=\"b\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author("#a { text-wrap: nowrap; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("text-wrap"), Is.EqualTo("nowrap"));
            // #b has no rule and its parent has no text-wrap — it sees the initial value.
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("text-wrap"), Is.EqualTo("wrap"),
                "sibling with no text-wrap declaration must see the initial 'wrap'");
        }

        // ── Cascade priority ─────────────────────────────────────────────────

        [Test]
        public void Text_wrap_important_beats_author_rule() {
            // !important forces win over a later non-important rule.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { text-wrap: nowrap !important; } #x { text-wrap: balance; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("text-wrap"), Is.EqualTo("nowrap"),
                "!important 'nowrap' must beat later 'balance'");
        }
    }
}
