using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Lists & Counters Level 3 — `counter-reset`, `counter-increment`,
    // `counter-set` cascade coverage, plus `content: counter(...)` round-trip.
    //
    // All three counter-* properties are registered in CssProperties as
    // non-inherited "none"-initial round-trip properties (CSS Lists L3 §5).
    // The rendering layer's counter-scope resolution is separate; the
    // cascade just carries the author value through.
    //
    // Spec references:
    //   CSS Lists L3 §5: counter-increment — increments named counters.
    //   CSS Lists L3 §5: counter-reset    — resets (or creates) named counters.
    //   CSS Lists L3 §5: counter-set      — sets without creating (newer prop).
    //   CSS2 §12.4: counter() / counters() in `content` on ::before / ::after.
    //   CSS Lists L3 §2: counter() / counters() function syntax.
    public class CounterPropertyTests {
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

        // ══════════════════════════════════════════════════════════════════
        // Registration state — assert each counter-* is registered with the
        // spec-required initial and (non-)inheritance flag.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Counter_reset_is_registered_non_inherited_initial_none() {
            int id = CssProperties.GetId("counter-reset");
            Assert.That(id, Is.GreaterThanOrEqualTo(0),
                "counter-reset must be registered per CSS Lists L3 §5");
            var prop = CssProperties.Get(id);
            Assert.That(prop.IsInherited, Is.False, "counter-reset is non-inherited");
            Assert.That(prop.InitialValue, Is.EqualTo("none"), "counter-reset initial = none");
        }

        [Test]
        public void Counter_increment_is_registered_non_inherited_initial_none() {
            int id = CssProperties.GetId("counter-increment");
            Assert.That(id, Is.GreaterThanOrEqualTo(0),
                "counter-increment must be registered per CSS Lists L3 §5");
            var prop = CssProperties.Get(id);
            Assert.That(prop.IsInherited, Is.False, "counter-increment is non-inherited");
            Assert.That(prop.InitialValue, Is.EqualTo("none"));
        }

        [Test]
        public void Counter_set_is_registered_non_inherited_initial_none() {
            int id = CssProperties.GetId("counter-set");
            Assert.That(id, Is.GreaterThanOrEqualTo(0),
                "counter-set must be registered per CSS Lists L3 §5");
            var prop = CssProperties.Get(id);
            Assert.That(prop.IsInherited, Is.False, "counter-set is non-inherited");
            Assert.That(prop.InitialValue, Is.EqualTo("none"));
        }

        // ══════════════════════════════════════════════════════════════════
        // `content` property — registered, non-inherited, initial "normal".
        // The cascade stores any author value verbatim. The rendering layer
        // (CascadeEngine.PseudoElements.ResolveContentString) interprets the
        // raw string; in v1 counter() / counters() fall to null (no box
        // generated) but the cascade layer must store the token faithfully.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Content_initial_is_normal() {
            // CSS2 §12.4 / CSS Generated Content L3 §2: initial value `normal`,
            // which computes to `none` for ::before / ::after (no box generated
            // by default).
            var cs = Compute("");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"));
        }

        [Test]
        public void Content_counter_function_round_trips_through_cascade() {
            // CSS Lists L3 §2.1: `counter(name)` function. The cascade must
            // carry the raw token even though v1 rendering treats it as null
            // (no pseudo-element box). This pins the cascade storage contract
            // so a future rendering pass can consume the stored value.
            var cs = Compute("#x { content: counter(my-counter); }");
            Assert.That(cs.Get("content"), Is.EqualTo("counter(my-counter)"));
        }

        [Test]
        public void Content_counter_with_style_arg_round_trips() {
            // CSS Lists L3 §2.1: `counter(name, <counter-style>)` — second
            // argument selects the marker rendering style. Cascade stores verbatim.
            var cs = Compute("#x { content: counter(chapter, lower-roman); }");
            Assert.That(cs.Get("content"), Is.EqualTo("counter(chapter, lower-roman)"));
        }

        [Test]
        public void Content_counters_function_round_trips_through_cascade() {
            // CSS Lists L3 §2.2: `counters(name, string)` — nested-list form.
            // The second argument is the separator between levels. The cascade
            // must store the raw form faithfully.
            var cs = Compute("#x { content: counters(section, \".\"); }");
            Assert.That(cs.Get("content"), Is.EqualTo("counters(section, \".\")"));
        }

        [Test]
        public void Content_counters_with_style_round_trips() {
            // CSS Lists L3 §2.2: `counters(name, string, style)` — three-arg
            // form with counter-style. Cascade stores as-is.
            var cs = Compute("#x { content: counters(item, \"-\", decimal); }");
            Assert.That(cs.Get("content"), Is.EqualTo("counters(item, \"-\", decimal)"));
        }

        [Test]
        public void Content_is_not_inherited() {
            // CSS Generated Content L3: `content` is NOT inherited (non-
            // inherited per spec). A child without its own rule must see the
            // initial value `normal`, not the parent's counter() form.
            var cs = ComputeChild("#parent { content: counter(c); }");
            Assert.That(cs.Get("content"), Is.EqualTo("normal"),
                "content is non-inherited; child must see initial 'normal', not parent's counter()");
        }

        // ══════════════════════════════════════════════════════════════════
        // Spec-required behaviour once counter-reset is registered.
        // Marked Ignore so the test suite reports them as skipped rather
        // than failing. Each Ignore message names the missing prerequisite.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Counter_reset_initial_is_none() {
            // CSS Lists L3 §5: initial value for counter-reset is `none`
            // (no counters are reset by default).
            var cs = Compute("");
            Assert.That(cs.Get("counter-reset"), Is.EqualTo("none"));
        }

        [Test]
        public void Counter_reset_single_name_defaults_to_zero() {
            // CSS Lists L3 §5: `counter-reset: my-counter` initialises
            // my-counter at 0 (the default start value).
            var cs = Compute("#x { counter-reset: my-counter; }");
            Assert.That(cs.Get("counter-reset"), Is.EqualTo("my-counter"));
        }

        [Test]
        public void Counter_reset_with_explicit_integer_round_trips() {
            // CSS Lists L3 §5: `counter-reset: my-counter 5` initialises
            // my-counter at 5.
            var cs = Compute("#x { counter-reset: my-counter 5; }");
            Assert.That(cs.Get("counter-reset"), Is.EqualTo("my-counter 5"));
        }

        [Test]
        public void Counter_reset_multiple_counters_round_trip() {
            // CSS Lists L3 §5: multiple name/integer pairs are allowed.
            var cs = Compute("#x { counter-reset: section 0 subsection 0; }");
            Assert.That(cs.Get("counter-reset"), Is.EqualTo("section 0 subsection 0"));
        }

        [Test]
        public void Counter_reset_is_not_inherited() {
            // CSS Lists L3 §5: counter-reset is NOT inherited.
            var cs = ComputeChild("#parent { counter-reset: c 3; }");
            Assert.That(cs.Get("counter-reset"), Is.EqualTo("none"),
                "counter-reset is non-inherited; child must see initial 'none'");
        }

        [Test]
        public void Counter_increment_initial_is_none() {
            // CSS Lists L3 §5: initial value for counter-increment is `none`.
            var cs = Compute("");
            Assert.That(cs.Get("counter-increment"), Is.EqualTo("none"));
        }

        [Test]
        public void Counter_increment_single_name_defaults_to_one() {
            // CSS Lists L3 §5: `counter-increment: my-counter` increments
            // my-counter by 1 (the default increment).
            var cs = Compute("#x { counter-increment: my-counter; }");
            Assert.That(cs.Get("counter-increment"), Is.EqualTo("my-counter"));
        }

        [Test]
        public void Counter_increment_with_explicit_value_round_trips() {
            // CSS Lists L3 §5: `counter-increment: my-counter 2` increments
            // my-counter by 2.
            var cs = Compute("#x { counter-increment: my-counter 2; }");
            Assert.That(cs.Get("counter-increment"), Is.EqualTo("my-counter 2"));
        }

        [Test]
        public void Counter_increment_is_not_inherited() {
            // CSS Lists L3 §5: counter-increment is NOT inherited.
            var cs = ComputeChild("#parent { counter-increment: c 2; }");
            Assert.That(cs.Get("counter-increment"), Is.EqualTo("none"),
                "counter-increment is non-inherited; child must see initial 'none'");
        }

        [Test]
        public void Counter_set_initial_is_none() {
            // CSS Lists L3 (newer): initial value for counter-set is `none`.
            var cs = Compute("");
            Assert.That(cs.Get("counter-set"), Is.EqualTo("none"));
        }

        [Test]
        public void Counter_set_single_name_with_value_round_trips() {
            // CSS Lists L3 (newer): `counter-set: my-counter 10` sets
            // my-counter to 10 without creating a new counter if one exists.
            var cs = Compute("#x { counter-set: my-counter 10; }");
            Assert.That(cs.Get("counter-set"), Is.EqualTo("my-counter 10"));
        }

        [Test]
        public void Counter_set_is_not_inherited() {
            // CSS Lists L3 (newer): counter-set is NOT inherited.
            var cs = ComputeChild("#parent { counter-set: c 5; }");
            Assert.That(cs.Get("counter-set"), Is.EqualTo("none"),
                "counter-set is non-inherited; child must see initial 'none'");
        }
    }
}
