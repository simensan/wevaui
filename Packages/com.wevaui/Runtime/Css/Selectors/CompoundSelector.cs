using System.Collections.Generic;

namespace Weva.Css.Selectors {
    internal sealed class CompoundSelector {
        public List<SimpleSelector> Parts { get; } = new();
        public string PseudoElement { get; set; }

        public Specificity Specificity {
            get {
                var s = Specificity.Zero;
                foreach (var p in Parts) s = Specificity.Add(s, p.Specificity);
                if (PseudoElement != null) s = Specificity.Add(s, new Specificity(0, 0, 1));
                return s;
            }
        }
    }

    internal sealed class CompoundSequence {
        public List<CompoundSelector> Compounds { get; } = new();
        public List<Combinator> Combinators { get; } = new();

        public Specificity Specificity {
            get {
                var s = Specificity.Zero;
                foreach (var c in Compounds) s = Specificity.Add(s, c.Specificity);
                return s;
            }
        }

        public string PseudoElement {
            get {
                if (Compounds.Count == 0) return null;
                return Compounds[Compounds.Count - 1].PseudoElement;
            }
        }
    }
}
