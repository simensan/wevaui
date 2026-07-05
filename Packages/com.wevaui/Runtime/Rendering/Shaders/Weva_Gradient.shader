// Weva gradient shader. Linear and radial gradients with up to 8 stops, evenly spaced
// when stop positions aren't supplied (CSS default behavior). Stops are uploaded as a
// global Vector4 array (rgb premultiplied, a = stop position 0..1) — the shader cooperates
// by reading _WevaGradientCount and _WevaGradientStops.
//
// _WevaGradientAxis is overloaded:
//   linear : (cosTheta, sinTheta, 0, 0)
//   radial : (centerX, centerY, radiusX, radiusY) in box-local pixel coordinates
//
// We pick linear vs radial by the sign of the .z component (radial = nonzero radius).

Shader "Hidden/Weva/Gradient" {
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
                float4 tangent    : TEXCOORD1;
            };

            float4 _WevaViewport;
            // C# COUPLING: this array dimension MUST match URPRenderBackend.MaxStops.
            // Update both sites in lockstep.
            float4 _WevaGradientStops[8];
            int _WevaGradientCount;
            float4 _WevaGradientAxis;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float2 ndc = IN.position.xy * _WevaViewport.zw * 2.0 - 1.0;
                ndc.y = -ndc.y;
                OUT.positionCS = float4(ndc, 0, 1);
                OUT.uv = IN.uv;
                OUT.tangent = IN.tangent;
                return OUT;
            }

            float4 sampleStops(float t) {
                if (_WevaGradientCount <= 0) return float4(0, 0, 0, 0);
                if (_WevaGradientCount == 1) return _WevaGradientStops[0];
                t = saturate(t);
                int n = _WevaGradientCount - 1;
                float scaled = t * (float)n;
                int i0 = (int)floor(scaled);
                if (i0 >= n) i0 = n - 1;
                float f = saturate(scaled - (float)i0);
                float4 a = _WevaGradientStops[i0];
                float4 b = _WevaGradientStops[i0 + 1];
                return lerp(a, b, f);
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
                bool isRadial = _WevaGradientAxis.z > 0.0001 || _WevaGradientAxis.w > 0.0001;
                float t;
                if (isRadial) {
                    float2 c = _WevaGradientAxis.xy;
                    float2 r = _WevaGradientAxis.zw;
                    float2 p = IN.uv - c;
                    p /= max(r, 1e-6);
                    t = length(p);
                } else {
                    float2 axis = _WevaGradientAxis.xy;
                    float2 p = IN.uv - 0.5;
                    t = dot(p, axis) + 0.5;
                }
                float4 c = sampleStops(t);

                float rx = IN.tangent.x;
                float ry = IN.tangent.y;
                if (rx > 0.0 || ry > 0.0) {
                    float2 halfSize = IN.tangent.zw;
                    float2 p = (IN.uv - 0.5) * (halfSize * 2.0);
                    float d = roundedBoxSdf(p, halfSize, rx, ry);
                    float aa = fwidth(d);
                    float coverage = saturate(0.5 - d / max(aa, 1e-6));
                    c *= coverage;
                }
                return c;
            }
            ENDHLSL
        }
    }
}
