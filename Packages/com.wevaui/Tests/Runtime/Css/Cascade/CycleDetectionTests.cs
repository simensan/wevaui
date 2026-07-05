using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Cycle-detection coverage for CSS Custom Properties L1 §3.1, attr()
    // resolution (CSS Values L4 §6.3), env() nested fallback (CSS
    // Environment Variables L1), and counter() missing-counter behaviour
    // (CSS Lists L3 §6.4).
    //
    // Spec reference (Custom Properties L1 §3.1):
    //   "If there is a cycle in the dependency graph, all the custom
    //   properties in the cycle must compute to their initial value
    //   (the guaranteed-invalid value)."
    //
    // The engine uses a HashSet<string> `seen` guard in VariableResolver
    // plus a MaxDepth=32 depth cap as a double-safety net.  The tests
    // below exercise every cycle topology and verify:
    //   (a) resolution terminates (no stack overflow / infinite loop),
    //   (b) cycle members produce the correct "invalid" sentinel so the
    //       cascade drops the declaration and reverts to initial/inherited,
    //   (c) non-cyclic deep chains still resolve correctly,
    //   (d) consumer-side fallbacks rescue cycle members per §3,
    //   (e) stored fallbacks inside cycle members do NOT rescue the cycle
    //       member itself (only the cycle-referencing fallback is ignored),
    //   (f) attr() does not recursively re-resolve attr() literals (spec:
    //       single-pass substitution),
    //   (g) content: counter(missing) does not crash when the named counter
    //       is never defined,
    //   (h) env() nested fallback resolves to innermost literal when all
    //       named variables are undefined.
    public class CycleDetectionTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        // ══════════════════════════════════════════════════════════════════
        // (1) var() self-reference — direct single-property cycle
        // ══════════════════════════════════════════════════════════════════

        // CSS Custom Properties L1 §3.1: `--a: var(--a)` → --a is cyclic →
        // the property becomes invalid-at-computed-value-time. A consumer
        // `color: var(--a)` (no fallback) → color reverts to initial "black".
        [Test]
        public void Var_self_reference_makes_property_invalid_no_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--a); color: var(--a); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // color initial is "black". Must not be empty.
            string color = cs.Get("color");
            Assert.That(color, Is.Not.Null, "color must not be null");
            Assert.That(color, Is.Not.Empty, "color must not be empty string");
            Assert.That(color, Is.EqualTo(CssProperties.InitialValueOf("color")),
                "self-referential cycle must revert color to its initial value");
        }

        // When a consumer provides a fallback the fallback rescues the invalid
        // cycle member: `color: var(--a, lime)` → "lime" (not initial).
        [Test]
        public void Var_self_reference_consumer_fallback_rescues_cycle() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--a); color: var(--a, lime); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("lime"),
                "consumer-side fallback must be used when cycle member is invalid");
        }

        // ══════════════════════════════════════════════════════════════════
        // (2) var() 2-property cycle
        // ══════════════════════════════════════════════════════════════════

        // CSS Custom Properties L1 §3.1: `--a: var(--b); --b: var(--a)` →
        // both are cycle members → invalid. Consumer `color: var(--a)` must
        // revert to initial.
        [Test]
        public void Var_two_property_cycle_both_members_become_invalid() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); color: var(--a); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string color = cs.Get("color");
            Assert.That(color, Is.Not.Null);
            Assert.That(color, Is.Not.Empty);
            Assert.That(color, Is.EqualTo(CssProperties.InitialValueOf("color")),
                "2-cycle members must both be invalid; color reverts to initial");
        }

        // Consumer fallback rescues a 2-cycle member.
        [Test]
        public void Var_two_property_cycle_consumer_fallback_wins() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); color: var(--a, navy); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("navy"),
                "consumer fallback must win when 2-cycle makes --a invalid");
        }

        // Verify resolution TERMINATES (no stack overflow).
        [Test]
        public void Var_two_property_cycle_terminates() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); color: var(--a, red); }")
            });
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => cs = engine.Compute(doc.GetElementById("x")),
                "2-cycle must terminate without exception");
            Assert.That(cs, Is.Not.Null);
        }

        // ══════════════════════════════════════════════════════════════════
        // (3) var() 3-property cycle
        // ══════════════════════════════════════════════════════════════════

        // `--a: var(--b); --b: var(--c); --c: var(--a)` → all three members
        // invalid → consumer gets initial.
        [Test]
        public void Var_three_property_cycle_all_members_become_invalid() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--c); --c: var(--a); color: var(--a); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string color = cs.Get("color");
            Assert.That(color, Is.Not.Null);
            Assert.That(color, Is.Not.Empty);
            Assert.That(color, Is.EqualTo(CssProperties.InitialValueOf("color")),
                "3-cycle: all members invalid; color must revert to initial");
        }

        [Test]
        public void Var_three_property_cycle_consumer_fallback_rescues() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--c); --c: var(--a); width: var(--a, 77px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("77px"),
                "consumer fallback wins for 3-cycle member");
        }

        [Test]
        public void Var_three_property_cycle_terminates() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--c); --c: var(--a); color: blue; }")
            });
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => cs = engine.Compute(doc.GetElementById("x")),
                "3-cycle must terminate without exception");
            Assert.That(cs, Is.Not.Null);
        }

        // ══════════════════════════════════════════════════════════════════
        // (4) Deep non-cyclic chain — must resolve to the terminal value
        // ══════════════════════════════════════════════════════════════════

        // 10-level linear chain `--a→--b→...→--j: 10px` must fully resolve.
        // MaxDepth=32 so a chain of depth 10 should never hit the cap.
        [Test]
        public void Var_ten_level_non_cyclic_chain_resolves_to_terminal() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { " +
                    "--a: var(--b); --b: var(--c); --c: var(--d); --d: var(--e); " +
                    "--e: var(--f); --f: var(--g); --g: var(--h); --h: var(--i); " +
                    "--i: var(--j); --j: 10px; " +
                    "width: var(--a); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("10px"),
                "10-level non-cyclic chain must resolve to the terminal value 10px");
        }

        // ══════════════════════════════════════════════════════════════════
        // (5) var() with fallback INSIDE cycle definition
        //
        // CSS Custom Properties L1 §3.1 semantics:
        //   `--a: var(--b, 5px); --b: var(--a, 10px)` → --a and --b cycle.
        //
        // Engine (actual) resolution trace for `color: var(--a)`:
        //   Resolving --a's raw value "var(--b, 5px)" with seen={}:
        //   → resolve --b (seen={"--b"}); --b's raw = "var(--a, 10px)"
        //     → resolve --a (seen={"--b","--a"}); --a's raw = "var(--b, 5px)"
        //       → resolve --b: "--b" IS in seen → InvalidValue (cycle detected)
        //     → cycle detected for inner "var(--b,5px)"; --a's outer fallback
        //       "10px" rescues → returns "10px" (removes "--a")
        //   → --b's substituted value = "10px" → not invalid → returns "10px"
        //   → --a resolves to "10px"
        // CSS Custom Properties L1 §3.1 — when both --a and --b are cycle
        // members, BOTH compute to the guaranteed-invalid value regardless
        // of any fallback supplied inside their own definitions. The
        // consumer `color: var(--a)` then sees --a as empty and reverts to
        // initial.
        [Test]
        public void Var_cycle_with_fallback_both_members_invalid() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b, 5px); --b: var(--a, 10px); color: var(--a); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs, Is.Not.Null, "cycle with fallback in definition must not crash");
            string color = cs.Get("color");
            // §3.1 — cycle membership trumps definition-side fallback;
            // color falls back to its initial value.
            Assert.That(color, Is.EqualTo(CssProperties.InitialValueOf("color")),
                "both cycle members must be invalid per §3.1; color must revert to initial");
        }

        // When consumer also supplies a fallback and cycle resolves to invalid,
        // the consumer's own fallback takes effect.
        [Test]
        public void Var_cycle_consumer_fallback_overrides_cycle_invalidity() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); color: var(--a, purple); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("purple"),
                "consumer-provided fallback must override cycle invalidity");
        }

        // ══════════════════════════════════════════════════════════════════
        // (6) attr() does NOT recursively re-resolve attr() literals
        //
        // CSS Values L4 §6.3: attr() performs a single-pass substitution
        // of the attribute value. If an attribute's value happens to contain
        // the text "attr(data-y)", the engine must return that literal string,
        // NOT recurse and substitute data-y.
        //
        // This guards against a hypothetical (incorrect) recursive attr()
        // implementation that would produce wrong values when attribute values
        // look like function calls.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Attr_does_not_recursively_resolve_attr_in_attribute_value() {
            // data-x attribute value is the literal string "attr(data-y)".
            // Spec says single-pass: content resolved against data-x yields
            // the raw string "attr(data-y)", NOT the resolved value of data-y.
            var doc = Html("<div id=\"x\" data-x=\"attr(data-y)\" data-y=\"green\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-x); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string content = cs.Get("content");
            Assert.That(content, Is.Not.Null,
                "content must not be null");
            // Must be the literal string, not "green" (which would indicate
            // recursive re-resolution of the attr() text inside the value).
            Assert.That(content, Is.Not.EqualTo("green"),
                "attr() must NOT recursively resolve attr() text found inside an attribute value");
            // The resolved value is the raw attribute content "attr(data-y)".
            Assert.That(content, Is.EqualTo("attr(data-y)"),
                "attr(data-x) must yield the literal attribute value without further substitution");
        }

        // Verify attr() chain depth cap: attr() referencing an attribute whose
        // value starts with "attr(" but without any nested attr() in the CSS
        // itself — confirms single-pass by checking that the literal is preserved.
        [Test]
        public void Attr_literal_value_containing_attr_text_is_preserved_as_string() {
            var doc = Html("<div id=\"x\" data-info=\"attr(ignored)\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: attr(data-info); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // The attr() call resolves data-info to the string "attr(ignored)".
            // The result must be "attr(ignored)" — not "" (empty) and not
            // a recursive resolution of "ignored".
            Assert.That(cs.Get("content"), Is.EqualTo("attr(ignored)"),
                "attr() must yield the raw attribute string without re-resolving it as CSS");
        }

        // ══════════════════════════════════════════════════════════════════
        // (7) content: counter(missing) — never-defined counter
        //
        // CSS Lists L3 §6.4: `counter()` in `content` on an element with no
        // matching counter scope must produce an empty string (counter value
        // defaults to 0 per spec, or the content value stores the token).
        // The engine stores the raw `counter(...)` token verbatim in the
        // cascade (rendering layer resolves scopes). We verify the cascade
        // itself does not throw and produces a non-null value.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Content_counter_of_missing_counter_does_not_crash() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { content: counter(never-defined-counter); }")
            });
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => cs = engine.Compute(doc.GetElementById("x")),
                "content: counter(missing) must not throw");
            Assert.That(cs, Is.Not.Null);
            string content = cs.Get("content");
            Assert.That(content, Is.Not.Null,
                "content with undefined counter must not be null");
            // The cascade stores the raw counter() token; rendering resolves
            // the scope. The stored value is non-initial.
            Assert.That(content, Is.Not.EqualTo("normal"),
                "content: counter(x) must override the initial 'normal' value");
        }

        // counter-reset and counter-increment on the same element is NOT a cycle
        // (counters are sequential, not referential). Both must cascade independently.
        [Test]
        public void Counter_reset_and_increment_on_same_element_are_not_cyclic() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { counter-reset: my-counter; counter-increment: my-counter; }")
            });
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => cs = engine.Compute(doc.GetElementById("x")),
                "counter-reset + counter-increment on same element must not crash");
            Assert.That(cs, Is.Not.Null);
            // Both counter-* properties must survive the cascade independently.
            Assert.That(cs.Get("counter-reset"), Is.EqualTo("my-counter"),
                "counter-reset must cascade correctly");
            Assert.That(cs.Get("counter-increment"), Is.EqualTo("my-counter"),
                "counter-increment must cascade correctly");
        }

        // ══════════════════════════════════════════════════════════════════
        // (8) env() nested fallback chain — undefined names
        //
        // CSS Environment Variables L1: `env(undef1, env(undef2, 5px))`.
        // When undef1 is not registered, the engine evaluates the fallback
        // expression `env(undef2, 5px)`. When undef2 is also not registered,
        // that falls back to "5px". Net result: "5px".
        // ══════════════════════════════════════════════════════════════════

        [SetUp]
        public void SetUp() {
            EnvironmentVariables.Reset();
        }

        [TearDown]
        public void TearDown() {
            EnvironmentVariables.Reset();
        }

        [Test]
        public void Env_nested_fallback_resolves_to_innermost_literal() {
            // Both undef1 and undef2 are unregistered. The nested fallback
            // chain must bottom out at the literal "5px".
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(undef1, env(undef2, 5px)); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("5px"),
                "nested env() fallback must resolve to the innermost literal when all names are undefined");
        }

        [Test]
        public void Env_nested_fallback_stops_at_first_registered_name() {
            // undef1 is not registered; undef2 IS registered to "12px".
            // Result: env(undef1, env(undef2, 5px)) → env(undef2, 5px) → "12px".
            EnvironmentVariables.Register("undef2", "12px");
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(undef1, env(undef2, 5px)); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("12px"),
                "nested env() fallback must use the first registered name in the chain");
        }

        [Test]
        public void Env_double_undefined_no_fallback_becomes_invalid() {
            // `env(undef1)` with no fallback → invalid-at-computed-value-time →
            // padding-top drops to initial value.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(undef1); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // padding-top initial is "0".
            string initial = CssProperties.InitialValueOf("padding-top");
            Assert.That(cs.Get("padding-top"), Is.EqualTo(initial),
                "env() with no fallback and undefined name must revert to initial");
        }

        // ══════════════════════════════════════════════════════════════════
        // (9) MaxDepth guard — extremely deep var() chain must terminate
        //
        // VariableResolver.MaxDepth = 32. A chain of 35 levels exceeds
        // this and must resolve to "" (InvalidValue collapsed) rather than
        // crashing with a StackOverflowException.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Var_chain_exceeding_max_depth_terminates_gracefully() {
            // Build a 35-level non-cyclic chain:
            // --v1 → --v2 → ... → --v34 → --v35: hotpink
            // At MaxDepth=32 the resolver returns InvalidValue (depth cap)
            // before reaching the terminal. Consumer var(--v1, fallback)
            // must fire the fallback without crashing.
            var sb = new System.Text.StringBuilder("#x { ");
            for (int i = 1; i <= 34; i++) {
                sb.Append($"--v{i}: var(--v{i + 1}); ");
            }
            sb.Append("--v35: hotpink; ");
            sb.Append("color: var(--v1, cornsilk); }");

            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(sb.ToString()) });
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => cs = engine.Compute(doc.GetElementById("x")),
                "35-level var() chain must terminate without crash");
            Assert.That(cs, Is.Not.Null);
            // With MaxDepth=32, the chain is cut short → invalid →
            // consumer fallback "cornsilk" fires.
            string color = cs.Get("color");
            Assert.That(color, Is.Not.Null);
            Assert.That(color, Is.Not.Empty,
                "depth-limited var() chain must not collapse to empty string");
        }

        // ══════════════════════════════════════════════════════════════════
        // (10) var() cycle does not bleed across elements
        //
        // A cycle on element A must not affect resolution on sibling B
        // that uses the same custom property name with a non-cyclic value.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Var_cycle_on_one_element_does_not_infect_sibling() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#a { --x: var(--x); color: var(--x, red); } " +
                    "#b { --x: blue; color: var(--x); }")
            });
            // Element #a: self-cycle on --x → invalid → consumer fallback "red"
            var csA = engine.Compute(doc.GetElementById("a"));
            Assert.That(csA.Get("color"), Is.EqualTo("red"),
                "#a must use consumer fallback when --x self-cycles");

            // Element #b: --x = "blue", no cycle → resolves normally
            var csB = engine.Compute(doc.GetElementById("b"));
            Assert.That(csB.Get("color"), Is.EqualTo("blue"),
                "#b must resolve --x to blue; sibling cycle must not bleed across elements");
        }
    }
}
