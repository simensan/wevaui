using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Weva.Dom;

namespace Weva.Css.Selectors {
    public static class SelectorMatcher {
        public static bool Matches(CompiledSelector sel, Element e, IElementStateProvider state = null, Element scopeRoot = null) {
            if (sel == null || e == null) return false;
            if (sel.PseudoElement != null) return false;
            return MatchSequence(sel.Sequence, e, state ?? NullStateProvider.Instance, scopeRoot);
        }

        // Match a selector whose rightmost compound carries a pseudo-element marker
        // against the originating element of that pseudo-element. The pseudo-element
        // name on the selector must equal `pseudoName`; the compound sequence is then
        // matched against `host` ignoring the pseudo flag. Used by the cascade's
        // `::backdrop` path so a rule like `dialog[open]::backdrop` matches the open
        // dialog element when computing its backdrop's style.
        public static bool MatchesPseudoElement(CompiledSelector sel, string pseudoName, Element host, IElementStateProvider state = null, Element scopeRoot = null) {
            if (sel == null || host == null || pseudoName == null) return false;
            if (sel.PseudoElement != pseudoName) return false;
            return MatchSequenceIgnoringPseudo(sel.Sequence, host, state ?? NullStateProvider.Instance, scopeRoot);
        }

        internal static bool MatchSequence(CompoundSequence seq, Element e, IElementStateProvider state, Element scopeRoot = null) {
            int i = seq.Compounds.Count - 1;
            if (!MatchCompound(seq.Compounds[i], e, state, scopeRoot)) return false;
            var current = e;
            while (i > 0) {
                var combinator = seq.Combinators[i - 1];
                var prev = seq.Compounds[i - 1];
                switch (combinator) {
                    case Combinator.Descendant: {
                        bool found = false;
                        var p = current.Parent as Element;
                        while (p != null) {
                            if (MatchCompound(prev, p, state, scopeRoot)) {
                                current = p;
                                found = true;
                                break;
                            }
                            p = p.Parent as Element;
                        }
                        if (!found) return false;
                        break;
                    }
                    case Combinator.Child: {
                        var p = current.Parent as Element;
                        if (p == null || !MatchCompound(prev, p, state, scopeRoot)) return false;
                        current = p;
                        break;
                    }
                    case Combinator.AdjacentSibling: {
                        var sib = PreviousElementSibling(current);
                        if (sib == null || !MatchCompound(prev, sib, state, scopeRoot)) return false;
                        current = sib;
                        break;
                    }
                    case Combinator.GeneralSibling: {
                        bool found = false;
                        var sib = PreviousElementSibling(current);
                        while (sib != null) {
                            if (MatchCompound(prev, sib, state, scopeRoot)) {
                                current = sib;
                                found = true;
                                break;
                            }
                            sib = PreviousElementSibling(sib);
                        }
                        if (!found) return false;
                        break;
                    }
                    default:
                        return false;
                }
                i--;
            }
            return true;
        }

        internal static bool MatchCompound(CompoundSelector compound, Element e, IElementStateProvider state, Element scopeRoot = null) {
            if (compound.PseudoElement != null) return false;
            foreach (var part in compound.Parts) {
                if (!MatchSimple(part, e, state, scopeRoot)) return false;
            }
            return true;
        }

        // Variant of MatchSequence that allows the rightmost compound to carry a
        // pseudo-element flag (which MatchCompound otherwise rejects). The caller —
        // CascadeEngine's backdrop path — has already verified the pseudo-element
        // name; here we just need the structural match against the originating
        // element. Non-rightmost compounds still reject any pseudo-element flag.
        internal static bool MatchSequenceIgnoringPseudo(CompoundSequence seq, Element e, IElementStateProvider state, Element scopeRoot = null) {
            int i = seq.Compounds.Count - 1;
            if (!MatchCompoundIgnoringPseudo(seq.Compounds[i], e, state, scopeRoot)) return false;
            var current = e;
            while (i > 0) {
                var combinator = seq.Combinators[i - 1];
                var prev = seq.Compounds[i - 1];
                switch (combinator) {
                    case Combinator.Descendant: {
                        bool found = false;
                        var p = current.Parent as Element;
                        while (p != null) {
                            if (MatchCompound(prev, p, state, scopeRoot)) {
                                current = p;
                                found = true;
                                break;
                            }
                            p = p.Parent as Element;
                        }
                        if (!found) return false;
                        break;
                    }
                    case Combinator.Child: {
                        var p = current.Parent as Element;
                        if (p == null || !MatchCompound(prev, p, state, scopeRoot)) return false;
                        current = p;
                        break;
                    }
                    case Combinator.AdjacentSibling: {
                        var sib = PreviousElementSibling(current);
                        if (sib == null || !MatchCompound(prev, sib, state, scopeRoot)) return false;
                        current = sib;
                        break;
                    }
                    case Combinator.GeneralSibling: {
                        bool found = false;
                        var sib = PreviousElementSibling(current);
                        while (sib != null) {
                            if (MatchCompound(prev, sib, state, scopeRoot)) {
                                current = sib;
                                found = true;
                                break;
                            }
                            sib = PreviousElementSibling(sib);
                        }
                        if (!found) return false;
                        break;
                    }
                    default:
                        return false;
                }
                i--;
            }
            return true;
        }

        internal static bool MatchCompoundIgnoringPseudo(CompoundSelector compound, Element e, IElementStateProvider state, Element scopeRoot = null) {
            foreach (var part in compound.Parts) {
                if (!MatchSimple(part, e, state, scopeRoot)) return false;
            }
            return true;
        }

        internal static bool MatchSimple(SimpleSelector part, Element e, IElementStateProvider state, Element scopeRoot = null) {
            switch (part) {
                case UniversalSelector _:
                    return true;
                case TypeSelector ts:
                    return e.TagName == ts.TagName;
                case IdSelector ids:
                    return e.Id == ids.Id;
                case ClassSelector cs:
                    return HasClass(e, cs.ClassName);
                case AttributeSelector at:
                    return MatchAttribute(at, e);
                case PseudoClassSelector pc:
                    return MatchPseudo(pc, e, state, scopeRoot);
                default:
                    return false;
            }
        }

        static bool HasClass(Element e, string name) {
            var raw = e.ClassName;
            if (string.IsNullOrEmpty(raw)) return false;
            return ContainsToken(raw, name);
        }

        static bool ContainsToken(string s, string token) => ContainsToken(s, token, false);

        static bool ContainsToken(string s, string token, bool ignoreCase) {
            if (string.IsNullOrEmpty(token)) return false;
            int len = s.Length, tlen = token.Length;
            int i = 0;
            while (i < len) {
                while (i < len && IsAsciiWhitespace(s[i])) i++;
                int start = i;
                while (i < len && !IsAsciiWhitespace(s[i])) i++;
                int wlen = i - start;
                if (wlen == tlen) {
                    bool match = true;
                    for (int k = 0; k < tlen; k++) {
                        char a = s[start + k];
                        char b = token[k];
                        if (ignoreCase) {
                            if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                            if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                        }
                        if (a != b) { match = false; break; }
                    }
                    if (match) return true;
                }
            }
            return false;
        }

        static bool IsAsciiWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        static bool MatchAttribute(AttributeSelector at, Element e) {
            if (!e.HasAttribute(at.Name)) return false;
            var v = e.GetAttribute(at.Name);
            if (v == null) return false;
            var cmp = at.CaseInsensitive
                ? System.StringComparison.OrdinalIgnoreCase
                : System.StringComparison.Ordinal;
            switch (at.Operator) {
                case AttributeOperator.Exists:
                    return true;
                case AttributeOperator.Equals:
                    return string.Equals(v, at.Value, cmp);
                case AttributeOperator.WhitespaceContains:
                    if (string.IsNullOrEmpty(at.Value)) return false;
                    return ContainsToken(v, at.Value, at.CaseInsensitive);
                case AttributeOperator.DashMatch:
                    if (at.Value == null) return false;
                    // CSS Selectors L4 §6.3.2: `[attr|=""]` matches only when
                    // attr equals empty string. The DashPrefix for an empty
                    // value is just "-", which would otherwise match every
                    // value starting with `-` (e.g., `data-foo="-bar"`).
                    if (at.Value.Length == 0) return v.Length == 0;
                    if (string.Equals(v, at.Value, cmp)) return true;
                    // Per CSS Selectors L4 §6.3, attribute substring operators
                    // are CODE-POINT comparisons (ordinal). The single-arg
                    // string overloads default to CurrentCulture and would
                    // fold characters per locale (Turkish dotted/dotless i,
                    // German ß↔SS, ligatures) — pin ordinal so cross-locale
                    // selector behavior matches the spec.
                    return v.StartsWith(at.DashPrefix, cmp);
                case AttributeOperator.Prefix:
                    if (string.IsNullOrEmpty(at.Value)) return false;
                    return v.StartsWith(at.Value, cmp);
                case AttributeOperator.Suffix:
                    if (string.IsNullOrEmpty(at.Value)) return false;
                    return v.EndsWith(at.Value, cmp);
                case AttributeOperator.Substring:
                    if (string.IsNullOrEmpty(at.Value)) return false;
                    return v.IndexOf(at.Value, cmp) >= 0;
                default:
                    return false;
            }
        }

        static bool MatchPseudo(PseudoClassSelector pc, Element e, IElementStateProvider state, Element scopeRoot = null) {
            switch (pc.Kind) {
                case PseudoClassKind.FirstChild:
                    return PreviousElementSibling(e) == null && e.Parent is Element;
                case PseudoClassKind.LastChild:
                    return NextElementSibling(e) == null && e.Parent is Element;
                case PseudoClassKind.OnlyChild:
                    return PreviousElementSibling(e) == null && NextElementSibling(e) == null && e.Parent is Element;
                case PseudoClassKind.FirstOfType:
                    return PreviousElementSiblingOfType(e, e.TagName) == null && e.Parent is Element;
                case PseudoClassKind.LastOfType:
                    return NextElementSiblingOfType(e, e.TagName) == null && e.Parent is Element;
                case PseudoClassKind.OnlyOfType:
                    return PreviousElementSiblingOfType(e, e.TagName) == null
                        && NextElementSiblingOfType(e, e.TagName) == null
                        && e.Parent is Element;
                case PseudoClassKind.NthChild: {
                    if (!(e.Parent is Element)) return false;
                    if (pc.NthOfFilter != null) {
                        int idx = FilteredChildIndex(e, pc.NthOfFilter, state, scopeRoot, fromEnd: false);
                        return idx > 0 && pc.Nth.Matches(idx);
                    }
                    int childIdx = ChildIndex(e);
                    return pc.Nth.Matches(childIdx);
                }
                case PseudoClassKind.NthLastChild: {
                    if (!(e.Parent is Element)) return false;
                    if (pc.NthOfFilter != null) {
                        int idx = FilteredChildIndex(e, pc.NthOfFilter, state, scopeRoot, fromEnd: true);
                        return idx > 0 && pc.Nth.Matches(idx);
                    }
                    int childIdx = ChildIndexFromEnd(e);
                    return pc.Nth.Matches(childIdx);
                }
                case PseudoClassKind.NthOfType: {
                    if (!(e.Parent is Element)) return false;
                    int idx = ChildIndexOfType(e);
                    return pc.Nth.Matches(idx);
                }
                case PseudoClassKind.NthLastOfType: {
                    if (!(e.Parent is Element)) return false;
                    int idx = ChildIndexOfTypeFromEnd(e);
                    return pc.Nth.Matches(idx);
                }
                case PseudoClassKind.Empty:
                    return e.Children.Count == 0;
                case PseudoClassKind.Not:
                    // CSS Selectors L4 §6.2 — `:not(<complex-selector-list>)`
                    // matches when NONE of the listed selectors match. Fixed
                    // in #258. InnerSimple is kept for legacy callers; new
                    // parses come through InnerList.
                    if (pc.InnerList != null) {
                        foreach (var seq in pc.InnerList) {
                            if (MatchSequence(seq, e, state, scopeRoot)) return false;
                        }
                        return true;
                    }
                    return pc.InnerSimple != null && !MatchSimple(pc.InnerSimple, e, state, scopeRoot);
                case PseudoClassKind.Is:
                case PseudoClassKind.Where: {
                    if (pc.InnerList == null) return false;
                    foreach (var seq in pc.InnerList) {
                        if (MatchSequence(seq, e, state, scopeRoot)) return true;
                    }
                    return false;
                }
                case PseudoClassKind.Has: {
                    if (pc.InnerList == null) return false;
                    return MatchHas(pc.InnerList, e, state, scopeRoot);
                }
                case PseudoClassKind.Lang:
                    return MatchesLanguage(e, pc.Argument);
                case PseudoClassKind.Dir:
                    return MatchesDirection(e, pc.Argument);
                case PseudoClassKind.Link:
                    return IsHyperlink(e) && !IsVisited(e, state);
                case PseudoClassKind.Visited:
                    return IsHyperlink(e) && IsVisited(e, state);
                case PseudoClassKind.AnyLink:
                    return IsHyperlink(e);
                case PseudoClassKind.Target:
                    return (state.GetState(e) & ElementState.Target) != 0;
                case PseudoClassKind.Scope:
                    return scopeRoot != null ? ReferenceEquals(e, scopeRoot) : IsRootElement(e);
                case PseudoClassKind.Hover:
                    return (state.GetState(e) & ElementState.Hover) != 0;
                case PseudoClassKind.Focus:
                    return (state.GetState(e) & ElementState.Focus) != 0;
                case PseudoClassKind.FocusVisible:
                    return (state.GetState(e) & ElementState.FocusVisible) != 0;
                case PseudoClassKind.FocusWithin:
                    return (state.GetState(e) & ElementState.FocusWithin) != 0;
                case PseudoClassKind.Active:
                    return (state.GetState(e) & ElementState.Active) != 0;
                case PseudoClassKind.Disabled:
                    return IsDisabled(e, state);
                case PseudoClassKind.Enabled:
                    return IsFormControl(e) && !IsDisabled(e, state);
                case PseudoClassKind.Checked:
                    return (state.GetState(e) & ElementState.Checked) != 0 || e.HasAttribute("checked");
                case PseudoClassKind.Required:
                    return IsRequiredCandidate(e) && e.HasAttribute("required");
                case PseudoClassKind.Optional:
                    return IsRequiredCandidate(e) && !e.HasAttribute("required");
                case PseudoClassKind.ReadOnly:
                    return !IsReadWrite(e, state);
                case PseudoClassKind.ReadWrite:
                    return IsReadWrite(e, state);
                case PseudoClassKind.Valid:
                    return IsValidationCandidate(e, state) && !IsInvalid(e, state);
                case PseudoClassKind.Invalid:
                    return IsValidationCandidate(e, state) && IsInvalid(e, state);
                case PseudoClassKind.InRange:
                    return IsRangeCandidate(e, state) && !IsOutOfRange(e);
                case PseudoClassKind.OutOfRange:
                    return IsRangeCandidate(e, state) && IsOutOfRange(e);
                case PseudoClassKind.UserValid:
                    return IsValidationCandidate(e, state)
                        && HasUserInteracted(e, state)
                        && !IsInvalid(e, state);
                case PseudoClassKind.UserInvalid:
                    return IsValidationCandidate(e, state)
                        && HasUserInteracted(e, state)
                        && IsInvalid(e, state);
                case PseudoClassKind.Default:
                    return IsDefaultElement(e);
                case PseudoClassKind.PlaceholderShown:
                    return (state.GetState(e) & ElementState.PlaceholderShown) != 0;
                case PseudoClassKind.PopoverOpen:
                    return e.HasAttribute("data-popover-open");
                case PseudoClassKind.Modal:
                    return e.HasAttribute("data-modal");
                case PseudoClassKind.Autofill:
                    return (state.GetState(e) & ElementState.Autofill) != 0;
                case PseudoClassKind.Root:
                    return IsRootElement(e);
                default:
                    return false;
            }
        }

        static bool IsHyperlink(Element e) {
            if (e == null || !e.HasAttribute("href")) return false;
            return e.TagName == "a" || e.TagName == "area";
        }

        static bool IsVisited(Element e, IElementStateProvider state) {
            // Weva has no browser history store. Per the browser initial
            // state, authored links are unvisited until a host integrates a
            // history provider, so :visited never matches in the core engine.
            return false;
        }

        static bool MatchesLanguage(Element e, string argument) {
            string lang = InheritedLanguage(e);
            if (string.IsNullOrEmpty(lang) || string.IsNullOrWhiteSpace(argument)) return false;
            lang = lang.Trim();
            foreach (var rawRange in SplitPseudoArgumentList(argument)) {
                string range = Unquote(rawRange).Trim();
                if (range.Length == 0) continue;
                if (range == "*") return true;
                if (lang.Equals(range, StringComparison.OrdinalIgnoreCase)) return true;
                if (lang.Length > range.Length
                    && lang[range.Length] == '-'
                    && lang.StartsWith(range, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        static string InheritedLanguage(Element e) {
            for (var cur = e; cur != null; cur = cur.Parent as Element) {
                string lang = cur.GetAttribute("lang");
                if (string.IsNullOrEmpty(lang)) lang = cur.GetAttribute("xml:lang");
                if (!string.IsNullOrWhiteSpace(lang)) return lang;
            }
            return null;
        }

        static bool MatchesDirection(Element e, string argument) {
            string arg = Unquote(argument).Trim();
            if (arg.Equals("auto", StringComparison.OrdinalIgnoreCase)) {
                return InheritedDirAttributeIsAuto(e);
            }
            if (!arg.Equals("ltr", StringComparison.OrdinalIgnoreCase)
                && !arg.Equals("rtl", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            return ResolveDirection(e).Equals(arg, StringComparison.OrdinalIgnoreCase);
        }

        static bool InheritedDirAttributeIsAuto(Element e) {
            for (var cur = e; cur != null; cur = cur.Parent as Element) {
                string raw = cur.GetAttribute("dir");
                if (string.IsNullOrWhiteSpace(raw)) continue;
                return raw.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        static string ResolveDirection(Element e) {
            for (var cur = e; cur != null; cur = cur.Parent as Element) {
                string raw = cur.GetAttribute("dir");
                if (string.IsNullOrWhiteSpace(raw)) continue;
                raw = raw.Trim();
                if (raw.Equals("ltr", StringComparison.OrdinalIgnoreCase)) return "ltr";
                if (raw.Equals("rtl", StringComparison.OrdinalIgnoreCase)) return "rtl";
                if (raw.Equals("auto", StringComparison.OrdinalIgnoreCase)) {
                    string autoDir = FirstStrongDirection(cur);
                    return autoDir ?? "ltr";
                }
            }
            return "ltr";
        }

        static string FirstStrongDirection(Node node) {
            if (node is TextNode text) {
                string data = text.Data;
                for (int i = 0; i < data.Length; ) {
                    int cp;
                    if (char.IsHighSurrogate(data[i]) && i + 1 < data.Length && char.IsLowSurrogate(data[i + 1])) {
                        cp = char.ConvertToUtf32(data[i], data[i + 1]);
                        i += 2;
                    } else {
                        cp = data[i];
                        i++;
                    }
                    if (IsRtlCodepoint(cp)) return "rtl";
                    if (IsLtrCodepoint(cp)) return "ltr";
                }
            }
            foreach (var child in node.Children) {
                if (child is Element ce && IsBidiOpaqueForAutoScan(ce)) continue;
                string dir = FirstStrongDirection(child);
                if (dir != null) return dir;
            }
            return null;
        }

        // HTML directionality algorithm: when auto-resolving the direction of
        // an ancestor, descendants that establish their own directionality
        // context don't contribute their text. That includes `<bdi>` (auto by
        // default), any descendant with its own `dir` attribute, and elements
        // with no rendered text (`<script>`, `<style>`).
        static bool IsBidiOpaqueForAutoScan(Element e) {
            if (e.TagName == "bdi" || e.TagName == "script" || e.TagName == "style") return true;
            return e.HasAttribute("dir");
        }

        static bool IsRtlCodepoint(int c) {
            return (c >= 0x0590 && c <= 0x08FF)
                || (c >= 0xFB1D && c <= 0xFDFF)
                || (c >= 0xFE70 && c <= 0xFEFF)
                || (c >= 0x10800 && c <= 0x10FFF)
                || (c >= 0x1E800 && c <= 0x1EFFF);
        }

        static bool IsLtrCodepoint(int c) {
            return (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= 0x00C0 && c <= 0x02AF)
                || (c >= 0x0370 && c <= 0x058F);
        }

        static string[] SplitPseudoArgumentList(string raw) {
            if (string.IsNullOrEmpty(raw)) return new string[] { "" };
            var parts = new System.Collections.Generic.List<string>();
            int start = 0;
            char quote = '\0';
            for (int i = 0; i < raw.Length; i++) {
                char c = raw[i];
                if (quote != '\0') {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == ',') {
                    parts.Add(raw.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(raw.Substring(start));
            return parts.ToArray();
        }

        static string Unquote(string raw) {
            if (raw == null) return "";
            raw = raw.Trim();
            if (raw.Length >= 2) {
                char first = raw[0];
                char last = raw[raw.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                    return raw.Substring(1, raw.Length - 2);
                }
            }
            return raw;
        }

        static bool IsDisabled(Element e, IElementStateProvider state) {
            if (e == null) return false;
            return (state.GetState(e) & ElementState.Disabled) != 0 || e.HasAttribute("disabled");
        }

        static bool IsFormControl(Element e) {
            if (e == null) return false;
            switch (e.TagName) {
                case "button":
                case "fieldset":
                case "input":
                case "optgroup":
                case "option":
                case "select":
                case "textarea":
                    return true;
                default:
                    return false;
            }
        }

        static bool IsRequiredCandidate(Element e) {
            if (e == null) return false;
            if (e.TagName == "select" || e.TagName == "textarea") return true;
            if (e.TagName != "input") return false;
            var type = InputType(e);
            return type != "hidden"
                && type != "button"
                && type != "reset"
                && type != "submit"
                && type != "image"
                && type != "range"
                && type != "color";
        }

        static bool IsValidationCandidate(Element e, IElementStateProvider state) {
            if (!IsRequiredCandidate(e)) return false;
            return !IsDisabled(e, state);
        }

        static bool IsRangeCandidate(Element e, IElementStateProvider state) {
            if (e == null || e.TagName != "input" || IsDisabled(e, state)) return false;
            var type = InputType(e);
            if (type != "number" && type != "range") return false;
            if (string.IsNullOrEmpty(e.GetAttribute("value"))) return false;
            return e.HasAttribute("min") || e.HasAttribute("max");
        }

        static bool HasUserInteracted(Element e, IElementStateProvider state) {
            return (state.GetState(e) & ElementState.UserInteracted) != 0;
        }

        static bool IsDefaultElement(Element e) {
            if (e == null) return false;
            if (e.TagName == "option") return IsDefaultOption(e);
            if (e.TagName == "input") {
                var type = InputType(e);
                if (type == "checkbox" || type == "radio") return e.HasAttribute("checked");
                if (type == "submit" || type == "image") return IsFirstSubmitButton(e);
                return false;
            }
            if (e.TagName == "button") {
                var type = e.GetAttribute("type");
                if (string.IsNullOrEmpty(type) || type.Equals("submit", StringComparison.OrdinalIgnoreCase)) {
                    return IsFirstSubmitButton(e);
                }
            }
            return false;
        }

        static bool IsDefaultOption(Element e) {
            if (e.HasAttribute("disabled")) return false;
            if (e.HasAttribute("selected")) return true;
            Element scope = null;
            for (var cur = e.Parent as Element; cur != null; cur = cur.Parent as Element) {
                if (cur.TagName == "select") { scope = cur; break; }
            }
            if (scope == null) scope = e.Parent as Element;
            if (scope == null) return false;
            Element firstOption = null;
            if (FindFirstDefaultOption(scope, ref firstOption)) return false;
            return ReferenceEquals(firstOption, e);
        }

        static bool FindFirstDefaultOption(Element root, ref Element firstOption) {
            foreach (var child in root.Children) {
                if (child is not Element c) continue;
                if (c.TagName == "option") {
                    if (c.HasAttribute("disabled")) continue;
                    if (c.HasAttribute("selected")) return true;
                    if (firstOption == null) firstOption = c;
                } else {
                    if (FindFirstDefaultOption(c, ref firstOption)) return true;
                }
            }
            return false;
        }

        static bool IsFirstSubmitButton(Element e) {
            var owner = FormOwner(e);
            if (owner == null) return false;
            var first = FirstSubmitButton(owner);
            return ReferenceEquals(first, e);
        }

        static Element FormOwner(Element e) {
            for (var cur = e.Parent as Element; cur != null; cur = cur.Parent as Element) {
                if (cur.TagName == "form") return cur;
            }
            return null;
        }

        static Element FirstSubmitButton(Element root) {
            if (root == null) return null;
            foreach (var child in root.Children) {
                if (child is Element e) {
                    if (IsSubmitButton(e) && !e.HasAttribute("disabled")) return e;
                    var nested = FirstSubmitButton(e);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        static bool IsSubmitButton(Element e) {
            if (e == null) return false;
            if (e.TagName == "button") {
                var type = e.GetAttribute("type");
                return string.IsNullOrEmpty(type) || type.Equals("submit", StringComparison.OrdinalIgnoreCase);
            }
            if (e.TagName != "input") return false;
            var inputType = InputType(e);
            return inputType == "submit" || inputType == "image";
        }

        static bool IsReadWrite(Element e, IElementStateProvider state) {
            if (e == null || IsDisabled(e, state)) return false;
            if (e.TagName == "textarea") return !e.HasAttribute("readonly");
            if (e.TagName != "input") return IsContentEditable(e);
            if (e.HasAttribute("readonly")) return false;
            switch (InputType(e)) {
                case "text":
                case "search":
                case "url":
                case "tel":
                case "email":
                case "password":
                case "date":
                case "datetime-local":
                case "month":
                case "number":
                case "time":
                case "week":
                    return true;
                default:
                    return false;
            }
        }

        static bool IsContentEditable(Element e) {
            for (var cur = e; cur != null; cur = cur.Parent as Element) {
                string raw = cur.GetAttribute("contenteditable");
                if (raw == null) continue;
                raw = raw.Trim();
                if (raw.Length == 0) return true;
                if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(raw, "plaintext-only", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return false;
        }

        static bool IsInvalid(Element e, IElementStateProvider state) {
            if (!IsValidationCandidate(e, state)) return false;
            string value = FormValue(e);
            if (e.HasAttribute("required") && string.IsNullOrEmpty(value)) return true;
            if (string.IsNullOrEmpty(value)) return false;

            if (e.TagName == "input") {
                switch (InputType(e)) {
                    case "email":
                        if (!LooksLikeEmail(value)) return true;
                        break;
                    case "url":
                        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return true;
                        break;
                    case "number":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return true;
                        if (TryDoubleAttr(e, "min", out var min) && number < min) return true;
                        if (TryDoubleAttr(e, "max", out var max) && number > max) return true;
                        break;
                }
            }

            string pattern = e.GetAttribute("pattern");
            if (!string.IsNullOrEmpty(pattern)) {
                try {
                    if (!Regex.IsMatch(value, "^(?:" + pattern + ")$")) return true;
                } catch (ArgumentException) {
                    return true;
                }
            }

            return false;
        }

        static bool IsOutOfRange(Element e) {
            if (e == null || e.TagName != "input") return false;
            if (!double.TryParse(e.GetAttribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                return false;
            }
            if (TryDoubleAttr(e, "min", out var min) && value < min) return true;
            if (TryDoubleAttr(e, "max", out var max) && value > max) return true;
            return false;
        }

        static string InputType(Element e) {
            var type = e.GetAttribute("type");
            return string.IsNullOrEmpty(type) ? "text" : type.Trim().ToLowerInvariant();
        }

        static string FormValue(Element e) {
            if (e == null) return "";
            if (e.TagName == "textarea") return TextContent(e);
            return e.GetAttribute("value") ?? "";
        }

        static string TextContent(Element e) {
            if (e == null || e.Children.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            AppendText(e, sb);
            return sb.ToString();
        }

        static void AppendText(Node node, System.Text.StringBuilder sb) {
            if (node is TextNode tn) {
                sb.Append(tn.Data);
                return;
            }
            foreach (var child in node.Children) AppendText(child, sb);
        }

        static bool TryDoubleAttr(Element e, string name, out double value) {
            return double.TryParse(e.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static bool LooksLikeEmail(string value) {
            int at = value.IndexOf('@');
            return at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0 && value.IndexOf('.', at + 1) > at + 1;
        }

        // Evaluates a `:has(<relative-selector-list>)` against `subject`. Each
        // entry has a synthetic anchor compound at index 0 (the matched element,
        // which `subject` plays the role of) and a leading combinator at
        // Combinators[0] specifying the relation between `subject` and the
        // first real compound. Per CSS Selectors L4 §17.4, the inner traversal
        // is anchored at the subject and walks OUTWARD — descendants/children
        // for `descendant`/`>`, forward siblings for `+`/`~` — never upward
        // through Parent past the subject. Multi-compound forms such as
        // `:has(> X > Y)` and `:has(+ X Y)` are matched by forward-chaining
        // the full relative selector left-to-right from the candidate that
        // satisfies the leading combinator, so the chain stays inside (or
        // forward of) the subject's relative scope.
        //
        // v1: walks the entire subtree (descendant relation) or sibling chain
        // (adjacent / general). Performance is O(subtree-size × inner-selectors)
        // per :has() check; cascade caches the per-element result so repeated
        // re-evaluations of the same element on the same DOM hit-cache.
        // Reactive invalidation: the cascade engine drops the per-element
        // style cache on any structural mutation in the subtree, which forces
        // a fresh :has() match on the next ComputeAll. See
        // CascadeEngine.InvalidateSubtree.
        internal static bool MatchHas(System.Collections.Generic.List<CompoundSequence> innerList, Element subject, IElementStateProvider state, Element scopeRoot = null) {
            foreach (var seq in innerList) {
                if (MatchHasSequence(seq, subject, state, scopeRoot)) return true;
            }
            return false;
        }

        static bool MatchHasSequence(CompoundSequence seq, Element subject, IElementStateProvider state, Element scopeRoot = null) {
            // seq.Compounds: [anchor, c1, c2, ...] with seq.Combinators:
            // [leadingRel, c1->c2, c2->c3, ...]. The leading combinator
            // describes the relation between subject and the first real
            // compound. Per CSS Selectors L4 §17.4 the inner traversal must
            // be anchored at the subject and walk OUTWARD (down for
            // descendant, right for siblings) — it must never walk upward
            // through Parent past the subject. We therefore strip the anchor
            // and run a forward, left-to-right walk over the relative chain
            // rather than delegating to MatchSequence (which walks
            // right-to-left via Parent / PreviousElementSibling).
            if (seq.Compounds.Count < 2) return false;
            var leading = seq.Combinators[0];
            switch (leading) {
                case Combinator.Descendant:
                    return WalkDescendantsForHas(subject, seq, state, scopeRoot);
                case Combinator.Child:
                    foreach (var c in subject.Children) {
                        if (c is Element ce && MatchHasChainForward(seq, 1, ce, state, scopeRoot)) return true;
                    }
                    return false;
                case Combinator.AdjacentSibling: {
                    var sib = NextElementSibling(subject);
                    if (sib == null) return false;
                    return MatchHasChainForward(seq, 1, sib, state, scopeRoot);
                }
                case Combinator.GeneralSibling: {
                    var sib = NextElementSibling(subject);
                    while (sib != null) {
                        if (MatchHasChainForward(seq, 1, sib, state, scopeRoot)) return true;
                        sib = NextElementSibling(sib);
                    }
                    return false;
                }
            }
            return false;
        }

        // Forward (left-to-right) matcher for the relative chain inside
        // `:has(...)`. `seq.Compounds[index]` is the compound currently being
        // tested against `current`; if more compounds remain to its right we
        // walk OUTWARD from `current` (down for descendant, immediate child
        // for `>`, forward siblings for `+`/`~`) and recurse. We never call
        // MatchSequence on the inner — that would walk Parent/Prev upward and
        // could escape the subject's relative scope, which §17.4 forbids.
        static bool MatchHasChainForward(CompoundSequence seq, int index, Element current, IElementStateProvider state, Element scopeRoot) {
            if (!MatchCompound(seq.Compounds[index], current, state, scopeRoot)) return false;
            if (index == seq.Compounds.Count - 1) return true;
            var next = seq.Combinators[index];
            switch (next) {
                case Combinator.Descendant:
                    return WalkDescendantsForHasChain(current, seq, index + 1, state, scopeRoot);
                case Combinator.Child:
                    foreach (var c in current.Children) {
                        if (c is Element ce && MatchHasChainForward(seq, index + 1, ce, state, scopeRoot)) return true;
                    }
                    return false;
                case Combinator.AdjacentSibling: {
                    var sib = NextElementSibling(current);
                    if (sib == null) return false;
                    return MatchHasChainForward(seq, index + 1, sib, state, scopeRoot);
                }
                case Combinator.GeneralSibling: {
                    var sib = NextElementSibling(current);
                    while (sib != null) {
                        if (MatchHasChainForward(seq, index + 1, sib, state, scopeRoot)) return true;
                        sib = NextElementSibling(sib);
                    }
                    return false;
                }
                default:
                    return false;
            }
        }

        static bool WalkDescendantsForHas(Element root, CompoundSequence seq, IElementStateProvider state, Element scopeRoot) {
            foreach (var c in root.Children) {
                if (c is Element ce) {
                    if (MatchHasChainForward(seq, 1, ce, state, scopeRoot)) return true;
                    if (WalkDescendantsForHas(ce, seq, state, scopeRoot)) return true;
                }
            }
            return false;
        }

        static bool WalkDescendantsForHasChain(Element root, CompoundSequence seq, int index, IElementStateProvider state, Element scopeRoot) {
            foreach (var c in root.Children) {
                if (c is Element ce) {
                    if (MatchHasChainForward(seq, index, ce, state, scopeRoot)) return true;
                    if (WalkDescendantsForHasChain(ce, seq, index, state, scopeRoot)) return true;
                }
            }
            return false;
        }

        static bool IsRootElement(Element e) {
            return e.Parent is Document && FirstElementChild(e.Parent) == e;
        }

        static Element FirstElementChild(Node n) {
            foreach (var c in n.Children) if (c is Element ec) return ec;
            return null;
        }

        static Element PreviousElementSibling(Element e) {
            if (!(e.Parent is Node parent)) return null;
            var children = parent.Children;
            int idx = -1;
            for (int i = 0; i < children.Count; i++) {
                if (ReferenceEquals(children[i], e)) { idx = i; break; }
            }
            if (idx <= 0) return null;
            for (int i = idx - 1; i >= 0; i--) {
                if (children[i] is Element ee) return ee;
            }
            return null;
        }

        static Element NextElementSibling(Element e) {
            if (!(e.Parent is Node parent)) return null;
            var children = parent.Children;
            int idx = -1;
            for (int i = 0; i < children.Count; i++) {
                if (ReferenceEquals(children[i], e)) { idx = i; break; }
            }
            if (idx < 0) return null;
            for (int i = idx + 1; i < children.Count; i++) {
                if (children[i] is Element ee) return ee;
            }
            return null;
        }

        static Element PreviousElementSiblingOfType(Element e, string tag) {
            var s = PreviousElementSibling(e);
            while (s != null) {
                if (s.TagName == tag) return s;
                s = PreviousElementSibling(s);
            }
            return null;
        }

        static Element NextElementSiblingOfType(Element e, string tag) {
            var s = NextElementSibling(e);
            while (s != null) {
                if (s.TagName == tag) return s;
                s = NextElementSibling(s);
            }
            return null;
        }

        static int ChildIndex(Element e) {
            if (!(e.Parent is Node parent)) return 0;
            int idx = 0;
            foreach (var c in parent.Children) {
                if (c is Element ec) {
                    idx++;
                    if (ReferenceEquals(ec, e)) return idx;
                }
            }
            return 0;
        }

        static int ChildIndexFromEnd(Element e) {
            if (!(e.Parent is Node parent)) return 0;
            var children = parent.Children;
            int idx = 0;
            for (int i = children.Count - 1; i >= 0; i--) {
                if (children[i] is Element ec) {
                    idx++;
                    if (ReferenceEquals(ec, e)) return idx;
                }
            }
            return 0;
        }

        // CSS Selectors L4 §6.6.5: count only siblings matching the `of <selector-list>`
        // filter. Returns the filtered 1-based index of `e` among matching siblings,
        // or 0 if `e` itself doesn't satisfy the filter.
        static int FilteredChildIndex(Element e, System.Collections.Generic.List<CompoundSequence> filter, IElementStateProvider state, Element scopeRoot, bool fromEnd) {
            if (!(e.Parent is Node parent)) return 0;
            if (!MatchesFilter(e, filter, state, scopeRoot)) return 0;
            var children = parent.Children;
            int idx = 0;
            if (fromEnd) {
                for (int i = children.Count - 1; i >= 0; i--) {
                    if (children[i] is Element ec && MatchesFilter(ec, filter, state, scopeRoot)) {
                        idx++;
                        if (ReferenceEquals(ec, e)) return idx;
                    }
                }
            } else {
                for (int i = 0; i < children.Count; i++) {
                    if (children[i] is Element ec && MatchesFilter(ec, filter, state, scopeRoot)) {
                        idx++;
                        if (ReferenceEquals(ec, e)) return idx;
                    }
                }
            }
            return 0;
        }

        static bool MatchesFilter(Element e, System.Collections.Generic.List<CompoundSequence> filter, IElementStateProvider state, Element scopeRoot) {
            foreach (var seq in filter) {
                if (MatchSequence(seq, e, state, scopeRoot)) return true;
            }
            return false;
        }

        static int ChildIndexOfType(Element e) {
            if (!(e.Parent is Node parent)) return 0;
            int idx = 0;
            foreach (var c in parent.Children) {
                if (c is Element ec && ec.TagName == e.TagName) {
                    idx++;
                    if (ReferenceEquals(ec, e)) return idx;
                }
            }
            return 0;
        }

        static int ChildIndexOfTypeFromEnd(Element e) {
            if (!(e.Parent is Node parent)) return 0;
            var children = parent.Children;
            int idx = 0;
            for (int i = children.Count - 1; i >= 0; i--) {
                if (children[i] is Element ec && ec.TagName == e.TagName) {
                    idx++;
                    if (ReferenceEquals(ec, e)) return idx;
                }
            }
            return 0;
        }
    }
}
