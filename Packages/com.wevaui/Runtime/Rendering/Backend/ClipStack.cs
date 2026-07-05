using System.Collections.Generic;
using Weva.Paint;

namespace Weva.Rendering {
    // Stencil-buffer clip stack. Each PushClip increments a stencil ref by 1; URPRenderBackend
    // is responsible for actually writing the rounded-rect mask into the stencil buffer using
    // ShaderResources.GetStencilWrite() (Pass 0 = Push/IncrSat, Pass 1 = Pop/DecrSat). This
    // class only owns the stack bookkeeping so it stays headlessly testable.
    //
    // Limitations:
    // - 8-bit stencil = 255 max increments. We clamp at 254 (StencilClipGeometry.MaxStencilRef);
    //   further pushes are silently dropped (TryPush returns false). Tests assert this.
    // - If the camera depth attachment lacks a stencil bit, URPRenderBackend skips the stencil
    //   draw entirely and emits a single warning per session (rounded-rect clips have no visual
    //   effect in that case).
    public sealed class ClipStack {
        public const int MaxDepth = StencilClipGeometry.MaxStencilRef;

        readonly Stack<ClipFrame> frames = new Stack<ClipFrame>();
        int currentRef;

        public ClipStack() { }

#if WEVA_URP
        // Legacy ctor retained for backwards compat with URPRenderBackend wiring; the
        // ShaderResources reference is unused here (the backend owns the stencil material).
        public ClipStack(ShaderResources resources) { }
#endif

        public int CurrentStencilRef => currentRef;
        public int Depth => frames.Count;

        // Returns true if the push fit within the stack budget; false if we hit MaxDepth.
        // No CommandBuffer interaction — URPRenderBackend issues the stencil-write draw
        // separately so the same stack can be exercised in headless tests.
        public bool TryPush(Rect bounds, BorderRadii radii, Transform2D worldTransform) {
            if (frames.Count >= MaxDepth) return false;
            currentRef++;
            frames.Push(new ClipFrame(currentRef, bounds, radii, worldTransform));
            return true;
        }

        public bool TryPop() {
            if (frames.Count == 0) return false;
            frames.Pop();
            currentRef = frames.Count == 0 ? 0 : frames.Peek().Ref;
            return true;
        }

        public ClipFrame Top {
            get {
                if (frames.Count == 0) return default;
                return frames.Peek();
            }
        }

        public void Reset() {
            frames.Clear();
            currentRef = 0;
        }

        public IReadOnlyCollection<ClipFrame> Frames => frames;

        public readonly struct ClipFrame {
            public readonly int Ref;
            public readonly Rect Bounds;
            public readonly BorderRadii Radii;
            public readonly Transform2D Transform;
            public ClipFrame(int @ref, Rect bounds, BorderRadii radii, Transform2D transform) {
                Ref = @ref; Bounds = bounds; Radii = radii; Transform = transform;
            }
        }
    }
}
