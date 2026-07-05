namespace Weva.Css.Selectors {
    internal readonly struct NthExpression {
        public readonly int A;
        public readonly int B;

        public NthExpression(int a, int b) {
            A = a;
            B = b;
        }

        public bool Matches(int index) {
            if (A == 0) return index == B;
            int diff = index - B;
            if (diff == 0) return true;
            if (A > 0) return diff > 0 && diff % A == 0;
            return diff < 0 && (-diff) % (-A) == 0;
        }
    }
}
