#if WEVA_URP
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.Rendering.Universal;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering {
    // Headless tests for UIRenderPass — confirm pass naming, render-pass-event placement,
    // and that both Execute (legacy) and RecordRenderGraph (URP 17+) entry points are
    // present. Reflection is used so we don't have to spin up the Unity render pipeline.
    public class UIRenderPassConfigTests {
        [Test]
        public void Pass_name_constant_is_stable() {
            Assert.That(UIRenderPass.PassName, Is.EqualTo("Weva.RenderPass"));
        }

        [Test]
        public void Constructor_takes_backend_argument() {
            // Verify the public constructor signature; we don't instantiate to avoid the
            // URPRenderBackend ctor allocating Unity Mesh objects at headless test time.
            var ctor = typeof(UIRenderPass).GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new Type[] { typeof(Weva.Rendering.URPRenderBackend) },
                null);
            Assert.That(ctor, Is.Not.Null, "UIRenderPass(URPRenderBackend) ctor must exist");
        }

        [Test]
        public void Render_pass_event_constant_is_after_rendering() {
            Assert.That(UIRenderPass.OverlayRenderPassEvent, Is.EqualTo(RenderPassEvent.AfterRendering));
            Assert.That(UIRenderGraphPass.OverlayRenderPassEvent, Is.EqualTo(RenderPassEvent.AfterRendering));
        }

        // Was two tests asserting that a legacy Execute existed and carried
        // [Obsolete]. bef1c39a removed it: URP 17.2 / Unity 6 deleted the
        // compatibility-mode methods from the base class, so Execute is not
        // deprecated, it is gone. RecordRenderGraph is the only entry point,
        // and that is what is worth pinning.
        [Test]
        public void Record_render_graph_is_the_only_entry_point() {
            var t = typeof(UIRenderPass);
#if UNITY_2023_3_OR_NEWER
            Assert.That(FindDeclaredMethod(t, "RecordRenderGraph"), Is.Not.Null,
                "RecordRenderGraph must be implemented for URP 17+");
#endif
            Assert.That(FindDeclaredMethod(t, "Execute"), Is.Null,
                "URP 17.2 removed the legacy compatibility-mode entry point");
        }

        static MethodInfo FindDeclaredMethod(Type t, string name) {
            var methods = t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var m in methods) {
                if (m.Name == name) return m;
            }
            return null;
        }

        [Test]
        public void Pass_name_used_by_profiling_sampler() {
            // The ProfilingSampler is a private field; just sanity-check the constant the
            // sampler is built from is non-empty so frame-debugger displays a useful entry.
            Assert.That(UIRenderPass.PassName.Length, Is.GreaterThan(0));
        }
    }
}
#endif
