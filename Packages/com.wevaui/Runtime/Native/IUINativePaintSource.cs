#if WEVA_URP
using Weva.Rendering;

namespace Weva.Native
{
    /// <summary>
    /// A paint source whose output is geometry (the core's draw list): the
    /// URP pass calls EmitNative with its command buffer and the meshes go
    /// straight to the GPU.
    /// </summary>
    internal interface IUINativePaintSource : IUIPaintSource
    {
        /// <summary>
        /// UIRenderGraphPass calls this with a Unity command buffer bound to
        /// the camera target; sources draw in SortingOrder.
        /// </summary>
        void EmitNative(UnityEngine.Rendering.CommandBuffer cmd, int viewportWidth, int viewportHeight);
    }
}
#endif
