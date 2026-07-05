using NUnit.Framework;
using System.IO;
using UnityEngine;
using Weva.Rendering;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering {
    // CONTRACT TESTS — intentionally brittle to shader/C# changes (L4).
    //
    // These tests pin the public contract between URPRenderBackend (which sets shader globals,
    // picks pass indices, and routes draw calls) and the StencilWrite.shader / Weva_Gradient.shader
    // files. They assert exact string literals (`"Hidden/Weva/StencilWrite"`, property names
    // like `_StencilRef`) and exact pass indices (Push=0, Pop=1) because the C# side resolves
    // those by string/index lookup at runtime — a rename on either side silently breaks rendering
    // with no compile error, so the regression has to be caught here.
    //
    // If you rename a shader, a pass, or a uniform/property and a test in this file goes red:
    // that's working as intended. Update BOTH the shader and the C# constant in the same change,
    // then update the literal asserted here. Do NOT delete or weaken these tests to "make them
    // pass" without verifying the rendering still works end-to-end against a real URP frame.
    public class StencilWriteShaderContractTests {
        [Test]
        public void Stencil_write_shader_name_matches_path() {
            Assert.That(StencilClipGeometry.ShaderName, Is.EqualTo("Hidden/Weva/StencilWrite"));
        }

        [Test]
        public void Push_pass_index_is_zero() {
            // Pass 0 in the shader is the IncrSat (push) pass per the shader's documented order.
            Assert.That(StencilClipGeometry.PushPassIndex, Is.EqualTo(0));
        }

        [Test]
        public void Pop_pass_index_is_one() {
            // Pass 1 in the shader is the DecrSat (pop) pass.
            Assert.That(StencilClipGeometry.PopPassIndex, Is.EqualTo(1));
        }

        [Test]
        public void Stencil_ref_global_property_name_is_stable() {
            // The content shaders all reference `_StencilRef` in their Stencil { Ref [...] }
            // block; the backend must set the same property via SetGlobalInt.
            Assert.That(StencilClipGeometry.StencilRefProperty, Is.EqualTo("_StencilRef"));
        }

        [Test]
        public void Stencil_comp_global_property_name_is_stable() {
            // The content shaders' Comp [_StencilComp] selects between Always (no clip) and
            // Equal (clipped). The backend toggles this when the stack depth changes.
            Assert.That(StencilClipGeometry.StencilCompProperty, Is.EqualTo("_StencilComp"));
        }

        [Test]
        public void Stencil_write_ref_global_property_name_is_stable() {
            // The StencilWrite shader's Push (Pass 0) and Pop (Pass 1) read `_StencilWriteRef`
            // to test against the parent ref (push) or the ref we're popping (pop). Backend
            // sets it via SetGlobalInt immediately before each DrawMesh.
            Assert.That(StencilClipGeometry.StencilWriteRefProperty, Is.EqualTo("_StencilWriteRef"));
        }

        [Test]
        public void Max_stencil_ref_matches_eight_bit_budget() {
            // 255 max in a single byte; we reserve one for testing → 254. If a platform with
            // wider stencil bits is added later, this constant moves and the backend tracks it.
            Assert.That(StencilClipGeometry.MaxStencilRef, Is.LessThanOrEqualTo(254));
            Assert.That(StencilClipGeometry.MaxStencilRef, Is.GreaterThan(0));
        }

        [TestCase("Runtime/Rendering/Shaders/Weva-Quad.shader")]
        [TestCase("Runtime/Rendering/Shaders/Weva_Solid.shader")]
        [TestCase("Runtime/Rendering/Shaders/Weva_Gradient.shader")]
        [TestCase("Runtime/Rendering/Shaders/Weva_Text.shader")]
        [TestCase("Runtime/Rendering/Shaders/Weva_Shadow.shader")]
        [TestCase("Runtime/Rendering/Shaders/Weva_Filter.shader")]
        public void Screen_space_content_shaders_ignore_scene_depth(string packageRelativePath) {
            var source = File.ReadAllText(PackagePath(packageRelativePath));
            Assert.That(source, Does.Contain("ZWrite Off"));
            Assert.That(source, Does.Contain("ZTest Always"));
        }

        [Test]
        public void Screen_space_urp_passes_load_scene_color_before_blending() {
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));
            var legacyPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderPass.cs"));

            Assert.That(renderGraphPass, Does.Contain("builder.UseTexture(color, AccessFlags.ReadWrite)"));
            Assert.That(legacyPass, Does.Contain("builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite)"));
        }

        [Test]
        public void Rounded_border_shader_antialiases_inner_and_outer_edges() {
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));

            Assert.That(quadShader, Does.Contain("float innerCutoff = 1.0 - Weva_Coverage(d + pickedWidth);"));
            Assert.That(quadShader, Does.Contain("float borderCoverage = coverage * innerCutoff;"));
            Assert.That(quadShader, Does.Not.Contain("distFromEdge < pickedWidth"));
        }

        [Test]
        public void Quad_shader_antialiases_aabb_and_rounded_clip_edges() {
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));

            Assert.That(quadShader, Does.Contain("Weva_AabbClipCoverage"));
            Assert.That(quadShader, Does.Contain("Weva_ClipPathCoverage"));
            Assert.That(quadShader, Does.Contain("col * clipCoverage"));
            Assert.That(quadShader, Does.Contain("fillColor * clipCoverage"));
        }

        [Test]
        public void Quad_shader_clips_outset_box_shadow_out_of_element_interior() {
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));
            var batcher = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIBatcher.cs"));

            Assert.That(quadShader, Does.Contain("outer box-shadow is"));
            Assert.That(quadShader, Does.Contain("local + shadowOffset"));
            Assert.That(quadShader, Does.Contain("a *= 1.0 - insideElement"));
            Assert.That(batcher, Does.Contain("transparent backgrounds do"));
            Assert.That(batcher, Does.Contain("inst.BorderWidths = new Vector4((float)shadow.OffsetX"));
        }

        [Test]
        public void Quad_shader_encodes_css_colors_for_gamma_compositing() {
            var shaderLib = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIShaderLib.hlsl"));
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));

            // A-SRGB-COMPOSITE: the UI composites in gamma sRGB. Every fragment
            // passes through the Weva_EncodeForTarget seam, which emits true
            // sRGB-encoded premultiplied colour for gamma-compositing targets
            // (the intermediate / sRGB=false RTs) and raw linear premul for the
            // lone linear-camera fallback. The legacy per-fragment 0.16
            // approximation (Weva_CssLinearTargetBlendLift) is gone.
            Assert.That(shaderLib, Does.Contain("float4 Weva_EncodeForTarget(float4 col)"));
            Assert.That(shaderLib, Does.Contain("return Weva_PremulLinearToPremulSrgb(col);"));
            Assert.That(shaderLib, Does.Contain("_WevaSrgbComposite"));
            Assert.That(shaderLib, Does.Not.Contain("Weva_CssLinearTargetBlendLift"));
            Assert.That(quadShader, Does.Contain("Weva_EncodeForTarget(masked)"));
        }

        [Test]
        public void Backdrop_filter_samples_postprocessed_backbuffer_copy() {
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));
            var filterRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));

            Assert.That(renderGraphPass, Does.Contain("resourceData.activeColorTexture"));
            Assert.That(renderGraphPass, Does.Contain("BackdropSourceTarget"));
            Assert.That(renderGraphPass, Does.Contain("CameraTargetDescriptor"));
            Assert.That(renderGraphPass, Does.Contain("TryBindBackdropCopy"));
            Assert.That(renderGraphPass, Does.Contain("cmd.Blit(backdropSourceTarget"));
            Assert.That(renderGraphPass, Does.Contain("Blitter.BlitTexture"));
            Assert.That(renderGraphPass, Does.Contain("rtHandleScale"));
            Assert.That(renderGraphPass, Does.Contain("cmd.SetViewport(new UnityEngine.Rect(0f, 0f, width, height))"));
            Assert.That(renderGraphPass, Does.Contain("effectiveBackdropSource"));
            Assert.That(renderGraphPass, Does.Contain("BackdropFilterEvents.Count > 0"));
            Assert.That(filterRuntime, Does.Contain("RenderTargetIdentifier backdropSourceTarget"));
            Assert.That(filterRuntime, Does.Contain("DrawQuadAtPx(cb, backdropSourceTarget"));
            Assert.That(renderGraphPass, Does.Not.Contain("resourceData.cameraColor"));
        }

        [Test]
        public void Backdrop_filter_capture_keeps_copy_source_screen_oriented() {
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));
            var filterRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));

            Assert.That(renderGraphPass, Does.Not.Contain("ShouldFlipBackdropSource"));
            Assert.That(renderGraphPass, Does.Not.Contain("GetTextureUVOrigin"));
            Assert.That(renderGraphPass, Does.Not.Contain("BackdropSourceNeedsYFlip"));
            Assert.That(renderGraphPass, Does.Contain("bool backdropCaptureSourceYFlip = backdropCopyAllocated"));
            Assert.That(filterRuntime, Does.Contain("DrawQuadAtPx(cb, backdropSourceTarget"));
            Assert.That(filterRuntime, Does.Contain("ComputeBackdropCaptureUvs"));
            Assert.That(filterRuntime, Does.Contain("cb.SetViewport(new UnityEngine.Rect(0f, 0f, sourceW, sourceH))"));
            Assert.That(filterRuntime, Does.Contain("cb.SetViewport(new UnityEngine.Rect(0f, 0f, parentWidth, parentHeight))"));
            Assert.That(filterRuntime, Does.Contain("u0, v0, u1, v1, sourceYFlip: false"));
        }

        [Test]
        public void Mix_blend_backdrop_sample_flips_with_capture_orientation() {
            // A-MIXBLEND-YFLIP: the SAME `_WevaBackdropCopyRt` copy feeds both
            // the backdrop-filter capture (which got the orientation fix) and
            // the mix-blend sampler `Weva_SampleBackdropPremul`. The sampler
            // must apply the same flip decision via the `_WevaBackdropYFlip`
            // global or intermediate-RT cameras (post-processing on, e.g.
            // a real game) blend against an upside-down backdrop.
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));

            Assert.That(quadShader, Does.Contain("float _WevaBackdropYFlip"));
            Assert.That(quadShader, Does.Contain("if (_WevaBackdropYFlip > 0.5) uv.y = 1.0 - uv.y"));
            Assert.That(renderGraphPass, Does.Contain("Shader.PropertyToID(\"_WevaBackdropYFlip\")"));
            Assert.That(renderGraphPass, Does.Contain(
                "cmd.SetGlobalFloat(IdWevaBackdropYFlip, backdropCaptureSourceYFlip ? 1f : 0f)"));
        }

        [Test]
        public void Backdrop_capture_uvs_mirror_against_full_texture_when_flipped() {
            var normal = UIRenderGraphFilterRuntime.ComputeBackdropCaptureUvs(
                sourceX: 10, sourceY: 20, sourceW: 30, sourceH: 40,
                parentWidth: 100, parentHeight: 200, sourceYFlip: false);
            Assert.That(normal.U0, Is.EqualTo(0.1).Within(0.000001));
            Assert.That(normal.U1, Is.EqualTo(0.4).Within(0.000001));
            Assert.That(normal.V0, Is.EqualTo(0.1).Within(0.000001));
            Assert.That(normal.V1, Is.EqualTo(0.3).Within(0.000001));

            // sourceYFlip = the copied backdrop texture is BOTTOM-UP, so the
            // CSS band [0.1, 0.3] must sample texture V [0.9, 0.7] — a
            // FULL-TEXTURE mirror that relocates the band AND reverses its
            // orientation. The earlier pin asserted (0.3, 0.1) — a
            // within-band swap that reversed orientation but read the WRONG
            // band: a top-of-screen backdrop-filter panel blurred the bottom
            // of the screen (GLASS-PANEL-DARK, measured live 2026-06-07 —
            // glass note cards sampled the teal blob from the mirrored
            // position; a no-op blur(0.01px) showed a neighbouring card's
            // mirrored text verbatim).
            var flipped = UIRenderGraphFilterRuntime.ComputeBackdropCaptureUvs(
                sourceX: 10, sourceY: 20, sourceW: 30, sourceH: 40,
                parentWidth: 100, parentHeight: 200, sourceYFlip: true);
            Assert.That(flipped.U0, Is.EqualTo(0.1).Within(0.000001));
            Assert.That(flipped.U1, Is.EqualTo(0.4).Within(0.000001));
            Assert.That(flipped.V0, Is.EqualTo(0.9).Within(0.000001));
            Assert.That(flipped.V1, Is.EqualTo(0.7).Within(0.000001));
        }

        [Test]
        public void Quad_shader_samples_real_image_texture_for_image_brushes() {
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));
            var shaderLib = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIShaderLib.hlsl"));
            var batcher = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIBatcher.cs"));
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));

            Assert.That(shaderLib, Does.Contain("TEXTURE2D(_WevaImage)"));
            Assert.That(quadShader, Does.Contain("brushIndex == 4"));
            Assert.That(quadShader, Does.Contain("_WevaImage (\"Weva Image\", 2D)"));
            Assert.That(quadShader, Does.Contain("SAMPLE_TEXTURE2D(_WevaImage"));
            Assert.That(batcher, Does.Not.Contain("Image brushes degrade to magenta"));
            Assert.That(UIRenderGraphPass.IdImageTexture, Is.EqualTo(Shader.PropertyToID("_WevaImage")));
            Assert.That(renderGraphPass, Does.Contain("st.Mpb.SetTexture(IdImageTexture, batchImageTex);"));
            Assert.That(renderGraphPass, Does.Contain("resources.GetQuadMaterial(batch.Key.StencilRef, batchImageTex);"));
            Assert.That(renderGraphPass, Does.Contain("readonly System.Collections.Generic.Dictionary<long, Material> imageMaterials"));
            Assert.That(renderGraphPass, Does.Contain("AreCachedImageBindingsValid(batcher)"));
            Assert.That(renderGraphPass, Does.Contain("AreCachedTextAtlasBindingsValid(batcher)"));
            Assert.That(renderGraphPass, Does.Not.Contain("cmd.SetGlobalTexture(IdImageTexture"));
            Assert.That(renderGraphPass, Does.Contain("public UnityEngine.Texture ImageTexture;"));
            Assert.That(renderGraphPass, Does.Contain("public UnityEngine.Texture AtlasTexture0;"));
            Assert.That(renderGraphPass, Does.Contain("batchImageTex = batch.Key.ImageTexture"));
            Assert.That(renderGraphPass, Does.Contain("AtlasTexture0 = batchAtlasTex0"));
            Assert.That(renderGraphPass, Does.Contain("AtlasTextureForId(batch.AtlasIdSlot0)"));
            Assert.That(batcher, Does.Contain("public readonly UnityEngine.Texture ImageTexture"));
        }

        [Test]
        public void Backdrop_filter_composite_is_clipped_to_rounded_border_radius() {
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));
            var filterRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));
            var filterShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva_Filter.shader"));

            Assert.That(renderGraphPass, Does.Contain("be.Radii"));
            Assert.That(filterRuntime, Does.Contain("_WevaFilterClipRadii"));
            Assert.That(filterShader, Does.Contain("Weva_RoundedBoxSdf"));
            Assert.That(filterShader, Does.Contain("_WevaFilterClipEnabled"));
        }

        [Test]
        public void Filter_composite_keeps_backdrop_capture_screen_oriented() {
            var filterRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));
            var filterShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva_Filter.shader"));

            Assert.That(filterRuntime, Does.Contain("_WevaFilterSourceYFlip"));
            Assert.That(filterRuntime, Does.Contain("_WevaFilterEncodeSrgb"));
            Assert.That(filterRuntime, Does.Not.Contain("sourceYFlip: sourceYFlip"));
            Assert.That(filterRuntime, Does.Contain("const bool InternalFilterSourceYFlip = true"));
            Assert.That(filterRuntime, Does.Contain("sourceYFlip: InternalFilterSourceYFlip"));
            Assert.That(filterRuntime, Does.Contain("ComputeBackdropCaptureUvs"));
            Assert.That(filterRuntime, Does.Contain("u0, v0, u1, v1, sourceYFlip: false"));
            // The composite pass uses bdFlipped (not a literal false) — the flip
            // tracks whether the internal ping-pong chain produced an odd number
            // of Y-flips (color-matrix-only chains contribute one flip each).
            // Commit aaabf45 changed this from `sourceYFlip: false` to
            // `sourceYFlip: bdFlipped` to fix upside-down compositing for
            // color-matrix-only chains (brightness, contrast, hue-rotate, etc.).
            Assert.That(filterRuntime, Does.Contain("radii, cropU0, cropV0, cropU1, cropV1, sourceYFlip: bdFlipped"));
            Assert.That(filterRuntime, Does.Contain("scopeBoxTransform, sourceYFlip: false"));
            // The composite-into-target blit encodes sRGB only when the target
            // colour space requires it (gamma target), not unconditionally — the
            // composite pass passes `encodeSrgb: encodeForTarget`. (Earlier this
            // was a literal `true`; the conditional form is correct for linear
            // targets, which must NOT re-encode.)
            Assert.That(filterRuntime, Does.Contain("sourceYFlip: false, encodeSrgb: encodeForTarget"));
            Assert.That(filterRuntime, Does.Contain("_WevaRawFilterOutput"));
            Assert.That(filterShader, Does.Contain("_WevaFilterSourceYFlip"));
            Assert.That(filterShader, Does.Contain("uv.y = 1.0 - uv.y"));
            Assert.That(filterShader, Does.Contain("Weva_EncodeForTarget(src)"));
        }

        [Test]
        public void Filter_runtime_coalesces_adjacent_color_matrix_filters() {
            var renderGraphRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));
            var legacyPipeline = File.ReadAllText(PackagePath("Runtime/Rendering/Backend/FilterPipeline.cs"));

            Assert.That(renderGraphRuntime, Does.Contain("TryGetColorMatrix"));
            Assert.That(renderGraphRuntime, Does.Contain("ColorMatrices.Compose(pendingColorMatrix, colorMatrix)"));
            Assert.That(legacyPipeline, Does.Contain("TryGetColorMatrix"));
            Assert.That(legacyPipeline, Does.Contain("ColorMatrices.Compose(pendingColorMatrix, colorMatrix)"));
        }

        [Test]
        public void Render_graph_filter_scopes_stack_for_nested_text_shadow_blurs() {
            var renderGraphPass = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphPass.cs"));
            var shaderLib = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIShaderLib.hlsl"));

            Assert.That(renderGraphPass, Does.Contain("readonly List<ActiveFilterScope> activeFilterScopes"));
            Assert.That(renderGraphPass, Does.Contain("ParentOriginX"));
            Assert.That(renderGraphPass, Does.Contain("drawingIntoFilterRt"));
            Assert.That(shaderLib, Does.Contain("_WevaRawFilterOutput"));
        }

        [Test]
        public void Filter_composite_transform_uses_same_absolute_matrix_convention_as_quad_shader() {
            var filterRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));

            Assert.That(quadShader, Does.Contain("The matrix acts on the FULL world position"));
            Assert.That(filterRuntime, Does.Contain("TransformCompositePoint(scopeBoxTransform, x"));
            Assert.That(filterRuntime, Does.Contain("t.Apply(px, py)"));
            Assert.That(filterRuntime, Does.Not.Contain("TransformAround(scopeBoxTransform"));
        }

        [Test]
        public void Gradients_pack_srgb_interpolation_sign_for_shader() {
            var shaderLib = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIShaderLib.hlsl"));
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));
            var batcher = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIBatcher.cs"));

            Assert.That(shaderLib, Does.Contain("Weva_GradientLerp"));
            Assert.That(shaderLib, Does.Contain("Weva_PremulLinearToPremulSrgb"));
            Assert.That(shaderLib, Does.Contain("Weva_PremulSrgbToPremulLinear"));
            // G1b — Oklab branch on the shader side and the 3-state encoder on
            // the C# side land together. The legacy sign-bit decode for sRGB
            // vs linear-RGB still runs first; the Oklab branch piggybacks via
            // a 0.25 fractional offset detected by `frac(abs(val)) > 0.1`.
            Assert.That(shaderLib, Does.Contain("Weva_LinearToOklab"));
            Assert.That(shaderLib, Does.Contain("Weva_OklabToLinear"));
            Assert.That(quadShader, Does.Contain("brushParams.w < 0.0 ? 1 : 0"));
            Assert.That(quadShader, Does.Contain("borderStyles.x < 0.0 ? 1 : 0"));
            Assert.That(quadShader, Does.Contain("frac(abs(brushParams.w)) > 0.1"));
            Assert.That(quadShader, Does.Contain("frac(abs(borderStyles.x)) > 0.1"));
            Assert.That(batcher, Does.Contain("GradientColorSpaceSign"));
            Assert.That(batcher, Does.Contain("EncodeGradientCountAndColorSpace"));
            Assert.That(batcher, Does.Contain("CssColorSpace.Srgb"));
            Assert.That(batcher, Does.Contain("CssColorSpace.Oklab"));
        }

        [Test]
        public void Text_shadow_blur_gain_preserves_small_shadows_and_attenuates_wide_glows() {
            var shaderLib = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIShaderLib.hlsl"));
            var quadShader = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva-Quad.shader"));

            Assert.That(shaderLib, Does.Contain("float wideGlowGain = 0.72 / (1.0 + blurPx * 0.08);"));
            Assert.That(shaderLib, Does.Contain("lerp(1.0, wideGlowGain, saturate((blurPx - 2.0) / 10.0))"));
            Assert.That(shaderLib, Does.Contain("Weva_TextShadowColorGain"));
            Assert.That(shaderLib, Does.Contain("darkShadow"));
            Assert.That(shaderLib, Does.Contain("lerp(1.0, 0.36, darkShadow * wideShadow)"));
            Assert.That(quadShader, Does.Contain("coverageT *= Weva_TextShadowColorGain(blurPx, fillColor);"));
        }

        [Test]
        public void Backdrop_filter_blurs_expanded_source_then_crops_to_border_box() {
            var filterRuntime = File.ReadAllText(PackagePath("Runtime/Rendering/URP/UIRenderGraphFilterRuntime.cs"));

            Assert.That(filterRuntime, Does.Contain("ComputeRtRect(bounds, transform, FilterChain.Empty"));
            Assert.That(filterRuntime, Does.Contain("ComputeRtRect(bounds, transform, filters"));
            Assert.That(filterRuntime, Does.Contain("cropU0"));
            Assert.That(filterRuntime, Does.Contain("cropV0"));
        }

        static string PackagePath(string packageRelativePath) {
            var root = Path.GetDirectoryName(Application.dataPath);
            var path = packageRelativePath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(root, "Packages", "com.wevaui", path);
        }
    }
}
