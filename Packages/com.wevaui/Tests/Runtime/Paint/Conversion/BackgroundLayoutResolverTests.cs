using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS Backgrounds 3 §3 resolution: position / size / repeat into a
    // BackgroundTile carrying absolute pixel dimensions and origin offsets
    // ready for the renderer to consume. Tests pin keyword resolution
    // (cover/contain/center/etc.), percentage and length forms, the
    // intrinsic aspect-ratio derivation when one axis is `auto`, and
    // the two-value repeat shorthand.
    public class BackgroundLayoutResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds(double w = 200, double h = 100) => new Rect(0, 0, w, h);
        static LengthContext Ctx() => LengthContext.Default;

        // ---------- size ----------

        [Test]
        public void Size_auto_uses_intrinsic_dimensions() {
            var s = Style();
            s.Set("background-size", "auto");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 64, 32, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(64));
            Assert.That(t.TileHeight, Is.EqualTo(32));
        }

        [Test]
        public void Size_contain_fits_inside_box_preserving_aspect() {
            var s = Style();
            s.Set("background-size", "contain");
            // intrinsic 100x50 (2:1) inside 200x100 → fills exactly (one of
            // the axes hits the box).
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 100, 50, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(200).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Size_contain_fits_when_image_is_square() {
            var s = Style();
            s.Set("background-size", "contain");
            // 100x100 inside 200x100 → height-bound: 100x100.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 100, 100, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(100).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Size_cover_fills_box_overflowing_other_axis() {
            var s = Style();
            s.Set("background-size", "cover");
            // 100x100 inside 200x100 → width-bound: 200x200.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 100, 100, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(200).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(200).Within(1e-6));
        }

        [Test]
        public void Size_explicit_pixel_lengths() {
            var s = Style();
            s.Set("background-size", "32px 64px");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 100, 100, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(32));
            Assert.That(t.TileHeight, Is.EqualTo(64));
        }

        [Test]
        public void Size_percent_resolves_against_box_dimensions() {
            var s = Style();
            s.Set("background-size", "50% 25%");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 64, 32, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(100));
            Assert.That(t.TileHeight, Is.EqualTo(25));
        }

        [Test]
        public void Size_auto_height_derives_from_aspect_ratio() {
            var s = Style();
            s.Set("background-size", "60px auto");
            // intrinsic 100x50 → ratio 2:1; width=60 → height=30.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 100, 50, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(60).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(30).Within(1e-6));
        }

        [Test]
        public void Size_auto_width_derives_from_aspect_ratio() {
            var s = Style();
            s.Set("background-size", "auto 40px");
            // intrinsic 100x50 → ratio 2:1; height=40 → width=80.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 100, 50, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(80).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(40).Within(1e-6));
        }

        // ---------- position ----------

        [Test]
        public void Position_default_zero_zero_when_unset() {
            var s = Style();
            s.Set("background-size", "50% 50%");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 64, 32, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(0));
            Assert.That(t.OriginY, Is.EqualTo(0));
        }

        [Test]
        public void Position_center_centers_tile_in_box() {
            var s = Style();
            s.Set("background-size", "100px 50px");
            s.Set("background-position", "center center");
            // tile 100x50 in 200x100 box → centered at (50, 25).
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 100, 50, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(50));
            Assert.That(t.OriginY, Is.EqualTo(25));
        }

        [Test]
        public void Position_bottom_right_pins_tile_to_far_corner() {
            var s = Style();
            s.Set("background-size", "50px 25px");
            s.Set("background-position", "right bottom");
            // tile 50x25 in 200x100 → right=200-50=150, bottom=100-25=75.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 50, 25, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(150));
            Assert.That(t.OriginY, Is.EqualTo(75));
        }

        [Test]
        public void Position_swap_when_vertical_keyword_is_first() {
            // CSS allows `top left` or `left top`; v1 swaps when the
            // first token is a vertical keyword.
            var s = Style();
            s.Set("background-size", "50px 25px");
            s.Set("background-position", "top right");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 50, 25, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(150)); // right
            Assert.That(t.OriginY, Is.EqualTo(0));   // top
        }

        [Test]
        public void Position_pixel_lengths() {
            var s = Style();
            s.Set("background-size", "50px 25px");
            s.Set("background-position", "10px 20px");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 50, 25, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(10));
            Assert.That(t.OriginY, Is.EqualTo(20));
        }

        [Test]
        public void Position_percent_resolves_against_box_minus_tile() {
            var s = Style();
            s.Set("background-size", "50px 50px");
            s.Set("background-position", "50% 50%");
            // (200-50) * 0.5 = 75; (100-50) * 0.5 = 25.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 50, 50, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(75));
            Assert.That(t.OriginY, Is.EqualTo(25));
        }

        // CSS Backgrounds 3 §3.7: the 4-value edge-offset form
        // `<edge> <offset> <edge> <offset>` anchors each offset to the named
        // edge. `right 10px top 20px` means 10px in from the right edge and
        // 20px down from the top. The 3-value form omits one offset, and the
        // axes can appear in either order.
        [Test]
        public void Position_four_value_edge_offset_anchors_to_named_edges() {
            // 4-value form: right 10px top 20px → x = (200 - 50) - 10 = 140; y = 20.
            var s = Style();
            s.Set("background-size", "50px 50px");
            s.Set("background-position", "right 10px top 20px");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 200), 50, 50, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(140).Within(1e-6));
            Assert.That(t.OriginY, Is.EqualTo(20).Within(1e-6));

            // 4-value form: left 10px bottom 20px → x = 10; y = (100 - 50) - 20 = 30.
            var s2 = Style();
            s2.Set("background-size", "50px 50px");
            s2.Set("background-position", "left 10px bottom 20px");
            var t2 = BackgroundLayoutResolver.Resolve(s2, Bounds(100, 100), 50, 50, Ctx());
            Assert.That(t2.OriginX, Is.EqualTo(10).Within(1e-6));
            Assert.That(t2.OriginY, Is.EqualTo(30).Within(1e-6));

            // Axes can appear in either order: top 20px right 10px ≡ right 10px top 20px.
            var s3 = Style();
            s3.Set("background-size", "50px 50px");
            s3.Set("background-position", "top 20px right 10px");
            var t3 = BackgroundLayoutResolver.Resolve(s3, Bounds(200, 200), 50, 50, Ctx());
            Assert.That(t3.OriginX, Is.EqualTo(140).Within(1e-6));
            Assert.That(t3.OriginY, Is.EqualTo(20).Within(1e-6));
        }

        [Test]
        public void Position_three_value_edge_offset_with_one_edge_unoffset() {
            // 3-value form: right 10px center → x = (200 - 50) - 10 = 140;
            // y centered = (100 - 50) / 2 = 25.
            var s = Style();
            s.Set("background-size", "50px 50px");
            s.Set("background-position", "right 10px center");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 50, 50, Ctx());
            Assert.That(t.OriginX, Is.EqualTo(140).Within(1e-6));
            Assert.That(t.OriginY, Is.EqualTo(25).Within(1e-6));

            // 3-value form: center bottom 20px → x centered = (200 - 50)/2 = 75;
            // y = (100 - 50) - 20 = 30.
            var s2 = Style();
            s2.Set("background-size", "50px 50px");
            s2.Set("background-position", "center bottom 20px");
            var t2 = BackgroundLayoutResolver.Resolve(s2, Bounds(200, 100), 50, 50, Ctx());
            Assert.That(t2.OriginX, Is.EqualTo(75).Within(1e-6));
            Assert.That(t2.OriginY, Is.EqualTo(30).Within(1e-6));
        }

        // ---------- repeat ----------

        [Test]
        public void Repeat_default_repeats_both_axes() {
            var s = Style();
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        [Test]
        public void Repeat_no_repeat_disables_both_axes() {
            var s = Style();
            s.Set("background-repeat", "no-repeat");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }

        [Test]
        public void Repeat_x_repeats_only_horizontal_axis() {
            var s = Style();
            s.Set("background-repeat", "repeat-x");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }

        [Test]
        public void Repeat_y_repeats_only_vertical_axis() {
            var s = Style();
            s.Set("background-repeat", "repeat-y");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        [Test]
        public void Repeat_two_value_form() {
            var s = Style();
            s.Set("background-repeat", "no-repeat repeat");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        [Test]
        public void Repeat_space_and_round_keywords_parsed_distinctly() {
            // Renderer treats space/round as plain repeat in v1, but the
            // parser preserves the distinction so future shader work can
            // honour the spec without re-parsing.
            var s = Style();
            s.Set("background-repeat", "space space");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Space));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.Space));

            s.Set("background-repeat", "round");
            t = BackgroundLayoutResolver.Resolve(s, Bounds(), 32, 32, Ctx());
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Round));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.Round));
        }

        // ---------- round ----------

        [Test]
        public void Round_x_scales_tile_to_fit_whole_repetitions() {
            // box=200, tile=64 → raw count = 3.125; rounded count = 3;
            // new tile = 200/3 ≈ 66.667.
            var s = Style();
            s.Set("background-size", "64px 64px");
            s.Set("background-repeat", "round repeat");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 64, 64, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(200.0 / 3.0).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(64));
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Round));
        }

        [Test]
        public void Round_clamps_to_one_when_tile_larger_than_box() {
            // box=50 < tile=64 → count would round to 1, tile = 50.
            var s = Style();
            s.Set("background-size", "64px 64px");
            s.Set("background-repeat", "round");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(50, 100), 64, 64, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(50));
        }

        // ---------- space ----------

        [Test]
        public void Space_distributes_leftover_pixels_as_gap_between_tiles() {
            // box=200, tile=64 → count = floor(200/64) = 3; leftover = 200 - 192 = 8;
            // gap = 8 / (3 - 1) = 4. Origin clamped to 0 per spec.
            var s = Style();
            s.Set("background-size", "64px 64px");
            s.Set("background-repeat", "space repeat");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 64, 64, Ctx());
            Assert.That(t.GapX, Is.EqualTo(4).Within(1e-6));
            Assert.That(t.OriginX, Is.EqualTo(0));
        }

        [Test]
        public void Space_with_single_tile_keeps_position_no_gap() {
            // box=50, tile=64 → count = 0; clamped to no-gap path; origin
            // resolves per parsed background-position.
            var s = Style();
            s.Set("background-size", "64px 64px");
            s.Set("background-repeat", "space");
            s.Set("background-position", "center");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(50, 50), 64, 64, Ctx());
            Assert.That(t.GapX, Is.EqualTo(0));
            Assert.That(t.OriginX, Is.EqualTo(-7)); // (50 - 64) * 0.5
        }

        // ---------- end-to-end ----------

        [Test]
        public void Combined_position_size_repeat_resolve_together() {
            var s = Style();
            s.Set("background-size", "32px 32px");
            s.Set("background-position", "center center");
            s.Set("background-repeat", "no-repeat");
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 64, 64, Ctx());
            Assert.That(t.TileWidth, Is.EqualTo(32));
            Assert.That(t.TileHeight, Is.EqualTo(32));
            Assert.That(t.OriginX, Is.EqualTo((200 - 32) * 0.5).Within(1e-6));
            Assert.That(t.OriginY, Is.EqualTo((100 - 32) * 0.5).Within(1e-6));
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }
    }
}
