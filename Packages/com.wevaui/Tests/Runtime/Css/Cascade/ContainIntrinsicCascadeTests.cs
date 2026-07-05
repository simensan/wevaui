using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Containment L2 §4 — `contain-intrinsic-size` and its
    // axis-specific longhands. Used as the dimension hint companion to
    // `content-visibility: auto` so the UA can reserve space for the
    // skipped subtree without measuring its real layout.
    //
    // All five properties are NOT inherited per spec. Initial value is
    // `none` (no hint — layout must measure or wait for first paint).
    //
    // Registration (CssProperties.BuildRegistry):
    //   contain-intrinsic-size         inherited=false initial="none"
    //   contain-intrinsic-width        inherited=false initial="none"
    //   contain-intrinsic-height       inherited=false initial="none"
    //   contain-intrinsic-block-size   inherited=false initial="none"
    //   contain-intrinsic-inline-size  inherited=false initial="none"
    //
    // Value syntax: `none | <length> | auto && <length>`
    // The two-token `auto <length>` form (and the L4 `auto none`) lets
    // the UA replace the saved last-measured size with the literal hint
    // after the first real layout — both forms must survive the cascade.
    //
    // Weva does NOT skip layout for off-screen content; these are
    // parse-only / cascade-only. The tests pin the round-trip alone.
    public class ContainIntrinsicCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ─── Initial values ───────────────────────────────────────────────

        [Test]
        public void ContainIntrinsicSize_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicWidth_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("contain-intrinsic-width"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicHeight_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("contain-intrinsic-height"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicBlockSize_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("contain-intrinsic-block-size"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicInlineSize_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("contain-intrinsic-inline-size"), Is.EqualTo("none"));
        }

        // ─── Value-form round-trips ───────────────────────────────────────

        [Test]
        public void ContainIntrinsicSize_none_round_trips() {
            var cs = Compute("#x { contain-intrinsic-size: none; }");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicSize_single_length_round_trips() {
            // §4: a single <length> means SAME value on both inline and block axes.
            var cs = Compute("#x { contain-intrinsic-size: 100px; }");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("100px"));
        }

        [Test]
        public void ContainIntrinsicSize_two_length_round_trips() {
            // §4: two <length> values = inline-size then block-size.
            var cs = Compute("#x { contain-intrinsic-size: 100px 200px; }");
            var v = cs.Get("contain-intrinsic-size");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("100px"));
            Assert.That(v, Does.Contain("200px"));
        }

        [Test]
        public void ContainIntrinsicSize_auto_length_round_trips() {
            // §4: `auto <length>` — the engine remembers the last-measured
            // size and falls back to <length> when the box has never
            // displayed. Both tokens must survive cascade.
            var cs = Compute("#x { contain-intrinsic-size: auto 300px; }");
            var v = cs.Get("contain-intrinsic-size");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("auto"));
            Assert.That(v, Does.Contain("300px"));
        }

        [Test]
        public void ContainIntrinsicSize_auto_two_length_round_trips() {
            // §4: `auto <length> <length>` — auto modifier on both axes.
            var cs = Compute("#x { contain-intrinsic-size: auto 100px 200px; }");
            var v = cs.Get("contain-intrinsic-size");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("auto"));
            Assert.That(v, Does.Contain("100px"));
            Assert.That(v, Does.Contain("200px"));
        }

        [Test]
        public void ContainIntrinsicWidth_single_length_round_trips() {
            var cs = Compute("#x { contain-intrinsic-width: 150px; }");
            Assert.That(cs.Get("contain-intrinsic-width"), Is.EqualTo("150px"));
        }

        [Test]
        public void ContainIntrinsicWidth_auto_length_round_trips() {
            var cs = Compute("#x { contain-intrinsic-width: auto 150px; }");
            var v = cs.Get("contain-intrinsic-width");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("auto"));
            Assert.That(v, Does.Contain("150px"));
        }

        [Test]
        public void ContainIntrinsicHeight_single_length_round_trips() {
            var cs = Compute("#x { contain-intrinsic-height: 250px; }");
            Assert.That(cs.Get("contain-intrinsic-height"), Is.EqualTo("250px"));
        }

        [Test]
        public void ContainIntrinsicHeight_em_unit_round_trips() {
            // Lengths in em are valid here per spec; preserved as authored.
            var cs = Compute("#x { contain-intrinsic-height: 5em; }");
            Assert.That(cs.Get("contain-intrinsic-height"), Is.EqualTo("5em"));
        }

        [Test]
        public void ContainIntrinsicBlockSize_length_round_trips() {
            var cs = Compute("#x { contain-intrinsic-block-size: 400px; }");
            Assert.That(cs.Get("contain-intrinsic-block-size"), Is.EqualTo("400px"));
        }

        [Test]
        public void ContainIntrinsicInlineSize_length_round_trips() {
            var cs = Compute("#x { contain-intrinsic-inline-size: 320px; }");
            Assert.That(cs.Get("contain-intrinsic-inline-size"), Is.EqualTo("320px"));
        }

        // ─── Inheritance (non-inherited per spec) ─────────────────────────

        [Test]
        public void ContainIntrinsicSize_does_not_inherit() {
            // §4: contain-intrinsic-size is NOT inherited. A child with no
            // own rule must resolve to its OWN initial `none`, not to the
            // parent's value.
            var child = ComputeChild("#p { contain-intrinsic-size: 100px; }");
            Assert.That(child.Get("contain-intrinsic-size"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicWidth_does_not_inherit() {
            var child = ComputeChild("#p { contain-intrinsic-width: 100px; }");
            Assert.That(child.Get("contain-intrinsic-width"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicHeight_does_not_inherit() {
            var child = ComputeChild("#p { contain-intrinsic-height: 100px; }");
            Assert.That(child.Get("contain-intrinsic-height"), Is.EqualTo("none"));
        }

        // ─── Cascade keywords ─────────────────────────────────────────────

        [Test]
        public void ContainIntrinsicSize_important_wins_cascade() {
            var cs = Compute("#x { contain-intrinsic-size: 100px !important; contain-intrinsic-size: 200px; }");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("100px"));
        }

        [Test]
        public void ContainIntrinsicSize_initial_keyword_resets_to_none() {
            // CSS Cascade L5 §7.1 — `initial` resolves to the property's
            // spec initial regardless of parent (which is moot here since
            // contain-intrinsic-size doesn't inherit).
            var cs = Compute("#x { contain-intrinsic-size: 100px; contain-intrinsic-size: initial; }");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicSize_unset_on_non_inherited_acts_as_initial() {
            // CSS Cascade L5 §7.3 — `unset` on a NON-inherited property
            // resolves to that property's initial value (here: `none`),
            // regardless of any parent rule.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { contain-intrinsic-size: 100px; } #c { contain-intrinsic-size: 50px; #c { contain-intrinsic-size: unset; } }")
            });
            var cs = Compute("#x { contain-intrinsic-size: 100px; contain-intrinsic-size: unset; }");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("none"));
        }

        [Test]
        public void ContainIntrinsicSize_inherit_keyword_pulls_parent() {
            // Explicit `inherit` always pulls the parent's value regardless
            // of the spec inheritance flag.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { contain-intrinsic-size: 100px; } #c { contain-intrinsic-size: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("contain-intrinsic-size"), Is.EqualTo("100px"));
        }

        // ─── Cross-property independence ──────────────────────────────────

        [Test]
        public void ContainIntrinsic_axis_longhands_independent_of_each_other() {
            // Setting only contain-intrinsic-width must NOT touch other
            // contain-intrinsic-* longhands.
            var cs = Compute("#x { contain-intrinsic-width: 200px; }");
            Assert.That(cs.Get("contain-intrinsic-width"), Is.EqualTo("200px"));
            Assert.That(cs.Get("contain-intrinsic-height"), Is.EqualTo("none"),
                "height longhand must remain at initial when only width is set");
            Assert.That(cs.Get("contain-intrinsic-block-size"), Is.EqualTo("none"),
                "block-size longhand must remain at initial when only width is set");
            Assert.That(cs.Get("contain-intrinsic-inline-size"), Is.EqualTo("none"),
                "inline-size longhand must remain at initial when only width is set");
        }

        [Test]
        public void ContainIntrinsic_does_not_bleed_to_or_from_contain_property() {
            // The unrelated `contain` property must NOT pick up
            // contain-intrinsic-size values, and vice-versa.
            var cs = Compute("#x { contain-intrinsic-size: 100px; }");
            Assert.That(cs.Get("contain-intrinsic-size"), Is.EqualTo("100px"));
            Assert.That(cs.Get("contain"), Is.EqualTo("none"),
                "`contain` must remain at initial when only contain-intrinsic-size is set");
        }
    }
}
