using System;
using System.Collections.Generic;
using System.Text;

namespace Weva.Components.Scoping {
    public static class SelectorScoper {
        // Rewrites a selector list so that the rightmost compound of every selector
        // matches only inside elements carrying the scope attribute. Handles :host
        // and :host(...) by collapsing them onto the host marker attribute instead;
        // a :host(.a, .b) inner list expands into multiple output selectors.
        public static IReadOnlyList<string> Scope(string selectorList, string scopeId) {
            if (selectorList == null) throw new ArgumentNullException(nameof(selectorList));
            if (string.IsNullOrEmpty(scopeId)) throw new ArgumentException("scopeId required", nameof(scopeId));

            var output = new List<string>();
            foreach (var single in SplitTopLevelCommas(selectorList)) {
                string trimmed = single.Trim();
                if (trimmed.Length == 0) continue;
                ScopeSingle(trimmed, scopeId, output);
            }
            return output;
        }

        static void ScopeSingle(string selector, string scopeId, List<string> output) {
            var compounds = SplitCompounds(selector);
            var variants = new List<List<CompoundChunk>> { compounds };

            for (int i = 0; i < compounds.Count; i++) {
                var hostList = TryExtractHostAlternativeList(compounds[i].Text);
                if (hostList == null) continue;

                var expanded = new List<List<CompoundChunk>>(variants.Count * hostList.Count);
                for (int v = 0; v < variants.Count; v++) {
                    for (int h = 0; h < hostList.Count; h++) {
                        var clone = CloneCompounds(variants[v]);
                        clone[i] = new CompoundChunk(hostList[h], clone[i].LeadingCombinator);
                        expanded.Add(clone);
                    }
                }
                variants = expanded;
            }

            for (int i = 0; i < variants.Count; i++) {
                output.Add(EmitSelector(variants[i], scopeId));
            }
        }

        static List<CompoundChunk> CloneCompounds(List<CompoundChunk> src) {
            var copy = new List<CompoundChunk>(src.Count);
            for (int i = 0; i < src.Count; i++) copy.Add(src[i]);
            return copy;
        }

        // Re-emits the selector. Each compound is rewritten:
        // - If it begins with :host or :host(...), use host-marker form.
        // - Otherwise, the rightmost compound has [data-uui-scope="..."] inserted
        //   before any trailing pseudo-element.
        static string EmitSelector(List<CompoundChunk> compounds, string scopeId) {
            var sb = new StringBuilder();
            int last = compounds.Count - 1;
            for (int i = 0; i < compounds.Count; i++) {
                if (i > 0) sb.Append(compounds[i].LeadingCombinator);
                string compoundOut;
                bool isHost = TryRewriteHost(compounds[i].Text, scopeId, out var hostText);
                if (isHost) {
                    compoundOut = hostText;
                } else if (i == last) {
                    compoundOut = AppendScopeAttr(compounds[i].Text, scopeId);
                } else {
                    compoundOut = compounds[i].Text;
                }
                sb.Append(compoundOut);
            }
            return sb.ToString();
        }

        // Splits a selector into compound chunks separated by combinators. Returns
        // a list where item[0].LeadingCombinator is empty and subsequent items'
        // LeadingCombinator captures the whitespace+combinator+whitespace text that
        // preceded them (so re-assembling preserves source-style spacing for
        // descendant selectors).
        static List<CompoundChunk> SplitCompounds(string selector) {
            var result = new List<CompoundChunk>();
            int i = 0;
            int n = selector.Length;

            int compoundStart = -1;
            string pendingCombinator = "";

            while (i < n) {
                char c = selector[i];
                if (compoundStart < 0) {
                    if (IsWhitespace(c)) { i++; continue; }
                    if (c == '>' || c == '+' || c == '~') {
                        // Standalone combinator at start (shouldn't happen for valid
                        // input but keep safe).
                        i++;
                        continue;
                    }
                    compoundStart = i;
                    if (c == '[' || c == '(') {
                        i = SkipBalanced(selector, i);
                    } else if (c == '"' || c == '\'') {
                        i = SkipString(selector, i);
                    } else {
                        i++;
                    }
                    continue;
                }
                // Inside a compound. Skip balanced parens/brackets/strings.
                if (c == '(' || c == '[') {
                    i = SkipBalanced(selector, i);
                    continue;
                }
                if (c == '"' || c == '\'') {
                    i = SkipString(selector, i);
                    continue;
                }
                if (IsWhitespace(c) || c == '>' || c == '+' || c == '~') {
                    int compoundEnd = i;
                    // Record this compound.
                    result.Add(new CompoundChunk(selector.Substring(compoundStart, compoundEnd - compoundStart), pendingCombinator));
                    compoundStart = -1;

                    // Scan whitespace + optional explicit combinator.
                    bool hasExplicit = false;
                    char explicitChar = '\0';
                    while (i < n) {
                        char cc = selector[i];
                        if (IsWhitespace(cc)) { i++; continue; }
                        if (!hasExplicit && (cc == '>' || cc == '+' || cc == '~')) {
                            hasExplicit = true;
                            explicitChar = cc;
                            i++;
                            continue;
                        }
                        break;
                    }
                    if (hasExplicit) {
                        pendingCombinator = " " + explicitChar + " ";
                    } else {
                        pendingCombinator = " ";
                    }
                    continue;
                }
                i++;
            }

            if (compoundStart >= 0) {
                result.Add(new CompoundChunk(selector.Substring(compoundStart, n - compoundStart), pendingCombinator));
            }

            return result;
        }

        static int SkipBalanced(string s, int start) {
            char open = s[start];
            char close = open == '(' ? ')' : (open == '[' ? ']' : '}');
            int depth = 0;
            int i = start;
            while (i < s.Length) {
                char c = s[i];
                if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                if (c == open) depth++;
                else if (c == close) {
                    depth--;
                    if (depth == 0) return i + 1;
                }
                i++;
            }
            return s.Length;
        }

        static int SkipString(string s, int start) {
            char q = s[start];
            int i = start + 1;
            while (i < s.Length) {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { i += 2; continue; }
                if (c == q) return i + 1;
                i++;
            }
            return s.Length;
        }

        // Attempts to recognise a compound whose left-most simple selector is :host
        // or :host(...) and rewrite it. Returns true and `output` contains the
        // rewritten compound. The rest of the compound (anything after the :host
        // bit, e.g. .foo in `:host.foo`) is preserved as-is and concatenated.
        static bool TryRewriteHost(string compound, string scopeId, out string output) {
            output = null;
            if (!StartsWith(compound, ":host")) return false;
            int after = 5; // length of ":host"
            string suffix;
            string innerInsertion = "";
            if (after < compound.Length && compound[after] == '(') {
                int close = SkipBalanced(compound, after); // index after ')'
                string inner = compound.Substring(after + 1, close - after - 2).Trim();
                suffix = compound.Substring(close);
                // If inner contains a top-level comma, this compound should already
                // have been comma-expanded by TryExtractHostAlternativeList.
                if (ContainsTopLevelComma(inner)) {
                    // Defensive fallback: keep one selector with the first alternative.
                    var alts = SplitTopLevelCommas(inner);
                    inner = alts[0].Trim();
                }
                innerInsertion = inner;
            } else {
                suffix = compound.Substring(after);
            }
            // Construct: [data-uui-host="<id>"] + innerInsertion + suffix
            var sb = new StringBuilder();
            sb.Append('[').Append(ScopeMarkers.HostAttribute).Append("=\"").Append(scopeId).Append("\"]");
            sb.Append(innerInsertion);
            sb.Append(suffix);
            output = sb.ToString();
            return true;
        }

        // If the compound is :host(.a, .b) and there are multiple alternatives,
        // returns the list of compounds [:host(.a), :host(.b)] (with surrounding
        // text after the host paren preserved on each). Returns null if not :host
        // with a comma-bearing argument list.
        static List<string> TryExtractHostAlternativeList(string compound) {
            if (!StartsWith(compound, ":host")) return null;
            int after = 5;
            if (after >= compound.Length || compound[after] != '(') return null;
            int close = SkipBalanced(compound, after);
            string inner = compound.Substring(after + 1, close - after - 2);
            if (!ContainsTopLevelComma(inner)) return null;
            string suffix = compound.Substring(close); // anything after the ')'
            var alts = SplitTopLevelCommas(inner);
            var result = new List<string>();
            foreach (var alt in alts) {
                string a = alt.Trim();
                if (a.Length == 0) continue;
                result.Add(":host(" + a + ")" + suffix);
            }
            return result;
        }

        // Inserts the scope attribute into a compound. The attribute slots in just
        // before the first top-level pseudo-class or pseudo-element so the attribute
        // matches against the originating element rather than a pseudo. If the
        // compound starts with a pseudo (e.g. :not(...)) or has none at all, the
        // attribute is appended at the end.
        static string AppendScopeAttr(string compound, string scopeId) {
            int peIdx = FindPseudoStart(compound);
            string scopeAttr = "[" + ScopeMarkers.ScopeAttribute + "=\"" + scopeId + "\"]";
            if (peIdx <= 0) {
                return compound + scopeAttr;
            }
            return compound.Substring(0, peIdx) + scopeAttr + compound.Substring(peIdx);
        }

        // Returns the index of the first ':' (single or double colon) at top level
        // (depth 0 outside parens/brackets/strings), or -1 if none. The scope
        // attribute is inserted at this index so it precedes pseudo-classes and
        // pseudo-elements alike.
        static int FindPseudoStart(string compound) {
            int i = 0;
            while (i < compound.Length) {
                char c = compound[i];
                if (c == '(' || c == '[') { i = SkipBalanced(compound, i); continue; }
                if (c == '"' || c == '\'') { i = SkipString(compound, i); continue; }
                if (c == ':') return i;
                i++;
            }
            return -1;
        }

        static List<string> SplitTopLevelCommas(string s) {
            var parts = new List<string>();
            int start = 0;
            int i = 0;
            int n = s.Length;
            while (i < n) {
                char c = s[i];
                if (c == '(' || c == '[') { i = SkipBalanced(s, i); continue; }
                if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                if (c == ',') {
                    parts.Add(s.Substring(start, i - start));
                    i++;
                    start = i;
                    continue;
                }
                i++;
            }
            if (start <= n) parts.Add(s.Substring(start, n - start));
            return parts;
        }

        static bool ContainsTopLevelComma(string s) {
            int i = 0;
            int n = s.Length;
            while (i < n) {
                char c = s[i];
                if (c == '(' || c == '[') { i = SkipBalanced(s, i); continue; }
                if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                if (c == ',') return true;
                i++;
            }
            return false;
        }

        static bool StartsWith(string s, string prefix) {
            if (s.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) {
                if (s[i] != prefix[i]) return false;
            }
            return true;
        }

        static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        readonly struct CompoundChunk {
            public readonly string Text;
            public readonly string LeadingCombinator;
            public CompoundChunk(string text, string leadingCombinator) {
                Text = text;
                LeadingCombinator = leadingCombinator;
            }
        }
    }
}
