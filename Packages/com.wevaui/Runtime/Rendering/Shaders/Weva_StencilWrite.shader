// Weva stencil clip-mask shader. Used by URPRenderBackend.Submit(PushClipCommand)
// and Submit(PopClipCommand) to write/erase the rounded-rect clip mask into the
// stencil buffer. ColorMask 0; ZWrite Off — only modifies stencil.
//
// Vertex contract matches URPRenderBackend.UploadMesh / MeshBuilder.AddQuad:
//   POSITION   (x, y, effectId)        x/y in screen pixel space (CSS top-left)
//   TEXCOORD0  (u, v)                  0..1 inside the quad
//   COLOR      ignored
//   TANGENT    (rx, ry, halfW, halfH)
//
// Pass 0 (Push): Comp Equal against the *parent* ref + Pass IncrSat.
//   Backend sets _StencilWriteRef = (currentRef - 1) before this draw, so the
//   increment only fires inside the parent's clip region — nested clips are the
//   intersection of their parents.
//
// Pass 1 (Pop): Comp Equal against the *current* ref + Pass DecrSat.
//   Backend sets _StencilWriteRef = currentRef (the ref about to be popped). This
//   undoes exactly the increment that pass 0 performed.
//
// _StencilRef is the global int that *content* shaders compare against (set by
// URPRenderBackend.UpdateStencilGlobals to the current stack depth). It is
// independent of _StencilWriteRef.

Shader "Hidden/Weva/StencilWrite" {
    Properties {
        _StencilWriteRef ("Stencil Write Ref", Float) = 0
    }

    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        // ZTest Always: the default LEqual silently fails the stencil
        // IncrSat/DecrSat passes when the depth buffer at this stage holds
        // values written by earlier opaque/transparent passes (camera depth
        // is not cleared between SRP passes). A failed ZTest means the
        // Stencil "Pass" op never fires — Fail/ZFail are Keep — so the clip
        // mask is silently dropped. The content quads are fine because
        // they don't depend on stencil writes; they only read the stencil
        // and accept Always for ref=0. Stencil-clipped content (chat lines,
        // minimap pins, HP bar fills) all stay invisible because their
        // mask was never written. ZTest Always restores deterministic
        // stencil writes regardless of upstream depth.
        ZTest Always
        Cull Off
        ColorMask 0
        Blend Zero One

        // Pass 0 — Push.
        Pass {
            Name "PushClip"
            Stencil {
                Ref [_StencilWriteRef]
                Comp Equal
                Pass IncrSat
                Fail Keep
                ZFail Keep
            }

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
                float4 tangent    : TEXCOORD1;
            };

            float4 _WevaViewport;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                ndc.y = -ndc.y;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
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
                float rx = IN.tangent.x;
                float ry = IN.tangent.y;
                float2 halfSize = IN.tangent.zw;
                float coverage;
                if (rx <= 0.0 && ry <= 0.0) {
                    coverage = 1.0;
                } else {
                    float2 p = (IN.uv - 0.5) * (halfSize * 2.0);
                    float d = roundedBoxSdf(p, halfSize, rx, ry);
                    coverage = step(d, 0.0);
                }
                clip(coverage - 0.5);
                return float4(0, 0, 0, 0);
            }
            ENDHLSL
        }

        // Pass 1 — Pop.
        Pass {
            Name "PopClip"
            Stencil {
                Ref [_StencilWriteRef]
                Comp Equal
                Pass DecrSat
                Fail Keep
                ZFail Keep
            }

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
                float4 tangent    : TEXCOORD1;
            };

            float4 _WevaViewport;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                ndc.y = -ndc.y;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
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
                float rx = IN.tangent.x;
                float ry = IN.tangent.y;
                float2 halfSize = IN.tangent.zw;
                float coverage;
                if (rx <= 0.0 && ry <= 0.0) {
                    coverage = 1.0;
                } else {
                    float2 p = (IN.uv - 0.5) * (halfSize * 2.0);
                    float d = roundedBoxSdf(p, halfSize, rx, ry);
                    coverage = step(d, 0.0);
                }
                clip(coverage - 0.5);
                return float4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }
}
