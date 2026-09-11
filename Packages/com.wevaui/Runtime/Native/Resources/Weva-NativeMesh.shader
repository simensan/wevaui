// Draws libweva's published draw list: triangle lists in document pixel
// space (top-left origin, y down) with straight-alpha vertex colours and an
// optional texture (the glyph atlas, a gradient or an image). The core has
// already clipped scissored geometry and resolved opacity, so this is a plain
// textured-triangle shader; rounded rectangles and backdrop filters arrive
// tessellated and are drawn as geometry.
//
// Colour: the core's vertex colours are LINEAR with straight alpha; its
// textures hold sRGB bytes (images, gradients) or white with coverage in
// alpha (the glyph atlas) and are uploaded without a colour-space flag. A
// browser composites in gamma space, and so does the Godot host (it encodes
// the vertex colours to sRGB and lets the canvas blend them), so:
//   _WevaNativeGamma = 1: the target is raw (an offscreen sRGB-encoded page);
//       vertex colours are encoded to sRGB and texels pass through, and the
//       blend happens in gamma space exactly as the Godot host's does.
//   _WevaNativeGamma = 0: the target is a linear colour buffer (a camera
//       pass); vertex colours pass through and texels are decoded, and the
//       blend happens in linear space, which differs along blended edges.
// _WevaNativeViewport: (width, height, 1/width, 1/height) of the target.
// _WevaNativeFlip: 0 to follow _ProjectionParams.x (a camera pass), else the
// sign to apply to NDC y after the top-left mapping (an offscreen target).
Shader "Hidden/Weva/NativeMesh" {
    Properties {
        [NoScaleOffset] _WevaTex ("Texture", 2D) = "white" {}
        _WevaTextured ("Textured", Float) = 0
    }
    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_WevaTex);
            SAMPLER(sampler_WevaTex);
            float4 _WevaNativeViewport;
            int _WevaNativeFlip;
            int _WevaNativeGamma;
            float _WevaTextured;

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };
            struct Varyings {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.positionOS.xy * _WevaNativeViewport.zw * 2.0 - 1.0;
                float flip = (_WevaNativeFlip == 0) ? -_ProjectionParams.x : (float)_WevaNativeFlip;
                ndc.y *= flip;
                OUT.positionCS = float4(ndc, 0, 1);
                float4 c = IN.color;
                if (_WevaNativeGamma != 0) c.rgb = LinearToSRGB(saturate(c.rgb));
                OUT.color = c;
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float4 c = IN.color;
                if (_WevaTextured > 0.5) {
                    float4 t = SAMPLE_TEXTURE2D(_WevaTex, sampler_WevaTex, IN.uv);
                    if (_WevaNativeGamma == 0) t.rgb = SRGBToLinear(t.rgb);
                    c *= t;
                }
                return c;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
