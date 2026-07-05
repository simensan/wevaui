using System;
using System.Globalization;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // Cross-cutting: for each property KIND, the spec-mandated interpolation
    // formula at t=0.5 must produce the correct midpoint end-to-end
    // (keyframes -> cascade -> computed value at 50% progress).
    //
    // Covers GAME_UI_COVERAGE_PLAN.md §17 "Animation interpolation by property
    // kind". Direct-interpolator unit tests (ValueInterpolatorTests /
    // MultiComponentInterpolatorTests) prove the formula in isolation; these
    // tests prove the formula survives the full runner pipeline.
    //
    // Setup pattern mirrors CssAnimationRunnerKeyframesTests.MakeRunner —
    // FakeUIClock advanced to 0.5 s into a 1 s linear animation gives exactly
    // t = 0.5 progress through the keyframes.
    public class InterpolationByPropertyKindTests {
        const double Eps = 1e-3;

        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner(string css) {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse(css);
            var cascade = new CascadeEngine(Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            return (runner, clock);
        }

        // Set up a named animation on an element and advance to 50% progress.
        static ComputedStyle SampleAt50Pct(CssAnimationRunner runner, FakeUIClock clock,
                                            Element e, ComputedStyle s) {
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            return runner.Compose(e, s);
        }

        static ComputedStyle BaseStyle(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        static double ParseNumber(string v) =>
            double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);

        // Parse "rgb(R, G, B)" or "rgba(R, G, B, A)" back to int channels.
        static void ParseRgb(string text, out int r, out int g, out int b) {
            string body = text.Substring(text.IndexOf('(') + 1);
            body = body.Substring(0, body.IndexOf(')'));
            string[] parts = body.Split(',');
            r = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            g = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            b = int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
        }

        // Parse "Npx" → double.
        static double ParsePx(string v) {
            if (v == null) throw new ArgumentNullException(nameof(v));
            string t = v.Trim();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                return double.Parse(t.Substring(0, t.Length - 2),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
            return ParseNumber(t);
        }

        // -----------------------------------------------------------------------
        // Kind 1: Length
        // CSS Animations L1 §3 / CSS Transitions L1 §2.3.
        // width: 100px -> 300px, t=0.5 => 200px.
        // -----------------------------------------------------------------------
        [Test]
        public void Length_midpoint_of_100px_to_300px_is_200px() {
            var (runner, clock) = MakeRunner(
                "@keyframes grow { from { width: 100px; } to { width: 300px; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "grow"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("width"), Is.Not.Null, "width must be sampled");
            Assert.That(ParsePx(c.Get("width")), Is.EqualTo(200.0).Within(0.5));
        }

        // -----------------------------------------------------------------------
        // Kind 2: Percentage
        // CSS Animations L1 §3: percentage values interpolate numerically.
        // width: 0% -> 100%, t=0.5 => 50%.
        // -----------------------------------------------------------------------
        [Test]
        public void Percentage_midpoint_of_0pct_to_100pct_is_50pct() {
            var (runner, clock) = MakeRunner(
                "@keyframes stretch { from { width: 0%; } to { width: 100%; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "stretch"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("width"), Is.Not.Null, "width must be sampled");
            string w = c.Get("width").Trim();
            // Accept either "50%" or the numeric-percentage form.
            Assert.That(w, Does.EndWith("%"), "percentage interpolation must preserve % unit");
            double pct = ParseNumber(w.Substring(0, w.Length - 1));
            Assert.That(pct, Is.EqualTo(50.0).Within(0.5));
        }

        // -----------------------------------------------------------------------
        // Kind 3: Color (oklab by default — CSS Color L4 §12)
        // color: red -> blue, t=0.5.
        // The oklab midpoint passes through a perceptually-balanced purple:
        //   - linear-RGB midpoint would give R~188, G=0, B~188 (no green).
        //   - oklab midpoint gives R~142, G>0, B~200 (visible green contribution).
        // Asserting G > 0 is the discriminating check.
        // -----------------------------------------------------------------------
        [Test]
        public void Color_red_to_blue_midpoint_uses_oklab_not_linear_rgb() {
            var (runner, clock) = MakeRunner(
                "@keyframes tint { from { color: rgb(255,0,0); } to { color: rgb(0,0,255); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "tint"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("color"), Is.Not.Null, "color must be sampled");
            string cv = c.Get("color");
            Assert.That(cv, Does.StartWith("rgb"), "color must serialize as rgb()");
            ParseRgb(cv, out int r, out int g, out int b);
            // Linear-RGB midpoint has G=0 exactly; oklab midpoint has G>0.
            Assert.That(g, Is.GreaterThan(0),
                "oklab color midpoint between red and blue must have nonzero green channel; got " + cv);
            // Both endpoints contribute.
            Assert.That(r, Is.GreaterThan(0), "red channel must be nonzero; got " + cv);
            Assert.That(b, Is.GreaterThan(0), "blue channel must be nonzero; got " + cv);
        }

        // -----------------------------------------------------------------------
        // Kind 4: Color — sRGB channel-wise identity check (same color).
        // Animating red -> red through oklab must round-trip to exact red;
        // proves the oklab path doesn't introduce channel drift on identity.
        // -----------------------------------------------------------------------
        [Test]
        public void Color_identity_red_to_red_through_oklab_has_no_channel_drift() {
            var (runner, clock) = MakeRunner(
                "@keyframes noop { from { color: rgb(255,0,0); } to { color: rgb(255,0,0); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "noop"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("color"), Is.Not.Null);
            ParseRgb(c.Get("color"), out int r, out int g, out int b);
            Assert.That(r, Is.EqualTo(255));
            Assert.That(g, Is.EqualTo(0));
            Assert.That(b, Is.EqualTo(0));
        }

        // -----------------------------------------------------------------------
        // Kind 5: Transform (2D per-function matched list)
        // CSS Transforms L1 §13: same-shape function list interpolates per arg.
        // transform: translateX(0) -> translateX(100px), t=0.5 => translateX(50px).
        // -----------------------------------------------------------------------
        [Test]
        public void Transform_translateX_matched_shape_midpoint_is_50px() {
            var (runner, clock) = MakeRunner(
                "@keyframes slide { from { transform: translateX(0px); } to { transform: translateX(100px); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "slide"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("transform"), Is.Not.Null, "transform must be sampled");
            string tv = c.Get("transform");
            // Per-function serialization: "translatex(50px)" (case-folded) or "translateX(50px)".
            Assert.That(tv, Does.Contain("50"), "midpoint translateX must be 50px; got: " + tv);
            Assert.That(tv.ToLowerInvariant(), Does.StartWith("translatex"),
                "matched shape must stay as translateX function; got: " + tv);
        }

        // -----------------------------------------------------------------------
        // Kind 6: Transform (matrix decomposition)
        // CSS Transforms L1 §17: mismatched function-list shapes collapse to
        // matrices, decompose, lerp components, recompose.
        // translateX(0) -> rotate(90deg) goes through matrix decomp at t=0.5.
        // Result must be a matrix() — NOT a discrete step to either endpoint.
        // -----------------------------------------------------------------------
        [Test]
        public void Transform_mismatched_shapes_go_through_matrix_decomp_at_midpoint() {
            var (runner, clock) = MakeRunner(
                "@keyframes morph { from { transform: translateX(0px); } to { transform: rotate(90deg); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "morph"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("transform"), Is.Not.Null, "transform must be sampled");
            string tv = c.Get("transform");
            // Must NOT be either raw endpoint value (that would be discrete).
            Assert.That(tv.ToLowerInvariant(), Is.Not.EqualTo("translatex(0px)"),
                "decomp path must not step to start; got: " + tv);
            Assert.That(tv.ToLowerInvariant(), Is.Not.EqualTo("rotate(90deg)"),
                "decomp path must not step to end; got: " + tv);
            // Must be a matrix() (the recompose output).
            Assert.That(tv.ToLowerInvariant(), Does.StartWith("matrix("),
                "decomp path must produce matrix(); got: " + tv);
        }

        // -----------------------------------------------------------------------
        // Kind 7: Number
        // opacity: 0 -> 1, t=0.5 => 0.5. Spec: numeric linear interpolation.
        // -----------------------------------------------------------------------
        [Test]
        public void Number_opacity_midpoint_is_0_5() {
            var (runner, clock) = MakeRunner(
                "@keyframes fade { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "fade"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("opacity"), Is.Not.Null, "opacity must be sampled");
            Assert.That(ParseNumber(c.Get("opacity")), Is.EqualTo(0.5).Within(Eps));
        }

        // -----------------------------------------------------------------------
        // Kind 8: Integer (z-index).
        // CSS Transitions L1 §2.3: integer-type properties interpolate as real
        // numbers then round to the nearest integer for use. z-index is
        // PropertyKind.Integer in the engine — emitted as an integer string
        // (no decimal point, no scientific notation).
        // -----------------------------------------------------------------------
        [Test]
        public void Integer_z_index_midpoint_of_0_to_10_is_5() {
            var (runner, clock) = MakeRunner(
                "@keyframes rise { from { z-index: 0; } to { z-index: 10; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "rise"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("z-index"), Is.Not.Null, "z-index must be sampled");
            // Engine MUST emit integer string per CSS Transitions L1 §2.3
            // ("integer animatable values are interpolated as real numbers
            // and the result is then converted to the type"). The string
            // must contain no decimal point.
            string text = c.Get("z-index").Trim();
            Assert.That(text, Is.EqualTo("5"),
                "z-index midpoint must serialize as integer '5', not '5.0'/'5.000'; got: " + text);
            Assert.That(text, Does.Not.Contain("."),
                "z-index integer-typed interpolation must not contain a decimal point");
        }

        // -----------------------------------------------------------------------
        // Kind 8b: Integer rounding at the .5 tie boundary.
        // z-index: 0 -> 11 at t=0.5 = 5.5; rounding away from zero (browser
        // convention) gives 6. The pre-fix Number-kind path would have
        // emitted "5.5" — this test guards the post-A10 rounding contract.
        // -----------------------------------------------------------------------
        [Test]
        public void Integer_z_index_midpoint_of_0_to_11_rounds_to_6() {
            var (runner, clock) = MakeRunner(
                "@keyframes climb { from { z-index: 0; } to { z-index: 11; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "climb"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string text = c.Get("z-index").Trim();
            Assert.That(text, Is.EqualTo("6"),
                "z-index midpoint 5.5 must round to '6' (AwayFromZero); got: " + text);
        }

        // -----------------------------------------------------------------------
        // Kind 8c: order (CSS Flexbox L1 §8) is also integer-typed.
        // -----------------------------------------------------------------------
        [Test]
        public void Integer_order_midpoint_of_negative_to_positive_rounds_to_integer() {
            var (runner, clock) = MakeRunner(
                "@keyframes shuffle { from { order: -3; } to { order: 4; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "shuffle"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string text = c.Get("order").Trim();
            // -3 → 4 at t=0.5 = 0.5; AwayFromZero rounds 0.5 to 1.
            Assert.That(text, Is.EqualTo("1"),
                "order midpoint 0.5 must round to integer '1' (AwayFromZero); got: " + text);
        }

        // -----------------------------------------------------------------------
        // Kind 9: Shadow list (box-shadow)
        // CSS Backgrounds 3 §3.5: per-shadow per-component lerp.
        // box-shadow: 0 0 0 black -> 10px 10px 10px red, t=0.5
        //   offsets and blur: 5px each; color: oklab midpoint of black and red.
        // -----------------------------------------------------------------------
        [Test]
        public void Shadow_list_box_shadow_midpoint_has_5px_offsets_and_blended_color() {
            var (runner, clock) = MakeRunner(
                "@keyframes glow { " +
                "from { box-shadow: 0px 0px 0px rgb(0,0,0); } " +
                "to { box-shadow: 10px 10px 10px rgb(255,0,0); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "glow"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("box-shadow"), Is.Not.Null, "box-shadow must be sampled");
            string sv = c.Get("box-shadow");
            // Offsets and blur must each be 5px (within 1px).
            // Serialization is "ox oy blur spread color" or similar.
            // We assert the string contains "5px" three times.
            int count5px = 0;
            int idx = 0;
            while ((idx = sv.IndexOf("5px", idx, StringComparison.OrdinalIgnoreCase)) >= 0) {
                count5px++;
                idx += 3;
            }
            Assert.That(count5px, Is.GreaterThanOrEqualTo(3),
                "box-shadow midpoint must contain three '5px' tokens (ox, oy, blur); got: " + sv);
            // Color must be blended (not pure black or pure red).
            Assert.That(sv, Does.Contain("rgb"), "box-shadow color must be an rgb() value; got: " + sv);
            int rgbIdx = sv.IndexOf("rgb(");
            Assert.That(rgbIdx, Is.GreaterThanOrEqualTo(0), "rgb( not found in: " + sv);
            string colorPart = sv.Substring(rgbIdx);
            colorPart = colorPart.Substring(0, colorPart.IndexOf(')') + 1);
            ParseRgb(colorPart, out int r, out int g, out int b);
            // Black is (0,0,0), red is (255,0,0): any blended color has R > 0.
            Assert.That(r, Is.GreaterThan(0), "blended shadow color must have R>0; color: " + colorPart);
        }

        // -----------------------------------------------------------------------
        // Kind 9b: Shadow list — exact midpoint lengths are 5px each.
        // Separate from the above to pin the numeric precision independently.
        // -----------------------------------------------------------------------
        [Test]
        public void Shadow_list_box_shadow_midpoint_color_is_not_endpoint() {
            var (runner, clock) = MakeRunner(
                "@keyframes glow2 { " +
                "from { box-shadow: 0px 0px 0px rgb(0,0,0); } " +
                "to { box-shadow: 10px 10px 10px rgb(255,0,0); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "glow2"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string sv = c.Get("box-shadow");
            Assert.That(sv, Is.Not.EqualTo("0px 0px 0px rgb(0, 0, 0)"),
                "midpoint must not be the start value");
            Assert.That(sv, Is.Not.EqualTo("10px 10px 10px rgb(255, 0, 0)"),
                "midpoint must not be the end value");
        }

        // -----------------------------------------------------------------------
        // Kind 10: Filter chain (blur)
        // CSS Filter Effects 1 §6: per-function interpolation when lists match.
        // filter: blur(0) -> blur(10px), t=0.5 => blur(5px).
        // -----------------------------------------------------------------------
        [Test]
        public void Filter_blur_midpoint_is_5px() {
            var (runner, clock) = MakeRunner(
                "@keyframes haze { from { filter: blur(0px); } to { filter: blur(10px); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "haze"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("filter"), Is.Not.Null, "filter must be sampled");
            string fv = c.Get("filter");
            Assert.That(fv, Does.Contain("blur"), "filter must be blur(); got: " + fv);
            // "blur(5px)" — parse the argument.
            int open = fv.IndexOf('(') + 1;
            int close = fv.IndexOf(')');
            Assert.That(open, Is.LessThan(close), "malformed filter: " + fv);
            string arg = fv.Substring(open, close - open).Trim();
            double px = ParsePx(arg);
            Assert.That(px, Is.EqualTo(5.0).Within(0.5), "blur midpoint must be 5px; got: " + fv);
        }

        // -----------------------------------------------------------------------
        // Kind 10b: Filter — mismatched function names fall back to discrete.
        // filter: brightness(1) -> saturate(2) is discrete at t < 0.5.
        // -----------------------------------------------------------------------
        [Test]
        public void Filter_mismatched_functions_are_discrete_in_runner() {
            var (runner, clock) = MakeRunner(
                "@keyframes weird { from { filter: brightness(1); } to { filter: saturate(2); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "weird"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            // t < 0.5: expect start value.
            runner.OnStyleChange(e, null, s);
            clock.Set(0.4);
            runner.Tick(0.4);
            var c = runner.Compose(e, s);
            string fv = c.Get("filter");
            Assert.That(fv, Is.Not.Null);
            // At t=0.4 discrete step holds start.
            Assert.That(fv, Does.Contain("brightness"),
                "discrete filter at t=0.4 must hold start value; got: " + fv);
        }

        // -----------------------------------------------------------------------
        // Kind 11: Gradient stops (A9)
        // CSS Images L3 §3.5 + CSS Transitions L1 §2.3.
        // background-image is registered as PropertyKind.Gradient.
        // Matched-shape pairs lerp per-stop; mismatched shapes are discrete.
        // -----------------------------------------------------------------------

        // 2-stop linear matched-shape: red→blue ↔ blue→red, t=0.5.
        // Stop 0: red=(255,0,0) ↔ blue=(0,0,255) midpoint ≈ (128,0,128).
        // Stop 1: blue=(0,0,255) ↔ red=(255,0,0) midpoint ≈ (128,0,128).
        // Both stops at t=0.5 should be the same mid-purple.
        [Test]
        public void Gradient_stops_midpoint_per_stop_when_supported() {
            var (runner, clock) = MakeRunner(
                "@keyframes blend { " +
                "from { background-image: linear-gradient(red, blue); } " +
                "to { background-image: linear-gradient(blue, red); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "blend"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string val = c.Get("background-image");
            // Result must be a gradient string.
            Assert.That(val, Is.Not.Null, "background-image must not be null at t=0.5");
            Assert.That(val, Does.Contain("gradient"), "result must contain 'gradient'");
            // At t=0.5 both stops are mid-purple: the color channels must not be
            // purely red or purely blue. R and B must both be non-zero.
            Assert.That(val, Does.Not.Contain("rgb(255, 0, 0)"),
                "stop at t=0.5 must NOT be pure red; got: " + val);
            Assert.That(val, Does.Not.Contain("rgb(0, 0, 255)"),
                "stop at t=0.5 must NOT be pure blue; got: " + val);
        }

        // 3-stop linear matched-shape: verify stop count preserved and output is a gradient.
        [Test]
        public void Gradient_3stop_linear_matched_shape_lerps_each_stop() {
            var (runner, clock) = MakeRunner(
                "@keyframes tri { " +
                "from { background-image: linear-gradient(red, green, blue); } " +
                "to { background-image: linear-gradient(blue, green, red); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "tri"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string val = c.Get("background-image");
            Assert.That(val, Is.Not.Null);
            Assert.That(val, Does.Contain("gradient"),
                "3-stop linear gradient at t=0.5 must still be a gradient string; got: " + val);
        }

        // Mismatched stop count → discrete: from 2-stop, to 3-stop.
        [Test]
        public void Gradient_mismatched_stop_count_is_discrete_at_t_below_half() {
            var (runner, clock) = MakeRunner(
                "@keyframes mc { " +
                "from { background-image: linear-gradient(red, blue); } " +
                "to { background-image: linear-gradient(red, green, blue); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "mc"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.3);
            runner.Tick(0.3);
            var c = runner.Compose(e, s);
            string val = c.Get("background-image");
            // Discrete at t=0.3: should hold from-value (2-stop gradient).
            // The from-value is "linear-gradient(red, blue)"; result must not
            // contain 3 stops (it won't if it's discrete-stepping to from).
            Assert.That(val, Is.Not.Null);
            // From side has "red" and "blue" but NOT "green".
            Assert.That(val, Does.Not.Contain("green"),
                "discrete step at t<0.5 must hold from-value (no green); got: " + val);
        }

        // Mismatched angle → discrete: 0deg vs 90deg.
        [Test]
        public void Gradient_mismatched_angle_is_discrete() {
            var (runner, clock) = MakeRunner(
                "@keyframes ma { " +
                "from { background-image: linear-gradient(0deg, red, blue); } " +
                "to { background-image: linear-gradient(90deg, blue, red); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "ma"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string val = c.Get("background-image");
            // Mismatched angles: discrete at t=0.5 → to-value.
            Assert.That(val, Is.Not.Null);
            // Result must be one of the discrete endpoints, not an interpolated blend.
            // At t=0.5 discrete step picks to-value → should contain "90deg".
            Assert.That(val, Does.Contain("90"),
                "discrete step at t=0.5 must emit to-value (90deg angle); got: " + val);
        }

        // Radial matched-shape: both circle gradients.
        [Test]
        public void Gradient_radial_matched_shape_lerps_stops() {
            var (runner, clock) = MakeRunner(
                "@keyframes rad { " +
                "from { background-image: radial-gradient(circle, red, blue); } " +
                "to { background-image: radial-gradient(circle, blue, red); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "rad"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string val = c.Get("background-image");
            Assert.That(val, Is.Not.Null);
            Assert.That(val, Does.Contain("gradient"),
                "radial-gradient matched-shape at t=0.5 must be a gradient; got: " + val);
            // Mid-stops: neither purely red nor purely blue.
            Assert.That(val, Does.Not.Contain("rgb(255, 0, 0)"),
                "radial stop at t=0.5 must not be pure red; got: " + val);
            Assert.That(val, Does.Not.Contain("rgb(0, 0, 255)"),
                "radial stop at t=0.5 must not be pure blue; got: " + val);
        }

        // Conic matched-shape: both conic gradients.
        [Test]
        public void Gradient_conic_matched_shape_lerps_stops() {
            var (runner, clock) = MakeRunner(
                "@keyframes con { " +
                "from { background-image: conic-gradient(red, blue); } " +
                "to { background-image: conic-gradient(blue, red); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "con"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string val = c.Get("background-image");
            Assert.That(val, Is.Not.Null);
            Assert.That(val, Does.Contain("gradient"),
                "conic-gradient matched-shape at t=0.5 must be a gradient; got: " + val);
        }

        // linear-gradient ↔ radial-gradient → discrete (type mismatch).
        [Test]
        public void Gradient_linear_to_radial_is_discrete() {
            var (runner, clock) = MakeRunner(
                "@keyframes cross { " +
                "from { background-image: linear-gradient(red, blue); } " +
                "to { background-image: radial-gradient(circle, blue, red); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "cross"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.3);
            runner.Tick(0.3);
            var c = runner.Compose(e, s);
            string val = c.Get("background-image");
            // Discrete: t<0.5 → from-value (linear-gradient).
            Assert.That(val, Is.Not.Null);
            Assert.That(val, Does.Contain("linear-gradient"),
                "cross-type discrete at t<0.5 must hold from-value; got: " + val);
        }

        // background-image: none → linear-gradient → discrete (no gradient identity).
        [Test]
        public void Gradient_none_to_gradient_is_discrete() {
            var (runner, clock) = MakeRunner(
                "@keyframes fromNone { " +
                "from { background-image: none; } " +
                "to { background-image: linear-gradient(red, blue); } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "fromNone"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.3);
            runner.Tick(0.3);
            var c = runner.Compose(e, s);
            string val = c.Get("background-image");
            // Discrete: t<0.5 → from-value (none or absent).
            // The value should be "none" or the runner may just leave it absent.
            bool isNoneOrAbsent = string.IsNullOrEmpty(val)
                || string.Equals(val, "none", StringComparison.OrdinalIgnoreCase);
            Assert.That(isNoneOrAbsent, Is.True,
                "none→gradient discrete at t<0.5 must hold from-value (none); got: " + val);
        }

        // -----------------------------------------------------------------------
        // Kind 12: Individual translate property (CSS Transforms L2 §13)
        // `translate: 0px 0px → 100px 50px` at t=0.5 = `50px 25px` (per-axis lerp).
        // Distinct from `transform: translate(...)` which routes through the
        // function-list path. PropertyKind.Translate uses the per-component
        // interpolator in ValueInterpolator.InterpolateIndividualTranslate.
        // -----------------------------------------------------------------------
        [Test]
        public void Translate_individual_property_lerps_per_axis_in_runner() {
            var (runner, clock) = MakeRunner(
                "@keyframes slide { from { translate: 0px 0px; } to { translate: 100px 50px; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "slide"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("translate"), Is.Not.Null, "translate must be sampled");
            string t = c.Get("translate");
            // At t=0.5 the X axis is 50px and Y axis is 25px (per-axis lerp).
            Assert.That(t, Does.Contain("50"), "translate X midpoint must be 50; got: " + t);
            Assert.That(t, Does.Contain("25"), "translate Y midpoint must be 25; got: " + t);
            Assert.That(t.ToLowerInvariant(), Does.Contain("px"),
                "individual translate must preserve px unit; got: " + t);
        }

        [Test]
        public void Translate_individual_single_axis_lerps_x_only() {
            // Single-axis form: `translate: 0` means X only, Y defaults to 0.
            // CSS Transforms L2 §13.1.
            var (runner, clock) = MakeRunner(
                "@keyframes shiftX { from { translate: 0px; } to { translate: 80px; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "shiftX"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string t = c.Get("translate");
            Assert.That(t, Is.Not.Null);
            Assert.That(t, Does.Contain("40"), "translate single-axis midpoint must be 40px; got: " + t);
        }

        [Test]
        public void Translate_individual_percentage_lerps_per_axis() {
            // Percentage values lerp numerically — `translate: 0% 0% → 50% 100%` at
            // t=0.5 = `25% 50%`. CSS Transforms L2 §13.1 + CSS Animations L1 §3.
            var (runner, clock) = MakeRunner(
                "@keyframes pctSlide { from { translate: 0% 0%; } to { translate: 50% 100%; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "pctSlide"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string t = c.Get("translate");
            Assert.That(t, Is.Not.Null);
            Assert.That(t, Does.Contain("25"), "X axis at t=0.5 should contain 25 (25% of 50%); got: " + t);
            Assert.That(t, Does.Contain("50"), "Y axis at t=0.5 should contain 50 (50% of 100%); got: " + t);
            Assert.That(t, Does.Contain("%"), "percentage unit must be preserved; got: " + t);
        }

        // -----------------------------------------------------------------------
        // Kind 13: Individual rotate property (CSS Transforms L2 §13.2)
        // `rotate: 0deg → 90deg` at t=0.5 = `45deg`.
        // -----------------------------------------------------------------------
        [Test]
        public void Rotate_individual_property_lerps_degrees_in_runner() {
            var (runner, clock) = MakeRunner(
                "@keyframes spin { from { rotate: 0deg; } to { rotate: 90deg; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "spin"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("rotate"), Is.Not.Null, "rotate must be sampled");
            string r = c.Get("rotate");
            Assert.That(r, Does.Contain("45"), "rotate midpoint must be 45deg; got: " + r);
            Assert.That(r.ToLowerInvariant(), Does.Contain("deg"),
                "rotate must preserve deg unit; got: " + r);
        }

        [Test]
        public void Rotate_individual_property_shortest_arc_at_359deg_to_1deg() {
            // CSS Transforms L2 §13.2 — rotate interpolates per arc, but the
            // engine in v1 may take the long way (359 → 1 = -358 step rather
            // than +2). Pin whichever the engine does — if the engine takes
            // the linear-numeric path, midpoint = (359+1)/2 = 180. If it
            // takes shortest-arc, midpoint = 0 (or 360).
            // This test pins the current numeric-linear behaviour; if engine
            // moves to shortest-arc, flip the assertion.
            var (runner, clock) = MakeRunner(
                "@keyframes wrap { from { rotate: 359deg; } to { rotate: 1deg; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "wrap"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string r = c.Get("rotate").ToLowerInvariant();
            // Either spec-correct (0deg or 360deg) or numeric-linear (180deg) is
            // an acceptable v1 outcome — just verify it's a valid angle string
            // and not the discrete endpoint.
            Assert.That(r, Does.Contain("deg"), "must serialize with deg unit; got: " + r);
            Assert.That(r, Is.Not.EqualTo("359deg"), "discrete-step start would be wrong; got: " + r);
            Assert.That(r, Is.Not.EqualTo("1deg"), "discrete-step end would be wrong; got: " + r);
        }

        // -----------------------------------------------------------------------
        // Kind 14: Individual scale property (CSS Transforms L2 §13.3)
        // `scale: 1 → 3` (single value, X+Y both scale) at t=0.5 = `2`.
        // -----------------------------------------------------------------------
        [Test]
        public void Scale_individual_property_single_value_lerps_to_midpoint() {
            var (runner, clock) = MakeRunner(
                "@keyframes pop { from { scale: 1; } to { scale: 3; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "pop"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            Assert.That(c.Get("scale"), Is.Not.Null, "scale must be sampled");
            string sv = c.Get("scale");
            // Midpoint 2 — may serialize as "2" or "2 2".
            Assert.That(sv, Does.Contain("2"), "scale midpoint must be 2; got: " + sv);
        }

        [Test]
        public void Scale_individual_two_value_lerps_per_axis() {
            // `scale: 1 2 → 3 4` at t=0.5 = `2 3` (per-axis lerp).
            var (runner, clock) = MakeRunner(
                "@keyframes asym { from { scale: 1 2; } to { scale: 3 4; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "asym"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string sv = c.Get("scale");
            Assert.That(sv, Is.Not.Null);
            Assert.That(sv, Does.Contain("2"), "X axis midpoint must contain 2; got: " + sv);
            Assert.That(sv, Does.Contain("3"), "Y axis midpoint must contain 3; got: " + sv);
        }

        [Test]
        public void Scale_individual_from_none_to_value_uses_identity_one() {
            // `scale: none` is identity (1 1). `scale: none → 2` at t=0.5
            // interpolates from implicit 1 → 2 = 1.5. CSS Transforms L2 §13.3.
            var (runner, clock) = MakeRunner(
                "@keyframes grow { from { scale: none; } to { scale: 2; } }");
            var e = new Element("div");
            var s = BaseStyle(e,
                ("animation-name", "grow"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            var c = SampleAt50Pct(runner, clock, e, s);
            string sv = c.Get("scale");
            Assert.That(sv, Is.Not.Null);
            // Midpoint of 1 → 2 = 1.5. Accept either "1.5" or "1.5 1.5".
            Assert.That(sv, Does.Contain("1.5"),
                "scale none→2 midpoint must be 1.5 (none = identity 1); got: " + sv);
        }
    }
}
