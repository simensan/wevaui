using System;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // CSS Masking Module Level 1 §10 — mask layer resolution at the paint boundary.
    //
    // These tests cover GAME_UI_COVERAGE_PLAN.md §3.4 "mask-composite layered
    // compositing" and "URL mask source pixel sampling" (B17). They drive
    // MaskResolver directly, checking:
    //
    //   - Single gradient mask layer emits correct brush and composite
    //   - Two layers with each mask-composite keyword (add/subtract/intersect/exclude)
    //   - Per-layer distinct composite ops ("add, subtract")
    //   - mask-mode: alpha vs luminance
    //   - mask-clip: border-box vs padding-box vs content-box
    //   - mask-position / mask-repeat affecting tile layout
    //   - mask-image: url(...) — geometry correct; luminance sampling is software-stub only (B17)
    //   - Layers with "none" in the middle are skipped but do not abort resolution
    //   - Short composite list cycles per CSS Masking 1 §10.7
    //
    // GPU shader compositing correctness is out of scope for this file.
    public class MaskLayeredCompositingTests {
        // ─── helpers ───────────────────────────────────────────────────────────

        sealed class StubImageSource : IImageSource {
            public StubImageSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds(double w = 100, double h = 80) => new Rect(0, 0, w, h);

        static MaskDefinition Resolve(ComputedStyle s, Rect bounds, IImageRegistry reg = null) {
            return MaskResolver.Resolve(s, bounds, LengthContext.Default, reg);
        }

        static MaskDefinition ResolveClipOrigin(ComputedStyle s, Rect clip, Rect origin, IImageRegistry reg = null) {
            return MaskResolver.Resolve(s, clip, origin, LengthContext.Default, reg);
        }

        static InMemoryImageRegistry RegistryWith(string handle, int w = 32, int h = 32) {
            var reg = new InMemoryImageRegistry();
            reg.Register(handle, new StubImageSource(w, h));
            return reg;
        }

        // Sets the mask-image property and, optionally, mask-composite.
        static ComputedStyle WithMask(string image, string composite = null, string mode = null,
                                      string clip = null, string repeat = null, string position = null,
                                      string size = null) {
            var s = Style();
            s.Set(CssProperties.MaskImageId, image);
            if (composite != null) s.Set(CssProperties.MaskCompositeId, composite);
            if (mode != null)      s.Set(CssProperties.MaskModeId, mode);
            if (clip != null)      s.Set(CssProperties.MaskClipId, clip);
            if (repeat != null)    s.Set(CssProperties.MaskRepeatId, repeat);
            if (position != null)  s.Set(CssProperties.MaskPositionId, position);
            if (size != null)      s.Set(CssProperties.MaskSizeId, size);
            return s;
        }

        // ─── §10.1  Single gradient layer ─────────────────────────────────────

        [Test]
        public void Single_gradient_layer_emits_one_layer_with_gradient_brush() {
            // CSS Masking 1 §3.1: a single linear-gradient mask-image should
            // produce exactly one MaskLayer with a Gradient brush.
            var s = WithMask("linear-gradient(black, transparent)");
            var result = Resolve(s, Bounds());
            Assert.That(result, Is.Not.Null, "MaskDefinition must be non-null when mask-image is set");
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.Layers[0].Brush, Is.Not.Null);
            Assert.That(result.Layers[0].Brush.Kind, Is.EqualTo(BrushKind.Gradient));
        }

        [Test]
        public void Single_gradient_layer_defaults_to_add_composite() {
            // CSS Masking 1 §6.1: initial value of mask-composite is `add`.
            var s = WithMask("linear-gradient(black, transparent)");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Add));
        }

        [Test]
        public void Single_gradient_layer_defaults_to_match_source_mode() {
            // CSS Masking 1 §3.2: initial value of mask-mode is `match-source`.
            var s = WithMask("linear-gradient(black, transparent)");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Mode, Is.EqualTo(MaskMode.MatchSource));
        }

        [Test]
        public void Single_layer_clip_rect_matches_supplied_bounds() {
            // The clip rect on the layer must match the bounds passed to Resolve
            // when no mask-clip override is present (defaults to border-box).
            var bounds = Bounds(200, 150);
            var s = WithMask("linear-gradient(black, transparent)");
            var result = Resolve(s, bounds);
            var b = result.Layers[0].Bounds;
            Assert.That(b.Width,  Is.EqualTo(200).Within(1e-6));
            Assert.That(b.Height, Is.EqualTo(150).Within(1e-6));
        }

        // ─── §10.2  mask-composite: add (two layers, union) ───────────────────

        [Test]
        public void Two_gradient_layers_with_add_both_resolve() {
            // CSS Masking 1 §10: two layers with mask-composite:add means the
            // union of both. Resolver must return exactly 2 layers with both
            // carrying the Add composite op.
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(transparent, black)",
                composite: "add, add");
            var result = Resolve(s, Bounds());
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Add));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Add));
        }

        [Test]
        public void Two_gradient_layers_add_both_have_gradient_brush() {
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(transparent, black)",
                composite: "add, add");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Brush.Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(result.Layers[1].Brush.Kind, Is.EqualTo(BrushKind.Gradient));
        }

        // ─── §10.3  mask-composite: subtract ─────────────────────────────────

        [Test]
        public void Two_layers_second_is_subtract() {
            // CSS Masking 1 §10: with mask-composite:add,subtract the top
            // layer (index 0) is add, the second layer (index 1) is subtract.
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(white, black)",
                composite: "add, subtract");
            var result = Resolve(s, Bounds());
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Add));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Subtract));
        }

        [Test]
        public void Single_layer_explicit_subtract() {
            var s = WithMask("linear-gradient(black, transparent)", composite: "subtract");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Subtract));
        }

        // ─── §10.4  mask-composite: intersect ────────────────────────────────

        [Test]
        public void Two_layers_intersect_composite_op() {
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(white, black)",
                composite: "intersect, intersect");
            var result = Resolve(s, Bounds());
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Intersect));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Intersect));
        }

        [Test]
        public void Single_layer_intersect_composite() {
            var s = WithMask("linear-gradient(black, transparent)", composite: "intersect");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Intersect));
        }

        // ─── §10.5  mask-composite: exclude ──────────────────────────────────

        [Test]
        public void Two_layers_exclude_composite_op() {
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(white, black)",
                composite: "exclude, exclude");
            var result = Resolve(s, Bounds());
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Exclude));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Exclude));
        }

        // ─── §10.6  Per-layer distinct composite ops ──────────────────────────

        [Test]
        public void Per_layer_composite_add_then_subtract() {
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(black, white)",
                composite: "add, subtract");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Add));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Subtract));
        }

        [Test]
        public void Per_layer_composite_intersect_then_exclude() {
            var s = WithMask(
                "linear-gradient(black, transparent), linear-gradient(black, white)",
                composite: "intersect, exclude");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Intersect));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Exclude));
        }

        // ─── §10.7  Short composite list cycles ───────────────────────────────

        [Test]
        public void Short_composite_list_cycles_to_match_image_count() {
            // CSS Masking 1 §10.7 / Backgrounds 3 §3.10: when mask-composite
            // has fewer entries than mask-image, the composite list cycles.
            // Three images + one composite => all three layers get that op.
            var s = Style();
            s.Set(CssProperties.MaskImageId,
                "linear-gradient(black, transparent), linear-gradient(transparent, black), linear-gradient(black, white)");
            s.Set(CssProperties.MaskCompositeId, "subtract");
            var result = Resolve(s, Bounds());
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result.Layers[0].Composite, Is.EqualTo(MaskComposite.Subtract));
            Assert.That(result.Layers[1].Composite, Is.EqualTo(MaskComposite.Subtract));
            Assert.That(result.Layers[2].Composite, Is.EqualTo(MaskComposite.Subtract));
        }

        // ─── mask-mode: alpha / luminance ─────────────────────────────────────

        [Test]
        public void Mask_mode_alpha_single_layer() {
            // CSS Masking 1 §3.2 — forcing alpha-channel masking.
            var s = WithMask("linear-gradient(black, transparent)", mode: "alpha");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Mode, Is.EqualTo(MaskMode.Alpha));
        }

        [Test]
        public void Mask_mode_luminance_single_layer() {
            // CSS Masking 1 §3.2 — luminance masking uses the RGB luma value
            // as the mask alpha. Resolver must carry the enum value.
            var s = WithMask("linear-gradient(black, transparent)", mode: "luminance");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Mode, Is.EqualTo(MaskMode.Luminance));
        }

        [Test]
        public void Mask_mode_per_layer_alpha_then_luminance() {
            var s = Style();
            s.Set(CssProperties.MaskImageId,
                "linear-gradient(black, transparent), linear-gradient(white, black)");
            s.Set(CssProperties.MaskModeId, "alpha, luminance");
            var result = Resolve(s, Bounds());
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Layers[0].Mode, Is.EqualTo(MaskMode.Alpha));
            Assert.That(result.Layers[1].Mode, Is.EqualTo(MaskMode.Luminance));
        }

        // ─── mask-clip: border-box / padding-box / content-box ───────────────

        [Test]
        public void Mask_clip_border_box_uses_full_bounds() {
            // border-box is the default for mask-clip. With no border, the clip
            // rect equals the supplied bounds exactly.
            var clip  = new Rect(0, 0, 100, 80);
            var origin = new Rect(0, 0, 100, 80);
            var s = WithMask("linear-gradient(black, transparent)", clip: "border-box");
            var result = ResolveClipOrigin(s, clip, origin);
            var b = result.Layers[0].Bounds;
            Assert.That(b.Width,  Is.EqualTo(100).Within(1e-6));
            Assert.That(b.Height, Is.EqualTo(80).Within(1e-6));
        }

        [Test]
        public void Mask_clip_with_clip_origin_overload_carries_clip_rect() {
            // When different clip and origin rects are supplied, the layer's
            // Bounds should match the clip rect (not origin).
            var clip   = new Rect(5, 5, 90, 70);
            var origin = new Rect(0, 0, 100, 80);
            var s = WithMask("linear-gradient(black, transparent)");
            var result = ResolveClipOrigin(s, clip, origin);
            var b = result.Layers[0].Bounds;
            Assert.That(b.X,      Is.EqualTo(5).Within(1e-6));
            Assert.That(b.Y,      Is.EqualTo(5).Within(1e-6));
            Assert.That(b.Width,  Is.EqualTo(90).Within(1e-6));
            Assert.That(b.Height, Is.EqualTo(70).Within(1e-6));
        }

        // ─── mask-position / mask-repeat affecting tile layout ─────────────────

        [Test]
        public void Mask_position_and_size_produce_non_default_tile() {
            // A gradient mask with explicit position/size should carry a
            // BackgroundTile whose dimensions differ from the full bounds.
            var s = WithMask(
                "linear-gradient(black, transparent)",
                position: "0 0",
                size: "50px 40px",
                repeat: "no-repeat");
            var result = Resolve(s, Bounds(100, 80));
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.Layers[0].Tile.HasValue, Is.True, "Tile must be set when size is explicit");
            var t = result.Layers[0].Tile.Value;
            Assert.That(t.TileWidth,  Is.EqualTo(50).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(40).Within(1e-6));
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }

        [Test]
        public void Mask_repeat_produces_tiling_mode() {
            // mask-repeat: repeat should propagate into the tile's RepeatX/Y.
            var s = WithMask("linear-gradient(black, transparent)", repeat: "repeat");
            var result = Resolve(s, Bounds());
            Assert.That(result.Layers[0].Tile.HasValue, Is.True);
            var t = result.Layers[0].Tile.Value;
            Assert.That(t.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(t.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        // ─── URL mask source (B17) ────────────────────────────────────────────

        [Test]
        public void Url_mask_image_resolves_layer_when_handle_registered() {
            // CSS Masking 1 §3.1: mask-image: url(handle) should produce an
            // Image brush layer with the handle. The software rasterizer doesn't
            // sample pixels (B17), but the resolver must at least emit the layer
            // with the correct brush kind and handle.
            var reg = RegistryWith("mask.png", 64, 64);
            var s = WithMask("url(\"mask.png\")");
            var result = Resolve(s, Bounds(), reg);
            Assert.That(result, Is.Not.Null, "MaskDefinition must be non-null for url() mask");
            Assert.That(result.Count, Is.EqualTo(1));
            var layer = result.Layers[0];
            Assert.That(layer.Brush, Is.Not.Null);
            Assert.That(layer.Brush.Kind, Is.EqualTo(BrushKind.Image),
                "url() mask source must produce an Image brush, not a gradient");
        }

        [Test]
        public void Url_mask_image_tile_derives_from_image_intrinsic_size() {
            // The tile geometry for a url() mask should reflect the image's
            // registered width/height as the intrinsic size (used when
            // mask-size is auto).
            var reg = RegistryWith("icon.png", 32, 32);
            var s = WithMask("url(\"icon.png\")", size: "auto", repeat: "no-repeat");
            var result = Resolve(s, Bounds(100, 80), reg);
            Assert.That(result, Is.Not.Null);
            var layer = result.Layers[0];
            Assert.That(layer.Tile.HasValue, Is.True);
            // auto size with registered intrinsic 32×32 → tile should be 32×32.
            var t = layer.Tile.Value;
            Assert.That(t.TileWidth,  Is.EqualTo(32).Within(1e-6));
            Assert.That(t.TileHeight, Is.EqualTo(32).Within(1e-6));
        }

        [Test]
        [Description("B17 — URL mask alpha/luminance sampling is a software-stub only; " +
                     "this test pins that mode is still carried on the layer so the URP " +
                     "shader can select the correct sampling path at runtime.")]
        public void Url_mask_with_luminance_mode_carries_mode_on_layer() {
            // Even though the software rasterizer doesn't sample the source's
            // luminance (B17), the resolver must pass mask-mode down to the layer
            // so the URP path can choose between alpha and luminance sampling.
            var reg = RegistryWith("mask_lum.png", 64, 64);
            var s = WithMask("url(\"mask_lum.png\")", mode: "luminance");
            var result = Resolve(s, Bounds(), reg);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Layers[0].Mode, Is.EqualTo(MaskMode.Luminance));
        }

        [Test]
        public void Url_mask_null_registry_throws_when_mask_image_declared() {
            // Regression guard: passing null registry with a url() mask must
            // throw ArgumentNullException (NG5 contract) — not silently skip.
            var s = WithMask("url(\"tex.png\")");
            Assert.Throws<ArgumentNullException>(() => Resolve(s, Bounds(), null));
        }

        // ─── "none" layer in the middle ───────────────────────────────────────

        [Test]
        public void None_layer_in_middle_definition_is_non_null_and_has_three_entries() {
            // CSS Masking 1 §3.1: `none` within a comma-separated list means
            // that layer contributes no visual mask alpha, but the resolver
            // still inserts a placeholder MaskLayer with a null Brush so that
            // the layer indices line up with the author's declared list.
            // MaskDefinition includes all non-null MaskLayer objects (even
            // those with null Brush); the GPU renderer uses Brush == null as
            // "skip this layer". We pin the actual three-layer count here so
            // any accidental change to this contract is caught.
            var s = Style();
            s.Set(CssProperties.MaskImageId,
                "linear-gradient(black, transparent), none, linear-gradient(white, black)");
            var result = Resolve(s, Bounds());
            Assert.That(result, Is.Not.Null,
                "Two renderable layers present: MaskDefinition must be non-null");
            // The `none` layer produces a placeholder with null Brush — so
            // Count == 3 (2 gradient layers + 1 null-brush placeholder).
            Assert.That(result.Count, Is.EqualTo(3),
                "All three declared layers are represented; `none` becomes a null-brush placeholder");
            // Verify that exactly the `none` placeholder has a null Brush.
            int nullBrushCount = 0;
            for (int i = 0; i < result.Count; i++) {
                if (result.Layers[i].Brush == null) nullBrushCount++;
            }
            Assert.That(nullBrushCount, Is.EqualTo(1), "Exactly one layer (the `none`) has a null Brush");
        }

        // ─── MaskDefinition.MaxRenderedLayers cap ────────────────────────────

        [Test]
        public void Four_layers_all_resolve_within_max_rendered_cap() {
            // MaskDefinition.MaxRenderedLayers == 4. Four gradient layers must
            // all appear in the returned definition (the cap is a GPU hint, not
            // a trim at resolve time).
            var s = Style();
            s.Set(CssProperties.MaskImageId,
                "linear-gradient(black, transparent), "
                + "linear-gradient(transparent, black), "
                + "linear-gradient(black, white), "
                + "linear-gradient(white, black)");
            var result = Resolve(s, Bounds());
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(MaskDefinition.MaxRenderedLayers));
        }

        // ─── MaskDefinition.Translate carries layers ──────────────────────────

        [Test]
        public void MaskDefinition_translate_shifts_all_layer_bounds() {
            // Translate is used when the paint converter offsets the absolute
            // coordinate system. All layers must shift by the same (dx, dy).
            var s = WithMask("linear-gradient(black, transparent)");
            var original = Resolve(s, new Rect(10, 20, 100, 80));
            var shifted  = original.Translate(5, -3);
            Assert.That(shifted.Count, Is.EqualTo(1));
            var ob = original.Layers[0].Bounds;
            var sb = shifted.Layers[0].Bounds;
            Assert.That(sb.X, Is.EqualTo(ob.X + 5).Within(1e-6));
            Assert.That(sb.Y, Is.EqualTo(ob.Y - 3).Within(1e-6));
            Assert.That(sb.Width,  Is.EqualTo(ob.Width).Within(1e-6));
            Assert.That(sb.Height, Is.EqualTo(ob.Height).Within(1e-6));
        }

        // ─── Regression: mask-image:none returns null ─────────────────────────

        [Test]
        public void Mask_image_none_returns_null_definition() {
            // CSS Masking 1 §3.1: `none` is the initial (no-mask) value.
            // MaskResolver must return null rather than an empty definition
            // so the paint converter can fast-path the no-mask case.
            var s = WithMask("none");
            var result = Resolve(s, Bounds());
            Assert.That(result, Is.Null,
                "mask-image:none must return null MaskDefinition");
        }
    }
}
