using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Logical Properties and Values L1 §4 — `inline-size`, `block-size`,
    // and their min/max companions. These are the writing-mode-aware
    // equivalents of `width` / `height`: under horizontal-tb (the engine
    // default), `inline-size` maps to width and `block-size` maps to
    // height. Under vertical writing modes those mappings swap.
    //
    // Weva inline-shapes horizontally only, but the cascade must still
    // accept and round-trip the logical longhands so author CSS that uses
    // them (common in spec-conformant codebases) doesn't drop on the floor.
    //
    // Registration (CssProperties.BuildRegistry, lines 590-595):
    //   inline-size       not inherited  initial="auto"
    //   block-size        not inherited  initial="auto"
    //   min-inline-size   not inherited  initial="auto"
    //   min-block-size    not inherited  initial="auto"
    //   max-inline-size   not inherited  initial="none"
    //   max-block-size    not inherited  initial="none"
    public class LogicalSizingCascadeTests {
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

        // ─── inline-size ──────────────────────────────────────────────────

        [Test]
        public void InlineSize_initial_is_auto() {
            var cs = Compute("");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void InlineSize_pixel_value_round_trips() {
            var cs = Compute("#x { inline-size: 240px; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("240px"));
        }

        [Test]
        public void InlineSize_percentage_round_trips() {
            var cs = Compute("#x { inline-size: 50%; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("50%"));
        }

        [Test]
        public void InlineSize_auto_keyword_round_trips() {
            var cs = Compute("#x { inline-size: auto; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void InlineSize_does_not_inherit() {
            // §4: logical sizing properties are NOT inherited per spec.
            var child = ComputeChild("#p { inline-size: 240px; }");
            Assert.That(child.Get("inline-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void InlineSize_important_wins_cascade() {
            var cs = Compute("#x { inline-size: 100px !important; inline-size: 200px; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("100px"));
        }

        [Test]
        public void InlineSize_initial_keyword_resets_to_auto() {
            var cs = Compute("#x { inline-size: 240px; inline-size: initial; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("auto"));
        }

        // ─── block-size ───────────────────────────────────────────────────

        [Test]
        public void BlockSize_initial_is_auto() {
            var cs = Compute("");
            Assert.That(cs.Get("block-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void BlockSize_pixel_value_round_trips() {
            var cs = Compute("#x { block-size: 120px; }");
            Assert.That(cs.Get("block-size"), Is.EqualTo("120px"));
        }

        [Test]
        public void BlockSize_em_unit_round_trips() {
            var cs = Compute("#x { block-size: 10em; }");
            Assert.That(cs.Get("block-size"), Is.EqualTo("10em"));
        }

        [Test]
        public void BlockSize_does_not_inherit() {
            var child = ComputeChild("#p { block-size: 200px; }");
            Assert.That(child.Get("block-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void BlockSize_important_wins_cascade() {
            var cs = Compute("#x { block-size: 80px !important; block-size: 160px; }");
            Assert.That(cs.Get("block-size"), Is.EqualTo("80px"));
        }

        // ─── min-inline-size ──────────────────────────────────────────────

        [Test]
        public void MinInlineSize_initial_is_auto() {
            // §4: initial = auto (computes to 0 for width-like contexts,
            // see CSS Sizing L3 §6.7; cascade carries the keyword).
            var cs = Compute("");
            Assert.That(cs.Get("min-inline-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void MinInlineSize_pixel_value_round_trips() {
            var cs = Compute("#x { min-inline-size: 50px; }");
            Assert.That(cs.Get("min-inline-size"), Is.EqualTo("50px"));
        }

        [Test]
        public void MinInlineSize_does_not_inherit() {
            var child = ComputeChild("#p { min-inline-size: 100px; }");
            Assert.That(child.Get("min-inline-size"), Is.EqualTo("auto"));
        }

        // ─── max-inline-size ──────────────────────────────────────────────

        [Test]
        public void MaxInlineSize_initial_is_none() {
            // §4: initial = none (no maximum).
            var cs = Compute("");
            Assert.That(cs.Get("max-inline-size"), Is.EqualTo("none"));
        }

        [Test]
        public void MaxInlineSize_pixel_value_round_trips() {
            var cs = Compute("#x { max-inline-size: 600px; }");
            Assert.That(cs.Get("max-inline-size"), Is.EqualTo("600px"));
        }

        [Test]
        public void MaxInlineSize_none_keyword_round_trips() {
            var cs = Compute("#x { max-inline-size: none; }");
            Assert.That(cs.Get("max-inline-size"), Is.EqualTo("none"));
        }

        [Test]
        public void MaxInlineSize_does_not_inherit() {
            var child = ComputeChild("#p { max-inline-size: 600px; }");
            Assert.That(child.Get("max-inline-size"), Is.EqualTo("none"));
        }

        [Test]
        public void MaxInlineSize_important_wins_cascade() {
            var cs = Compute("#x { max-inline-size: 500px !important; max-inline-size: 800px; }");
            Assert.That(cs.Get("max-inline-size"), Is.EqualTo("500px"));
        }

        // ─── min-block-size ──────────────────────────────────────────────

        [Test]
        public void MinBlockSize_initial_is_auto() {
            var cs = Compute("");
            Assert.That(cs.Get("min-block-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void MinBlockSize_pixel_value_round_trips() {
            var cs = Compute("#x { min-block-size: 40px; }");
            Assert.That(cs.Get("min-block-size"), Is.EqualTo("40px"));
        }

        [Test]
        public void MinBlockSize_does_not_inherit() {
            var child = ComputeChild("#p { min-block-size: 100px; }");
            Assert.That(child.Get("min-block-size"), Is.EqualTo("auto"));
        }

        // ─── max-block-size ──────────────────────────────────────────────

        [Test]
        public void MaxBlockSize_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("max-block-size"), Is.EqualTo("none"));
        }

        [Test]
        public void MaxBlockSize_pixel_value_round_trips() {
            var cs = Compute("#x { max-block-size: 800px; }");
            Assert.That(cs.Get("max-block-size"), Is.EqualTo("800px"));
        }

        [Test]
        public void MaxBlockSize_none_keyword_round_trips() {
            var cs = Compute("#x { max-block-size: none; }");
            Assert.That(cs.Get("max-block-size"), Is.EqualTo("none"));
        }

        [Test]
        public void MaxBlockSize_does_not_inherit() {
            var child = ComputeChild("#p { max-block-size: 800px; }");
            Assert.That(child.Get("max-block-size"), Is.EqualTo("none"));
        }

        // ─── Sizing-keyword values (CSS Sizing L3 §6) ─────────────────────

        [Test]
        public void InlineSize_min_content_keyword_round_trips() {
            // CSS Sizing L3 §6.3 — `min-content` keyword sizes the box to
            // the longest unbreakable inline content.
            var cs = Compute("#x { inline-size: min-content; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("min-content"));
        }

        [Test]
        public void InlineSize_max_content_keyword_round_trips() {
            // CSS Sizing L3 §6.4 — `max-content` keyword sizes the box to
            // its preferred / un-line-broken inline extent.
            var cs = Compute("#x { inline-size: max-content; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("max-content"));
        }

        [Test]
        public void InlineSize_fit_content_keyword_round_trips() {
            // CSS Sizing L3 §6.5 — bare `fit-content` keyword (no argument).
            var cs = Compute("#x { inline-size: fit-content; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("fit-content"));
        }

        // ─── Cross-property independence ─────────────────────────────────

        [Test]
        public void Logical_sizing_longhands_are_independent() {
            // Setting one logical sizing longhand must NOT bleed into the
            // other five. They share the inline/block axis prefix but
            // live in distinct cascade slots.
            var cs = Compute("#x { inline-size: 240px; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("240px"));
            Assert.That(cs.Get("block-size"), Is.EqualTo("auto"),
                "block-size must remain at initial when only inline-size is set");
            Assert.That(cs.Get("min-inline-size"), Is.EqualTo("auto"),
                "min-inline-size must remain at initial");
            Assert.That(cs.Get("max-inline-size"), Is.EqualTo("none"),
                "max-inline-size must remain at initial");
            Assert.That(cs.Get("min-block-size"), Is.EqualTo("auto"),
                "min-block-size must remain at initial");
            Assert.That(cs.Get("max-block-size"), Is.EqualTo("none"),
                "max-block-size must remain at initial");
        }

        [Test]
        public void Logical_sizing_maps_to_physical_under_horizontal_tb() {
            // CSS Logical Properties L1 §5: under the default writing-mode
            // (horizontal-tb), `inline-size` maps to `width` and
            // `block-size` maps to `height`. Weva performs that mapping
            // at cascade time so downstream layout reads physical
            // properties directly — authors get the spec-mandated
            // physical mirror as if they'd written width/height.
            var cs = Compute("#x { inline-size: 240px; block-size: 120px; }");
            Assert.That(cs.Get("inline-size"), Is.EqualTo("240px"));
            Assert.That(cs.Get("block-size"), Is.EqualTo("120px"));
            Assert.That(cs.Get("width"), Is.EqualTo("240px"),
                "inline-size maps to width under horizontal-tb (CSS Logical L1 §5)");
            Assert.That(cs.Get("height"), Is.EqualTo("120px"),
                "block-size maps to height under horizontal-tb");
        }
    }
}
