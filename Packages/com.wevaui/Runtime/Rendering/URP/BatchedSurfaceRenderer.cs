#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Weva.Rendering.URP {
    // Renders a recorded BatchedURPRenderBackend into a caller-supplied RenderTexture via a
    // one-off CommandBuffer + Graphics.ExecuteCommandBuffer. No Camera, no URP
    // ScriptableRendererFeature, no scene — it works in edit mode, in headless play mode, and
    // inside an EditorWindow repaint, because nothing here depends on a render pipeline driving
    // a camera. The Hidden/Weva/Quad shader computes clip-space by hand from _WevaViewport, so
    // Material.SetPass/CommandBuffer.DrawMesh draws it regardless of the active pipeline.
    //
    // This is the shared surface-rendering core behind two callers:
    //   - GpuGoldenRunner: renders to a temporary RT then ReadPixels for pixel goldens.
    //   - the editor panel host: renders to a persistent RT then blits it into the window.
    //
    // Resources (shaders/materials, chunk meshes) are held for the lifetime of the instance so
    // a panel can render every repaint without reallocating. GraphicsBuffers carrying per-quad
    // instance data are rebuilt each Render (the data changes) but disposed one frame late: the
    // GPU may not have consumed them by the time ExecuteCommandBuffer returns, and unlike the
    // golden runner there is no ReadPixels here to force a sync.
    public sealed class BatchedSurfaceRenderer : IDisposable {
        const int MaxChunkSize = UIRenderGraphPass.MaxInstancesPerDraw;

        readonly UIBatchedResources resources = new UIBatchedResources();
        readonly Dictionary<int, Mesh> chunkMeshCache = new Dictionary<int, Mesh>();

        // Double-buffered owned GraphicsBuffers: the list filled this Render is disposed at the
        // start of the NEXT Render, by which point the prior frame's GPU work has retired.
        readonly List<GraphicsBuffer> liveBuffers = new List<GraphicsBuffer>();
        readonly List<GraphicsBuffer> retiringBuffers = new List<GraphicsBuffer>();

        bool disposed;

        // True once the Hidden/Weva/Quad shader resolved. When false, Render is a no-op (clear
        // only) — callers should degrade gracefully (e.g. show an error chrome) rather than crash.
        public bool IsReady => resources.QuadShader != null;

        // Renders the backend's accumulated batches into `target`, clearing to clearColor first.
        // The backend must already have had BeginFrame()/Submit()/EndFrame() called. `width` and
        // `height` are the target's pixel dimensions (used for the viewport / NDC mapping).
        // srgbEncodeTarget: when true (golden / pixel-readback path) the shader
        // sRGB-ENCODES into the sRGB=false target so the raw bytes ARE the final
        // sRGB image. When false the shader emits raw LINEAR premul — used by the
        // editor-panel display path in a LINEAR project, where EditorGUI.Draw-
        // PreviewTexture re-encodes linear→sRGB on display; sRGB-encoding here too
        // would double-encode and wash the panel out. (In a Gamma project the GUI
        // shows the bytes 1:1, so the panel passes true there.)
        public void Render(BatchedURPRenderBackend backend, RenderTexture target, int width, int height, Color clearColor,
                           bool srgbEncodeTarget = true) {
            if (disposed) throw new ObjectDisposedException(nameof(BatchedSurfaceRenderer));
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            if (target == null) throw new ArgumentNullException(nameof(target));

            // Retire the buffers from two frames ago, then age this-frame's-predecessor into the
            // retiring slot. Net effect: every instance buffer lives exactly one extra frame
            // after the Render that created it, so the GPU is guaranteed to have consumed it.
            DisposeList(retiringBuffers);
            retiringBuffers.AddRange(liveBuffers);
            liveBuffers.Clear();

            var cmd = CommandBufferPool.Get("Weva.BatchedSurfaceRenderer");
            try {
                cmd.SetRenderTarget(target);
                cmd.ClearRenderTarget(false, true, clearColor);

                if (!IsReady) {
                    Graphics.ExecuteCommandBuffer(cmd);
                    return;
                }

                var viewport = new Vector4(width, height,
                    width  > 0 ? 1f / width  : 0f,
                    height > 0 ? 1f / height : 0f);
                cmd.SetGlobalVector(UIRenderGraphPass.IdViewport, viewport);
                cmd.SetGlobalVector(UIRenderGraphPass.IdViewportOrigin, Vector4.zero);
                // A-SRGB-COMPOSITE: this single-RT path renders straight into its
                // (sRGB=false) read-back / preview target. Golden/readback callers
                // want the bytes sRGB-encoded (gamma compositing → the bytes ARE
                // the final image). The editor-panel display path in a LINEAR
                // project passes false: EditorGUI.DrawPreviewTexture re-encodes
                // linear→sRGB on display, so the RT must hold linear premul or the
                // panel double-encodes (washed out).
                cmd.SetGlobalFloat(UIRenderGraphPass.IdWevaSrgbComposite, srgbEncodeTarget ? 1f : 0f);

                var sh = resources.QuadShader;
                var kwBrushLinear = new LocalKeyword(sh, UIRenderGraphPass.KeywordBrushLinear);
                var kwBrushRadial = new LocalKeyword(sh, UIRenderGraphPass.KeywordBrushRadial);
                var kwBrushConic  = new LocalKeyword(sh, UIRenderGraphPass.KeywordBrushConic);
                var kwBordered    = new LocalKeyword(sh, UIRenderGraphPass.KeywordBordered);
                var kwShadowOut   = new LocalKeyword(sh, UIRenderGraphPass.KeywordShadowOutset);
                var kwShadowIn    = new LocalKeyword(sh, UIRenderGraphPass.KeywordShadowInset);
                var kwText        = new LocalKeyword(sh, UIRenderGraphPass.KeywordText);

                var batcher = backend.Batcher;
                int stride  = UIRenderGraphPass.InstanceFloat4Stride;

                for (int b = 0; b < batcher.Batches.Count; b++) {
                    var batch = batcher.Batches[b];
                    int total = batch.InstanceCount;
                    if (total <= 0) continue;

                    UnityEngine.Texture batchImageTex = null;
                    if (batch.Key.Brush == UIQuadBrush.Image) batchImageTex = batch.Key.ImageTexture;
                    var mat = resources.GetQuadMaterial(batch.Key.StencilRef, batchImageTex);
                    if (mat == null) continue;

                    bool isFillClass =
                        batch.Key.Brush == UIQuadBrush.Solid
                        || batch.Key.Brush == UIQuadBrush.Text
                        || batch.Key.Brush == UIQuadBrush.LinearGradient
                        || batch.Key.Brush == UIQuadBrush.RadialGradient
                        || batch.Key.Brush == UIQuadBrush.ConicGradient
                        || batch.Key.Brush == UIQuadBrush.Shadow
                        || batch.Key.Brush == UIQuadBrush.ShadowInset;
                    cmd.SetKeyword(mat, kwBrushLinear, isFillClass);
                    cmd.SetKeyword(mat, kwBrushRadial, isFillClass);
                    cmd.SetKeyword(mat, kwBrushConic,  isFillClass);
                    cmd.SetKeyword(mat, kwBordered,    isFillClass);
                    cmd.SetKeyword(mat, kwShadowOut,   isFillClass);
                    cmd.SetKeyword(mat, kwShadowIn,    isFillClass);
                    cmd.SetKeyword(mat, kwText,        isFillClass);

                    // Resolve this batch's glyph atlas textures (up to 4 slots).
                    // Bound on the per-chunk MaterialPropertyBlock below ALONG WITH
                    // their _GlyphAtlasChannelMask — the SDF lives in a specific
                    // texture channel and the shader samples `dot(texel, mask)`.
                    // Omitting the mask (the prior bug) left it at zero, so every
                    // glyph sampled 0 coverage and text rendered invisible while
                    // solid fills were unaffected. Mirrors UIRenderGraphPass.
                    var atlasTex0 = batch.AtlasIdSlot0 != 0 ? Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot0) : null;
                    var atlasTex1 = batch.AtlasIdSlot1 != 0 ? Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot1) : null;
                    var atlasTex2 = batch.AtlasIdSlot2 != 0 ? Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot2) : null;
                    var atlasTex3 = batch.AtlasIdSlot3 != 0 ? Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot3) : null;

                    int offset = 0;
                    while (offset < total) {
                        int chunk     = Math.Min(MaxChunkSize, total - offset);
                        int float4Cnt = chunk * stride;

                        var data = new Vector4[float4Cnt];
                        if (offset == 0) {
                            UIRenderGraphPass.PackInstances(batch.Instances, chunk, data);
                        } else {
                            var sliced = new UIQuadInstance[chunk];
                            Array.Copy(batch.Instances, offset, sliced, 0, chunk);
                            UIRenderGraphPass.PackInstances(sliced, chunk, data);
                        }

                        var gb = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                                                   float4Cnt, sizeof(float) * 4);
                        gb.SetData(data);
                        liveBuffers.Add(gb);

                        var mpb = new MaterialPropertyBlock();
                        mpb.SetBuffer(UIRenderGraphPass.IdInstancesSB, gb);
                        mpb.SetInt(UIRenderGraphPass.IdInstanceCount, chunk);
                        mpb.SetVector(UIRenderGraphPass.IdViewport, viewport);
                        // Glyph atlas textures + per-slot channel masks (text batches).
                        if (atlasTex0 != null) {
                            mpb.SetTexture(UIRenderGraphPass.IdGlyphAtlas, atlasTex0);
                            mpb.SetVector(UIRenderGraphPass.IdGlyphAtlasChannelMask, UIRenderGraphPass.GetGlyphAtlasChannelMask(atlasTex0));
                        }
                        if (atlasTex1 != null) {
                            mpb.SetTexture(UIRenderGraphPass.IdGlyphAtlas1, atlasTex1);
                            mpb.SetVector(UIRenderGraphPass.IdGlyphAtlas1ChannelMask, UIRenderGraphPass.GetGlyphAtlasChannelMask(atlasTex1));
                        }
                        if (atlasTex2 != null) {
                            mpb.SetTexture(UIRenderGraphPass.IdGlyphAtlas2, atlasTex2);
                            mpb.SetVector(UIRenderGraphPass.IdGlyphAtlas2ChannelMask, UIRenderGraphPass.GetGlyphAtlasChannelMask(atlasTex2));
                        }
                        if (atlasTex3 != null) {
                            mpb.SetTexture(UIRenderGraphPass.IdGlyphAtlas3, atlasTex3);
                            mpb.SetVector(UIRenderGraphPass.IdGlyphAtlas3ChannelMask, UIRenderGraphPass.GetGlyphAtlasChannelMask(atlasTex3));
                        }

                        var mesh = GetChunkMesh(chunk);
                        cmd.DrawMesh(mesh, Matrix4x4.identity, mat, 0, 0, mpb);

                        offset += chunk;
                    }
                }

                Graphics.ExecuteCommandBuffer(cmd);
            } finally {
                CommandBufferPool.Release(cmd);
            }
        }

        // Forces any pending instance buffers to be released immediately. Callers that DO sync
        // the GPU within the same frame (e.g. GpuGoldenRunner's ReadPixels) can call this right
        // after Render to avoid carrying buffers an extra frame.
        public void FlushBuffers() {
            DisposeList(retiringBuffers);
            DisposeList(liveBuffers);
        }

        Mesh GetChunkMesh(int size) {
            if (chunkMeshCache.TryGetValue(size, out var cached) && cached != null) return cached;
            var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            int vCount = size * 4;
            int iCount = size * 6;
            var verts    = new Vector3[vCount];
            var uvs      = new Vector2[vCount];
            var tangents = new Vector4[vCount];
            var idx      = new int[iCount];
            for (int q = 0; q < size; q++) {
                int v0 = q * 4;
                verts[v0 + 0] = new Vector3(0f, 0f, 0f);
                verts[v0 + 1] = new Vector3(0f, 1f, 0f);
                verts[v0 + 2] = new Vector3(1f, 1f, 0f);
                verts[v0 + 3] = new Vector3(1f, 0f, 0f);
                uvs[v0 + 0] = new Vector2(0f, 0f);
                uvs[v0 + 1] = new Vector2(0f, 1f);
                uvs[v0 + 2] = new Vector2(1f, 1f);
                uvs[v0 + 3] = new Vector2(1f, 0f);
                var t = new Vector4((float)q, 0f, 0f, 1f);
                tangents[v0 + 0] = t;
                tangents[v0 + 1] = t;
                tangents[v0 + 2] = t;
                tangents[v0 + 3] = t;
                int t0 = q * 6;
                idx[t0 + 0] = v0 + 0; idx[t0 + 1] = v0 + 1; idx[t0 + 2] = v0 + 2;
                idx[t0 + 3] = v0 + 0; idx[t0 + 4] = v0 + 2; idx[t0 + 5] = v0 + 3;
            }
            m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTangents(tangents);
            m.SetIndices(idx, MeshTopology.Triangles, 0);
            m.UploadMeshData(false);
            chunkMeshCache[size] = m;
            return m;
        }

        static void DisposeList(List<GraphicsBuffer> list) {
            for (int i = 0; i < list.Count; i++) list[i]?.Dispose();
            list.Clear();
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            DisposeList(liveBuffers);
            DisposeList(retiringBuffers);
            foreach (var kv in chunkMeshCache) {
                if (kv.Value == null) continue;
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(kv.Value);
                else UnityEngine.Object.DestroyImmediate(kv.Value);
#else
                UnityEngine.Object.Destroy(kv.Value);
#endif
            }
            chunkMeshCache.Clear();
            resources.Dispose();
        }
    }
}
#endif
