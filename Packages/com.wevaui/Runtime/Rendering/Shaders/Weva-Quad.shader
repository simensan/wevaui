// Weva über-shader. Single material handles all brush kinds via instance data and
// keyword toggles. The fragment computes a rounded-rect SDF, anti-aliased coverage, and
// blends through one of the brush samplers (solid / linear / radial / conic / shadow /
// text). Bordered quads layer per-edge stripes on top of the body fill.
//
// Per-instance data is uploaded as a packed Vector4 array via MaterialPropertyBlock
// (`_WevaInstances`). The vertex shader reads WEVA_INSTANCE_FLOAT4S float4s
// starting at instanceID*WEVA_INSTANCE_FLOAT4S.
// Bound count = `_WevaInstanceCount`; out-of-range IDs collapse to a degenerate quad.
//
// Coordinate system: vertex inputs are a unit quad (0..1)x(0..1). The vertex shader maps
// (uv - 0.5) * 2 * halfSize + centerXY into pixel space, then projects via _WevaViewport.

Shader "Hidden/Weva/Quad" {
    Properties {
        _SrcBlend ("Src Blend", Float) = 1
        _DstBlend ("Dst Blend", Float) = 10
        _StencilRef ("Stencil Ref", Float) = 0
        _StencilComp ("Stencil Comp", Float) = 8
        [NoScaleOffset] _WevaImage ("Weva Image", 2D) = "white" {}
        // B16 — path coverage mask texture. Bound per-batch when any mask layer
        // carries kind=5 (image-mask) whose source is a PathCoverageImageSource.
        // Defaults to white (fully revealing) so batches without a path mask
        // sample 1.0 for all fragments and the mask has no visible effect.
        [NoScaleOffset] _WevaMaskImage ("Weva Mask Image", 2D) = "white" {}
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
            // Shader Model 4.5 — required for StructuredBuffer<float4>
            // (_WevaInstancesSB below) which the vertex shader reads to
            // expand the per-instance quad. Without this directive Unity
            // compiles to SM 2.5/3.0, which has no StructuredBuffer support,
            // and the shader fails silently on platforms that fall back to
            // a lower feature level — notably Android OpenGL ES 3.0, where
            // the failure manifests as a fully-white screen (no draws emit,
            // the camera clears to its default color). Vulkan + ES 3.1+
            // both satisfy SM 4.5; pair with Player Settings'
            // `Require ES 3.1` to refuse installation on ES 3.0–only
            // devices that can't run this shader at all.
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            // CSS gradient brushes are dispatched by brushIndex inside the
            // shader; the keywords just toggle whether each branch compiles
            // in. The Class-0 batch (Solid + Text + Gradient*) enables ALL
            // three keywords simultaneously so a single variant handles every
            // fill kind — that requires INDEPENDENT toggles, not a single
            // mutually-exclusive multi_compile (which would force the
            // runtime to pick one and compile the other two branches out,
            // making e.g. conic-gradient invisible whenever the linear
            // variant won the keyword race). Three boolean keywords expand
            // to 8 variants (2^3) instead of 4, but only one — all-three-on
            // — is actually used by class-0; the others stay tiny.
            #pragma multi_compile_local _ _BRUSH_LINEAR
            #pragma multi_compile_local _ _BRUSH_RADIAL
            #pragma multi_compile_local _ _BRUSH_CONIC
            #pragma multi_compile_local _ _BORDERED
            // Same independent-toggle pattern as the brush keywords above:
            // class-0 batches mix outset + inset shadows alongside fills,
            // so both shadow keywords need to be on simultaneously. The
            // shader's shadow branch (`if (brushIndex == 5 || brushIndex
            // == 6)`) isn't keyword-gated and always compiles in, but the
            // keywords still trigger material state to be consistent with
            // the brush-class-0 batching contract.
            #pragma multi_compile_local _ _SHADOW_OUTSET
            #pragma multi_compile_local _ _SHADOW_INSET
            #pragma multi_compile_local _ _TEXT
            // _TEXT_COLOR is gone: color-bitmap vs mono-SDF text now
            // dispatches per-instance via BorderColorTop.y's high bit
            // (see the brushIndex==7 branch in frag). Folding the two
            // paths into one shader variant means consecutive glyphs from
            // different-typed atlases land in the same batch instead of
            // forcing a flush — match3's HUD went from 11 atlas-clash
            // boundaries to zero.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.wevaui/Runtime/Rendering/URP/UIShaderLib.hlsl"

            // Per-chunk mesh layout: each batch chunk is rendered as a
            // *single* DrawMesh of (4 verts × chunk quads). TANGENT.x
            // carries the per-quad instance index (0..255) as a float so
            // the vertex shader can read instance data from
            // _WevaInstances. We use TANGENT (a float4 channel by
            // default in Mesh) because COLOR is UNorm8-normalised on
            // upload — values >1 clamp and integers >255 alias, breaking
            // distinguishability past chunk size 64.
            //
            // We avoid cmd.DrawMeshInstanced because under URP / Unity 6
            // RasterCommandBuffer it silently produces no rasterized
            // pixels (no errors, no draws — verified by replacing the
            // call with cmd.DrawMesh, which works).
            struct Attributes {
                float2 vertex     : POSITION;
                float2 uv         : TEXCOORD0;
                float4 instanceId : TANGENT;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                nointerpolation float instanceId : TEXCOORD1;
                // World-space pixel position of the fragment, used by the
                // ClipRect AABB scissor in the fragment shader. Computed in
                // the vertex shader after applying the per-quad 2D affine
                // transform so rotated/skewed quads still see the correct
                // pre-transform pixel coordinate to test against the parent
                // clip rect. (For non-axis-aligned content this degrades to
                // the AABB of the rotated geometry, which is conservative
                // and matches Chrome's behavior for `overflow: hidden` on
                // rotated descendants.)
                float2 worldPos   : TEXCOORD2;
                // Hot per-instance data forwarded from the vertex shader
                // with `nointerpolation` so the fragment reads them as
                // constants instead of issuing StructuredBuffer loads
                // every fragment. Every quad needs all five for the SDF +
                // brush + clip path; pulling them through interpolators
                // converts ~5 SB loads per fragment into 0 — a measurable
                // win once the per-fragment cost dominates (panels +
                // gradients covering large pixel areas at 1080p+).
                // Less-common fields (border colors/widths, text UV slot)
                // stay in StructuredBuffer and are loaded behind the
                // _BORDERED / _TEXT keyword guards.
                nointerpolation float4 posSize    : TEXCOORD3;
                nointerpolation float4 radii      : TEXCOORD4;
                nointerpolation float4 fillColor  : TEXCOORD5;
                nointerpolation float4 brushParams: TEXCOORD6;
                nointerpolation float4 clipRect   : TEXCOORD7;
                nointerpolation float4 clipShape0 : TEXCOORD8;
                // Per-corner vertical radii (rTL, rTR, rBR, rBL) for elliptical
                // border-radius. Pairs with `radii` (horizontal). A component of
                // 0 means "use the matching `radii` component" so circular and
                // legacy quads stay bit-identical.
                nointerpolation float4 radiiY     : TEXCOORD9;
            };

            float4 _WevaViewport;
            // When drawing batches into an offscreen filter RT, the instance
            // positions stay in screen-pixel space (so the per-fragment ClipRect
            // test against IN.worldPos remains correct), but the NDC mapping
            // must subtract the RT's screen-space origin. _WevaViewportOrigin.xy
            // carries (offsetX, offsetY) — zero when drawing to the main color
            // target.
            float4 _WevaViewportOrigin;
            // Instance data lives in a StructuredBuffer rather than a cbuffer
            // so a single draw can carry well past the 256-instance cbuffer
            // cap (we use 1024 in the C# pass). Each draw owns its own buffer
            // (via the per-MPB.SetBuffer pool) so chunks don't trample each
            // other at execute time.
            StructuredBuffer<float4> _WevaInstancesSB;

            #define WEVA_INSTANCE_FLOAT4S 58

            float4 ReadInstance(uint id, int slot) {
                uint idx = id * WEVA_INSTANCE_FLOAT4S + slot;
                return _WevaInstancesSB[idx];
            }

            Varyings vert(Attributes IN) {
                Varyings OUT;
                // Instance index is encoded in TANGENT.x as a float (0..255).
                // Mesh stores Tangents as float4 unconditionally so the
                // value round-trips losslessly even with chunkSize > 64.
                // SV_InstanceID + cmd.DrawMeshInstanced silently produced
                // nothing on Unity 6 / URP RG (verified empirically).
                uint id = (uint)(IN.instanceId.x + 0.5);

                float4 posSize = ReadInstance(id, 0);
                float4 transformR0 = ReadInstance(id, 10);
                float4 transformR1 = ReadInstance(id, 11);
                float4 transformR2 = ReadInstance(id, 12);

                float2 halfSize = posSize.zw;
                float2 center = posSize.xy;

                // Pre-load the per-instance fields once (reused for the AA-bleed
                // gate below and the fragment interpolators at the tail).
                float4 radiiInst = ReadInstance(id, 1);
                float4 brushParamsInst = ReadInstance(id, 3);

                // Geometry AA bleed. A `border-radius` curve is inscribed in the
                // border box and runs TANGENT to the quad boundary (a 50% circle
                // touches at the four edge mid-points). The SDF coverage ramp
                // (Weva_Coverage) needs ~1px of fragments on BOTH sides of the
                // edge, but the outer half (signed distance > 0) falls outside
                // the quad — where the rasterizer emits no fragments. The ramp
                // is clipped to its inner half, leaving a hard, aliased step on
                // the curve (the glass blob's circular clip edge against its
                // radial-gradient fill: a 0→0.5 jump with no outer AA band).
                //
                // Expand the quad outward by `aaBleed` CSS px and carry an
                // EXPANDED uv so the fragment's box-local coordinate and brush
                // uv still map to the true element box; the SDF then renders its
                // full outer AA band into the bleed margin. Gated to rounded
                // solid/gradient fills (brush 0..3) — plain rects have no curved
                // edge to alias (and tile edge-to-edge, where an outer fringe
                // would seam), and image/shadow/text paths sample uv assuming
                // [0,1] (atlas-bleed / tile-clip risk). 1.5px matches
                // clipRejectPad so an ancestor overflow clip still trims it.
                int brushIndexV = (int)(brushParamsInst.x + 0.5);
                float maxRadiusV = max(max(radiiInst.x, radiiInst.y), max(radiiInst.z, radiiInst.w));
                float aaBleed = (maxRadiusV > 0.5 && brushIndexV >= 0 && brushIndexV <= 3) ? 1.5 : 0.0;
                float2 uvScale = (halfSize + aaBleed) / max(halfSize, float2(1e-3, 1e-3));
                float2 uvExpanded = 0.5 + (IN.uv - 0.5) * uvScale;

                // The matrix acts on the FULL world position (local + center),
                // not just the box-local offset. Paired with CPU-side pivot
                // baking (see EmitWrappersFresh), this means parent transforms
                // rotate child positions through the parent's pivot — fixing
                // `::before` and other children sliding inside a rotated parent.
                // For a solo rotated box, the math collapses to the prior
                // `R*local + center` behaviour (pivot baking cancels out).
                float2 local = (uvExpanded - 0.5) * (halfSize * 2.0);
                float2 world = local + center;
                float2 wpos;
                wpos.x = transformR0.x * world.x + transformR1.x * world.y + transformR2.x;
                wpos.y = transformR0.y * world.x + transformR1.y * world.y + transformR2.y;

                // Pixel space → NDC. CSS-y grows down, clip-y grows up; the
                // base mapping (2*wpos.y/H - 1) puts CSS-top at NDC-bottom
                // (= screen bottom on standard projection). Multiply by
                // -_ProjectionParams.x so it lands at screen TOP regardless
                // of whether URP wrote the framebuffer Y-flipped (D3D
                // graphicsUVStartsAtTop=true → x=+1, on-screen direct → x=-1).
                // _ProjectionParams.x is the canonical Unity flag for this.
                // Subtract _WevaViewportOrigin so filter-RT passes (which
                // have a per-RT viewport with a screen-space origin) place
                // their content at the right sub-rect.
                float2 rtPos = wpos - _WevaViewportOrigin.xy;
                float2 ndc = rtPos * _WevaViewport.zw * 2.0 - 1.0;
                ndc.y *= -_ProjectionParams.x;
                OUT.positionCS = float4(ndc, 0, 1);
                // Carry the bleed-expanded uv (== IN.uv when aaBleed == 0) so the
                // fragment recomputes the same box-local coordinate the geometry
                // was built from and the brush samples the true box.
                OUT.uv = uvExpanded;
                OUT.instanceId = (float)id;
                OUT.worldPos = wpos;
                // Pre-load the five hot fields once per vertex; the
                // fragment shader reads them from interpolators.
                OUT.posSize = posSize;
                OUT.radii = radiiInst;
                OUT.fillColor = ReadInstance(id, 2);
                OUT.brushParams = brushParamsInst;
                OUT.clipRect = ReadInstance(id, 13);
                OUT.clipShape0 = ReadInstance(id, 16);
                OUT.radiiY = ReadInstance(id, 57);
                return OUT;
            }

            bool Weva_ClipPathContains(uint id, float4 shape0, float2 worldPos) {
                int kind = (int)(shape0.x + 0.5);
                if (kind == 0) return true;
                if (kind == 1) {
                    float left = shape0.y;
                    float top = shape0.z;
                    float right = shape0.w;
                    float4 s1 = ReadInstance(id, 17);
                    float bottom = s1.x;
                    if (worldPos.x < left || worldPos.x > right || worldPos.y < top || worldPos.y > bottom) return false;
                    float2 center = float2((left + right) * 0.5, (top + bottom) * 0.5);
                    float2 halfSize = float2(max((right - left) * 0.5, 0.0), max((bottom - top) * 0.5, 0.0));
                    float4 radii = float4(s1.y, s1.z, s1.w, ReadInstance(id, 18).x);
                    float sd = Weva_RoundedBoxSdf(worldPos - center, halfSize, radii);
                    return sd <= 0.0;
                }
                if (kind == 2) {
                    float2 d = worldPos - shape0.yz;
                    return dot(d, d) <= shape0.w * shape0.w;
                }
                if (kind == 3) {
                    float ry = ReadInstance(id, 17).x;
                    float rx = max(shape0.w, 1e-5);
                    ry = max(ry, 1e-5);
                    float2 n = (worldPos - shape0.yz) / float2(rx, ry);
                    return dot(n, n) <= 1.0;
                }
                if (kind == 4) {
                    int count = min(8, max(0, (int)(shape0.y + 0.5)));
                    if (count < 3) return false;
                    // shape0.z carries the CSS fill rule (0 = nonzero, 1 = evenodd).
                    // Defaults to 0 for older callers that left the channel zeroed,
                    // matching the spec default `nonzero`. See tracker G5b.
                    int fillRule = (int)(shape0.z + 0.5);
                    float4 p01 = ReadInstance(id, 17);
                    float4 p23 = ReadInstance(id, 18);
                    float4 p45 = ReadInstance(id, 19);
                    float4 p67 = ReadInstance(id, 20);
                    float2 prev = count == 8 ? p67.zw
                        : count == 7 ? p67.xy
                        : count == 6 ? p45.zw
                        : count == 5 ? p45.xy
                        : count == 4 ? p23.zw
                        : p23.xy;
                    bool insideEvenOdd = false;
                    int winding = 0;
                    [loop]
                    for (int i = 0; i < 8; i++) {
                        if (i >= count) break;
                        float2 curr = i == 0 ? p01.xy
                            : i == 1 ? p01.zw
                            : i == 2 ? p23.xy
                            : i == 3 ? p23.zw
                            : i == 4 ? p45.xy
                            : i == 5 ? p45.zw
                            : i == 6 ? p67.xy
                            : p67.zw;
                        if (fillRule == 1) {
                            // Even-odd: toggle on every horizontal-ray crossing.
                            bool crosses = (curr.y > worldPos.y) != (prev.y > worldPos.y);
                            if (crosses) {
                                float denom = prev.y - curr.y;
                                denom = abs(denom) < 1e-6 ? (denom < 0 ? -1e-6 : 1e-6) : denom;
                                float atX = (prev.x - curr.x) * (worldPos.y - curr.y) / denom + curr.x;
                                if (worldPos.x < atX) insideEvenOdd = !insideEvenOdd;
                            }
                        } else {
                            // Nonzero winding: track edge direction. Mirrors
                            // PolygonClipPathShape.Contains' winding path
                            // (j=prev, i=curr): edges with prev.y <= y and
                            // curr.y > y are tagged "upward" (+1), the reverse
                            // is "downward" (-1). Inside when accumulated
                            // winding is nonzero. The C# labels follow CSS-y
                            // (grows down); fragment matches one-for-one.
                            bool upward = prev.y <= worldPos.y && curr.y > worldPos.y;
                            bool downward = prev.y > worldPos.y && curr.y <= worldPos.y;
                            if (upward || downward) {
                                float denom = curr.y - prev.y;
                                denom = abs(denom) < 1e-6 ? (denom < 0 ? -1e-6 : 1e-6) : denom;
                                float atX = (curr.x - prev.x) * (worldPos.y - prev.y) / denom + prev.x;
                                if (worldPos.x < atX) {
                                    winding += upward ? 1 : -1;
                                }
                            }
                        }
                        prev = curr;
                    }
                    return fillRule == 1 ? insideEvenOdd : (winding != 0);
                }
                return true;
            }

            // CSS Compositing 1 §11 — separable RGB blend mode formulas.
            // Inputs are PREMULTIPLIED-alpha colors per the spec; the C#
            // side already premultiplies fill/text/shadow contributions
            // before they reach the shader. Each helper returns the blended
            // RGB; the caller wires the spec-mandated source-over alpha
            // contribution separately.
            //
            // The 13 modes implemented here are the "separable" ones — they
            // operate channel-by-channel and don't need an sRGB->HSL pass.
            // The four HSL-based modes (hue / saturation / color /
            // luminosity, CSS Compositing 1 §11.5..§11.8) follow further
            // down via the SetLum/SetSat/ClipColor helper chain.
            //
            // Reference: https://www.w3.org/TR/compositing-1/#blendingseparable
            float3 Weva_BlendMultiply(float3 a, float3 b)  { return a * b; }
            float3 Weva_BlendScreen(float3 a, float3 b)    { return a + b - a * b; }
            float3 Weva_BlendOverlay(float3 a, float3 b) {
                // overlay(a,b) = a <= 0.5 ? 2*a*b : 1 - 2*(1-a)*(1-b)
                // Evaluate per-channel via lerp on a step mask.
                float3 lo = 2.0 * a * b;
                float3 hi = 1.0 - 2.0 * (1.0 - a) * (1.0 - b);
                float3 m = step(0.5, a); // 1 when a > 0.5, 0 when a <= 0.5
                return lerp(lo, hi, m);
            }
            float3 Weva_BlendDarken(float3 a, float3 b)    { return min(a, b); }
            float3 Weva_BlendLighten(float3 a, float3 b)   { return max(a, b); }
            float3 Weva_BlendColorDodge(float3 a, float3 b) {
                // a == 0 ? 0 : b == 1 ? 1 : min(1, a / (1 - b))
                // (a is backdrop, b is source in the spec naming; we follow
                // that convention so the formula reads 1:1 with §11.)
                float3 r;
                [unroll]
                for (int i = 0; i < 3; i++) {
                    float ai = a[i];
                    float bi = b[i];
                    if (ai == 0.0) r[i] = 0.0;
                    else if (bi >= 1.0) r[i] = 1.0;
                    else r[i] = min(1.0, ai / max(1.0 - bi, 1e-6));
                }
                return r;
            }
            float3 Weva_BlendColorBurn(float3 a, float3 b) {
                // a == 1 ? 1 : b == 0 ? 0 : 1 - min(1, (1 - a) / b)
                float3 r;
                [unroll]
                for (int i = 0; i < 3; i++) {
                    float ai = a[i];
                    float bi = b[i];
                    if (ai >= 1.0) r[i] = 1.0;
                    else if (bi == 0.0) r[i] = 0.0;
                    else r[i] = 1.0 - min(1.0, (1.0 - ai) / max(bi, 1e-6));
                }
                return r;
            }
            float3 Weva_BlendHardLight(float3 a, float3 b) {
                // hard-light(a,b) = overlay(b,a) — the multiply/screen split
                // is driven by the SOURCE rather than the BACKDROP.
                float3 lo = 2.0 * a * b;
                float3 hi = 1.0 - 2.0 * (1.0 - a) * (1.0 - b);
                float3 m = step(0.5, b);
                return lerp(lo, hi, m);
            }
            float3 Weva_BlendSoftLight(float3 a, float3 b) {
                // CSS Compositing 1 §11.9: piecewise soft-light. D(a) selects
                // the upper-half curve: a <= 0.25 ? ((16a - 12)a + 4)a : sqrt(a).
                float3 r;
                [unroll]
                for (int i = 0; i < 3; i++) {
                    float ai = a[i];
                    float bi = b[i];
                    float d;
                    if (ai <= 0.25) d = ((16.0 * ai - 12.0) * ai + 4.0) * ai;
                    else d = sqrt(ai);
                    if (bi <= 0.5) r[i] = ai - (1.0 - 2.0 * bi) * ai * (1.0 - ai);
                    else r[i] = ai + (2.0 * bi - 1.0) * (d - ai);
                }
                return r;
            }
            float3 Weva_BlendDifference(float3 a, float3 b) { return abs(a - b); }
            float3 Weva_BlendExclusion(float3 a, float3 b)  { return a + b - 2.0 * a * b; }
            float3 Weva_BlendPlusLighter(float3 a, float3 b) {
                // plus-lighter(a,b) = min(1, a + b). Operates on premultiplied
                // components per CSS Compositing 1 §11.13.
                return min(float3(1.0, 1.0, 1.0), a + b);
            }

            // CSS Compositing 1 §11.4 — HSL helper scaffolding for the four
            // HSL-based blend modes (hue / saturation / color / luminosity).
            //
            // Operates on UN-premultiplied components per the spec. The
            // caller is responsible for unpremultiplying both inputs before
            // feeding them in, and re-premultiplying the result. (Per
            // CSS Compositing 1 §10 the alpha contribution is independent
            // of the RGB blend formula either way.)
            //
            // The Lum/Sat decomposition + SetLum/SetSat re-projection is
            // intentionally NOT a full sRGB->HSL cylindrical conversion —
            // the spec defines HSL-based modes via a luminosity coefficient
            // dot product and a sat = max-min channel range, then projects
            // back through ClipColor (which rescales toward Lum to keep
            // the result inside [0,1]^3). This is cheaper and pixel-exact
            // against the spec text.
            float Weva_Lum(float3 c) {
                return dot(c, float3(0.3, 0.59, 0.11));
            }

            float Weva_Sat(float3 c) {
                return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
            }

            // ClipColor(C): if any channel is out of [0,1] after a SetLum
            // shift, rescale the WHOLE color toward Lum so the OOR channel
            // lands at the boundary. Spec §11.4.
            float3 Weva_ClipColor(float3 c) {
                float l = Weva_Lum(c);
                float n = min(c.r, min(c.g, c.b));
                float x = max(c.r, max(c.g, c.b));
                // Branch-free rescale: scaleLow = l / (l - n) when n < 0,
                // scaleHigh = (1 - l) / (x - l) when x > 1. We apply low
                // first then high (the spec's two-step form); if neither
                // bound is violated both passes are no-ops.
                if (n < 0.0) {
                    float denom = l - n;
                    denom = denom == 0.0 ? 1e-6 : denom;
                    c = l + (c - l) * (l / denom);
                }
                if (x > 1.0) {
                    float denom = x - l;
                    denom = denom == 0.0 ? 1e-6 : denom;
                    c = l + (c - l) * ((1.0 - l) / denom);
                }
                return c;
            }

            // SetLum(C, l): translate the color so its Lum matches l, then
            // ClipColor to keep channels in range. Spec §11.4.
            float3 Weva_SetLum(float3 c, float l) {
                float d = l - Weva_Lum(c);
                return Weva_ClipColor(c + float3(d, d, d));
            }

            // SetSat(C, s): rescale the channel range so max - min = s,
            // preserving the relative order of the three channels.
            // Spec §11.4 sorts (Cmin, Cmid, Cmax); only Cmid and Cmax get
            // a non-zero result, Cmin clamps to 0.
            //
            // We sort branch-free via min/max chains: cmin / cmax are the
            // obvious min/max, cmid = (r + g + b) - cmin - cmax. Then we
            // compute the new mid/max values, and write them back into
            // r/g/b based on which channel held which sorted slot.
            float3 Weva_SetSat(float3 c, float s) {
                float cmin = min(c.r, min(c.g, c.b));
                float cmax = max(c.r, max(c.g, c.b));
                float cmid = c.r + c.g + c.b - cmin - cmax;
                float range = cmax - cmin;
                // New cmid / cmax under the target saturation. When the
                // original range is degenerate (cmax == cmin) the color
                // is a uniform grey; SetSat leaves it as solid 0,0,0 per
                // spec ("if Cmax > Cmin: ...; otherwise: ...Cmid = 0,
                // Cmax = 0").
                float newMid;
                float newMax;
                if (range > 0.0) {
                    newMid = ((cmid - cmin) * s) / range;
                    newMax = s;
                } else {
                    newMid = 0.0;
                    newMax = 0.0;
                }
                // Project the sorted (0, newMid, newMax) triple back to
                // the r/g/b ordering. We compare each channel against the
                // ORIGINAL cmin / cmax; the equality branches use isMin /
                // isMax masks, with cmid as the residual. Ties (e.g. two
                // channels at cmax) collapse identically — the result has
                // both at newMax, matching the spec's "preserve order"
                // intent.
                float3 r;
                // Channel R
                if (c.r >= cmax - 1e-7 && c.r >= cmax) r.r = newMax;
                else if (c.r <= cmin + 1e-7 && c.r <= cmin) r.r = 0.0;
                else r.r = newMid;
                // Channel G
                if (c.g >= cmax - 1e-7 && c.g >= cmax) r.g = newMax;
                else if (c.g <= cmin + 1e-7 && c.g <= cmin) r.g = 0.0;
                else r.g = newMid;
                // Channel B
                if (c.b >= cmax - 1e-7 && c.b >= cmax) r.b = newMax;
                else if (c.b <= cmin + 1e-7 && c.b <= cmin) r.b = 0.0;
                else r.b = newMid;
                return r;
            }

            // CSS Compositing 1 §11.5: hue(Cb, Cs)
            //   = SetLum(SetSat(Cs, Sat(Cb)), Lum(Cb))
            // Result takes the SOURCE hue, the BACKDROP sat, the BACKDROP lum.
            float3 Weva_BlendHue(float3 cb, float3 cs) {
                return Weva_SetLum(Weva_SetSat(cs, Weva_Sat(cb)), Weva_Lum(cb));
            }

            // CSS Compositing 1 §11.6: saturation(Cb, Cs)
            //   = SetLum(SetSat(Cb, Sat(Cs)), Lum(Cb))
            // Result keeps the BACKDROP hue/lum but takes the SOURCE sat.
            float3 Weva_BlendSaturation(float3 cb, float3 cs) {
                return Weva_SetLum(Weva_SetSat(cb, Weva_Sat(cs)), Weva_Lum(cb));
            }

            // CSS Compositing 1 §11.7: color(Cb, Cs)
            //   = SetLum(Cs, Lum(Cb))
            // Result takes the SOURCE hue+sat with the BACKDROP lum.
            float3 Weva_BlendColor(float3 cb, float3 cs) {
                return Weva_SetLum(cs, Weva_Lum(cb));
            }

            // CSS Compositing 1 §11.8: luminosity(Cb, Cs)
            //   = SetLum(Cb, Lum(Cs))
            // Result keeps the BACKDROP hue+sat but takes the SOURCE lum.
            // (The inverse of color, useful for re-toning monochrome overlays.)
            float3 Weva_BlendLuminosity(float3 cb, float3 cs) {
                return Weva_SetLum(cb, Weva_Lum(cs));
            }

            // Per-batch destination-copy texture. DrainBatches (UIRenderGraph-
            // Pass.cs) blits colorTarget → this RT immediately before drawing
            // any batch whose NeedsBackdropRefresh flag is set (B24 v1,
            // CSS Compositing 1 §10). The blit captures everything painted into
            // the camera target up to (but not including) the current batch —
            // body background, sibling panels, earlier blended layers — so the
            // blend dispatcher here sees a backdrop matching what Chrome would
            // use for the same element. The global _WevaBackdropAvailable float
            // gates sampling: 0 = no copy bound (use black), 1 = bound.
            TEXTURE2D(_WevaBackdrop);
            SAMPLER(sampler_WevaBackdrop);
            float4 _WevaBackdrop_TexelSize;
            // 0 = no backdrop bound (use black); 1 = bound.
            float _WevaBackdropAvailable;
            // 0 = copy is top-down (CSS orientation — camera rendered straight
            // to the backbuffer); 1 = copy is bottom-up (camera rendered to an
            // intermediate RT, e.g. post-processing on) and the sample must
            // flip V. Mirrors backdropCaptureSourceYFlip on the C# side — the
            // backdrop-filter capture got this correction first (A-MIXBLEND-
            // YFLIP was the filed sibling gap for this sampler).
            float _WevaBackdropYFlip;

            float3 Weva_SampleBackdropPremul(float2 worldPos) {
                if (_WevaBackdropAvailable < 0.5) return float3(0.0, 0.0, 0.0);
                // worldPos is screen-pixel space; _WevaBackdrop_TexelSize.xy
                // gives 1/width, 1/height. The backdrop is assumed to already
                // hold the active color target with premultiplied alpha.
                float2 uv = worldPos * _WevaBackdrop_TexelSize.xy;
                if (_WevaBackdropYFlip > 0.5) uv.y = 1.0 - uv.y;
                return SAMPLE_TEXTURE2D(_WevaBackdrop, sampler_WevaBackdrop, uv).rgb;
            }

            // Pure blend formula dispatch — UNPREMULTIPLIED inputs and output.
            // CSS Compositing 1 §11: each mode's formula B(Cb, Cs) operates on
            // straight (unpremultiplied) colour values. Both callers below
            // unpremultiply before calling this helper and re-premultiply after.
            //
            // Modes 1..11: separable (§11.1..§11.12 excluding plus-lighter).
            // Modes 13..16: non-separable HSL-based (§11.5..§11.8).
            // Mode 12 (plus-lighter) is a PREMULTIPLIED compositing operator
            // and is handled separately in Weva_ApplyMixBlendMode below.
            // HLSL has no forward declarations — this helper must be defined
            // before both Weva_ApplyMixBlendMode and Weva_FinishFragment.
            float3 Weva_BlendFormula(int mode, float3 cb, float3 cs) {
                if (mode == 1)  return Weva_BlendMultiply(cb, cs);
                if (mode == 2)  return Weva_BlendScreen(cb, cs);
                if (mode == 3)  return Weva_BlendOverlay(cb, cs);
                if (mode == 4)  return Weva_BlendDarken(cb, cs);
                if (mode == 5)  return Weva_BlendLighten(cb, cs);
                if (mode == 6)  return Weva_BlendColorDodge(cb, cs);
                if (mode == 7)  return Weva_BlendColorBurn(cb, cs);
                if (mode == 8)  return Weva_BlendHardLight(cb, cs);
                if (mode == 9)  return Weva_BlendSoftLight(cb, cs);
                if (mode == 10) return Weva_BlendDifference(cb, cs);
                if (mode == 11) return Weva_BlendExclusion(cb, cs);
                if (mode == 13) return Weva_BlendHue(cb, cs);
                if (mode == 14) return Weva_BlendSaturation(cb, cs);
                if (mode == 15) return Weva_BlendColor(cb, cs);
                if (mode == 16) return Weva_BlendLuminosity(cb, cs);
                return cs; // pass-through for unknown modes
            }

            // Page-backdrop blend dispatcher (CSS Compositing 1 §6 / mix-blend-mode).
            //
            // `srcPremul` is the per-fragment source contribution (already
            // premultiplied + clipped + masked). `dstPremul` is the pre-existing
            // page-backdrop color sampled from _WevaBackdrop. Returns the blended
            // source RGB; alpha is untouched (source-over alpha is applied
            // independently of the RGB blend formula).
            //
            // Modes 1..11: separable — both page-backdrop and element-local paths
            //   reuse Weva_BlendFormula.
            // Mode 12: plus-lighter — PREMULTIPLIED compositing operator; kept here
            //   only (element-local background-blend-mode disallows it per spec).
            // Modes 13..16: non-separable HSL-based — also via Weva_BlendFormula.
            // Mode 17 (ExactSrgbSourceOver): internal-only; returns a sentinel float4
            //   via the caller (Weva_FinishFragment) that bypasses the hardware
            //   blend entirely. This function is never called for mode 17 — the
            //   caller short-circuits BEFORE this dispatch.
            float3 Weva_ApplyMixBlendMode(int mode, float3 srcPremul, float3 dstPremul, float srcAlpha) {
                if (mode <= 0) return srcPremul;
                // plus-lighter is a PREMULTIPLIED compositing operator
                // (CSS Compositing 1 §11.13: co = αs·Cs + αb·Cb), not a
                // separable blend, so it keeps its premultiplied inputs.
                // Valid only for mix-blend-mode (page-backdrop); not valid for
                // background-blend-mode (CSS Compositing 1 §9 accepts only
                // <blend-mode> values, which excludes plus-lighter).
                // v1: computed in linear premul (Chrome adds sRGB-encoded
                // premul values; divergence only matters for mid-range sums —
                // revisit if a real design depends on it).
                if (mode == 12) return Weva_BlendPlusLighter(dstPremul, srcPremul);
                // Every other mode: the blend formula B(Cb, Cs) is defined on
                // UNPREMULTIPLIED colour, and the blended source must be
                // re-weighted by the source alpha for the premultiplied
                // src-over the hardware blend (One, OneMinusSrcAlpha) performs:
                // the emitted premultiplied RGB is αs·B(Cb, Cs).
                //
                // The separable modes (1..11) previously fed srcPremul straight
                // into the helpers and returned WITHOUT the ·srcAlpha re-weight.
                // For a partially/fully transparent fragment that over-counts
                // the backdrop: e.g. screen() on a transparent sweep pixel
                // returned B(Cb, 0) = Cb (the raw backdrop) as premultiplied
                // RGB at alpha 0, and the One blend then ADDED that backdrop
                // onto the framebuffer — blowing the radar minimap's transparent
                // sweep area out to white. Unpremultiply → blend → re-premultiply
                // (what the HSL modes 13..16 already did). For an opaque source
                // (srcAlpha == 1) this is identical to the old result, so opaque
                // blends are unchanged. dstPremul comes from _WevaBackdrop,
                // sampled as an opaque framebuffer colour, so it is already
                // effectively unpremultiplied against alpha 1.
                //
                // Colour space: CSS Compositing 1 blends in the page's colour
                // space — sRGB. Chrome verified analytically (its multiply
                // against #ffcc00 zeroes the blue channel exactly, which only
                // holds for sRGB-encoded operands). Our pipeline is linear, so
                // encode both operands to sRGB, blend, and decode — the same
                // round-trip the element-local background-blend path performs.
                float a = max(srcAlpha, 1e-6);
                float3 cs = Weva_LinearToSrgb(saturate(srcPremul / a));
                // A-SRGB-COMPOSITE: under gamma compositing the backdrop copy is
                // already sRGB-ENCODED (the B24 per-batch refresh blits the sRGB
                // intermediate into _WevaBackdrop), so use it directly; otherwise
                // it is linear and must be encoded.
                float3 cb = (_WevaSrgbComposite > 0.5)
                    ? saturate(dstPremul)
                    : Weva_LinearToSrgb(saturate(dstPremul));
                float3 blended = Weva_SrgbToLinear(Weva_BlendFormula(mode, cb, cs));
                return blended * srcAlpha;
            }

            float Weva_AabbClipCoverage(float4 clipRect, float2 worldPos) {
                float d = max(
                    max(clipRect.x - worldPos.x, worldPos.x - clipRect.z),
                    max(clipRect.y - worldPos.y, worldPos.y - clipRect.w));
                return Weva_Coverage(d);
            }

            // Anti-aliased coverage for a polygon clip-path (kind 4). Mirrors
            // Weva_ClipPathContains' winding / even-odd test for the inside
            // SIGN, but also accumulates the distance to the nearest edge so the
            // cut corners get a ~1px coverage ramp instead of a hard 1-bit
            // stairstep (the visible aliasing on story-bubble's octagon corners).
            float Weva_PolygonClipCoverage(uint id, float4 shape0, float2 worldPos) {
                int count = min(8, max(0, (int)(shape0.y + 0.5)));
                if (count < 3) return 0.0;
                int fillRule = (int)(shape0.z + 0.5);
                float4 p01 = ReadInstance(id, 17);
                float4 p23 = ReadInstance(id, 18);
                float4 p45 = ReadInstance(id, 19);
                float4 p67 = ReadInstance(id, 20);
                float2 prev = count == 8 ? p67.zw
                    : count == 7 ? p67.xy
                    : count == 6 ? p45.zw
                    : count == 5 ? p45.xy
                    : count == 4 ? p23.zw
                    : p23.xy;
                bool insideEvenOdd = false;
                int winding = 0;
                float minDistSq = 1e20;
                [loop]
                for (int i = 0; i < 8; i++) {
                    if (i >= count) break;
                    float2 curr = i == 0 ? p01.xy
                        : i == 1 ? p01.zw
                        : i == 2 ? p23.xy
                        : i == 3 ? p23.zw
                        : i == 4 ? p45.xy
                        : i == 5 ? p45.zw
                        : i == 6 ? p67.xy
                        : p67.zw;
                    // Distance from worldPos to edge segment (prev -> curr).
                    float2 pa = worldPos - prev;
                    float2 ba = curr - prev;
                    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
                    float2 off = pa - ba * h;
                    minDistSq = min(minDistSq, dot(off, off));
                    if (fillRule == 1) {
                        bool crosses = (curr.y > worldPos.y) != (prev.y > worldPos.y);
                        if (crosses) {
                            float denom = prev.y - curr.y;
                            denom = abs(denom) < 1e-6 ? (denom < 0 ? -1e-6 : 1e-6) : denom;
                            float atX = (prev.x - curr.x) * (worldPos.y - curr.y) / denom + curr.x;
                            if (worldPos.x < atX) insideEvenOdd = !insideEvenOdd;
                        }
                    } else {
                        bool upward = prev.y <= worldPos.y && curr.y > worldPos.y;
                        bool downward = prev.y > worldPos.y && curr.y <= worldPos.y;
                        if (upward || downward) {
                            float denom = curr.y - prev.y;
                            denom = abs(denom) < 1e-6 ? (denom < 0 ? -1e-6 : 1e-6) : denom;
                            float atX = (curr.x - prev.x) * (worldPos.y - prev.y) / denom + prev.x;
                            if (worldPos.x < atX) winding += upward ? 1 : -1;
                        }
                    }
                    prev = curr;
                }
                bool inside = fillRule == 1 ? insideEvenOdd : (winding != 0);
                float dist = sqrt(minDistSq);
                // Signed distance: negative inside, positive outside — matches
                // the convention Weva_Coverage expects (same as the SDF clips).
                float sd = inside ? -dist : dist;
                return Weva_Coverage(sd);
            }

            float Weva_ClipPathCoverage(uint id, float4 shape0, float2 worldPos) {
                int kind = (int)(shape0.x + 0.5);
                if (kind == 0) return 1.0;
                if (kind == 1) {
                    float left = shape0.y;
                    float top = shape0.z;
                    float right = shape0.w;
                    float4 s1 = ReadInstance(id, 17);
                    float bottom = s1.x;
                    float2 center = float2((left + right) * 0.5, (top + bottom) * 0.5);
                    float2 halfSize = float2(max((right - left) * 0.5, 0.0), max((bottom - top) * 0.5, 0.0));
                    float4 radii = float4(s1.y, s1.z, s1.w, ReadInstance(id, 18).x);
                    float sd = Weva_RoundedBoxSdf(worldPos - center, halfSize, radii);
                    return Weva_Coverage(sd);
                }
                if (kind == 2) {
                    float sd = length(worldPos - shape0.yz) - shape0.w;
                    return Weva_Coverage(sd);
                }
                if (kind == 3) {
                    float ry = ReadInstance(id, 17).x;
                    float rx = max(shape0.w, 1e-5);
                    ry = max(ry, 1e-5);
                    float2 n = (worldPos - shape0.yz) / float2(rx, ry);
                    return Weva_Coverage(length(n) - 1.0);
                }
                if (kind == 4) return Weva_PolygonClipCoverage(id, shape0, worldPos);
                return Weva_ClipPathContains(id, shape0, worldPos) ? 1.0 : 0.0;
            }

            float4 SampleBrush(uint id, float4 brushParams, float2 uv, float4 fillColor, float2 boxSize) {
                // R18: round (matching the vertex shader's brushIndexV) rather
                // than truncate. The value is an exact small integer today so
                // this is identical, but a half-precision interpolator or a
                // 1-ulp-low perturbation would otherwise turn brush 7 (Text)
                // into 6 (ShadowInset).
                int brushIndex = (int)(brushParams.x + 0.5);

                // Brush 0 = solid. Branchless solid path returns the per-instance color.
                if (brushIndex == 0) {
                    return fillColor;
                }

                if (brushIndex == 4) {
                    float4 uvSlot = ReadInstance(id, 5);
                    float2 sampleUv = uv;
                    float4 tile = ReadInstance(id, 6);
                    if (tile.z > 0.0 && tile.w > 0.0) {
                        float4 repeatGap = ReadInstance(id, 7);
                        float2 stride = max(tile.zw + max(repeatGap.zw, float2(0.0, 0.0)), float2(1e-5, 1e-5));
                        float2 p = uv * boxSize - tile.xy;
                        bool inside = true;
                        if ((int)(repeatGap.x + 0.5) == 1) {
                            inside = inside && p.x >= 0.0 && p.x < tile.z;
                        } else {
                            p.x = p.x - floor(p.x / stride.x) * stride.x;
                            inside = inside && p.x < tile.z;
                        }
                        if ((int)(repeatGap.y + 0.5) == 1) {
                            inside = inside && p.y >= 0.0 && p.y < tile.w;
                        } else {
                            p.y = p.y - floor(p.y / stride.y) * stride.y;
                            inside = inside && p.y < tile.w;
                        }
                        if (!inside) return float4(0, 0, 0, 0);
                        sampleUv = p / max(tile.zw, float2(1e-5, 1e-5));
                    }
                    float2 uvMin = float2(brushParams.y, brushParams.z);
                    float2 uvMax = float2(brushParams.w, uvSlot.x);
                    float2 imageUv = float2(
                        lerp(uvMin.x, uvMax.x, sampleUv.x),
                        lerp(uvMax.y, uvMin.y, sampleUv.y));
                    float4 texel = SAMPLE_TEXTURE2D(_WevaImage, sampler_WevaImage, imageUv);
                    return float4(texel.rgb * texel.a, texel.a) * fillColor.a;
                }

                float4 stop1 = ReadInstance(id, 5); // BorderColorTop = stop[1] color (or stop[N-1] in 2-stop fast path)
                float4 startColor = fillColor;

                // GTILE-1: Gradient tile remapping.
                // BorderWidths (slot 4) carries (originX, originY, tileW, tileH) in
                // box-local pixels. Default = (0, 0, boxW, boxH) = full-box repeat
                // (packed by UIBatcher.EmitGradient for every gradient instance).
                // BorderStyles.xy = (noRepX, noRepY): 1 = no-repeat (clip outside tile),
                // 0 = repeat (frac wrap). Only valid for non-repeating linear ≤4-stop
                // gradients (UIBatcher only writes these flags for that case).
                //
                // For conic/radial BorderStyles.x carries the encoded stop count (e.g.
                // -5.0 for a 5-stop sRGB conic). That value never hits exactly 1.0 so
                // noRepX is safe. BUT BorderStyles.y carries p4 — the 5th stop position —
                // which IS exactly 1.0 for any gradient whose last stop is at t=1. That
                // made noRepY = true for every 5-stop (and some 6-stop) conic/radial
                // gradient, clipping the bottom half of the element to transparent.
                //
                // Guard: only treat BorderStyles.xy as no-repeat flags when the gradient
                // is a non-repeating linear ≤4-stop. Detection: abs(.x) < 1.5 (rules out
                // stopCount ≥2 encoded as ±N) AND .w < 0.5 (rules out repeating-linear
                // and conic/radial which both set .w=1 when repeating). For ≤4-stop
                // linear, .x is 0 (sRGB) or ±small fraction for colorspace; the noRep
                // flags sit in .x==1 / .y==1 only when UIBatcher explicitly sets them.
                //
                // CSS Backgrounds 3 §3.4/§3.6: the gradient is resolved within the tile
                // rect, not the element box.
                //
                // LINEAR ONLY. This tile remap reads slot 4 (origin/size) and
                // BorderStyles.xy (no-repeat flags) — data UIBatcher.EmitGradient
                // only packs meaningfully for linear gradients. For conic/radial,
                // BorderStyles.x carries the stop count (not a no-repeat flag) and
                // only the full-box default tile applies (documented v1 scope), so
                // the remap should be the identity. It WASN'T: the `floor(gp /
                // tileSize)` wrap below ran on radial too, and because the shader's
                // boxSize (= halfSize*2, integer-rounded from posSize) and the
                // packed tileSize (= bounds.Width, fractional for vw-sized blobs)
                // disagree by a sub-pixel, the wrap seam landed ~1px INSIDE the
                // border-radius edge instead of at it — sampling the gradient at
                // the opposite edge and stamping a faint teal spike-ring at the
                // clip boundary. Invisible on blobs whose gradient is still opaque
                // at the edge, but a visible crawling streak on a glass blob whose
                // radial-gradient has already faded to alpha 0 before the edge
                // (the dark annulus makes the spike stand out). Gating to linear
                // makes radial/conic use uv directly — correct for the full-box
                // default and free of the seam.
                if (brushIndex == 1) {
                    float4 tileData = ReadInstance(id, 4);
                    float2 tileOrigin = tileData.xy;
                    float2 tileSize   = max(tileData.zw, float2(1e-5, 1e-5));
                    float2 gp = uv * boxSize - tileOrigin;
                    float4 noRepFlags = ReadInstance(id, 9); // BorderStyles
                    // Only non-repeating linear ≤4-stop gradients use .xy as no-repeat
                    // flags. abs(.x) >= 1.5 means a stop-count is encoded (conic/radial/
                    // linear-5+). .w >= 0.5 means repeating (sets flag to 1.0).
                    bool isTileableLinear = abs(noRepFlags.x) < 1.5 && noRepFlags.w < 0.5;
                    bool noRepX = isTileableLinear && (int)(noRepFlags.x + 0.5) == 1;
                    bool noRepY = isTileableLinear && (int)(noRepFlags.y + 0.5) == 1;
                    if (noRepX) {
                        if (gp.x < 0.0 || gp.x >= tileData.z) return float4(0, 0, 0, 0);
                    } else {
                        gp.x = gp.x - floor(gp.x / tileSize.x) * tileSize.x;
                    }
                    if (noRepY) {
                        if (gp.y < 0.0 || gp.y >= tileData.w) return float4(0, 0, 0, 0);
                    } else {
                        gp.y = gp.y - floor(gp.y / tileSize.y) * tileSize.y;
                    }
                    uv = gp / tileSize;
                }

                // Multi-stop dispatch: stopCount lives in BrushParams.w for
                // linear, in BorderStyles.x for conic. Radial stays 2-stop.
                // The 2-stop path is the cheap fast path: one extra
                // StructuredBuffer load (stop1) and one lerp. Multi-stop pays
                // 2 more loads (stop[2], stop[3], positions) only when
                // stopCount > 2 — uniform per-draw, branch-friendly.

#if defined(_BRUSH_LINEAR)
                if (brushIndex == 1) {
                    // G1b 3-state colorSpace decode:
                    //   sign >= 0           → 0 (linear-RGB)
                    //   sign  < 0, frac~=0  → 1 (sRGB; legacy default)
                    //   sign  < 0, frac~=.25→ 2 (Oklab; new in G1b)
                    int colorSpace = brushParams.w < 0.0 ? 1 : 0;
                    if (colorSpace == 1 && frac(abs(brushParams.w)) > 0.1) colorSpace = 2;
                    int stopCount = (int)(abs(brushParams.w) + 0.5);
                    // Slot 9 (BorderStyles) is multiplexed for linear quads:
                    //   Repeating linear:   (0, period, 0, 1)   — .w = flag
                    //   Non-repeating 5/6:  (stopCount, p4, p5, 0)
                    //   Non-repeating 2-4:  (0, 0, 0, 0)
                    // The flag lives in .w (not .x) so a stopCount of 5 or 6
                    // in the non-repeating 5/6-stop layout can't be misread
                    // as "isRepeating > 0.5". See UIBatcher.EmitGradient.
                    float4 borderStyles = ReadInstance(id, 9);
                    bool isRepeating = borderStyles.w > 0.5;
                    if (isRepeating) {
                        // The repeating sampler always uses the multi-stop slot
                        // layout (it needs stop positions to compute the ramp);
                        // stop2/stop3 read past valid data for stopCount<=2 but
                        // the function ignores them in that branch.
                        float4 stop2 = ReadInstance(id, 6);
                        float4 stop3 = ReadInstance(id, 7);
                        float4 positions = ReadInstance(id, 8);
                        return Weva_LinearGradientRepeating(uv, brushParams.yz,
                                                               startColor, stop1, stop2, stop3,
                                                               positions, stopCount, borderStyles.y,
                                                               colorSpace);
                    }
                    if (stopCount <= 2) {
                        return Weva_LinearGradient(uv, brushParams.yz, startColor, stop1, colorSpace);
                    }
                    float4 stop2nr = ReadInstance(id, 6);
                    float4 stop3nr = ReadInstance(id, 7);
                    float4 positionsNr = ReadInstance(id, 8);
                    if (stopCount <= 4) {
                        return Weva_LinearGradientMultiStop(uv, brushParams.yz,
                                                              startColor, stop1, stop2nr, stop3nr,
                                                              positionsNr, stopCount, colorSpace);
                    }
                    // 5/6-stop linear: read the two extra colors and the
                    // extra positions piggybacked on BorderStyles.y/.z, then
                    // hand off to the 6-stop walker. Mirrors the conic
                    // dispatch a few lines below.
                    float4 stop4 = ReadInstance(id, 14);
                    float4 stop5 = ReadInstance(id, 15);
                    return Weva_LinearGradientMultiStop6(uv, brushParams.yz,
                                                           startColor, stop1, stop2nr, stop3nr,
                                                           stop4, stop5,
                                                           positionsNr, borderStyles.y, borderStyles.z,
                                                           stopCount, colorSpace);
                }
#endif
#if defined(_BRUSH_RADIAL)
                if (brushIndex == 2) {
                    float4 radiusYPad = ReadInstance(id, 6);
                    float2 radius = float2(brushParams.w, radiusYPad.x);
                    // Radial slot layout:
                    //   stop0 = Color, stop1 = BorderColorTop,
                    //   stop2 = BorderColorBottom, stop3 = GradientStop4,
                    //   positions = BorderColorLeft, count/sign = BorderStyles.x.
                    //   IsRepeating flag = BorderStyles.w (G13c).
                    // Older 2-stop packing left BorderStyles.x at 0 and
                    // stored the sRGB flag in positions.z; keep that fallback
                    // so pre-existing cached data still samples correctly.
                    float4 positions = ReadInstance(id, 8);
                    float4 borderStyles = ReadInstance(id, 9);
                    int stopCount = (int)(abs(borderStyles.x) + 0.5);
                    int colorSpace = borderStyles.x < 0.0 ? 1 : 0;
                    if (colorSpace == 1 && frac(abs(borderStyles.x)) > 0.1) colorSpace = 2;
                    if (stopCount <= 0) {
                        stopCount = 2;
                        colorSpace = positions.z > 0.5 ? 1 : 0;
                    }
                    float4 stop2 = ReadInstance(id, 7);
                    float4 stop3 = ReadInstance(id, 14);
                    // G13c: repeating-radial-gradient. BorderStyles.w == 1
                    // (set by UIBatcher.EmitGradient when RadialGradient
                    // .IsRepeating is true) routes to the wrap sampler.
                    bool radialRepeating = borderStyles.w > 0.5;
                    if (radialRepeating) {
                        return Weva_RadialGradientRepeating(uv, brushParams.yz, radius,
                                                               startColor, stop1, stop2, stop3,
                                                               positions, stopCount, colorSpace);
                    }
                    return Weva_RadialGradientMultiStop(uv, brushParams.yz, radius,
                                                           startColor, stop1, stop2, stop3,
                                                           positions, stopCount, colorSpace);
                }
#endif
#if defined(_BRUSH_CONIC)
                if (brushIndex == 3) {
                    float4 borderStyles = ReadInstance(id, 9);
                    int colorSpace = borderStyles.x < 0.0 ? 1 : 0;
                    if (colorSpace == 1 && frac(abs(borderStyles.x)) > 0.1) colorSpace = 2;
                    int stopCount = (int)(abs(borderStyles.x) + 0.5);
                    // G13c: BorderStyles.w == 1 marks a repeating-conic-gradient.
                    // 2-stop repeating conic with default positions (0,1) has
                    // period == 1, which is the same sweep as the non-repeating
                    // 2-stop sampler — the fast path stays valid. 2-stop
                    // repeating with non-default positions is PROMOTED to a
                    // 3-stop encoding by UIBatcher.EmitGradient, so by the time
                    // stopCount > 2 we have valid stop positions in slot 8 and
                    // can dispatch to the wrap sampler safely.
                    bool conicRepeating = borderStyles.w > 0.5;
                    if (stopCount <= 2) {
                        return Weva_ConicGradient(uv, brushParams.yz, brushParams.w, startColor, stop1, colorSpace);
                    }
                    float4 stop2 = ReadInstance(id, 6);
                    float4 stop3 = ReadInstance(id, 7);
                    float4 positions = ReadInstance(id, 8);
                    if (conicRepeating) {
                        return Weva_ConicGradientRepeating(uv, brushParams.yz, brushParams.w,
                                                              startColor, stop1, stop2, stop3,
                                                              positions, stopCount, colorSpace);
                    }
                    if (stopCount <= 4) {
                        return Weva_ConicGradientMultiStop(uv, brushParams.yz, brushParams.w,
                                                             startColor, stop1, stop2, stop3,
                                                             positions, stopCount, colorSpace);
                    }
                    // 5/6-stop conic: read the two extra colors and the
                    // extra positions piggybacked on BorderStyles.y/.z.
                    float4 stop4 = ReadInstance(id, 14);
                    float4 stop5 = ReadInstance(id, 15);
                    return Weva_ConicGradientMultiStop6(uv, brushParams.yz, brushParams.w,
                                                          startColor, stop1, stop2, stop3,
                                                          stop4, stop5,
                                                          positions, borderStyles.y, borderStyles.z,
                                                          stopCount, colorSpace);
                }
#endif
                // Fallback when the matching keyword isn't enabled: degrade to the start color.
                return startColor;
            }

            float4 Weva_MaskStops(float t, float4 c0, float4 c1, float4 c2, float4 c3, float4 p, int count) {
                t = saturate(t);
                if (count <= 1) return c0;
                if (count == 2) {
                    float span = max(p.y - p.x, 1e-6);
                    return lerp(c0, c1, saturate((t - p.x) / span));
                }
                if (t <= p.y) {
                    float span01 = max(p.y - p.x, 1e-6);
                    return lerp(c0, c1, saturate((t - p.x) / span01));
                }
                if (count == 3 || t <= p.z) {
                    float span12 = max(p.z - p.y, 1e-6);
                    return lerp(c1, c2, saturate((t - p.y) / span12));
                }
                float span23 = max(p.w - p.z, 1e-6);
                return lerp(c2, c3, saturate((t - p.z) / span23));
            }

            // Packed mask metadata. Source value is always non-negative
            // (UIBatcher writes via integer-to-float with no negative branch),
            // so we expose a uint helper for the bitfield extractions. Using
            // unsigned divides/modulus instead of signed clears FXC's
            // "integer divides may be much slower, try using uints if
            // possible" warning at the call sites — d3d11 hardware lacks
            // signed integer division.
            uint Weva_MaskPackedU(float4 maskParams0) {
                return (uint)(maskParams0.y + 0.5);
            }
            int Weva_MaskPacked(float4 maskParams0) {
                return (int)Weva_MaskPackedU(maskParams0);
            }

            int Weva_MaskLayerCount(uint id) {
                return (int)clamp(Weva_MaskPackedU(ReadInstance(id, 21)) / 16u, 0u, 4u);
            }

            int Weva_MaskComposite(uint id, int baseSlot) {
                return (int)((Weva_MaskPackedU(ReadInstance(id, baseSlot)) / 4u) % 4u);
            }

            float Weva_CompositeMaskAlpha(float src, float dst, int op) {
                if (op == 1) return src * (1.0 - dst);
                if (op == 2) return src * dst;
                if (op == 3) return src * (1.0 - dst) + dst * (1.0 - src);
                return src + dst * (1.0 - src);
            }

            float Weva_MaskLayerAlpha(uint id, int baseSlot, float2 worldPos) {
                float4 maskParams0 = ReadInstance(id, baseSlot + 0);
                int kind = (int)(maskParams0.x + 0.5);
                if (kind == 0) return 0.0;

                float4 bounds = ReadInstance(id, baseSlot + 1);
                if (worldPos.x < bounds.x || worldPos.y < bounds.y
                    || worldPos.x >= bounds.x + bounds.z || worldPos.y >= bounds.y + bounds.w) {
                    return 0.0;
                }

                float4 tile = ReadInstance(id, baseSlot + 2);
                float2 tileSize = max(tile.zw, float2(1e-5, 1e-5));
                float2 p = worldPos - bounds.xy - tile.xy;
                // params0.z packs repeatX + repeatY*4 (each a BackgroundRepeatMode,
                // 0..3); params0.w now carries the gradient stop count (see UIBatcher).
                // uint math: D3D warns that int modulus/divide is slower.
                uint packedRepeat = (uint)(maskParams0.z + 0.5);
                int repeatX = (int)(packedRepeat % 4u);
                int repeatY = (int)((packedRepeat / 4u) % 4u);
                if (repeatX == 1) {
                    if (p.x < 0.0 || p.x >= tileSize.x) return 0.0;
                } else {
                    p.x = p.x - floor(p.x / tileSize.x) * tileSize.x;
                }
                if (repeatY == 1) {
                    if (p.y < 0.0 || p.y >= tileSize.y) return 0.0;
                } else {
                    p.y = p.y - floor(p.y / tileSize.y) * tileSize.y;
                }
                float2 uv = p / tileSize;

                float4 col = float4(1, 1, 1, 1);
                if (kind == 1) {
                    col = ReadInstance(id, baseSlot + 4);
                } else if (kind == 5) {
                    // B16 — image mask layer: sample _WevaMaskImage at the tile UV.
                    // The coverage image is white RGB + alpha=coverage (0=outside,
                    // 1=inside, intermediate at AA edges). We sample bilinearly and
                    // use the alpha channel as the mask value. The stored color at
                    // baseSlot+4 is (1,1,1,1) — we multiply by it for futureproofing
                    // but it's always white for the path coverage case.
                    float4 storedCol = ReadInstance(id, baseSlot + 4);
                    float4 texel = SAMPLE_TEXTURE2D(_WevaMaskImage, sampler_WevaMaskImage, uv);
                    col = storedCol * texel;
                } else {
                    float4 mp1 = ReadInstance(id, baseSlot + 3);
                    float4 c0 = ReadInstance(id, baseSlot + 4);
                    float4 c1 = ReadInstance(id, baseSlot + 5);
                    float4 c2 = ReadInstance(id, baseSlot + 6);
                    float4 c3 = ReadInstance(id, baseSlot + 7);
                    float4 positions = ReadInstance(id, baseSlot + 8);
                    // Stop count comes from params0.w for all gradient kinds. (It
                    // used to be read from mp1.z/mp1.w, but a radial fills all four
                    // mp1 slots with cx/cy/rx/ry, so mp1.z held radiusX → count==1 →
                    // single solid stop → the mask revealed everything, no dot.)
                    int count = (int)(maskParams0.w + 0.5);
                    count = clamp(count, 1, 4);
                    float t = 0.0;
                    if (kind == 2) {
                        float2 dir = mp1.xy;
                        float len = max(abs(dir.x) + abs(dir.y), 1e-6);
                        t = (dot(uv - 0.5, dir) + 0.5 * len) / len;
                        if (mp1.w > 0.5) t = frac(t);
                    } else if (kind == 3) {
                        float2 radius = max(mp1.zw, float2(1e-6, 1e-6));
                        float2 n = (uv - mp1.xy) / radius;
                        t = length(n);
                    } else if (kind == 4) {
                        float2 d = uv - mp1.xy;
                        float angle = atan2(d.x, -d.y) * 57.29577951308232 - mp1.z;
                        t = frac(angle / 360.0);
                    }
                    col = Weva_MaskStops(t, c0, c1, c2, c3, positions, count);
                }

                int mode = (int)(Weva_MaskPackedU(maskParams0) % 4u);
                float alpha = col.a;
                if (mode == 2) {
                    alpha *= dot(col.rgb, float3(0.2126, 0.7152, 0.0722));
                }
                return saturate(alpha);
            }

            float Weva_MaskAlpha(uint id, float2 worldPos) {
                int layerCount = Weva_MaskLayerCount(id);
                if (layerCount <= 0) return 1.0;
                int bottomSlot = 21 + (layerCount - 1) * 9;
                float alpha = Weva_MaskLayerAlpha(id, bottomSlot, worldPos);
                [loop]
                for (int layer = layerCount - 2; layer >= 0; layer--) {
                    int slot = 21 + layer * 9;
                    float src = Weva_MaskLayerAlpha(id, slot, worldPos);
                    alpha = Weva_CompositeMaskAlpha(src, alpha, Weva_MaskComposite(id, slot));
                }
                return saturate(alpha);
            }

            float4 Weva_ApplyMask(uint id, float2 worldPos, float4 premulColor) {
                return premulColor * Weva_MaskAlpha(id, worldPos);
            }

            // Final compositing step. Runs the active mask, then the blend
            // dispatcher. Two distinct paths are supported (CSS Compositing 1):
            //
            //   Page-backdrop path (§6, mix-blend-mode, row0.w ≈ 0):
            //     Samples _WevaBackdrop at the fragment's world position and
            //     applies the named blend formula against the page colour.
            //     anyMixBlendModeInFrame must be true for the backdrop copy
            //     to have been issued this frame.
            //
            //   Element-local path (§9, background-blend-mode, row0.w ≈ 1):
            //     Blends the fragment against the element's own background-color
            //     baked into TransformRow1.zw / TransformRow2.zw. Never samples
            //     _WevaBackdrop. The compositing formula per CSS Compositing 1 §9:
            //       Cs' = (1 − αb)·Cs + αb·B(Cb, Cs)
            //     where αb = base color alpha, Cb = base colour (sRGB unpremul),
            //     Cs = source fragment (sRGB unpremul). Blending is done in sRGB
            //     to match Chrome's behaviour for background-blend-mode.
            //
            // The blend ordinal lives in TransformRow0.z (slot 10, shared by both
            // paths). The element-local flag is in TransformRow0.w. The .a
            // contribution is unchanged by blending — per CSS Compositing 1 §10
            // the source-over alpha is applied independently of the RGB formula.
            // Must appear AFTER Weva_BlendFormula and Weva_ApplyMixBlendMode in
            // the shader source — HLSL has no forward declarations.
            float4 Weva_FinishFragment(uint id, float2 worldPos, float4 colPremul) {
                float4 masked = Weva_ApplyMask(id, worldPos, colPremul);
                float4 row0 = ReadInstance(id, 10);
                int mode = (int)(row0.z + 0.5);
                if (mode > 0) {
                    if (row0.w > 0.5) {
                        // Element-local background-blend-mode path (CSS §9).
                        // Base color is baked into spare channels of rows 11/12.
                        float4 r1 = ReadInstance(id, 11);
                        float4 r2 = ReadInstance(id, 12);
                        // baseCol is linear, unpremultiplied (matching UIBatcher
                        // LinearColor fill convention — project renders in Linear).
                        float4 baseCol = float4(r1.z, r1.w, r2.z, r2.w);
                        // Blend formula is defined in sRGB to match Chrome.
                        // Convert source fragment from linear-premul to sRGB-straight.
                        float a = max(masked.a, 1e-6);
                        float3 cs = Weva_LinearToSrgb(saturate(masked.rgb / a));
                        // Base color from CPU is linear-straight; convert to sRGB.
                        float3 cb = Weva_LinearToSrgb(saturate(baseCol.rgb));
                        // CSS Compositing 1 §9: Cs' = (1−αb)·Cs + αb·B(Cb,Cs).
                        float3 B = Weva_BlendFormula(mode, cb, cs);
                        float3 csPrime = lerp(cs, B, baseCol.a);
                        // Convert back to linear-premul for the hardware blend.
                        masked.rgb = Weva_SrgbToLinear(csPrime) * masked.a;
                    } else if (mode == 17) {
                        // ExactSrgbSourceOver (internal mode 17) is OBSOLETE under
                        // gamma compositing: the fixed-function One/OneMinusSrcAlpha
                        // blend in the sRGB target already performs exact sRGB
                        // source-over, so glass fills the converter still wraps in
                        // this internal mode render as ordinary premultiplied fills
                        // — fall through to Weva_EncodeForTarget(masked) below. (The
                        // converter-side emission is removed separately.)
                    } else {
                        // Page-backdrop mix-blend-mode path (CSS §6).
                        float3 dstPremul = Weva_SampleBackdropPremul(worldPos);
                        float3 blended = Weva_ApplyMixBlendMode(mode, masked.rgb, dstPremul, masked.a);
                        masked = float4(blended, masked.a);
                    }
                }
                return Weva_EncodeForTarget(masked);
            }

            float4 frag(Varyings IN) : SV_Target {
                uint id = (uint)IN.instanceId;
                // AABB scissor in pixel space — replaces the FF-stencil clip
                // path that silently failed on Unity 6 / URP RG. ClipRect
                // (slot 13) is (xmin, ymin, xmax, ymax); fragments outside
                // are discarded. Sentinel ±1e9 rect is "no clip" — every
                // real screen coord passes the comparison.
                // ClipRect / posSize / radii / fillColor / brushParams come
                // through interpolators (vertex pre-loaded them) so we don't
                // pay 5 StructuredBuffer loads per fragment for the hot path.
                float4 clipRect = IN.clipRect;
                const float clipRejectPad = 1.5;
                if (IN.worldPos.x < clipRect.x - clipRejectPad || IN.worldPos.x > clipRect.z + clipRejectPad
                    || IN.worldPos.y < clipRect.y - clipRejectPad || IN.worldPos.y > clipRect.w + clipRejectPad) {
                    discard;
                }
                float clipCoverage = IN.clipShape0.x > 0.5
                    ? Weva_ClipPathCoverage(id, IN.clipShape0, IN.worldPos)
                    : Weva_AabbClipCoverage(clipRect, IN.worldPos);
                if (clipCoverage <= 0.001) {
                    discard;
                }
                float4 posSize = IN.posSize;
                float4 radii = IN.radii;
                // Per-instance fill color MUST be loaded from the StructuredBuffer
                // in the fragment shader, not pre-loaded via the vertex shader's
                // `nointerpolation` interpolator. The mega-mesh path emits one
                // single DrawMesh containing many quads, with per-quad TANGENT.x
                // carrying the instance index. Empirically — on Unity 6 + URP
                // RenderGraph + DX11 — `nointerpolation float4` interpolators
                // backed by per-vertex VS reads of a StructuredBuffer collapse to
                // the FIRST quad's value across the WHOLE chunk for a fragment
                // shader stage that ALSO runs SDF/text branches. Symptom in the
                // chat demo: bubble-meta paints "12:02 · " (white-ish) AND
                // "<span class=read>read</span>" (green) in the same color — the
                // green per-instance value is silently broadcast away because the
                // interpolator pulls from quad 0 only. Reading slot 2 in the
                // fragment per-instance restores per-glyph color without
                // breaking batching: each fragment now uses ITS quad's id (which
                // does come through nointerpolation correctly because instanceId
                // is read from a vertex attribute, not a StructuredBuffer).
                float4 fillColor = ReadInstance(id, 2);
                float4 brushParams = IN.brushParams;
                int brushIndex = (int)(brushParams.x + 0.5); // R18: round, see SampleBrush

                float2 halfSize = posSize.zw;
                float2 local = (IN.uv - 0.5) * (halfSize * 2.0);

#if defined(_TEXT)
                // Text path: brushParams.yzw + BorderColorTop.x carry the atlas UV rect.
                // Layout: brushParams = (brushIndex, u0, v0, u1); BorderColorTop.x = v1.
                if (brushIndex == 7) {
                    float4 uvSlot = ReadInstance(id, 5);
                    float2 uvMin = float2(brushParams.y, brushParams.z);
                    float2 uvMax = float2(brushParams.w, uvSlot.x);
                    // BorderColorTop.y carries a packed (slot, isColor) tuple:
                    //   value 0 = slot 0, mono SDF
                    //   value 1 = slot 1, mono SDF
                    //   bits 0..1 = atlas slot 0..3
                    //   bit 2     = color bitmap
                    //   bit 3     = tint color atlas with fill color
                    //   bit 4     = direct alpha coverage atlas
                    // Encoded by UIBatcher when emitting glyph quads.
                    // Per-instance dispatch lets a single draw mix mono SDF
                    // text and color-bitmap emoji on the same batch — without
                    // this the renderer flushed every time consecutive glyphs
                    // came from different-typed atlases (match3: ~11 such
                    // boundaries, collapsed to one batch after this change).
                    int slotEnc = (int)(uvSlot.y + 0.5);
                    int atlasSlot = slotEnc & 3;
                    bool isColorText = (slotEnc & 4) != 0;
                    bool isCoverageText = (slotEnc & 16) != 0;
                    // Bit 2: tint color emoji with CSS `color` rather than
                    // preserving the texel's RGB. Set per-instance by
                    // UIBatcher for text-default emoji codepoints (↩ ⏸
                    // etc.) so they render monochrome and match Chrome's
                    // text presentation. Color emojis without this bit
                    // (🔨 🔄 🐱) keep their full color.
                    bool tintFillColor = (slotEnc & 8) != 0;
                    // BorderColorTop.z carries the CSS Text Decoration §6
                    // text-shadow blur-radius in pixels. Non-zero only on the
                    // shadow phantom DrawTextCommand emitted by EmitTextRun.
                    // We pass it to the SDF sampler which widens its AA band
                    // proportionally (Path A — SDF dilation). Zero = crisp.
                    float blurPx = uvSlot.z;
                    // BorderColorTop.w carries the faux-bold SDF-threshold
                    // shift. Zero = regular (the smoothstep midpoint stays at
                    // 0.5, output identical to pre-faux-bold builds);
                    // positive values move the midpoint inward by `bias`
                    // units, widening the glyph silhouette by roughly
                    // `bias / fwidth(d)` screen pixels. Mapped from CSS
                    // font-weight in SdfGlyphAtlasAdapter.ComputeWeightBias.
                    float weightBias = uvSlot.w;
                    // Degenerate UV (uvMin == uvMax) is the decoration-rect signal:
                    // emit the flat fill color without sampling the atlas.
                    if (uvMax.x <= uvMin.x || uvMax.y <= uvMin.y) {
                        return Weva_FinishFragment(id, IN.worldPos, fillColor * clipCoverage);
                    }
                    // Atlas UVs use Unity's texture convention (v=0 at bottom)
                    // while our quad's IN.uv has y growing downward in CSS pixel
                    // space (top vertex has uv=(0,0)). Flipping V here aligns
                    // the top of the quad with uvMax.y (atlas-top of the glyph)
                    // so glyphs render right-side-up.
                    float2 atlasUv = float2(
                        lerp(uvMin.x, uvMax.x, IN.uv.x),
                        lerp(uvMax.y, uvMin.y, IN.uv.y));
                    if (isColorText) {
                        // Color-bitmap atlas path (e.g. Segoe UI Emoji COLOR
                        // bake, RGBA32). The atlas already encodes the glyph's
                        // colors; we read the RGBA texel directly, modulate by
                        // the per-instance fill alpha (so opacity / fades still
                        // apply), and ignore the SDF coverage smoothstep that
                        // would otherwise produce a monochrome silhouette.
                        float4 texel = atlasSlot == 3
                            ? Weva_SampleColorText3(atlasUv)
                            : atlasSlot == 2
                                ? Weva_SampleColorText2(atlasUv)
                                : atlasSlot == 1
                                    ? Weva_SampleColorText1(atlasUv)
                                    : Weva_SampleColorText(atlasUv);
                        float4 colored;
                        if (tintFillColor) {
                            // Text-default emoji path: ignore the texel's
                            // RGB (which carries the platform-emoji colors)
                            // and use the CSS fill color, with texel.a as
                            // a coverage mask. Matches how Chrome renders
                            // ↩ ⏸ ⚠ ❤ etc. as text-colored monochrome.
                            colored = float4(fillColor.rgb * texel.a, fillColor.a * texel.a);
                        } else {
                            colored = float4(texel.rgb, texel.a) * fillColor.a;
                        }
                        return Weva_FinishFragment(id, IN.worldPos, colored * clipCoverage);
                    }
                    if (isCoverageText) {
                        float coverageA = atlasSlot == 3
                            ? Weva_SampleCoverageText3(atlasUv)
                            : atlasSlot == 2
                                ? Weva_SampleCoverageText2(atlasUv)
                                : atlasSlot == 1
                                    ? Weva_SampleCoverageText1(atlasUv)
                                    : Weva_SampleCoverageText(atlasUv);
                        coverageA = Weva_ApplyCoverageTextBias(coverageA, weightBias);
                        return Weva_FinishFragment(id, IN.worldPos,
                            float4(fillColor.rgb * coverageA, fillColor.a * coverageA) * clipCoverage);
                    }
                    float coverageT = atlasSlot == 3
                        ? Weva_SampleSdfText3(atlasUv, uvMin, uvMax, blurPx, weightBias)
                        : atlasSlot == 2
                            ? Weva_SampleSdfText2(atlasUv, uvMin, uvMax, blurPx, weightBias)
                            : atlasSlot == 1
                                ? Weva_SampleSdfText1(atlasUv, uvMin, uvMax, blurPx, weightBias)
                                : Weva_SampleSdfText(atlasUv, uvMin, uvMax, blurPx, weightBias);
                    coverageT *= Weva_TextShadowColorGain(blurPx, fillColor);
                    return Weva_FinishFragment(id, IN.worldPos,
                        float4(fillColor.rgb * coverageT, fillColor.a * coverageT) * clipCoverage);
                }
#endif

                // Shadow brushes use an erf-Gaussian approximation, no SDF coverage.
                if (brushIndex == 5 || brushIndex == 6) {
                    float blur = brushParams.y;
                    float spread = brushParams.z;
                    float2 innerHalf;
                    if (brushIndex == 6) {
                        // Inset (CSS Backgrounds 3 §7.2): quad == element bounds,
                        // shadow fills element minus an inner lit rect inset by
                        // `spread`, with a blur-wide gaussian falloff. Lit-rect
                        // half-size = halfSize - spread; gaussian envelope sits
                        // `blur` further inside, so the erf box half = halfSize
                        // - spread - blur (negative spread expands the lit rect).
                        innerHalf = halfSize - float2(blur + spread, blur + spread);
                    } else {
                        // Outset: C# expands the quad only to give the gaussian
                        // enough fade room. The shadow silhouette itself remains
                        // elementHalf + spread, so undo the same render padding
                        // here before feeding the shape into the blur function.
                        // If this subtracts only `blur`, the 1.5x-blur quad pad
                        // inflates small circular shadows into fat rounded rects.
                        float quadPad = blur * 1.5 + abs(spread);
                        innerHalf = halfSize - float2(quadPad - spread, quadPad - spread);
                    }
                    innerHalf = max(innerHalf, float2(0.0, 0.0));
                    float2 shadowLocal = local;
                    if (brushIndex == 6) {
                        shadowLocal -= ReadInstance(id, 4).xy;
                    }
                    float a;
                    // For zero-blur outset shadows (the `0 0 0 Npx color`
                    // hard-ring pattern, e.g. match3's `.tile.selected {
                    // box-shadow: 0 0 0 3px gold }`), the erf Gaussian
                    // collapses to a sharp rectangle and corners render
                    // square instead of following border-radius. Route
                    // crisp outset shadows through the rounded-box SDF
                    // (same as the regular fill path) so the ring honors
                    // per-corner radii. The shadow shape is the element
                    // expanded by `spread`, so the radii also expand by
                    // `spread` — clamped to the shape's half-size so a
                    // small spread doesn't bloat a tile's corner curve.
                    // Shape radii follow the element's border-radius, scaled
                    // by `spread` for outset (the silhouette grows by spread
                    // so corners do too) or shrunk for inset. Clamped to the
                    // shape's half-size so a small spread doesn't bloat a
                    // tile's corner curve.
                    // Per-corner vertical radii (elliptical border-radius), with
                    // the same zero-fallback-to-horizontal as the fill path so a
                    // box-shadow follows the element's true corner curve instead
                    // of a circular approximation. Without this an inset shadow on
                    // an element with `border-radius: 70px / 30px` traces a circle
                    // at the corner while the fill follows the ellipse — visible as
                    // the shadow/highlight "not following the shape" at the most
                    // asymmetric corners (load-game's `.thumb`).
                    float4 shadowRadiiYsrc = float4(
                        IN.radiiY.x > 0.0 ? IN.radiiY.x : radii.x,
                        IN.radiiY.y > 0.0 ? IN.radiiY.y : radii.y,
                        IN.radiiY.z > 0.0 ? IN.radiiY.z : radii.z,
                        IN.radiiY.w > 0.0 ? IN.radiiY.w : radii.w);
                    float radiusBias = brushIndex == 6 ? -spread : spread;
                    // Clamp each axis to its own half-extent so a small spread
                    // doesn't bloat the corner.
                    float4 shadowRadiiX = max(min(radii + radiusBias, innerHalf.x), 0.0);
                    float4 shadowRadiiY = max(min(shadowRadiiYsrc + radiusBias, innerHalf.y), 0.0);
                    if (brushIndex == 5 && blur < 0.5) {
                        // Zero-blur outset (e.g. `box-shadow: 0 0 0 3px gold`):
                        // collapse to a crisp rounded SDF, same as fills.
                        float sd = Weva_RoundedBoxSdfPerAxis(shadowLocal, innerHalf, shadowRadiiX, shadowRadiiY);
                        a = Weva_Coverage(sd);
                    } else {
                        // Blurred outset / inset: feed the rounded-box SDF
                        // through the erf gaussian so corners blur with the
                        // rest of the rect. The previous rectangular path
                        // left square corners on every blurred shadow, even
                        // ones cast by rounded tiles.
                        a = Weva_BoxShadowAlphaRoundedPerAxis(shadowLocal, innerHalf, shadowRadiiX, shadowRadiiY, blur);
                    }
                    if (brushIndex == 5) {
                        // CSS Backgrounds §6.1.2: an outer box-shadow is
                        // outside the border box only. The shadow is painted
                        // below the element background, but transparent
                        // backgrounds still must not reveal the shadow through
                        // the element interior. The quad is centered on the
                        // shifted shadow, so move back into the unshifted
                        // element's local space before clipping out the
                        // original border box.
                        float2 shadowOffset = ReadInstance(id, 4).xy;
                        float2 elementHalf = max(innerHalf - float2(spread, spread), float2(0.0, 0.0));
                        float clipRadiusMax = min(elementHalf.x, elementHalf.y);
                        float4 elementRadiiX = min(radii, float4(clipRadiusMax, clipRadiusMax, clipRadiusMax, clipRadiusMax));
                        float4 elementRadiiY = min(shadowRadiiYsrc, float4(clipRadiusMax, clipRadiusMax, clipRadiusMax, clipRadiusMax));
                        float insideElement = Weva_Coverage(Weva_RoundedBoxSdfPerAxis(local + shadowOffset, elementHalf, elementRadiiX, elementRadiiY));
                        a *= 1.0 - insideElement;
                    }
                    if (brushIndex == 6) {
                        // Clip the inset shadow to the element's true (elliptical)
                        // border-radius, not a circular approximation.
                        float outer = Weva_Coverage(Weva_RoundedBoxSdfPerAxis(local, halfSize, radii, shadowRadiiYsrc));
                        a = (1.0 - a) * outer;
                    }
                    return Weva_FinishFragment(id, IN.worldPos, fillColor * a * clipCoverage);
                }

                // Elliptical corners: pair the horizontal radii with the
                // per-corner vertical radii. A zero vertical component falls
                // back to the horizontal one so circular corners (rx == ry)
                // and legacy quads (RadiiY unset) hit the per-axis SDF's exact
                // circular branch — bit-identical to Weva_RoundedBoxSdf.
                float4 radiiY = float4(
                    IN.radiiY.x > 0.0 ? IN.radiiY.x : radii.x,
                    IN.radiiY.y > 0.0 ? IN.radiiY.y : radii.y,
                    IN.radiiY.z > 0.0 ? IN.radiiY.z : radii.z,
                    IN.radiiY.w > 0.0 ? IN.radiiY.w : radii.w);
                float d = Weva_RoundedBoxSdfPerAxis(local, halfSize, radii, radiiY);
                float coverage = Weva_Coverage(d);

                float4 col = SampleBrush(id, brushParams, IN.uv, fillColor, halfSize * 2.0);
                col *= coverage;

#if defined(_BORDERED)
                // Bordered: layer per-edge color where the fragment is within
                // `width` of the matching edge.
                //
                // Edge picking is axis-aligned (closest of top/right/bottom/left
                // by distance). Coverage uses the OUTER SDF plus an inner SDF
                // cutoff, not axis-aligned distRight/distTop, so the border
                // ring follows the rounded corner curve. The previous strict
                // axis-aligned `distSide < wSide` check produced visible holes
                // at the rounded corners — at e.g. local≈(24,-23) on a 50×50
                // slot with radius 4 and width 1, distRight≈1 and distTop≈2
                // (both ≥ width 1), so the if-chain emitted nothing and the
                // pixel rendered as transparent body fill (slot has
                // background:transparent).
                float4 borderWidths = ReadInstance(id, 4);     // top, right, bottom, left
                float4 colTop = ReadInstance(id, 5);
                float4 colRight = ReadInstance(id, 6);
                float4 colBot = ReadInstance(id, 7);
                float4 colLeft = ReadInstance(id, 8);
                float4 styles = ReadInstance(id, 9);

                float2 absLocal = abs(local);
                float distTop = halfSize.y - absLocal.y;
                float distBot = halfSize.y - absLocal.y;
                float distLeft = halfSize.x - absLocal.x;
                float distRight = halfSize.x - absLocal.x;
                bool isTop = local.y < 0.0 && distTop <= distLeft && distTop <= distRight;
                bool isBot = local.y >= 0.0 && distBot <= distLeft && distBot <= distRight;
                bool isLeft = !isTop && !isBot && local.x < 0.0;
                bool isRight = !isTop && !isBot && local.x >= 0.0;

                float wTop = borderWidths.x;
                float wRight = borderWidths.y;
                float wBot = borderWidths.z;
                float wLeft = borderWidths.w;

                float4 edgeCol = float4(0,0,0,0);
                int edgeStyle = 0;
                float along = 0.0;
                float thickness = 0.0;
                float pickedWidth = 0.0;

                if (isTop) {
                    edgeCol = colTop; edgeStyle = (int)styles.x; along = local.x; thickness = wTop; pickedWidth = wTop;
                } else if (isBot) {
                    edgeCol = colBot; edgeStyle = (int)styles.z; along = local.x; thickness = wBot; pickedWidth = wBot;
                } else if (isLeft) {
                    edgeCol = colLeft; edgeStyle = (int)styles.w; along = local.y; thickness = wLeft; pickedWidth = wLeft;
                } else if (isRight) {
                    edgeCol = colRight; edgeStyle = (int)styles.y; along = local.y; thickness = wRight; pickedWidth = wRight;
                }

                // Slots 4 (widths) / 9 (styles) / 5-7 (edge colors) are multiplexed: for a
                // gradient or image fill (brushIndex != 0) they carry gradient tile/stop/position
                // data, NOT border data. Painting an "edge" from them drew a phantom border in a
                // gradient stop colour — the conic/linear 5/6-stop "wedge" (its colour is always
                // c3 = BorderColorBottom = slot 7). Only a solid fill (brushIndex 0) packs real
                // border data into these slots, so only it may paint a border here; a real border
                // on a gradient element is emitted as its own solid quad.
                if (brushIndex == 0 && edgeStyle != 0 && pickedWidth > 0.0) {
                    float pat = Weva_BorderEdgePattern(edgeStyle, along + halfSize.x, thickness);
                    float innerCutoff = 1.0 - Weva_Coverage(d + pickedWidth);
                    float borderCoverage = coverage * innerCutoff;
                    // Source-over compositing of the border onto the body.
                    // The previous `lerp(col, edgeCol * coverage, edgeCol.a * pat)`
                    // multiplied the border RGB by edgeCol.a TWICE (once via
                    // the lerp's t-factor, once via `edgeCol * t`), so a
                    // border like rgba(255,255,255,0.10) on a transparent
                    // body produced a 0.10*0.10 = 0.01 alpha and rendered
                    // as ~0% opacity — invisible. Compose normally instead:
                    //   src.a  = edgeCol.a * pat * borderCoverage
                    //   out.rgb = src.rgb * src.a + dst.rgb * (1 - src.a)
                    //   out.a   = src.a         + dst.a   * (1 - src.a)
                    float bandAlpha = edgeCol.a * pat * borderCoverage;
                    col.rgb = edgeCol.rgb * bandAlpha + col.rgb * (1.0 - bandAlpha);
                    col.a   = bandAlpha             + col.a   * (1.0 - bandAlpha);
                }
#endif
                return Weva_FinishFragment(id, IN.worldPos, col * clipCoverage);
            }
            ENDHLSL
        }
    }
}
