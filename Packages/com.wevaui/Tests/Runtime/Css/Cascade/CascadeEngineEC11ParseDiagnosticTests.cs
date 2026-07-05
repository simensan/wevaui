using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // EC11 — Four by-design parse-exception catches inside the cascade
    // (`@media`, `@container` condition, `@container` prelude, selector). The
    // cascade ignores parse-failed rules per Cascade L4; the fix preserves
    // the drop but adds a UICssDiagnostics.Warn for each unique offending text
    // so a CSS author who typos a `@media` query or selector sees their rule
    // was silently dropped.
    //
    // Dedupe key shape: "<source>:" + rawText. We test that:
    //   (1) the warning fires once per unique malformed input,
    //   (2) the by-design behavior (rule dropped, no styles cascaded from
    //       that source) is preserved,
    //   (3) repeated misses with the same input log only once.
    public class CascadeEngineEC11ParseDiagnosticTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        [SetUp]
        public void Reset() {
            CascadeEngine.ResetWarnings_TestOnly();
        }

        [Test]
        public void Malformed_media_query_drops_rule_and_warns() {
            // `@media foo bar baz` isn't a valid media query — the parser
            // throws MediaQueryParseException and the catch sets parsed=null,
            // which the surrounding apply-evaluator treats as "match nothing".
            LogAssert.Expect(LogType.Warning, new Regex(@"EC11/media.*parse failed"));

            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@media foo bar baz xyz { #x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));

            // By-design: a media query that fails to parse evaluates to false,
            // so the inner rule does not apply. The element's color remains
            // unset (computed-default).
            Assert.That(cs.Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Malformed_selector_drops_rule_and_warns() {
            // `>div` (combinator at the start) throws "Empty compound selector"
            // inside SelectorParser. The catch in CompileStyleRule continues
            // to the next selector and EC11/selector surfaces the dropped rule.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC11/selector.*parse failed"));

            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(">div { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));

            // Rule dropped — element gets no color from the malformed selector.
            Assert.That(cs.Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Repeated_resolution_of_same_malformed_media_query_logs_once() {
            // Compute() forces the media-cache lookup per call. After the
            // first parse failure, subsequent calls hit the negative cache
            // (mediaCache stores null) and the warning is NOT re-emitted
            // because the parser is not re-invoked. We additionally verify
            // a fresh CascadeEngine constructed from the same source ALSO
            // dedupes because the dedupe set is process-static.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC11/media"));

            for (int i = 0; i < 50; i++) {
                var engine = new CascadeEngine(new[] {
                    Author("@media zzz yyy xxx www { #x { color: red; } }")
                });
                var doc = Html("<div id=\"x\"></div>");
                engine.Compute(doc.GetElementById("x"));
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Distinct_malformed_selectors_each_warn_once() {
            // Two distinct combinator-at-start selectors → two dedupe keys
            // for EC11/selector. Confirms the dedupe is keyed on the
            // offending selector text, not a class-level "warned" flag.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC11/selector.*a-only"));
            LogAssert.Expect(LogType.Warning, new Regex(@"EC11/selector.*b-only"));

            var doc = Html("<div></div>");
            var engine = new CascadeEngine(new[] {
                Author(">a-only { color: red; } >b-only { color: blue; }")
            });
            engine.ComputeAll(doc);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_media_query_does_not_warn() {
            // Sanity: well-formed media query must not trip the warn path.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@media (min-width: 100px) { #x { color: red; } }")
            });
            engine.Compute(doc.GetElementById("x"));

            LogAssert.NoUnexpectedReceived();
        }
    }
}
