// Weva SDF text shader.
//
// Contract with the TextCore atlas integration:
//   _AtlasTex      : Texture2D (R8, single-channel SDF). Per-glyph distance encoded as
//                    [0..255], with 128 = the glyph boundary; >128 = inside, <128 = outside.
//                    The pixel range (i.e. how many *pixels in the atlas* one unit of SDF
//                    distance covers) is encoded in TANGENT.x ("_SdfPxRange").
//   per-vertex uv  : Glyph atlas UVs. The four corners of a glyph quad are TL/BL/BR/TR.
//   per-vertex color : Premultiplied LinearColor (final tint).
//   TANGENT        : (sdfPxRange, glyphScale, 0, 0) — same for every vertex of a glyph.
//
// The TextCore agent must produce glyph quads via URPRenderBackend.EmitGlyphQuads. Each
// quad's TANGENT.x = pxRange and TANGENT.y = the rasterized scale factor (atlas px per
// font px). A coarse fallback when pxRange is unset (= 0) treats the texel as binary.

Shader "Hidden/Weva/Text" {
    Properties {
        _AtlasTex ("Atlas", 2D) = "white" {}
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

            TEXTURE2D(_AtlasTex);
            SAMPLER(sampler_AtlasTex);
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

            float4 frag(Varyings IN) : SV_Target {
                float d = SAMPLE_TEXTURE2D(_AtlasTex, sampler_AtlasTex, IN.uv).r;
                float pxRange = max(IN.tangent.x, 1.0);
                float scale = max(IN.tangent.y, 1.0);
                float aa = pxRange / scale;
                float coverage = saturate((d - 0.5) * aa + 0.5);
                return IN.color * coverage;
            }
            ENDHLSL
        }
    }
}
