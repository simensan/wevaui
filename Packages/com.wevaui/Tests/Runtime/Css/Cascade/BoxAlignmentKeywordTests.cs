using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Box Alignment Module Level 3 — cascade-side keyword coverage.
    //
    // The LAYOUT behaviour of these properties is tested by FlexLayout /
    // GridLayout integration tests. These tests pin the cascade contract:
    //   - initial value when no rule matches
    //   - every spec keyword round-trips through the cascade unchanged
    //   - non-inheritance (all six longhands are non-inherited)
    //   - `safe`/`unsafe` overflow-position prefix is preserved as authored
    //   - multi-word keywords (`first baseline`, `last baseline`) round-trip
    //
    // Spec refs:
    //   CSS Box Alignment L3 §5 — justify-content, align-content
    //   CSS Box Alignment L3 §6 — justify-self, align-self
    //   CSS Box Alignment L3 §7 — justify-items, align-items
    public class BoxAlignmentKeywordTests {
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
        // justify-content §5
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Justify_content_initial_is_normal() {
            // Box Alignment L3 §5: initial value is `normal`.
            var cs = Compute("");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("normal"));
        }

        [Test]
        public void Justify_content_does_not_inherit() {
            // Box Alignment L3 §5: non-inherited.
            var cs = ComputeChild("div { justify-content: center; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("normal"),
                "justify-content is non-inherited; child must see initial `normal`");
        }

        [Test]
        public void Justify_content_start_round_trips() {
            var cs = Compute("#x { justify-content: start; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("start"));
        }

        [Test]
        public void Justify_content_end_round_trips() {
            var cs = Compute("#x { justify-content: end; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("end"));
        }

        [Test]
        public void Justify_content_center_round_trips() {
            var cs = Compute("#x { justify-content: center; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("center"));
        }

        [Test]
        public void Justify_content_flex_start_round_trips() {
            var cs = Compute("#x { justify-content: flex-start; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("flex-start"));
        }

        [Test]
        public void Justify_content_flex_end_round_trips() {
            var cs = Compute("#x { justify-content: flex-end; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("flex-end"));
        }

        [Test]
        public void Justify_content_space_between_round_trips() {
            var cs = Compute("#x { justify-content: space-between; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("space-between"));
        }

        [Test]
        public void Justify_content_space_around_round_trips() {
            var cs = Compute("#x { justify-content: space-around; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("space-around"));
        }

        [Test]
        public void Justify_content_space_evenly_round_trips() {
            var cs = Compute("#x { justify-content: space-evenly; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("space-evenly"));
        }

        [Test]
        public void Justify_content_stretch_round_trips() {
            var cs = Compute("#x { justify-content: stretch; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Justify_content_left_round_trips() {
            // `left`/`right` are physical-axis aliases (not valid for flex but
            // valid for Box Alignment in grid/block).
            var cs = Compute("#x { justify-content: left; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("left"));
        }

        [Test]
        public void Justify_content_right_round_trips() {
            var cs = Compute("#x { justify-content: right; }");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("right"));
        }

        // ══════════════════════════════════════════════════════════════════
        // align-content §5
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Align_content_initial_is_normal() {
            // Box Alignment L3 §5: initial = `normal`.
            var cs = Compute("");
            Assert.That(cs.Get("align-content"), Is.EqualTo("normal"));
        }

        [Test]
        public void Align_content_does_not_inherit() {
            var cs = ComputeChild("div { align-content: stretch; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("normal"),
                "align-content is non-inherited; child must see initial `normal`");
        }

        [Test]
        public void Align_content_start_round_trips() {
            var cs = Compute("#x { align-content: start; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("start"));
        }

        [Test]
        public void Align_content_end_round_trips() {
            var cs = Compute("#x { align-content: end; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("end"));
        }

        [Test]
        public void Align_content_center_round_trips() {
            var cs = Compute("#x { align-content: center; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("center"));
        }

        [Test]
        public void Align_content_flex_start_round_trips() {
            var cs = Compute("#x { align-content: flex-start; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("flex-start"));
        }

        [Test]
        public void Align_content_flex_end_round_trips() {
            var cs = Compute("#x { align-content: flex-end; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("flex-end"));
        }

        [Test]
        public void Align_content_space_between_round_trips() {
            var cs = Compute("#x { align-content: space-between; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("space-between"));
        }

        [Test]
        public void Align_content_space_around_round_trips() {
            var cs = Compute("#x { align-content: space-around; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("space-around"));
        }

        [Test]
        public void Align_content_space_evenly_round_trips() {
            var cs = Compute("#x { align-content: space-evenly; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("space-evenly"));
        }

        [Test]
        public void Align_content_stretch_round_trips() {
            var cs = Compute("#x { align-content: stretch; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("stretch"));
        }

        // ══════════════════════════════════════════════════════════════════
        // align-items §7
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Align_items_initial_is_stretch() {
            // Box Alignment L3 §7: initial = `stretch`.
            // (Note: this differs from `normal` — for most formatting
            // contexts `normal` behaves like `stretch`, but the cascade
            // stores "stretch" as the initial to avoid flex-container
            // ambiguity.)
            var cs = Compute("");
            Assert.That(cs.Get("align-items"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Align_items_does_not_inherit() {
            var cs = ComputeChild("div { align-items: center; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("stretch"),
                "align-items is non-inherited; child must see initial `stretch`");
        }

        [Test]
        public void Align_items_start_round_trips() {
            var cs = Compute("#x { align-items: start; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("start"));
        }

        [Test]
        public void Align_items_end_round_trips() {
            var cs = Compute("#x { align-items: end; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("end"));
        }

        [Test]
        public void Align_items_center_round_trips() {
            var cs = Compute("#x { align-items: center; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("center"));
        }

        [Test]
        public void Align_items_flex_start_round_trips() {
            var cs = Compute("#x { align-items: flex-start; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("flex-start"));
        }

        [Test]
        public void Align_items_flex_end_round_trips() {
            var cs = Compute("#x { align-items: flex-end; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("flex-end"));
        }

        [Test]
        public void Align_items_baseline_round_trips() {
            var cs = Compute("#x { align-items: baseline; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("baseline"));
        }

        [Test]
        public void Align_items_first_baseline_round_trips() {
            // Box Alignment L3 §7: `first baseline` and `last baseline`
            // are two-token baseline-alignment keywords.
            var cs = Compute("#x { align-items: first baseline; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("first baseline"));
        }

        [Test]
        public void Align_items_last_baseline_round_trips() {
            var cs = Compute("#x { align-items: last baseline; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("last baseline"));
        }

        [Test]
        public void Align_items_safe_center_round_trips() {
            // `safe`/`unsafe` overflow-position prefix: the cascade stores
            // the full authored value; FlexProperties strips the prefix
            // when mapping to the layout enum.
            var cs = Compute("#x { align-items: safe center; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("safe center"));
        }

        [Test]
        public void Align_items_unsafe_end_round_trips() {
            var cs = Compute("#x { align-items: unsafe end; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("unsafe end"));
        }

        // ══════════════════════════════════════════════════════════════════
        // justify-items §7
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Justify_items_initial_is_legacy() {
            // Box Alignment L3 §7: initial = `legacy` (inherits `left`/
            // `right`/`center` from the nearest ancestor that set it).
            var cs = Compute("");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("legacy"));
        }

        [Test]
        public void Justify_items_does_not_inherit() {
            // justify-items is non-inherited per spec.
            var cs = ComputeChild("div { justify-items: center; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("legacy"),
                "justify-items is non-inherited; child must see initial `legacy`");
        }

        [Test]
        public void Justify_items_start_round_trips() {
            var cs = Compute("#x { justify-items: start; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("start"));
        }

        [Test]
        public void Justify_items_end_round_trips() {
            var cs = Compute("#x { justify-items: end; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("end"));
        }

        [Test]
        public void Justify_items_center_round_trips() {
            var cs = Compute("#x { justify-items: center; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("center"));
        }

        [Test]
        public void Justify_items_stretch_round_trips() {
            var cs = Compute("#x { justify-items: stretch; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Justify_items_baseline_round_trips() {
            var cs = Compute("#x { justify-items: baseline; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("baseline"));
        }

        [Test]
        public void Justify_items_first_baseline_round_trips() {
            var cs = Compute("#x { justify-items: first baseline; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("first baseline"));
        }

        [Test]
        public void Justify_items_last_baseline_round_trips() {
            var cs = Compute("#x { justify-items: last baseline; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("last baseline"));
        }

        [Test]
        public void Justify_items_safe_center_round_trips() {
            var cs = Compute("#x { justify-items: safe center; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("safe center"));
        }

        [Test]
        public void Justify_items_unsafe_end_round_trips() {
            var cs = Compute("#x { justify-items: unsafe end; }");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("unsafe end"));
        }

        // ══════════════════════════════════════════════════════════════════
        // align-self §6
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Align_self_initial_is_auto() {
            // Box Alignment L3 §6: initial = `auto` (defers to parent's
            // align-items value at layout time).
            var cs = Compute("");
            Assert.That(cs.Get("align-self"), Is.EqualTo("auto"));
        }

        [Test]
        public void Align_self_does_not_inherit() {
            var cs = ComputeChild("div { align-self: center; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("auto"),
                "align-self is non-inherited; child must see initial `auto`");
        }

        [Test]
        public void Align_self_start_round_trips() {
            var cs = Compute("#x { align-self: start; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("start"));
        }

        [Test]
        public void Align_self_end_round_trips() {
            var cs = Compute("#x { align-self: end; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("end"));
        }

        [Test]
        public void Align_self_center_round_trips() {
            var cs = Compute("#x { align-self: center; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("center"));
        }

        [Test]
        public void Align_self_flex_start_round_trips() {
            var cs = Compute("#x { align-self: flex-start; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("flex-start"));
        }

        [Test]
        public void Align_self_flex_end_round_trips() {
            var cs = Compute("#x { align-self: flex-end; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("flex-end"));
        }

        [Test]
        public void Align_self_stretch_round_trips() {
            var cs = Compute("#x { align-self: stretch; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Align_self_baseline_round_trips() {
            var cs = Compute("#x { align-self: baseline; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("baseline"));
        }

        [Test]
        public void Align_self_first_baseline_round_trips() {
            var cs = Compute("#x { align-self: first baseline; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("first baseline"));
        }

        [Test]
        public void Align_self_safe_center_round_trips() {
            var cs = Compute("#x { align-self: safe center; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("safe center"));
        }

        // ══════════════════════════════════════════════════════════════════
        // justify-self §6
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Justify_self_initial_is_auto() {
            // Box Alignment L3 §6: initial = `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("auto"));
        }

        [Test]
        public void Justify_self_does_not_inherit() {
            var cs = ComputeChild("div { justify-self: center; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("auto"),
                "justify-self is non-inherited; child must see initial `auto`");
        }

        [Test]
        public void Justify_self_start_round_trips() {
            var cs = Compute("#x { justify-self: start; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("start"));
        }

        [Test]
        public void Justify_self_end_round_trips() {
            var cs = Compute("#x { justify-self: end; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("end"));
        }

        [Test]
        public void Justify_self_center_round_trips() {
            var cs = Compute("#x { justify-self: center; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("center"));
        }

        [Test]
        public void Justify_self_stretch_round_trips() {
            var cs = Compute("#x { justify-self: stretch; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Justify_self_flex_start_round_trips() {
            var cs = Compute("#x { justify-self: flex-start; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("flex-start"));
        }

        [Test]
        public void Justify_self_left_round_trips() {
            var cs = Compute("#x { justify-self: left; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("left"));
        }

        [Test]
        public void Justify_self_right_round_trips() {
            var cs = Compute("#x { justify-self: right; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("right"));
        }

        [Test]
        public void Justify_self_baseline_round_trips() {
            var cs = Compute("#x { justify-self: baseline; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("baseline"));
        }

        [Test]
        public void Justify_self_last_baseline_round_trips() {
            var cs = Compute("#x { justify-self: last baseline; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("last baseline"));
        }

        [Test]
        public void Justify_self_unsafe_end_round_trips() {
            var cs = Compute("#x { justify-self: unsafe end; }");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("unsafe end"));
        }
    }
}
