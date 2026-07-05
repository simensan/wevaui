using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Animation {
    // G8b — CSS Transforms L2 §13. Cascade-side registration for the 3D
    // transform properties (perspective / transform-style / backface-visibility
    // / perspective-origin) and per-component interpolation for the L2
    // individual properties (translate / rotate / scale). Weva's paint
    // pipeline is 2D-only so the 3D properties are cascade-only round trips;
    // the interpolation side honours the spec's per-axis component lerp.
    public class TransformsL2RegistrationAndInterpTests {
        static LengthContext Ctx() => LengthContext.Default;

        // ---- Part A: cascade-side registration round-trip ----

        // CSS Transforms L2 §13.1 — `perspective: <length> | none`; non-
        // inherited; initial `none`. Cascade-only: Weva paints flat.
        [Test]
        public void Perspective_500px_round_trips_through_cascade_G8b() {
            Assert.That(CssProperties.TryGet("perspective", out _), Is.True);
            Assert.That(CssProperties.GetId("perspective"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("perspective"), Is.False);
            Assert.That(CssProperties.InitialValueOf("perspective"), Is.EqualTo("none"));

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#x { perspective: 500px; }"))
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("perspective"), Is.EqualTo("500px"));
        }

        // CSS Transforms L2 §13.2 — `transform-style: flat | preserve-3d`;
        // non-inherited; initial `flat`. Cascade-only in Weva.
        [Test]
        public void Transform_style_preserve_3d_parses_and_round_trips_G8b() {
            Assert.That(CssProperties.TryGet("transform-style", out _), Is.True);
            Assert.That(CssProperties.GetId("transform-style"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("transform-style"), Is.False);
            Assert.That(CssProperties.InitialValueOf("transform-style"), Is.EqualTo("flat"));

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#x { transform-style: preserve-3d; }"))
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("transform-style"), Is.EqualTo("preserve-3d"));
        }

        // CSS Transforms L2 §13.3 — `backface-visibility: visible | hidden`
        // and §13.4 — `perspective-origin: <position>`. Both registered with
        // spec initials; cascade-only.
        [Test]
        public void Backface_visibility_and_perspective_origin_round_trip_G8b() {
            Assert.That(CssProperties.TryGet("backface-visibility", out _), Is.True);
            Assert.That(CssProperties.IsInherited("backface-visibility"), Is.False);
            Assert.That(CssProperties.InitialValueOf("backface-visibility"), Is.EqualTo("visible"));

            Assert.That(CssProperties.TryGet("perspective-origin", out _), Is.True);
            Assert.That(CssProperties.IsInherited("perspective-origin"), Is.False);
            Assert.That(CssProperties.InitialValueOf("perspective-origin"), Is.EqualTo("50% 50%"));

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse(
                    "#x { backface-visibility: hidden; perspective-origin: 25% 75%; }"))
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("backface-visibility"), Is.EqualTo("hidden"));
            Assert.That(cs.Get("perspective-origin"), Is.EqualTo("25% 75%"));
        }

        // ---- Part B: per-component interpolation ----

        // CSS Transforms L2 §13.5 / §3.2 — `translate` interpolates per
        // component (x, y, z independently). Missing components default to 0.
        // `translate: 10px -> 20px 30px` at t=0.5: x is 10->20 (=15), y is the
        // implicit 0 from the from-side lerped against 30 (=15).
        [Test]
        public void Translate_one_arg_vs_two_arg_interpolates_per_component_G8b() {
            var v = ValueInterpolator.Interpolate("10px", "20px 30px", 0.5,
                PropertyKind.Translate, Ctx());
            Assert.That(v, Is.EqualTo("15px 15px"));
        }

        // Pure two-arg lerp (regression / contract pin).
        [Test]
        public void Translate_two_arg_each_axis_lerps_independently_G8b() {
            var v = ValueInterpolator.Interpolate("0 0", "10px 40px", 0.25,
                PropertyKind.Translate, Ctx());
            // x: 0->10 at 0.25 = 2.5, y: 0->40 at 0.25 = 10.
            Assert.That(v, Is.EqualTo("2.5px 10px"));
        }

        // CSS Transforms L2 §13.6 / §3.4 — `scale` interpolates per component.
        // One-arg `scale: 1` expands to `1 1` per spec. `1 -> 2 3` at t=0.5
        // therefore lerps the implicit-1 y-axis against 3 to give `1.5 2`.
        [Test]
        public void Scale_one_arg_expands_to_pair_for_per_axis_interp_G8b() {
            var v = ValueInterpolator.Interpolate("1", "2 3", 0.5,
                PropertyKind.Scale, Ctx());
            Assert.That(v, Is.EqualTo("1.5 2"));
        }

        // CSS Transforms L2 §13.7 / §3.3 — `rotate` lerps the angle. Pure-Z
        // (default axis) endpoints lerp linearly; axis match preserved.
        [Test]
        public void Rotate_default_axis_angle_lerps_linearly_G8b() {
            var v = ValueInterpolator.Interpolate("0deg", "90deg", 0.5,
                PropertyKind.Rotate, Ctx());
            Assert.That(v, Is.EqualTo("45deg"));
        }

        // `rotate: none` is the identity (0deg). Interpolating `none -> 60deg`
        // at t=0.5 is therefore 30deg — verifies the spec's identity-fill
        // for the missing-side angle.
        [Test]
        public void Rotate_none_to_angle_treats_none_as_zero_G8b() {
            var v = ValueInterpolator.Interpolate("none", "60deg", 0.5,
                PropertyKind.Rotate, Ctx());
            Assert.That(v, Is.EqualTo("30deg"));
        }

        // PropertyKindRegistry classification — ensures the cascade-driven
        // dispatch routes individual transforms through their dedicated
        // kinds rather than falling through to Discrete.
        [Test]
        public void Property_kind_registry_classifies_individual_transforms_G8b() {
            Assert.That(PropertyKindRegistry.Of("translate"), Is.EqualTo(PropertyKind.Translate));
            Assert.That(PropertyKindRegistry.Of("rotate"), Is.EqualTo(PropertyKind.Rotate));
            Assert.That(PropertyKindRegistry.Of("scale"), Is.EqualTo(PropertyKind.Scale));
            Assert.That(PropertyKindRegistry.IsAnimatable("translate"), Is.True);
            Assert.That(PropertyKindRegistry.IsAnimatable("rotate"), Is.True);
            Assert.That(PropertyKindRegistry.IsAnimatable("scale"), Is.True);
        }
    }
}
