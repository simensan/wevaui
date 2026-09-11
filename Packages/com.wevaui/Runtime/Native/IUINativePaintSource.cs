#if WEVA_URP
using Weva.Rendering;

namespace Weva.Native
{
    /// <summary>
    /// A paint source whose output is already geometry (libweva's draw list)
    /// rather than paint commands. The URP pass calls EmitNative with its
    /// command buffer after the ordinary sources have emitted paint, so
    /// the meshes go straight to the GPU through the same pass.
    /// </summary>
    public interface IUINativePaintSource : IUIPaintSource
    {
        /// <summary>The legacy UIRendererFeature's pass: its own command buffer wrapper.</summary>
        void EmitNative(IUICommandBuffer cmd, int viewportWidth, int viewportHeight);
        /// <summary>
        /// The batched UIRenderGraphPass, the renderer feature a project actually
        /// runs: a Unity command buffer bound to the camera target, before the
        /// C# batches are drained so a native document composes beneath them.
        /// </summary>
        void EmitNative(UnityEngine.Rendering.CommandBuffer cmd, int viewportWidth, int viewportHeight);
    }
}
#endif
