using NUnit.Framework;
using Weva.Css.Selectors;

namespace Weva.Tests.Css.Selectors {
    public class SpecificityTests {
        static Specificity S(string sel) => SelectorParser.Parse(sel).Specificity;

        [Test]
        public void Single_id() {
            Assert.That(S("#foo"), Is.EqualTo(new Specificity(1, 0, 0)));
        }

        [Test]
        public void Single_class() {
            Assert.That(S(".foo"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void Single_attribute() {
            Assert.That(S("[disabled]"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void Single_tag() {
            Assert.That(S("div"), Is.EqualTo(new Specificity(0, 0, 1)));
        }

        [Test]
        public void Universal_zero() {
            Assert.That(S("*"), Is.EqualTo(new Specificity(0, 0, 0)));
        }

        [Test]
        public void Pseudo_element_contributes_to_c() {
            Assert.That(S("::before"), Is.EqualTo(new Specificity(0, 0, 1)));
        }

        [Test]
        public void Legacy_single_colon_pseudo_element_contributes_to_c() {
            Assert.That(S(":before"), Is.EqualTo(new Specificity(0, 0, 1)));
        }

        [Test]
        public void Pseudo_class_contributes_to_b() {
            Assert.That(S(":hover"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void Compound_div_dot_foo() {
            Assert.That(S("div.foo"), Is.EqualTo(new Specificity(0, 1, 1)));
        }

        [Test]
        public void Hash_a_dot_b_descendant_div() {
            Assert.That(S("#a.b div"), Is.EqualTo(new Specificity(1, 1, 1)));
        }

        [Test]
        public void Tag_pseudo() {
            Assert.That(S("a:hover"), Is.EqualTo(new Specificity(0, 1, 1)));
        }

        [Test]
        public void Is_uses_max_of_inner() {
            Assert.That(S(":is(.a, #b)"), Is.EqualTo(new Specificity(1, 0, 0)));
        }

        [Test]
        public void Where_is_zero_regardless() {
            Assert.That(S(":where(#a, .b, div)"), Is.EqualTo(new Specificity(0, 0, 0)));
        }

        [Test]
        public void Not_uses_max_of_inner() {
            Assert.That(S(":not(.foo)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void Not_with_id_inner() {
            Assert.That(S(":not(#x)"), Is.EqualTo(new Specificity(1, 0, 0)));
        }

        [Test]
        public void Compare_a_dominates_b() {
            var a = new Specificity(1, 0, 0);
            var b = new Specificity(0, 99, 99);
            Assert.That(a.CompareTo(b), Is.GreaterThan(0));
        }

        [Test]
        public void Compare_b_dominates_c() {
            var a = new Specificity(0, 1, 0);
            var b = new Specificity(0, 0, 99);
            Assert.That(a.CompareTo(b), Is.GreaterThan(0));
        }

        [Test]
        public void Specificity_add() {
            var a = new Specificity(1, 2, 3);
            var b = new Specificity(0, 1, 1);
            Assert.That(Specificity.Add(a, b), Is.EqualTo(new Specificity(1, 3, 4)));
        }

        [Test]
        public void Equality_works() {
            Assert.That(new Specificity(1, 2, 3), Is.EqualTo(new Specificity(1, 2, 3)));
            Assert.That(new Specificity(1, 2, 3), Is.Not.EqualTo(new Specificity(1, 2, 4)));
        }

        [Test]
        public void ToString_is_tuple() {
            Assert.That(new Specificity(1, 2, 3).ToString(), Is.EqualTo("(1,2,3)"));
        }

        [Test]
        public void Is_with_only_classes() {
            Assert.That(S(":is(.a, .b)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        // Regression: a compound selector accumulates A/B/C from each part —
        // `#d` -> (1,0,0), two classes -> (0,2,0), one tag -> (0,0,1), totalling
        // (1,2,1). Verifies CompoundSelector.Specificity sums correctly across
        // mixed simple-selector kinds.
        [Test]
        public void Compound_id_classes_tag_sum_correctly() {
            Assert.That(S("a.b.c#d"), Is.EqualTo(new Specificity(1, 2, 1)));
        }

        // Regression: `*` contributes (0,0,0) but a pseudo-element bumps C by 1,
        // so `*::before` should be (0,0,1) — same as a bare type selector.
        // Pins the universal+pseudo-element interaction.
        [Test]
        public void Universal_with_pseudo_element_is_C_one() {
            Assert.That(S("*::before"), Is.EqualTo(new Specificity(0, 0, 1)));
        }

        // Regression: `:where()` MUST contribute zero even when wrapping an
        // id selector — this is the whole point of `:where` for cascade
        // authoring (low-specificity defaults).
        [Test]
        public void Where_wrapping_id_is_zero() {
            Assert.That(S("a:where(#hero)"), Is.EqualTo(new Specificity(0, 0, 1)));
        }
    }
}
