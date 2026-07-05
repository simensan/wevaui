// Shared SDF math + brush sampling helpers for Weva's batched quad shader.
//
// Coordinate convention: 2D pixel space, top-left origin. The vertex shader computes
// box-local coordinates (centered on the quad center) and feeds them to these helpers.

#ifndef WEVA_UI_SHADER_LIB_INCLUDED
#define WEVA_UI_SHADER_LIB_INCLUDED

// Inigo Quilez rounded-box SDF, generalized to per-corner radii. The four-radii vector
// (rTL, rTR, rBR, rBL) selects by the sign of localXY which corner this fragment falls in.
// Reference: https://iquilezles.org/articles/distfunctions2d/ (sdRoundedBox).
float Weva_RoundedBoxSdf(float2 p, float2 halfSize, float4 cornerRadii) {
    // pick radius by quadrant: cornerRadii = (TL, TR, BR, BL)
    float r;
    r = (p.x > 0.0) ? cornerRadii.y : cornerRadii.x; // top: TR/TL
    if (p.y > 0.0) r = (p.x > 0.0) ? cornerRadii.z : cornerRadii.w; // bottom: BR/BL

    float2 q = abs(p) - halfSize + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// Per-axis variant for asymmetric corner radii (e.g. `border-radius: 50px / 25px`).
// cornerRadiiX and cornerRadiiY each pack (TL, TR, BR, BL) per axis.
//
// The element is split into the four corner quadrants and the straight-edge /
// interior region. ONLY inside a corner quadrant (the fragment is within
// (rx, ry) of that corner along both axes) is the elliptical distance used;
// everywhere else the plain box SDF applies, so the straight edges are
// completely INDEPENDENT of the corner radii. This is what the circular SDF
// gets for free (its `+r … −r` cancels on the edges); the previous per-axis
// form scaled the edge distance by `min(rx,ry)/r` instead, so two corners
// with different radii (e.g. TL 14×40 vs TR 70×30) placed the shared straight
// edge at slightly different sub-pixel positions either side of the box
// centre — a visible vertical "seam" down the middle of the fill (load-game's
// `.thumb`). The elliptical branch uses the gradient-corrected ellipse SDF
// `k1·(k1−1)/k2`, which meets the box SDF exactly at each quadrant boundary,
// so the whole function is C0-continuous. For rx == ry it reduces bit-for-bit
// to the circular `length(e) − r`, and to the box SDF for a zero radius.
float Weva_RoundedBoxSdfPerAxis(float2 p, float2 halfSize, float4 cornerRadiiX, float4 cornerRadiiY) {
    float rx, ry;
    if (p.x > 0.0) {
        if (p.y > 0.0) { rx = cornerRadiiX.z; ry = cornerRadiiY.z; } // BR
        else           { rx = cornerRadiiX.y; ry = cornerRadiiY.y; } // TR
    } else {
        if (p.y > 0.0) { rx = cornerRadiiX.w; ry = cornerRadiiY.w; } // BL
        else           { rx = cornerRadiiX.x; ry = cornerRadiiY.x; } // TL
    }
    rx = clamp(rx, 0.0, halfSize.x);
    ry = clamp(ry, 0.0, halfSize.y);
    float2 ap = abs(p);
    // Offset from the corner ellipse centre (positive on both axes => inside
    // the corner quadrant).
    float2 e = ap - (halfSize - float2(rx, ry));
    if (e.x > 0.0 && e.y > 0.0 && rx > 1e-4 && ry > 1e-4) {
        float2 en = float2(e.x / rx, e.y / ry);
        float k1 = length(en);
        float2 eg = float2(e.x / (rx * rx), e.y / (ry * ry));
        float k2 = max(length(eg), 1e-6);
        // Gradient-corrected ellipse SDF; equals length(e)−r when rx==ry and
        // equals the box edge distance (e.axis − r.axis) on each quadrant edge.
        return k1 * (k1 - 1.0) / k2;
    }
    // Straight edge / interior: plain box SDF — radius-independent.
    float2 d = ap - halfSize;
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
}

// Anti-aliased coverage from a signed distance: a ~1px linear ramp around d=0.
//
// The AA width must be the true screen-space gradient magnitude of d. fwidth(d)
// = |ddx(d)| + |ddy(d)| is the L1 norm, which equals the real magnitude only
// when the gradient is axis-aligned (straight edges). On a rounded CORNER the
// SDF gradient runs diagonally, where L1 overestimates by up to sqrt(2) — so
// fwidth widened the AA band ~1.4x at corners vs straight edges, blending in
// extra background and leaving a soft gray fringe on the curve. length(ddx,ddy)
// is the Euclidean (L2) magnitude: a uniform ~1px band at every edge
// orientation. Straight edges are bit-unchanged (one-axis gradient → L1 == L2);
// only the over-soft corners tighten to match.
float Weva_Coverage(float d) {
    float2 g = float2(ddx(d), ddy(d));
    float aa = max(length(g), 1e-6);
    return saturate(0.5 - d / aa);
}

// IEC 61966-2-1 sRGB OETF (linear → gamma encode). CPU LinearColor stores
// gamma-decoded values (sRGB byte / 255 → linear via FromCssColor). The
// Unity's Linear color-space path expects shader output in linear space and
// handles the final display conversion through the render target / swapchain.
// Gamma projects do not perform that conversion, so CSS linear colors need a
// final encode before blending into the target. Pre-multiplied alpha must be
// split before encoding (RGB encode applies to non-premul color), then re-applied.
//
// We use a fast polynomial approximation of the spec OETF rather than the
// reference pow(c, 1/2.4)*1.055-0.055 + linear-segment-for-darks formula.
// At 2M+ fragments per frame the pow() and three-way per-channel branch
// each cost real ALU; the polynomial below matches the spec to <0.005
// absolute error across the full [0,1] range — visually indistinguishable
// from the reference. The `low` linear segment for c < 0.0031308 is
// folded into the polynomial because at that range the polynomial is
// already close to `c*12.92` to within the same tolerance.
//
// Reference for the polynomial fit:
//   Mikkel Gjoel, "Fast sRGB approximation" (Frostbite, 2017).
float3 Weva_LinearToSrgb(float3 c) {
    // Exact IEC 61966-2-1 sRGB transfer (inverse of Weva_SrgbToLinear). The
    // gamma-space compositing path encodes here and decodes with the exact
    // curve at the final composite, so encode/decode must be true inverses —
    // the earlier polynomial fit left a ~midtone round-trip error that read as
    // slightly-too-dark / higher-contrast stacked translucent glass layers
    // (A-SRGB-COMPOSITE). saturate first: out-of-[0,1] inputs (premul math)
    // must clamp, and pow of a negative is undefined.
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(lo, hi, step(0.0031308, c));
}

float3 Weva_SrgbToLinear(float3 c) {
    c = saturate(c);
    float3 low = c / 12.92;
    float3 high = pow((c + 0.055) / 1.055, 2.4);
    return lerp(low, high, step(0.04045, c));
}

float4 Weva_PremulLinearToPremulSrgb(float4 col) {
    if (col.a < 1e-5) return col;
    float3 unpremul = col.rgb / col.a;
    return float4(Weva_LinearToSrgb(unpremul) * col.a, col.a);
}

float4 Weva_PremulSrgbToPremulLinear(float4 col) {
    if (col.a < 1e-5) return col;
    float3 unpremul = col.rgb / col.a;
    return float4(Weva_SrgbToLinear(unpremul) * col.a, col.a);
}

// CSS Color 4 §11 Oklab matrices (Björn Ottosson, 2020). Mirror of the CPU
// helpers in `CssColor.LinearRgbToOklab` / `OklabToLinearRgb` so the GPU
// gradient lerp produces the same midpoint as `Gradient.Sample` when the
// author writes `linear-gradient(in oklab, ...)`. The two matrices are NOT
// inverses of each other component-wise — both pass through a non-linear
// cube-root / cube step, so the GPU code must replicate the CPU branch
// exactly to converge to the same value.
//
// Inputs/outputs are linear-sRGB triples (no alpha). The caller in
// Weva_GradientLerp handles premultiplied-alpha bookkeeping around the
// colour-space change because Oklab has no defined behaviour for premul.
float3 Weva_LinearToOklab(float3 c) {
    float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
    float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
    float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
    // `sign(x) * pow(abs(x), 1/3)` keeps the cube root real for negative
    // out-of-gamut values that can sneak in via premultiplied alpha math.
    float3 lms = float3(l, m, s);
    float3 lmsP = sign(lms) * pow(abs(lms) + 1e-12, float3(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0));
    float L = 0.2104542553 * lmsP.x + 0.7936177850 * lmsP.y - 0.0040720468 * lmsP.z;
    float A = 1.9779984951 * lmsP.x - 2.4285922050 * lmsP.y + 0.4505937099 * lmsP.z;
    float B = 0.0259040371 * lmsP.x + 0.7827717662 * lmsP.y - 0.8086757660 * lmsP.z;
    return float3(L, A, B);
}

float3 Weva_OklabToLinear(float3 lab) {
    float lP = lab.x + 0.3963377774 * lab.y + 0.2158037573 * lab.z;
    float mP = lab.x - 0.1055613458 * lab.y - 0.0638541728 * lab.z;
    float sP = lab.x - 0.0894841775 * lab.y - 1.2914855480 * lab.z;
    float l = lP * lP * lP;
    float m = mP * mP * mP;
    float s = sP * sP * sP;
    float r = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
    float g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
    float b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
    return float3(r, g, b);
}

// `colorSpace` channel encoding (packed in BrushParams.w / BorderStyles.x by
// UIBatcher.EncodeGradientCountAndColorSpace — G1b):
//   0 = linear-RGB  (positive packed value, integer count)
//   1 = sRGB        (negative packed value, integer count; legacy default)
//   2 = Oklab       (negative packed value with a +0.25 fractional offset)
// The 3-state encoding rides on top of the legacy sign-bit scheme so the
// existing magnitude readout (`(int)(abs(val) + 0.5)`) still recovers the
// stop count correctly for all three states. See G1b in CSS_COMPLIANCE_ISSUES.md.
float4 Weva_GradientLerp(float4 a, float4 b, float t, int colorSpace) {
    // Single tail return with a result accumulator. The earlier form used
    // three early returns, which FXC's flow analysis flagged as a possibly-
    // uninitialized inlined-function return value at every call site —
    // d3d11 player builds could read garbage at the gradient call sites.
    // See Weva_SampleSdfText for the same pattern + symptom.
    float4 result;
    if (colorSpace == 1) {
        float4 sa = Weva_PremulLinearToPremulSrgb(a);
        float4 sb = Weva_PremulLinearToPremulSrgb(b);
        result = Weva_PremulSrgbToPremulLinear(lerp(sa, sb, t));
    } else if (colorSpace == 2) {
        // Oklab: LinearRGB → unpremultiply → Oklab → lerp(L/a/b) → LinearRGB →
        // re-premultiply. Alpha lerps linearly (Oklab has no alpha channel).
        // Pure-alpha=0 endpoints skip the round-trip to dodge the divide-by-zero
        // and stay byte-identical to the linear path for transparent stops.
        float alpha = lerp(a.a, b.a, t);
        if (a.a < 1e-5 && b.a < 1e-5) {
            result = float4(0, 0, 0, alpha);
        } else {
            float3 ar = a.a < 1e-5 ? float3(0, 0, 0) : a.rgb / a.a;
            float3 br = b.a < 1e-5 ? float3(0, 0, 0) : b.rgb / b.a;
            float3 oa = Weva_LinearToOklab(ar);
            float3 ob = Weva_LinearToOklab(br);
            float3 mixedLab = lerp(oa, ob, t);
            float3 mixedLin = Weva_OklabToLinear(mixedLab);
            result = float4(mixedLin * alpha, alpha);
        }
    } else {
        result = lerp(a, b, t);
    }
    return result;
}

float4 Weva_PremulSrgbEncode(float4 col) {
#if defined(UNITY_COLORSPACE_GAMMA)
    if (col.a < 1e-5) return col;
    float3 unpremul = col.rgb / col.a;
    float3 enc = Weva_LinearToSrgb(unpremul);
    return float4(enc * col.a, col.a);
#else
    return col;
#endif
}

float _WevaRawFilterOutput;
// A-SRGB-COMPOSITE: 1 when the draw target stores sRGB-ENCODED premultiplied
// colour (gamma-space compositing) — the intermediate UI RT and the
// BatchedSurfaceRenderer sRGB=false golden/editor-panel targets. Set per-frame
// by the pass. 0 only on the degenerate pre-shader-ready frame that would draw
// straight to the linear camera (nothing visible renders then anyway).
float _WevaSrgbComposite;

// ── A-SRGB-COMPOSITE encode seam ──────────────────────────────────────
// Every CSS-premultiplied fragment passes through here before the
// fixed-function One/OneMinusSrcAlpha blend. Gamma-compositing targets get
// *true* sRGB-encoded premultiplied colour so the blend runs in sRGB space —
// Chrome-exact over ANY background. A final pass decodes the intermediate
// sRGB-premul back to linear for the camera (Linear projects); a Gamma project
// already holds gamma values. Filter-scope CONTENT renders with
// _WevaRawFilterOutput=1 (raw linear premul) so blur / colour-matrix math
// stays in linear light. The lone non-gamma fallback (linear camera, before
// the filter shader is ready) emits raw linear premul — no approximation.
float4 Weva_EncodeForTarget(float4 col) {
    if (_WevaRawFilterOutput > 0.5) return col;
    if (_WevaSrgbComposite > 0.5) return Weva_PremulLinearToPremulSrgb(col);
    return col;
}

// 2-stop linear gradient sample. Direction is pre-rotated unit vector; t is computed
// against the box-local UV (0..1).
float4 Weva_LinearGradient(float2 uv, float2 dir, float4 colorStart, float4 colorEnd, int colorSpace) {
    float t = saturate(dot(uv - 0.5, dir) + 0.5);
    return Weva_GradientLerp(colorStart, colorEnd, t, colorSpace);
}

float4 Weva_RadialGradient(float2 uv, float2 center, float2 radius, float4 colorStart, float4 colorEnd, float p0, float p1, int colorSpace) {
    float2 r = max(radius, float2(1e-6, 1e-6));
    float2 p = (uv - center) / r;
    float radial = saturate(length(p));
    // Remap [p0..p1] -> [0..1] so stops like `... 0%, transparent 50%`
    // fade out by half the farthest-corner radius instead of stretching
    // the ramp across the full radius.
    float t = saturate((radial - p0) / max(p1 - p0, 1e-4));
    return Weva_GradientLerp(colorStart, colorEnd, t, colorSpace);
}

// Back-compat overload: defaults stop positions to (0, 1) which is the
// pre-position behavior (full ramp across the entire radius).
float4 Weva_RadialGradient(float2 uv, float2 center, float2 radius, float4 colorStart, float4 colorEnd) {
    return Weva_RadialGradient(uv, center, radius, colorStart, colorEnd, 0.0, 1.0, 0);
}

// G13c: repeating-radial-gradient sampler. The normalized radius (no
// saturate) is fed through `frac()` so the ramp tiles outward past the
// gradient radius. `period` is the largest packed stop position; for the
// common 2-stop case (positions = (p0, p1, _, _)) the period is p1; for
// stopCount >= 3 it's the last valid position. The shader is dispatched
// here from Weva-Quad.shader's radial branch when BorderStyles.w > 0.5.
float4 Weva_RadialGradientRepeating(float2 uv, float2 center, float2 radius,
                                       float4 c0, float4 c1, float4 c2, float4 c3,
                                       float4 positions, int stopCount, int colorSpace) {
    float2 r = max(radius, float2(1e-6, 1e-6));
    float2 p = (uv - center) / r;
    // No saturate — the wrap below tiles the ramp past the radius.
    float radial = length(p);
    float lastPos;
    if (stopCount <= 2) lastPos = positions.y;
    else if (stopCount == 3) lastPos = positions.z;
    else lastPos = positions.w;
    float period = max(lastPos, 1e-6);
    float scaled = radial / period;
    float t = (scaled - floor(scaled)) * period;
    // Single tail return so FXC sees one initialised path. See Weva_GradientLerp.
    float4 result;
    if (stopCount <= 2) {
        float pa = positions.x;
        float pb = positions.y;
        float span = max(pb - pa, 1e-6);
        float k = saturate((t - pa) / span);
        result = Weva_GradientLerp(c0, c1, k, colorSpace);
    } else if (stopCount == 3) {
        float pa = positions.x;
        float pb = positions.y;
        float pc = positions.z;
        if (t <= pb) {
            float s = max(pb - pa, 1e-6);
            result = Weva_GradientLerp(c0, c1, saturate((t - pa) / s), colorSpace);
        } else {
            float s2 = max(pc - pb, 1e-6);
            result = Weva_GradientLerp(c1, c2, saturate((t - pb) / s2), colorSpace);
        }
    } else {
        // stopCount >= 4
        float q0 = positions.x;
        float q1 = positions.y;
        float q2 = positions.z;
        float q3 = positions.w;
        if (t <= q1) {
            float s = max(q1 - q0, 1e-6);
            result = Weva_GradientLerp(c0, c1, saturate((t - q0) / s), colorSpace);
        } else if (t <= q2) {
            float s = max(q2 - q1, 1e-6);
            result = Weva_GradientLerp(c1, c2, saturate((t - q1) / s), colorSpace);
        } else {
            float sL = max(q3 - q2, 1e-6);
            result = Weva_GradientLerp(c2, c3, saturate((t - q2) / sL), colorSpace);
        }
    }
    return result;
}

// G13c: repeating-conic-gradient sampler. The angle-mod-360 logic in the
// non-repeating walker already wraps angles into [0, 360); for the
// repeating form we additionally tile the ramp with period equal to the
// largest packed stop position so a stop set like (red 0deg, blue 90deg)
// repeats every 90deg around the circle.
float4 Weva_ConicGradientRepeating(float2 uv, float2 center, float fromAngleDeg,
                                      float4 c0, float4 c1, float4 c2, float4 c3,
                                      float4 positions, int stopCount, int colorSpace) {
    float2 pv = uv - center;
    float angle = atan2(pv.x, -pv.y) * (180.0 / 3.14159265);
    angle -= fromAngleDeg;
    angle = angle - 360.0 * floor(angle / 360.0);
    float tRaw = angle / 360.0;
    float lastPos;
    if (stopCount <= 2) lastPos = positions.y;
    else if (stopCount == 3) lastPos = positions.z;
    else lastPos = positions.w;
    float period = max(lastPos, 1e-6);
    float scaled = tRaw / period;
    float t = (scaled - floor(scaled)) * period;
    // Single tail return so FXC sees one initialised path. See Weva_GradientLerp.
    float4 result;
    if (stopCount <= 2) {
        float pa = positions.x;
        float pb = positions.y;
        float span = max(pb - pa, 1e-6);
        float k = saturate((t - pa) / span);
        result = Weva_GradientLerp(c0, c1, k, colorSpace);
    } else if (stopCount == 3) {
        float pa = positions.x;
        float pb = positions.y;
        float pc = positions.z;
        if (t <= pb) {
            float s = max(pb - pa, 1e-6);
            result = Weva_GradientLerp(c0, c1, saturate((t - pa) / s), colorSpace);
        } else {
            float s2 = max(pc - pb, 1e-6);
            result = Weva_GradientLerp(c1, c2, saturate((t - pb) / s2), colorSpace);
        }
    } else {
        // stopCount >= 4
        float q0 = positions.x;
        float q1 = positions.y;
        float q2 = positions.z;
        float q3 = positions.w;
        if (t <= q1) {
            float s = max(q1 - q0, 1e-6);
            result = Weva_GradientLerp(c0, c1, saturate((t - q0) / s), colorSpace);
        } else if (t <= q2) {
            float s = max(q2 - q1, 1e-6);
            result = Weva_GradientLerp(c1, c2, saturate((t - q1) / s), colorSpace);
        } else {
            float sL = max(q3 - q2, 1e-6);
            result = Weva_GradientLerp(c2, c3, saturate((t - q2) / sL), colorSpace);
        }
    }
    return result;
}

// ─── Multi-stop walker (up to 4 stops) ──────────────────────────────────────
// Slot map (matches UIBatcher.EmitGradient):
//   c0..c3      = stop colors (for stopCount=2 only c0/c3 are valid; for
//                 stopCount=3 c0/c1/c2 are valid; for 4 all four are valid).
//   positions   = (s0, s1, s2, s3) in [0,1]; s0 always 0, s_last always 1.
//   stopCount   = 2, 3, or 4.
//
// stopCount<=2 collapses to the cheap 2-color lerp so the common path stays
// cheap (one ALU branch, fold-friendly). 3/4 walk the segment containing t
// and lerp between the two segment colors.
//
float4 Weva_GradientWalk(float t, float4 c0, float4 c1, float4 c2, float4 c3,
                            float4 positions, int stopCount, int colorSpace) {
    // Single tail return so FXC sees one initialised path. See Weva_GradientLerp.
    float4 result;
    if (stopCount <= 2) {
        result = Weva_GradientLerp(c0, c3, t, colorSpace);
    } else {
        float pStart = positions.x;
        if (t <= pStart) {
            result = c0;
        } else if (stopCount == 3) {
            float p1 = positions.y;
            float p2 = positions.z;
            if (t >= p2) {
                result = c2;
            } else if (t <= p1) {
                float span = max(p1 - pStart, 1e-6);
                result = Weva_GradientLerp(c0, c1, saturate((t - pStart) / span), colorSpace);
            } else {
                float span2 = max(p2 - p1, 1e-6);
                result = Weva_GradientLerp(c1, c2, saturate((t - p1) / span2), colorSpace);
            }
        } else {
            // stopCount == 4
            float q1 = positions.y;
            float q2 = positions.z;
            float q3 = positions.w;
            if (t >= q3) {
                result = c3;
            } else if (t <= q1) {
                float span = max(q1 - pStart, 1e-6);
                result = Weva_GradientLerp(c0, c1, saturate((t - pStart) / span), colorSpace);
            } else if (t <= q2) {
                float span = max(q2 - q1, 1e-6);
                result = Weva_GradientLerp(c1, c2, saturate((t - q1) / span), colorSpace);
            } else {
                float spanLast = max(q3 - q2, 1e-6);
                result = Weva_GradientLerp(c2, c3, saturate((t - q2) / spanLast), colorSpace);
            }
        }
    }
    return result;
}

// Multi-stop sibling of Weva_LinearGradient. stopCount<=2 collapses to
// the same lerp the 2-stop function does.
float4 Weva_LinearGradientMultiStop(float2 uv, float2 dir,
                                       float4 c0, float4 c1, float4 c2, float4 c3,
                                       float4 positions, int stopCount, int colorSpace) {
    float t = saturate(dot(uv - 0.5, dir) + 0.5);
    return Weva_GradientWalk(t, c0, c1, c2, c3, positions, stopCount, colorSpace);
}

float4 Weva_RadialGradientMultiStop(float2 uv, float2 center, float2 radius,
                                       float4 c0, float4 c1, float4 c2, float4 c3,
                                       float4 positions, int stopCount, int colorSpace) {
    float2 r = max(radius, float2(1e-6, 1e-6));
    float2 p = (uv - center) / r;
    float radial = saturate(length(p));
    // Single tail return so FXC sees one initialised path. See Weva_GradientLerp.
    float4 result;
    if (stopCount <= 2) {
        result = Weva_RadialGradient(uv, center, radius, c0, c1, positions.x, positions.y, colorSpace);
    } else {
        result = Weva_GradientWalk(radial, c0, c1, c2, c3, positions, stopCount, colorSpace);
    }
    return result;
}

// Repeating-linear-gradient sampler. The raw axis-projected t is divided by
// `period` and frac()'d so the ramp tiles along the gradient axis. Period is
// pre-baked by UIBatcher.EmitGradient as "largest stop position in [0,1]
// space" (px stops are normalized by the gradient line length there).
//
// Stop layout matches the non-repeating multi-stop path so the same slot
// readouts in Weva-Quad.shader can be reused — c1 carries the LAST color
// for stopCount==2 (the legacy 2-stop layout) and the SECOND color for 3/4
// stops. `positions` carries (p0, p1, p2, p3) where for 2-stop both p0 and
// p1 should be the first and last stop positions.
float4 Weva_LinearGradientRepeating(float2 uv, float2 dir,
                                       float4 c0, float4 c1, float4 c2, float4 c3,
                                       float4 positions, int stopCount, float period, int colorSpace) {
    float t_raw = dot(uv - 0.5, dir) + 0.5;
    float per = max(period, 1e-6);
    float t = frac(t_raw / per) * per;
    // After scaling back to [0..period], t now lives in the same numeric
    // range as the packed stop positions. Walk segments directly so the
    // stops at the edges of the period (e.g. transparent @ p_last + same
    // color @ p_first) bridge cleanly. Single tail return so FXC sees one
    // initialised path. See Weva_GradientLerp.
    float4 result;
    if (stopCount <= 2) {
        float p0 = positions.x;
        float p1 = positions.y;
        float span = max(p1 - p0, 1e-6);
        float k = saturate((t - p0) / span);
        result = Weva_GradientLerp(c0, c1, k, colorSpace);
    } else if (stopCount == 3) {
        float p0 = positions.x;
        float p1 = positions.y;
        float p2 = positions.z;
        if (t <= p1) {
            float s = max(p1 - p0, 1e-6);
            result = Weva_GradientLerp(c0, c1, saturate((t - p0) / s), colorSpace);
        } else {
            float s2 = max(p2 - p1, 1e-6);
            result = Weva_GradientLerp(c1, c2, saturate((t - p1) / s2), colorSpace);
        }
    } else {
        // stopCount >= 4
        float q0 = positions.x;
        float q1 = positions.y;
        float q2 = positions.z;
        float q3 = positions.w;
        if (t <= q1) {
            float s = max(q1 - q0, 1e-6);
            result = Weva_GradientLerp(c0, c1, saturate((t - q0) / s), colorSpace);
        } else if (t <= q2) {
            float s = max(q2 - q1, 1e-6);
            result = Weva_GradientLerp(c1, c2, saturate((t - q1) / s), colorSpace);
        } else {
            float sL = max(q3 - q2, 1e-6);
            result = Weva_GradientLerp(c2, c3, saturate((t - q2) / sL), colorSpace);
        }
    }
    return result;
}

// Multi-stop sibling of Weva_ConicGradient.
float4 Weva_ConicGradientMultiStop(float2 uv, float2 center, float fromAngleDeg,
                                      float4 c0, float4 c1, float4 c2, float4 c3,
                                      float4 positions, int stopCount, int colorSpace) {
    float2 p = uv - center;
    float angle = atan2(p.x, -p.y) * (180.0 / 3.14159265);
    angle -= fromAngleDeg;
    angle = angle - 360.0 * floor(angle / 360.0);
    float t = saturate(angle / 360.0);
    return Weva_GradientWalk(t, c0, c1, c2, c3, positions, stopCount, colorSpace);
}

// ─── 6-stop walker (conic only) ─────────────────────────────────────────────
// Extends Weva_GradientWalk for stopCount in {5, 6}. Used by the conic
// multi-stop dispatch when the source gradient packs > 4 stops. Slot map
// (matches UIBatcher.EmitGradient conic 5/6-stop branch):
//   c0..c3      = stops 0..3 (slots 2 / 5 / 6 / 7 in the SB)
//   c4          = stop 4    (slot 14)
//   c5          = stop 5    (slot 15; ignored when stopCount == 5)
//   positions   = (p0, p1, p2, p3) in [0,1] (slot 8)
//   p4, p5      = extra positions packed into BorderStyles.y/.z (slot 9)
// The segment walk is a straight extension of the 4-stop branch — same
// pattern, two more comparisons.
float4 Weva_GradientWalk6(float t,
                             float4 c0, float4 c1, float4 c2, float4 c3,
                             float4 c4, float4 c5,
                             float4 positions, float p4, float p5,
                             int stopCount, int colorSpace) {
    float pStart = positions.x;
    if (t <= pStart) return c0;
    float q1 = positions.y;
    float q2 = positions.z;
    float q3 = positions.w;
    if (stopCount == 5) {
        if (t >= p4) return c4;
        if (t <= q1) {
            float span = max(q1 - pStart, 1e-6);
            return Weva_GradientLerp(c0, c1, saturate((t - pStart) / span), colorSpace);
        }
        if (t <= q2) {
            float span = max(q2 - q1, 1e-6);
            return Weva_GradientLerp(c1, c2, saturate((t - q1) / span), colorSpace);
        }
        if (t <= q3) {
            float span = max(q3 - q2, 1e-6);
            return Weva_GradientLerp(c2, c3, saturate((t - q2) / span), colorSpace);
        }
        float span4 = max(p4 - q3, 1e-6);
        return Weva_GradientLerp(c3, c4, saturate((t - q3) / span4), colorSpace);
    }
    // stopCount == 6
    if (t >= p5) return c5;
    if (t <= q1) {
        float span = max(q1 - pStart, 1e-6);
        return Weva_GradientLerp(c0, c1, saturate((t - pStart) / span), colorSpace);
    }
    if (t <= q2) {
        float span = max(q2 - q1, 1e-6);
        return Weva_GradientLerp(c1, c2, saturate((t - q1) / span), colorSpace);
    }
    if (t <= q3) {
        float span = max(q3 - q2, 1e-6);
        return Weva_GradientLerp(c2, c3, saturate((t - q2) / span), colorSpace);
    }
    if (t <= p4) {
        float span = max(p4 - q3, 1e-6);
        return Weva_GradientLerp(c3, c4, saturate((t - q3) / span), colorSpace);
    }
    float span5 = max(p5 - p4, 1e-6);
    return Weva_GradientLerp(c4, c5, saturate((t - p4) / span5), colorSpace);
}

// 5/6-stop conic. Reads two extra colors (c4, c5) and two extra positions
// (p4, p5) from the instance data; the shader dispatches here only when
// stopCount > 4 so the cheaper 2/3/4-stop walker continues to handle the
// common case without paying the extra ALU.
float4 Weva_ConicGradientMultiStop6(float2 uv, float2 center, float fromAngleDeg,
                                       float4 c0, float4 c1, float4 c2, float4 c3,
                                       float4 c4, float4 c5,
                                       float4 positions, float p4, float p5,
                                       int stopCount, int colorSpace) {
    float2 p = uv - center;
    float angle = atan2(p.x, -p.y) * (180.0 / 3.14159265);
    angle -= fromAngleDeg;
    angle = angle - 360.0 * floor(angle / 360.0);
    float t = saturate(angle / 360.0);
    return Weva_GradientWalk6(t, c0, c1, c2, c3, c4, c5, positions, p4, p5, stopCount, colorSpace);
}

// 5/6-stop linear. Mirror of Weva_ConicGradientMultiStop6 but with the
// linear axis projection (dot the box-local UV onto the pre-rotated unit
// direction) feeding the 6-stop walker. Dispatched from Weva-Quad.shader's
// linear branch when stopCount > 4. The 2/3/4-stop walker stays the entry
// point for the common case; this path adds two more comparisons and two
// more StructuredBuffer loads (slots 14 / 15) so we only pay for it when
// the user actually authored a 5- or 6-color linear ramp.
float4 Weva_LinearGradientMultiStop6(float2 uv, float2 dir,
                                        float4 c0, float4 c1, float4 c2, float4 c3,
                                        float4 c4, float4 c5,
                                        float4 positions, float p4, float p5,
                                        int stopCount, int colorSpace) {
    float t = saturate(dot(uv - 0.5, dir) + 0.5);
    return Weva_GradientWalk6(t, c0, c1, c2, c3, c4, c5, positions, p4, p5, stopCount, colorSpace);
}

float4 Weva_ConicGradient(float2 uv, float2 center, float fromAngleDeg, float4 colorStart, float4 colorEnd, int colorSpace) {
    float2 p = uv - center;
    float angle = atan2(p.x, -p.y) * (180.0 / 3.14159265);
    angle -= fromAngleDeg;
    angle = angle - 360.0 * floor(angle / 360.0);
    float t = saturate(angle / 360.0);
    return Weva_GradientLerp(colorStart, colorEnd, t, colorSpace);
}

// Drop-shadow Gaussian approximation. Uses the rational erf used by Skia/Chromium for
// axis-aligned box shadows; the per-corner radii are folded into the SDF input so corners
// blur with the rest of the rect.
float Weva_FastErf(float x) {
    float s = sign(x);
    x = abs(x);
    float t = 1.0 / (1.0 + 0.3275911 * x);
    float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-x * x);
    return s * y;
}

float Weva_BoxShadowAlpha(float2 p, float2 halfSize, float blur) {
    float sigma = max(blur * 0.5, 0.5);
    float scale = 1.0 / (sigma * 1.4142135);
    float ax = 0.5 * (Weva_FastErf((p.x + halfSize.x) * scale) - Weva_FastErf((p.x - halfSize.x) * scale));
    float ay = 0.5 * (Weva_FastErf((p.y + halfSize.y) * scale) - Weva_FastErf((p.y - halfSize.y) * scale));
    return saturate(ax * ay);
}

// Rounded-corner variant: feeds the rounded-box signed distance through the
// erf falloff so the blur follows the element's border-radius instead of
// rendering with square corners. Without this the regular rectangular
// gaussian leaves visible right-angle corners on every blurred box-shadow,
// which Chrome doesn't — Chrome's filter convolves the rounded silhouette.
//   sd <  0 (inside  shape): erf(neg) → 0.5*(1-(-1)) = 1, fully lit
//   sd =  0 (on edge):       returns 0.5
//   sd >  0 (outside shape): erf(pos) → 0.5*(1-1)    = 0, fades out
// The `sd * 1/(sigma*sqrt(2))` argument matches the rectangular path's
// normalization so a 0-radius input collapses (within ~1%) to the same
// silhouette Weva_BoxShadowAlpha produces — meaning rectangular shadows
// don't regress when we switch them to this function.
float Weva_BoxShadowAlphaRounded(float2 p, float2 halfSize, float4 radii, float blur) {
    float sigma = max(blur * 0.5, 0.5);
    float scale = 1.0 / (sigma * 1.4142135);
    float sd = Weva_RoundedBoxSdf(p, halfSize, radii);
    return saturate(0.5 * (1.0 - Weva_FastErf(sd * scale)));
}

// Per-axis (elliptical-corner) variant — same erf falloff fed by the
// per-axis rounded-box SDF so a box-shadow on an element with elliptical
// border-radius (`border-radius: 70px / 30px`) blurs along the actual
// corner curve instead of a circular approximation.
float Weva_BoxShadowAlphaRoundedPerAxis(float2 p, float2 halfSize, float4 radiiX, float4 radiiY, float blur) {
    float sigma = max(blur * 0.5, 0.5);
    float scale = 1.0 / (sigma * 1.4142135);
    float sd = Weva_RoundedBoxSdfPerAxis(p, halfSize, radiiX, radiiY);
    return saturate(0.5 * (1.0 - Weva_FastErf(sd * scale)));
}

// Edge-style coverage: dashed/dotted/double styles painted along the *closest* border edge.
// Returns the per-edge color the fragment should pick up — the caller multiplies it by the
// border SDF mask to actually stamp the edge.
//
// styleCode: 0=None, 1=Solid, 2=Dashed, 3=Dotted, 4=Double
float Weva_BorderEdgePattern(int styleCode, float along, float thickness) {
    if (styleCode == 1) return 1.0; // solid
    if (styleCode == 4) {
        // double: two stripes at 1/3 and 2/3 of the band width — outer edge bias
        return 1.0;
    }
    if (styleCode == 2) {
        // dashed: 2*thickness on, 2*thickness off
        float period = max(thickness * 4.0, 1.0);
        float phase = along - period * floor(along / period);
        return phase < period * 0.5 ? 1.0 : 0.0;
    }
    if (styleCode == 3) {
        // dotted: thickness on, thickness off
        float period = max(thickness * 2.0, 1.0);
        float phase = along - period * floor(along / period);
        return phase < period * 0.5 ? 1.0 : 0.0;
    }
    return 0.0;
}

// SDF text sampling helper. _GlyphAtlas is bound per-draw by UIRenderGraphPass
// when a _TEXT-keyword batch is issued. The atlas may be either:
//   - R8 (TextCore-rasterized atlas): SDF is in the red channel.
//   - Alpha8 (TMP-baked atlas, the common SDFAA case): SDF is in the alpha
//     channel; D3D/Vulkan present it as (0, 0, 0, A) when sampled, so reading
//     `.r` returns 0 and the glyphs disappear.
// _GlyphAtlasChannelMask is a Vector4 selector (1,0,0,0) for R8 or (0,0,0,1)
// for Alpha8/RGBA, set per-batch by C# from the bound atlas texture's format.
// We dot the sample by the mask to recover the SDF distance regardless of
// which channel actually carries the data.
TEXTURE2D(_GlyphAtlas);
SAMPLER(sampler_GlyphAtlas);
float4 _GlyphAtlasChannelMask;
// Secondary atlas slot. Lets a single text batch reference up to two
// distinct atlases (e.g. text + emoji) by encoding an atlas slot in
// per-instance data. Without this, every text→emoji→text alternation
// flushed a new batch (atlas-1 + atlas-3 = 170 batches in the demo).
TEXTURE2D(_GlyphAtlas1);
SAMPLER(sampler_GlyphAtlas1);
float4 _GlyphAtlas1ChannelMask;
TEXTURE2D(_GlyphAtlas2);
SAMPLER(sampler_GlyphAtlas2);
float4 _GlyphAtlas2ChannelMask;
TEXTURE2D(_GlyphAtlas3);
SAMPLER(sampler_GlyphAtlas3);
float4 _GlyphAtlas3ChannelMask;

TEXTURE2D(_WevaImage);
SAMPLER(sampler_WevaImage);

// B16 — path coverage mask texture. Bound per-batch when a clip-path: path()
// element's synthetic mask layer (kind=5) requires GPU texture sampling.
// Defaults to "white" (1,1,1,1) so batches without a path mask sample alpha=1
// and produce no clip effect. The alpha channel encodes coverage (0=outside,
// 1=inside, intermediate at AA edges). RGB is always white.
TEXTURE2D(_WevaMaskImage);
SAMPLER(sampler_WevaMaskImage);

// SDF sampling with 2× horizontal + 2× vertical supersampling and a
// narrowed smoothstep AA band. The default `smoothstep(0.5 - fwidth, 0.5 +
// fwidth, d)` produces a 2-texel-wide transition zone — readable but
// noticeably soft at small font sizes (11–14 px) because most pixels land
// on the band edge. Two changes here:
//
//   1. Halve the AA band (fwidth * 0.5) so edges hit black/white at the
//      pixel boundary instead of fading across two pixels. Crisper edges.
//   2. 4-tap supersample at ±0.25 px offsets in both axes. The four sub-
//      pixel coverages average into a single pixel coverage that's much
//      closer to the analytic integral than a single sample. At small
//      font sizes this kills the staircasing on diagonal strokes (the
//      tops of v/w/y/A/V/X) that sub-pixel positioning alone couldn't
//      smooth out.
//
// 4 taps + tighter band ~doubles the per-fragment ALU + texture work in
// the text path. With our two-draw demo it's a non-issue; on heavy
// glyph-count frames the cost remains bounded by the visible text area.
// 2-tap horizontal supersample with the AA band halved relative to the
// classic single-sample recipe. Two key changes vs. plain hardware-bilinear
// sampling:
//
//   1. Halve the AA band (fwidth * 0.5) so edges hit black/white at the
//      pixel boundary instead of fading across two pixels — measurable
//      crispness gain for 11–14 px text where most pixels land on the band.
//   2. 2-tap sample at ±0.25 px on the x-axis. The two sub-pixel coverages
//      average into a single pixel coverage that's much closer to the
//      analytic integral than a single sample. At small font sizes this
//      kills the staircasing on diagonal strokes (tops of v/w/y/A/V/X)
//      that sub-pixel positioning alone couldn't smooth out.
//
// The earlier 4-tap (2×2 grid) version of this function gave a slightly
// crisper image on near-vertical strokes but cost a measured ~0.1 ms more
// per frame on the demo and was visually indistinguishable from 2-tap on
// the medium-sized text the demo actually uses; staircasing was already
// subjective at that point. 2 taps + tighter band keeps the visual win
// without the bandwidth.
// Path A — text-shadow blur. `blurPx` is the CSS Text Decoration §6
// blur-radius in CSS pixels propagated from the per-instance data. The SDF
// is normalized so 0.5 ≈ glyph edge and the gradient (fwidth(d)) describes
// "screen pixels per SDF unit" at this fragment. Multiplying that gradient
// by `blurPx` converts the requested blur back into SDF units, giving the
// number we need to widen the smoothstep band by so the glyph silhouette
// feathers outward by `blurPx` screen pixels.
//
// The falloff is linear in the SDF distance (smoothstep is roughly linear
// across its band), not Gaussian. For typical CSS values (`text-shadow:
// 0 1px 4px ...`) it's visually indistinguishable from a true Gaussian blur
// at common UI font sizes; very large radii (≥ 16 px) read as a soft
// outline rather than a Gaussian falloff. A future RT-Gaussian path can
// replace this without touching the C# call sites.
//
// max(fwidth*0.5, 1e-6) is the original baseline AA band. We add `blurPx *
// fwidth(d0)` to it: `fwidth(d0)` is "how many SDF units does the sample
// jump per screen pixel," so multiplying by `blurPx` extends the smoothstep
// band to span `blurPx` more pixels on each side.
// Faux-bold SDF threshold shift. `weightBias` shifts the smoothstep midpoint
// from 0.5 to (0.5 - weightBias), which moves the coverage transition INWARD
// in SDF-distance units. Since the SDF stores distance from the glyph edge
// with 0.5 ≈ edge, a positive bias treats values < 0.5 (i.e. SLIGHTLY
// outside the original silhouette) as still covered, widening the stroke.
// At weightBias=0 the result is byte-for-byte identical to the prior
// signature — regular (400-weight) text is unaffected.
//
// The widen amount in screen pixels is approximately `weightBias /
// fwidth(d)`. For a 24 px-rendered glyph atlas with fwidth(d) ≈ 0.05 per
// pixel, weightBias = 0.075 thickens strokes by ≈1.5 px — the "subtly
// thicker than 400" target for weight 700. Negative biases would thin the
// glyph but are NOT supported here (the SdfGlyphAtlasAdapter clamps weights
// below 400 to bias 0); see the C# helper for the rationale.
// Smoothstep half-width multiplier. Was 0.5 (1-pixel AA band centered on
// the edge). 0.34 narrows the band by 32% — measurably sharper body text
// at common UI sizes without crossing into single-pixel aliasing. Pure
// taste knob; bump back toward 0.4 if rotated/animated text starts to
// crawl. Calibrated against the badge-text reference render at 12-14px
// where the prior 0.5 band read as visibly soft.
#define WEVA_TEXT_AA_HALF 0.34

// Stem-darkening contribution: a size-driven inward bias on the
// smoothstep midpoint, kicking in when the rendered glyph is small
// enough that fwidth(d) (SDF units per screen pixel) crosses ~0.05.
// Matches what FreeType / CoreText / WebKit do under the hood — at body
// sizes the eye perceives dim AA pixels as muddied strokes, so widening
// the silhouette by a fraction of a pixel restores apparent stroke
// weight. Capped at 0.05 (~ +0.6px stem thickening on a 12px glyph;
// negligible at 24+px).
#define WEVA_TEXT_STEM_DARKEN_MAX 0.05

// Flip to 0 to restore smoothstep anti-aliasing. With AA off the smoothstep
// collapses to a step() at the SDF midpoint, giving 1-bit coverage per
// supersample (so final coverage is 0, 0.5, or 1 from the 2-tap x-axis SS).
// Blur falls back to the AA path because step() can't widen.
#define WEVA_TEXT_DISABLE_AA 0

float Weva_TextBlurCoverage(float d, float mid, float fw, float blurPx) {
    float distPx = (d - mid) / max(fw, 1e-6);
    // CSS text-shadow blur is a convolution of the glyph mask, not just a
    // widened edge AA band. Large radii therefore have a broad, low-contrast
    // center instead of a second crisp glyph silhouette. Use a wider
    // edge-distance sigma so 12-20px UI glows read like browser text-shadow
    // while still staying in the batched glyph path.
    float sigma = max(blurPx * 1.5, 0.5);
    float coverage = saturate(0.5 * (1.0 + Weva_FastErf(distPx / (sigma * 1.41421356237))));
    // A true Gaussian blur spreads the glyph mask's energy over a larger
    // footprint, so the peak alpha falls as blur radius grows. The SDF path
    // has no offscreen convolution buffer, so apply a radius-based gain to
    // avoid wide text-shadows reading as dark duplicate glyphs.
    float blurGain = 1.0 / (1.0 + blurPx * 0.08);
    return coverage * blurGain;
}

float Weva_UvInsideRect(float2 uv, float2 uvMin, float2 uvMax) {
    return step(uvMin.x, uv.x) * step(uvMin.y, uv.y)
        * step(uv.x, uvMax.x) * step(uv.y, uvMax.y);
}

float Weva_CrispSdfCoverage(float d, float mid, float fw) {
    float aa = max(fw * WEVA_TEXT_AA_HALF, 1e-6);
    return smoothstep(mid - aa, mid + aa, d);
}

float Weva_TextShadowBlurGain(float blurPx) {
    // Match browser text-shadow behavior more closely than the old SDF band
    // widening path: larger radii spread the mask energy over a wider area,
    // so the visible peak must fall or the shadow reads as a duplicate glyph.
    float wideGlowGain = 0.72 / (1.0 + blurPx * 0.08);
    return lerp(1.0, wideGlowGain, saturate((blurPx - 2.0) / 10.0));
}

float Weva_TextShadowColorGain(float blurPx, float4 premulColor) {
    if (blurPx <= 0.0 || premulColor.a < 1e-5) return 1.0;
    float3 unpremul = saturate(premulColor.rgb / premulColor.a);
    float luma = dot(unpremul, float3(0.2126, 0.7152, 0.0722));
    float darkShadow = saturate((0.30 - luma) / 0.30);
    float wideShadow = saturate((blurPx - 6.0) / 14.0);
    return lerp(1.0, 0.36, darkShadow * wideShadow);
}

float Weva_SampleSdfDistance(float2 uv, float2 uvMin, float2 uvMax) {
    float inside = Weva_UvInsideRect(uv, uvMin, uvMax);
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv), _GlyphAtlasChannelMask) * inside;
}

float Weva_SampleSdfDistance1(float2 uv, float2 uvMin, float2 uvMax) {
    float inside = Weva_UvInsideRect(uv, uvMin, uvMax);
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv), _GlyphAtlas1ChannelMask) * inside;
}

float Weva_SampleSdfDistance2(float2 uv, float2 uvMin, float2 uvMax) {
    float inside = Weva_UvInsideRect(uv, uvMin, uvMax);
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv), _GlyphAtlas2ChannelMask) * inside;
}

float Weva_SampleSdfDistance3(float2 uv, float2 uvMin, float2 uvMax) {
    float inside = Weva_UvInsideRect(uv, uvMin, uvMax);
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv), _GlyphAtlas3ChannelMask) * inside;
}

float Weva_SampleSdfTextBlur(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    float centerD = Weva_SampleSdfDistance(uv, uvMin, uvMax);
    float fw = max(fwidth(centerD), 1e-4);
    float mid = 0.5 - weightBias;
    float2 pxX = ddx(uv);
    float2 pxY = ddy(uv);
    float r1 = blurPx * 0.32;
    float r2 = blurPx * 0.68;
    float r3 = blurPx * 1.05;
    float d1 = r1 * 0.70710678;
    float d2 = r2 * 0.70710678;
    float sum = 0.0;
    float wsum = 0.0;
#define WEVA_ADD_SDF_BLUR_SAMPLE(OFFX, OFFY, W) { \
        float2 suv = uv + pxX * (OFFX) + pxY * (OFFY); \
        sum += Weva_CrispSdfCoverage(Weva_SampleSdfDistance(suv, uvMin, uvMax), mid, fw) * (W); \
        wsum += (W); \
    }
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0, 0.0, 0.12)
    WEVA_ADD_SDF_BLUR_SAMPLE( r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE(-r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0,  r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0, -r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE( d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE(-d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE( d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE(-d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE( r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE(-r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0,  r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0, -r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE( d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE(-d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE( d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE(-d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE( r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE(-r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0,  r3, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE(0.0, -r3, 0.015)
#undef WEVA_ADD_SDF_BLUR_SAMPLE
    return (sum / max(wsum, 1e-6)) * Weva_TextShadowBlurGain(blurPx);
}

float Weva_SampleSdfTextBlur1(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    float centerD = Weva_SampleSdfDistance1(uv, uvMin, uvMax);
    float fw = max(fwidth(centerD), 1e-4);
    float mid = 0.5 - weightBias;
    float2 pxX = ddx(uv);
    float2 pxY = ddy(uv);
    float r1 = blurPx * 0.32;
    float r2 = blurPx * 0.68;
    float r3 = blurPx * 1.05;
    float d1 = r1 * 0.70710678;
    float d2 = r2 * 0.70710678;
    float sum = 0.0;
    float wsum = 0.0;
#define WEVA_ADD_SDF_BLUR_SAMPLE1(OFFX, OFFY, W) { \
        float2 suv = uv + pxX * (OFFX) + pxY * (OFFY); \
        sum += Weva_CrispSdfCoverage(Weva_SampleSdfDistance1(suv, uvMin, uvMax), mid, fw) * (W); \
        wsum += (W); \
    }
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0, 0.0, 0.12)
    WEVA_ADD_SDF_BLUR_SAMPLE1( r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0,  r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0, -r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE1( d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE1( d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE1( r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0,  r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0, -r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE1( d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE1( d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE1( r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE1(-r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0,  r3, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE1(0.0, -r3, 0.015)
#undef WEVA_ADD_SDF_BLUR_SAMPLE1
    return (sum / max(wsum, 1e-6)) * Weva_TextShadowBlurGain(blurPx);
}

float Weva_SampleSdfTextBlur2(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    float centerD = Weva_SampleSdfDistance2(uv, uvMin, uvMax);
    float fw = max(fwidth(centerD), 1e-4);
    float mid = 0.5 - weightBias;
    float2 pxX = ddx(uv);
    float2 pxY = ddy(uv);
    float r1 = blurPx * 0.32;
    float r2 = blurPx * 0.68;
    float r3 = blurPx * 1.05;
    float d1 = r1 * 0.70710678;
    float d2 = r2 * 0.70710678;
    float sum = 0.0;
    float wsum = 0.0;
#define WEVA_ADD_SDF_BLUR_SAMPLE2(OFFX, OFFY, W) { \
        float2 suv = uv + pxX * (OFFX) + pxY * (OFFY); \
        sum += Weva_CrispSdfCoverage(Weva_SampleSdfDistance2(suv, uvMin, uvMax), mid, fw) * (W); \
        wsum += (W); \
    }
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0, 0.0, 0.12)
    WEVA_ADD_SDF_BLUR_SAMPLE2( r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0,  r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0, -r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE2( d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE2( d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE2( r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0,  r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0, -r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE2( d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE2( d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE2( r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE2(-r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0,  r3, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE2(0.0, -r3, 0.015)
#undef WEVA_ADD_SDF_BLUR_SAMPLE2
    return (sum / max(wsum, 1e-6)) * Weva_TextShadowBlurGain(blurPx);
}

float Weva_SampleSdfTextBlur3(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    float centerD = Weva_SampleSdfDistance3(uv, uvMin, uvMax);
    float fw = max(fwidth(centerD), 1e-4);
    float mid = 0.5 - weightBias;
    float2 pxX = ddx(uv);
    float2 pxY = ddy(uv);
    float r1 = blurPx * 0.32;
    float r2 = blurPx * 0.68;
    float r3 = blurPx * 1.05;
    float d1 = r1 * 0.70710678;
    float d2 = r2 * 0.70710678;
    float sum = 0.0;
    float wsum = 0.0;
#define WEVA_ADD_SDF_BLUR_SAMPLE3(OFFX, OFFY, W) { \
        float2 suv = uv + pxX * (OFFX) + pxY * (OFFY); \
        sum += Weva_CrispSdfCoverage(Weva_SampleSdfDistance3(suv, uvMin, uvMax), mid, fw) * (W); \
        wsum += (W); \
    }
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0, 0.0, 0.12)
    WEVA_ADD_SDF_BLUR_SAMPLE3( r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-r1, 0.0, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0,  r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0, -r1, 0.08)
    WEVA_ADD_SDF_BLUR_SAMPLE3( d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-d1,  d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE3( d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-d1, -d1, 0.06)
    WEVA_ADD_SDF_BLUR_SAMPLE3( r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-r2, 0.0, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0,  r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0, -r2, 0.035)
    WEVA_ADD_SDF_BLUR_SAMPLE3( d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-d2,  d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE3( d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-d2, -d2, 0.025)
    WEVA_ADD_SDF_BLUR_SAMPLE3( r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE3(-r3, 0.0, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0,  r3, 0.015)
    WEVA_ADD_SDF_BLUR_SAMPLE3(0.0, -r3, 0.015)
#undef WEVA_ADD_SDF_BLUR_SAMPLE3
    return (sum / max(wsum, 1e-6)) * Weva_TextShadowBlurGain(blurPx);
}

float Weva_SampleSdfText(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    if (blurPx > 0.0) {
        return Weva_SampleSdfTextBlur(uv, uvMin, uvMax, blurPx, weightBias);
    }
    // From here `blurPx <= 0.0`. The earlier shape of this function had a
    // second `if (blurPx > 0.0)` further down with its own return inside,
    // and a tail unconditional `return smoothstep(...)`. FXC's flow analysis
    // could not prove the inner `if` was dead and emitted a "use of
    // potentially uninitialized variable (Weva_SampleSdfText)" warning —
    // on d3d11 player builds this compiles to an actual uninitialized SGPR
    // read, so the shader returns garbage SDF coverage and small body text
    // renders as smeared/overlapping glyphs (visible in match3 / production
    // builds even though Editor looked correct). Linearising the control
    // flow with one unconditional tail return + a single AA branch under
    // WEVA_TEXT_DISABLE_AA gets the warning to clear.
    float2 dx = ddx(uv) * 0.25;
    float2 dy = ddy(uv) * 0.25;
    float4 m = _GlyphAtlasChannelMask;
    float d0 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv - dx - dy), m);
    float d1 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv + dx - dy), m);
    float d2 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv - dx + dy), m);
    float d3 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv + dx + dy), m);
    float dc = 0.25 * (d0 + d1 + d2 + d3);
    float fw = max(fwidth(dc), 1e-6);
    float aa = max(fw * WEVA_TEXT_AA_HALF, 1e-6);
    // Size-driven stem darkening: ramp from 0 at fw~=0.032 to the cap at
    // fw~=0.104+. Subtle on 24px text, visible on 12px body text.
    // weightBias is the faux-bold inward shift; the stem-darken cap fades
    // out as it grows so bold strokes don't muddy/fill counters.
    float stemDarken = saturate(fw * 14.0 - 0.45) * WEVA_TEXT_STEM_DARKEN_MAX
                     * saturate(1.0 - weightBias * 10.0);
    float mid = 0.5 - weightBias - stemDarken;
#if WEVA_TEXT_DISABLE_AA
    return 0.25 * (step(mid, d0) + step(mid, d1) + step(mid, d2) + step(mid, d3));
#else
    return 0.25 * (
        smoothstep(mid - aa, mid + aa, d0)
        + smoothstep(mid - aa, mid + aa, d1)
        + smoothstep(mid - aa, mid + aa, d2)
        + smoothstep(mid - aa, mid + aa, d3));
#endif
}

float Weva_SampleSdfText1(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    if (blurPx > 0.0) {
        return Weva_SampleSdfTextBlur1(uv, uvMin, uvMax, blurPx, weightBias);
    }
    // See Weva_SampleSdfText for why the duplicate blur-branch was removed.
    float2 dx = ddx(uv) * 0.25;
    float2 dy = ddy(uv) * 0.25;
    float4 m = _GlyphAtlas1ChannelMask;
    float d0 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv - dx - dy), m);
    float d1 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv + dx - dy), m);
    float d2 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv - dx + dy), m);
    float d3 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv + dx + dy), m);
    float dc = 0.25 * (d0 + d1 + d2 + d3);
    float fw = max(fwidth(dc), 1e-6);
    float aa = max(fw * WEVA_TEXT_AA_HALF, 1e-6);
    float stemDarken = saturate(fw * 14.0 - 0.45) * WEVA_TEXT_STEM_DARKEN_MAX
                     * saturate(1.0 - weightBias * 10.0);
    float mid = 0.5 - weightBias - stemDarken;
#if WEVA_TEXT_DISABLE_AA
    return 0.25 * (step(mid, d0) + step(mid, d1) + step(mid, d2) + step(mid, d3));
#else
    return 0.25 * (
        smoothstep(mid - aa, mid + aa, d0)
        + smoothstep(mid - aa, mid + aa, d1)
        + smoothstep(mid - aa, mid + aa, d2)
        + smoothstep(mid - aa, mid + aa, d3));
#endif
}

float Weva_SampleSdfText2(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    if (blurPx > 0.0) {
        return Weva_SampleSdfTextBlur2(uv, uvMin, uvMax, blurPx, weightBias);
    }
    // See Weva_SampleSdfText for why the duplicate blur-branch was removed.
    float2 dx = ddx(uv) * 0.25;
    float2 dy = ddy(uv) * 0.25;
    float4 m = _GlyphAtlas2ChannelMask;
    float d0 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv - dx - dy), m);
    float d1 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv + dx - dy), m);
    float d2 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv - dx + dy), m);
    float d3 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv + dx + dy), m);
    float dc = 0.25 * (d0 + d1 + d2 + d3);
    float fw = max(fwidth(dc), 1e-6);
    float aa = max(fw * WEVA_TEXT_AA_HALF, 1e-6);
    float stemDarken = saturate(fw * 14.0 - 0.45) * WEVA_TEXT_STEM_DARKEN_MAX
                     * saturate(1.0 - weightBias * 10.0);
    float mid = 0.5 - weightBias - stemDarken;
#if WEVA_TEXT_DISABLE_AA
    return 0.25 * (step(mid, d0) + step(mid, d1) + step(mid, d2) + step(mid, d3));
#else
    return 0.25 * (
        smoothstep(mid - aa, mid + aa, d0)
        + smoothstep(mid - aa, mid + aa, d1)
        + smoothstep(mid - aa, mid + aa, d2)
        + smoothstep(mid - aa, mid + aa, d3));
#endif
}

float Weva_SampleSdfText3(float2 uv, float2 uvMin, float2 uvMax, float blurPx, float weightBias) {
    if (blurPx > 0.0) {
        return Weva_SampleSdfTextBlur3(uv, uvMin, uvMax, blurPx, weightBias);
    }
    // See Weva_SampleSdfText for why the duplicate blur-branch was removed.
    float2 dx = ddx(uv) * 0.25;
    float2 dy = ddy(uv) * 0.25;
    float4 m = _GlyphAtlas3ChannelMask;
    float d0 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv - dx - dy), m);
    float d1 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv + dx - dy), m);
    float d2 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv - dx + dy), m);
    float d3 = dot(SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv + dx + dy), m);
    float dc = 0.25 * (d0 + d1 + d2 + d3);
    float fw = max(fwidth(dc), 1e-6);
    float aa = max(fw * WEVA_TEXT_AA_HALF, 1e-6);
    float stemDarken = saturate(fw * 14.0 - 0.45) * WEVA_TEXT_STEM_DARKEN_MAX
                     * saturate(1.0 - weightBias * 10.0);
    float mid = 0.5 - weightBias - stemDarken;
#if WEVA_TEXT_DISABLE_AA
    return 0.25 * (step(mid, d0) + step(mid, d1) + step(mid, d2) + step(mid, d3));
#else
    return 0.25 * (
        smoothstep(mid - aa, mid + aa, d0)
        + smoothstep(mid - aa, mid + aa, d1)
        + smoothstep(mid - aa, mid + aa, d2)
        + smoothstep(mid - aa, mid + aa, d3));
#endif
}

// Back-compat overloads. Existing call sites (e.g. tests / preview tooling
// that include this header directly without per-instance bias plumbing)
// keep the original signatures and default to bias = 0 (regular weight).
float Weva_SampleSdfText(float2 uv, float blurPx) { return Weva_SampleSdfText(uv, float2(0.0, 0.0), float2(1.0, 1.0), blurPx, 0.0); }
float Weva_SampleSdfText1(float2 uv, float blurPx) { return Weva_SampleSdfText1(uv, float2(0.0, 0.0), float2(1.0, 1.0), blurPx, 0.0); }
float Weva_SampleSdfText2(float2 uv, float blurPx) { return Weva_SampleSdfText2(uv, float2(0.0, 0.0), float2(1.0, 1.0), blurPx, 0.0); }
float Weva_SampleSdfText3(float2 uv, float blurPx) { return Weva_SampleSdfText3(uv, float2(0.0, 0.0), float2(1.0, 1.0), blurPx, 0.0); }
float Weva_SampleSdfText(float2 uv) { return Weva_SampleSdfText(uv, float2(0.0, 0.0), float2(1.0, 1.0), 0.0, 0.0); }
float Weva_SampleSdfText1(float2 uv) { return Weva_SampleSdfText1(uv, float2(0.0, 0.0), float2(1.0, 1.0), 0.0, 0.0); }
float Weva_SampleSdfText2(float2 uv) { return Weva_SampleSdfText2(uv, float2(0.0, 0.0), float2(1.0, 1.0), 0.0, 0.0); }
float Weva_SampleSdfText3(float2 uv) { return Weva_SampleSdfText3(uv, float2(0.0, 0.0), float2(1.0, 1.0), 0.0, 0.0); }

float Weva_SampleCoverageText(float2 uv) {
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv), _GlyphAtlasChannelMask);
}
float Weva_SampleCoverageText1(float2 uv) {
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv), _GlyphAtlas1ChannelMask);
}
float Weva_SampleCoverageText2(float2 uv) {
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv), _GlyphAtlas2ChannelMask);
}
float Weva_SampleCoverageText3(float2 uv) {
    return dot(SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv), _GlyphAtlas3ChannelMask);
}

float Weva_ApplyCoverageTextBias(float coverage, float weightBias) {
    coverage = saturate(coverage);
    float b = saturate(weightBias);
    // TextCore's hinted coverage atlases are literal grayscale bitmaps. On
    // dark UI, Chrome/DirectWrite-style small text reads heavier because edge
    // coverage is gamma/contrast corrected before compositing. Apply the
    // optical correction to ALL coverage text — the earlier cut gated it on
    // weightBias > 0, so regular (400) body text composited RAW linear
    // coverage and read visibly dimmer/thinner than Chrome (glass sample,
    // 2026-06-07: note text measured ~10-15% under Chrome's ink even after
    // accounting for the panel color). Base gamma 0.76 lifts partial
    // coverage by ~9-12% in the stroke mid-band (c=0.6 -> 0.68, c=0.7 ->
    // 0.76); solid interiors stay solid (1 -> 1); faux-bold pushes the
    // gamma further (0.68) and keeps its small coverage floor.
    float gamma = lerp(0.76, 0.68, saturate(b * 4.0));
    float corrected = pow(max(coverage, 1e-6), gamma);
    return saturate(corrected + b * 0.04 * (1.0 - corrected));
}

// Color-text samplers. Used by the _TEXT_COLOR variant for RGBA bitmap
// atlases (e.g. Segoe UI Emoji COLOR bake). No SDF math — the atlas
// pixels already carry the visible color so we return the texel verbatim
// and let the fragment apply premul / sRGB encoding. Hardware-bilinear
// sampling is sufficient: bitmap emoji are baked at the size they're
// rendered (no SDF distance field to reconstruct).
float4 Weva_SampleColorText(float2 uv) {
    return SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, uv);
}
float4 Weva_SampleColorText1(float2 uv) {
    return SAMPLE_TEXTURE2D(_GlyphAtlas1, sampler_GlyphAtlas1, uv);
}
float4 Weva_SampleColorText2(float2 uv) {
    return SAMPLE_TEXTURE2D(_GlyphAtlas2, sampler_GlyphAtlas2, uv);
}
float4 Weva_SampleColorText3(float2 uv) {
    return SAMPLE_TEXTURE2D(_GlyphAtlas3, sampler_GlyphAtlas3, uv);
}

#endif
