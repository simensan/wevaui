using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Diagnostic + regression coverage for the css-effects ".tile-mask"
    // polka-dot (tracker: css-effects "mask longhands" / tiled-radial mask not
    // rendering live). The full chain is:
    //
    //   CSS mask-* longhands
    //     → MaskResolver.ResolveLayer  (this file drives it)         [C#]
    //         · BackgroundLayoutResolver resolves the tile (size/repeat/pos)
    //         · BackgroundResolver.TryParseGradient parses the radial
    //           against a TILE-sized box (0,0,tileW,tileH)
    //     → MaskLayer (brush + BackgroundTile)
    //     → UIBatcher.EncodeMaskLayer   (normalizes radial cx/cy/rx/ry by tile) [C#]
    //     → Weva-Quad.shader mask branch (tiled radial sample)              [GPU]
    //
    // The GPU step can't be pixel-verified headlessly, so these tests pin the
    // C# half: if the resolved tile is 64x64 + repeat and the radial parses
    // against the tile, the data reaching the shader is correct and any visible
    // failure is shader-side. If one of these asserts trips, the bug is C#.
    //
    // TestContext output dumps the actual numbers so a run (even a green one)
    // tells you exactly what the shader is being fed.
    public class MaskTileDiagnosticTests {
        static ComputedStyle Mask(string image, string mode = null, string repeat = null,
                                  string position = null, string size = null,
                                  string clip = null, string origin = null) {
            var s = new ComputedStyle(new Element("div"));
            s.Set(CssProperties.MaskImageId, image);
            if (mode != null)     s.Set(CssProperties.MaskModeId, mode);
            if (repeat != null)   s.Set(CssProperties.MaskRepeatId, repeat);
            if (position != null) s.Set(CssProperties.MaskPositionId, position);
            if (size != null)     s.Set(CssProperties.MaskSizeId, size);
            if (clip != null)     s.Set(CssProperties.MaskClipId, clip);
            if (origin != null)   s.Set(CssProperties.MaskOriginId, origin);
            return s;
        }

        static MaskDefinition Resolve(ComputedStyle s, double w, double h) =>
            MaskResolver.Resolve(s, new Rect(0, 0, w, h), LengthContext.Default, null);

        static void Dump(string label, MaskLayer m, double tileW, double tileH) {
            string brush = m.Brush == null ? "<null>" : m.Brush.Kind.ToString();
            string grad = "-";
            string norm = "-";
            if (m.Brush?.GradientValue is RadialGradient r) {
                grad = $"radial center=({r.CenterX:F2},{r.CenterY:F2}) radius=({r.RadiusX:F2},{r.RadiusY:F2})";
                double tw = tileW > 0 ? tileW : 1, th = tileH > 0 ? tileH : 1;
                norm = $"shader cx/cy=({r.CenterX / tw:F3},{r.CenterY / th:F3}) rx/ry=({r.RadiusX / tw:F3},{r.RadiusY / th:F3})";
            } else if (m.Brush?.GradientValue is LinearGradient l) {
                grad = $"linear angle={l.AngleDegrees:F1} stops={l.Stops.Count}";
            }
            string tile = m.Tile.HasValue
                ? $"tile=(size {m.Tile.Value.TileWidth:F2}x{m.Tile.Value.TileHeight:F2} origin {m.Tile.Value.OriginX:F2},{m.Tile.Value.OriginY:F2} repeat {m.Tile.Value.RepeatX}/{m.Tile.Value.RepeatY})"
                : "tile=<none>";
            TestContext.WriteLine($"[{label}] brush={brush} mode={m.Mode} composite={m.Composite} {grad} {tile} {norm}");
        }

        // The exact ".tile-mask" declaration from css-effects.css.
        [Test]
        public void TileMask_resolves_to_a_64px_repeating_radial_tile() {
            var s = Mask(
                image: "radial-gradient(circle, black 0%, black 45%, transparent 64%)",
                mode: "alpha",
                repeat: "repeat",
                position: "8px 8px",
                size: "64px 64px",
                clip: "border-box",
                origin: "border-box");
            var def = Resolve(s, 200, 150);
            Assert.That(def, Is.Not.Null, "tile-mask produced no mask definition");
            var m = def.Layers[0];
            Dump("tile-mask", m, m.Tile?.TileWidth ?? 0, m.Tile?.TileHeight ?? 0);

            Assert.That(m.Brush, Is.Not.Null, "tile-mask layer has no brush (radial gradient failed to resolve)");
            Assert.That(m.Brush.Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(m.Brush.GradientValue, Is.InstanceOf<RadialGradient>(),
                "tile-mask brush should be a radial gradient");
            Assert.That(m.Tile.HasValue, Is.True, "tile-mask must carry a BackgroundTile for repeat to work");

            var tile = m.Tile.Value;
            // mask-size: 64px 64px → a 64x64 tile, NOT the full 200x150 box.
            Assert.That(tile.TileWidth, Is.EqualTo(64.0).Within(0.5),
                "mask-size:64px must yield a 64px tile (full-box width means tiling was lost → one giant dot = blank)");
            Assert.That(tile.TileHeight, Is.EqualTo(64.0).Within(0.5), "mask-size:64px must yield a 64px tile height");
            // mask-repeat: repeat → both axes repeat.
            Assert.That(tile.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat), "mask-repeat:repeat lost on X");
            Assert.That(tile.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat), "mask-repeat:repeat lost on Y");

            // The radial is parsed against the TILE box (0,0,64,64): center ~32,
            // radius (circle, farthest-corner) ~45. Normalized by tile → ~0.5.
            var rad = (RadialGradient)m.Brush.GradientValue;
            Assert.That(rad.CenterX, Is.EqualTo(32.0).Within(8.0),
                "radial center should sit mid-tile (parsed against the 64px tile, not the 200px box)");
            Assert.That(rad.RadiusX, Is.GreaterThan(0.0), "radial radius must be positive");
            Assert.That(rad.RadiusX, Is.LessThan(64.0),
                "radial radius should be tile-scale (~45); a box-scale radius (~100) means it was parsed against the box not the tile → dots overflow into solid fill");
        }

        // Control: the working ".fade-mask" — a non-repeating full-box linear
        // gradient. Confirms the harness and that a 100% no-repeat mask resolves
        // to a full-box tile (the case that renders correctly today).
        [Test]
        public void FadeMask_resolves_to_full_box_no_repeat_linear() {
            var s = Mask(
                image: "linear-gradient(90deg, transparent, black 34%, black 70%, transparent)",
                mode: "alpha",
                repeat: "no-repeat",
                size: "100% 100%");
            var def = Resolve(s, 200, 150);
            Assert.That(def, Is.Not.Null);
            var m = def.Layers[0];
            Dump("fade-mask", m, m.Tile?.TileWidth ?? 0, m.Tile?.TileHeight ?? 0);

            Assert.That(m.Brush.GradientValue, Is.InstanceOf<LinearGradient>());
            if (m.Tile.HasValue) {
                Assert.That(m.Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
                Assert.That(m.Tile.Value.TileWidth, Is.EqualTo(200.0).Within(1.0),
                    "100% mask-size should fill the box width");
            }
        }

        // Isolation probe in code: does a REPEATING LINEAR mask tile correctly?
        // If linear-repeat resolves a small tile but radial-repeat does not, the
        // divergence is in the radial parse path, not the tile path.
        [Test]
        public void RepeatingLinearMask_resolves_a_small_tile_for_comparison() {
            var s = Mask(
                image: "linear-gradient(90deg, black, transparent)",
                mode: "alpha",
                repeat: "repeat",
                size: "32px 32px");
            var def = Resolve(s, 200, 150);
            Assert.That(def, Is.Not.Null);
            var m = def.Layers[0];
            Dump("linear-tile-32", m, m.Tile?.TileWidth ?? 0, m.Tile?.TileHeight ?? 0);

            Assert.That(m.Tile.HasValue, Is.True);
            Assert.That(m.Tile.Value.TileWidth, Is.EqualTo(32.0).Within(0.5));
            Assert.That(m.Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }
    }
}
