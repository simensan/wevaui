// Turns libweva's published draw list into Unity meshes and draws them.
//
// The core hands the host textured triangle lists in document pixels with
// clip and opacity already resolved (scissored geometry is clipped before it
// is published, as the Godot host relies on), so the host's job is upload:
// mirror the document's textures by id, merge consecutive draws that share a
// texture into one mesh, and issue one DrawMesh per merged run through
// Hidden/Weva/NativeMesh. Backdrop-filter draws carry a transparent shape and
// are skipped; rounded rectangles arrive tessellated and draw as geometry.
//
// Two entry points: Draw(IUICommandBuffer) for the URP pass, and
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
    public sealed unsafe class NativeDocumentRenderer : IDisposable
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
        }

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
            var keep = new HashSet<ulong>();
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
                texture.Apply(false, false);
                _textures[t.id] = texture;
            }
            var dropped = new List<ulong>();
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
                var stale = new List<(ulong, int)>();
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
            foreach (Batch b in _batches) _meshPool.Add(b.Mesh);
            _batches.Clear();
            DrawsSkipped = 0;
            TrianglesUploaded = 0;
            ReadOnlySpan<weva_draw> draws = doc.Draws();
            int i = 0;
            while (i < draws.Length)
            {
                weva_draw first = draws[i];
                if (first.kind == (int)weva_draw_kind.WEVA_DRAW_BACKDROP_FILTER || first.vertex_count == 0 || first.index_count == 0)
                {
                    DrawsSkipped++;
                    i++;
                    continue;
                }
                _positions.Clear();
                _colors.Clear();
                _uvs.Clear();
                _indices.Clear();
                ulong texture = first.texture_id;
                int j = i;
                while (j < draws.Length)
                {
                    weva_draw d = draws[j];
                    if (d.kind == (int)weva_draw_kind.WEVA_DRAW_BACKDROP_FILTER || d.vertex_count == 0 || d.index_count == 0)
                    {
                        // A skipped draw between two runs does not split them.
                        DrawsSkipped++;
                        j++;
                        continue;
                    }
                    if (d.texture_id != texture || d.blend_mode != first.blend_mode) break;
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
                _batches.Add(new Batch { Mesh = mesh, Texture = texture, Triangles = _indices.Count / 3, Blend = first.blend_mode });
                TrianglesUploaded += _indices.Count / 3;
                i = j;
            }
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
        /// Issues the batches into a legacy command buffer. flip = 0 follows
        /// _ProjectionParams.x; gamma composites sRGB-encoded into a raw target
        /// the way a browser and the Godot host do (see the shader).
        /// </summary>
        public void Draw(CommandBuffer cmd, int width, int height, int flip = 0, bool gamma = false)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            cmd.SetGlobalVector(IdViewport, Viewport(width, height));
            cmd.SetGlobalInt(IdFlip, flip);
            cmd.SetGlobalInt(IdGamma, gamma ? 1 : 0);
            foreach (Batch b in _batches)
            {
                Material m = MaterialFor(b.Texture, b.Blend);
                if (m != null) cmd.DrawMesh(b.Mesh, Matrix4x4.identity, m);
            }
        }

#if WEVA_URP
        /// <summary>Issues the batches into the URP pass's command buffer (legacy or RenderGraph).</summary>
        public void Draw(IUICommandBuffer cmd, int width, int height)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            cmd.SetGlobalVector(IdViewport, Viewport(width, height));
            // A camera pass into a linear colour buffer: the shader follows
            // _ProjectionParams.x for the flip and blends in linear space.
            cmd.SetGlobalInt(IdFlip, 0);
            cmd.SetGlobalInt(IdGamma, 0);
            foreach (Batch b in _batches)
            {
                Material m = MaterialFor(b.Texture, b.Blend);
                if (m != null) cmd.DrawMesh(b.Mesh, Matrix4x4.identity, m);
            }
        }
#endif

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
            Draw(cmd, width, height, SystemInfo.graphicsUVStartsAtTop ? 1 : -1, gamma: true);
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
            _synced = false;
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
