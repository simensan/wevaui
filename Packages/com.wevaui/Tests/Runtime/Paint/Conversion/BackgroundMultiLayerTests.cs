using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // Multi-layer (comma-separated) `background` resolution. Pins:
    //   * Top-level commas split layers; commas inside `linear-gradient(...)`
    //     stay attached to the gradient.
    //   * Per-layer position/size/repeat (the `<position> / <size>` modifier
    //     after each image) reach BackgroundLayoutResolver indexed parallel
    //     to the image list.
    //   * The trailing solid color in `background: <image>, <color>` paints
    //     UNDER all image layers (last in the layer list).
    //   * Layers paint front-to-back: layer 0 (first declared) is topmost;
    //     callers iterate the returned list in reverse for paint order.
    public class BackgroundMultiLayerTests {
        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds(double w = 100, double h = 100) => new Rect(0, 0, w, h);

        // ---------- comma splitting ----------

        [Test]
        public void Two_gradient_layers_both_reach_paint() {
            // Regression: `background: linear-gradient(red, blue), linear-gradient(green, yellow)`
            // must split at the TOP-LEVEL comma and produce two gradient
            // brushes — the inner stop-list commas must stay with each
            // gradient or only one layer reaches paint.
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue), linear-gradient(green, yellow)");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Gradient));
            // First layer is `red → blue`; second is `green → yellow`.
            var l0 = (LinearGradient)output[0].GradientValue;
            var l1 = (LinearGradient)output[1].GradientValue;
            Assert.That(l0.Stops[0].Color.R, Is.GreaterThan(0.5f));
            Assert.That(l0.Stops[1].Color.B, Is.GreaterThan(0.5f));
            Assert.That(l1.Stops[0].Color.G, Is.GreaterThan(0.05f));
        }

        [Test]
        public void Four_layer_world_before_stack_resolves_all_layers() {
            // randhtml `.world::before`: a top-fade plus three mountain
            // triangles. Each layer is a `linear-gradient(...)` so the
            // resolver must produce 4 gradient brushes.
            var s = Style();
            s.Set("background-image",
                "linear-gradient(to top, #11141c 0%, transparent 100%), "
                + "linear-gradient(135deg, transparent 50%, #1c2030 50%), "
                + "linear-gradient(225deg, transparent 50%, #232838 50%), "
                + "linear-gradient(135deg, transparent 50%, #1a1e2a 50%)");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(4));
            for (int i = 0; i < 4; i++) {
                Assert.That(output[i], Is.Not.Null, $"layer {i} parsed");
                Assert.That(output[i].Kind, Is.EqualTo(BrushKind.Gradient), $"layer {i} kind");
            }
        }

        // ---------- per-layer position/size/repeat ----------

        [Test]
        public void Per_layer_position_size_repeat_reach_url_image_tile() {
            // Two url() layers with distinct `<position>/<size>` modifiers.
            // The cascade has already split the shorthand into comma-joined
            // longhand lists; the resolver must index parallel to the image
            // list when computing each tile.
            var s = Style();
            s.Set("background-image", "url(a), url(b)");
            s.Set("background-position", "0 0, 50% 50%");
            s.Set("background-size", "20px 30px, 40% 60%");
            s.Set("background-repeat", "no-repeat, repeat");

            var reg = new InMemoryImageRegistry();
            reg.Register("a", new StubSource(10, 10));
            reg.Register("b", new StubSource(10, 10));

            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(200, 100), Bounds(200, 100),
                output, null, reg, LengthContext.Default);

            Assert.That(output.Count, Is.EqualTo(2));
            // Layer 0: position 0 0, size 20x30, no-repeat.
            Assert.That(output[0].Tile.HasValue, Is.True);
            var t0 = output[0].Tile.Value;
            Assert.That(t0.TileWidth, Is.EqualTo(20).Within(1e-6));
            Assert.That(t0.TileHeight, Is.EqualTo(30).Within(1e-6));
            Assert.That(t0.OriginX, Is.EqualTo(0).Within(1e-6));
            Assert.That(t0.OriginY, Is.EqualTo(0).Within(1e-6));
            Assert.That(t0.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            // Layer 1: position 50% 50%, size 40% x 60%, repeat. With size
            // 80x60 inside 200x100, position 50% resolves against (box-tile),
            // so origin = (200-80)*0.5 = 60, (100-60)*0.5 = 20.
            Assert.That(output[1].Tile.HasValue, Is.True);
            var t1 = output[1].Tile.Value;
            Assert.That(t1.TileWidth, Is.EqualTo(80).Within(1e-6));
            Assert.That(t1.TileHeight, Is.EqualTo(60).Within(1e-6));
            Assert.That(t1.OriginX, Is.EqualTo(60).Within(1e-6));
            Assert.That(t1.OriginY, Is.EqualTo(20).Within(1e-6));
            Assert.That(t1.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        [Test]
        public void Shorter_longhand_list_cycles_to_match_image_count() {
            // CSS Backgrounds 3 §3.10: when `background-position` has fewer
            // entries than `background-image`, the values cycle. Three
            // images + two positions → layers (a, b, a).
            var s = Style();
            s.Set("background-image", "url(a), url(b), url(c)");
            s.Set("background-position", "0 0, 50% 50%");
            s.Set("background-size", "10px 10px");

            var reg = new InMemoryImageRegistry();
            reg.Register("a", new StubSource(8, 8));
            reg.Register("b", new StubSource(8, 8));
            reg.Register("c", new StubSource(8, 8));

            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(100, 100), Bounds(100, 100),
                output, null, reg, LengthContext.Default);

            Assert.That(output.Count, Is.EqualTo(3));
            // Layer 2 should take position[0] (cycled): origin (0, 0).
            var t2 = output[2].Tile.Value;
            Assert.That(t2.OriginX, Is.EqualTo(0).Within(1e-6));
            Assert.That(t2.OriginY, Is.EqualTo(0).Within(1e-6));
        }

        // ---------- color tail ----------

        [Test]
        public void Trailing_solid_color_is_emitted_as_last_layer() {
            // `background: linear-gradient(...), red` — the gradient is the
            // top layer, the color is the bottom layer. The resolver appends
            // the color brush AFTER the image list so the painter (which
            // iterates the list in reverse) draws color first.
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue)");
            s.Set("background-color", "green");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.SolidColor));
            // Green: G > R, B.
            Assert.That(output[1].Color.G, Is.GreaterThan(0.05f));
            Assert.That(output[1].Color.R, Is.LessThan(0.05f));
        }

        [Test]
        public void Transparent_color_does_not_emit_a_layer() {
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue)");
            s.Set("background-color", "transparent");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(1));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
        }

        // ---------- gradient layer position/size carrier ----------

        [Test]
        public void Gradient_layer_with_per_layer_position_size_carries_tile() {
            // Gradient layers carry a BackgroundTile so the URP shader can
            // clip / position the gradient inside the box. CSS treats
            // gradients as images with no intrinsic size, so `auto` sizes
            // resolve against the origin box (= "fill the whole quad",
            // matching the pre-tile behaviour for unmodified gradients).
            var s = Style();
            s.Set("background-image", "linear-gradient(135deg, transparent 50%, blue 50%), linear-gradient(red, blue)");
            s.Set("background-position", "0 0, 0 0");
            s.Set("background-size", "30px 30px, auto");
            s.Set("background-repeat", "no-repeat, repeat");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(200, 100), Bounds(200, 100),
                output, null, null, LengthContext.Default);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            // Layer 0: explicit 30x30 no-repeat → tile carries those values.
            Assert.That(output[0].Tile.HasValue, Is.True);
            var t0 = output[0].Tile.Value;
            Assert.That(t0.TileWidth, Is.EqualTo(30).Within(1e-6));
            Assert.That(t0.TileHeight, Is.EqualTo(30).Within(1e-6));
            Assert.That(t0.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            // Layer 1: `auto` size + `repeat` → fills the whole 200x100 box.
            Assert.That(output[1].Tile.HasValue, Is.True);
            var t1 = output[1].Tile.Value;
            Assert.That(t1.TileWidth, Is.EqualTo(200).Within(1e-6));
            Assert.That(t1.TileHeight, Is.EqualTo(100).Within(1e-6));
            Assert.That(t1.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        // ---------- gradient tile clip rect ----------

        [Test]
        public void Linear_gradient_with_50_percent_size_and_no_repeat_paints_top_left_quadrant() {
            // `linear-gradient(...) 0 0 / 50% 50% no-repeat` on a 100x100 box
            // must clip the gradient to the top-left 50x50 rect. The brush's
            // BackgroundTile carries the rect; the URP shader's
            // Weva_GradientTileUv helper discards fragments outside it
            // (no-repeat), so the bottom-right quadrant ends up transparent
            // and any layer beneath shows through.
            var s = Style();
            s.Set("background-image", "linear-gradient(135deg, transparent 50%, blue 50%)");
            s.Set("background-position", "0 0");
            s.Set("background-size", "50% 50%");
            s.Set("background-repeat", "no-repeat");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(100, 100), Bounds(100, 100),
                output, null, null, LengthContext.Default);
            Assert.That(output.Count, Is.EqualTo(1));
            Assert.That(output[0].Tile.HasValue, Is.True);
            var t = output[0].Tile.Value;
            Assert.That(t.OriginX, Is.EqualTo(0).Within(1e-6));
            Assert.That(t.OriginY, Is.EqualTo(0).Within(1e-6));
            Assert.That(t.TileWidth, Is.EqualTo(50).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(50).Within(1e-6));
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }

        [Test]
        public void Mountain_triangle_gradient_carries_30vmin_tile() {
            // Pin the randhtml `.world::before` mountain-triangle case.
            // Each layer's tile is independent; the cascade splits the
            // multi-layer position/size/repeat trio in lockstep with the
            // image list.
            var s = Style();
            s.Set("background-image",
                "linear-gradient(135deg, transparent 50%, #1c2030 50%), "
                + "linear-gradient(225deg, transparent 50%, #232838 50%)");
            s.Set("background-position", "0 0, 30px 0");
            s.Set("background-size", "30px 30px, 30px 30px");
            s.Set("background-repeat", "no-repeat, no-repeat");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(120, 60), Bounds(120, 60),
                output, null, null, LengthContext.Default);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Tile.HasValue, Is.True);
            Assert.That(output[1].Tile.HasValue, Is.True);
            var t0 = output[0].Tile.Value;
            Assert.That(t0.TileWidth, Is.EqualTo(30).Within(1e-6));
            Assert.That(t0.OriginX, Is.EqualTo(0).Within(1e-6));
            var t1 = output[1].Tile.Value;
            Assert.That(t1.TileWidth, Is.EqualTo(30).Within(1e-6));
            Assert.That(t1.OriginX, Is.EqualTo(30).Within(1e-6));
        }

        // ---------- BackgroundLayoutResolver per-layer overload ----------

        [Test]
        public void Layout_resolver_per_layer_overload_uses_supplied_strings() {
            // Sanity: passing per-layer strings overrides whatever the style
            // exposes for the joined longhand. Required for the multi-layer
            // resolution path to honor the right values per layer.
            var s = Style();
            s.Set("background-position", "0 0, 50% 50%"); // joined value
            s.Set("background-size", "10px, 50%");
            s.Set("background-repeat", "no-repeat, repeat");
            // Ask for layer 1's values explicitly.
            var t = BackgroundLayoutResolver.Resolve(s, Bounds(200, 100), 8, 8,
                LengthContext.Default, "50% 50%", "50%", "repeat");
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(t.TileWidth, Is.EqualTo(100).Within(1e-6));
        }
    }
}
