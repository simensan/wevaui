#if WEVA_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Weva.Rendering {
    public sealed class ShaderResources : System.IDisposable {
        public const string SolidShaderName = "Hidden/Weva/Solid";
        public const string GradientShaderName = "Hidden/Weva/Gradient";
        public const string TextShaderName = "Hidden/Weva/Text";
        public const string ShadowShaderName = "Hidden/Weva/Shadow";
        public const string FilterShaderName = "Hidden/Weva/Filter";
        public const string StencilWriteShaderName = "Hidden/Weva/StencilWrite";

        // Pass indices on the StencilWrite shader. Documented contract: tests assert these.
        public const int StencilWritePushPass = 0;
        public const int StencilWritePopPass = 1;

        // Pass indices on the Filter shader. Must match Weva_Filter.shader's pass order.
        public const int FilterCompositePass = 0;
        public const int FilterBlurHorizontalPass = 1;
        public const int FilterBlurVerticalPass = 2;
        public const int FilterColorMatrixPass = 3;
        public const int FilterDropShadowTintPass = 4;

        public Shader Solid { get; private set; }
        public Shader Gradient { get; private set; }
        public Shader Text { get; private set; }
        public Shader Shadow { get; private set; }
        public Shader Filter { get; private set; }
        public Shader StencilWrite { get; private set; }

        readonly Dictionary<MaterialKey, Material> pool = new Dictionary<MaterialKey, Material>();
        Material stencilWriteMaterial;

        public ShaderResources() {
            Solid = Shader.Find(SolidShaderName);
            Gradient = Shader.Find(GradientShaderName);
            Text = Shader.Find(TextShaderName);
            Shadow = Shader.Find(ShadowShaderName);
            Filter = Shader.Find(FilterShaderName);
            StencilWrite = Shader.Find(StencilWriteShaderName);
        }

        public bool IsReady =>
            Solid != null && Gradient != null && Text != null && Shadow != null && Filter != null
            && StencilWrite != null;

        public Material GetStencilWrite() {
            if (StencilWrite == null) return null;
            if (stencilWriteMaterial == null) {
                stencilWriteMaterial = new Material(StencilWrite) { hideFlags = HideFlags.HideAndDontSave };
            }
            return stencilWriteMaterial;
        }

        public Material GetMaterial(Shader shader, BlendKind blend) {
            if (shader == null) return null;
            var key = new MaterialKey(shader.GetInstanceID(), blend);
            if (pool.TryGetValue(key, out var existing) && existing != null) return existing;
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            ApplyBlend(mat, blend);
            pool[key] = mat;
            return mat;
        }

        public Material GetSolid(BlendKind blend = BlendKind.PremultipliedAlpha) => GetMaterial(Solid, blend);
        public Material GetGradient(BlendKind blend = BlendKind.PremultipliedAlpha) => GetMaterial(Gradient, blend);
        public Material GetText(BlendKind blend = BlendKind.PremultipliedAlpha) => GetMaterial(Text, blend);
        public Material GetShadow(BlendKind blend = BlendKind.PremultipliedAlpha) => GetMaterial(Shadow, blend);
        public Material GetFilter(BlendKind blend = BlendKind.PremultipliedAlpha) => GetMaterial(Filter, blend);

        public void Dispose() {
            foreach (var mat in pool.Values) {
                if (mat != null) {
#if UNITY_EDITOR
                    if (Application.isPlaying) Object.Destroy(mat);
                    else Object.DestroyImmediate(mat);
#else
                    Object.Destroy(mat);
#endif
                }
            }
            pool.Clear();
            if (stencilWriteMaterial != null) {
#if UNITY_EDITOR
                if (Application.isPlaying) Object.Destroy(stencilWriteMaterial);
                else Object.DestroyImmediate(stencilWriteMaterial);
#else
                Object.Destroy(stencilWriteMaterial);
#endif
                stencilWriteMaterial = null;
            }
        }

        static void ApplyBlend(Material mat, BlendKind blend) {
            switch (blend) {
                case BlendKind.PremultipliedAlpha:
                    mat.SetInt("_SrcBlend", (int)BlendMode.One);
                    mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    break;
                case BlendKind.AlphaBlend:
                    mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    break;
                case BlendKind.Additive:
                    mat.SetInt("_SrcBlend", (int)BlendMode.One);
                    mat.SetInt("_DstBlend", (int)BlendMode.One);
                    break;
                case BlendKind.Opaque:
                    mat.SetInt("_SrcBlend", (int)BlendMode.One);
                    mat.SetInt("_DstBlend", (int)BlendMode.Zero);
                    break;
            }
        }

        readonly struct MaterialKey : System.IEquatable<MaterialKey> {
            public readonly int ShaderId;
            public readonly BlendKind Blend;
            public MaterialKey(int shaderId, BlendKind blend) { ShaderId = shaderId; Blend = blend; }
            public bool Equals(MaterialKey other) => ShaderId == other.ShaderId && Blend == other.Blend;
            public override bool Equals(object obj) => obj is MaterialKey k && Equals(k);
            public override int GetHashCode() {
                unchecked { return (ShaderId * 397) ^ (int)Blend; }
            }
        }
    }

    public enum BlendKind {
        PremultipliedAlpha,
        AlphaBlend,
        Additive,
        Opaque
    }
}
#endif
