#if WEVA_URP
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Paint;
using Weva.Rendering;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.Backend {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md K5:
    //   The legacy URPRenderBackend caps the per-draw gradient-stop shader uniform at
    //   8 entries (Weva_Gradient.shader:55 declares `float4 _WevaGradientStops[8]`)
    //   and silently truncates anything beyond. Before K5 there was no diagnostic, so a
    //   9+ stop CSS gradient on the legacy path rendered with the trailing stops dropped
    //   without any author-facing signal. K5 wired a Debug.LogWarning into EmitGradient
    //   that fires once per frame when truncation actually happens.
    //
    // The active batched RenderGraph path (BatchedURPRenderBackend / UIBatcher) packs
    // stop positions per instance and does not share this cap; the warning text says so.
    //
    // We exercise the legacy backend directly via a tiny in-process IUICommandBuffer
    // fake. Materials resolved through Shader.Find may return null in headless test
    // contexts — the gradient-stop code path executes through SetGlobalVectorArray on
    // the fake command buffer regardless (FlushIfMaterialChanges tolerates null,
    // FlushMesh early-returns on a null material), so the warning logic is reachable
    // even when the URP shader assets aren't loaded.
    public class URPRenderBackendGradientStopCapTests {
        sealed class FakeCommandBuffer : IUICommandBuffer {
            public void SetGlobalVector(int nameID, Vector4 value) { }
            public void SetGlobalInt(int nameID, int value) { }
            public void SetGlobalVectorArray(int nameID, Vector4[] values) { }
            public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material) { }
            public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass) { }
        }

        static List<GradientStop> MakeStops(int count) {
            var stops = new List<GradientStop>(count);
            for (int i = 0; i < count; i++) {
                double t = count <= 1 ? 0 : (double)i / (count - 1);
                // Vary color so the first-stop payload in the warning is meaningful.
                float c = (float)t;
                stops.Add(new GradientStop(new LinearColor(c, c, c, 1f), t));
            }
            return stops;
        }

        static URPRenderBackend NewBackend() {
            var b = new URPRenderBackend();
            b.BeginFrame(new FakeCommandBuffer(), 800, 600, false);
            return b;
        }

        [Test]
        public void Nine_stop_gradient_logs_truncation_warning_K5() {
            var b = NewBackend();
            try {
                var stops = MakeStops(9);
                var grad = new LinearGradient(0, stops);
                // The warning text names the cap, the actual count, and the batched-path hint.
                // Use a regex so the test is resilient to minor message tweaks while still
                // pinning the load-bearing facts (count=9, cap=8, batched-path hint).
                LogAssert.Expect(LogType.Warning,
                    new Regex(@"Weva:.*9 stops.*legacy URPRenderBackend.*8.*BatchedURPRenderBackend"));
                b.Submit(new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad)));
            } finally {
                b.EndFrame();
                b.Dispose();
            }
        }

        [Test]
        public void Eight_stop_gradient_does_not_log_truncation_warning_K5() {
            // 8 stops fit exactly; no truncation, so no warning.
            var b = NewBackend();
            try {
                var stops = MakeStops(8);
                var grad = new LinearGradient(0, stops);
                b.Submit(new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad)));
            } finally {
                b.EndFrame();
                b.Dispose();
            }
            // LogAssert.NoUnexpectedReceived would also be implicit on test teardown,
            // but pin it explicitly so a regression that adds a stray log surfaces here.
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Truncation_warning_is_throttled_to_once_per_frame_K5() {
            // Submitting the same over-cap gradient twice in one frame should log once,
            // not twice — the warning is gated by a per-frame flag.
            var b = NewBackend();
            try {
                var stops = MakeStops(12);
                var grad = new LinearGradient(0, stops);
                LogAssert.Expect(LogType.Warning, new Regex(@"Weva:.*12 stops"));
                b.Submit(new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad)));
                b.Submit(new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad)));
            } finally {
                b.EndFrame();
                b.Dispose();
            }
            // Exactly the one Expect above — any second warning would be unexpected.
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Truncation_warning_reapplies_after_BeginFrame_K5() {
            // BeginFrame resets the once-per-frame guard so the next frame's submission
            // of a still-over-cap gradient re-surfaces the warning. This mirrors the
            // existing warnedNoStencil pattern in the backend.
            var b = NewBackend();
            try {
                var stops = MakeStops(10);
                var grad = new LinearGradient(0, stops);
                LogAssert.Expect(LogType.Warning, new Regex(@"Weva:.*10 stops"));
                b.Submit(new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad)));
                b.EndFrame();

                b.BeginFrame(new FakeCommandBuffer(), 800, 600, false);
                LogAssert.Expect(LogType.Warning, new Regex(@"Weva:.*10 stops"));
                b.Submit(new FillRectCommand(new Rect(0, 0, 100, 100), Brush.Gradient(grad)));
            } finally {
                b.EndFrame();
                b.Dispose();
            }
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
