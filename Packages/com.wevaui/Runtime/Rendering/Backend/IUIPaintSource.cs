using Weva.Paint;

namespace Weva.Rendering {
    public interface IUIPaintSource {
        int Order { get; }
        void EmitPaint(IRenderBackend backend);

        // True when the paint output for this source might differ from the
        // last frame the render pass emitted. Lets the render pass skip the
        // BeginFrame/EmitPaint/EndFrame cycle entirely on idle frames so
        // the previous frame's batches feed the GPU verbatim — paint
        // conversion + batching is wasted work when nothing's changed.
        // Sources without a cheap "is anything dirty" answer can return
        // true unconditionally; correctness is preserved either way.
        bool NeedsRepaint { get; }
    }

    public interface IRenderViewportAwarePaintSource {
        void PrepareForRenderViewport(int width, int height);
    }
}
