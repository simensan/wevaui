// `backdrop-filter`: the one effect the core cannot turn into triangles,
// because it reads what is already in the target. The renderer copies the
// target, binds the copy here, and draws the element's SHAPE with this
// shader: each fragment blurs the copy under itself and applies the colour
// matrix the core composed from the filter list (grayscale, sepia, saturate,
// invert, brightness, contrast, opacity -- one affine transform in sRGB), so
// nothing here parses CSS. It REPLACES what is behind the element (Blend One
// Zero), as the Godot host's does; the element's own background is a later
// draw that lands on top.
//
// Where a fragment samples: its own pixel in the copy, which pass 1 made
// in memory order. In the URP pass the sample's y is inverted when the
// projection is NOT flipped (_ProjectionParams.x > 0, the camera's render
// texture in Unity 6's RenderGraph) -- measured, not derived: with the
// other rule a top-half element filtered the bottom half
// (NativeRenderPassTests.Pass_AppliesABackdropFilter_ToTheRightPixels).
// The back buffer as the target (a player without an intermediate) is the
// case that test cannot reach. Offscreen (an explicit _WevaNativeFlip)
// nothing is inverted.
Shader "Hidden/Weva/NativeBackdrop" {
    Properties {
        _WevaBackdropSigma ("Sigma", Float) = 0
        _WevaBackdropRow0 ("Row 0", Vector) = (1, 0, 0, 0)
        _WevaBackdropRow1 ("Row 1", Vector) = (0, 1, 0, 0)
        _WevaBackdropRow2 ("Row 2", Vector) = (0, 0, 1, 0)
        _WevaBackdropOffset ("Offset and alpha", Vector) = (0, 0, 0, 1)
    }
    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One Zero
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_WevaBackdropCopy);
            SAMPLER(sampler_WevaBackdropCopy);
            float4 _WevaNativeViewport;
            int _WevaNativeFlip;
            int _WevaNativeGamma;
            float _WevaBackdropSigma;
            float4 _WevaBackdropRow0;
            float4 _WevaBackdropRow1;
            float4 _WevaBackdropRow2;
            float4 _WevaBackdropOffset;

            struct Attributes {
                float3 positionOS : POSITION;
            };
            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.positionOS.xy * _WevaNativeViewport.zw * 2.0 - 1.0;
                float flip = (_WevaNativeFlip == 0) ? -_ProjectionParams.x : (float)_WevaNativeFlip;
                ndc.y *= flip;
                OUT.positionCS = float4(ndc, 0, 1);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float2 uv = IN.positionCS.xy * _WevaNativeViewport.zw;
                if (_WevaNativeFlip == 0 && _ProjectionParams.x > 0) uv.y = 1.0 - uv.y;
                float3 c;
                float sigma = _WevaBackdropSigma;
                if (sigma <= 0.0) {
                    c = SAMPLE_TEXTURE2D(_WevaBackdropCopy, sampler_WevaBackdropCopy, uv).rgb;
                } else {
                    // A 7x7 Gaussian spaced to span about two sigma, in one
                    // pass: the same kernel the Godot host runs, so the two
                    // hosts agree on the blur's shape.
                    float d = sigma * 0.66;
                    float3 acc = 0;
                    float wsum = 0;
                    for (int y = -3; y <= 3; y++) {
                        for (int x = -3; x <= 3; x++) {
                            float2 o = float2(x, y) * d;
                            float w = exp(-dot(o, o) / (2.0 * sigma * sigma));
                            acc += SAMPLE_TEXTURE2D(_WevaBackdropCopy, sampler_WevaBackdropCopy, uv + o * _WevaNativeViewport.zw).rgb * w;
                            wsum += w;
                        }
                    }
                    c = acc / wsum;
                }
                // The matrix is composed in sRGB: a linear target is encoded
                // for it and decoded after.
                if (_WevaNativeGamma == 0) c = LinearToSRGB(saturate(c));
                c = float3(dot(_WevaBackdropRow0.xyz, c), dot(_WevaBackdropRow1.xyz, c), dot(_WevaBackdropRow2.xyz, c)) + _WevaBackdropOffset.xyz;
                c = saturate(c);
                if (_WevaNativeGamma == 0) c = SRGBToLinear(c);
                return float4(c, _WevaBackdropOffset.w);
            }
            ENDHLSL
        }

        // Pass 1: the copy. A full-screen triangle into the copy texture,
        // each pixel reading the same pixel of the source bound by
        // identifier (a multisampled source resolves on the way) -- a copy
        // in memory order, which is what pass 0's sampling assumes.
        Pass {
            Name "Copy"
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vertCopy
            #pragma fragment fragCopy
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_WevaBackdropSource);
            SAMPLER(sampler_WevaBackdropSource);
            float4 _WevaNativeViewport;

            struct CopyVaryings {
                float4 positionCS : SV_POSITION;
            };

            CopyVaryings vertCopy(uint id : SV_VertexID) {
                CopyVaryings OUT;
                float2 ndc = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
                OUT.positionCS = float4(ndc, 0, 1);
                return OUT;
            }

            float4 fragCopy(CopyVaryings IN) : SV_Target {
                return SAMPLE_TEXTURE2D(_WevaBackdropSource, sampler_WevaBackdropSource, IN.positionCS.xy * _WevaNativeViewport.zw);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
