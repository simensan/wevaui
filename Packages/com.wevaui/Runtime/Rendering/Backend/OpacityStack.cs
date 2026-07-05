using System;
using System.Collections.Generic;

namespace Weva.Rendering {
    public sealed class OpacityStack {
        readonly List<float> stack = new List<float>(16);

        public OpacityStack() {
            stack.Add(1f);
        }

        public float Current => stack[stack.Count - 1];
        public int Depth => stack.Count - 1;

        public void Push(float local) {
            if (local < 0f) local = 0f;
            if (local > 1f) local = 1f;
            stack.Add(Current * local);
        }

        public void Pop() {
            if (stack.Count <= 1) throw new InvalidOperationException("OpacityStack underflow");
            stack.RemoveAt(stack.Count - 1);
        }

        public void Reset() {
            stack.Clear();
            stack.Add(1f);
        }
    }
}
