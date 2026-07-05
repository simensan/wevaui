#if WEVA_URP_BATCHER_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Paint.Images;
using Weva.Rendering;
using Weva.Rendering.URP;
using FontStyle = Weva.Paint.FontStyle;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    public class UIBatcherTests {
        [Test]
        public void Single_solid_quad_produces_one_batch_with_one_instance() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
            Assert.That(batcher.Batches[0].InstanceCount, Is.EqualTo(1));
            Assert.That(batcher.Batches[0].Key.Brush, Is.EqualTo(UIQuadBrush.Solid));
        }

        [Test]
        public void Elliptical_corners_pack_x_into_Radii_and_y_into_RadiiY() {
            // border-radius: 70px 60px 64px 72px / 48px 46px 50px 46px
            var radii = new BorderRadii(
                new CornerRadius(70, 48),
                new CornerRadius(60, 46),
                new CornerRadius(64, 50),
                new CornerRadius(72, 46));
            var batcher = new UIBatcher();
            // Box large enough that ClampToBounds leaves the radii untouched.
            batcher.SubmitFillRect(new Rect(0, 0, 400, 200), Brush.SolidColor(LinearColor.White), radii);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            // Radii (slot 1) = horizontal per corner (TL, TR, BR, BL).
            Assert.That(inst.Radii, Is.EqualTo(new Vector4(70, 60, 64, 72)));
            // RadiiY (slot 57) = vertical per corner.
            Assert.That(inst.RadiiY, Is.EqualTo(new Vector4(48, 46, 50, 46)));
        }

        [Test]
        public void Circular_corners_pack_equal_Radii_and_RadiiY() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100),
                Brush.SolidColor(LinearColor.White), BorderRadii.Uniform(12));
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            // Symmetric corner — RadiiY mirrors Radii so the shader's per-axis SDF
            // collapses to the exact circular path.
            Assert.That(inst.Radii, Is.EqualTo(new Vector4(12, 12, 12, 12)));
            Assert.That(inst.RadiiY, Is.EqualTo(inst.Radii));
        }

        [Test]
        public void Zero_radius_leaves_RadiiY_zero() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.Radii, Is.EqualTo(Vector4.zero));
            Assert.That(inst.RadiiY, Is.EqualTo(Vector4.zero));
        }

        [Test]
        public void Image_brush_emits_texture_batch_with_handle_and_uv_rect() {
            var tex = new Texture2D(16, 8);
            try {
                var registry = new InMemoryImageRegistry();
                registry.Register("hero", new Texture2DImageSource(tex));

                var batcher = new UIBatcher { ImageRegistry = registry };
                batcher.SubmitFillRect(new Rect(0, 0, 32, 16),
                    Brush.ImageFullRect("hero", ImageRenderingMode.Auto),
                    BorderRadii.Zero);
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(1));
                var batch = batcher.Batches[0];
                Assert.That(batch.Key.Brush, Is.EqualTo(UIQuadBrush.Image));
                Assert.That(batch.Key.ImageHandle, Is.EqualTo("hero"));
                Assert.That(batch.Key.ImageTexture, Is.SameAs(tex));
                var inst = batch.Instances[0];
                Assert.That(inst.BrushParams.x, Is.EqualTo((float)UIQuadBrush.Image));
                Assert.That(inst.BrushParams.y, Is.EqualTo(0f));
                Assert.That(inst.BrushParams.z, Is.EqualTo(0f));
                Assert.That(inst.BrushParams.w, Is.EqualTo(1f));
                Assert.That(inst.BorderColorTop.x, Is.EqualTo(1f));
            } finally {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void Image_brush_pixel_snap_keeps_sampled_extent_stable_at_fractional_origins() {
            var tex = new Texture2D(16, 16);
            try {
                var registry = new InMemoryImageRegistry();
                registry.Register("icon", new Texture2DImageSource(tex));
                var brush = Brush.ImageFullRect("icon", ImageRenderingMode.Auto);

                var batcher = new UIBatcher { ImageRegistry = registry };
                batcher.SubmitFillRect(new Rect(10, 20, 48, 48), brush, BorderRadii.Zero);
                batcher.SubmitFillRect(new Rect(10.35, 20.65, 48, 48), brush, BorderRadii.Zero);
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(1));
                Assert.That(batcher.Batches[0].InstanceCount, Is.EqualTo(2));
                var integerOrigin = batcher.Batches[0].Instances[0];
                var fractionalOrigin = batcher.Batches[0].Instances[1];
                Assert.That(integerOrigin.PosSize.z * 2f, Is.EqualTo(48f).Within(1e-4f));
                Assert.That(integerOrigin.PosSize.w * 2f, Is.EqualTo(48f).Within(1e-4f));
                Assert.That(fractionalOrigin.PosSize.z * 2f, Is.EqualTo(48f).Within(1e-4f));
                Assert.That(fractionalOrigin.PosSize.w * 2f, Is.EqualTo(48f).Within(1e-4f));
                Assert.That(fractionalOrigin.PosSize.x - fractionalOrigin.PosSize.z, Is.EqualTo(10.35f).Within(1e-4f));
                Assert.That(fractionalOrigin.PosSize.y - fractionalOrigin.PosSize.w, Is.EqualTo(20.65f).Within(1e-4f));
            } finally {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void Conic_overlay_and_image_keep_matching_fractional_bounds() {
            var tex = new Texture2D(16, 16);
            try {
                var registry = new InMemoryImageRegistry();
                registry.Register("icon", new Texture2DImageSource(tex));
                var stops = new List<GradientStop> {
                    new GradientStop(new LinearColor(0f, 0f, 0f, 0.8f), 0.0),
                    new GradientStop(new LinearColor(0f, 0f, 0f, 0.8f), 0.75),
                    new GradientStop(new LinearColor(1f, 1f, 1f, 0.3f), 0.75),
                    new GradientStop(new LinearColor(1f, 1f, 1f, 0.1f), 1.0),
                };
                var gradient = new ConicGradient(0.0, 28, 28, stops);
                var bounds = new Rect(10.35, 20.65, 56, 56);

                var batcher = new UIBatcher { ImageRegistry = registry };
                batcher.SubmitFillRect(bounds, Brush.ImageFullRect("icon", ImageRenderingMode.Auto), BorderRadii.Zero);
                batcher.SubmitFillRect(bounds, Brush.Gradient(gradient), BorderRadii.Zero);
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(2));
                var image = batcher.Batches[0].Instances[0];
                var overlay = batcher.Batches[1].Instances[0];
                Assert.That(image.PosSize.x - image.PosSize.z, Is.EqualTo(overlay.PosSize.x - overlay.PosSize.z).Within(1e-4f));
                Assert.That(image.PosSize.y - image.PosSize.w, Is.EqualTo(overlay.PosSize.y - overlay.PosSize.w).Within(1e-4f));
                Assert.That(image.PosSize.z * 2f, Is.EqualTo(overlay.PosSize.z * 2f).Within(1e-4f));
                Assert.That(image.PosSize.w * 2f, Is.EqualTo(overlay.PosSize.w * 2f).Within(1e-4f));
            } finally {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void Image_batches_disambiguate_same_handle_from_different_registries() {
            var texA = new Texture2D(4, 4);
            var texB = new Texture2D(4, 4);
            try {
                var registryA = new InMemoryImageRegistry();
                registryA.Register("icon", new Texture2DImageSource(texA));
                var registryB = new InMemoryImageRegistry();
                registryB.Register("icon", new Texture2DImageSource(texB));

                var batcher = new UIBatcher { ImageRegistry = registryA };
                batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                    Brush.ImageFullRect("icon", ImageRenderingMode.Auto),
                    BorderRadii.Zero);
                batcher.ImageRegistry = registryB;
                batcher.SubmitFillRect(new Rect(20, 0, 10, 10),
                    Brush.ImageFullRect("icon", ImageRenderingMode.Auto),
                    BorderRadii.Zero);
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(2));
                Assert.That(batcher.Batches[0].Key.ImageHandle, Is.EqualTo("icon"));
                Assert.That(batcher.Batches[0].Key.ImageTexture, Is.SameAs(texA));
                Assert.That(batcher.Batches[1].Key.ImageHandle, Is.EqualTo("icon"));
                Assert.That(batcher.Batches[1].Key.ImageTexture, Is.SameAs(texB));
            } finally {
                Object.DestroyImmediate(texA);
                Object.DestroyImmediate(texB);
            }
        }

        [Test]
        public void Many_identical_quads_merge_into_one_batch() {
            var batcher = new UIBatcher();
            for (int i = 0; i < 200; i++) {
                batcher.SubmitFillRect(new Rect(i, 0, 1, 1), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            }
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
            Assert.That(batcher.Batches[0].InstanceCount, Is.EqualTo(200));
        }

        [Test]
        public void Different_brushes_break_into_separate_batches() {
            // Solid, Shadow, and Solid are all BrushClass 0 — they merge into
            // a single batch. The per-instance `brushIndex` in BrushParams.x
            // dispatches the correct shader path per quad. This collapse was
            // intentional (commit "perf: fold shadow brushes into fill BrushClass").
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.SubmitDrawShadow(new Rect(0, 0, 10, 10), BorderRadii.Zero,
                new BoxShadow(0, 0, 4, 0, LinearColor.Black, false));
            batcher.SubmitFillRect(new Rect(20, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
        }

        [Test]
        public void Outset_shadow_quad_uses_three_sigma_padding_without_inflating_silhouette() {
            var batcher = new UIBatcher();
            batcher.SubmitDrawShadow(new Rect(10, 20, 100, 40), BorderRadii.Zero,
                new BoxShadow(3, 5, 32, 4, LinearColor.Black, false));
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            // CSS blur-radius maps to sigma=blur/2 in the shader. The quad
            // only needs 3 sigma of fade room; padding further than that makes
            // the shader reconstruct a larger source silhouette than the box.
            Assert.That(inst.PosSize.x, Is.EqualTo(63f).Within(1e-4f));
            Assert.That(inst.PosSize.y, Is.EqualTo(45f).Within(1e-4f));
            Assert.That(inst.PosSize.z, Is.EqualTo(102f).Within(1e-4f));
            Assert.That(inst.PosSize.w, Is.EqualTo(72f).Within(1e-4f));
            Assert.That(inst.BrushParams.y, Is.EqualTo(32f));
            Assert.That(inst.BrushParams.z, Is.EqualTo(4f));
            Assert.That(inst.BorderWidths.x, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(inst.BorderWidths.y, Is.EqualTo(5f).Within(1e-4f));
        }

        [Test]
        public void Per_batch_instance_cap_is_respected_with_split_batches() {
            var batcher = new UIBatcher();
            batcher.MaxInstancesPerBatch = 64;
            for (int i = 0; i < 130; i++) {
                batcher.SubmitFillRect(new Rect(i, 0, 1, 1), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            }
            batcher.Finish();
            // 130 / 64 = 2 full + 1 remainder = 3 batches.
            Assert.That(batcher.Batches.Count, Is.EqualTo(3));
            Assert.That(batcher.Batches[0].InstanceCount, Is.LessThanOrEqualTo(64));
            Assert.That(batcher.Batches[1].InstanceCount, Is.LessThanOrEqualTo(64));
            Assert.That(batcher.Batches[2].InstanceCount, Is.EqualTo(130 - 128));
        }

        [Test]
        public void Reset_clears_all_state() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PushClip(new Rect(0, 0, 100, 100), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.GreaterThan(0));
            batcher.Reset();
            Assert.That(batcher.Batches.Count, Is.EqualTo(0));
            Assert.That(batcher.Clips.CurrentRef, Is.EqualTo(0));
            Assert.That(batcher.Clips.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Null_clip_path_push_pop_preserves_enclosing_clip_rect() {
            // An unresolvable clip-path reaches the batcher as a null shape.
            // PushClipPath(null) used to push only the path stack while
            // PopClipPath popped BOTH stacks, so the pop consumed the
            // enclosing PushClip's rect — everything after it in the scope
            // clipped to the grandparent and the eventual PopClip popped one
            // frame too deep, corrupting the rest of the frame.
            var batcher = new UIBatcher();
            batcher.PushClip(new Rect(10, 20, 100, 80), BorderRadii.Zero);

            batcher.PushClipPath(null);
            batcher.PopClipPath();

            // Still inside the PushClip scope: must clip to (10,20)-(110,100).
            batcher.SubmitFillRect(new Rect(0, 0, 140, 120), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopClip();

            // Outside the scope: must be unclipped (NoClipRect sentinel).
            batcher.SubmitFillRect(new Rect(0, 0, 300, 300), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();

            var inside = batcher.Batches[0].Instances[0];
            Assert.That(inside.ClipRect, Is.EqualTo(new Vector4(10f, 20f, 110f, 100f)));

            var outside = batcher.Batches[0].Instances[1];
            Assert.That(outside.ClipRect.x, Is.LessThanOrEqualTo(-1e8f));
            Assert.That(outside.ClipRect.z, Is.GreaterThanOrEqualTo(1e8f));
        }

        [Test]
        public void Rounded_push_clip_encodes_inset_clip_shape_for_instances() {
            var batcher = new UIBatcher();
            batcher.PushClip(new Rect(10, 20, 100, 80), BorderRadii.Uniform(12));
            batcher.SubmitFillRect(new Rect(0, 0, 140, 120), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopClip();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipRect.x, Is.EqualTo(10f));
            Assert.That(inst.ClipRect.y, Is.EqualTo(20f));
            Assert.That(inst.ClipRect.z, Is.EqualTo(110f));
            Assert.That(inst.ClipRect.w, Is.EqualTo(100f));
            Assert.That(inst.ClipShape0.x, Is.EqualTo((float)ClipPathShapeKind.Inset));
            Assert.That(inst.ClipShape0.y, Is.EqualTo(10f));
            Assert.That(inst.ClipShape0.z, Is.EqualTo(20f));
            Assert.That(inst.ClipShape0.w, Is.EqualTo(110f));
            Assert.That(inst.ClipShape1.x, Is.EqualTo(100f));
            Assert.That(inst.ClipShape1.y, Is.EqualTo(12f));
            Assert.That(inst.ClipShape1.z, Is.EqualTo(12f));
            Assert.That(inst.ClipShape1.w, Is.EqualTo(12f));
            Assert.That(inst.ClipShape2.x, Is.EqualTo(12f));
        }

        [Test]
        public void Transformed_rounded_push_clip_keeps_inset_clip_shape() {
            var batcher = new UIBatcher();
            batcher.PushTransform(new Transform2D(1.2f, 0f, 0f, 1.2f, -10f, 4f));
            batcher.PushClip(new Rect(100, 50, 200, 120), BorderRadii.Uniform(24));
            batcher.SubmitFillRect(new Rect(0, 0, 320, 220), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopClip();
            batcher.PopTransform();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipShape0.x, Is.EqualTo((float)ClipPathShapeKind.Inset));
            Assert.That(inst.ClipShape0.y, Is.EqualTo(110f).Within(1e-4f));
            Assert.That(inst.ClipShape0.z, Is.EqualTo(64f).Within(1e-4f));
            Assert.That(inst.ClipShape0.w, Is.EqualTo(350f).Within(1e-4f));
            Assert.That(inst.ClipShape1.x, Is.EqualTo(208f).Within(1e-4f));
            Assert.That(inst.ClipShape1.y, Is.EqualTo(28.8f).Within(1e-4f));
        }

        [Test]
        public void Pop_clip_restores_previous_clip_shape() {
            var batcher = new UIBatcher();
            batcher.PushClip(new Rect(10, 20, 100, 80), BorderRadii.Uniform(12));
            batcher.PopClip();
            batcher.SubmitFillRect(new Rect(0, 0, 20, 20), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipRect.x, Is.LessThan(-9e8f));
            Assert.That(inst.ClipShape0.x, Is.EqualTo(0f));
        }

        [Test]
        public void Border_quads_break_from_solid_quads_via_keyword() {
            // Bordered is intentionally NOT part of UIBatchKey equality (per the
            // comment on UIBatchKey.Equals). Solid + bordered + solid all share
            // BrushClass 0 and merge into one batch; per-instance BorderWidths
            // == zero means "no border rendered" for the non-bordered quads.
            // The `Bordered` flag on the key records the first instance's state
            // but does not force a batch split.
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            var borders = Borders.Uniform(new BorderEdge(BorderStyle.Solid, 1, LinearColor.Black));
            batcher.SubmitStrokeBorder(new Rect(0, 0, 10, 10), borders, BorderRadii.Zero);
            batcher.SubmitFillRect(new Rect(20, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
        }

        [Test]
        public void Subtree_capture_starts_on_batch_boundary() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);

            var marker = batcher.BeginSubtreeCapture();
            batcher.SubmitFillRect(new Rect(20, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.SubmitDrawShadow(new Rect(40, 0, 10, 10), BorderRadii.Zero,
                new BoxShadow(0, 0, 4, 0, LinearColor.Black, false));
            var snap = batcher.EndSubtreeCapture(marker);

            try {
                int count = 0;
                bool sawPreCaptureQuad = false;
                bool sawSubtreeFill = false;
                bool sawSubtreeShadow = false;
                for (int si = 0; si < snap.Segments.Count; si++) {
                    var seg = snap.Segments[si];
                    for (int i = 0; i < seg.Count; i++) {
                        count++;
                        // PosSize = (cx, cy, halfW, halfH); left edge = cx - halfW.
                        float leftX = seg.Instances[i].PosSize.x - seg.Instances[i].PosSize.z;
                        if (Mathf.Approximately(leftX, 0f)) sawPreCaptureQuad = true;
                        if (Mathf.Approximately(leftX, 20f)) sawSubtreeFill = true;
                        if (leftX > 30f && leftX < 40f) sawSubtreeShadow = true;
                    }
                }

                Assert.That(count, Is.EqualTo(2));
                Assert.That(sawPreCaptureQuad, Is.False);
                Assert.That(sawSubtreeFill, Is.True);
                Assert.That(sawSubtreeShadow, Is.True);
            } finally {
                snap?.Recycle();
            }
        }

        [Test]
        public void Pack_instances_lays_fields_in_documented_order() {
            var inst = new UIQuadInstance {
                PosSize = new Vector4(1, 2, 3, 4),
                Radii = new Vector4(5, 6, 7, 8),
                Color = new Vector4(9, 10, 11, 12),
                BrushParams = new Vector4(13, 14, 15, 16),
                BorderWidths = new Vector4(17, 18, 19, 20),
                BorderColorTop = new Vector4(21, 22, 23, 24),
                BorderColorRight = new Vector4(25, 26, 27, 28),
                BorderColorBottom = new Vector4(29, 30, 31, 32),
                BorderColorLeft = new Vector4(33, 34, 35, 36),
                BorderStyles = new Vector4(37, 38, 39, 40),
                TransformRow0 = new Vector4(41, 42, 43, 44),
                TransformRow1 = new Vector4(45, 46, 47, 48),
                TransformRow2 = new Vector4(49, 50, 51, 52),
                ClipRect = new Vector4(53, 54, 55, 56)
            };
            var dst = new Vector4[UIRenderGraphPass.InstanceFloat4Stride];
            UIRenderGraphPass.PackInstances(new[] { inst }, 1, dst);
            Assert.That(dst[0], Is.EqualTo(new Vector4(1, 2, 3, 4)));
            Assert.That(dst[3], Is.EqualTo(new Vector4(13, 14, 15, 16)));
            Assert.That(dst[10], Is.EqualTo(new Vector4(41, 42, 43, 44)));
            Assert.That(dst[13], Is.EqualTo(new Vector4(53, 54, 55, 56)));
        }

        [Test]
        public void Layered_mask_packs_layer_count_and_composite_slots() {
            var batcher = new UIBatcher();
            var layers = new[] {
                new MaskLayer(
                    new Rect(0, 0, 20, 20),
                    Brush.SolidColor(new LinearColor(1f, 1f, 1f, 0.5f)),
                    MaskMode.Alpha,
                    MaskComposite.Intersect,
                    new BackgroundTile(20, 20, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat)),
                new MaskLayer(
                    new Rect(0, 0, 20, 20),
                    Brush.SolidColor(LinearColor.White),
                    MaskMode.Luminance,
                    MaskComposite.Add,
                    new BackgroundTile(20, 20, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat)),
            };
            batcher.PushMask(new MaskDefinition(layers));
            batcher.SubmitFillRect(new Rect(0, 0, 20, 20), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopMask();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.MaskParams0.x, Is.EqualTo(1f));
            Assert.That(inst.MaskParams0.y, Is.EqualTo(41f)); // alpha + intersect*4 + 2 layers*16
            Assert.That(inst.Mask1Params0.x, Is.EqualTo(1f));
            Assert.That(inst.Mask1Params0.y, Is.EqualTo(2f)); // luminance + add*4
        }

        // Regression: the css-effects ".tile-mask" polka-dot. A radial mask fills
        // all four params1 slots with cx/cy/rx/ry, so the stop count had no home and
        // the shader read it from params1.z (radiusX≈0.707 → count==1) — collapsing
        // the radial to a single solid stop (full reveal, no dot). The count now
        // lives in params0.w; params0.z packs repeatX + repeatY*4.
        [Test]
        public void Radial_mask_packs_stop_count_in_params0_w_and_repeat_in_z() {
            var stops = new[] {
                new GradientStop(new LinearColor(0f, 0f, 0f, 1f), 0.0),   // black
                new GradientStop(new LinearColor(0f, 0f, 0f, 1f), 0.45),  // black
                new GradientStop(new LinearColor(0f, 0f, 0f, 0f), 0.64),  // transparent
            };
            var radial = new RadialGradient(32, 32, 45.25, 45.25, RadialGradientShape.Circle, stops);
            var layer = new MaskLayer(
                new Rect(0, 0, 200, 150),
                Brush.Gradient(radial),
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(64, 64, 8, 8, BackgroundRepeatMode.Repeat, BackgroundRepeatMode.Repeat));

            var batcher = new UIBatcher();
            batcher.PushMask(new MaskDefinition(new[] { layer }));
            batcher.SubmitFillRect(new Rect(0, 0, 200, 150), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopMask();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.MaskParams0.x, Is.EqualTo(3f), "radial mask kind");
            Assert.That(inst.MaskParams0.w, Is.EqualTo(3f),
                "3 stops must reach params0.w — count==1 here is the polka-dot bug (single solid stop)");
            Assert.That(inst.MaskParams0.z, Is.EqualTo(0f), "repeat/repeat packs as 0 + 0*4 = 0");
            // params1 = (cx/tileW, cy/tileH, rx/tileW, ry/tileH)
            Assert.That(inst.MaskParams1.x, Is.EqualTo(0.5f).Within(0.01f), "center x normalized by 64px tile");
            Assert.That(inst.MaskParams1.z, Is.EqualTo(45.25f / 64f).Within(0.01f), "radius x normalized by 64px tile");
        }

        [Test]
        public void Norepeat_mask_packs_repeat_as_five_in_params0_z() {
            // NoRepeat == enum 1 on both axes → 1 + 1*4 = 5.
            var layer = new MaskLayer(
                new Rect(0, 0, 100, 80),
                Brush.SolidColor(LinearColor.White),
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(100, 80, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat));

            var batcher = new UIBatcher();
            batcher.PushMask(new MaskDefinition(new[] { layer }));
            batcher.SubmitFillRect(new Rect(0, 0, 100, 80), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopMask();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.MaskParams0.z, Is.EqualTo(5f), "NoRepeat/NoRepeat → 1 + 1*4 = 5");
        }

        [Test]
        public void Default_max_instances_matches_cbuffer_cap() {
            // DefaultMaxInstancesPerBatch = 1024 is the cross-platform constant-buffer
            // cap (64 KB / InstanceFloat4Stride bytes per instance). Authors running a
            // StructuredBuffer path can raise this via MaxInstancesPerBatch.
            Assert.That(UIBatcher.DefaultMaxInstancesPerBatch, Is.EqualTo(1024));
        }

        [Test]
        public void Filter_scope_cache_replay_requires_matching_identity() {
            Assert.That(UIRenderGraphPass.ShouldUseCachedFilterScope(false), Is.False);
            Assert.That(UIRenderGraphPass.ShouldUseCachedFilterScope(true), Is.True);

            var blur = new FilterChain(new FilterFunction[] { new BlurFilter(40) });
            var brightness = new FilterChain(new FilterFunction[] { new BrightnessFilter(1.08) });

            int aurora = UIRenderGraphFilterRuntime.ComputeScopeFingerprint(0, 0, 100, 100, blur);
            int hint = UIRenderGraphFilterRuntime.ComputeScopeFingerprint(20, 20, 30, 20, brightness);
            Assert.That(hint, Is.Not.EqualTo(aurora));
        }

        [Test]
        public void Filter_scope_batches_use_temp_rt_viewport() {
            var cameraViewport = UIRenderGraphPass.MakeViewportVector(1699, 930);
            var scopeViewport = UIRenderGraphPass.MakeViewportVector(92, 49);

            Assert.That(scopeViewport.x, Is.EqualTo(92f));
            Assert.That(scopeViewport.y, Is.EqualTo(49f));
            Assert.That(scopeViewport.z, Is.EqualTo(1f / 92f).Within(0.00001f));
            Assert.That(scopeViewport.w, Is.EqualTo(1f / 49f).Within(0.00001f));
            Assert.That(scopeViewport, Is.Not.EqualTo(cameraViewport),
                "Filter-scope batches must not override the temp RT viewport with the camera viewport.");
        }

        [Test]
        public void Filter_scope_composite_transform_uses_absolute_matrix_coordinates() {
            // Matrix for scale(.2) around a CSS pixel pivot at (100, 100),
            // matching the pivot-baked transform that BoxToPaintConverter
            // stores on PushFilterCommand.ScopeBoxTransform.
            var scaleAroundPivot = new Transform2D(0.2f, 0f, 0f, 0.2f, 80f, 80f);

            var corner = UIRenderGraphFilterRuntime.TransformCompositePoint(scaleAroundPivot, 80, 90);

            Assert.That(corner.x, Is.EqualTo(96f).Within(0.0001f));
            Assert.That(corner.y, Is.EqualTo(98f).Within(0.0001f));
        }

        [Test]
        public void Backdrop_filter_events_require_backdrop_copy_without_mix_blend_mode() {
            var batcher = new UIBatcher();
            var filters = new FilterChain(new FilterFunction[] { new BlurFilter(8) });

            batcher.DrawBackdropFilter(new Rect(0, 0, 100, 100), BorderRadii.Zero, filters);
            batcher.Finish();

            Assert.That(batcher.HasAnyMixBlendMode, Is.False);
            Assert.That(batcher.Batches.Count, Is.EqualTo(0));
            Assert.That(batcher.BackdropFilterEvents.Count, Is.EqualTo(1));
            Assert.That(UIRenderGraphPass.NeedsBackdropCopy(batcher), Is.True);
            Assert.That(UIRenderGraphPass.HasDrainableWork(batcher), Is.True);
        }

        [Test]
        public void Filter_scope_begin_indices_can_collide_when_hover_inserts_scope() {
            var blur = new FilterChain(new FilterFunction[] { new BlurFilter(40) });
            var brightness = new FilterChain(new FilterFunction[] { new BrightnessFilter(1.08) });

            var before = new UIBatcher();
            before.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            before.PushFilter(new Rect(0, 0, 100, 100), blur);
            before.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            before.PopFilter();
            before.Finish();

            var after = new UIBatcher();
            after.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            after.PushFilter(new Rect(20, 20, 30, 20), brightness);
            after.SubmitFillRect(new Rect(20, 20, 30, 20), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            after.PopFilter();
            after.PushFilter(new Rect(0, 0, 100, 100), blur);
            after.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            after.PopFilter();
            after.Finish();

            var beforeScopeKey = before.FilterEvents[0].BeginIndex + 1;
            var insertedScopeKey = after.FilterEvents[0].BeginIndex + 1;
            Assert.That(insertedScopeKey, Is.EqualTo(beforeScopeKey),
                "BeginIndex alone can identify a different filter when a hover rule inserts a new scope.");
            Assert.That(after.FilterEvents[0].Filters.ToString(), Is.Not.EqualTo(before.FilterEvents[0].Filters.ToString()));

            int beforeHash = UIRenderGraphPass.ComputeFilterScopeContentHash(
                before.Batches, before.FilterEvents[0].BeginIndex, before.FilterEvents[0].EndIndex);
            int insertedHash = UIRenderGraphPass.ComputeFilterScopeContentHash(
                after.Batches, after.FilterEvents[0].BeginIndex, after.FilterEvents[0].EndIndex);
            Assert.That(insertedHash, Is.Not.EqualTo(beforeHash));
        }

        [Test]
        public void Empty_batcher_has_no_batches() {
            var batcher = new UIBatcher();
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(0));
        }

        [Test]
        public void Replayed_subtree_snapshot_is_intersected_with_current_parent_clip() {
            var source = new UIBatcher();
            source.PushClip(new Rect(0, 0, 200, 200), BorderRadii.Zero);
            var marker = source.BeginSubtreeCapture();
            source.SubmitFillRect(new Rect(0, 150, 40, 80), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            var snap = source.EndSubtreeCapture(marker);
            source.StampParentContext(snap);
            source.PopClip();

            var replay = new UIBatcher();
            replay.PushClip(new Rect(0, 0, 100, 100), BorderRadii.Zero);
            replay.ReplaySubtreeSnapshot(snap, 0, 0);
            replay.Finish();

            Assert.That(replay.Batches.Count, Is.EqualTo(1));
            var inst = replay.Batches[0].Instances[0];
            Assert.That(inst.ClipRect.w, Is.EqualTo(100f).Within(1e-4f),
                "Retained instances must not keep a stale taller scrollport clip.");

            snap.Recycle();
        }

        [Test]
        public void Replayed_subtree_snapshot_remaps_changed_parent_transform() {
            var source = new UIBatcher();
            source.PushTransform(Transform2D.Translate(0, -10));
            var marker = source.BeginSubtreeCapture();
            source.SubmitFillRect(new Rect(0, 100, 40, 20), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            var snap = source.EndSubtreeCapture(marker);
            source.StampParentContext(snap);
            source.PopTransform();

            var replay = new UIBatcher();
            replay.PushTransform(Transform2D.Translate(0, -50));
            replay.ReplaySubtreeSnapshot(snap, 0, 0);
            replay.Finish();

            Assert.That(replay.Batches.Count, Is.EqualTo(1));
            var inst = replay.Batches[0].Instances[0];
            Assert.That(inst.TransformRow2.y, Is.EqualTo(-50f).Within(1e-4f),
                "Retained subtree replay must use the current parent transform, not the captured one.");

            snap.Recycle();
        }

        [Test]
        public void Coverage_text_atlas_sets_direct_alpha_slot_bit() {
            Weva.Text.Sdf.AtlasRegistry.Clear();
            try {
                var face = new Weva.Text.TextCore.FaceInfo(
                    "coverage-test",
                    "coverage-test/regular",
                    400,
                    Weva.Text.TextCore.FaceInfo.StyleNormal);
                var atlas = new Weva.Text.TextCore.GlyphAtlas();
                Weva.Text.Sdf.AtlasRegistry.RegisterAtlas(face, atlas);
                Weva.Text.Sdf.AtlasRegistry.MarkCoverageAtlas(atlas);
                int atlasId = Weva.Text.Sdf.AtlasRegistry.GetAtlasId(atlas);

                var batcher = new UIBatcher();
                batcher.SubmitGlyphQuads(new[] {
                    new SdfGlyphQuad(
                        new Rect(0, 0, 10, 10),
                        LinearColor.White,
                        new Vector2(0, 0),
                        new Vector2(1, 1),
                        atlasId)
                }, atlasId);
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(1));
                Assert.That((int)batcher.Batches[0].Instances[0].BorderColorTop.y, Is.EqualTo(16));
            } finally {
                Weva.Text.Sdf.AtlasRegistry.Clear();
            }
        }

        [Test]
        public void Text_batch_can_bind_four_distinct_atlases_before_flushing() {
            Weva.Text.Sdf.AtlasRegistry.Clear();
            try {
                int[] ids = new int[5];
                for (int i = 0; i < ids.Length; i++) {
                    var face = new Weva.Text.TextCore.FaceInfo(
                        "atlas-" + i,
                        "atlas-" + i + "/regular",
                        400,
                        Weva.Text.TextCore.FaceInfo.StyleNormal);
                    var atlas = new Weva.Text.TextCore.GlyphAtlas();
                    Weva.Text.Sdf.AtlasRegistry.RegisterAtlas(face, atlas);
                    ids[i] = Weva.Text.Sdf.AtlasRegistry.GetAtlasId(atlas);
                }

                var batcher = new UIBatcher();
                for (int i = 0; i < ids.Length; i++) {
                    batcher.SubmitGlyphQuads(new[] {
                        new SdfGlyphQuad(
                            new Rect(i * 10, 0, 10, 10),
                            LinearColor.White,
                            new Vector2(0, 0),
                            new Vector2(1, 1),
                            ids[i])
                    }, ids[i]);
                }
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(2));
                Assert.That(batcher.Batches[0].AtlasIdSlot0, Is.EqualTo(ids[0]));
                Assert.That(batcher.Batches[0].AtlasIdSlot1, Is.EqualTo(ids[1]));
                Assert.That(batcher.Batches[0].AtlasIdSlot2, Is.EqualTo(ids[2]));
                Assert.That(batcher.Batches[0].AtlasIdSlot3, Is.EqualTo(ids[3]));
                Assert.That((int)batcher.Batches[0].Instances[0].BorderColorTop.y, Is.EqualTo(0));
                Assert.That((int)batcher.Batches[0].Instances[1].BorderColorTop.y, Is.EqualTo(1));
                Assert.That((int)batcher.Batches[0].Instances[2].BorderColorTop.y, Is.EqualTo(2));
                Assert.That((int)batcher.Batches[0].Instances[3].BorderColorTop.y, Is.EqualTo(3));
                Assert.That(batcher.Batches[1].AtlasIdSlot0, Is.EqualTo(ids[4]));
            } finally {
                Weva.Text.Sdf.AtlasRegistry.Clear();
            }
        }

        [Test]
        public void Replay_flushes_when_inflight_batch_holds_a_different_atlas_in_a_needed_slot() {
            // Replayed glyph instances bake their atlas-slot index against the
            // CAPTURED binding. If the in-flight batch already holds a
            // different atlas in that slot, replay used to adopt it silently
            // (the slot was non-empty so the restore was skipped) and the
            // replayed glyphs sampled the wrong texture. Replay must flush on
            // a slot conflict and re-open the batch with the captured binding.
            var batcher = new UIBatcher();

            // Frame 1: capture a glyph run bound to atlas 101.
            var marker = batcher.BeginSubtreeCapture();
            batcher.SubmitGlyphQuads(new[] {
                new SdfGlyphQuad(new Rect(0, 0, 10, 10), LinearColor.White,
                    new Vector2(0, 0), new Vector2(1, 1), 101)
            }, 101);
            var snap = batcher.EndSubtreeCapture(marker);
            Assert.That(snap.Segments[0].AtlasIdSlot0, Is.EqualTo(101));
            batcher.Reset();

            // Frame 2: live text from a DIFFERENT font occupies slot 0 of the
            // in-flight batch, then the retained subtree replays.
            batcher.SubmitGlyphQuads(new[] {
                new SdfGlyphQuad(new Rect(20, 0, 10, 10), LinearColor.White,
                    new Vector2(0, 0), new Vector2(1, 1), 202)
            }, 202);
            batcher.ReplaySubtreeSnapshot(snap, 0, 0);
            batcher.Finish();

            // The conflict must split the draw: live glyphs against 202,
            // replayed glyphs against the captured 101 — never one batch
            // whose binding mismatches half its instances.
            Assert.That(batcher.Batches.Count, Is.EqualTo(2));
            Assert.That(batcher.Batches[0].AtlasIdSlot0, Is.EqualTo(202));
            Assert.That(batcher.Batches[0].InstanceCount, Is.EqualTo(1));
            Assert.That(batcher.Batches[1].AtlasIdSlot0, Is.EqualTo(101));
            Assert.That(batcher.Batches[1].InstanceCount, Is.EqualTo(1));
        }

        [Test]
        public void Replay_adopts_captured_atlas_into_empty_slot_without_flushing() {
            // The compatible case must keep batching: same atlas id, or an
            // empty slot, replays into the in-flight batch with no split.
            var batcher = new UIBatcher();

            var marker = batcher.BeginSubtreeCapture();
            batcher.SubmitGlyphQuads(new[] {
                new SdfGlyphQuad(new Rect(0, 0, 10, 10), LinearColor.White,
                    new Vector2(0, 0), new Vector2(1, 1), 101)
            }, 101);
            var snap = batcher.EndSubtreeCapture(marker);
            batcher.Reset();

            // Same-atlas live content: replay must NOT split the batch.
            batcher.SubmitGlyphQuads(new[] {
                new SdfGlyphQuad(new Rect(20, 0, 10, 10), LinearColor.White,
                    new Vector2(0, 0), new Vector2(1, 1), 101)
            }, 101);
            batcher.ReplaySubtreeSnapshot(snap, 0, 0);
            batcher.Finish();

            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
            Assert.That(batcher.Batches[0].AtlasIdSlot0, Is.EqualTo(101));
            Assert.That(batcher.Batches[0].InstanceCount, Is.EqualTo(2));
        }

        // Regression: `background: linear-gradient(...) 0 0 / 50% 50% no-repeat`
        // must clip to the top-left quadrant. The cascade resolves a
        // BackgroundTile on the gradient brush; UIBatcher packs tile origin
        // + size into slot 4 (BorderWidths) and the no-repeat flags into
        // slot 9 (BorderStyles). The shader uses those slots to discard
        // outside the tile rect, leaving the rest of the box transparent so
        // the underlying layer (or page background) shows through. Without
        // this packing the gradient fills the whole quad regardless of the
        // per-layer rect.
        [Test]
        public void Gradient_brush_with_tile_packs_origin_size_and_norepeat_flags() {
            var batcher = new UIBatcher();
            var stops = new System.Collections.Generic.List<GradientStop> {
                new GradientStop(LinearColor.Black, 0),
                new GradientStop(LinearColor.White, 1),
            };
            var grad = new LinearGradient(135, stops);
            // 100x100 box, top-left 50x50 tile, no-repeat.
            var tile = new BackgroundTile(50, 50, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad, tile), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
            var inst = batcher.Batches[0].Instances[0];
            // Slot 4 = (originX, originY, tileW, tileH) in box-local pixels.
            Assert.That(inst.BorderWidths.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(inst.BorderWidths.y, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(inst.BorderWidths.z, Is.EqualTo(50f).Within(1e-4f));
            Assert.That(inst.BorderWidths.w, Is.EqualTo(50f).Within(1e-4f));
            // Slot 9 = (noRepX, noRepY, gapX, gapY). Both axes no-repeat → 1.
            Assert.That(inst.BorderStyles.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(inst.BorderStyles.y, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(inst.BorderStyles.z, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0f).Within(1e-4f));
        }

        // Without a tile the gradient fills the whole box: the batcher
        // synthesises a default tile (origin 0, size = box, repeat) so the
        // shader's tile-clip path is a no-op for legacy brushes. This pins
        // that default — single-layer gradients without per-layer
        // position/size keep their pre-tile rendering.
        [Test]
        public void Gradient_brush_without_tile_defaults_to_full_box_fill() {
            var batcher = new UIBatcher();
            var stops = new System.Collections.Generic.List<GradientStop> {
                new GradientStop(LinearColor.Black, 0),
                new GradientStop(LinearColor.White, 1),
            };
            batcher.SubmitFillRect(new Rect(10, 20, 100, 80),
                Brush.Gradient(new LinearGradient(0, stops)), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            // Default tile: origin (0, 0), size = (boxW, boxH).
            Assert.That(inst.BorderWidths.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(inst.BorderWidths.y, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(inst.BorderWidths.z, Is.EqualTo(100f).Within(1e-4f));
            Assert.That(inst.BorderWidths.w, Is.EqualTo(80f).Within(1e-4f));
            // Default = repeat in both axes (BorderStyles.xy == 0), so the
            // shader's modulo wrap covers the right/bottom edge pixels.
            Assert.That(inst.BorderStyles.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(inst.BorderStyles.y, Is.EqualTo(0f).Within(1e-4f));
        }

        // ─── Multi-stop gradient packing ─────────────────────────────────────
        // Verifies the slot map documented inside UIBatcher.EmitGradient. The
        // shader reads:
        //   linear  : signed stopCount in BrushParams.w; positions in BorderColorLeft.
        //   conic   : signed stopCount in BorderStyles.x (BrushParams.w is angle);
        //             positions in BorderColorLeft.
        //   radial  : 2-stop only — slot 6 holds (normalized) RadiusY, leaving
        //             no room for stop[2] without a new uniform.
        // For 2 stops the legacy layout is preserved exactly.

        static LinearColor C(float r, float g, float b) => new LinearColor(r, g, b, 1f);

        [Test]
        public void Linear_gradient_two_stops_uses_legacy_slot_layout() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 0, 1), 1.0),
            };
            var grad = new LinearGradient(0, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.x, Is.EqualTo((float)UIQuadBrush.LinearGradient));
            Assert.That(Mathf.Abs(inst.BrushParams.w), Is.EqualTo(2f));
            Assert.That(inst.BrushParams.w, Is.LessThan(0f));
            Assert.That(inst.Color.x, Is.EqualTo(1f));
            Assert.That(inst.Color.z, Is.EqualTo(0f));
            Assert.That(inst.BorderColorTop.x, Is.EqualTo(0f));
            Assert.That(inst.BorderColorTop.z, Is.EqualTo(1f));
        }

        [Test]
        public void Linear_gradient_two_stops_with_explicit_positions_uses_positioned_multistop_layout() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 1, 1), 0.0),
                new GradientStop(new LinearColor(1f, 1f, 1f, 0f), 0.48),
            };
            var grad = new LinearGradient(180, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];

            Assert.That(Mathf.Abs(inst.BrushParams.w), Is.EqualTo(3f));
            Assert.That(inst.BrushParams.w, Is.LessThan(0f));
            Assert.That(inst.Color.w, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(inst.BorderColorTop.w, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(inst.BorderColorRight.w, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.48f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.48f).Within(1e-5f));
        }

        [Test]
        public void Linear_gradient_three_stops_packs_into_slots_2_5_6_with_positions() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 1, 0), 0.5),
                new GradientStop(C(0, 0, 1), 1.0),
            };
            var grad = new LinearGradient(0, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(Mathf.Abs(inst.BrushParams.w), Is.EqualTo(3f));
            Assert.That(inst.BrushParams.w, Is.LessThan(0f));
            Assert.That(inst.Color.x, Is.EqualTo(1f));
            Assert.That(inst.BorderColorTop.y, Is.EqualTo(1f));
            Assert.That(inst.BorderColorRight.z, Is.EqualTo(1f));
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0f));
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(1f));
        }

        [Test]
        public void Linear_gradient_four_stops_packs_into_slots_2_5_6_7_with_positions() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(1, 1, 0), 0.33),
                new GradientStop(C(0, 1, 0), 0.66),
                new GradientStop(C(0, 0, 1), 1.0),
            };
            var grad = new LinearGradient(0, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(Mathf.Abs(inst.BrushParams.w), Is.EqualTo(4f));
            Assert.That(inst.BrushParams.w, Is.LessThan(0f));
            Assert.That(inst.Color.x, Is.EqualTo(1f));
            Assert.That(inst.BorderColorTop.x, Is.EqualTo(1f));
            Assert.That(inst.BorderColorTop.y, Is.EqualTo(1f));
            Assert.That(inst.BorderColorRight.y, Is.EqualTo(1f));
            Assert.That(inst.BorderColorBottom.z, Is.EqualTo(1f));
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0f));
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.33f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.66f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(1f));
        }

        [Test]
        public void Linear_gradient_six_stops_packs_native_six_stop_slots() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(1, 1, 0), 0.2),
                new GradientStop(C(0, 1, 0), 0.4),
                new GradientStop(C(0, 1, 1), 0.6),
                new GradientStop(C(0, 0, 1), 0.8),
                new GradientStop(C(1, 0, 1), 1.0),
            };
            var grad = new LinearGradient(0, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(Mathf.Abs(inst.BrushParams.w), Is.EqualTo(6f));
            Assert.That(inst.BrushParams.w, Is.LessThan(0f));
            Assert.That(inst.Color.x, Is.EqualTo(1f));
            Assert.That(inst.BorderColorBottom.y, Is.EqualTo(1f));
            Assert.That(inst.BorderColorBottom.z, Is.EqualTo(1f));
            Assert.That(inst.GradientStop4.z, Is.EqualTo(1f));
            Assert.That(inst.GradientStop5.x, Is.EqualTo(1f));
            Assert.That(inst.GradientStop5.z, Is.EqualTo(1f));
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0f));
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(Mathf.Abs(inst.BorderStyles.x), Is.EqualTo(6f));
            Assert.That(inst.BorderStyles.x, Is.LessThan(0f));
            Assert.That(inst.BorderStyles.y, Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(inst.BorderStyles.z, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Conic_gradient_six_stops_packs_count_into_border_styles() {
            // BrushParams.w carries FromAngleDegrees for conic — stop count
            // must NOT clobber it; it lives in BorderStyles.x. Conic gradients
            // pack up to 6 stops natively; the extra two colors land in
            // GradientStop4 (slot 14) + GradientStop5 (slot 15), and their
            // positions piggyback on BorderStyles.y/.z.
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0.4f, 0.6f), 0.0),
                new GradientStop(C(1, 0.9f, 0.3f), 0.2),
                new GradientStop(C(0.3f, 0.9f, 0.5f), 0.4),
                new GradientStop(C(0.4f, 0.6f, 1f), 0.6),
                new GradientStop(C(0.7f, 0.5f, 1f), 0.8),
                new GradientStop(C(1, 0.4f, 0.6f), 1.0),
            };
            var grad = new ConicGradient(45.0, 50, 50, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            // BrushParams.w is FromAngleDegrees (45), NOT stopCount.
            Assert.That(inst.BrushParams.w, Is.EqualTo(45f));
            Assert.That(Mathf.Abs(inst.BorderStyles.x), Is.EqualTo(6f));
            Assert.That(inst.BorderStyles.x, Is.LessThan(0f));
            // p4/p5 packed into BorderStyles.y/.z.
            Assert.That(inst.BorderStyles.y, Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(inst.BorderStyles.z, Is.EqualTo(1f).Within(1e-5f));
            // First four positions still land in BorderColorLeft (slot 8).
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0f));
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(0.6f).Within(1e-5f));
            // 5th color (yellow-ish at i=4: 0.7, 0.5, 1.0).
            Assert.That(inst.GradientStop4.x, Is.EqualTo(0.7f).Within(1e-5f));
            // 6th color (pink wrap: 1, 0.4, 0.6).
            Assert.That(inst.GradientStop5.x, Is.EqualTo(1f).Within(1e-5f));
        }

        // GTILE-1 regression: the tiling code in Weva-Quad.shader reads
        // BorderStyles.xy as "no-repeat X/Y" flags (values 0 or 1).  For a
        // 5-stop conic gradient BorderStyles.y carries p4 — the 5th stop
        // position — which equals 1.0 when the last stop is at t=1 (the
        // common case).  The (int)(1.0 + 0.5) == 1 test returned true, making
        // noRepY = true and clipping away the bottom half of every 5-stop
        // conic card.  These tests pin the exact packing so a future change
        // that inadvertently shifts BorderStyles.y away from p4 regresses.

        [Test]
        public void Conic_gradient_five_stops_packs_positions_into_BorderColorLeft_and_BorderStyles() {
            // .card-conic reproduces the visible regression:
            //   conic-gradient(from 0deg, #4f46e5, #ec4899, #f59e0b, #16a34a, #4f46e5)
            // Default positions: {0, 0.25, 0.5, 0.75, 1.0}.
            // p0..p3 → BorderColorLeft.xyzw; p4 → BorderStyles.y; p5 = p4.
            var stops = new List<GradientStop> {
                new GradientStop(C(0.310f, 0.275f, 0.898f), 0.00),   // #4f46e5 indigo
                new GradientStop(C(0.925f, 0.282f, 0.600f), 0.25),   // #ec4899 pink
                new GradientStop(C(0.961f, 0.620f, 0.043f), 0.50),   // #f59e0b amber
                new GradientStop(C(0.086f, 0.639f, 0.161f), 0.75),   // #16a34a green
                new GradientStop(C(0.310f, 0.275f, 0.898f), 1.00),   // #4f46e5 wrap
            };
            var grad = new ConicGradient(0.0, 50, 50, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];

            // stopCount in BorderStyles.x; negative sign = sRGB encoding.
            Assert.That(Mathf.Abs(inst.BorderStyles.x), Is.EqualTo(5f),
                "5-stop conic: abs(BorderStyles.x) must be 5");
            Assert.That(inst.BorderStyles.x, Is.LessThan(0f),
                "5-stop sRGB conic: BorderStyles.x must be negative");
            // p0..p3 land in BorderColorLeft (slot 8).
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0.00f).Within(1e-5f), "p0 = 0.00");
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.25f).Within(1e-5f), "p1 = 0.25");
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.50f).Within(1e-5f), "p2 = 0.50");
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(0.75f).Within(1e-5f), "p3 = 0.75");
            // GTILE-1 regression pin: BorderStyles.y carries p4 = 1.0, NOT a no-repeat flag.
            // If the shader's isTileableLinear guard fires erroneously this still passes the
            // C# packing test — the regression is a shader mis-read, not a CPU packing error.
            Assert.That(inst.BorderStyles.y, Is.EqualTo(1.00f).Within(1e-5f),
                "GTILE-1 regression: BorderStyles.y = p4 = 1.0 (not a no-repeat flag)");
            Assert.That(inst.BorderStyles.z, Is.EqualTo(1.00f).Within(1e-5f),
                "BorderStyles.z = p5 = p4 = 1.0 (symmetric for 5-stop)");
            // Non-repeating: BorderStyles.w must be 0.
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0f).Within(1e-5f),
                "Non-repeating conic: BorderStyles.w = 0 (no repeating flag)");
        }

        [Test]
        public void Conic_gradient_five_stops_positions_are_strictly_monotonic_in_BorderColorLeft() {
            // Regression pin: stop positions must be strictly monotonic (0 ≤ p0 < p1 < p2 < p3 ≤ p4).
            // A packing bug that duplicates or reverses a position causes the gradient walker
            // to produce a constant-color segment (lerp over zero-width interval).
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(1, 0.5f, 0), 0.25),
                new GradientStop(C(1, 1, 0), 0.5),
                new GradientStop(C(0, 1, 0), 0.75),
                new GradientStop(C(0, 0, 1), 1.0),
            };
            var grad = new ConicGradient(0.0, 50, 50, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];

            float p0 = inst.BorderColorLeft.x;
            float p1 = inst.BorderColorLeft.y;
            float p2 = inst.BorderColorLeft.z;
            float p3 = inst.BorderColorLeft.w;
            float p4 = inst.BorderStyles.y;
            Assert.That(p0, Is.LessThan(p1), "p0 < p1");
            Assert.That(p1, Is.LessThan(p2), "p1 < p2");
            Assert.That(p2, Is.LessThan(p3), "p2 < p3");
            Assert.That(p3, Is.LessThan(p4), "p3 < p4");
        }

        [Test]
        public void Conic_gradient_five_stops_non_repeating_does_not_set_repeating_flag() {
            // Guard against a future change that unconditionally sets BorderStyles.w = 1.
            // Non-repeating 5-stop conics must keep .w = 0 so the shader uses the
            // standard Weva_GradientWalk6 path instead of the repeating-conic path.
            // This also verifies the stopCount (5) does NOT collide with the isRepeating
            // flag (which would route through a different HLSL branch).
            var stops = new List<GradientStop> {
                new GradientStop(C(0.310f, 0.275f, 0.898f), 0.00),
                new GradientStop(C(0.925f, 0.282f, 0.600f), 0.25),
                new GradientStop(C(0.961f, 0.620f, 0.043f), 0.50),
                new GradientStop(C(0.086f, 0.639f, 0.161f), 0.75),
                new GradientStop(C(0.310f, 0.275f, 0.898f), 1.00),
            };
            var grad = new ConicGradient(0.0, 50, 50, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(Mathf.Abs(inst.BorderStyles.x), Is.EqualTo(5f));
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0f).Within(1e-5f),
                "Non-repeating 5-stop conic must NOT set the repeating flag (BorderStyles.w = 0)");
        }

        [Test]
        public void Conic_gradient_five_stops_p4_equals_one_for_full_range_gradient() {
            // Explicit regression for GTILE-1 shader mis-read:
            // BorderStyles.y = p4 = 1.0 is the CORRECT value for a 5-stop gradient
            // where the last stop is at t=1.  The shader's old code did
            //   bool noRepY = (int)(BorderStyles.y + 0.5) == 1;
            // which evaluates to true when BorderStyles.y = 1.0, clipping the
            // bottom half of the conic card to transparent (the green-wedge bug).
            // This test pins that the CPU packs p4 = 1.0 so the shader fix
            // (isTileableLinear guard) is the only necessary change.
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 1, 0), 0.5),
                new GradientStop(C(0, 0, 1), 0.75),
                new GradientStop(C(1, 1, 0), 0.875),
                new GradientStop(C(1, 0, 1), 1.0),
            };
            var grad = new ConicGradient(0.0, 50, 50, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            // The last stop is at 1.0 → p4 must be exactly 1.0.
            Assert.That(inst.BorderStyles.y, Is.EqualTo(1.0f).Within(1e-5f),
                "p4 for last stop at t=1.0 must equal 1.0 — GTILE-1 shader guard is needed to prevent this from being read as noRepY");
            // Verify this is NOT a repeating gradient.
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void Radial_gradient_three_stops_packs_middle_stop_and_radius_y() {
            // Radial v1 stays 2-stop — slot 6 carries (normalized) RadiusY.
            // bounds = 100x100, so RadiusX=40 → 0.4 normalized,
            // RadiusY=30 → 0.3 normalized.
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 1, 0), 0.5),
                new GradientStop(C(0, 0, 1), 1.0),
            };
            var grad = new RadialGradient(50, 50, 40, 30, RadialGradientShape.Ellipse, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.x, Is.EqualTo((float)UIQuadBrush.RadialGradient));
            Assert.That(inst.BrushParams.w, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(inst.BorderColorRight.x, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(Mathf.Abs(inst.BorderStyles.x), Is.EqualTo(3f));
            Assert.That(inst.BorderStyles.x, Is.LessThan(0f));
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0f));
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(1f));
            // Color = stop[0] (red), BorderColorTop = stop[1] (green),
            // BorderColorBottom = stop[2] (blue).
            Assert.That(inst.Color.x, Is.EqualTo(1f));
            Assert.That(inst.BorderColorTop.y, Is.EqualTo(1f));
            Assert.That(inst.BorderColorBottom.z, Is.EqualTo(1f));
        }

        // G13c: repeating-conic-gradient must set BorderStyles.w = 1 so the
        // shader's conic branch dispatches to the wrap sampler. Mirrors the
        // layout the repeating-linear branch uses (also flag in .w).
        [Test]
        public void Repeating_conic_gradient_packs_repeating_flag_into_border_styles_w() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 0, 1), 0.25),
                new GradientStop(C(0, 1, 0), 1.0),
            };
            // 3 stops so we land in the multi-stop branch (which packs the
            // repeating flag); the 2-stop fast path's wrap is a no-op since
            // its period equals one full sweep anyway.
            var grad = new ConicGradient(0.0, 50, 50, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: true);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            // stopCount lives in BorderStyles.x (sign carries color space);
            // IsRepeating flag lives in BorderStyles.w.
            Assert.That(Mathf.Abs(inst.BorderStyles.x), Is.EqualTo(3f));
            Assert.That(inst.BorderStyles.w, Is.EqualTo(1f).Within(1e-5f),
                "Repeating-conic must set BorderStyles.w = 1 for the shader wrap dispatch.");
        }

        // G13c: non-repeating conic must leave BorderStyles.w at 0 so the
        // shader keeps using the non-wrap multi-stop walker. Regression pin
        // — without this, a future refactor that always sets the flag would
        // silently change non-repeating conic appearance.
        [Test]
        public void Non_repeating_conic_gradient_leaves_repeating_flag_zero() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 0, 1), 0.25),
                new GradientStop(C(0, 1, 0), 1.0),
            };
            var grad = new ConicGradient(0.0, 50, 50, stops);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0f).Within(1e-5f),
                "Non-repeating conic must leave BorderStyles.w = 0.");
        }

        // G13c: repeating-radial-gradient must set BorderStyles.w = 1.
        [Test]
        public void Repeating_radial_gradient_packs_repeating_flag_into_border_styles_w() {
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 0, 1), 0.5),
            };
            var grad = new RadialGradient(50, 50, 40, 40,
                RadialGradientShape.Circle, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: true);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BorderStyles.w, Is.EqualTo(1f).Within(1e-5f),
                "Repeating-radial must set BorderStyles.w = 1 for the shader wrap dispatch.");
        }

        [Test]
        public void Text_snapshot_cache_reuses_shape_across_subpixel_origin_phase() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new PhaseSnappingAtlas();
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var first = new DrawTextCommand(
                    new Rect(10.2, 20.1, 100, 14),
                    "Resize",
                    font,
                    LinearColor.White,
                    TextDecoration.None);
                var second = new DrawTextCommand(
                    new Rect(10.8, 20.6, 100, 14),
                    "Resize",
                    font,
                    LinearColor.White,
                    TextDecoration.None);

                var batcher1 = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher1, first);
                batcher1.Finish();
                batcher1.Reset();

                var batcher2 = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher2, second);
                batcher2.Finish();

                Assert.That(atlas.ShapeCalls, Is.EqualTo(1));
                Assert.That(batcher2.Batches.Count, Is.EqualTo(1));
                var inst = batcher2.Batches[0].Instances[0];
                double left = inst.PosSize.x - inst.PosSize.z;
                double top = inst.PosSize.y - inst.PosSize.w;
                Assert.That(left, Is.EqualTo(11.0).Within(0.001));
                Assert.That(top, Is.EqualTo(21.0).Within(0.001));
                batcher2.Reset();
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void Text_snapshot_cache_invalidates_when_atlas_version_changes() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 1 };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var command = new DrawTextCommand(
                    new Rect(10, 20, 100, 14),
                    "Scroll",
                    font,
                    LinearColor.White,
                    TextDecoration.None);

                var batcher1 = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher1, command);
                batcher1.Finish();
                Assert.That(batcher1.Batches[0].Instances[0].BrushParams.y, Is.EqualTo(0.1f).Within(0.0001f));

                atlas.VersionValue = 2;

                var batcher2 = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher2, command);
                batcher2.Finish();
                Assert.That(atlas.ShapeCalls, Is.EqualTo(2));
                Assert.That(batcher2.Batches[0].Instances[0].BrushParams.y, Is.EqualTo(0.2f).Within(0.0001f));
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void Text_snapshot_cache_is_skipped_when_atlas_policy_disables_it() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 1, UseSnapshots = false };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var command = new DrawTextCommand(
                    new Rect(10, 20, 100, 14),
                    "Dynamic",
                    font,
                    LinearColor.White,
                    TextDecoration.None);

                var batcher1 = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher1, command);
                batcher1.Finish();

                var batcher2 = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher2, command);
                batcher2.Finish();

                Assert.That(atlas.ShapeCalls, Is.EqualTo(2));
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void PrepareText_runs_before_glyph_emit_for_snapshot_misses() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 1 };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var command = new DrawTextCommand(
                    new Rect(10, 20, 100, 14),
                    "Hover",
                    font,
                    LinearColor.White,
                    TextDecoration.None);
                var list = new PaintList();
                list.Add(command);

                SdfTextRendering.PrepareText(list);

                Assert.That(atlas.PrepareCalls, Is.EqualTo(1));
                Assert.That(atlas.VersionValue, Is.EqualTo(2));

                var batcher = new UIBatcher();
                SdfTextRendering.EmitGlyphs(batcher, command);
                batcher.Finish();

                Assert.That(atlas.ShapeCalls, Is.EqualTo(1));
                Assert.That(batcher.Batches[0].Instances[0].BrushParams.y, Is.EqualTo(0.2f).Within(0.0001f));
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void Batched_backend_prepares_text_before_submitting_commands() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 1 };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var backend = new BatchedURPRenderBackend();
                backend.BeginFrame();
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var command = new DrawTextCommand(
                    new Rect(10, 20, 100, 14),
                    "Frame",
                    font,
                    LinearColor.White,
                    TextDecoration.None);
                var list = new PaintList();
                list.Add(command);

                backend.PrepareText(list);
                for (int i = 0; i < list.Commands.Count; i++) list.Commands[i].Submit(backend);
                backend.EndFrame();

                Assert.That(atlas.PrepareCalls, Is.EqualTo(1));
                Assert.That(atlas.ShapeCalls, Is.EqualTo(1));
                Assert.That(backend.Batcher.Batches[0].Instances[0].BrushParams.y, Is.EqualTo(0.2f).Within(0.0001f));
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void Subtree_batch_snapshot_invalidates_when_atlas_version_changes() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 7 };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var batcher = new UIBatcher();
                var marker = batcher.BeginSubtreeCapture();
                batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
                var snap = batcher.EndSubtreeCapture(marker);
                try {
                    Assert.That(snap, Is.Not.Null);
                    Assert.That(snap.IsValid, Is.True);
                    atlas.VersionValue = 8;
                    Assert.That(snap.IsValid, Is.False);
                } finally {
                    snap?.Recycle();
                }
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void Color_matrix_composition_preserves_css_filter_order() {
            var first = ColorMatrices.Saturate(0.82);
            var second = ColorMatrices.Brightness(0.82);
            var input = new Vector4(0.25f, 0.5f, 0.75f, 0.8f);

            var separate = ColorMatrices.Evaluate(second, ColorMatrices.Evaluate(first, input));
            var combined = ColorMatrices.Evaluate(ColorMatrices.Compose(first, second), input);

            Assert.That(combined.x, Is.EqualTo(separate.x).Within(1e-5f));
            Assert.That(combined.y, Is.EqualTo(separate.y).Within(1e-5f));
            Assert.That(combined.z, Is.EqualTo(separate.z).Within(1e-5f));
            Assert.That(combined.w, Is.EqualTo(separate.w).Within(1e-5f));
        }

        sealed class PhaseSnappingAtlas : IGlyphAtlasWithId {
            public int ShapeCalls { get; private set; }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
                return TryShape(command, output, out _);
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
                ShapeCalls++;
                atlasId = 0;
                double x = command.Bounds.X + PixelSnapDelta(command.Bounds.X);
                double y = command.Bounds.Y + PixelSnapDelta(command.Bounds.Y + command.Bounds.Height);
                output.Add(new SdfGlyphQuad(
                    new Rect(x, y, 6, 10),
                    command.Color,
                    Vector2.zero,
                    Vector2.one));
                return true;
            }

            static double PixelSnapDelta(double value) {
                return System.Math.Floor(value + 0.5) - value;
            }
        }

        sealed class VersionedUvAtlas : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy {
            public long VersionValue;
            public bool UseSnapshots = true;
            public int ShapeCalls { get; private set; }
            public int PrepareCalls { get; private set; }
            public long Version => VersionValue;
            public bool UseTextRunSnapshots => UseSnapshots;

            public void PrepareText(DrawTextCommand command) {
                PrepareCalls++;
                VersionValue = 2;
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
                return TryShape(command, output, out _);
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
                ShapeCalls++;
                atlasId = 0;
                float u0 = VersionValue == 1 ? 0.1f : 0.2f;
                output.Add(new SdfGlyphQuad(
                    command.Bounds,
                    command.Color,
                    new Vector2(u0, 0),
                    new Vector2(u0 + 0.05f, 1)));
                return true;
            }
        }
    }
}
#endif
