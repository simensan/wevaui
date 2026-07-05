using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Box Alignment Module Level 3 §8 — gap longhand cascade coverage.
    //
    // GapShorthandTests (in Shorthands/) tests the shorthand expander in
    // isolation. These tests exercise the full cascade pipeline:
    //   - initial values on the three longhands (row-gap, column-gap, gap)
    //   - keyword + length + percentage round-trips
    //   - non-inheritance
    //   - `gap` shorthand → longhand expansion through the cascade
    //
    // Spec refs:
    //   CSS Box Alignment L3 §8.1 — row-gap / column-gap
    //   CSS Box Alignment L3 §8.2 — gap (shorthand)
    public class GapPropertyCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Initial values §8.1
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Row_gap_initial_is_normal() {
            // Box Alignment L3 §8.1: initial value is `normal`.
            // For flex/grid containers `normal` computes to 0; the stored
            // string preserves the "unset" signal so layout can distinguish
            // an explicit `0` from an unset gap.
            var cs = Compute("");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("normal"));
        }

        [Test]
        public void Column_gap_initial_is_normal() {
            var cs = Compute("");
            Assert.That(cs.Get("column-gap"), Is.EqualTo("normal"));
        }

        [Test]
        public void Gap_shorthand_initial_is_normal() {
            var cs = Compute("");
            Assert.That(cs.Get("gap"), Is.EqualTo("normal"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Non-inheritance §8.1
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Row_gap_does_not_inherit() {
            var cs = ComputeChild("div { row-gap: 12px; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("normal"),
                "row-gap is non-inherited; child must see initial `normal`");
        }

        [Test]
        public void Column_gap_does_not_inherit() {
            var cs = ComputeChild("div { column-gap: 12px; }");
            Assert.That(cs.Get("column-gap"), Is.EqualTo("normal"),
                "column-gap is non-inherited; child must see initial `normal`");
        }

        // ══════════════════════════════════════════════════════════════════
        // Length values — row-gap / column-gap longhands
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Row_gap_length_round_trips() {
            var cs = Compute("#x { row-gap: 16px; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("16px"));
        }

        [Test]
        public void Column_gap_length_round_trips() {
            var cs = Compute("#x { column-gap: 24px; }");
            Assert.That(cs.Get("column-gap"), Is.EqualTo("24px"));
        }

        [Test]
        public void Row_gap_em_round_trips() {
            var cs = Compute("#x { row-gap: 1.5em; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("1.5em"));
        }

        [Test]
        public void Row_gap_zero_round_trips() {
            var cs = Compute("#x { row-gap: 0; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("0"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Percentage values §8.1
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Row_gap_percentage_round_trips() {
            // Box Alignment L3 §8.3: row-gap percentages resolve against
            // the container's block-axis size. The cascade stores the
            // authored percentage string; layout resolves it.
            var cs = Compute("#x { row-gap: 5%; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("5%"));
        }

        [Test]
        public void Column_gap_percentage_round_trips() {
            // column-gap percentages resolve against the inline-axis size.
            var cs = Compute("#x { column-gap: 10%; }");
            Assert.That(cs.Get("column-gap"), Is.EqualTo("10%"));
        }

        // ══════════════════════════════════════════════════════════════════
        // gap shorthand expansion through cascade §8.2
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Gap_one_value_expands_to_both_longhands() {
            // `gap: 8px` sets both row-gap and column-gap to 8px.
            var cs = Compute("#x { gap: 8px; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("8px"));
            Assert.That(cs.Get("column-gap"), Is.EqualTo("8px"));
        }

        [Test]
        public void Gap_two_values_sets_row_then_column() {
            // `gap: <row-gap> <column-gap>` — block axis first.
            var cs = Compute("#x { gap: 4px 20px; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("4px"));
            Assert.That(cs.Get("column-gap"), Is.EqualTo("20px"));
        }

        [Test]
        public void Gap_normal_shorthand_propagates_to_longhands() {
            var cs = Compute("#x { gap: normal; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("normal"));
            Assert.That(cs.Get("column-gap"), Is.EqualTo("normal"));
        }

        [Test]
        public void Longhand_overrides_gap_shorthand() {
            // A later longhand must win over an earlier shorthand.
            var cs = Compute("#x { gap: 8px; column-gap: 32px; }");
            Assert.That(cs.Get("row-gap"), Is.EqualTo("8px"),
                "row-gap comes from the gap shorthand");
            Assert.That(cs.Get("column-gap"), Is.EqualTo("32px"),
                "column-gap is overridden by the explicit longhand");
        }
    }
}
