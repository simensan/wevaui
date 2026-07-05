using System;
using System.Collections.Generic;
using Weva.Paint;

namespace Weva.Rendering {
    // Maintains a stack of nested affine transforms. Push multiplies onto the current top
    // (so that a child's transform is composed with its parent), pop discards. The current
    // top represents the cumulative model matrix applied to subsequent geometry.
    public sealed class TransformStack {
        readonly List<Transform2D> stack = new List<Transform2D>(16);

        public TransformStack() {
            stack.Add(Transform2D.Identity);
        }

        public Transform2D Current => stack[stack.Count - 1];
        public int Depth => stack.Count - 1;

        public void Push(Transform2D local) {
            // Apply the local transform first (in object space), then the parent. The
            // existing Multiply convention is "apply this first, then other", so
            // local.Multiply(current) yields a combined transform that takes a point
            // through `local` and then `current`.
            var combined = local.Multiply(Current);
            stack.Add(combined);
        }

        public void Pop() {
            if (stack.Count <= 1) throw new InvalidOperationException("TransformStack underflow");
            stack.RemoveAt(stack.Count - 1);
        }

        public void Reset() {
            stack.Clear();
            stack.Add(Transform2D.Identity);
        }
    }
}
