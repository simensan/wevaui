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
        void EmitNative(IUICommandBuffer cmd, int viewportWidth, int viewportHeight);
    }
}
#endif
