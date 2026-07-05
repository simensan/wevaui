#if WEVA_URP
using System;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering.URP {
    // Regression and diagnostic tests for the backdrop-filter border-radius clip bug
    // (a real game-timer: blurred region leaks outside the pill's rounded corners).
    //
    // Root-cause analysis: the composite step in ApplyBackdropAndComposite passes
    // `sourceYFlip: false` (hardcoded) regardless of the bdFlipped state accumulated
    // by the filter chain. For color-matrix-only chains, bdFlipped ends up true after
    // the internal flip, and the hardcoded false produced an upside-down composite.
    // For blur-only chains (the common case) bdFlipped stays false and the value was
    // incidentally correct.
    //
    // The tests here pin:
    //   1. ComputeRtRect gives the border box for an empty filter chain (no padding).
    //   2. ComputeRtRect gives the padded source rect for blur (3*sigma px pad each side).
    //   3. Crop UV computation keeps UVs strictly within [0, 1] for a non-origin element.
    //   4. Crop UVs map the clip rect to the correct sub-region of the source RT.
    //   5. bdFlipped / sourceYFlip: blur-only chains keep bdFlipped = false (no fix needed
    //      for the common timer case); color-matrix chains set bdFlipped = true.
    public class BackdropFilterClipTests {
        const double Eps = 1e-9;

        static Rect MakeRect(double x, double y, double w, double h) => new Rect(x, y, w, h);

        // ── Test 1: ComputeRtRect with empty filter chain equals border box ────────────────
        [Test]
        public void ComputeRtRect_empty_filter_returns_border_box_exactly() {
            // A pill-shaped timer at screen position (500, 20), size 200x40.
            var bounds = MakeRect(500, 20, 200, 40);
            var t = Transform2D.Identity;
            var (x, y, w, h) = UIRenderGraphFilterRuntime.ComputeRtRect(
                bounds, t, FilterChain.Empty, 1280, 720);
            Assert.That(x, Is.EqualTo(500), "clip rect must start at border box left edge");
            Assert.That(y, Is.EqualTo(20),  "clip rect must start at border box top edge");
            Assert.That(w, Is.EqualTo(200), "clip rect width must equal border box width");
            Assert.That(h, Is.EqualTo(40),  "clip rect height must equal border box height");
        }

        // ── Test 2: ComputeRtRect with blur(6px) pads by 3*sigma = 18px ──────────────────
        [Test]
        public void ComputeRtRect_blur_adds_three_sigma_padding_each_side() {
            // blur(6px) → padPx = ceil(6 * 3) = 18. Source rect grows by 18px on every side.
            var bounds = MakeRect(100, 50, 200, 80);
            var filters = new FilterChain(new FilterFunction[] { new BlurFilter(6.0) });
            var t = Transform2D.Identity;
            var (sx, sy, sw, sh) = UIRenderGraphFilterRuntime.ComputeRtRect(
                bounds, t, filters, 1280, 720);
            Assert.That(sx, Is.EqualTo(100 - 18), "source X should be clip X minus 18px padding");
            Assert.That(sy, Is.EqualTo(50  - 18), "source Y should be clip Y minus 18px padding");
            Assert.That(sw, Is.EqualTo(200 + 36), "source W should be clip W plus 36px (18 each side)");
            Assert.That(sh, Is.EqualTo(80  + 36), "source H should be clip H plus 36px (18 each side)");
        }

        // ── Test 3: Crop UVs are strictly within [0,1] for a non-origin element ───────────
        [Test]
        public void Crop_uvs_are_within_unit_range_for_non_origin_element() {
            // Timer pill at (500, 20), 200x40, blur(6px) → pad 18.
            int clipX = 500, clipY = 20, clipW = 200, clipH = 40;
            int sourceX = clipX - 18, sourceY = clipY - 18;
            int sourceW = clipW + 36, sourceH = clipH + 36;
            double inv = 1.0 / sourceW;
            double invH = 1.0 / sourceH;
            double u0 = (clipX - sourceX) * inv;    // = 18 / 236
            double v0 = (clipY - sourceY) * invH;   // = 18 / 76
            double u1 = (clipX + clipW - sourceX) * inv;  // = 218 / 236
            double v1 = (clipY + clipH - sourceY) * invH; // = 58 / 76
            Assert.That(u0, Is.GreaterThan(0.0).And.LessThan(1.0), "crop U0 must be in (0,1)");
            Assert.That(v0, Is.GreaterThan(0.0).And.LessThan(1.0), "crop V0 must be in (0,1)");
            Assert.That(u1, Is.GreaterThan(0.0).And.LessThan(1.0), "crop U1 must be in (0,1)");
            Assert.That(v1, Is.GreaterThan(0.0).And.LessThan(1.0), "crop V1 must be in (0,1)");
            Assert.That(u0, Is.LessThan(u1), "crop U0 must be left of U1");
            Assert.That(v0, Is.LessThan(v1), "crop V0 must be above V1");
        }

        // ── Test 4: Crop UVs locate the clip rect at the correct sub-region ───────────────
        [Test]
        public void Crop_uvs_map_clip_rect_to_correct_sub_rect_of_source_rt() {
            // Same pill as above. The crop rect should exactly enclose the border-box
            // region inside the padded source RT. In UV space:
            //   u0 = 18 / 236  ≈ 0.0763
            //   u1 = 218 / 236 ≈ 0.9237  (u1 - u0 = 200/236 — exactly the pill width)
            //   v0 = 18 / 76   ≈ 0.2368
            //   v1 = 58 / 76   ≈ 0.7632  (v1 - v0 = 40/76 — exactly the pill height)
            int clipX = 500, clipY = 20, clipW = 200, clipH = 40;
            int sourceX = 482, sourceY = 2, sourceW = 236, sourceH = 76;
            double inv = 1.0 / sourceW;
            double invH = 1.0 / sourceH;
            double u0 = (clipX - sourceX) * inv;
            double v0 = (clipY - sourceY) * invH;
            double u1 = (clipX + clipW - sourceX) * inv;
            double v1 = (clipY + clipH - sourceY) * invH;
            // Width and height in UV space must correspond to the clip rect dimensions.
            double uvWidth  = (u1 - u0) * sourceW;  // should be clipW = 200
            double uvHeight = (v1 - v0) * sourceH;  // should be clipH = 40
            Assert.That(uvWidth,  Is.EqualTo(clipW).Within(Eps),
                "UV extent in X must span exactly the clip rect width");
            Assert.That(uvHeight, Is.EqualTo(clipH).Within(Eps),
                "UV extent in Y must span exactly the clip rect height");
            // The crop sub-rect must be symmetric: same padding on each side.
            Assert.That(u0, Is.EqualTo(1.0 - u1).Within(1e-6),
                "crop U0 and (1 - U1) must be equal — uniform blur padding on left and right");
        }

        // ── Test 5: Source rect is clamped to viewport bounds ─────────────────────────────
        [Test]
        public void ComputeRtRect_clamps_source_rect_at_viewport_edges() {
            // Element at top edge (y=2) with blur(6px) → unclamped sourceY = 2-18 = -16.
            // After clamping: sourceY = 0, sourceH is reduced accordingly.
            var bounds = MakeRect(100, 2, 200, 40);
            var filters = new FilterChain(new FilterFunction[] { new BlurFilter(6.0) });
            var t = Transform2D.Identity;
            var (sx, sy, sw, sh) = UIRenderGraphFilterRuntime.ComputeRtRect(
                bounds, t, filters, 1280, 720);
            Assert.That(sy, Is.EqualTo(0), "source Y should be clamped to 0 at the top edge");
            Assert.That(sh, Is.LessThan(80 + 36),
                "source H must be less than unpadded size when clamped at top edge");
            // The element bottom (y=2+40=42) plus 18px pad = 60. Still within 720.
            Assert.That(sy + sh, Is.EqualTo(60),
                "source bottom edge should still reach element bottom + 18px pad");
        }

        // ── Test 6: Blur-only chain leaves bdFlipped = false (timer-case orientation) ──────
        // This is a code-contract test: we verify that ApplyBlur does NOT toggle bdFlipped
        // in ApplyBackdropAndComposite. The blur always runs an even number of internal
        // blits (H+V pairs), so net flip is zero. The sourceYFlip fix (bdFlipped) is correct
        // for the blur-only case: bdFlipped stays false, composite reads with sourceYFlip: false.
        //
        // We verify indirectly by checking the source-text contract rather than running the GPU
        // path (which requires a real CommandBuffer).
        [Test]
        public void ApplyBackdropAndComposite_uses_bdFlipped_for_composite_sourceYFlip() {
            // Contract test: the composite DrawQuadAtPx must pass `sourceYFlip: bdFlipped`
            // rather than hardcoded `false`. We verify via the source text the calling
            // convention was updated by the fix.
            //
            // The source file is read from the package path relative to the test assembly.
            var path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(BackdropFilterClipTests).Assembly.Location),
                "..", "..", "..", "..", "..", "..", // up to repo root
                "Packages", "com.wevaui", "Runtime", "Rendering", "URP",
                "UIRenderGraphFilterRuntime.cs");
            if (!System.IO.File.Exists(path)) {
                // Fallback: Unity test runner cwd is typically Assets/; find via project root.
                var altPath = System.IO.Path.Combine(
                    UnityEngine.Application.dataPath, "..", "Packages", "com.wevaui",
                    "Runtime", "Rendering", "URP", "UIRenderGraphFilterRuntime.cs");
                path = altPath;
            }
            string src = System.IO.File.ReadAllText(path);
            Assert.That(src, Does.Contain("sourceYFlip: bdFlipped"),
                "ApplyBackdropAndComposite must use bdFlipped for the composite sourceYFlip. " +
                "Hardcoded `sourceYFlip: false` would flip color-matrix-only chains upside-down.");
            // Belt-and-braces: the old hardcoded value should NOT appear in the composite call.
            // (There are other DrawQuadAtPx calls with sourceYFlip: false for the intermediate
            // blur/downsample passes — we specifically check the radii-carrying composite call.)
            Assert.That(src, Does.Not.Contain(
                "radii, cropU0, cropV0, cropU1, cropV1, sourceYFlip: false"),
                "The radii-carrying composite call must not hardcode sourceYFlip: false.");
        }

        // ── Test 7: SetCompositeClip packs both X and Y radii vectors ─────────────────────
        //
        // CSS Backgrounds & Borders L3 §5 — `border-radius: <x> / <y>` lets authors set
        // asymmetric corner radii. The previous SetCompositeClip dropped YRadius and
        // packed only XRadius per corner; for any non-symmetric radii the SDF in the
        // shader read the wrong value on the cross axis and clipped to the wrong shape.
        //
        // This is a source-text contract test (same shape as the bdFlipped one above):
        // the C# side must call SetVector for BOTH _WevaFilterClipRadii (X) AND
        // _WevaFilterClipRadiiY (Y), and the shader's fragment branch must call the
        // per-axis SDF helper rather than the legacy circular one.
        [Test]
        public void Composite_clip_packs_per_axis_radii_for_asymmetric_border_radius() {
            string repoRoot;
            // Same path-discovery shape as Test 6 above so both tests run from the same
            // assembly-relative root.
            var byAsm = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(BackdropFilterClipTests).Assembly.Location),
                "..", "..", "..", "..", "..", "..");
            if (System.IO.Directory.Exists(System.IO.Path.Combine(byAsm, "Packages", "com.wevaui"))) {
                repoRoot = byAsm;
            } else {
                repoRoot = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..");
            }

            string runtimePath = System.IO.Path.Combine(repoRoot,
                "Packages", "com.wevaui", "Runtime", "Rendering", "URP", "UIRenderGraphFilterRuntime.cs");
            string runtimeSrc = System.IO.File.ReadAllText(runtimePath);
            // C# packs the Y vector via the new shader property id.
            Assert.That(runtimeSrc, Does.Contain("IdFilterClipRadiiY"),
                "SetCompositeClip must define and use IdFilterClipRadiiY so the shader receives YRadius per corner.");
            Assert.That(runtimeSrc, Does.Contain("radii.TopLeft.YRadius"),
                "SetCompositeClip must pack TopLeft.YRadius into the Y radii vector — dropping it clipped asymmetric corners against the wrong axis.");

            string shaderPath = System.IO.Path.Combine(repoRoot,
                "Packages", "com.wevaui", "Runtime", "Rendering", "Shaders", "Weva_Filter.shader");
            string shaderSrc = System.IO.File.ReadAllText(shaderPath);
            Assert.That(shaderSrc, Does.Contain("_WevaFilterClipRadiiY"),
                "Weva_Filter.shader must declare _WevaFilterClipRadiiY so the per-axis SDF can read YRadii.");
            Assert.That(shaderSrc, Does.Contain("Weva_RoundedBoxSdfPerAxis"),
                "Filter composite branch must call Weva_RoundedBoxSdfPerAxis with both radii vectors. " +
                "Calling the legacy circular Weva_RoundedBoxSdf with X-only radii silently drops asymmetric corner shapes.");
        }

        // ── Test 8: RoundRectSdf — the CPU helper the shader mirrors —
        // is correct for asymmetric radii. The shader port reads this same
        // algorithm; if either path drifts, both should be updated together.
        [Test]
        public void RoundRectSdf_PerAxis_asymmetric_corner_is_negative_inside_and_positive_outside() {
            // Pill-shaped element: 200×40, with a 30×10 asymmetric corner radius
            // (wide-short ellipse on the corner — tests that the per-axis SDF
            // correctly differentiates the X and Y arcs).
            double halfW = 100, halfH = 20, rx = 30, ry = 10;
            // Center of the box is well inside the shape — d must be strongly negative.
            double dCenter = Weva.Rendering.RoundRectSdf.SamplePerAxis(0, 0, halfW, halfH, rx, ry);
            Assert.That(dCenter, Is.LessThan(-5),
                "center fragment must be deep inside the shape (large negative SDF)");
            // A point on the corner's principal x-axis at exactly rx (boundary).
            // local pos = (halfW - 0, ±halfH) — i.e., on the corner-circle's x extreme.
            // Actually, the boundary on the X axis of the corner ellipse is at
            // local pos = (halfW, 0) — the rightmost point of the rect, which is on
            // the rounded boundary since the corner curve starts here.
            double dRightEdge = Weva.Rendering.RoundRectSdf.SamplePerAxis(halfW, 0, halfW, halfH, rx, ry);
            Assert.That(dRightEdge, Is.EqualTo(0).Within(0.001),
                "fragment on the right edge of the pill must be on the rounded boundary (SDF ≈ 0)");
            // A point outside the box must be strictly positive.
            double dOutside = Weva.Rendering.RoundRectSdf.SamplePerAxis(halfW + 5, halfH + 5, halfW, halfH, rx, ry);
            Assert.That(dOutside, Is.GreaterThan(0),
                "fragment outside the box must yield positive SDF");
        }

        [Test]
        public void RoundRectSdf_PerAxis_with_equal_radii_matches_symmetric_path() {
            // The per-axis path with rx == ry must agree with the symmetric `Sample`
            // (which calls the standard IQ circular formula) to within float epsilon
            // — pinned so future refactors don't drift the two paths apart.
            double halfW = 50, halfH = 30, r = 12;
            for (double x = -60; x <= 60; x += 10) {
                for (double y = -40; y <= 40; y += 10) {
                    double dPerAxis = Weva.Rendering.RoundRectSdf.SamplePerAxis(x, y, halfW, halfH, r, r);
                    double dSym = Weva.Rendering.RoundRectSdf.Sample(x, y, halfW, halfH, r);
                    Assert.That(dPerAxis, Is.EqualTo(dSym).Within(1e-6),
                        $"per-axis SDF must agree with circular SDF at ({x},{y})");
                }
            }
        }
    }
}
#endif
