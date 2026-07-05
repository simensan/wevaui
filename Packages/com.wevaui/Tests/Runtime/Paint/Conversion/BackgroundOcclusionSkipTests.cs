using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Background-layer occlusion skip (BoxToPaintConverter): when an opaque
    // layer fully covers the paint rect, the layers beneath it are invisible
    // and must not be emitted. Beyond the wasted fill, a lower layer carries
    // the box's rounded-rect AA, and its corner AA edge bleeds the backdrop
    // through the opaque layer's own AA edge — the gray corner fringe on
    // multi-layer backgrounds (menu.html .card-gradient: opaque gradient over
    // a `white` background-color). Skipping the occluded layers removes the
    // fringe AND a draw call.
    public class BackgroundOcclusionSkipTests {
        // Counts FillRects covering (≈) the element's full box, split by brush
        // kind. The element is the only painted box in these fixtures, so a
        // box-sized fill is a background layer.
        static (int solid, int gradient) BoxFillKinds(IReadOnlyList<PaintCommand> cmds, double w, double h) {
            int solid = 0, gradient = 0;
            foreach (var c in cmds) {
                if (!(c is FillRectCommand fr) || fr.Brush == null) continue;
                if (System.Math.Abs(fr.Bounds.Width - w) > 1.5 || System.Math.Abs(fr.Bounds.Height - h) > 1.5) continue;
                if (fr.Brush.Kind == BrushKind.SolidColor) solid++;
                else if (fr.Brush.Kind == BrushKind.Gradient) gradient++;
            }
            return (solid, gradient);
        }

        [Test]
        public void Opaque_gradient_over_color_skips_the_hidden_color_layer() {
            // The opaque gradient fully covers the box, so the `white`
            // background-color beneath it is never visible — only the gradient
            // fill should be emitted.
            const string css = @"
                #t { width: 120px; height: 80px; border-radius: 12px;
                     background: linear-gradient(135deg, #fef3c7, #fde68a), white; }
            ";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 400, viewportHeight: 300);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var (solid, gradient) = BoxFillKinds(cmds, 120, 80);
            Assert.That(gradient, Is.EqualTo(1), "the opaque gradient layer paints");
            Assert.That(solid, Is.EqualTo(0),
                "the white background-color is fully occluded by the opaque gradient and must be skipped");
        }

        [Test]
        public void Semi_transparent_top_layer_keeps_the_layer_beneath() {
            // The gradient fades to transparent, so the `white` beneath shows
            // through — it must NOT be skipped.
            const string css = @"
                #t { width: 120px; height: 80px;
                     background: linear-gradient(135deg, rgba(255,0,0,1), rgba(255,0,0,0)), white; }
            ";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 400, viewportHeight: 300);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var (solid, gradient) = BoxFillKinds(cmds, 120, 80);
            Assert.That(gradient, Is.EqualTo(1), "gradient layer paints");
            Assert.That(solid, Is.EqualTo(1),
                "the white layer is visible through the transparent gradient stop and must be kept");
        }

        [Test]
        public void Background_blend_mode_disables_the_skip() {
            // background-blend-mode blends each layer with the ones beneath, so
            // a lower layer contributes even under an opaque upper layer — the
            // skip must be disabled (radar minimap-bg pattern).
            const string css = @"
                #t { width: 120px; height: 80px;
                     background: linear-gradient(135deg, #fef3c7, #fde68a), white;
                     background-blend-mode: overlay; }
            ";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 400, viewportHeight: 300);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var (solid, gradient) = BoxFillKinds(cmds, 120, 80);
            Assert.That(gradient, Is.EqualTo(1), "gradient layer paints");
            Assert.That(solid, Is.EqualTo(1),
                "background-blend-mode:overlay must keep the lower layer for the blend");
        }

        [Test]
        public void Opaque_solid_over_gradient_skips_the_gradient() {
            // A trailing opaque solid color is the BOTTOM layer; an opaque
            // gradient on top hides it. (Layer order: gradient top, color
            // bottom.) Only the top gradient should paint.
            const string css = @"
                #t { width: 120px; height: 80px;
                     background: linear-gradient(90deg, #112233, #445566), #abcdef; }
            ";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 400, viewportHeight: 300);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var (solid, gradient) = BoxFillKinds(cmds, 120, 80);
            Assert.That(gradient, Is.EqualTo(1));
            Assert.That(solid, Is.EqualTo(0), "opaque gradient hides the trailing solid color");
        }
    }
}
