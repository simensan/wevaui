using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Audit (2026-05-16): CSS_FEATURES.md lists `letter-spacing` AND
    // `word-spacing` as ✅ supported, yet the only existing behavioural
    // coverage is three single-line `letter-spacing` tests in
    // InlineLayoutTests (Letter_spacing_adds_width_to_text_run /
    // _em_resolves_against_font_size / _normal_is_zero) plus a parse
    // round-trip in TextPropertyIntegrationTests. None of the
    // line-breaking, inheritance, white-space, or word-spacing
    // interactions are covered.
    //
    // This file exercises the spec contract from CSS Text Module 3 §10
    // (Spacing) against the engine's MonoFontMetrics pipeline:
    //   - §10.1 letter-spacing applies a per-glyph advance and is the
    //     primary driver pushing words past the available width.
    //   - §10.2 word-spacing applies extra advance to "word separator
    //     character" runs (U+0020, U+00A0, U+1361, U+10100, U+10101,
    //     U+1039F, U+1091F).
    //   - §10.4 spacing is INHERITED (CssProperties registers both with
    //     inherit:true).
    //
    // MonoFontMetrics defaults: charWidthEm = 0.5 ⇒ 8 px per char @
    // 16px font-size. LineBreaker's MeasureCached adds
    // `LetterSpacingPx * (text.Length - 1)` to the natural advance per
    // CSS Text 3 §10.1 (spacing BETWEEN adjacent characters): 3 chars
    // add 2× spacing, not 3×. Trailing-character spacing is suppressed.
    public class LetterSpacingLineBreakingTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static List<LineBox> LinesOf(BlockBox p) {
            var lines = new List<LineBox>();
            foreach (var c in p.Children) if (c is LineBox lb) lines.Add(lb);
            return lines;
        }

        static List<TextRun> RunsUnder(Box root) {
            var list = new List<TextRun>();
            foreach (var b in AllBoxes(root)) if (b is TextRun tr) list.Add(tr);
            return list;
        }

        // ------------------------------------------------------------------
        // letter-spacing — line breaking integration
        // ------------------------------------------------------------------

        // CSS Text 3 §10.1 + §4.1: when `letter-spacing` inflates a word's
        // advance past the available content width the line breaker must
        // wrap on the next break opportunity. Natural width of "ab cd ef"
        // is 64 px (8 chars * 8); with letter-spacing 6px each 2-char word
        // grows by 6 (one inter-letter gap), 3 words ⇒ +18 ⇒ ~82 + 2
        // spaces (16) = ~82 px ⇒ wrap required at width 70.
        [Test]
        public void Letter_spacing_forces_wrap_when_spaced_width_exceeds_container() {
            var (rootNarrow, _, _) = Build(
                "<p style=\"letter-spacing:6px;width:70px\">ab cd ef</p>", null, 800);
            var pNarrow = FindFirstBlock(rootNarrow, "p");
            Assert.That(LinesOf(pNarrow).Count, Is.GreaterThan(1),
                "letter-spacing should inflate measured text width and force wrapping");

            // Sanity: same paragraph at letter-spacing:normal fits on one line.
            var (rootCtrl, _, _) = Build(
                "<p style=\"letter-spacing:normal;width:80px\">ab cd ef</p>", null, 800);
            var pCtrl = FindFirstBlock(rootCtrl, "p");
            Assert.That(LinesOf(pCtrl).Count, Is.EqualTo(1),
                "control: same paragraph without spacing must fit");
        }

        // CSS Text 3 §10.4: letter-spacing is an INHERITED property.
        // CssProperties.cs registers letter-spacing with inherit:true; a
        // child <span> with no own letter-spacing must measure with the
        // parent's resolved value.
        [Test]
        public void Letter_spacing_inherits_through_inline_descendant() {
            // outer .x sets letter-spacing:4px; inner <span> measures via
            // inheritance. "xyz" natural = 24 px; spaced = 24 + 4*2 = 32
            // (3 chars produce 2 inter-letter gaps per CSS Text 3 §10.1).
            var (root, _, _) = Build(
                "<p class=\"x\"><span>xyz</span></p>",
                ".x { letter-spacing: 4px; }",
                viewportWidth: 800);
            var p = FindFirstBlock(root, "p");
            TextRun spanRun = null;
            foreach (var tr in RunsUnder(p)) if (tr.Text == "xyz") { spanRun = tr; break; }
            Assert.That(spanRun, Is.Not.Null);
            Assert.That(spanRun.Width, Is.EqualTo(32).Within(0.001));
        }

        // CSS Text 3 §10.1: <length> values may be negative ("Negative
        // values are allowed, but there may be implementation-dependent
        // limits"). Engine formula is `natural + spacing * (length - 1)`,
        // so -2px on "abcd" (32 px natural, 3 inter-letter gaps) yields
        // 32 + -2*3 = 26 px — strictly less than the natural run.
        [Test]
        public void Letter_spacing_negative_value_shrinks_measured_width() {
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:-2px\">abcd</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var tr in RunsUnder(p)) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Width, Is.EqualTo(26).Within(0.001),
                "negative letter-spacing should subtract from each inter-letter gap");
        }

        // CSS Text 3 §10.1: letter-spacing inflates the run width even
        // inside `white-space: pre`, because `pre` only suppresses
        // collapsing/wrapping, not glyph advance. "abc" preserved verbatim
        // ⇒ 24 + 2*2 = 28 px (3 chars produce 2 inter-letter gaps).
        [Test]
        public void Letter_spacing_applies_under_white_space_pre() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;letter-spacing:2px\">abc</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var tr in RunsUnder(p)) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Width, Is.EqualTo(28).Within(0.001));
        }

        // CSS Text 3 §10.1: letter-spacing measured-width must scale per
        // run independently when nested inline elements declare different
        // values. Outer p has 2px, inner <strong> has 6px (no inheritance
        // because the more-specific declaration overrides). The two runs
        // — outer "hi " and inner "yo" — should land on different
        // per-glyph budgets.
        [Test]
        public void Letter_spacing_per_run_when_inline_overrides_parent() {
            // Outer p declares 2px; inner <strong> declares 6px on its own
            // rule — the strong run resolves to the more-specific value
            // rather than inheriting the parent's 2px. The inner run text
            // "yo" is 2 chars (1 inter-letter gap) ⇒ 16 + 6*1 = 22 px.
            // Differs from the outer parent value (which would yield
            // 16 + 2*1 = 18), so a single point assertion proves the
            // per-run cascade wiring.
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:2px\">x <strong style=\"letter-spacing:6px\">yo</strong></p>",
                null, viewportWidth: 800);
            var p = FindFirstBlock(root, "p");
            TextRun innerRun = null;
            foreach (var tr in RunsUnder(p)) if (tr.Text == "yo") { innerRun = tr; break; }
            Assert.That(innerRun, Is.Not.Null, "inner 'yo' run missing");
            Assert.That(innerRun.Width, Is.EqualTo(22).Within(0.001),
                "strong-scoped 6px must override inherited 2px on its own run");
        }

        // ------------------------------------------------------------------
        // word-spacing — declared layout-affecting but not consumed
        // ------------------------------------------------------------------

        // v1 GAP: CSS_FEATURES.md lists `word-spacing` as ✅, and
        // CssProperties.cs registers it with inherit:true,
        // LayoutAffectingProperties includes it in the relayout-trigger
        // set, AND PropertyKindRegistry includes it in the length-typed
        // properties — but a runtime grep shows NO consumer in
        // Layout/Text/Paint reads the property. The expected CSS Text 3
        // §10.2 behaviour is that "ab cd" (5 chars * 8 = 40 px natural)
        // with `word-spacing: 8px` should measure 48 px because the
        // CSS Text 3 §10.2: word-spacing adds extra advance to every
        // word-separator (U+0020) in the run. "ab cd" contains one space,
        // so word-spacing:8px adds +8 px on top of the 40 px natural width.
        [Test]
        public void WordSpacing_adds_advance_to_each_inter_word_space() {
            var (root, _, _) = Build(
                "<p style=\"word-spacing:8px\">ab cd</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            double total = 0;
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            foreach (var c in line.Children) if (c is TextRun tr) total += tr.Width;
            // Natural "ab cd" at 8 px/char = 40 px; +1 space * 8 px = 48 px.
            Assert.That(total, Is.EqualTo(48).Within(0.001));
        }

        // The companion to the v1 GAP: even though word-spacing has no
        // effect on layout width, the cascade must still surface the
        // author value so a future fixer can wire it in without re-doing
        // the parse path. This pins the round-trip.
        [Test]
        public void WordSpacing_value_survives_the_cascade() {
            var (_, styles, _) = Build(
                "<p id=\"x\" style=\"word-spacing:8px\">a b</p>", null, 800);
            // Locate the <p id="x"> element via the styles map keyed by
            // the Element instances Build() cascaded over — keeps the
            // assertion independent of layout-pass mutations.
            Weva.Css.Cascade.ComputedStyle cs = null;
            foreach (var kv in styles) {
                if (kv.Key.Id == "x") { cs = kv.Value; break; }
            }
            Assert.That(cs, Is.Not.Null);
            Assert.That(cs.Get("word-spacing"), Is.EqualTo("8px"));
        }

        // ------------------------------------------------------------------
        // letter-spacing — value-type edge cases
        // ------------------------------------------------------------------

        // CSS Values & Units §6.1: `rem` resolves against the ROOT
        // element's font-size, not the current element's. LayoutContext
        // pins RootFontSizePx = 16, so 0.25rem = 4px regardless of any
        // local font-size override. Existing letter-spacing em test
        // covers element-relative resolution; this test covers the
        // root-relative path through ToLengthContext.
        [Test]
        public void Letter_spacing_rem_resolves_against_root_font_size() {
            // Local font-size:32px wouldn't change rem resolution; 0.25rem
            // still resolves to 0.25 * 16 = 4 px. "ab" natural at 32px
            // (mono 0.5em) = 32; spaced = 32 + 4*1 = 36 (2 chars produce
            // 1 inter-letter gap per CSS Text 3 §10.1).
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:0.25rem;font-size:32px\">ab</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var tr in RunsUnder(p)) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Width, Is.EqualTo(36).Within(0.001));
        }

        // CSS Values 4: calc() must work anywhere a <length> is accepted.
        // 1px + 1px = 2px; "abc" (24 px natural) + 2*2 = 28 px (3 chars
        // produce 2 inter-letter gaps).
        [Test]
        public void Letter_spacing_calc_resolves_at_layout_time() {
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:calc(1px + 1px)\">abc</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var tr in RunsUnder(p)) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Width, Is.EqualTo(28).Within(0.001));
        }

        // CSS Text 3 §10.1: the value `0` (a length, not a keyword) must
        // behave identically to `normal`. Guards against an over-eager
        // resolver that treats `0` as falsy and short-circuits but only
        // for the keyword path.
        [Test]
        public void Letter_spacing_zero_length_matches_normal() {
            var (rootZero, _, _) = Build(
                "<p style=\"letter-spacing:0\">abcd</p>", null, 800);
            var pZero = FindFirstBlock(rootZero, "p");
            TextRun zRun = null;
            foreach (var tr in RunsUnder(pZero)) { zRun = tr; break; }
            Assert.That(zRun, Is.Not.Null);
            Assert.That(zRun.Width, Is.EqualTo(32).Within(0.001),
                "letter-spacing:0 should produce zero extra advance");
        }

        // CSS Text 3 §10.1: letter-spacing applies between EVERY pair of
        // adjacent typographic characters in an inline box — including
        // word/space boundaries. The user-visible contract is line.Width:
        // for "ab cd" (5 chars) with letter-spacing:2px and MonoFontMetrics
        // (8 px/char), the line must be 5 × 8 + 4 × 2 = 48 px wide.
        //
        // The engine has two layout paths and both must hit the same final
        // line.Width and same rightmost-glyph X extent:
        //   - Fast path (`InlineLayout.TryLayoutSingleRunFast`) emits a
        //     single TextRun spanning the whole text. MeasureCached on the
        //     full string includes every inter-character gap natively
        //     (the `LetterSpacingPx * (text.Length - 1)` term in
        //     LineBreaker.MeasureCached covers all N-1 gaps).
        //   - Slow path (`AppendCollapsing` + `LineBreaker.AddFragment`)
        //     tokenises into word/space fragments and re-adds the inter-
        //     fragment gap via `SeamLetterSpacing`. Each fragment becomes
        //     its own TextRun; per-fragment Width is intra-fragment only
        //     and the seam lives in the next fragment's X offset.
        // Both paths converge on the same visible width. The TextRun
        // representation (1 vs 3 runs) is an implementation detail of the
        // box tree — not a CSS-spec contract. So this test pins the spec
        // contract (line.Width + rightmost glyph extent) and tolerates
        // either representation.
        //
        // Regression guard: a single unbreakable token "abcd" still
        // measures 32 + 2×3 = 38 px (one fragment, no seams) — guards
        // against an over-eager fix that double-counts within a fragment.
        [Test]
        public void Letter_spacing_applies_at_inter_fragment_seams() {
            var (rootSpaced, _, _) = Build(
                "<p style=\"letter-spacing:2px\">ab cd</p>", null, 800);
            var pSpaced = FindFirstBlock(rootSpaced, "p");
            var linesSpaced = LinesOf(pSpaced);
            Assert.That(linesSpaced.Count, Is.EqualTo(1), "ab cd must fit on one line at 800px");
            Assert.That(linesSpaced[0].Width, Is.EqualTo(48).Within(0.001),
                "spec contract: line.Width = N×glyph_width + (N-1)×letter-spacing = 5×8 + 4×2 = 48");

            // The rightmost glyph extent (last run's X + Width) must reach
            // 48 too — verifies the engine isn't compensating for an
            // under-counted line.Width by stretching the LAST run, and
            // catches the slow-path bug where the seam offset would be
            // lost between fragments.
            double maxRight = 0;
            foreach (var tr in RunsUnder(pSpaced)) {
                double right = tr.X + tr.Width;
                if (right > maxRight) maxRight = right;
            }
            Assert.That(maxRight, Is.EqualTo(48).Within(0.001),
                "rightmost glyph extent must reach the spec-correct line width " +
                "regardless of whether the engine took the fast (1-run) or slow (3-run) path");

            var (rootSingle, _, _) = Build(
                "<p style=\"letter-spacing:2px\">abcd</p>", null, 800);
            var pSingle = FindFirstBlock(rootSingle, "p");
            TextRun singleRun = null;
            foreach (var tr in RunsUnder(pSingle)) { singleRun = tr; break; }
            Assert.That(singleRun, Is.Not.Null);
            Assert.That(singleRun.Width, Is.EqualTo(38).Within(0.001),
                "regression guard: single unbreakable fragment still uses (length-1) gaps only");

            var linesSingle = LinesOf(pSingle);
            Assert.That(linesSingle.Count, Is.EqualTo(1));
            Assert.That(linesSingle[0].Width, Is.EqualTo(38).Within(0.001),
                "single-fragment line width must match the run width — no phantom seam gap added at the start of the line");
        }

        // CSS Text 3 §10.1: a single unbreakable word's measured advance
        // includes its letter-spacing accumulation. Sanity: when the
        // container has room (width=64 ≥ 40), no wrapping occurs even
        // though spacing inflates the run beyond its natural width.
        [Test]
        public void Letter_spacing_keeps_single_word_on_one_line_when_room_remains() {
            // "abcd" natural = 32 px; +2*3 = 38 px (4 chars produce 3
            // inter-letter gaps). width=64 leaves room.
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:2px;width:64px\">abcd</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.EqualTo(1), "single word fitting in width must not wrap");
            TextRun first = null;
            foreach (var tr in RunsUnder(p)) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Width, Is.EqualTo(38).Within(0.001));
        }
    }
}
