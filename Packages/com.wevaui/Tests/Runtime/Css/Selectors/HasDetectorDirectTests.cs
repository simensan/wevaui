using NUnit.Framework;
using Weva.Css.Selectors;

namespace Weva.Tests.Css.Selectors {
    // TG27 — direct coverage for HasDetector.Contains, which the cascade calls
    // on every compiled selector to decide whether the stylesheet needs
    // :has-sensitive invalidation. The walk has to traverse into nested
    // :is() / :where() / :not() arms (including non-first arms and arbitrary
    // depth) because :has(...) is permitted anywhere except inside another
    // :has(). These tests bypass the cascade and pin the walk directly so a
    // regression in HasDetector doesn't silently disable has-sensitivity for
    // an entire sheet (which would manifest as missed restyles on DOM
    // mutation, a hard-to-spot symptom).
    public class HasDetectorDirectTests {
        static CompiledSelector Compile(string s) => SelectorParser.Parse(s);

        [Test]
        public void Detects_top_level_has() {
            Assert.That(HasDetector.Contains(Compile(".foo:has(.bar)")), Is.True);
        }

        [Test]
        public void Does_not_detect_plain_class_selector() {
            Assert.That(HasDetector.Contains(Compile(".foo")), Is.False);
        }

        [Test]
        public void Detects_has_nested_inside_is() {
            Assert.That(HasDetector.Contains(Compile(".foo:is(.bar:has(.baz))")), Is.True);
        }

        [Test]
        public void Detects_has_nested_inside_where() {
            Assert.That(HasDetector.Contains(Compile(".foo:where(.bar:has(.baz))")), Is.True);
        }

        [Test]
        public void Detects_has_nested_inside_not() {
            Assert.That(HasDetector.Contains(Compile(".foo:not(.bar:has(.baz))")), Is.True);
        }

        [Test]
        public void Detects_has_in_non_first_arm_of_is_list() {
            // Pins the loop over PseudoClassSelector.InnerList: a buggy walk
            // that bails out after the first arm would miss the :has() that
            // lives in the second selector of the :is(...) list.
            Assert.That(HasDetector.Contains(Compile(".foo:is(.bar, .baz:has(.qux))")), Is.True);
        }

        [Test]
        public void Detects_has_at_deep_nesting_depth() {
            // :not(:is(:where(:has(.bar)))) — three layers of nested pseudo
            // wrappers before the :has. Pins that the recursion descends
            // through every layer of InnerList rather than only inspecting
            // the immediate child.
            Assert.That(HasDetector.Contains(Compile(".foo:not(:is(:where(:has(.bar))))")), Is.True);
        }

        [Test]
        public void Does_not_detect_when_is_list_has_no_has() {
            // Negative case for the multi-arm walk: confirms a clean :is()
            // list with no :has() anywhere returns false (so the cascade
            // doesn't pessimistically flip into has-sensitive mode).
            Assert.That(HasDetector.Contains(Compile(".foo:is(.bar, .baz)")), Is.False);
        }

        // ----- Bonus coverage: ContainsPseudo(kind) shares the same walker. -----

        [Test]
        public void ContainsPseudo_finds_has_via_kind_query() {
            // ContainsPseudo is the generic form of the walk; sanity-check
            // that requesting PseudoClassKind.Has agrees with Contains() on
            // the same input, so the two entry points don't drift.
            var sel = Compile(".foo:is(.bar:has(.baz))");
            Assert.That(HasDetector.ContainsPseudo(sel, PseudoClassKind.Has), Is.True);
            Assert.That(HasDetector.ContainsPseudo(sel, PseudoClassKind.Hover), Is.False);
        }

        [Test]
        public void Contains_handles_null_selector_gracefully() {
            // Defensive: cascade code-paths may hand a null reference; the
            // detector returns false rather than NRE'ing.
            Assert.That(HasDetector.Contains(null), Is.False);
            Assert.That(HasDetector.ContainsPseudo(null, PseudoClassKind.Has), Is.False);
        }
    }
}
