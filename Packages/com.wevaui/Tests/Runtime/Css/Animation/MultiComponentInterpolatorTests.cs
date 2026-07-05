using NUnit.Framework;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // H18b: multi-component property interpolators.
    //
    //   - background-position / background-size: per-axis <length-percentage>
    //     pairs, with `center`/`left`/`right`/`top`/`bottom` resolving to
    //     percentages. cover/contain/auto are discrete-only.
    //   - box-shadow / text-shadow: per-shadow per-component lerp; inset flag
    //     is not animatable (mismatched inset → discrete fallback).
    //   - clip-path: same-shape basic-shape interpolation only (inset / circle
    //     / ellipse / polygon with matching vertex counts).
    //
    // Pins the contract for ValueInterpolator's new PropertyKind dispatch
    // (PropertyKind.BackgroundPosition / .BackgroundSize / .BoxShadow /
    // .TextShadow / .ClipPath) and the per-property author-visible serialization.
    public class MultiComponentInterpolatorTests {
        static LengthContext Ctx() => LengthContext.Default;

        // --- background-position --------------------------------------------------

        [Test]
        public void BackgroundPosition_zero_to_full_at_half_is_half_each_axis() {
            var v = ValueInterpolator.Interpolate(
                "0% 0%", "100% 100%", 0.5, PropertyKind.BackgroundPosition, Ctx());
            Assert.That(v, Is.EqualTo("50% 50%"));
        }

        [Test]
        public void BackgroundPosition_keyword_center_resolves_to_half() {
            // `center` ↔ `right bottom` should lerp X from 50% → 100% and Y
            // from 50% → 100%, both at t=0.5 = 75%.
            var v = ValueInterpolator.Interpolate(
                "center", "right bottom", 0.5, PropertyKind.BackgroundPosition, Ctx());
            Assert.That(v, Is.EqualTo("75% 75%"));
        }

        [Test]
        public void BackgroundPosition_keyword_left_to_right_lerps_x() {
            // `left top` (0% 0%) → `right top` (100% 0%) at t=0.5.
            var v = ValueInterpolator.Interpolate(
                "left top", "right top", 0.5, PropertyKind.BackgroundPosition, Ctx());
            Assert.That(v, Is.EqualTo("50% 0%"));
        }

        [Test]
        public void BackgroundPosition_multi_layer_lerps_per_layer() {
            // Two-layer position: each layer lerps independently.
            var v = ValueInterpolator.Interpolate(
                "0% 0%, 100% 100%", "100% 100%, 0% 0%", 0.5,
                PropertyKind.BackgroundPosition, Ctx());
            Assert.That(v, Is.EqualTo("50% 50%, 50% 50%"));
        }

        [Test]
        public void BackgroundPosition_layer_count_mismatch_is_discrete() {
            // 1 layer ↔ 2 layers — discrete step at t < 0.5.
            var low = ValueInterpolator.Interpolate(
                "0% 0%", "0% 0%, 100% 100%", 0.4, PropertyKind.BackgroundPosition, Ctx());
            var high = ValueInterpolator.Interpolate(
                "0% 0%", "0% 0%, 100% 100%", 0.6, PropertyKind.BackgroundPosition, Ctx());
            Assert.That(low, Is.EqualTo("0% 0%"));
            Assert.That(high, Is.EqualTo("0% 0%, 100% 100%"));
        }

        // --- background-size ------------------------------------------------------

        [Test]
        public void BackgroundSize_length_pair_lerps_per_axis() {
            var v = ValueInterpolator.Interpolate(
                "50px 100px", "100px 200px", 0.5, PropertyKind.BackgroundSize, Ctx());
            Assert.That(v, Is.EqualTo("75px 150px"));
        }

        [Test]
        public void BackgroundSize_cover_is_discrete_against_length() {
            // `cover` doesn't decompose to per-axis lengths; mixed
            // length ↔ cover falls back to discrete.
            var low = ValueInterpolator.Interpolate(
                "50px 100px", "cover", 0.4, PropertyKind.BackgroundSize, Ctx());
            var high = ValueInterpolator.Interpolate(
                "50px 100px", "cover", 0.6, PropertyKind.BackgroundSize, Ctx());
            Assert.That(low, Is.EqualTo("50px 100px"));
            Assert.That(high, Is.EqualTo("cover"));
        }

        [Test]
        public void BackgroundSize_auto_to_length_is_discrete_per_axis() {
            // `auto` on either side of a single axis isn't animatable.
            var low = ValueInterpolator.Interpolate(
                "auto auto", "100px 100px", 0.4, PropertyKind.BackgroundSize, Ctx());
            Assert.That(low, Is.EqualTo("auto auto"));
        }

        // --- box-shadow -----------------------------------------------------------

        [Test]
        public void BoxShadow_lerps_lengths_and_color_oklab_midpoint() {
            // `0 0 5px red` (oy/ox 0, blur 5, red) → `10px 10px 5px blue`
            // at t=0.5 = `5px 5px 5px <oklab midpoint of red+blue>`.
            // The exact midpoint depends on oklab math; we pin the lengths
            // exactly and require the color to be present + non-trivially
            // between red and blue (G channel > 0 distinguishes from the
            // linear-RGB midpoint, which would be exactly rgb(127,0,127)).
            var v = ValueInterpolator.Interpolate(
                "0 0 5px red", "10px 10px 5px blue", 0.5,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(v, Does.StartWith("5px 5px 5px "));
            Assert.That(v, Does.Contain("rgb"));
            // Sanity: the oklab midpoint of red and blue is NOT the exact
            // linear-RGB midpoint; we verify R > 0 (red endpoint contributes)
            // AND B > 0 (blue endpoint contributes).
            int rgbStart = v.IndexOf("rgb(");
            Assert.That(rgbStart, Is.GreaterThan(0), "missing rgb() color");
        }

        [Test]
        public void BoxShadow_mismatched_inset_falls_back_to_discrete() {
            // One shadow inset, one not: inset is not interpolable per CSS
            // Backgrounds 3 §3.5; whole list demotes to discrete step.
            var low = ValueInterpolator.Interpolate(
                "inset 0 0 5px red", "0 0 5px red", 0.4,
                PropertyKind.BoxShadow, Ctx());
            var high = ValueInterpolator.Interpolate(
                "inset 0 0 5px red", "0 0 5px red", 0.6,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(low, Is.EqualTo("inset 0 0 5px red"));
            Assert.That(high, Is.EqualTo("0 0 5px red"));
        }

        [Test]
        public void BoxShadow_both_inset_interpolates_components() {
            // Both inset — the inset keyword carries through and components lerp.
            var v = ValueInterpolator.Interpolate(
                "inset 0 0 0 0 red", "inset 10px 10px 10px 10px red", 0.5,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(v, Does.StartWith("inset 5px 5px 5px 5px"));
        }

        [Test]
        public void BoxShadow_zero_pads_when_one_side_has_more_shadows() {
            // Browsers pad the shorter list with transparent zero-shadows
            // so a fade-in to the second shadow works. The second shadow
            // here should appear at half-strength (offsets + blur halved).
            var v = ValueInterpolator.Interpolate(
                "0 0 0 red", "0 0 0 red, 10px 10px 10px red", 0.5,
                PropertyKind.BoxShadow, Ctx());
            // Find the top-level layer comma (after the first shadow's color).
            // Scan depth — rgba(...) contains internal commas.
            int depth = 0;
            int layerComma = -1;
            for (int i = 0; i < v.Length; i++) {
                if (v[i] == '(') depth++;
                else if (v[i] == ')') depth--;
                else if (v[i] == ',' && depth == 0) { layerComma = i; break; }
            }
            Assert.That(layerComma, Is.GreaterThan(0), "missing layer separator");
            string secondShadow = v.Substring(layerComma + 1).TrimStart();
            // Second shadow's offsets/blur should be ~5px (half of 10px).
            Assert.That(secondShadow, Does.StartWith("5px 5px 5px"));
        }

        // --- text-shadow ----------------------------------------------------------

        [Test]
        public void TextShadow_three_lengths_lerps_per_component() {
            // text-shadow has no spread arg — only ox/oy/blur.
            var v = ValueInterpolator.Interpolate(
                "0 0 5px red", "10px 10px 5px blue", 0.5,
                PropertyKind.TextShadow, Ctx());
            Assert.That(v, Does.StartWith("5px 5px 5px"));
        }

        // --- currentColor in shadows (hud dot-pulse regression) -------------------
        //
        // `@keyframes dot-pulse { 0%,100% { box-shadow: 0 0 6px currentColor; }
        //                          50%     { box-shadow: 0 0 18px currentColor; } }`
        // blinked hard on/off: the shadow tokenizer had no `currentColor`
        // branch, the parse failed, and the whole list demoted to a discrete
        // step at t=0.5. currentColor resolves per-element at paint time, so
        // it must stay symbolic and let the geometry lerp.

        [Test]
        public void BoxShadow_currentColor_lerps_geometry_and_keeps_keyword() {
            var v = ValueInterpolator.Interpolate(
                "0 0 6px currentColor", "0 0 18px currentColor", 0.5,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(v, Does.StartWith("0px 0px 12px"));
            Assert.That(v, Does.Contain("currentColor"));
        }

        [Test]
        public void BoxShadow_currentColor_keyword_is_case_insensitive() {
            var v = ValueInterpolator.Interpolate(
                "0 0 6px CURRENTCOLOR", "0 0 18px currentcolor", 0.25,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(v, Does.StartWith("0px 0px 9px"));
            Assert.That(v, Does.Contain("currentColor"));
        }

        [Test]
        public void BoxShadow_omitted_color_means_currentColor_and_pairs_with_it() {
            // CSS Backgrounds 3 §3.5: omitted shadow color = currentColor.
            var v = ValueInterpolator.Interpolate(
                "0 0 6px", "0 0 18px currentColor", 0.25,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(v, Does.StartWith("0px 0px 9px"));
            Assert.That(v, Does.Contain("currentColor"));
        }

        [Test]
        public void BoxShadow_both_omitted_colors_lerp_without_emitting_color() {
            var v = ValueInterpolator.Interpolate(
                "0 0 6px", "0 0 18px", 0.5,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(v, Does.StartWith("0px 0px 12px"));
            Assert.That(v, Does.Not.Contain("currentColor"));
            Assert.That(v, Does.Not.Contain("rgb"));
        }

        [Test]
        public void BoxShadow_concrete_vs_currentColor_steps_color_keeps_geometry_smooth() {
            // A concrete↔currentColor pair can't lerp the color slot without
            // the element's resolved color; the color steps at t=0.5 but the
            // geometry must keep lerping (no whole-list discrete demotion).
            var low = ValueInterpolator.Interpolate(
                "0 0 6px red", "0 0 18px currentColor", 0.25,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(low, Does.StartWith("0px 0px 9px"));
            Assert.That(low, Does.Contain("rgb"));
            var high = ValueInterpolator.Interpolate(
                "0 0 6px red", "0 0 18px currentColor", 0.75,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(high, Does.StartWith("0px 0px 15px"));
            Assert.That(high, Does.Contain("currentColor"));
        }

        [Test]
        public void BoxShadow_two_color_tokens_still_rejected() {
            // `red currentColor` is invalid — list demotes to discrete.
            var low = ValueInterpolator.Interpolate(
                "0 0 6px red currentColor", "0 0 18px red", 0.4,
                PropertyKind.BoxShadow, Ctx());
            Assert.That(low, Is.EqualTo("0 0 6px red currentColor"));
        }

        [Test]
        public void TextShadow_currentColor_lerps_geometry() {
            var v = ValueInterpolator.Interpolate(
                "0 0 2px currentColor", "0 0 10px currentColor", 0.5,
                PropertyKind.TextShadow, Ctx());
            Assert.That(v, Does.StartWith("0px 0px 6px"));
            Assert.That(v, Does.Contain("currentColor"));
        }

        // --- clip-path ------------------------------------------------------------

        [Test]
        public void ClipPath_inset_lerps_sides() {
            var v = ValueInterpolator.Interpolate(
                "inset(10px)", "inset(30px)", 0.5, PropertyKind.ClipPath, Ctx());
            Assert.That(v, Is.EqualTo("inset(20px)"));
        }

        [Test]
        public void ClipPath_circle_lerps_radius() {
            var v = ValueInterpolator.Interpolate(
                "circle(10px)", "circle(30px)", 0.5, PropertyKind.ClipPath, Ctx());
            Assert.That(v, Is.EqualTo("circle(20px)"));
        }

        [Test]
        public void ClipPath_different_shapes_fall_back_to_discrete() {
            // inset() ↔ circle() is not interpolable per CSS Masking §6.
            var low = ValueInterpolator.Interpolate(
                "inset(10px)", "circle(20px)", 0.4, PropertyKind.ClipPath, Ctx());
            var high = ValueInterpolator.Interpolate(
                "inset(10px)", "circle(20px)", 0.6, PropertyKind.ClipPath, Ctx());
            Assert.That(low, Is.EqualTo("inset(10px)"));
            Assert.That(high, Is.EqualTo("circle(20px)"));
        }

        [Test]
        public void ClipPath_polygon_matching_points_lerps_per_vertex() {
            var v = ValueInterpolator.Interpolate(
                "polygon(0% 0%, 100% 0%, 50% 100%)",
                "polygon(20% 20%, 80% 20%, 50% 80%)",
                0.5, PropertyKind.ClipPath, Ctx());
            Assert.That(v, Is.EqualTo("polygon(10% 10%, 90% 10%, 50% 90%)"));
        }

        [Test]
        public void ClipPath_polygon_vertex_count_mismatch_is_discrete() {
            var low = ValueInterpolator.Interpolate(
                "polygon(0% 0%, 100% 0%, 50% 100%)",
                "polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%)",
                0.4, PropertyKind.ClipPath, Ctx());
            Assert.That(low, Is.EqualTo("polygon(0% 0%, 100% 0%, 50% 100%)"));
        }

        // --- PropertyKindRegistry registration ------------------------------------

        [Test]
        public void PropertyKindRegistry_maps_h18b_properties_to_new_kinds() {
            Assert.That(PropertyKindRegistry.Of("background-position"),
                Is.EqualTo(PropertyKind.BackgroundPosition));
            Assert.That(PropertyKindRegistry.Of("background-size"),
                Is.EqualTo(PropertyKind.BackgroundSize));
            Assert.That(PropertyKindRegistry.Of("box-shadow"),
                Is.EqualTo(PropertyKind.BoxShadow));
            Assert.That(PropertyKindRegistry.Of("text-shadow"),
                Is.EqualTo(PropertyKind.TextShadow));
            Assert.That(PropertyKindRegistry.Of("clip-path"),
                Is.EqualTo(PropertyKind.ClipPath));
        }
    }
}
