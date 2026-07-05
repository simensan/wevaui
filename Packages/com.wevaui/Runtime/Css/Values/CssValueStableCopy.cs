using System.Collections.Generic;

namespace Weva.Css.Values {
    // Deep-clones a CssValue parse tree, replacing every CssLength / CssNumber /
    // CssPercentage leaf with a fresh non-pool-managed instance. CssValuePool
    // rents and Reset()s those three types between layout passes, so any
    // long-lived cache (ComputedStyle.parsedValues, CssValue.parseCache) that
    // stored a pool-rented reference would observe corrupted (value, unit)
    // fields on the NEXT pass after the pool reset. Cloning on insert breaks
    // the aliasing — the cached instance is owned by the cache, not by the
    // pool, and stays stable until the caller invalidates it.
    //
    // Containers (CssValueList, CssFunctionCall) hold child references that may
    // themselves be pool-mutable; we recurse so the entire tree is detached
    // from the pool's lifetime contract. Process-immutable leaf types
    // (CssKeyword, CssIdentifier, CssColor, CssString, CssUrl, CssRatio) pass
    // through unchanged. CssCalc also carries pool-mutable leaves inside its
    // CalcNode tree, so it must be cloned too; cached min()/max()/clamp()
    // values are evaluated long after the parse pool has recycled those leaves.
    internal static class CssValueStableCopy {
        public static CssValue Of(CssValue v) {
            if (v == null) return null;
            // Fast path: if the tree contains zero pool-mutable leaves, the
            // entire subtree is already stable across pool resets and we can
            // hand the original reference back without allocating. Profiling
            // showed CloneList / CloneFunctionCall accounting for most of
            // the per-frame List<CssValue> allocations even on styles whose
            // values were pure keyword lists (flex-flow, justify-content,
            // align-items, …). The pre-scan walks the same tree shape as the
            // clone path but never allocates.
            if (!ContainsPoolMutableLeaf(v)) return v;
            switch (v) {
                case CssLength l:
                    return new CssLength(l.Value, l.Unit, l.Raw);
                case CssNumber n:
                    return new CssNumber(n.Value, n.Raw);
                case CssPercentage p:
                    return new CssPercentage(p.Value, p.Raw);
                case CssValueList list:
                    return CloneList(list);
                case CssFunctionCall fn:
                    return CloneFunctionCall(fn);
                case CssCalc calc:
                    return new CssCalc(CloneCalcNode(calc.Expression), calc.Raw);
            }
            return v;
        }

        static bool ContainsPoolMutableLeaf(CssValue v) {
            if (v is CssLength || v is CssNumber || v is CssPercentage) return true;
            if (v is CssCalc calc) return ContainsPoolMutableLeaf(calc.Expression);
            if (v is CssValueList list) {
                var items = list.Items;
                for (int i = 0; i < items.Count; i++) {
                    if (ContainsPoolMutableLeaf(items[i])) return true;
                }
                return false;
            }
            if (v is CssFunctionCall fn) {
                var args = fn.Arguments;
                for (int i = 0; i < args.Count; i++) {
                    if (ContainsPoolMutableLeaf(args[i])) return true;
                }
                return false;
            }
            return false;
        }

        static CssValueList CloneList(CssValueList list) {
            var items = list.Items;
            int n = items.Count;
            var cloned = new List<CssValue>(n);
            for (int i = 0; i < n; i++) {
                cloned.Add(Of(items[i]));
            }
            return new CssValueList(cloned, list.Separator);
        }

        static CssFunctionCall CloneFunctionCall(CssFunctionCall fn) {
            var args = fn.Arguments;
            int n = args.Count;
            var cloned = new List<CssValue>(n);
            for (int i = 0; i < n; i++) {
                cloned.Add(Of(args[i]));
            }
            return new CssFunctionCall(fn.Name, cloned, fn.Raw);
        }

        static bool ContainsPoolMutableLeaf(CalcNode node) {
            switch (node) {
                case CalcLengthNode _:
                case CalcNumberNode _:
                case CalcPercentageNode _:
                    return true;
                case CalcVariableNode _:
                    return false;
                case CalcBinaryNode b:
                    return ContainsPoolMutableLeaf(b.Left) || ContainsPoolMutableLeaf(b.Right);
                case CalcMathNode m:
                    for (int i = 0; i < m.Args.Count; i++) {
                        if (ContainsPoolMutableLeaf(m.Args[i])) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        static CalcNode CloneCalcNode(CalcNode node) {
            switch (node) {
                case CalcLengthNode l:
                    return new CalcLengthNode(new CssLength(l.Length.Value, l.Length.Unit, l.Length.Raw));
                case CalcNumberNode n:
                    return new CalcNumberNode(new CssNumber(n.Number.Value, n.Number.Raw));
                case CalcPercentageNode p:
                    return new CalcPercentageNode(new CssPercentage(p.Percentage.Value, p.Percentage.Raw));
                case CalcVariableNode v:
                    return new CalcVariableNode(v.Variable);
                case CalcBinaryNode b:
                    return new CalcBinaryNode(b.Op, CloneCalcNode(b.Left), CloneCalcNode(b.Right));
                case CalcMathNode m: {
                    var args = new List<CalcNode>(m.Args.Count);
                    for (int i = 0; i < m.Args.Count; i++) {
                        args.Add(CloneCalcNode(m.Args[i]));
                    }
                    return new CalcMathNode(m.Function, args);
                }
                default:
                    return node;
            }
        }
    }
}
