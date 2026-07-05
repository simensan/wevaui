using NUnit.Framework;
using System;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Transforms Module Level 2 §3 — individual standalone transform
    // properties: `translate`, `rotate`, and `scale`.
    //
    // These differ from the identically-named transform *functions* (translate(),
    // rotate(), scale()) in the `transform` shorthand: they are independent CSS
    // properties that cascade separately and compose with each other and with
    // `transform` at paint time as:
    //
    //   effective = translate * rotate * scale * transform
    //
    // (i.e. the explicit `transform` is applied to the point first, then the
    // individual scale, then rotate, then translate — matching the browser).
    //
    // Coverage:
    //   - Property metadata (registered, non-inherited, initial `none`).
    //   - Cascade round-trip for every keyword/unit form per spec grammar.
    //   - TransformResolver.ResolveTransform matrix assertions.
    //   - Composition with `transform` shorthand and with each other.
    //   - Non-inheritance.
    public class TransformIndividualPropertyTests {
        const double Eps = 1e-4;

        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        // ── 1. Property metadata ─────────────────────────────────────────────

        [Test]
        public void Translate_property_is_registered_non_inherited_initial_none() {
            // CSS Transforms L2 §3.2: initial value `none`, non-inherited.
            Assert.That(CssProperties.TryGet("translate", out _), Is.True);
            Assert.That(CssProperties.GetId("translate"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("translate"), Is.False);
            Assert.That(CssProperties.InitialValueOf("translate"), Is.EqualTo("none"));
        }

        [Test]
        public void Rotate_property_is_registered_non_inherited_initial_none() {
            // CSS Transforms L2 §3.3: initial value `none`, non-inherited.
            Assert.That(CssProperties.TryGet("rotate", out _), Is.True);
            Assert.That(CssProperties.GetId("rotate"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("rotate"), Is.False);
            Assert.That(CssProperties.InitialValueOf("rotate"), Is.EqualTo("none"));
        }

        [Test]
        public void Scale_property_is_registered_non_inherited_initial_none() {
            // CSS Transforms L2 §3.4: initial value `none`, non-inherited.
            Assert.That(CssProperties.TryGet("scale", out _), Is.True);
            Assert.That(CssProperties.GetId("scale"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("scale"), Is.False);
            Assert.That(CssProperties.InitialValueOf("scale"), Is.EqualTo("none"));
        }

        // ── 2. `translate` — cascade round-trips ────────────────────────────

        [Test]
        public void Translate_none_round_trips() {
            var cs = Compute("#x { translate: none; }");
            Assert.That(cs.Get("translate"), Is.EqualTo("none"));
        }

        [Test]
        public void Translate_single_length_round_trips() {
            var cs = Compute("#x { translate: 20px; }");
            Assert.That(cs.Get("translate"), Is.EqualTo("20px"));
        }

        [Test]
        public void Translate_two_lengths_round_trips() {
            var cs = Compute("#x { translate: 10px 30px; }");
            Assert.That(cs.Get("translate"), Is.EqualTo("10px 30px"));
        }

        [Test]
        public void Translate_percent_values_round_trip() {
            var cs = Compute("#x { translate: 50% 25%; }");
            Assert.That(cs.Get("translate"), Is.EqualTo("50% 25%"));
        }

        [Test]
        public void Translate_three_values_with_z_round_trips() {
            // The Z component is stored; 2D paint ignores it.
            var cs = Compute("#x { translate: 10px 20px 5px; }");
            Assert.That(cs.Get("translate"), Is.EqualTo("10px 20px 5px"));
        }

        // ── 3. `translate` — resolver assertions ─────────────────────────────

        [Test]
        public void Translate_single_length_resolves_x_only() {
            // Grammar: `translate: <length-percentage>` sets X; Y is 0.
            var s = Style();
            s.Set("translate", "15px");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.Tx, Is.EqualTo(15).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Translate_two_lengths_sets_x_and_y() {
            var s = Style();
            s.Set("translate", "10px 40px");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.Tx, Is.EqualTo(10).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(40).Within(Eps));
        }

        [Test]
        public void Translate_percent_resolves_against_reference_box() {
            // translate: 50% 25% with refW=200, refH=100 -> (100, 25).
            var s = Style();
            s.Set("translate", "50% 25%");
            var t = TransformResolver.ResolveTransform(s, 200, 100);
            Assert.That(t.Tx, Is.EqualTo(100).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(25).Within(Eps));
        }

        [Test]
        public void Translate_none_resolves_to_identity() {
            var s = Style();
            s.Set("translate", "none");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // ── 4. `rotate` — cascade round-trips ────────────────────────────────

        [Test]
        public void Rotate_none_round_trips() {
            var cs = Compute("#x { rotate: none; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("none"));
        }

        [Test]
        public void Rotate_angle_deg_round_trips() {
            var cs = Compute("#x { rotate: 45deg; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("45deg"));
        }

        [Test]
        public void Rotate_angle_turn_round_trips() {
            var cs = Compute("#x { rotate: 0.5turn; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("0.5turn"));
        }

        [Test]
        public void Rotate_angle_rad_round_trips() {
            var cs = Compute("#x { rotate: 1.5707963267948966rad; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("1.5707963267948966rad"));
        }

        [Test]
        public void Rotate_angle_grad_round_trips() {
            var cs = Compute("#x { rotate: 100grad; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("100grad"));
        }

        [Test]
        public void Rotate_z_axis_keyword_round_trips() {
            // Grammar: `rotate: z <angle>` — explicit Z axis keyword.
            var cs = Compute("#x { rotate: z 90deg; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("z 90deg"));
        }

        [Test]
        public void Rotate_x_axis_keyword_round_trips() {
            // 3D: `rotate: x 45deg` — parsed and stored; paint is identity.
            var cs = Compute("#x { rotate: x 45deg; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("x 45deg"));
        }

        [Test]
        public void Rotate_vector_form_round_trips() {
            // Grammar: `rotate: <nx> <ny> <nz> <angle>` (vector form).
            var cs = Compute("#x { rotate: 0 0 1 45deg; }");
            Assert.That(cs.Get("rotate"), Is.EqualTo("0 0 1 45deg"));
        }

        // ── 5. `rotate` — resolver assertions ────────────────────────────────

        [Test]
        public void Rotate_deg_90_maps_x_to_y() {
            var s = Style();
            s.Set("rotate", "90deg");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_turn_quarter_is_90deg() {
            var s = Style();
            s.Set("rotate", "0.25turn");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_rad_pi_over_2_is_90deg() {
            var s = Style();
            s.Set("rotate", "1.5707963267948966rad");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_grad_100_is_90deg() {
            var s = Style();
            s.Set("rotate", "100grad");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_z_keyword_90deg_maps_x_to_y() {
            var s = Style();
            s.Set("rotate", "z 90deg");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_x_keyword_returns_identity_in_2d() {
            // CSS Transforms L2: `rotate: x <angle>` is a 3D rotation about X.
            // 2D paint approximates this as identity.
            var s = Style();
            s.Set("rotate", "x 90deg");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity),
                "rotate: x <angle> is 3D; 2D paint returns identity");
        }

        [Test]
        public void Rotate_y_keyword_returns_identity_in_2d() {
            var s = Style();
            s.Set("rotate", "y 90deg");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity),
                "rotate: y <angle> is 3D; 2D paint returns identity");
        }

        [Test]
        public void Rotate_pure_z_vector_maps_to_planar_rotation() {
            // `rotate: 0 0 1 90deg` == `rotate: z 90deg` == rotate(90deg).
            var s = Style();
            s.Set("rotate", "0 0 1 90deg");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_none_is_identity() {
            var s = Style();
            s.Set("rotate", "none");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // ── 6. `scale` — cascade round-trips ─────────────────────────────────

        [Test]
        public void Scale_none_round_trips() {
            var cs = Compute("#x { scale: none; }");
            Assert.That(cs.Get("scale"), Is.EqualTo("none"));
        }

        [Test]
        public void Scale_single_number_round_trips() {
            var cs = Compute("#x { scale: 2; }");
            Assert.That(cs.Get("scale"), Is.EqualTo("2"));
        }

        [Test]
        public void Scale_two_numbers_round_trips() {
            var cs = Compute("#x { scale: 2 3; }");
            Assert.That(cs.Get("scale"), Is.EqualTo("2 3"));
        }

        [Test]
        public void Scale_three_numbers_with_z_round_trips() {
            // Z is carried through; 2D paint ignores it.
            var cs = Compute("#x { scale: 2 3 1; }");
            Assert.That(cs.Get("scale"), Is.EqualTo("2 3 1"));
        }

        [Test]
        public void Scale_percent_round_trips() {
            var cs = Compute("#x { scale: 150%; }");
            Assert.That(cs.Get("scale"), Is.EqualTo("150%"));
        }

        // ── 7. `scale` — resolver assertions ─────────────────────────────────

        [Test]
        public void Scale_single_number_uniform() {
            var s = Style();
            s.Set("scale", "3");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.A, Is.EqualTo(3).Within(Eps));
            Assert.That(t.D, Is.EqualTo(3).Within(Eps));
        }

        [Test]
        public void Scale_two_numbers_independent() {
            var s = Style();
            s.Set("scale", "2 0.5");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t.D, Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Scale_percent_resolves_to_fractional_scale() {
            // 200% -> 2.0, 50% -> 0.5.
            var s = Style();
            s.Set("scale", "200%");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t.D, Is.EqualTo(2).Within(Eps));
        }

        [Test]
        public void Scale_none_is_identity() {
            var s = Style();
            s.Set("scale", "none");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // ── 8. Composition with `transform` shorthand ─────────────────────────

        [Test]
        public void Translate_composes_outer_to_transform() {
            // CSS Transforms L2 §3: effective = translate * rotate * scale * transform.
            // With only `translate: 10px` and no transform/rotate/scale, the
            // effective transform is just the translation.
            var s = Style();
            s.Set("translate", "10px 0");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.Tx, Is.EqualTo(10).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Translate_then_transform_rotate_compose_correctly() {
            // translate: 10px; transform: rotate(45deg)
            // Effective = translate(10, 0) * identity * identity * rotate(45).
            // Point (0, 0): rotate(45) -> (0, 0); translate -> (10, 0).
            // Point (1, 0): rotate(45) -> (cos45, sin45); translate -> (10+cos45, sin45).
            var s = Style();
            s.Set("translate", "10px 0");
            s.Set("transform", "rotate(45deg)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            double cos45 = Math.Cos(Math.PI / 4);
            double sin45 = Math.Sin(Math.PI / 4);
            var (x0, y0) = t.Apply(0, 0);
            Assert.That(x0, Is.EqualTo(10).Within(Eps));
            Assert.That(y0, Is.EqualTo(0).Within(Eps));
            var (x1, y1) = t.Apply(1, 0);
            Assert.That(x1, Is.EqualTo(10 + cos45).Within(Eps));
            Assert.That(y1, Is.EqualTo(sin45).Within(Eps));
        }

        [Test]
        public void Scale_then_transform_translate_compose_correctly() {
            // scale: 2; transform: translate(10px, 0)
            // Effective = identity * identity * scale(2, 2) * translate(10, 0).
            // Point (0, 0): translate -> (10, 0); scale -> (20, 0).
            var s = Style();
            s.Set("scale", "2");
            s.Set("transform", "translate(10px, 0)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(20).Within(Eps));
            Assert.That(y, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void All_three_individual_props_compose_in_spec_order() {
            // Compose: translate: 5px 0; rotate: 90deg; scale: 2;
            // Effective = T(5,0) * R(90) * S(2) * I.
            // Point (1, 0): S(2) -> (2, 0); R(90) -> (0, 2); T(5,0) -> (5, 2).
            var s = Style();
            s.Set("translate", "5px 0");
            s.Set("rotate", "90deg");
            s.Set("scale", "2");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(5).Within(Eps));
            Assert.That(y, Is.EqualTo(2).Within(Eps));
        }

        [Test]
        public void Individual_props_with_none_values_are_identity_components() {
            // When all three are `none` the effective transform is identity.
            var s = Style();
            s.Set("translate", "none");
            s.Set("rotate", "none");
            s.Set("scale", "none");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // ── 9. Non-inheritance ────────────────────────────────────────────────

        [Test]
        public void Translate_property_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { translate: 50px 50px; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("translate"), Is.EqualTo("none"),
                "translate is non-inherited; child must see the initial none");
        }

        [Test]
        public void Rotate_property_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { rotate: 45deg; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("rotate"), Is.EqualTo("none"),
                "rotate is non-inherited; child must see the initial none");
        }

        [Test]
        public void Scale_property_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { scale: 2; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("scale"), Is.EqualTo("none"),
                "scale is non-inherited; child must see the initial none");
        }

        // ── 10. transform-box cascade-only round-trip ─────────────────────────
        // CSS Transforms L1 §6.2 — keywords: content-box, border-box,
        // fill-box, stroke-box, view-box. Initial `view-box` (HTML default).
        // Non-inherited. The resolver in BoxToPaintConverter honours
        // `content-box` (offsets pivot by border+padding); the SVG-specific
        // keywords (fill-box/stroke-box) fall back to border-box for HTML
        // elements, but the cascade round-trips every keyword verbatim.
        [Test]
        public void Transform_box_initial_is_view_box() {
            var cs = Compute("");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("view-box"));
        }

        [Test]
        public void Transform_box_fill_box_round_trips() {
            var cs = Compute("#x { transform-box: fill-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("fill-box"));
        }

        [Test]
        public void Transform_box_content_box_round_trips() {
            var cs = Compute("#x { transform-box: content-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("content-box"));
        }

        [Test]
        public void Transform_box_border_box_round_trips() {
            var cs = Compute("#x { transform-box: border-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Transform_box_stroke_box_round_trips() {
            var cs = Compute("#x { transform-box: stroke-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("stroke-box"));
        }

        [Test]
        public void Transform_box_view_box_round_trips() {
            var cs = Compute("#x { transform-box: view-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("view-box"));
        }

        [Test]
        public void Transform_box_is_not_inherited() {
            // CSS Transforms L1 §6.2 — Inherited: no.
            var doc = HtmlParser.Parse("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#parent { transform-box: content-box; }"))
            });
            engine.Compute(doc.GetElementById("parent"));
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("transform-box"), Is.EqualTo("view-box"),
                "transform-box is non-inherited; child must see the initial view-box");
        }

        [Test]
        public void Transform_box_invalid_keyword_pass_through_in_v1() {
            // CURRENT BEHAVIOUR: the cascade does NOT validate keyword values
            // for registered properties — invalid tokens are stored verbatim
            // rather than discarded per CSS Cascade L4 §3.3. This affects every
            // keyword-typed property in v1 and is tracked as a separate gap
            // (parser-validation). The resolver in BoxToPaintConverter only
            // treats `content-box` specially; any other value (including the
            // invalid one) falls through to the border-box default, so the
            // visual outcome is correct even though the round-tripped value
            // is non-conformant.
            var cs = Compute("#x { transform-box: not-a-real-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("not-a-real-box"),
                "v1 cascade is keyword pass-through; invalid token survives. " +
                "Tracked as a separate parser-validation gap.");
        }
    }
}
