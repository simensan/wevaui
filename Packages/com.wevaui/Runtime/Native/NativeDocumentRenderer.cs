// Turns libweva's published draw list into Unity meshes and draws them.
//
// The core hands the host textured triangle lists in document pixels with
// clip and opacity already resolved (scissored geometry is clipped before it
// is published, as the Godot host relies on), so the host's job is upload:
// mirror the document's textures by id, merge consecutive draws that share a
// texture into one mesh, and issue one DrawMesh per merged run through
// Hidden/Weva/NativeMesh. A backdrop-filter draw is the one thing that is
// not geometry: its shape is drawn with Hidden/Weva/NativeBackdrop over a
// copy of the target taken just before it, so it sees everything drawn
// beneath (runs split around it); rounded rectangles arrive tessellated
// and draw as geometry.
//
// Two entry points: Draw(CommandBuffer) for the URP pass, and
// RenderToTexture for tests and screenshots (a legacy CommandBuffer executed
// immediately against an offscreen target).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if WEVA_URP
using Weva.Rendering;
#endif

namespace Weva.Native
{
    internal sealed unsafe class NativeDocumentRenderer : IDisposable
    {
        private static readonly int IdViewport = Shader.PropertyToID("_WevaNativeViewport");
        private static readonly int IdFlip = Shader.PropertyToID("_WevaNativeFlip");
        private static readonly int IdGamma = Shader.PropertyToID("_WevaNativeGamma");
        private static readonly int IdTex = Shader.PropertyToID("_WevaTex");
        private static readonly int IdTextured = Shader.PropertyToID("_WevaTextured");

        private sealed class Batch
        {
            public Mesh Mesh;
            public ulong Texture;
            public int Triangles;
            public int Blend;   // weva_blend_mode
            public bool Backdrop;
            public weva_backdrop_effect Effect;
            // The draw versions this run was built from, when the core
            // publishes them: an identical sequence next frame is the same
            // geometry, and its mesh is kept instead of rebuilt.
            public ulong[] Versions;
        }

        private static readonly int IdBackdropCopy = Shader.PropertyToID("_WevaBackdropCopy");
        private static readonly int IdBackdropSource = Shader.PropertyToID("_WevaBackdropSource");
        private static readonly int IdBackdropSigma = Shader.PropertyToID("_WevaBackdropSigma");
        private static readonly int IdBackdropRow0 = Shader.PropertyToID("_WevaBackdropRow0");
        private static readonly int IdBackdropRow1 = Shader.PropertyToID("_WevaBackdropRow1");
        private static readonly int IdBackdropRow2 = Shader.PropertyToID("_WevaBackdropRow2");
        private static readonly int IdBackdropOffset = Shader.PropertyToID("_WevaBackdropOffset");
        private Material _backdrop;
        private readonly MaterialPropertyBlock _backdropProps = new MaterialPropertyBlock();

        /// <summary>How many backdrop-filter draws the last sync produced.</summary>
        public int BackdropDraws { get; private set; }

        private static readonly int IdBlend = Shader.PropertyToID("_WevaBlend");
        private static readonly int IdSrcBlend = Shader.PropertyToID("_WevaSrcBlend");
        private static readonly int IdDstBlend = Shader.PropertyToID("_WevaDstBlend");
        private static readonly int IdBlendOp = Shader.PropertyToID("_WevaBlendOp");
        private readonly Dictionary<(ulong, int), Material> _blendMaterials = new Dictionary<(ulong, int), Material>();

        /// <summary>
        /// The blend states a weva_blend_mode maps to: multiply, screen, darken and
        /// lighten are plain states; the rest need the backdrop and draw normally.
        /// </summary>
        private static (int shaderMode, UnityEngine.Rendering.BlendMode src, UnityEngine.Rendering.BlendMode dst, UnityEngine.Rendering.BlendOp op) BlendStateFor(int blend)
        {
            switch ((weva_blend_mode)blend)
            {
                case weva_blend_mode.WEVA_BLEND_MULTIPLY:
                    return (1, UnityEngine.Rendering.BlendMode.DstColor, UnityEngine.Rendering.BlendMode.Zero, UnityEngine.Rendering.BlendOp.Add);
                case weva_blend_mode.WEVA_BLEND_SCREEN:
                    return (2, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendMode.OneMinusSrcColor, UnityEngine.Rendering.BlendOp.Add);
                case weva_blend_mode.WEVA_BLEND_DARKEN:
                    return (3, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendOp.Min);
                case weva_blend_mode.WEVA_BLEND_LIGHTEN:
                    return (4, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendOp.Max);
                default:
                    return (0, UnityEngine.Rendering.BlendMode.SrcAlpha, UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha, UnityEngine.Rendering.BlendOp.Add);
            }
        }

        private Shader _shader;
        private readonly Dictionary<ulong, Texture2D> _textures = new Dictionary<ulong, Texture2D>();
        private readonly Dictionary<ulong, Material> _materials = new Dictionary<ulong, Material>();
        private Material _untextured;
        private readonly List<Batch> _batches = new List<Batch>();
        // Last frame's runs by first draw version, for reuse; and scratch the
        // sync reuses rather than allocating each frame.
        private readonly Dictionary<ulong, Batch> _previousRuns = new Dictionary<ulong, Batch>();
        private readonly List<ulong> _runVersions = new List<ulong>(64);
        private readonly HashSet<ulong> _keepTextures = new HashSet<ulong>();
        private readonly List<ulong> _droppedTextures = new List<ulong>();
        private readonly List<(ulong, int)> _staleBlends = new List<(ulong, int)>();
        public int RunsReused { get; private set; }
        private readonly List<Mesh> _meshPool = new List<Mesh>();
        private readonly List<Vector3> _positions = new List<Vector3>(1024);
        private readonly List<Color> _colors = new List<Color>(1024);
        private readonly List<Vector2> _uvs = new List<Vector2>(1024);
        private readonly List<int> _indices = new List<int>(2048);
        private ulong _serial;
        private bool _synced;

        /// <summary>The draw serial the meshes were built from.</summary>
        public ulong Serial => _serial;
        public int BatchCount => _batches.Count;
        public int TextureCount => _textures.Count;
        public int DrawsSkipped { get; private set; }
        public int TrianglesUploaded { get; private set; }

        public bool IsReady
        {
            get
            {
                EnsureShader();
                return _shader != null;
            }
        }

        private void EnsureShader()
        {
            if (_shader != null) return;
            _shader = Resources.Load<Shader>("Weva-NativeMesh");
            if (_shader == null) _shader = Shader.Find("Hidden/Weva/NativeMesh");
        }

        /// <summary>
        /// Rebuilds meshes and textures when the document has published a new
        /// draw list since the last call. Returns true when something changed.
        /// </summary>
        public bool Sync(NativeDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            ulong serial = doc.DrawSerial;
            if (_synced && serial == _serial) return false;
            _serial = serial;
            _synced = true;
            SyncTextures(doc);
            BuildBatches(doc);
            return true;
        }

        private void SyncTextures(NativeDocument doc)
        {
            ReadOnlySpan<weva_texture> published = doc.Textures();
            // Ids are never reused within a document: keep what is held, add
            // what is new, drop what the document no longer publishes.
            HashSet<ulong> keep = _keepTextures;
            keep.Clear();
            for (int i = 0; i < published.Length; i++)
            {
                weva_texture t = published[i];
                keep.Add(t.id);
                if (_textures.ContainsKey(t.id)) continue;
                if (t.width <= 0 || t.height <= 0 || t.rgba == null) continue;
                // Raw bytes: sRGB for images and gradients, white plus coverage
                // for the glyph atlas. The shader decides whether to decode.
                var texture = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, true)
                {
                    name = "Weva.Native.Texture." + t.id,
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                // Row 0 of the core's pixels is the image's top row, and it lands
                // at v = 0, which is where the core's uvs put the top: no flip.
                texture.LoadRawTextureData((IntPtr)t.rgba, t.width * t.height * 4);
                // Not readable: an id's pixels never change, and nothing reads
                // them back, so the CPU copy was a second copy of every image.
                texture.Apply(false, true);
                _textures[t.id] = texture;
            }
            List<ulong> dropped = _droppedTextures;
            dropped.Clear();
            foreach (KeyValuePair<ulong, Texture2D> held in _textures)
            {
                if (!keep.Contains(held.Key)) dropped.Add(held.Key);
            }
            foreach (ulong id in dropped)
            {
                Destroy(_textures[id]);
                _textures.Remove(id);
                if (_materials.TryGetValue(id, out Material m))
                {
                    Destroy(m);
                    _materials.Remove(id);
                }
                List<(ulong, int)> stale = _staleBlends;
                stale.Clear();
                foreach (var key in _blendMaterials.Keys) if (key.Item1 == id) stale.Add(key);
                foreach (var key in stale)
                {
                    Destroy(_blendMaterials[key]);
                    _blendMaterials.Remove(key);
                }
            }
        }

        private void BuildBatches(NativeDocument doc)
        {
            // Last frame's versioned runs stay available for reuse; everything
            // else goes back to the pool now.
            _previousRuns.Clear();
            foreach (Batch b in _batches)
            {
                if (b.Versions != null && b.Versions.Length > 0 && !_previousRuns.ContainsKey(b.Versions[0]))
                    _previousRuns[b.Versions[0]] = b;
                else
                    _meshPool.Add(b.Mesh);
            }
            _batches.Clear();
            DrawsSkipped = 0;
            TrianglesUploaded = 0;
            BackdropDraws = 0;
            RunsReused = 0;
            ReadOnlySpan<weva_draw> draws = doc.Draws();
            ReadOnlySpan<ulong> versions = doc.DrawVersions();
            if (versions.Length != draws.Length) versions = ReadOnlySpan<ulong>.Empty;
            int i = 0;
            while (i < draws.Length)
            {
                weva_draw first = draws[i];
                if (first.vertex_count == 0 || first.index_count == 0)
                {
                    DrawsSkipped++;
                    i++;
                    continue;
                }
                if (first.kind == (int)weva_draw_kind.WEVA_DRAW_BACKDROP_FILTER)
                {
                    // The shape, on its own: what is beneath it must already be
                    // in the target when it draws, so it neither joins the run
                    // before it nor lets the run after it start early.
                    _positions.Clear();
                    _indices.Clear();
                    for (nuint v = 0; v < first.vertex_count; v++) _positions.Add(new Vector3(first.vertices[v].x, first.vertices[v].y, 0));
                    for (nuint k = 0; k < first.index_count; k++) _indices.Add((int)first.indices[k]);
                    Mesh shape = RentMesh();
                    shape.indexFormat = _positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    shape.SetVertices(_positions);
                    shape.SetIndices(_indices, MeshTopology.Triangles, 0, false);
                    shape.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1f));
                    _batches.Add(new Batch { Mesh = shape, Triangles = _indices.Count / 3, Backdrop = true, Effect = first.backdrop });
                    BackdropDraws++;
                    i++;
                    continue;
                }
                _positions.Clear();
                _colors.Clear();
                _uvs.Clear();
                _indices.Clear();
                ulong texture = first.texture_id;
                // The run's extent and versions first: when last frame built a
                // run from the same commands, its mesh is reused as it is.
                int end = i;
                _runVersions.Clear();
                int skipped = 0;
                while (end < draws.Length)
                {
                    weva_draw d = draws[end];
                    if (d.kind == (int)weva_draw_kind.WEVA_DRAW_BACKDROP_FILTER) break;
                    if (d.vertex_count == 0 || d.index_count == 0)
                    {
                        skipped++;
                        end++;
                        continue;
                    }
                    if (d.texture_id != texture || d.blend_mode != first.blend_mode) break;
                    if (!versions.IsEmpty) _runVersions.Add(versions[end]);
                    end++;
                }
                if (_runVersions.Count > 0 && _previousRuns.TryGetValue(_runVersions[0], out Batch previous) &&
                    SameVersions(previous.Versions, _runVersions))
                {
                    _previousRuns.Remove(_runVersions[0]);
                    _batches.Add(previous);
                    DrawsSkipped += skipped;
                    RunsReused++;
                    i = end;
                    continue;
                }
                int j = i;
                while (j < end)
                {
                    weva_draw d = draws[j];
                    if (d.vertex_count == 0 || d.index_count == 0)
                    {
                        // An empty draw between two runs does not split them.
                        DrawsSkipped++;
                        j++;
                        continue;
                    }
                    int baseVertex = _positions.Count;
                    for (nuint v = 0; v < d.vertex_count; v++)
                    {
                        weva_vertex vertex = d.vertices[v];
                        _positions.Add(new Vector3(vertex.x, vertex.y, 0));
                        _colors.Add(new Color(vertex.r, vertex.g, vertex.b, vertex.a));
                        _uvs.Add(new Vector2(vertex.u, vertex.v));
                    }
                    for (nuint k = 0; k < d.index_count; k++) _indices.Add(baseVertex + (int)d.indices[k]);
                    j++;
                }
                Mesh mesh = RentMesh();
                mesh.indexFormat = _positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(_positions);
                mesh.SetColors(_colors);
                mesh.SetUVs(0, _uvs);
                mesh.SetIndices(_indices, MeshTopology.Triangles, 0, false);
                mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1f));
                _batches.Add(new Batch
                {
                    Mesh = mesh, Texture = texture, Triangles = _indices.Count / 3, Blend = first.blend_mode,
                    Versions = _runVersions.Count > 0 ? _runVersions.ToArray() : null,
                });
                TrianglesUploaded += _indices.Count / 3;
                i = j;
            }
            // Runs that did not come back: their meshes return to the pool.
            foreach (Batch stale in _previousRuns.Values) _meshPool.Add(stale.Mesh);
            _previousRuns.Clear();
        }

        private static bool SameVersions(ulong[] held, List<ulong> run)
        {
            if (held == null || held.Length != run.Count) return false;
            for (int k = 0; k < held.Length; k++)
            {
                if (held[k] != run[k]) return false;
            }
            return true;
        }

        private Mesh RentMesh()
        {
            if (_meshPool.Count > 0)
            {
                Mesh m = _meshPool[_meshPool.Count - 1];
                _meshPool.RemoveAt(_meshPool.Count - 1);
                m.Clear();
                return m;
            }
            var mesh = new Mesh { name = "Weva.Native.Batch", hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            return mesh;
        }

        private Material MaterialFor(ulong texture, int blend)
        {
            if (blend == (int)weva_blend_mode.WEVA_BLEND_NORMAL) return MaterialFor(texture);
            var state = BlendStateFor(blend);
            if (state.shaderMode == 0) return MaterialFor(texture);   // no plain blend state for it
            EnsureShader();
            if (_shader == null) return null;
            if (!_blendMaterials.TryGetValue((texture, blend), out Material m))
            {
                Material basis = MaterialFor(texture);
                if (basis == null) return null;
                m = new Material(basis) { hideFlags = HideFlags.HideAndDontSave };
                m.SetFloat(IdBlend, state.shaderMode);
                m.SetFloat(IdSrcBlend, (float)state.src);
                m.SetFloat(IdDstBlend, (float)state.dst);
                m.SetFloat(IdBlendOp, (float)state.op);
                _blendMaterials[(texture, blend)] = m;
            }
            return m;
        }

        private Material MaterialFor(ulong texture)
        {
            EnsureShader();
            if (_shader == null) return null;
            if (texture == 0 || !_textures.TryGetValue(texture, out Texture2D tex))
            {
                if (_untextured == null)
                {
                    _untextured = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
                    _untextured.SetFloat(IdTextured, 0);
                }
                return _untextured;
            }
            if (!_materials.TryGetValue(texture, out Material m))
            {
                m = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
                m.SetFloat(IdTextured, 1);
                m.SetTexture(IdTex, tex);
                _materials[texture] = m;
            }
            return m;
        }

        /// <summary>
        /// Issues the batches into a command buffer bound to <paramref name="target"/>.
        /// flip = 0 follows _ProjectionParams.x; gamma composites sRGB-encoded
        /// into a raw target the way a browser and the Godot host do (see the
        /// shader). A backdrop-filter draw copies the target first and draws
        /// its shape over the copy; with no target given it is skipped.
        /// </summary>
        public void Draw(CommandBuffer cmd, int width, int height, int flip, bool gamma, in NativeRenderTarget target)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            cmd.SetGlobalVector(IdViewport, Viewport(width, height));
            cmd.SetGlobalInt(IdFlip, flip);
            cmd.SetGlobalInt(IdGamma, gamma ? 1 : 0);
            foreach (Batch b in _batches)
            {
                if (b.Backdrop)
                {
                    if (!target.IsSet) continue;
                    DrawBackdrop(cmd, b, target);
                    continue;
                }
                Material m = MaterialFor(b.Texture, b.Blend);
                if (m != null) cmd.DrawMesh(b.Mesh, Matrix4x4.identity, m);
            }
        }

        public void Draw(CommandBuffer cmd, int width, int height, int flip = 0, bool gamma = false)
        {
            Draw(cmd, width, height, flip, gamma, default);
        }

        // Copy what is in the target (a resolve, when it is multisampled),
        // rebind the target, and draw the shape through the backdrop shader
        // with the copy bound. Inside the URP pass the copy is a RenderGraph
        // texture the pass declared, filled by the shader's own copy pass
        // with the target bound by identifier: a legacy CommandBuffer.Blit
        // in a RenderGraph pass left the camera target uncleared on the
        // frames after, and the Blitter wants a Texture behind the handle,
        // which the camera colour is not. Offscreen, with no graph, a
        // temporary RT and a Blit do.
        private void DrawBackdrop(CommandBuffer cmd, Batch b, in NativeRenderTarget target)
        {
            EnsureShader();
            if (_backdrop == null)
            {
                Shader shader = Resources.Load<Shader>("Weva-NativeBackdrop");
                if (shader == null) shader = Shader.Find("Hidden/Weva/NativeBackdrop");
                if (shader == null) return;
                _backdrop = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            // Inside the render graph without a copy: the backdrop appeared
            // after the feature decided this frame needed none. The legacy
            // Blit fallback must not run in a graph pass (see above); the
            // next frame declares the copy.
            if (target.ColorHandle != null && target.CopyHandle == null) return;
            bool graph = target.ColorHandle != null && target.CopyHandle != null;
            if (graph)
            {
                cmd.SetRenderTarget(target.CopyHandle);
                cmd.SetGlobalTexture(IdBackdropSource, target.Color);
                cmd.DrawProcedural(Matrix4x4.identity, _backdrop, 1, MeshTopology.Triangles, 3);
                if (target.HasDepth) cmd.SetRenderTarget(target.ColorHandle, target.DepthHandle);
                else cmd.SetRenderTarget(target.ColorHandle);
                cmd.SetGlobalTexture(IdBackdropCopy, target.CopyHandle);
            }
            else
            {
                RenderTextureDescriptor copy = target.Descriptor;
                copy.depthBufferBits = 0;
                copy.msaaSamples = 1;
                copy.bindMS = false;
                copy.useMipMap = false;
                cmd.GetTemporaryRT(IdBackdropCopy, copy, FilterMode.Bilinear);
                cmd.Blit(target.Color, IdBackdropCopy);
                if (target.HasDepth) cmd.SetRenderTarget(target.Color, target.Depth);
                else cmd.SetRenderTarget(target.Color);
                cmd.SetGlobalTexture(IdBackdropCopy, IdBackdropCopy);
            }
            weva_backdrop_effect e = b.Effect;
            _backdropProps.Clear();
            _backdropProps.SetFloat(IdBackdropSigma, (float)(e.blur_radius / 2.0));
            _backdropProps.SetVector(IdBackdropRow0, new Vector4(e.color_matrix[0], e.color_matrix[1], e.color_matrix[2], 0));
            _backdropProps.SetVector(IdBackdropRow1, new Vector4(e.color_matrix[3], e.color_matrix[4], e.color_matrix[5], 0));
            _backdropProps.SetVector(IdBackdropRow2, new Vector4(e.color_matrix[6], e.color_matrix[7], e.color_matrix[8], 0));
            _backdropProps.SetVector(IdBackdropOffset, new Vector4(e.color_offset[0], e.color_offset[1], e.color_offset[2], e.color_alpha));
            cmd.DrawMesh(b.Mesh, Matrix4x4.identity, _backdrop, 0, 0, _backdropProps);
            if (!graph) cmd.ReleaseTemporaryRT(IdBackdropCopy);
        }


        private static Vector4 Viewport(int width, int height)
        {
            return new Vector4(width, height, width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f);
        }

        /// <summary>
        /// Renders the document's current draw list into a new texture of the
        /// given size over <paramref name="clear"/> (an sRGB colour) and reads
        /// it back as sRGB-encoded pixels, composited in gamma space like a
        /// browser page. Unity's texture rows run bottom-up: the page's top is
        /// the texture's top row (y = height - 1).
        /// </summary>
        public Texture2D RenderToTexture(NativeDocument doc, int width, int height, Color clear)
        {
            Sync(doc);
            // A raw target: what the shader writes is what is stored, so the
            // sRGB-encoded output is not encoded a second time on write.
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Weva.Native.Offscreen" };
            rt.Create();
            var cmd = new CommandBuffer { name = "Weva.Native.Offscreen" };
            cmd.SetRenderTarget(rt);
            cmd.ClearRenderTarget(true, true, clear);
            // An offscreen target is not a camera pass, so _ProjectionParams is
            // not set for it: apply the flip Unity would (its projection into a
            // texture is inverted where UVs start at the top, D3D/Metal/Vulkan,
            // and not on OpenGL) so the page's top lands at the texture's top.
            Draw(cmd, width, height, SystemInfo.graphicsUVStartsAtTop ? 1 : -1, true, NativeRenderTarget.Of(rt));
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false, false) { name = "Weva.Native.Readback" };
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            result.Apply(false, false);
            RenderTexture.active = previous;
            rt.Release();
            Destroy(rt);
            return result;
        }

        public void Dispose()
        {
            foreach (Batch b in _batches) Destroy(b.Mesh);
            _batches.Clear();
            foreach (Mesh m in _meshPool) Destroy(m);
            _meshPool.Clear();
            foreach (Texture2D t in _textures.Values) Destroy(t);
            _textures.Clear();
            foreach (Material m in _materials.Values) Destroy(m);
            foreach (Material m in _blendMaterials.Values) Destroy(m);
            _blendMaterials.Clear();
            _materials.Clear();
            if (_untextured != null) Destroy(_untextured);
            _untextured = null;
            if (_backdrop != null) Destroy(_backdrop);
            _backdrop = null;
            _synced = false;
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }

    /// <summary>
    /// The target a draw list is issued into, for the draws that need to read
    /// it back (backdrop-filter). Inside the URP pass the handles are set and
    /// the copy is a texture the pass declared; offscreen only the identifiers
    /// and the descriptor are, and the renderer makes its own copy.
    /// </summary>
    internal readonly struct NativeRenderTarget
    {
        public readonly RenderTargetIdentifier Color;
        public readonly RenderTargetIdentifier Depth;
        public readonly bool HasDepth;
        public readonly RenderTextureDescriptor Descriptor;
        public readonly RTHandle ColorHandle;
        public readonly RTHandle DepthHandle;
        public readonly RTHandle CopyHandle;
        public readonly bool IsSet;

        public NativeRenderTarget(RTHandle color, RTHandle depth, RTHandle copy, RenderTextureDescriptor descriptor)
        {
            ColorHandle = color;
            DepthHandle = depth;
            CopyHandle = copy;
            Color = color;
            Depth = depth != null ? (RenderTargetIdentifier)depth : default;
            HasDepth = depth != null;
            Descriptor = descriptor;
            IsSet = true;
        }

        public NativeRenderTarget(RenderTargetIdentifier color, RenderTextureDescriptor descriptor)
        {
            Color = color;
            Depth = default;
            HasDepth = false;
            Descriptor = descriptor;
            ColorHandle = null;
            DepthHandle = null;
            CopyHandle = null;
            IsSet = true;
        }

        public static NativeRenderTarget Of(RenderTexture rt) => new NativeRenderTarget(rt, rt.descriptor);
    }
}
