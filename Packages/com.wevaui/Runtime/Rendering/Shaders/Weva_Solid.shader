// Weva solid-color shader with rounded-rect SDF antialiasing.
//
// Vertex inputs (legacy channels — match URPRenderBackend.UploadMesh):
//   POSITION    (x, y, effectId)        x/y in screen pixel space (CSS top-left)
//   TEXCOORD0   (u, v)                  0..1 inside the quad
//   COLOR       linearColor (premul)
//   TANGENT     (rx, ry, halfW, halfH)
//
// Output: premultiplied alpha. Blend = One, OneMinusSrcAlpha (set on the material via
// _SrcBlend / _DstBlend at C# time).

Shader "Hidden/Weva/Solid" {
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
                float4 tangent    : TEXCOORD1;
            };

            float4 _WevaViewport;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                ndc.y = -ndc.y;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                OUT.tangent = IN.tangent;
                return OUT;
            }

            float roundedBoxSdf(float2 p, float2 halfSize, float rx, float ry) {
                float2 r = float2(rx, ry);
                float2 d = abs(p) - (halfSize - r);
                float2 a = max(d, 0.0);
                float inside = min(max(d.x, d.y), 0.0);
                float n = length(a / max(r, 1e-6));
                float corner = (rx > 0 && ry > 0) ? (n - 1.0) * min(rx, ry) : length(a);
                return inside + corner;
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 halfSize = IN.tangent.zw;
                float rx = IN.tangent.x;
                float ry = IN.tangent.y;
                if (rx <= 0.0 && ry <= 0.0) {
                    return IN.color;
                }
                float2 p = (IN.uv - 0.5) * (halfSize * 2.0);
                float d = roundedBoxSdf(p, halfSize, rx, ry);
                float aa = fwidth(d);
                float coverage = saturate(0.5 - d / max(aa, 1e-6));
                return IN.color * coverage;
            }
            ENDHLSL
        }
    }
}
