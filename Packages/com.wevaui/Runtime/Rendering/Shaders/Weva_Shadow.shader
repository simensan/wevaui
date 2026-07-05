// Weva box-shadow shader. Approximates a Gaussian blur of a rounded-rect by sampling
// the analytic erf-of-rounded-rect SDF. This is the standard trick used by Skia / Chromium
// for axis-aligned box shadows; for v1 we treat radii as a uniform pair (per-corner radii
// fall back to the largest pair, see RoundRectSdf.PackUniform).
//
// _WevaShadowParams = (innerHalfW, innerHalfH, blurRadius, spread)
// per-vertex uv = pixel offset relative to the shadow rect's TL corner (0..innerW+2*pad)
// per-vertex tangent = unused
// per-vertex color = shadow color (premultiplied)

Shader "Hidden/Weva/Shadow" {
    Properties {
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
        Blend [_SrcBlend] [_DstBlend]
        Stencil {
            Ref [_StencilRef]
            Comp [_StencilComp]
            Pass Keep
            Fail Keep
            ZFail Keep
        }

        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes {
                float3 position : POSITION;
                float2 uv       : TEXCOORD0;
                float4 color    : COLOR;
                float4 tangent  : TANGENT;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            float4 _WevaViewport;
            float4 _WevaShadowParams;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                ndc.y = -ndc.y;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                return OUT;
            }

            // Approximate erf using a rational approximation. This is the same form used in
            // Chromium's box_shadow_filter_painter and Skia's SkBlurMask.
            float fastErf(float x) {
                float s = sign(x);
                x = abs(x);
                float t = 1.0 / (1.0 + 0.3275911 * x);
                float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-x * x);
                return s * y;
            }

            float boxShadowAlpha(float2 p, float2 halfSize, float blur) {
                float sigma = max(blur * 0.5, 0.5);
                float scale = 1.0 / (sigma * 1.4142135);
                float ax = 0.5 * (fastErf((p.x + halfSize.x) * scale) - fastErf((p.x - halfSize.x) * scale));
                float ay = 0.5 * (fastErf((p.y + halfSize.y) * scale) - fastErf((p.y - halfSize.y) * scale));
                return saturate(ax * ay);
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 halfSize = _WevaShadowParams.xy + _WevaShadowParams.w;
                float blur = max(_WevaShadowParams.z, 0.0);
                float2 p = IN.uv - halfSize; // recenter
                float a = boxShadowAlpha(p, halfSize, blur);
                return IN.color * a;
            }
            ENDHLSL
        }
    }
}
