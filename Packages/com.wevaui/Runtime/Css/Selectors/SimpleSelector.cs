using System.Collections.Generic;

namespace Weva.Css.Selectors {
    internal abstract class SimpleSelector {
        public abstract Specificity Specificity { get; }
    }

    internal sealed class UniversalSelector : SimpleSelector {
        public static readonly UniversalSelector Instance = new();
        public override Specificity Specificity => Specificity.Zero;
    }

    internal sealed class TypeSelector : SimpleSelector {
        public string TagName { get; }
        public TypeSelector(string tagName) { TagName = tagName; }
        public override Specificity Specificity => new(0, 0, 1);
    }

    internal sealed class IdSelector : SimpleSelector {
        public string Id { get; }
        public IdSelector(string id) { Id = id; }
        public override Specificity Specificity => new(1, 0, 0);
    }

    internal sealed class ClassSelector : SimpleSelector {
        public string ClassName { get; }
        public ClassSelector(string className) { ClassName = className; }
        public override Specificity Specificity => new(0, 1, 0);
    }

    internal sealed class AttributeSelector : SimpleSelector {
        public string Name { get; }
        public AttributeOperator Operator { get; }
        public string Value { get; }
        public string DashPrefix { get; }
        public bool CaseInsensitive { get; }

        public AttributeSelector(string name, AttributeOperator op, string value, bool caseInsensitive = false) {
            Name = name;
            Operator = op;
            Value = value;
            DashPrefix = op == AttributeOperator.DashMatch && value != null ? value + "-" : null;
            CaseInsensitive = caseInsensitive;
        }

        public override Specificity Specificity => new(0, 1, 0);
    }

    internal sealed class PseudoClassSelector : SimpleSelector {
        public PseudoClassKind Kind { get; }
        public NthExpression Nth { get; }
        public SimpleSelector InnerSimple { get; }
        public List<CompoundSequence> InnerList { get; }
        public List<CompoundSequence> NthOfFilter { get; }
        public string Argument { get; }

        public PseudoClassSelector(PseudoClassKind kind) {
            Kind = kind;
        }

        public PseudoClassSelector(PseudoClassKind kind, NthExpression nth) {
            Kind = kind;
            Nth = nth;
        }

        public PseudoClassSelector(PseudoClassKind kind, NthExpression nth, List<CompoundSequence> nthOfFilter) {
            Kind = kind;
            Nth = nth;
            NthOfFilter = nthOfFilter;
        }

        public PseudoClassSelector(PseudoClassKind kind, SimpleSelector inner) {
            Kind = kind;
            InnerSimple = inner;
        }

        public PseudoClassSelector(PseudoClassKind kind, List<CompoundSequence> innerList) {
            Kind = kind;
            InnerList = innerList;
        }

        public PseudoClassSelector(PseudoClassKind kind, string argument) {
            Kind = kind;
            Argument = argument;
        }

        public override Specificity Specificity {
            get {
                switch (Kind) {
                    case PseudoClassKind.Where:
                        return Specificity.Zero;
                    case PseudoClassKind.Not:
                    case PseudoClassKind.Is:
                    case PseudoClassKind.Has: {
                        // CSS Selectors L4 §5.7: :not(), :has() and :is() all
                        // use the specificity of the highest-specificity
                        // selector in their argument list. (:not used to be
                        // simple-selector-only and read InnerSimple; #258
                        // moved it to the same list-based path as :is/:has,
                        // but keep the InnerSimple fallback for any legacy
                        // callers that constructed PseudoClassSelector
                        // directly.)
                        var max = Specificity.Zero;
                        if (InnerList != null) {
                            foreach (var c in InnerList) max = Specificity.Max(max, c.Specificity);
                        } else if (InnerSimple != null) {
                            max = InnerSimple.Specificity;
                        }
                        return max;
                    }
                    case PseudoClassKind.NthChild:
                    case PseudoClassKind.NthLastChild: {
                        // CSS Selectors L4 §6.6.5: `:nth-child(An+B of S)` has
                        // the specificity of :nth-child(An+B) — (0,1,0) — PLUS
                        // the max specificity of the selector list S.  The
                        // base (0,1,0) is retained when no filter is present.
                        var nth = new Specificity(0, 1, 0);
                        if (NthOfFilter == null) return nth;
                        var filterMax = Specificity.Zero;
                        foreach (var c in NthOfFilter) filterMax = Specificity.Max(filterMax, c.Specificity);
                        return Specificity.Add(nth, filterMax);
                    }
                    default:
                        return new Specificity(0, 1, 0);
                }
            }
        }
    }
}
