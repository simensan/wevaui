using Weva.Paint;

namespace Weva.Rendering {
    // Pure-C# helpers shared by the URP backend and the headless tests that exercise the
    // stencil clip-mask geometry. Encoding the rounded-rect into a MeshBuilder is identical
    // to the path used by other content quads — the difference is the *material* (stencil
    // write) and the absence of color writes.
    //
    // Constants below are the public API contract that StencilWriteShaderContractTests
    // pins down — backend and tests reference these names rather than literals.
    public static class StencilClipGeometry {
        public const string ShaderName = "Hidden/Weva/StencilWrite";
        public const int PushPassIndex = 0;
        public const int PopPassIndex = 1;
        // Property read by the *content* shaders (Equal-test) and set by URPRenderBackend
        // every time the stack depth changes.
        public const string StencilRefProperty = "_StencilRef";
        // Property read by content shaders to switch between Always (no clip) and Equal
        // (clipped). 8 = Always, 3 = Equal — see UnityEngine.Rendering.CompareFunction.
        public const string StencilCompProperty = "_StencilComp";
        // Property read by the StencilWrite shader's Push/Pop passes to scope the
        // increment/decrement to the parent's clip region. See the shader header for
        // semantics; backend sets this immediately before the DrawMesh call.
        public const string StencilWriteRefProperty = "_StencilWriteRef";

        // 8-bit stencil buffer = 255 max increments. We reserve one bit (clamp at 254) so
        // that the comparison stays well-defined even when an extra Push happens during
        // overflow recovery.
        public const int MaxStencilRef = 254;

        // Encodes the clip mask quad into the MeshBuilder. The same geometry is used for
        // both the Push (IncrSat) and Pop (DecrSat) draws so the stencil increments/
        // decrements cover the same fragments, restoring the buffer on Pop.
        //
        // The rounded-rect SDF runs in the shader; this method only stages the four
        // vertices. Returns the index of the first vertex emitted (== old vertex count).
        public static int EncodeClipMask(
            MeshBuilder builder,
            Rect bounds,
            BorderRadii radii,
            Transform2D worldTransform) {
            float rx, ry;
            (rx, ry) = RoundRectSdf.PackUniform(radii);
            // Color is irrelevant (ColorMask 0 in the stencil shader); we still pass white
            // so any inadvertent debug visualization shows the mask.
            return builder.AddQuad(bounds, LinearColor.White, MeshBuilder.EffectIdSolid, rx, ry, worldTransform);
        }
    }
}
