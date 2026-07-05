using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Profiling;

namespace Weva.Compiled {
    // Two-stage matcher. Stage 1: SelectorIndex narrows a 50-rule sheet to a
    // small candidate set per element. Stage 2 (per candidate):
    //   - If SelectorShape.CanFastMatch, verify entirely against the snapshot
    //     using descendant/child combinator walks over Parent[] arrays.
    //   - Else delegate to the managed SelectorMatcher (covers attribute
    //     selectors, pseudo-classes, sibling combinators, :nth-*, :is/:not, etc).
    public static class SnapshotMatcher {
        internal static List<int> Match(DomSnapshot snapshot, int nodeId,
            SelectorIndex index, IReadOnlyList<CompiledSelector> selectors,
            IElementStateProvider state = null) {
            var result = new List<int>();
            var buf = new IntsBuffer();
            MatchInto(snapshot, nodeId, index, selectors, state, buf, result);
            return result;
        }

        internal static void MatchInto(DomSnapshot snapshot, int nodeId,
            SelectorIndex index, IReadOnlyList<CompiledSelector> selectors,
            IElementStateProvider state, IntsBuffer scratch, List<int> output) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.SnapshotSelectorMatch)) {
                if (snapshot.Kinds[nodeId] != NodeKind.Element) return;

                int tagSym = snapshot.TagSymbols[nodeId];
                int idSym = snapshot.IdSymbols[nodeId];
                ReadOnlySpan<int> classes = snapshot.ClassesOf(nodeId);

                int attrOff = snapshot.FirstAttribute[nodeId];
                int attrCnt = snapshot.AttributeCount[nodeId];
                ReadOnlySpan<int> attrNames = attrOff < 0
                    ? ReadOnlySpan<int>.Empty
                    : new ReadOnlySpan<int>(snapshot.AttributeNames, attrOff, attrCnt);

                var candidates = index.CandidateSelectors(tagSym, idSym, classes, scratch, attrNames);
                var span = candidates.AsSpan();

                Element managed = null;
                for (int i = 0; i < span.Length; i++) {
                    int sIdx = span[i];
                    ref readonly var shape = ref index.GetShape(sIdx);
                    if (shape.CanFastMatch) {
                        if (FastMatch(snapshot, nodeId, in shape, state)) output.Add(sIdx);
                        continue;
                    }
                    if (managed == null) {
                        managed = snapshot.ManagedNodes[nodeId] as Element;
                        if (managed == null) return;
                    }
                    if (SelectorMatcher.Matches(selectors[sIdx], managed, state)) output.Add(sIdx);
                }
            }
        }

        static bool FastMatch(DomSnapshot snap, int nodeId, in SelectorIndex.SelectorShape shape, IElementStateProvider state) {
            // Compounds[0] is the rightmost; verify it matches `nodeId` first.
            if (!CompoundMatches(snap, nodeId, shape.Compounds[0], state)) return false;
            int current = nodeId;
            int compoundCount = shape.Compounds.Length;
            for (int i = 1; i < compoundCount; i++) {
                var combinator = shape.Combinators[i - 1];
                var prev = shape.Compounds[i];
                if (combinator == Combinator.Child) {
                    int parent = snap.Parent[current];
                    if (parent < 0 || snap.Kinds[parent] != NodeKind.Element) return false;
                    if (!CompoundMatches(snap, parent, prev, state)) return false;
                    current = parent;
                } else if (combinator == Combinator.Descendant) {
                    bool found = false;
                    int p = snap.Parent[current];
                    while (p >= 0 && snap.Kinds[p] == NodeKind.Element) {
                        if (CompoundMatches(snap, p, prev, state)) {
                            current = p;
                            found = true;
                            break;
                        }
                        p = snap.Parent[p];
                    }
                    if (!found) return false;
                } else {
                    return false;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool CompoundMatches(DomSnapshot snap, int nodeId, in SelectorIndex.CompoundShape c, IElementStateProvider state) {
            if (c.TagSym != 0 && snap.TagSymbols[nodeId] != c.TagSym) return false;
            if (c.IdSym != 0 && snap.IdSymbols[nodeId] != c.IdSym) return false;
            if (c.RequiredState != ElementState.None) {
                // State pseudo-classes (:hover, :focus, ...) — check the
                // bitmask. Skip if no state provider; the legacy fallback
                // would have done the same.
                if (state == null) return false;
                if (snap.ManagedNodes[nodeId] is not Element managed) return false;
                if ((state.GetState(managed) & c.RequiredState) != c.RequiredState) return false;
            }
            var required = c.ClassSyms;
            if (required.Length > 0) {
                int classOff = snap.ClassRangeOffset[nodeId];
                int classCnt = snap.ClassRangeCount[nodeId];
                var classArr = snap.ClassSymbols;
                for (int i = 0; i < required.Length; i++) {
                    int needle = required[i];
                    bool found = false;
                    for (int j = 0; j < classCnt; j++) {
                        if (classArr[classOff + j] == needle) { found = true; break; }
                    }
                    if (!found) return false;
                }
            }
            var attrs = c.Attrs;
            if (attrs != null) {
                for (int i = 0; i < attrs.Length; i++) {
                    if (!AttributeMatches(snap, nodeId, attrs[i])) return false;
                }
            }
            return true;
        }

        static bool AttributeMatches(DomSnapshot snap, int nodeId, in SelectorIndex.AttrConstraint c) {
            int valueSym = snap.GetAttributeValue(nodeId, c.NameSym);
            if (c.Op == AttributeOperator.Exists) return ValueExists(snap, nodeId, c.NameSym);
            if (!ValueExists(snap, nodeId, c.NameSym)) return false;
            string v = snap.Symbols.Get(valueSym);
            if (v == null) return false;
            switch (c.Op) {
                case AttributeOperator.Equals: return valueSym == c.ValueSym;
                case AttributeOperator.WhitespaceContains:
                    return !string.IsNullOrEmpty(c.Value) && ContainsToken(v, c.Value);
                case AttributeOperator.DashMatch:
                    if (c.Value == null) return false;
                    if (v == c.Value) return true;
                    return v.StartsWith(c.DashPrefix, StringComparison.Ordinal);
                case AttributeOperator.Prefix:
                    return !string.IsNullOrEmpty(c.Value) && v.StartsWith(c.Value, StringComparison.Ordinal);
                case AttributeOperator.Suffix:
                    return !string.IsNullOrEmpty(c.Value) && v.EndsWith(c.Value, StringComparison.Ordinal);
                case AttributeOperator.Substring:
                    return !string.IsNullOrEmpty(c.Value) && v.IndexOf(c.Value, StringComparison.Ordinal) >= 0;
                default:
                    return false;
            }
        }

        static bool ValueExists(DomSnapshot snap, int nodeId, int nameSym) {
            int off = snap.FirstAttribute[nodeId];
            if (off < 0) return false;
            int cnt = snap.AttributeCount[nodeId];
            for (int i = 0; i < cnt; i++) {
                if (snap.AttributeNames[off + i] == nameSym) return true;
            }
            return false;
        }

        static bool ContainsToken(string s, string token) {
            if (string.IsNullOrEmpty(token)) return false;
            int len = s.Length, tlen = token.Length;
            int i = 0;
            while (i < len) {
                while (i < len && IsAsciiWs(s[i])) i++;
                int start = i;
                while (i < len && !IsAsciiWs(s[i])) i++;
                int wlen = i - start;
                if (wlen == tlen) {
                    bool match = true;
                    for (int k = 0; k < tlen; k++) {
                        if (s[start + k] != token[k]) { match = false; break; }
                    }
                    if (match) return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsAsciiWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    }
}
