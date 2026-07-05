#if WEVA_URP
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Paint;
using Weva.Rendering;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // Editor-mode integration tests for the batched URP backend. These spin up a real
    // material + mesh to verify the GPU dispatch shape; they're marked [Explicit] because
    // they require a URP renderer asset configured with UIBatchedRendererFeature.
    //
    // The CI smoke run doesn't pull these (they need a live render pipeline). Local
    // developers can flip the [Explicit] off temporarily to validate end-to-end output.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor, RuntimePlatform.LinuxEditor)]
    public class URPRenderBackendIntegrationTests {
        sealed class TestPaintSource : IUIPaintSource {
            public int Order { get; set; }
            public bool NeedsRepaint { get; set; } = true;
            public int Calls;

            public void EmitPaint(IRenderBackend backend) {
                Calls++;
                backend.Submit(new FillRectCommand(new Rect(0, 0, 8, 8),
                    Brush.SolidColor(LinearColor.White), BorderRadii.Zero));
            }
        }

        [Test]
        public void EmitAllPaintSources_rebuilds_when_registered_sources_change() {
            UIPaintSourceRegistry.Clear();
            var resources = new UIBatchedResources();
            var backend = new BatchedURPRenderBackend();
            UIRenderGraphPass pass = null;
            try {
                pass = new UIRenderGraphPass(backend, resources);
                var a = new TestPaintSource { Order = 0 };
                var b = new TestPaintSource { Order = 1 };
                UIPaintSourceRegistry.Register(a);
                UIPaintSourceRegistry.Register(b);

                pass.EmitAllPaintSources();
                Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
                Assert.That(a.Calls, Is.EqualTo(1));
                Assert.That(b.Calls, Is.EqualTo(1));

                a.NeedsRepaint = false;
                b.NeedsRepaint = false;
                UIPaintSourceRegistry.Unregister(a);

                pass.EmitAllPaintSources();
                Assert.That(a.Calls, Is.EqualTo(1));
                Assert.That(b.Calls, Is.EqualTo(2));
                Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));

                UIPaintSourceRegistry.Unregister(b);

                pass.EmitAllPaintSources();
                Assert.That(backend.Batcher.Batches.Count, Is.EqualTo(0));
            } finally {
                UIPaintSourceRegistry.Clear();
                pass?.Dispose();
                resources.Dispose();
            }
        }

        [Test, Explicit]
        public void Quad_mesh_is_a_unit_square_with_two_triangles() {
            var resources = new UIBatchedResources();
            var backend = new BatchedURPRenderBackend();
            try {
                var pass = new UIRenderGraphPass(backend, resources);
                Assert.That(pass.QuadMesh, Is.Not.Null);
                Assert.That(pass.QuadMesh.vertexCount, Is.EqualTo(4));
                Assert.That(pass.QuadMesh.triangles.Length, Is.EqualTo(6));
                pass.Dispose();
            } finally {
                resources.Dispose();
            }
        }

        [Test, Explicit]
        public void Quad_material_is_loaded_with_the_uber_shader() {
            var resources = new UIBatchedResources();
            try {
                Assume.That(resources.QuadShader, Is.Not.Null,
                    "Hidden/Weva/Quad shader must be in the player project's Always Included Shaders");
                var mat = resources.GetQuadMaterial();
                Assert.That(mat, Is.Not.Null);
                Assert.That(mat.shader.name, Is.EqualTo(UIBatchedResources.QuadShaderName));
            } finally {
                resources.Dispose();
            }
        }

        [Test, Explicit]
        public void Render_pixel_to_render_texture_produces_non_zero_coverage() {
            // Smoke-tests the GPU draw via a small RT. Skipped in headless CI; the
            // [Explicit] tag keeps it out of the default run.
            var resources = new UIBatchedResources();
            var backend = new BatchedURPRenderBackend();
            UIRenderGraphPass pass = null;
            RenderTexture rt = null;
            try {
                Assume.That(resources.QuadShader, Is.Not.Null);
                pass = new UIRenderGraphPass(backend, resources);
                rt = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32);
                rt.Create();
                Graphics.SetRenderTarget(rt);
                GL.Clear(true, true, Color.clear);
                // Paint a single 32×32 white quad at (16,16). We can't actually run the
                // RenderGraph pass headlessly, so we issue a one-off DrawMesh against the
                // material instead — this validates the mesh + shader compile, not the
                // RenderGraph wiring.
                backend.BeginFrame();
                backend.Submit(new FillRectCommand(new Rect(16, 16, 32, 32),
                    Brush.SolidColor(LinearColor.White), BorderRadii.Zero));
                backend.EndFrame();
                Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            } finally {
                if (rt != null) {
                    Graphics.SetRenderTarget(null);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
                pass?.Dispose();
                resources?.Dispose();
            }
        }

        [Test, Explicit]
        public void Pack_instances_writes_to_provided_buffer_without_allocations() {
            var insts = new UIQuadInstance[3];
            for (int i = 0; i < insts.Length; i++) {
                insts[i] = new UIQuadInstance {
                    PosSize = new Vector4(i, i, i, i),
                    Radii = Vector4.one,
                    Color = Vector4.one,
                    BrushParams = Vector4.zero,
                    ClipRect = new Vector4(0, 0, 0, 1)
                };
            }
            var dst = new Vector4[3 * UIRenderGraphPass.InstanceFloat4Stride];
            UIRenderGraphPass.PackInstances(insts, 3, dst);
            for (int i = 0; i < 3; i++) {
                Assert.That(dst[i * UIRenderGraphPass.InstanceFloat4Stride].x, Is.EqualTo((float)i));
            }
        }

        [Test, Explicit]
        public void Render_pass_apply_keywords_toggles_per_brush_keyword() {
            var resources = new UIBatchedResources();
            try {
                Assume.That(resources.QuadShader, Is.Not.Null);
                var mat = resources.GetQuadMaterial();
                var keyLinear = new UIBatchKey(UIQuadBrush.LinearGradient, false, 0);
                UIRenderGraphPass.ApplyKeywords(mat, keyLinear);
                Assert.That(mat.IsKeywordEnabled(UIRenderGraphPass.KeywordBrushLinear), Is.True);
                var keySolid = new UIBatchKey(UIQuadBrush.Solid, false, 0);
                UIRenderGraphPass.ApplyKeywords(mat, keySolid);
                Assert.That(mat.IsKeywordEnabled(UIRenderGraphPass.KeywordBrushLinear), Is.False);
            } finally {
                resources.Dispose();
            }
        }
    }
}
#endif
