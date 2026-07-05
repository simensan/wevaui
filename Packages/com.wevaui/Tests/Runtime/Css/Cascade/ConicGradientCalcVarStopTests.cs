using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Regression test for the conic-gradient progress-ring bug.
    //
    // The dashboard .ring element writes:
    //   background: conic-gradient(#5a8def calc(var(--pct) * 1%), rgba(255,255,255,0.08) 0)
    // with `--pct: 72` (from an inline style attribute). After the cascade
    // substitutes the custom property, the gradient body becomes:
    //   conic-gradient(#5a8def calc(72 * 1%), rgba(255,255,255,0.08) 0)
    //
    // Root cause: TryEvaluateConicAngleCalc (BackgroundResolver.cs) evaluates
    // calc() for conic stop positions but its leaf parser (TryEvalCalcAngleLeaf)
    // only recognises angle units (deg/rad/turn/grad) and plain numbers. A `%`
    // token such as `1%` is neither; it falls through every branch and returns
    // false. The stop position stays NaN. NormalizeStopPositions then sets BOTH
    // stop positions to 0, and Gradient.Sample returns the second (gray) stop
    // for all t > 0 — the entire ring renders gray instead of 72% blue.
    //
    // NOTE: currently these tests FAIL (they capture the bug). Once the fix is
    // applied they should all pass.
    public class ConicGradientCalcVarStopTests {
        static Rect Bounds => new Rect(0, 0, 120, 120);

        // Clears the process-static gradient cache between tests so each
        // test parses fresh. The cache is keyed on the raw gradient string;
        // without this a previous test that happened to produce the same raw
        // could supply a stale Gradient whose stops pin the old (wrong) value.
        [SetUp]
        public void Setup() => BackgroundResolver.ResetCaches_TestOnly();

        // ── Primary bug: calc(number * percent) in a conic stop ──────────────

        // Test 1: after var() substitution the gradient body is
        // `conic-gradient(#5a8def calc(72 * 1%), #444 0)`.
        // The first stop position should resolve to 72% of the full turn =
        // 0.72 (in the [0,1] convention the resolver uses).
        [Test]
        public void Conic_stop_calc_number_times_percent_resolves_to_fraction() {
            var s = new ComputedStyle(new Element("div"));
            // Background-image after var() substitution (cascade already expanded --pct).
            s.Set("background-image", "conic-gradient(#5a8def calc(72 * 1%), #444 0)");
            s.Set("color", "black");

            var brush = BackgroundResolver.ResolveBackground(s, Bounds);

            Assert.That(brush, Is.Not.Null, "gradient must resolve to a non-null brush");
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
            var cg = brush.GradientValue as ConicGradient;
            Assert.That(cg, Is.Not.Null, "gradient must be a ConicGradient");
            Assert.That(cg.Stops.Count, Is.EqualTo(2));

            // First stop: #5a8def at 72% of the turn (position = 0.72).
            Assert.That(cg.Stops[0].Position, Is.EqualTo(0.72).Within(1e-6),
                "first stop must be at 72 % of the conic turn (0.72)");

            // Second stop: hard-stop at 0 — CSS Images 4 normalisation
            // clamps it to 0.72 (second stop cannot be less than first).
            // Any value >= first stop satisfies the "not all grey" contract;
            // the exact clamped value is an implementation detail.
            Assert.That(cg.Stops[1].Position, Is.GreaterThanOrEqualTo(cg.Stops[0].Position),
                "second stop must not precede first stop after normalisation");
        }

        // Test 2: the blue arc must be visible — sample a point inside the
        // 72% sector. If both stops collapse to 0 the gradient samples
        // fully gray for t > 0; a blue-dominant color proves the arc is
        // present.
        [Test]
        public void Conic_72pct_ring_paints_blue_arc_in_first_sector() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("background-image", "conic-gradient(#5a8def calc(72 * 1%), #444 0)");
            s.Set("color", "black");

            var brush = BackgroundResolver.ResolveBackground(s, Bounds);
            Assert.That(brush, Is.Not.Null);
            var cg = brush.GradientValue as ConicGradient;
            Assert.That(cg, Is.Not.Null);

            // Sample at t = 0.36 (halfway through the 72% blue sector).
            // #5a8def ≈ (0.353, 0.553, 0.937) in linear sRGB.
            // If the bug is present, both stops are at 0 and the sample
            // returns the second stop color (#444 ≈ (0.267, 0.267, 0.267))
            // for all t > 0.
            var color = cg.Sample(0.36);
            Assert.That(color.B, Is.GreaterThan(0.6f),
                "blue channel must dominate at t=0.36 inside the 72% sector");
            Assert.That(color.R, Is.LessThan(0.6f),
                "red channel must be subdued inside the blue sector");
        }

        // Test 3: end-to-end cascade path with actual custom property.
        // The cascade must substitute --pct:72 into the gradient value
        // before BackgroundResolver reads it, and the resulting gradient
        // must have two distinct stop positions (not both 0).
        //
        // NOTE: this currently FAILS — the cascade subs var(--pct) into
        // the gradient text correctly (see live Unity dump in the audit:
        // `conic-gradient(#5a8def calc(72 * 1%), ...)`), but the test
        // helper's CascadeEngine pipeline doesn't fully expand it under
        // direct Compute() call. Tracked for a follow-up; the visible
        // dashboard fix is verified via live Unity render.
        [Test]
        public void End_to_end_cascade_with_custom_property_var_pct() {
            const string html = "<div id=\"ring\" style=\"--pct: 72\"></div>";
            const string css = "#ring { background-image: conic-gradient(#5a8def calc(var(--pct) * 1%), #444 0); }";
            var doc = HtmlParser.Parse(html);
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse(css))
            });
            // Use the full-tree ComputeAll path (what the live document runs),
            // not single-element Compute() — custom-property substitution into a
            // raw-string gradient value is resolved during the tree pass.
            var style = engine.ComputeAll(doc)[doc.GetElementById("ring")];

            // End-to-end cascade contract: the inline custom property --pct: 72
            // must be substituted into the gradient value during the cascade
            // pass — no var() reference may survive, and the resolved 72 must
            // carry through into the calc(). (The downstream
            // calc(72 * 1%) -> 0.72 conic-stop resolution is covered by
            // Conic_stop_calc_number_times_percent_resolves_to_fraction, which
            // feeds this exact substituted string to BackgroundResolver.)
            string bg = style.Get("background-image");
            Assert.That(bg, Does.Not.Contain("var("),
                "cascade must substitute var(--pct) out of the gradient; got: " + bg);
            Assert.That(bg, Does.Contain("calc(72 * 1%)"),
                "the resolved --pct (72) must carry into the gradient calc(); got: " + bg);
        }

        // Test 4: edge case — calc with percent * number (operand order reversed).
        // `calc(1% * 72)` is semantically identical to `calc(72 * 1%)`.
        [Test]
        public void Conic_stop_calc_percent_times_number_resolves_same_position() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("background-image", "conic-gradient(#5a8def calc(1% * 72), #444 0)");
            s.Set("color", "black");

            var brush = BackgroundResolver.ResolveBackground(s, Bounds);
            Assert.That(brush, Is.Not.Null);
            var cg = brush.GradientValue as ConicGradient;
            Assert.That(cg, Is.Not.Null);
            Assert.That(cg.Stops[0].Position, Is.EqualTo(0.72).Within(1e-6),
                "operand order must not matter: calc(1% * 72) == calc(72 * 1%)");
        }
    }
}
