using System.Collections.Generic;
using System.Text;
using Weva.Css.Values;

namespace Weva.Css.Selectors {
    public static class SelectorParser {
        public static CompiledSelector Parse(string text) {
            if (text == null) throw new SelectorParseException("Selector text is null", 1);
            var p = new Parser(text);
            var seq = p.ParseSequence();
            p.SkipWhitespace();
            if (!p.AtEnd()) throw p.Error($"Unexpected character '{p.Peek()}'");
            // Trim leading/trailing whitespace from the source text so the
            // DevTools trace shows ".card:hover > .title" not " .card:hover > .title ".
            return new CompiledSelector(seq, text.Trim());
        }

        // Parse a selector-list (comma-separated) and return one CompiledSelector
        // per complex selector. Each carries only its own slice of the source text,
        // not the full comma list.
        internal static List<CompiledSelector> ParseSelectorListCompiled(string text) {
            if (text == null) throw new SelectorParseException("Selector text is null", 1);
            var p = new Parser(text);
            var result = p.ParseSequenceListCompiled(text);
            p.SkipWhitespace();
            if (!p.AtEnd()) throw p.Error($"Unexpected character '{p.Peek()}'");
            return result;
        }

        internal static List<CompoundSequence> ParseSelectorList(string text) {
            if (text == null) throw new SelectorParseException("Selector text is null", 1);
            var p = new Parser(text);
            var list = p.ParseSequenceList();
            p.SkipWhitespace();
            if (!p.AtEnd()) throw p.Error($"Unexpected character '{p.Peek()}'");
            return list;
        }

        internal sealed class Parser {
            readonly string src;
            int pos;

            public Parser(string s) {
                src = s ?? "";
                pos = 0;
            }

            public bool AtEnd() => pos >= src.Length;
            public char Peek() => pos < src.Length ? src[pos] : '\0';
            char PeekAt(int offset) => pos + offset < src.Length ? src[pos + offset] : '\0';
            void Advance() { if (pos < src.Length) pos++; }
            public int Column => pos + 1;

            public SelectorParseException Error(string msg) => new(msg, Column);

            public void SkipWhitespace() {
                while (!AtEnd() && IsWhitespace(Peek())) Advance();
            }

            bool ConsumeWhitespace() {
                bool any = false;
                while (!AtEnd() && IsWhitespace(Peek())) {
                    Advance();
                    any = true;
                }
                return any;
            }

            public List<CompoundSequence> ParseSequenceList() {
                var list = new List<CompoundSequence>();
                SkipWhitespace();
                list.Add(ParseSequence());
                while (true) {
                    SkipWhitespace();
                    if (AtEnd() || Peek() != ',') break;
                    Advance();
                    SkipWhitespace();
                    list.Add(ParseSequence());
                }
                return list;
            }

            // Like ParseSequenceList but returns CompiledSelector instances, each
            // carrying the source-text slice for its own complex selector. This is
            // the entry point used by CascadeEngine.CompileStyleRule so DevTools
            // can surface the human-readable selector text per rule.
            public List<CompiledSelector> ParseSequenceListCompiled(string fullText) {
                var list = new List<CompiledSelector>();
                SkipWhitespace();
                int sliceStart = pos;
                var seq = ParseSequence();
                int sliceEnd = pos;
                list.Add(new CompiledSelector(seq, SliceTrimmed(fullText, sliceStart, sliceEnd)));
                while (true) {
                    SkipWhitespace();
                    if (AtEnd() || Peek() != ',') break;
                    Advance(); // consume ','
                    SkipWhitespace();
                    sliceStart = pos;
                    seq = ParseSequence();
                    sliceEnd = pos;
                    list.Add(new CompiledSelector(seq, SliceTrimmed(fullText, sliceStart, sliceEnd)));
                }
                return list;
            }

            static string SliceTrimmed(string s, int start, int end) {
                // Trim trailing whitespace (leading was already consumed by SkipWhitespace
                // before each ParseSequence call).
                while (end > start && IsWhitespace(s[end - 1])) end--;
                if (start >= end) return "";
                return s.Substring(start, end - start);
            }

            // CSS Selectors L4 §4.2 / §17.1 — forgiving selector list, used by
            // `:is()` and `:where()` (and by the spec for `<forgiving-relative-
            // selector-list>` elsewhere). Each comma-separated alternate is
            // parsed independently; an alternate that fails to parse is
            // silently dropped from the result rather than invalidating the
            // whole rule. `:not()` does NOT use this — its argument is an
            // unforgiving `<complex-selector-list>` per §6.2.
            //
            // Empty result is meaningful: `:is(:unknown)` parses to an :is
            // with an empty inner list, which the matcher treats as "never
            // matches" (SelectorMatcher.MatchSimple Is/Where arm returns
            // false when InnerList is empty). The surrounding rule survives.
            public List<CompoundSequence> ParseForgivingSequenceList() {
                var list = new List<CompoundSequence>();
                SkipWhitespace();
                while (true) {
                    int savedPos = pos;
                    try {
                        list.Add(ParseSequence());
                    } catch (SelectorParseException) {
                        pos = savedPos;
                        SkipToNextForgivingDelimiter();
                    }
                    SkipWhitespace();
                    if (AtEnd() || Peek() != ',') break;
                    Advance();
                    SkipWhitespace();
                }
                return list;
            }

            // Advances `pos` past tokens up to the next top-level comma or
            // up to (but NOT past) the enclosing `)` that terminates the
            // outer pseudo-class argument list. Tracks paren/bracket nesting
            // and skips over quoted attribute-value strings so `,` inside
            // `[data-x="a,b"]` or `:foo(.x, .y)` doesn't break the alternate
            // boundary.
            void SkipToNextForgivingDelimiter() {
                int depth = 0;
                while (!AtEnd()) {
                    char ch = Peek();
                    if (ch == '"' || ch == '\'') {
                        char quote = ch;
                        Advance();
                        while (!AtEnd() && Peek() != quote) {
                            if (Peek() == '\\') {
                                Advance();
                                if (!AtEnd()) Advance();
                            } else {
                                Advance();
                            }
                        }
                        if (!AtEnd()) Advance(); // closing quote
                        continue;
                    }
                    if (ch == '(' || ch == '[') { depth++; Advance(); continue; }
                    if (ch == ')' || ch == ']') {
                        if (depth == 0) return; // leave the outer ')' for the caller
                        depth--;
                        Advance();
                        continue;
                    }
                    if (ch == ',' && depth == 0) return;
                    Advance();
                }
            }

            public CompoundSequence ParseSequence() {
                SkipWhitespace();
                var seq = new CompoundSequence();
                seq.Compounds.Add(ParseCompound());
                while (true) {
                    bool hadWhitespace = ConsumeWhitespace();
                    if (AtEnd()) break;
                    char c = Peek();
                    Combinator combinator;
                    if (c == '>' || c == '+' || c == '~') {
                        Advance();
                        SkipWhitespace();
                        combinator = c switch {
                            '>' => Combinator.Child,
                            '+' => Combinator.AdjacentSibling,
                            '~' => Combinator.GeneralSibling,
                            _ => Combinator.Descendant
                        };
                    } else if (c == ',' || c == ')') {
                        break;
                    } else if (hadWhitespace) {
                        combinator = Combinator.Descendant;
                    } else {
                        break;
                    }
                    if (AtEnd() || Peek() == ',' || Peek() == ')') {
                        throw Error("Expected selector after combinator");
                    }
                    if (Peek() == '>' || Peek() == '+' || Peek() == '~') {
                        throw Error($"Unexpected combinator '{Peek()}'");
                    }
                    seq.Combinators.Add(combinator);
                    seq.Compounds.Add(ParseCompound());
                }
                if (seq.Compounds.Count == 0) throw Error("Empty selector");
                return seq;
            }

            CompoundSelector ParseCompound() {
                var compound = new CompoundSelector();
                bool any = false;
                while (!AtEnd()) {
                    char c = Peek();
                    if (c == ':' && PeekAt(1) == ':') {
                        Advance(); Advance();
                        var name = ReadIdent();
                        if (name.Length == 0) throw Error("Expected pseudo-element name");
                        if (compound.PseudoElement != null) throw Error("Multiple pseudo-elements");
                        if (!IsKnownPseudoElement(name))
                            throw Error($"Unknown pseudo-element '::{name}'");
                        // CSS Selectors L4 §3.3: pseudo-element names are
                        // ASCII case-insensitive. Store the canonical
                        // lowercase form so MatchesPseudoElement's ordinal
                        // comparison against the cascade's lowercase
                        // pseudo-name ("before" / "after" / ...) matches
                        // when the author wrote `::BEFORE`.
                        compound.PseudoElement = CssStringUtil.ToLowerInvariantOrSame(name);
                        any = true;
                        continue;
                    }
                    if (c == '*') {
                        if (compound.PseudoElement != null) throw Error("Selector after pseudo-element");
                        Advance();
                        if (Peek() == '|' && PeekAt(1) != '=') {
                            Advance();
                            if (Peek() == '*') {
                                Advance();
                                compound.Parts.Add(UniversalSelector.Instance);
                            } else {
                                var local = ReadIdent();
                                if (local.Length == 0) throw Error("Expected local name after '*|'");
                                compound.Parts.Add(new TypeSelector(CssStringUtil.ToLowerInvariantOrSame(local)));
                            }
                            any = true;
                            continue;
                        }
                        compound.Parts.Add(UniversalSelector.Instance);
                        any = true;
                        continue;
                    }
                    if (c == '|' && PeekAt(1) != '=') {
                        if (compound.PseudoElement != null) throw Error("Selector after pseudo-element");
                        if (any) break;
                        Advance();
                        if (Peek() == '*') {
                            Advance();
                            compound.Parts.Add(UniversalSelector.Instance);
                        } else {
                            var local = ReadIdent();
                            if (local.Length == 0) throw Error("Expected local name after '|'");
                            compound.Parts.Add(new TypeSelector(CssStringUtil.ToLowerInvariantOrSame(local)));
                        }
                        any = true;
                        continue;
                    }
                    if (c == '#') {
                        if (compound.PseudoElement != null) throw Error("Selector after pseudo-element");
                        Advance();
                        var id = ReadIdent();
                        if (id.Length == 0) throw Error("Expected identifier after '#'");
                        compound.Parts.Add(new IdSelector(id));
                        any = true;
                        continue;
                    }
                    if (c == '.') {
                        if (compound.PseudoElement != null) throw Error("Selector after pseudo-element");
                        Advance();
                        var cn = ReadIdent();
                        if (cn.Length == 0) throw Error("Expected identifier after '.'");
                        compound.Parts.Add(new ClassSelector(cn));
                        any = true;
                        continue;
                    }
                    if (c == '[') {
                        if (compound.PseudoElement != null) throw Error("Selector after pseudo-element");
                        compound.Parts.Add(ParseAttribute());
                        any = true;
                        continue;
                    }
                    if (c == ':') {
                        // CSS Selectors L4 §3.6 allows pseudo-classes to qualify
                        // pseudo-elements (e.g. ::before:hover). We implement this
                        // for the WebKit scrollbar family: ::-webkit-scrollbar-thumb:hover
                        // and ::-webkit-scrollbar-thumb:active are widely authored in
                        // game CSS to style the thumb on interaction. Allow interaction
                        // pseudo-class qualifiers after any webkit scrollbar pseudo-element;
                        // all other pseudo-elements still reject any trailing selector.
                        if (compound.PseudoElement != null) {
                            if (IsWebkitScrollbarPseudoElement(compound.PseudoElement)
                                && PeekAt(1) != ':'
                                && IsInteractionPseudoClassAt(pos + 1)) {
                                // Known interaction pseudo-class — parse and append it so
                                // the cascade can match :hover / :active on the host element
                                // when computing the thumb's pseudo-element style.
                                compound.Parts.Add(ParsePseudoClass());
                                any = true;
                                continue;
                            }
                            throw Error("Selector after pseudo-element");
                        }
                        if (TryParseLegacyPseudoElement(out var pseudoName)) {
                            compound.PseudoElement = pseudoName;
                            any = true;
                            continue;
                        }
                        compound.Parts.Add(ParsePseudoClass());
                        any = true;
                        continue;
                    }
                    if (IsIdentStart(c)) {
                        if (compound.PseudoElement != null) throw Error("Selector after pseudo-element");
                        if (any) break;
                        var name = ReadIdent();
                        // CSS Selectors L4 §6.1 — `ns|type` namespace-prefixed
                        // type selectors. No @namespace machinery in v1; drop
                        // the prefix and match on the local name. `|=` is the
                        // attribute dash-match operator and never appears here,
                        // but guard against it for symmetry with the attribute
                        // selector path.
                        if (Peek() == '|' && PeekAt(1) != '=') {
                            Advance();
                            if (Peek() == '*') {
                                Advance();
                                compound.Parts.Add(UniversalSelector.Instance);
                            } else {
                                var local = ReadIdent();
                                if (local.Length == 0) throw Error("Expected local name after '|'");
                                compound.Parts.Add(new TypeSelector(CssStringUtil.ToLowerInvariantOrSame(local)));
                            }
                        } else {
                            compound.Parts.Add(new TypeSelector(CssStringUtil.ToLowerInvariantOrSame(name)));
                        }
                        any = true;
                        continue;
                    }
                    break;
                }
                if (!any) throw Error("Empty compound selector");
                return compound;
            }

            bool TryParseLegacyPseudoElement(out string pseudoName) {
                pseudoName = null;
                int start = pos;
                if (Peek() != ':' || PeekAt(1) == ':') return false;
                Advance();
                var name = ReadIdent();
                var lname = CssStringUtil.ToLowerInvariantOrSame(name);
                if ((lname == "before" || lname == "after") && (AtEnd() || Peek() != '(')) {
                    pseudoName = lname;
                    return true;
                }

                pos = start;
                return false;
            }

            AttributeSelector ParseAttribute() {
                if (Peek() != '[') throw Error("Expected '['");
                Advance();
                SkipWhitespace();
                string name;
                if (Peek() == '*' && PeekAt(1) == '|' && PeekAt(2) != '=') {
                    Advance();
                    Advance();
                    name = ReadIdent();
                    if (name.Length == 0) throw Error("Expected attribute local name after '*|'");
                } else {
                    name = ReadIdent();
                    if (name.Length == 0) throw Error("Expected attribute name");
                    // CSS Selectors L4 §6.3.5 allows `ns|attr` namespace-prefixed
                    // attribute names. The engine has no @namespace machinery,
                    // so we accept the syntax and drop the prefix — matching on
                    // the local name — rather than dropping the rule. `|=` is
                    // the dash-match operator and must NOT be consumed here.
                    if (Peek() == '|' && PeekAt(1) != '=') {
                        Advance();
                        var local = ReadIdent();
                        if (local.Length == 0) throw Error("Expected attribute local name after '|'");
                        name = local;
                    }
                }
                SkipWhitespace();
                if (AtEnd()) throw Error("Unterminated attribute selector");
                if (Peek() == ']') {
                    Advance();
                    return new AttributeSelector(CssStringUtil.ToLowerInvariantOrSame(name), AttributeOperator.Exists, null);
                }
                AttributeOperator op;
                char c = Peek();
                if (c == '=') {
                    Advance();
                    op = AttributeOperator.Equals;
                } else if (PeekAt(1) == '=') {
                    op = c switch {
                        '~' => AttributeOperator.WhitespaceContains,
                        '|' => AttributeOperator.DashMatch,
                        '^' => AttributeOperator.Prefix,
                        '$' => AttributeOperator.Suffix,
                        '*' => AttributeOperator.Substring,
                        _ => throw Error($"Unknown attribute operator '{c}='")
                    };
                    Advance();
                    Advance();
                } else {
                    throw Error($"Expected attribute operator, got '{c}'");
                }
                SkipWhitespace();
                var value = ReadAttributeValue();
                SkipWhitespace();
                bool caseInsensitive = false;
                if (!AtEnd() && (Peek() == 'i' || Peek() == 'I' || Peek() == 's' || Peek() == 'S')) {
                    char flag = Peek();
                    char after = PeekAt(1);
                    if (after == ']' || IsWhitespace(after)) {
                        caseInsensitive = (flag == 'i' || flag == 'I');
                        Advance();
                        SkipWhitespace();
                    }
                }
                if (AtEnd() || Peek() != ']') throw Error("Unterminated attribute selector");
                Advance();
                return new AttributeSelector(CssStringUtil.ToLowerInvariantOrSame(name), op, value, caseInsensitive);
            }

            string ReadAttributeValue() {
                if (AtEnd()) throw Error("Expected attribute value");
                char c = Peek();
                if (c == '"' || c == '\'') return ReadQuotedString(c);
                // CSS Selectors L4 strictly requires unquoted values to be a
                // valid <ident-token>, but real-world stylesheets commonly use
                // unquoted file-extension fragments like `[src$=.png]`. Accept
                // any non-terminator character (including a leading '.') and
                // read up to the closing ']' / whitespace. This matches what
                // browsers tolerate.
                if (c != ']' && !IsWhitespace(c)) {
                    var sb = new StringBuilder();
                    while (!AtEnd()) {
                        char ch = Peek();
                        if (ch == ']' || IsWhitespace(ch)) break;
                        sb.Append(ch);
                        Advance();
                    }
                    return sb.ToString();
                }
                throw Error("Expected attribute value");
            }

            string ReadQuotedString(char quote) {
                Advance();
                var sb = new StringBuilder();
                while (!AtEnd() && Peek() != quote) {
                    if (Peek() == '\\' && pos + 1 < src.Length) {
                        Advance();
                        sb.Append(Peek());
                        Advance();
                        continue;
                    }
                    sb.Append(Peek());
                    Advance();
                }
                if (AtEnd()) throw Error("Unterminated string");
                Advance();
                return sb.ToString();
            }

            // PH1: cap for functional-pseudo recursion — `:not(:not(:not(…)))`
            // (or :is/:where nesting; only :has was previously blocked) drove
            // ParsePseudoClass <-> ParseSequenceList into an uncatchable
            // StackOverflow. Over-depth throws SelectorParseException — the
            // cascade's per-rule EC11 catch drops the rule with a warning.
            const int MaxPseudoDepth = 32;
            int pseudoDepth;

            PseudoClassSelector ParsePseudoClass() {
                if (pseudoDepth >= MaxPseudoDepth)
                    throw Error("pseudo-class nesting deeper than " + MaxPseudoDepth + " levels");
                pseudoDepth++;
                try {
                    return ParsePseudoClassInner();
                } finally {
                    pseudoDepth--;
                }
            }

            PseudoClassSelector ParsePseudoClassInner() {
                if (Peek() != ':') throw Error("Expected ':'");
                Advance();
                var name = ReadIdent();
                if (name.Length == 0) throw Error("Expected pseudo-class name");
                var lname = CssStringUtil.ToLowerInvariantOrSame(name);
                bool hasArgs = !AtEnd() && Peek() == '(';
                if (!hasArgs) {
                    if (!TryMapSimplePseudo(lname, out var kind))
                        throw Error($"Unknown pseudo-class ':{lname}'");
                    return new PseudoClassSelector(kind);
                }
                Advance();
                switch (lname) {
                    case "nth-child":
                    case "nth-last-child":
                    case "nth-of-type":
                    case "nth-last-of-type": {
                        SkipWhitespace();
                        var nth = ParseNth();
                        SkipWhitespace();
                        // CSS Selectors L4 §6.6.5: `:nth-child(An+B of <selector-list>)`
                        // restricts An+B to children matching the filter. The spec
                        // only defines `of S` on :nth-child / :nth-last-child;
                        // :nth-of-type variants tolerate-and-ignore (rule still
                        // parses, filter discarded — see C9).
                        List<CompoundSequence> nthOfFilter = null;
                        if (TryReadKeyword("of")) {
                            SkipWhitespace();
                            nthOfFilter = ParseSequenceList();
                            SkipWhitespace();
                        }
                        if (AtEnd() || Peek() != ')') throw Error($"Unterminated ':{lname}('");
                        Advance();
                        var kind = lname switch {
                            "nth-child" => PseudoClassKind.NthChild,
                            "nth-last-child" => PseudoClassKind.NthLastChild,
                            "nth-of-type" => PseudoClassKind.NthOfType,
                            _ => PseudoClassKind.NthLastOfType
                        };
                        bool filterApplies = kind == PseudoClassKind.NthChild
                            || kind == PseudoClassKind.NthLastChild;
                        return filterApplies && nthOfFilter != null
                            ? new PseudoClassSelector(kind, nth, nthOfFilter)
                            : new PseudoClassSelector(kind, nth);
                    }
                    case "not": {
                        // CSS Selectors L4 §6.2: `:not(<complex-selector-list>)`
                        // accepts a full selector list, NOT just a simple
                        // selector. `:not(.a.b)`, `:not(a > b)`, and
                        // `:not(.a, .b)` are all valid. Fixed in #258 — was
                        // previously routed through ParseSimpleForNot which
                        // rejected compounds. Specificity is the highest of
                        // the listed selectors (same rule as :is).
                        SkipWhitespace();
                        var list = ParseSequenceList();
                        SkipWhitespace();
                        if (AtEnd() || Peek() != ')') throw Error("Unterminated ':not('");
                        Advance();
                        return new PseudoClassSelector(PseudoClassKind.Not, list);
                    }
                    case "is":
                    case "where": {
                        SkipWhitespace();
                        // CSS Selectors L4 §4.2 — `:is()` / `:where()` take a
                        // forgiving selector list: invalid alternates are
                        // silently dropped instead of invalidating the rule.
                        var list = ParseForgivingSequenceList();
                        SkipWhitespace();
                        if (AtEnd() || Peek() != ')') throw Error($"Unterminated ':{lname}('");
                        Advance();
                        var kind = lname == "is" ? PseudoClassKind.Is : PseudoClassKind.Where;
                        return new PseudoClassSelector(kind, list);
                    }
                    case "has": {
                        SkipWhitespace();
                        // CSS Selectors L4: `:has(<relative-selector-list>)`.
                        // Each item in the list is a relative selector — a
                        // regular complex selector that MAY begin with a leading
                        // combinator (`> `, `+ `, `~ `) which is taken to mean
                        // "child / next-sibling / following-sibling of the
                        // anchor element". When no leading combinator is
                        // present, the relation defaults to descendant.
                        // v1: `:has(:has(...))` is rejected per spec § 5.7.
                        // We enforce that by tracking parser depth at compile time.
                        if (insideHas) throw Error(":has() may not be nested inside :has()");
                        insideHas = true;
                        try {
                            var list = ParseRelativeSelectorList();
                            SkipWhitespace();
                            if (AtEnd() || Peek() != ')') throw Error("Unterminated ':has('");
                            Advance();
                            return new PseudoClassSelector(PseudoClassKind.Has, list);
                        } finally {
                            insideHas = false;
                        }
                    }
                    case "lang":
                    case "dir": {
                        SkipWhitespace();
                        string argument = ReadFunctionalArgumentRaw(lname);
                        SkipWhitespace();
                        if (AtEnd() || Peek() != ')') throw Error($"Unterminated ':{lname}('");
                        Advance();
                        if (string.IsNullOrWhiteSpace(argument)) throw Error($"Empty ':{lname}('");
                        var kind = lname == "lang" ? PseudoClassKind.Lang : PseudoClassKind.Dir;
                        return new PseudoClassSelector(kind, argument.Trim());
                    }
                    default:
                        throw Error($"Unknown pseudo-class ':{name}('");
                }
            }

            bool insideHas;

            // Parses a relative-selector-list: comma-separated CompoundSequences,
            // each of which may begin with a `>`, `+`, or `~` combinator. The
            // leading combinator is encoded as the first entry of the resulting
            // CompoundSequence's Combinators list and there is a synthetic
            // empty-anchor compound at the start; SelectorMatcher.MatchHas
            // strips this and walks the relation.
            List<CompoundSequence> ParseRelativeSelectorList() {
                var list = new List<CompoundSequence>();
                SkipWhitespace();
                list.Add(ParseRelativeSequence());
                while (true) {
                    SkipWhitespace();
                    if (AtEnd() || Peek() != ',') break;
                    Advance();
                    SkipWhitespace();
                    list.Add(ParseRelativeSequence());
                }
                return list;
            }

            CompoundSequence ParseRelativeSequence() {
                SkipWhitespace();
                Combinator leading = Combinator.Descendant;
                if (!AtEnd()) {
                    char c = Peek();
                    if (c == '>' || c == '+' || c == '~') {
                        Advance();
                        SkipWhitespace();
                        leading = c switch {
                            '>' => Combinator.Child,
                            '+' => Combinator.AdjacentSibling,
                            '~' => Combinator.GeneralSibling,
                            _ => Combinator.Descendant
                        };
                    }
                }
                var seq = ParseSequence();
                // Encode the leading combinator by prepending an "anchor"
                // compound (an UniversalSelector placeholder) to the front of
                // the sequence. The matcher strips the prepended anchor; the
                // combinator between [anchor] and [first] is the leading
                // relation between the :has() subject and the matched
                // descendant/sibling.
                var anchor = new CompoundSelector();
                anchor.Parts.Add(UniversalSelector.Instance);
                seq.Compounds.Insert(0, anchor);
                seq.Combinators.Insert(0, leading);
                return seq;
            }

            SimpleSelector ParseSimpleForNot() {
                if (AtEnd() || Peek() == ')') throw Error("Empty ':not()'");
                char c = Peek();
                if (c == '*') {
                    Advance();
                    return UniversalSelector.Instance;
                }
                if (c == '#') {
                    Advance();
                    var id = ReadIdent();
                    if (id.Length == 0) throw Error("Expected identifier after '#'");
                    return new IdSelector(id);
                }
                if (c == '.') {
                    Advance();
                    var cn = ReadIdent();
                    if (cn.Length == 0) throw Error("Expected identifier after '.'");
                    return new ClassSelector(cn);
                }
                if (c == '[') return ParseAttribute();
                if (c == ':') return ParsePseudoClass();
                if (IsIdentStart(c)) {
                    var name = ReadIdent();
                    return new TypeSelector(CssStringUtil.ToLowerInvariantOrSame(name));
                }
                // We're sitting on a character that isn't a valid simple
                // selector start (likely a combinator like `>` or whitespace
                // signalling a complex selector). Log BEFORE throwing so the
                // cascade's catch site doesn't have to discriminate this case
                // from other selector parse failures — the throw still
                // happens, behaviour is preserved.
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "SelectorParser",
                    ":not(complex selector) not supported, rule '" + src + "' will not match");
                throw Error("Expected simple selector inside ':not()'");
            }

            NthExpression ParseNth() {
                if (AtEnd()) throw Error("Expected nth expression");
                int start = pos;
                if (TryReadKeyword("odd")) return new NthExpression(2, 1);
                pos = start;
                if (TryReadKeyword("even")) return new NthExpression(2, 0);
                pos = start;

                int sign = 1;
                if (Peek() == '+') { Advance(); }
                else if (Peek() == '-') { sign = -1; Advance(); }

                int? leadingNum = null;
                if (!AtEnd() && Peek() >= '0' && Peek() <= '9') {
                    leadingNum = ReadInt();
                }

                if (!AtEnd() && (Peek() == 'n' || Peek() == 'N')) {
                    Advance();
                    int a = sign * (leadingNum ?? 1);
                    SkipWhitespace();
                    int b = 0;
                    if (!AtEnd() && (Peek() == '+' || Peek() == '-')) {
                        int bSign = Peek() == '+' ? 1 : -1;
                        Advance();
                        SkipWhitespace();
                        if (AtEnd() || Peek() < '0' || Peek() > '9') throw Error("Expected integer after sign in nth");
                        b = bSign * ReadInt();
                    }
                    return new NthExpression(a, b);
                }

                if (leadingNum == null) throw Error("Invalid nth expression");
                return new NthExpression(0, sign * leadingNum.Value);
            }

            int ReadInt() {
                int v = 0;
                bool any = false;
                while (!AtEnd() && Peek() >= '0' && Peek() <= '9') {
                    v = v * 10 + (Peek() - '0');
                    Advance();
                    any = true;
                }
                if (!any) throw Error("Expected integer");
                return v;
            }

            string ReadFunctionalArgumentRaw(string pseudoName) {
                int start = pos;
                bool inQuote = false;
                char quote = '\0';
                while (!AtEnd()) {
                    char c = Peek();
                    if (inQuote) {
                        if (c == '\\' && pos + 1 < src.Length) {
                            Advance();
                            Advance();
                            continue;
                        }
                        if (c == quote) inQuote = false;
                        Advance();
                        continue;
                    }
                    if (c == '"' || c == '\'') {
                        inQuote = true;
                        quote = c;
                        Advance();
                        continue;
                    }
                    if (c == ')') break;
                    Advance();
                }
                if (inQuote) throw Error($"Unterminated string in ':{pseudoName}('");
                return src.Substring(start, pos - start);
            }

            bool TryReadKeyword(string kw) {
                if (pos + kw.Length > src.Length) return false;
                for (int i = 0; i < kw.Length; i++) {
                    if (char.ToLowerInvariant(src[pos + i]) != kw[i]) return false;
                }
                int after = pos + kw.Length;
                if (after < src.Length) {
                    char a = src[after];
                    if (IsIdentChar(a)) return false;
                }
                pos = after;
                return true;
            }

            string ReadIdent() {
                if (AtEnd()) return "";
                int start = pos;
                if (Peek() == '-') Advance();
                if (!AtEnd() && IsIdentStart(Peek())) {
                    Advance();
                    while (!AtEnd() && IsIdentChar(Peek())) Advance();
                    return src.Substring(start, pos - start);
                }
                pos = start;
                return "";
            }

            static bool TryMapSimplePseudo(string lname, out PseudoClassKind kind) {
                switch (lname) {
                    case "first-child": kind = PseudoClassKind.FirstChild; return true;
                    case "last-child": kind = PseudoClassKind.LastChild; return true;
                    case "only-child": kind = PseudoClassKind.OnlyChild; return true;
                    case "first-of-type": kind = PseudoClassKind.FirstOfType; return true;
                    case "last-of-type": kind = PseudoClassKind.LastOfType; return true;
                    case "only-of-type": kind = PseudoClassKind.OnlyOfType; return true;
                    case "empty": kind = PseudoClassKind.Empty; return true;
                    case "hover": kind = PseudoClassKind.Hover; return true;
                    case "focus": kind = PseudoClassKind.Focus; return true;
                    case "focus-visible": kind = PseudoClassKind.FocusVisible; return true;
                    case "focus-within": kind = PseudoClassKind.FocusWithin; return true;
                    case "active": kind = PseudoClassKind.Active; return true;
                    case "link": kind = PseudoClassKind.Link; return true;
                    case "visited": kind = PseudoClassKind.Visited; return true;
                    case "any-link": kind = PseudoClassKind.AnyLink; return true;
                    case "target": kind = PseudoClassKind.Target; return true;
                    case "scope": kind = PseudoClassKind.Scope; return true;
                    case "disabled": kind = PseudoClassKind.Disabled; return true;
                    case "enabled": kind = PseudoClassKind.Enabled; return true;
                    case "checked": kind = PseudoClassKind.Checked; return true;
                    case "required": kind = PseudoClassKind.Required; return true;
                    case "optional": kind = PseudoClassKind.Optional; return true;
                    case "read-only": kind = PseudoClassKind.ReadOnly; return true;
                    case "read-write": kind = PseudoClassKind.ReadWrite; return true;
                    case "valid": kind = PseudoClassKind.Valid; return true;
                    case "invalid": kind = PseudoClassKind.Invalid; return true;
                    case "in-range": kind = PseudoClassKind.InRange; return true;
                    case "out-of-range": kind = PseudoClassKind.OutOfRange; return true;
                    case "user-valid": kind = PseudoClassKind.UserValid; return true;
                    case "user-invalid": kind = PseudoClassKind.UserInvalid; return true;
                    case "default": kind = PseudoClassKind.Default; return true;
                    case "placeholder-shown": kind = PseudoClassKind.PlaceholderShown; return true;
                    case "popover-open": kind = PseudoClassKind.PopoverOpen; return true;
                    case "modal": kind = PseudoClassKind.Modal; return true;
                    case "autofill": kind = PseudoClassKind.Autofill; return true;
                    case "root": kind = PseudoClassKind.Root; return true;
                    default: kind = default; return false;
                }
            }

            static bool IsKnownPseudoElement(string name) {
                var l = CssStringUtil.ToLowerInvariantOrSame(name);
                // Standard pseudo-elements.
                if (l == "before" || l == "after" || l == "placeholder" || l == "selection" || l == "backdrop" || l == "marker") return true;
                // WebKit scrollbar pseudo-elements (non-standard, widely authored).
                // ::-webkit-scrollbar, ::-webkit-scrollbar-thumb, ::-webkit-scrollbar-track
                // are routed to dedicated paint buckets and mapped onto the same paint
                // machinery as CSS Scrollbars L1 (scrollbar-width / scrollbar-color).
                // ::-webkit-scrollbar-corner, ::-webkit-scrollbar-button, and
                // ::-webkit-scrollbar-resizer parse without error but are currently
                // ignored at paint time — documented follow-up.
                if (l == "-webkit-scrollbar" || l == "-webkit-scrollbar-thumb" || l == "-webkit-scrollbar-track"
                    || l == "-webkit-scrollbar-corner" || l == "-webkit-scrollbar-button" || l == "-webkit-scrollbar-resizer") return true;
                return false;
            }

            // Returns true when `name` is one of the three webkit scrollbar
            // pseudo-elements that accept interaction pseudo-class qualifiers
            // (::-webkit-scrollbar-thumb:hover / :active). The corner/button/resizer
            // variants parse-and-ignore, so there's no need to qualify them.
            static bool IsWebkitScrollbarPseudoElement(string name) {
                return name == "-webkit-scrollbar"
                    || name == "-webkit-scrollbar-thumb"
                    || name == "-webkit-scrollbar-track"
                    || name == "-webkit-scrollbar-corner"
                    || name == "-webkit-scrollbar-button"
                    || name == "-webkit-scrollbar-resizer";
            }

            // Peek ahead in `src` from `startPos` (which is just past the `:`)
            // and return true when the ident that follows is `hover` or `active`.
            // Only these two interaction pseudo-classes are meaningful on the
            // webkit scrollbar thumb (Chrome honours them); everything else
            // falls through to the "Selector after pseudo-element" error.
            bool IsInteractionPseudoClassAt(int startPos) {
                // Skip optional second colon (shouldn't appear here, but guard).
                int i = startPos;
                if (i >= src.Length) return false;
                // Read the ident.
                int nameStart = i;
                while (i < src.Length && IsIdentChar(src[i])) i++;
                if (i == nameStart) return false;
                string name = src.Substring(nameStart, i - nameStart);
                var l = CssStringUtil.ToLowerInvariantOrSame(name);
                return l == "hover" || l == "active";
            }

            static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
            static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c >= 0x80;
            static bool IsIdentChar(char c) => IsIdentStart(c) || (c >= '0' && c <= '9') || c == '-';
        }
    }
}
