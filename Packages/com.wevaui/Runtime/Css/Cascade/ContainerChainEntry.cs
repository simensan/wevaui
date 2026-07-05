using Weva.Css.Container;

namespace Weva.Css.Cascade {
    // CSS Containment L3 §3 — represents one link in a nested @container chain.
    // The chain is ordered outermost-first; ContainerMatches walks from innermost
    // (last entry) outward when evaluating a nested @container rule hierarchy.
    // This struct is a support type for CompileRuleNestedChain in CascadeEngine.
    internal readonly struct ContainerChainEntry {
        public readonly ContainerQueryList Condition;
        public readonly string Name;

        public ContainerChainEntry(ContainerQueryList condition, string name) {
            Condition = condition;
            Name = name;
        }
    }
}
