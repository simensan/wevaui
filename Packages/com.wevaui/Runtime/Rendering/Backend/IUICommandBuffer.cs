#if WEVA_URP
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Weva.Rendering {
    // Abstraction so URPRenderBackend can target either a legacy CommandBuffer (Execute path)
    // or a RasterCommandBuffer (RecordRenderGraph path) without code duplication. Both Unity
    // types expose the same DrawMesh / SetGlobal* surface via different concrete types — this
    // interface picks the subset URPRenderBackend uses and the two adapters below forward
    // method calls 1:1.
    public interface IUICommandBuffer {
        void SetGlobalVector(int nameID, Vector4 value);
        void SetGlobalInt(int nameID, int value);
        void SetGlobalVectorArray(int nameID, Vector4[] values);
        void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material);
        void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass);
    }

    public sealed class LegacyUICommandBuffer : IUICommandBuffer {
        readonly CommandBuffer cb;
        public LegacyUICommandBuffer(CommandBuffer cb) { this.cb = cb; }
        public CommandBuffer Native => cb;
        public void SetGlobalVector(int nameID, Vector4 value) => cb.SetGlobalVector(nameID, value);
        public void SetGlobalInt(int nameID, int value) => cb.SetGlobalInt(nameID, value);
        public void SetGlobalVectorArray(int nameID, Vector4[] values) => cb.SetGlobalVectorArray(nameID, values);
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material) => cb.DrawMesh(mesh, matrix, material);
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass) =>
            cb.DrawMesh(mesh, matrix, material, submeshIndex, shaderPass);
    }

#if UNITY_2023_3_OR_NEWER
    // RasterCommandBuffer is the RenderGraph-era equivalent. It exposes the same surface
    // we use; the only reason for an adapter is the type isn't assignable to CommandBuffer.
    public sealed class RasterUICommandBuffer : IUICommandBuffer {
        readonly RasterCommandBuffer cb;
        public RasterUICommandBuffer(RasterCommandBuffer cb) { this.cb = cb; }
        public RasterCommandBuffer Native => cb;
        public void SetGlobalVector(int nameID, Vector4 value) => cb.SetGlobalVector(nameID, value);
        public void SetGlobalInt(int nameID, int value) => cb.SetGlobalInt(nameID, value);
        public void SetGlobalVectorArray(int nameID, Vector4[] values) => cb.SetGlobalVectorArray(nameID, values);
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material) => cb.DrawMesh(mesh, matrix, material);
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass) =>
            cb.DrawMesh(mesh, matrix, material, submeshIndex, shaderPass);
    }
#endif
}
#endif
