using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Values {
    // CSS Images Level 3 / Level 4 — gradient parse round-trips at the cascade level.
    //
    // BackgroundResolverTests covers paint-side resolution (angle computation,
    // radius resolution, stop interpolation). This file focuses purely on whether
    // each gradient syntax variant is accepted by the CSS parser and survives the
    // cascade round-trip via background-image:
    //
    //   linear-gradient  — angle forms: explicit deg, `to` keyword, no angle
    //   radial-gradient  — shape + size keywords, at-position, percentage radius
    //   conic-gradient   — from-angle, at-position, hard-stop pairs
    //   repeating-*      — repeating variants of the above three
    //   color hints      — bare percentage between two stops (midpoint hint)
    //   percentage stops — explicit stop positions
    //
    // Each test asserts the value string stored in the cascade matches the input
    // (the cascade stores values verbatim as authored strings). Tests do NOT
    // assert on computed color or geometry — that belongs to BackgroundResolverTests.
    public class GradientParseTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static string Get(string gradientValue) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author($"#x {{ background-image: {gradientValue}; }}")
            });
            return engine.Compute(doc.GetElementById("x")).Get("background-image");
        }

        // ── linear-gradient ───────────────────────────────────────────────

        [Test]
        public void Linear_gradient_no_angle_defaults_accepted() {
            // No explicit angle → gradient runs from top to bottom (180deg).
            // Parser must accept the two-stop form without an angle token.
            var got = Get("linear-gradient(red, blue)");
            Assert.That(got, Is.EqualTo("linear-gradient(red, blue)"));
        }

        [Test]
        public void Linear_gradient_explicit_degree_angle_accepted() {
            var got = Get("linear-gradient(45deg, red, blue)");
            Assert.That(got, Is.EqualTo("linear-gradient(45deg, red, blue)"));
        }

        [Test]
        public void Linear_gradient_to_right_keyword_accepted() {
            // `to right` = 90deg. Keyword direction form per CSS Images 3 §3.1.
            var got = Get("linear-gradient(to right, red, blue)");
            Assert.That(got, Is.EqualTo("linear-gradient(to right, red, blue)"));
        }

        [Test]
        public void Linear_gradient_to_bottom_left_diagonal_accepted() {
            var got = Get("linear-gradient(to bottom left, #fff, #000)");
            Assert.That(got, Is.EqualTo("linear-gradient(to bottom left, #fff, #000)"));
        }

        [Test]
        public void Linear_gradient_percentage_stops_round_trip() {
            // Explicit percentage positions on each stop.
            var got = Get("linear-gradient(red 0%, green 50%, blue 100%)");
            Assert.That(got, Is.EqualTo("linear-gradient(red 0%, green 50%, blue 100%)"));
        }

        [Test]
        public void Linear_gradient_color_hint_midpoint_accepted() {
            // A bare percentage between stops is a color-hint (midpoint hint).
            // CSS Images 4 §3.4: "interpolation hint" — the parser must not strip it.
            var got = Get("linear-gradient(red, 30%, blue)");
            Assert.That(got, Is.EqualTo("linear-gradient(red, 30%, blue)"));
        }

        [Test]
        public void Linear_gradient_three_stops_no_positions_accepted() {
            var got = Get("linear-gradient(red, green, blue)");
            Assert.That(got, Is.EqualTo("linear-gradient(red, green, blue)"));
        }

        // ── radial-gradient ───────────────────────────────────────────────

        [Test]
        public void Radial_gradient_circle_at_center_accepted() {
            var got = Get("radial-gradient(circle at 50% 50%, white, black)");
            Assert.That(got, Is.EqualTo("radial-gradient(circle at 50% 50%, white, black)"));
        }

        [Test]
        public void Radial_gradient_ellipse_farthest_corner_accepted() {
            var got = Get("radial-gradient(ellipse farthest-corner at 30% 70%, red, blue)");
            Assert.That(got, Is.EqualTo("radial-gradient(ellipse farthest-corner at 30% 70%, red, blue)"));
        }

        [Test]
        public void Radial_gradient_closest_side_keyword_accepted() {
            var got = Get("radial-gradient(circle closest-side at 50px 50px, red, blue)");
            Assert.That(got, Is.EqualTo("radial-gradient(circle closest-side at 50px 50px, red, blue)"));
        }

        [Test]
        public void Radial_gradient_no_shape_no_size_accepted() {
            // Bare radial-gradient with only stops: defaults to `ellipse farthest-corner`.
            var got = Get("radial-gradient(red, blue)");
            Assert.That(got, Is.EqualTo("radial-gradient(red, blue)"));
        }

        [Test]
        public void Radial_gradient_percentage_stops_accepted() {
            var got = Get("radial-gradient(circle at center, red 0%, blue 100%)");
            Assert.That(got, Is.EqualTo("radial-gradient(circle at center, red 0%, blue 100%)"));
        }

        // ── conic-gradient ────────────────────────────────────────────────

        [Test]
        public void Conic_gradient_simple_stops_accepted() {
            // Bare conic-gradient: starts from 0deg at center.
            var got = Get("conic-gradient(red, green, blue)");
            Assert.That(got, Is.EqualTo("conic-gradient(red, green, blue)"));
        }

        [Test]
        public void Conic_gradient_from_angle_accepted() {
            // CSS Images 4 §3.3: `from <angle>` rotates the start angle.
            var got = Get("conic-gradient(from 45deg, red, blue)");
            Assert.That(got, Is.EqualTo("conic-gradient(from 45deg, red, blue)"));
        }

        [Test]
        public void Conic_gradient_from_angle_at_position_accepted() {
            var got = Get("conic-gradient(from 45deg at 50% 50%, red, blue)");
            Assert.That(got, Is.EqualTo("conic-gradient(from 45deg at 50% 50%, red, blue)"));
        }

        [Test]
        public void Conic_gradient_hard_stop_pairs_accepted() {
            // Two angle stops on the same color produce a hard stop (no blending).
            var got = Get("conic-gradient(red 0deg 90deg, blue 90deg 180deg, green 180deg 360deg)");
            Assert.That(got, Is.EqualTo(
                "conic-gradient(red 0deg 90deg, blue 90deg 180deg, green 180deg 360deg)"));
        }

        // ── repeating-linear-gradient ─────────────────────────────────────

        [Test]
        public void Repeating_linear_gradient_simple_accepted() {
            // CSS Images 3 §3.6: repeating variant tiles the gradient image.
            var got = Get("repeating-linear-gradient(red 0px, blue 20px)");
            Assert.That(got, Is.EqualTo("repeating-linear-gradient(red 0px, blue 20px)"));
        }

        [Test]
        public void Repeating_linear_gradient_with_angle_accepted() {
            var got = Get("repeating-linear-gradient(45deg, red 0%, blue 10%)");
            Assert.That(got, Is.EqualTo("repeating-linear-gradient(45deg, red 0%, blue 10%)"));
        }

        // ── repeating-radial-gradient ─────────────────────────────────────

        [Test]
        public void Repeating_radial_gradient_accepted() {
            // CSS Images 3 §3.8: repeating radial gradient.
            var got = Get("repeating-radial-gradient(circle at 50% 50%, red 0, blue 20px)");
            Assert.That(got, Is.EqualTo("repeating-radial-gradient(circle at 50% 50%, red 0, blue 20px)"));
        }

        // ── repeating-conic-gradient ──────────────────────────────────────

        [Test]
        public void Repeating_conic_gradient_accepted() {
            // CSS Images 4 §3.9: repeating conic gradient.
            var got = Get("repeating-conic-gradient(red 0deg 30deg, blue 30deg 60deg)");
            Assert.That(got, Is.EqualTo("repeating-conic-gradient(red 0deg 30deg, blue 30deg 60deg)"));
        }
    }
}
