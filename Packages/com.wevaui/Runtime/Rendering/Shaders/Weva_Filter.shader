// Weva filter pipeline shader. Five passes:
//   0 = composite        — premul-alpha blend of source RT into destination.
//   1 = blur-horizontal  — separable Gaussian along X.
//   2 = blur-vertical    — separable Gaussian along Y.
//   3 = color matrix     — 4×4 matrix + bias (brightness/contrast/grayscale/sepia/invert/saturate/hue-rotate/opacity).
//   4 = drop-shadow tint — RGB ← tint.rgb * src.a, alpha ← src.a * tint.a (silhouette).
//
// Coordinate convention matches Weva_Quad: the vertex shader takes a unit quad with
// pixel-space positions and maps to NDC via _WevaViewport.zw (1/width, 1/height).
// Filter passes draw a single quad covering the filter's bounds; the source RT is bound
// on _MainTex with its texel size in _MainTex_TexelSize.
//
// Gaussian: _WevaFilterParams = (sigma, stepX (1/w), stepY (1/h), sampleCount N).
// For separable blur of radius r px, sigma = r/2 and N = ceil(sigma*3). The shader
// unrolls up to 31 taps (N <= 15); the FilterPipeline runs multiple passes for larger σ.
//
// Color matrix: _WevaFilterMatrixRow0..3 hold rows of a 4×4 matrix; _WevaFilterMatrixBias
// holds the +bias column. Operates on straight-alpha RGB (the shader unpremultiplies,
// applies, re-premultiplies); CSS Filter Effects 1 §3 mandates straight-alpha math.

Shader "Hidden/Weva/Filter" {
    Properties {
        // _MainTex is intentionally NOT in the Properties block: the
        // pass binds the source RT via cb.SetGlobalTexture(_MainTex,
        // tempRt) at record time. Listing _MainTex here would create
        // a per-material slot whose `= "white"` default shadows the
        // global binding under URP RenderGraph's unsafe-pass path,
        // making SAMPLE_TEXTURE2D(_MainTex, ...) return opaque white
        // for every fragment. The composite then wrote opaque white
        // over the camera target, wiping any content (match3 body bg)
        // painted before the filter scope.
        _SrcBlend ("Src Blend", Float) = 1
        _DstBlend ("Dst Blend", Float) = 10
        _StencilRef ("Stencil Ref", Float) = 0
        _StencilComp ("Stencil Comp", Float) = 8
    }

    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        // ------------------------------------------------------------------
        // Pass 0 — composite the source RT into the destination with premul-
        // alpha blending. Used by Pop to splat the final RT back to its parent.
        // ------------------------------------------------------------------
        Pass {
            Name "Composite"
            // Hardcoded premultiplied-alpha blend rather than the
            // material-property-driven `Blend [_SrcBlend] [_DstBlend]`.
            // The property path was producing visibly incorrect output
            // under URP RenderGraph's unsafe-pass path — the material's
            // fixed-function blend state appeared to read stale (or
            // ReplaceOne/Zero) values at GPU execute time, even though
            // mat._SrcBlend / mat._DstBlend inspected as 1 / 10 from
            // C#. Symptom: every transparent pixel of the temp RT
            // (most of the screen for a `filter: blur` scope on
            // `.bg-aurora`) wrote opaque white onto the camera target,
            // wiping the body's radial gradient background. Hardcoding
            // the blend factors makes the GPU bracket binding
            // unambiguous and matches the premul contract the rest of
            // the pipeline uses.
            Blend One OneMinusSrcAlpha
            Stencil {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass Keep
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.wevaui/Runtime/Rendering/URP/UIShaderLib.hlsl"

            struct Attributes { float3 position : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float2 pixelPos : TEXCOORD1; };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float4 _WevaViewport;
            float4 _WevaFilterClipRect;
            float4 _WevaFilterClipRadii;
            float4 _WevaFilterClipRadiiY;
            float _WevaFilterClipEnabled;
            float _WevaFilterSourceYFlip;
            float _WevaFilterEncodeSrgb;
            // A-SRGB-COMPOSITE: 1 on the final intermediate-UI-RT -> camera
            // composite in a Linear project — the intermediate holds sRGB-
            // encoded premultiplied colour (gamma-space composited), so decode
            // it back to linear premul for the linear camera target.
            float _WevaFilterDecodeSrgb;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                // Match the quad shader's Y-flip convention so filter RT
                // content composites onto the camera target with the same
                // orientation it was rendered with. `-_ProjectionParams.x`
                // gives +1 when URP wrote the framebuffer un-flipped and -1
                // when D3D/Vulkan flipped it for us; the same hint applies
                // to temp RTs created via cmd.GetTemporaryRT inside this pass.
                ndc.y *= -_ProjectionParams.x;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                OUT.pixelPos = IN.position.xy;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 uv = IN.uv;
                if (_WevaFilterSourceYFlip > 0.5) uv.y = 1.0 - uv.y;
                float4 src = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                if (_WevaFilterClipEnabled > 0.5) {
                    float2 halfSize = _WevaFilterClipRect.zw * 0.5;
                    float2 center = _WevaFilterClipRect.xy + halfSize;
                    // Per-axis SDF: honour asymmetric `border-radius: <x> / <y>`
                    // by passing both axis vectors. When YRadii == XRadii (the
                    // common symmetric case) the function short-circuits to the
                    // exact circular SDF, so symmetric clipping is bit-identical
                    // to the previous behaviour.
                    float d = Weva_RoundedBoxSdfPerAxis(IN.pixelPos - center,
                        halfSize, _WevaFilterClipRadii, _WevaFilterClipRadiiY);
                    src *= Weva_Coverage(d);
                }
                if (_WevaFilterDecodeSrgb > 0.5) return Weva_PremulSrgbToPremulLinear(src);
                if (_WevaFilterEncodeSrgb > 0.5) return Weva_EncodeForTarget(src);
                return src;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Pass 1 — Gaussian blur, horizontal.
        // ------------------------------------------------------------------
        Pass {
            Name "BlurHorizontal"
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float3 position : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float4 _WevaViewport;
            float4 _WevaFilterParams;
            float _WevaFilterSourceYFlip;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                // Match the quad shader's Y-flip convention so filter RT
                // content composites onto the camera target with the same
                // orientation it was rendered with. `-_ProjectionParams.x`
                // gives +1 when URP wrote the framebuffer un-flipped and -1
                // when D3D/Vulkan flipped it for us; the same hint applies
                // to temp RTs created via cmd.GetTemporaryRT inside this pass.
                ndc.y *= -_ProjectionParams.x;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                return OUT;
            }

            float gaussWeight(float x, float sigma) {
                float s2 = max(sigma * sigma, 1e-4);
                return exp(-(x * x) / (2.0 * s2));
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 uv = IN.uv;
                if (_WevaFilterSourceYFlip > 0.5) uv.y = 1.0 - uv.y;
                float sigma = _WevaFilterParams.x;
                float2 step = float2(_WevaFilterParams.y, 0.0);
                int N = (int)_WevaFilterParams.w;
                float w0 = gaussWeight(0.0, sigma);
                float4 sum = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * w0;
                float weightSum = w0;
                [loop]
                for (int i = 1; i <= 31; i++) {
                    if (i > N) break;
                    float w = gaussWeight((float)i, sigma);
                    float2 du = step * (float)i;
                    sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + du) * w;
                    sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - du) * w;
                    weightSum += 2.0 * w;
                }
                return sum / max(weightSum, 1e-6);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Pass 2 — Gaussian blur, vertical.
        // ------------------------------------------------------------------
        Pass {
            Name "BlurVertical"
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float3 position : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float4 _WevaViewport;
            float4 _WevaFilterParams;
            float _WevaFilterSourceYFlip;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                // Match the quad shader's Y-flip convention so filter RT
                // content composites onto the camera target with the same
                // orientation it was rendered with. `-_ProjectionParams.x`
                // gives +1 when URP wrote the framebuffer un-flipped and -1
                // when D3D/Vulkan flipped it for us; the same hint applies
                // to temp RTs created via cmd.GetTemporaryRT inside this pass.
                ndc.y *= -_ProjectionParams.x;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                return OUT;
            }

            float gaussWeight(float x, float sigma) {
                float s2 = max(sigma * sigma, 1e-4);
                return exp(-(x * x) / (2.0 * s2));
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 uv = IN.uv;
                if (_WevaFilterSourceYFlip > 0.5) uv.y = 1.0 - uv.y;
                float sigma = _WevaFilterParams.x;
                float2 step = float2(0.0, _WevaFilterParams.z);
                int N = (int)_WevaFilterParams.w;
                float w0 = gaussWeight(0.0, sigma);
                float4 sum = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * w0;
                float weightSum = w0;
                [loop]
                for (int i = 1; i <= 31; i++) {
                    if (i > N) break;
                    float w = gaussWeight((float)i, sigma);
                    float2 du = step * (float)i;
                    sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + du) * w;
                    sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - du) * w;
                    weightSum += 2.0 * w;
                }
                return sum / max(weightSum, 1e-6);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Pass 3 — color matrix. Applies a 4×4 matrix + bias to the sample
        // (in straight-alpha RGB, then re-premultiplied). Powers brightness,
        // contrast, grayscale, sepia, invert, saturate, hue-rotate, opacity.
        //
        // COLOR SPACE: CSS Filter Effects 1 §8.4 — the filter functions'
        // color math operates on sRGB-ENCODED values (the default
        // `color-interpolation-filters: sRGB`; Chrome applies the matrices
        // to gamma-encoded channels). Our RTs hold LINEAR values, so the
        // pass decodes to sRGB around the matrix. Applying the matrix to
        // linear values was the GLASS-PANEL-DARK divergence: glass.css's
        // `backdrop-filter: ... saturate(1.7)` over-saturated and darkened
        // the non-dominant channels (measured panel (43,108,181) vs
        // Chrome's (60,109,198); the linear-applied matrix reproduces the
        // exact red/blue-deficit signature arithmetically).
        // ------------------------------------------------------------------
        Pass {
            Name "ColorMatrix"
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.wevaui/Runtime/Rendering/URP/UIShaderLib.hlsl"

            struct Attributes { float3 position : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float4 _WevaViewport;
            float4 _WevaFilterMatrixRow0;
            float4 _WevaFilterMatrixRow1;
            float4 _WevaFilterMatrixRow2;
            float4 _WevaFilterMatrixRow3;
            float4 _WevaFilterMatrixBias;
            float _WevaFilterSourceYFlip;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                // Match the quad shader's Y-flip convention so filter RT
                // content composites onto the camera target with the same
                // orientation it was rendered with. `-_ProjectionParams.x`
                // gives +1 when URP wrote the framebuffer un-flipped and -1
                // when D3D/Vulkan flipped it for us; the same hint applies
                // to temp RTs created via cmd.GetTemporaryRT inside this pass.
                ndc.y *= -_ProjectionParams.x;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 uv = IN.uv;
                if (_WevaFilterSourceYFlip > 0.5) uv.y = 1.0 - uv.y;
                float4 src = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                if (src.a < 1e-5) return src;
                // CSS Filter Effects: matrix math runs on sRGB-encoded
                // straight-alpha channels (see pass header comment).
                float3 rgb = Weva_LinearToSrgb(src.rgb / src.a);
                float r = dot(_WevaFilterMatrixRow0.xyz, rgb) + _WevaFilterMatrixBias.x;
                float g = dot(_WevaFilterMatrixRow1.xyz, rgb) + _WevaFilterMatrixBias.y;
                float b = dot(_WevaFilterMatrixRow2.xyz, rgb) + _WevaFilterMatrixBias.z;
                float3 outRgb = Weva_SrgbToLinear(saturate(float3(r, g, b)));
                // Row3.w is the alpha scale (always 1 for color filters; ≠1 for opacity()).
                float outA = src.a * _WevaFilterMatrixRow3.w;
                return float4(outRgb * outA, outA);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Pass 4 — drop-shadow silhouette: RGB ← tint.rgb (scaled by src.a),
        // alpha ← src.a × tint.a. Output is premul; consumed by the blur passes.
        // ------------------------------------------------------------------
        Pass {
            Name "DropShadowTint"
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float3 position : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float4 _WevaViewport;
            float4 _WevaFilterDropShadowTint;
            float _WevaFilterSourceYFlip;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                // Match the quad shader's Y-flip convention so filter RT
                // content composites onto the camera target with the same
                // orientation it was rendered with. `-_ProjectionParams.x`
                // gives +1 when URP wrote the framebuffer un-flipped and -1
                // when D3D/Vulkan flipped it for us; the same hint applies
                // to temp RTs created via cmd.GetTemporaryRT inside this pass.
                ndc.y *= -_ProjectionParams.x;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 uv = IN.uv;
                if (_WevaFilterSourceYFlip > 0.5) uv.y = 1.0 - uv.y;
                float4 src = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                float a = src.a * _WevaFilterDropShadowTint.a;
                return float4(_WevaFilterDropShadowTint.rgb * a, a);
            }
            ENDHLSL
        }
    }
}
