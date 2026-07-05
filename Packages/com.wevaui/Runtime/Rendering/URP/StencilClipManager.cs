using System;
using System.Collections.Generic;
using Weva.Paint;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Rendering.URP {
    // Push/pop stencil-clip stack used by UIBatcher. Stores per-push frames so the pass
    // can re-emit the exact same geometry on Pop (DecrSat must cover the same fragments
    // IncrSat touched).
    //
    // Records each push/pop event into a flat list so the render pass can interleave the
    // stencil draws with the content draws in order. The events let the pass run a
    // mask-write quad immediately before the first content batch that uses the new ref.
    //
    // Maximum nesting depth = 16 (per spec). Beyond that, TryPush returns false and the
    // batcher is expected to silently drop the clip — the visual effect is "no clip"
    // for that subtree, which is the same behavior as IMGUIDocumentRenderer.
    public sealed class StencilClipManager {
        public const int MaxDepth = 16;

        readonly Stack<ClipFrame> stack = new Stack<ClipFrame>();
        readonly List<ClipEvent> events = new List<ClipEvent>();
        int currentRef;

        public int CurrentRef => currentRef;
        public int Depth => stack.Count;
        public IReadOnlyList<ClipEvent> Events => events;

        public bool PushClip(PaintRect bounds, BorderRadii radii, Transform2D worldTransform) {
            if (stack.Count >= MaxDepth) return false;
            currentRef++;
            var frame = new ClipFrame(currentRef, bounds, radii, worldTransform);
            stack.Push(frame);
            events.Add(ClipEvent.MakePush(frame));
            return true;
        }

        public bool PopClip() {
            if (stack.Count == 0) return false;
            var frame = stack.Pop();
            events.Add(ClipEvent.MakePop(frame));
            currentRef = stack.Count == 0 ? 0 : stack.Peek().Ref;
            return true;
        }

        public void Reset() {
            stack.Clear();
            events.Clear();
            currentRef = 0;
        }

        public readonly struct ClipFrame {
            public readonly int Ref;
            public readonly PaintRect Bounds;
            public readonly BorderRadii Radii;
            public readonly Transform2D WorldTransform;

            public ClipFrame(int @ref, PaintRect bounds, BorderRadii radii, Transform2D worldTransform) {
                Ref = @ref;
                Bounds = bounds;
                Radii = radii;
                WorldTransform = worldTransform;
            }
        }

        public readonly struct ClipEvent {
            public readonly ClipEventKind Kind;
            public readonly ClipFrame Frame;

            ClipEvent(ClipEventKind kind, ClipFrame frame) { Kind = kind; Frame = frame; }
            public static ClipEvent MakePush(ClipFrame f) => new ClipEvent(ClipEventKind.Push, f);
            public static ClipEvent MakePop(ClipFrame f) => new ClipEvent(ClipEventKind.Pop, f);
        }

        public enum ClipEventKind { Push, Pop }
    }
}
