#if WEVA_URP
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering.URP {
    // Pure-C# unit coverage of the BatchedURPRenderBackend conversion path. Exercises the
    // IRenderBackend → UIBatcher → UIQuadInstance pipeline without pulling any Unity
    // rendering dependencies. The integration tests under URPRenderBackendIntegrationTests
    // cover the actual GPU dispatch.
    public class URPRenderBackendTests {
        const float Eps = 1e-4f;

        BatchedURPRenderBackend NewBackend() {
            var b = new BatchedURPRenderBackend();
            b.BeginFrame();
            return b;
        }

        [Test]
        public void Empty_command_list_produces_no_batches() {
            var b = NewBackend();
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(0));
        }

        [Test]
        public void FillRect_solid_emits_one_solid_quad() {
            var b = NewBackend();
            var cmd = new FillRectCommand(new Rect(10, 20, 100, 50), Brush.SolidColor(LinearColor.White));
            b.Submit(cmd);
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(1));
            var batch = b.Batcher.Batches[0];
            Assert.That(batch.Key.Brush, Is.EqualTo(UIQuadBrush.Solid));
            Assert.That(batch.Key.Bordered, Is.False);
            Assert.That(batch.InstanceCount, Is.EqualTo(1));
            // Center = (60, 45), halfSize = (50, 25)
            Assert.That(batch.Instances[0].PosSize.x, Is.EqualTo(60f).Within(Eps));
            Assert.That(batch.Instances[0].PosSize.y, Is.EqualTo(45f).Within(Eps));
            Assert.That(batch.Instances[0].PosSize.z, Is.EqualTo(50f).Within(Eps));
            Assert.That(batch.Instances[0].PosSize.w, Is.EqualTo(25f).Within(Eps));
        }

        [Test]
        public void Linear_gradient_brush_packs_direction_into_brush_params() {
            var b = NewBackend();
            var stops = new List<GradientStop> {
                new GradientStop(LinearColor.Black, 0),
                new GradientStop(LinearColor.White, 1)
            };
            var grad = new LinearGradient(0, stops); // 0 deg = +X
            var cmd = new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad));
            b.Submit(cmd);
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(1));
            var inst = b.Batcher.Batches[0].Instances[0];
            Assert.That(b.Batcher.Batches[0].Key.Brush, Is.EqualTo(UIQuadBrush.LinearGradient));
            Assert.That(inst.BrushParams.x, Is.EqualTo((float)UIQuadBrush.LinearGradient).Within(Eps));
            // cos(0) = 1, sin(0) = 0
            Assert.That(inst.BrushParams.y, Is.EqualTo(1f).Within(Eps));
            Assert.That(inst.BrushParams.z, Is.EqualTo(0f).Within(Eps));
            // Stop count packed in .w; sign carries default sRGB interpolation.
            Assert.That(System.Math.Abs(inst.BrushParams.w), Is.EqualTo(2f).Within(Eps));
            Assert.That(inst.BrushParams.w, Is.LessThan(0f));
        }

        [Test]
        public void Conic_gradient_brush_packs_center_and_from_angle() {
            var b = NewBackend();
            var stops = new List<GradientStop> {
                new GradientStop(LinearColor.Black, 0),
                new GradientStop(LinearColor.White, 1)
            };
            var grad = new ConicGradient(45, 50, 60, stops);
            var cmd = new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad));
            b.Submit(cmd);
            b.EndFrame();
            var inst = b.Batcher.Batches[0].Instances[0];
            Assert.That(b.Batcher.Batches[0].Key.Brush, Is.EqualTo(UIQuadBrush.ConicGradient));
            Assert.That(inst.BrushParams.x, Is.EqualTo((float)UIQuadBrush.ConicGradient).Within(Eps));
            Assert.That(inst.BrushParams.y, Is.EqualTo(50f).Within(Eps));
            Assert.That(inst.BrushParams.z, Is.EqualTo(60f).Within(Eps));
            Assert.That(inst.BrushParams.w, Is.EqualTo(45f).Within(Eps));
        }

        [Test]
        public void Multiple_solid_fillrects_coalesce_into_one_batch() {
            var b = NewBackend();
            for (int i = 0; i < 5; i++) {
                b.Submit(new FillRectCommand(new Rect(i * 10, 0, 10, 10), Brush.SolidColor(LinearColor.White)));
            }
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(1));
            Assert.That(b.Batcher.Batches[0].InstanceCount, Is.EqualTo(5));
        }

        [Test]
        public void Different_brush_kinds_break_the_batch() {
            var b = NewBackend();
            b.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White)));
            var stops = new List<GradientStop> {
                new GradientStop(LinearColor.Black, 0),
                new GradientStop(LinearColor.White, 1)
            };
            b.Submit(new FillRectCommand(new Rect(20, 0, 10, 10), Brush.Gradient(new LinearGradient(0, stops))));
            b.Submit(new FillRectCommand(new Rect(40, 0, 10, 10), Brush.SolidColor(LinearColor.White)));
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(3));
            Assert.That(b.Batcher.Batches[0].Key.Brush, Is.EqualTo(UIQuadBrush.Solid));
            Assert.That(b.Batcher.Batches[1].Key.Brush, Is.EqualTo(UIQuadBrush.LinearGradient));
            Assert.That(b.Batcher.Batches[2].Key.Brush, Is.EqualTo(UIQuadBrush.Solid));
        }

        [Test]
        public void StrokeBorder_emits_bordered_quad_with_per_edge_data() {
            var b = NewBackend();
            var borders = new Borders(
                new BorderEdge(BorderStyle.Solid, 1, LinearColor.Black),
                new BorderEdge(BorderStyle.Dashed, 2, new LinearColor(1, 0, 0, 1)),
                new BorderEdge(BorderStyle.Dotted, 3, new LinearColor(0, 1, 0, 1)),
                new BorderEdge(BorderStyle.Double, 4, new LinearColor(0, 0, 1, 1)));
            b.Submit(new StrokeBorderCommand(new Rect(0, 0, 100, 50), borders, BorderRadii.Zero));
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(1));
            var batch = b.Batcher.Batches[0];
            Assert.That(batch.Key.Bordered, Is.True);
            var inst = batch.Instances[0];
            Assert.That(inst.BorderWidths.x, Is.EqualTo(1f).Within(Eps));
            Assert.That(inst.BorderWidths.y, Is.EqualTo(2f).Within(Eps));
            Assert.That(inst.BorderWidths.z, Is.EqualTo(3f).Within(Eps));
            Assert.That(inst.BorderWidths.w, Is.EqualTo(4f).Within(Eps));
            Assert.That((int)inst.BorderStyles.x, Is.EqualTo((int)BorderStyle.Solid));
            Assert.That((int)inst.BorderStyles.y, Is.EqualTo((int)BorderStyle.Dashed));
            Assert.That((int)inst.BorderStyles.z, Is.EqualTo((int)BorderStyle.Dotted));
            Assert.That((int)inst.BorderStyles.w, Is.EqualTo((int)BorderStyle.Double));
            // Red right border:
            Assert.That(inst.BorderColorRight.x, Is.EqualTo(1f).Within(Eps));
        }

        [Test]
        public void DrawShadow_outset_uses_shadow_brush_kind() {
            var b = NewBackend();
            var shadow = new BoxShadow(0, 0, 8, 0, new LinearColor(0, 0, 0, 0.5f), inset: false);
            b.Submit(new DrawShadowCommand(new Rect(50, 50, 100, 100), BorderRadii.Zero, shadow));
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(1));
            Assert.That(b.Batcher.Batches[0].Key.Brush, Is.EqualTo(UIQuadBrush.Shadow));
            var inst = b.Batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.y, Is.EqualTo(8f).Within(Eps));
            Assert.That(inst.BrushParams.w, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void DrawShadow_inset_uses_shadow_inset_brush_kind() {
            var b = NewBackend();
            var shadow = new BoxShadow(3, 5, 4, 0, LinearColor.Black, inset: true);
            b.Submit(new DrawShadowCommand(new Rect(0, 0, 100, 100), BorderRadii.Zero, shadow));
            b.EndFrame();
            Assert.That(b.Batcher.Batches[0].Key.Brush, Is.EqualTo(UIQuadBrush.ShadowInset));
            var inst = b.Batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.w, Is.EqualTo(1f).Within(Eps));
            Assert.That(inst.PosSize.x, Is.EqualTo(50f).Within(Eps));
            Assert.That(inst.PosSize.y, Is.EqualTo(50f).Within(Eps));
            Assert.That(inst.BorderWidths.x, Is.EqualTo(3f).Within(Eps));
            Assert.That(inst.BorderWidths.y, Is.EqualTo(5f).Within(Eps));
        }

        [Test]
        public void PushClip_increments_stencil_ref_and_pop_decrements_it() {
            var b = NewBackend();
            Assert.That(b.Batcher.Clips.CurrentRef, Is.EqualTo(0));
            b.Submit(new PushClipCommand(new Rect(0, 0, 100, 100)));
            Assert.That(b.Batcher.Clips.CurrentRef, Is.EqualTo(1));
            b.Submit(new PushClipCommand(new Rect(10, 10, 80, 80)));
            Assert.That(b.Batcher.Clips.CurrentRef, Is.EqualTo(2));
            b.Submit(new PopClipCommand());
            Assert.That(b.Batcher.Clips.CurrentRef, Is.EqualTo(1));
            b.Submit(new PopClipCommand());
            Assert.That(b.Batcher.Clips.CurrentRef, Is.EqualTo(0));
            b.EndFrame();
        }

        [Test]
        public void PushClip_records_event_for_render_pass_replay() {
            var b = NewBackend();
            b.Submit(new PushClipCommand(new Rect(0, 0, 100, 100), BorderRadii.Uniform(8)));
            b.Submit(new PopClipCommand());
            b.EndFrame();
            var evts = b.Batcher.Clips.Events;
            Assert.That(evts.Count, Is.EqualTo(2));
            Assert.That(evts[0].Kind, Is.EqualTo(StencilClipManager.ClipEventKind.Push));
            Assert.That(evts[0].Frame.Ref, Is.EqualTo(1));
            Assert.That(evts[0].Frame.Radii.TopLeft.XRadius, Is.EqualTo(8));
            Assert.That(evts[1].Kind, Is.EqualTo(StencilClipManager.ClipEventKind.Pop));
        }

        [Test]
        public void Transform_stack_composes_through_pushes() {
            var b = NewBackend();
            b.Submit(new PushTransformCommand(Transform2D.Translate(10, 20)));
            b.Submit(new FillRectCommand(new Rect(0, 0, 5, 5), Brush.SolidColor(LinearColor.White)));
            b.EndFrame();
            // The instance row 2 holds (Tx, Ty).
            var inst = b.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow2.x, Is.EqualTo(10f).Within(Eps));
            Assert.That(inst.TransformRow2.y, Is.EqualTo(20f).Within(Eps));
        }

        [Test]
        public void Push_pop_transform_restores_previous_transform() {
            var b = NewBackend();
            b.Submit(new PushTransformCommand(Transform2D.Translate(10, 20)));
            b.Submit(new PushTransformCommand(Transform2D.Translate(5, 5)));
            b.Submit(new PopTransformCommand());
            b.Submit(new FillRectCommand(new Rect(0, 0, 5, 5), Brush.SolidColor(LinearColor.White)));
            b.Submit(new PopTransformCommand());
            b.EndFrame();
            var inst = b.Batcher.Batches[0].Instances[0];
            // Inner pop returns to outer translate (10, 20).
            Assert.That(inst.TransformRow2.x, Is.EqualTo(10f).Within(Eps));
            Assert.That(inst.TransformRow2.y, Is.EqualTo(20f).Within(Eps));
        }

        [Test]
        public void Opacity_stack_premultiplies_alpha_into_instance_color() {
            var b = NewBackend();
            b.Submit(new PushOpacityCommand(0.5));
            b.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White)));
            b.Submit(new PopOpacityCommand());
            b.EndFrame();
            var inst = b.Batcher.Batches[0].Instances[0];
            // Premultiplied: (1*0.5, 1*0.5, 1*0.5, 0.5)
            Assert.That(inst.Color.x, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(inst.Color.w, Is.EqualTo(0.5f).Within(Eps));
        }

        [Test]
        public void Clip_change_breaks_batch_even_with_same_brush() {
            var b = NewBackend();
            b.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White)));
            b.Submit(new PushClipCommand(new Rect(0, 0, 100, 100)));
            b.Submit(new FillRectCommand(new Rect(20, 20, 10, 10), Brush.SolidColor(LinearColor.White)));
            b.Submit(new PopClipCommand());
            b.EndFrame();
            // Two batches with different stencil refs.
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(2));
            Assert.That(b.Batcher.Batches[0].Key.StencilRef, Is.EqualTo(0));
            Assert.That(b.Batcher.Batches[1].Key.StencilRef, Is.EqualTo(1));
        }

        [Test]
        public void DrawText_in_fallback_mode_emits_per_glyph_rectangles() {
            var b = NewBackend();
            // Without an atlas registered, SdfTextRendering falls back to advance-width rects.
            var prev = SdfTextRendering.Atlas;
            SdfTextRendering.SetAtlas(null);
            try {
                var font = new FontHandle("default", 14, 400, Weva.Paint.FontStyle.Normal);
                b.Submit(new DrawTextCommand(new Rect(0, 0, 100, 14), "Hi", font, LinearColor.Black, default));
                b.EndFrame();
                // Two letters → at least one quad.
                int total = 0;
                for (int i = 0; i < b.Batcher.Batches.Count; i++) total += b.Batcher.Batches[i].InstanceCount;
                Assert.That(total, Is.GreaterThan(0));
            } finally {
                SdfTextRendering.SetAtlas(prev);
            }
        }

        [Test]
        public void Begin_frame_resets_state_from_previous_frame() {
            var b = NewBackend();
            b.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White)));
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(1));
            b.BeginFrame();
            b.EndFrame();
            Assert.That(b.Batcher.Batches.Count, Is.EqualTo(0));
        }

        // Regression: any `filter: blur(...)` declaration on any descendant
        // must not perturb the batcher's opacity stack. The earlier
        // implementation routed PushFilter through Batcher.PushOpacity(1f) as
        // a "marker"; that pushed an OpacityLayer onto opacityLayers AND
        // forced FlushCurrentBatch, which in practice caused upstream content
        // (e.g. the body's gradient bg in the match3 demo) to render as
        // solid white. PushFilter is now a true no-op.
        [Test]
        public void PushFilter_unsupported_chain_does_not_touch_opacity_stack() {
            var b = NewBackend();
            var filters = new FilterChain(new FilterFunction[] { new BlurFilter(20) });
            b.Submit(new PushFilterCommand(new Rect(0, 0, 100, 100), filters));
            Assert.That(b.Batcher.OpacityLayerCount, Is.EqualTo(0),
                "PushFilter must not push an OpacityLayer marker");
            Assert.That(b.Batcher.CurrentOpacity, Is.EqualTo(1f),
                "PushFilter must not perturb currentOpacity");
            b.Submit(new PopFilterCommand());
            Assert.That(b.Batcher.OpacityLayerCount, Is.EqualTo(0));
            b.EndFrame();
        }

        [Test]
        public void Body_background_quad_emitted_before_child_with_filter_blur() {
            // Mirrors the match3-demo repro:
            //   body { background: red }
            //   div { position: fixed; inset: 0; filter: blur(20px) }
            // The body's red FillRect must end up in the batch list.
            var b = NewBackend();
            var red = new LinearColor(1f, 0f, 0f, 1f);
            b.Submit(new FillRectCommand(new Rect(0, 0, 800, 600), Brush.SolidColor(red)));
            var filters = new FilterChain(new FilterFunction[] { new BlurFilter(20) });
            b.Submit(new PushFilterCommand(new Rect(0, 0, 800, 600), filters));
            b.Submit(new FillRectCommand(new Rect(0, 0, 800, 600), Brush.SolidColor(LinearColor.Transparent)));
            b.Submit(new PopFilterCommand());
            b.EndFrame();

            bool sawRedBody = false;
            for (int i = 0; i < b.Batcher.Batches.Count; i++) {
                var batch = b.Batcher.Batches[i];
                if (batch.Key.Brush != UIQuadBrush.Solid) continue;
                for (int j = 0; j < batch.InstanceCount; j++) {
                    var inst = batch.Instances[j];
                    if (inst.Color.x > 0.5f && inst.Color.w > 0.5f) {
                        sawRedBody = true;
                        break;
                    }
                }
                if (sawRedBody) break;
            }
            Assert.That(sawRedBody, Is.True,
                "Body's red background quad must survive a child's PushFilter / PopFilter — " +
                "the no-op filter must not drop or recolor upstream batches.");
            Assert.That(b.Batcher.OpacityLayerCount, Is.EqualTo(0));
        }

        [Test]
        public void PushFilter_PopFilter_balanced_stack_after_endframe() {
            var b = NewBackend();
            var filters = new FilterChain(new FilterFunction[] { new BlurFilter(10) });
            b.Submit(new PushFilterCommand(new Rect(0, 0, 50, 50), filters));
            b.Submit(new PushFilterCommand(new Rect(0, 0, 25, 25), filters));
            b.Submit(new PopFilterCommand());
            b.Submit(new PopFilterCommand());
            b.EndFrame();
            Assert.That(b.Batcher.OpacityLayerCount, Is.EqualTo(0));
            Assert.That(b.Batcher.CurrentOpacity, Is.EqualTo(1f));
        }
    }
}
#endif
