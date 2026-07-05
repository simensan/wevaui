using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Overflow L4 §6 I12e/I12f cascade coverage — gaps not covered by
    // OverflowClipMarginCascadeTests.cs:
    //
    //  1. Logical-axis aliases not yet tested:
    //       inline-end  → right in LTR  (was tested: inline-start → left only)
    //       block-end   → bottom in horizontal-tb  (was tested: block-start → top only)
    //       inline-start → right in RTL  (RTL not previously tested)
    //       inline-end   → left in RTL
    //
    //  2. <visual-box> keyword round-trip through the full CSS cascade.
    //     The CascadeEngine stores the raw string (e.g. "content-box 8px") as-authored;
    //     this file pins that the cascade-computed Get() value preserves the keyword so
    //     the OverflowResolver can later extract it via GetParsed → CssValueList.
    public class OverflowClipMarginLogicalAndVisualBoxTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css, string id = "x") {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById(id));
        }

        static ComputedStyle ComputeWithDirection(string css, string direction = "ltr", string id = "x") {
            // Wrap element in a container that sets direction so the cascade
            // sees the parent direction for the logical alias resolution.
            var doc = Html("<div id=\"x\" style=\"direction:" + direction + "\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById(id));
        }

        // ── LTR logical aliases (horizontal-tb default) ──────────────────────

        [Test]
        public void Overflow_clip_margin_inline_end_aliases_to_right_in_ltr() {
            // In horizontal-tb / LTR: inline-end = right.
            var cs = Compute("#x { overflow-clip-margin-inline-end: 9px; }");
            Assert.That(cs.Get("overflow-clip-margin-right"), Is.EqualTo("9px"),
                "overflow-clip-margin-inline-end must alias to right in LTR writing mode");
            // Other physical sides untouched.
            Assert.That(cs.Get("overflow-clip-margin-left"),   Is.EqualTo("0px"));
            Assert.That(cs.Get("overflow-clip-margin-top"),    Is.EqualTo("0px"));
            Assert.That(cs.Get("overflow-clip-margin-bottom"), Is.EqualTo("0px"));
        }

        [Test]
        public void Overflow_clip_margin_block_end_aliases_to_bottom_in_horizontal_tb() {
            // In horizontal-tb: block-end = bottom.
            var cs = Compute("#x { overflow-clip-margin-block-end: 13px; }");
            Assert.That(cs.Get("overflow-clip-margin-bottom"), Is.EqualTo("13px"),
                "overflow-clip-margin-block-end must alias to bottom in horizontal-tb");
            Assert.That(cs.Get("overflow-clip-margin-top"),  Is.EqualTo("0px"));
            Assert.That(cs.Get("overflow-clip-margin-left"), Is.EqualTo("0px"));
            Assert.That(cs.Get("overflow-clip-margin-right"), Is.EqualTo("0px"));
        }

        [Test]
        public void All_four_logical_longhands_alias_correctly_in_ltr() {
            // Set all four logical longhands; verify each maps to its physical counterpart.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { " +
                    "overflow-clip-margin-inline-start: 1px; " +
                    "overflow-clip-margin-inline-end: 2px; " +
                    "overflow-clip-margin-block-start: 3px; " +
                    "overflow-clip-margin-block-end: 4px; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("overflow-clip-margin-left"),   Is.EqualTo("1px"),
                "inline-start → left in LTR");
            Assert.That(cs.Get("overflow-clip-margin-right"),  Is.EqualTo("2px"),
                "inline-end → right in LTR");
            Assert.That(cs.Get("overflow-clip-margin-top"),    Is.EqualTo("3px"),
                "block-start → top in horizontal-tb");
            Assert.That(cs.Get("overflow-clip-margin-bottom"), Is.EqualTo("4px"),
                "block-end → bottom in horizontal-tb");
        }

        // ── RTL logical aliases (horizontal-tb + RTL) ────────────────────────

        [Test]
        public void Overflow_clip_margin_inline_start_aliases_to_right_in_rtl() {
            // In horizontal-tb / RTL: inline-start = right.
            var doc = Html("<div id=\"x\" style=\"direction:rtl\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { overflow-clip-margin-inline-start: 7px; direction: rtl; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("overflow-clip-margin-right"), Is.EqualTo("7px"),
                "overflow-clip-margin-inline-start must alias to right in RTL writing mode");
            Assert.That(cs.Get("overflow-clip-margin-left"), Is.EqualTo("0px"));
        }

        [Test]
        public void Overflow_clip_margin_inline_end_aliases_to_left_in_rtl() {
            // In horizontal-tb / RTL: inline-end = left.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { overflow-clip-margin-inline-end: 5px; direction: rtl; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("overflow-clip-margin-left"), Is.EqualTo("5px"),
                "overflow-clip-margin-inline-end must alias to left in RTL writing mode");
            Assert.That(cs.Get("overflow-clip-margin-right"), Is.EqualTo("0px"));
        }

        // ── <visual-box> keyword round-trip through cascade ──────────────────

        [Test]
        public void Shorthand_with_visual_box_keyword_round_trips_through_cascade() {
            // CSS Overflow L4 §6: `<visual-box>? <length [0,∞]>?` grammar.
            // The cascade stores the raw value as-authored; Get() must return a
            // string that still carries the visual-box keyword so the OverflowResolver
            // can extract it via GetParsed → CssValueList → TryReadVisualBox.
            var cs = Compute("#x { overflow: clip; overflow-clip-margin: content-box 8px; }");
            string raw = cs.Get("overflow-clip-margin");
            Assert.That(raw, Does.Contain("content-box"),
                "content-box keyword must survive the cascade Get() round-trip");
            Assert.That(raw, Does.Contain("8px"),
                "length part must survive the cascade Get() round-trip");
        }

        [Test]
        public void Shorthand_padding_box_keyword_round_trips_through_cascade() {
            var cs = Compute("#x { overflow: clip; overflow-clip-margin: padding-box 12px; }");
            string raw = cs.Get("overflow-clip-margin");
            Assert.That(raw, Does.Contain("padding-box"),
                "padding-box keyword must survive the cascade Get() round-trip");
            Assert.That(raw, Does.Contain("12px"));
        }

        [Test]
        public void Shorthand_border_box_keyword_round_trips_through_cascade() {
            var cs = Compute("#x { overflow: clip; overflow-clip-margin: border-box 4px; }");
            string raw = cs.Get("overflow-clip-margin");
            Assert.That(raw, Does.Contain("border-box"),
                "border-box keyword must survive the cascade Get() round-trip");
            Assert.That(raw, Does.Contain("4px"));
        }

        [Test]
        public void Per_side_longhand_with_visual_box_keyword_round_trips_through_cascade() {
            // A per-side longhand with visual-box must also preserve the raw string.
            var cs = Compute("#x { overflow: clip; overflow-clip-margin-top: content-box 6px; }");
            string raw = cs.Get("overflow-clip-margin-top");
            Assert.That(raw, Does.Contain("content-box"),
                "content-box keyword on per-side top longhand must survive cascade");
            Assert.That(raw, Does.Contain("6px"));
        }

        [Test]
        public void Visual_box_keyword_only_no_length_round_trips_through_cascade() {
            // Grammar allows omitting the length (<length [0,∞]>? is optional).
            // Result: visual-box sets the reference edge; length defaults to 0 at resolve.
            var cs = Compute("#x { overflow: clip; overflow-clip-margin: border-box; }");
            string raw = cs.Get("overflow-clip-margin");
            Assert.That(raw, Does.Contain("border-box"),
                "visual-box-only declaration (no length) must survive cascade");
        }
    }
}
