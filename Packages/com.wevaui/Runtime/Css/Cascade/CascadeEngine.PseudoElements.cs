using System.Collections.Generic;
using Weva.Css.Selectors;
using Weva.Dom;

namespace Weva.Css.Cascade {
    // ::before / ::after pseudo-element cascade. Per CSS 2.1 §12, a
    // ::before / ::after rule with a non-default `content` value generates
    // an anonymous box that the originating element treats as its first
    // (::before) or last (::after) child. The pseudo's computed style is
    // the cascaded result of selectors that target ::before / ::after, and
    // it inherits any unset inherited property from the originating
    // element's own computed style — so unset color / font-* propagate
    // naturally without authors having to repeat them.
    //
    // v1 scope: string-content only (`content: "..."` and `content: ""`).
    // attr() / counter() / url() / image content is not supported here;
    // the resolver simply returns null for any unrecognized form, which
    // BoxBuilder treats as "no pseudo box".
    //
    // Caching: not cached on the engine. BoxBuilder rebuilds the box tree
    // each layout pass; the cost of rebuilding a pseudo ComputedStyle is
    // proportional to the number of ::before/::after rules in the
    // stylesheet, which is typically zero or a small handful. Mirrors
    // ComputeBackdrop's caching policy.
    public sealed partial class CascadeEngine {
        public ComputedStyle ComputeBefore(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "before", beforeRules, stateProvider);
        }

        public ComputedStyle ComputeAfter(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "after", afterRules, stateProvider);
        }

        // ::placeholder — the styling hook for the placeholder text on form
        // controls (`<input>`, `<textarea>`). InputRenderer.DrawTextOverlay
        // calls this for every text-input box; when the rule list returns a
        // ComputedStyle, the renderer paints the placeholder using its
        // resolved `color` / `opacity`. Returns null when no author rule
        // matches, in which case the renderer falls back to a faded
        // host color.
        public ComputedStyle ComputePlaceholder(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "placeholder", placeholderRules, stateProvider);
        }

        // ::selection — the styling hook for the highlight rect and the
        // text color of the selected substring. InputRenderer.DrawTextOverlay
        // pulls `background-color` for the selection rect and `color` for
        // the foreground glyphs. Returns null when no rule matches; the
        // renderer falls back to the UA-default selection palette.
        public ComputedStyle ComputeSelection(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "selection", selectionRules, stateProvider);
        }

        // ::marker - style hook for list item marker boxes. BoxBuilder uses
        // the returned style for the anonymous marker box and marker TextRun,
        // while still deriving marker text/image from the host list item's
        // list-style-* properties.
        public ComputedStyle ComputeMarker(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "marker", markerRules, stateProvider);
        }

        // ::-webkit-scrollbar / ::-webkit-scrollbar-thumb / ::-webkit-scrollbar-track
        // Computed style for the three WebKit scrollbar pseudo-elements.
        // Returns null when no matching rule exists for the given host element.
        //
        // Precedence contract (mirrors Chrome):
        //   When HasWebkitScrollbarRules(host) is true (i.e., ANY of the three
        //   buckets matches the element) the webkit styles supersede CSS Scrollbars
        //   L1 (scrollbar-color / scrollbar-width) for that element at paint time.
        //   ScrollMath resolvers enforce this by checking webkit presence first.
        //
        // Note: stateProvider is forwarded so ::-webkit-scrollbar:hover etc. can
        // match in future; currently no hover mapping is implemented.
        public ComputedStyle ComputeWebkitScrollbar(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "-webkit-scrollbar", webkitScrollbarRules, stateProvider);
        }

        public ComputedStyle ComputeWebkitScrollbarThumb(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "-webkit-scrollbar-thumb", webkitScrollbarThumbRules, stateProvider);
        }

        public ComputedStyle ComputeWebkitScrollbarTrack(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "-webkit-scrollbar-track", webkitScrollbarTrackRules, stateProvider);
        }

        // ::-webkit-scrollbar-corner — the square overlap region painted when both
        // the vertical and horizontal scrollbars are simultaneously visible. When
        // authored, the `background-color` fills that square. Returns null when no
        // matching rule exists (caller skips the corner fill, matching Chrome's
        // behaviour for overlay-style scrollbars with no authored corner rule).
        public ComputedStyle ComputeWebkitScrollbarCorner(Element host, IElementStateProvider stateProvider = null) {
            return ComputePseudoElement(host, "-webkit-scrollbar-corner", webkitScrollbarCornerRules, stateProvider);
        }

        // Returns true if ANY webkit scrollbar rule matched the host element, i.e.,
        // if webkit styles should override CSS Scrollbars L1 for this element.
        // Implemented as a lightweight null-check on the three computed styles;
        // callers that already computed one style can pass it in and short-circuit.
        public bool HasWebkitScrollbarRules(Element host, IElementStateProvider stateProvider = null) {
            if (host == null) return false;
            return ComputeWebkitScrollbar(host, stateProvider) != null
                || ComputeWebkitScrollbarThumb(host, stateProvider) != null
                || ComputeWebkitScrollbarTrack(host, stateProvider) != null;
        }

        ComputedStyle ComputePseudoElement(Element host, string pseudoName, List<CompiledRule> rules, IElementStateProvider stateProvider) {
            if (host == null) return null;
            if (rules == null || rules.Count == 0) return null;
            var state = stateProvider ?? NullStateProvider.Instance;

            // Resolve the host's own computed style FIRST. ComputePseudoElement
            // shares `scratch` with the regular Compute path, so we must run
            // any recursive Compute call before we populate the scratch's
            // pseudo-specific match list — otherwise ComputeFor's
            // ResetPerElement() would erase the matches we just collected.
            // Compute hits the engine cache when the host was already resolved
            // this pass, which is the steady-state path.
            var hostStyle = Compute(host, state);

            scratch.ResetPerElement();
            var matches = scratch.Matches;
            CollectPseudoMatches(host, state, pseudoName, rules, matches);
            // No matched declarations at all means the author wrote no
            // ::before/::after rule that targets `host`. Skip without paying
            // for an empty ComputedStyle allocation; the caller treats null
            // as "no pseudo box".
            if (matches.Count == 0) return null;

            ExpandShorthandMatchesInto(matches, scratch.ExpandedMatches, scratch);
            var expanded = scratch.ExpandedMatches;
            expanded.Sort(CompareForCascadeDelegate);

            var style = new ComputedStyle(host, 192);
            var perPropertyWinner = scratch.PerPropertyWinner;
            for (int i = 0; i < expanded.Count; i++) {
                var m = expanded[i];
                // Per-property keyword validation: skip invalid declarations so
                // the cascade falls back to the next-lower-priority match.
                if (!CssPropertyKeywordValidator.IsValidValue(m.Declaration.PropertyId, m.Declaration.ValueText)) {
                    continue;
                }
                perPropertyWinner[m.Declaration.Property] = m;
            }

            var rawValues = scratch.RawValues;
            foreach (var kv in perPropertyWinner) {
                rawValues[kv.Key] = kv.Value.Declaration.ValueText;
            }

            // Resolve custom properties first, then seed inherited customs from
            // the host. Pseudo-elements participate in the var() namespace as
            // an inheriting child so authors can reference --tokens declared
            // on the originating element.
            foreach (var kv in rawValues) {
                if (!CssProperties.IsCustomProperty(kv.Key)) continue;
                string resolved = KeywordResolver.Resolve(kv.Key, kv.Value, hostStyle);
                style.Set(kv.Key, resolved);
            }
            if (hostStyle != null) {
                foreach (var kv in hostStyle.Enumerate()) {
                    if (!CssProperties.IsCustomProperty(kv.Key)) continue;
                    if (!style.Contains(kv.Key)) style.Set(kv.Key, kv.Value);
                }
            }

            var customsResolved = scratch.CustomsResolved;
            foreach (var kv in style.Enumerate()) {
                if (!CssProperties.IsCustomProperty(kv.Key)) continue;
                string resolvedCustom = VariableResolver.Resolve(kv.Value, style);
                resolvedCustom = EnvResolver.Resolve(resolvedCustom);
                resolvedCustom = AttrResolver.Resolve(resolvedCustom, host);
                resolvedCustom = LightDarkResolver.Resolve(resolvedCustom, ResolveEffectiveColorScheme(style, mediaContext));
                customsResolved[kv.Key] = resolvedCustom;
            }
            foreach (var kv in customsResolved) {
                style.Set(kv.Key, kv.Value);
            }

            foreach (var kv in rawValues) {
                if (CssProperties.IsCustomProperty(kv.Key)) continue;
                // CSS Custom Properties L1 §3 — invalid-at-computed-value-time:
                // an unresolvable var() with no fallback drops the entire
                // declaration; FillInherited below pulls the host's value
                // (for inherited properties) or the property's initial
                // value (non-inherited).
                if (!VariableResolver.TryResolve(kv.Value, style, out string withVars)) {
                    continue;
                }
                // env() resolved at the same phase as var(); unresolvable
                // env() with no fallback ALSO drops the declaration.
                if (!EnvResolver.TryResolve(withVars, out string withEnv)) {
                    continue;
                }
                // `content` resolves attr() as part of ResolveContentString
                // (which preserves the literal text without re-quoting). The
                // generic AttrResolver pass would unquote it into a bare
                // ident that ResolveContentString rejects, so skip it here
                // and let the consumer handle attr() with the host.
                string withAttr = kv.Key == "content"
                    ? withEnv
                    : AttrResolver.Resolve(withEnv, host);
                // Mirror ComputeFor: honour the pseudo's own (or inherited)
                // color-scheme when resolving light-dark() in regular
                // properties. The custom-property pass above already does
                // this; the regular-property pass previously bypassed
                // ResolveEffectiveColorScheme and read MediaContext.ColorScheme
                // directly, so a host with `color-scheme: dark` had its
                // `::before { color: light-dark(#fff, #000) }` resolve against
                // the document scheme instead of the host's.
                string withLightDark = LightDarkResolver.Resolve(withAttr, ResolveEffectiveColorScheme(style, mediaContext));
                string resolved = KeywordResolver.Resolve(kv.Key, withLightDark, hostStyle);
                style.Set(kv.Key, resolved);

                // Mirror the post-resolution shorthand expansion in ComputeFor: a
                // shorthand whose raw value contains var() bypassed pre-cascade
                // expansion, so we re-expand here after var() substitution to
                // populate longhands that paint code reads (background-color,
                // border-*-color, etc.).
                if (ContainsSubstitutionMarker(kv.Value) && Weva.Css.Cascade.Shorthands.ShorthandRegistry.TryGet(kv.Key, out var lateExpander)) {
                    foreach (var lh in lateExpander.Expand(resolved ?? "")) {
                        if (rawValues.ContainsKey(lh.Key)) continue;
                        string lhResolved = KeywordResolver.Resolve(lh.Key, lh.Value, hostStyle);
                        style.Set(lh.Key, lhResolved);
                    }
                }
            }

            // Inherit from the host: any inherited property the pseudo didn't
            // set takes the host's value; non-inherited properties fall back
            // to their initial. This is exactly FillInherited's contract, but
            // with the host playing the role of "parent style".
            FillInherited(style, hostStyle);

            return style;
        }

        void CollectPseudoMatches(Element host, IElementStateProvider state, string pseudoName, List<CompiledRule> rules, List<MatchedDeclaration> matches) {
            for (int ri = 0; ri < rules.Count; ri++) {
                var rule = rules[ri];
                if (rule.Media != null && !rule.Media.Evaluate(mediaContext)) continue;
                if (rule.Container != null && !PseudoContainerMatches(host, rule)) continue;
                Element scopeRoot = null;
                if (rule.Scope != null) {
                    scopeRoot = rule.Scope.FindScopeRoot(host, state);
                    if (scopeRoot == null) continue;
                }
                if (!SelectorMatcher.MatchesPseudoElement(rule.Selector, pseudoName, host, state, scopeRoot)) continue;
                string selectorText = rule.Selector.SourceText;
                int declIndex = 0;
                foreach (var decl in rule.Declarations) {
                    matches.Add(new MatchedDeclaration(decl, rule.Origin, rule.Selector.Specificity, rule.SourceIndex, false, declIndex, rule.LayerOrdinal, selectorText));
                    declIndex++;
                }
            }
        }

        // Mirror of BackdropContainerMatches: pseudo-element rules' container
        // resolution is uncached because their (host, ruleIdx) coordinates
        // collide with the regular rule index space; each match pays a fresh
        // ContainerResolver.Resolve call. Pseudo rules are typically scarce,
        // so caching wouldn't pay off.
        bool PseudoContainerMatches(Element host, CompiledRule rule) {
            var ctx = elementToBoxLookup != null
                ? Weva.Css.Container.ContainerResolver.Resolve(host, rule.ContainerName, elementToBoxLookup)
                : Weva.Css.Container.ContainerContext.None;
            return rule.Container.Evaluate(ctx);
        }

        // Decodes the cascaded `content` value into the literal text the
        // pseudo box should render. Returns:
        //   - null when no box should be generated (`normal` / `none` /
        //     unset / unknown forms like url() with no other segments).
        //   - "" for `content: ""` — an empty string deliberately produces
        //     a visible-but-text-less box (used for decorative absolutely-
        //     positioned overlays, e.g. mountain pieces in the demo).
        //   - the decoded text for single or multi-segment content lists
        //     combining quoted strings, attr(), counter(), and counters().
        //
        // CSS Generated Content L3 §2 — multi-segment support:
        //   content: "Hello " attr(data-name) → "Hello world" (when data-name="world")
        //   content: counter(c) " items" → "3 items" (when counter c=3)
        //   content: counters(item, ".") → "1.2.3" (from nested scopes)
        //
        // A segment that contains url() with no resolvable text partner
        // still returns null — url-only content is not representable as text.
        public static string ResolveContentString(string raw) {
            return ResolveContentString(raw, null, null);
        }

        // Overload that accepts the originating element so `attr(name)` and
        // `attr(name, "fallback")` can be resolved against the host's
        // attributes. counter() / counters() still need a CounterContext.
        public static string ResolveContentString(string raw, Element host) {
            return ResolveContentString(raw, host, null);
        }

        // Full overload: element for attr() and a counter context for
        // counter() / counters(). Pass null for either when not available;
        // the corresponding functions return empty string in that case.
        //
        // CSS Generated Content L3 §2 — ICounterContext contract:
        //   counter(name)       → ctx.GetCounterValue(name, "decimal")
        //   counter(name,style) → ctx.GetCounterValue(name, style)
        //   counters(name,sep)  → string.Join(sep, ctx.GetCounterValues(name, "decimal"))
        //   counters(name,sep,style) → string.Join(sep, ctx.GetCounterValues(name, style))
        //
        // CSS Generated Content L3 §3.1 — quote keyword contract:
        //   open-quote    → insert open string for current depth, then depth++
        //   close-quote   → depth-- (≥0), then insert close string for new depth
        //   no-open-quote → depth++ only, no insertion
        //   no-close-quote → depth-- (≥0) only, no insertion
        //   `quotes: none` → open/close keywords produce "" but still adjust depth
        public static string ResolveContentString(string raw, Element host, ICounterContext counterCtx) {
            return ResolveContentString(raw, host, counterCtx, null);
        }

        // Overload that also accepts the resolved `quotes` property value from
        // the pseudo-element's computed style. `quotesValue` may be null (treated
        // as "auto"), "none", "auto", or a list of <string> <string> pairs.
        public static string ResolveContentString(string raw, Element host, ICounterContext counterCtx, string quotesValue) {
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw == "normal" || raw == "none") return null;
            string s = raw.Trim();
            if (s.Length == 0) return null;

            // Tokenise the content value into segments and concatenate them.
            // A single quoted-string is the common fast path.
            // Segments: quoted-string | attr(...) | counter(...) | counters(...) | url(...) | quote keywords
            // url() segments that aren't paired with text produce null overall.
            var segments = TokenizeContentSegments(s);
            if (segments == null) return null;

            // Parse the quotes property for quote-keyword resolution.
            // Parsed lazily below when a quote keyword segment is encountered.
            string[][] quotePairs = null;
            bool quotePairsResolved = false;
            bool quotesNone = quotesValue == "none";

            // Single-segment fast path (preserves existing behaviour for the common cases).
            if (segments.Count == 1) {
                var seg = segments[0];
                switch (seg.Kind) {
                    case ContentSegmentKind.QuotedString:
                        return seg.Text;
                    case ContentSegmentKind.Attr:
                        if (host == null) return null;
                        return ResolveAttrSegment(seg.Text, host);
                    case ContentSegmentKind.Counter:
                        // null ctx → no counter scope info; CSS spec says an
                        // unresolvable counter() produces an empty string, not
                        // "suppress box". Return "" so the pseudo box still exists.
                        if (counterCtx == null) return "";
                        return ResolveCounterSegment(seg.Text, counterCtx);
                    case ContentSegmentKind.Counters:
                        if (counterCtx == null) return "";
                        return ResolveCountersSegment(seg.Text, counterCtx);
                    case ContentSegmentKind.OpenQuote: {
                        if (!quotePairsResolved) { quotePairs = ParseQuotePairs(quotesValue); quotePairsResolved = true; }
                        int depth = counterCtx?.QuoteDepth ?? 0;
                        counterCtx?.IncrementQuoteDepth();
                        if (quotesNone) return "";
                        return GetQuoteString(quotePairs, depth, true);
                    }
                    case ContentSegmentKind.CloseQuote: {
                        if (!quotePairsResolved) { quotePairs = ParseQuotePairs(quotesValue); quotePairsResolved = true; }
                        counterCtx?.DecrementQuoteDepth();
                        int depth = counterCtx?.QuoteDepth ?? 0;
                        if (quotesNone) return "";
                        return GetQuoteString(quotePairs, depth, false);
                    }
                    case ContentSegmentKind.NoOpenQuote:
                        counterCtx?.IncrementQuoteDepth();
                        return "";
                    case ContentSegmentKind.NoCloseQuote:
                        counterCtx?.DecrementQuoteDepth();
                        return "";
                    default:
                        return null; // url() or unknown — no text representation
                }
            }

            // Multi-segment: concatenate all resolvable segments.
            // If ANY segment is url() / unknown with no text partner, the whole
            // value is treated as unsupported (null → no pseudo box).
            var sb = new System.Text.StringBuilder();
            for (int si = 0; si < segments.Count; si++) {
                var seg = segments[si];
                switch (seg.Kind) {
                    case ContentSegmentKind.QuotedString:
                        sb.Append(seg.Text);
                        break;
                    case ContentSegmentKind.Attr: {
                        string attrVal = (host != null) ? ResolveAttrSegment(seg.Text, host) : null;
                        // null from attr() with no fallback means empty (attr present but missing).
                        // null meaning "suppress box" only comes from explicit none fallback.
                        if (attrVal == null) {
                            // attr with no host: treat missing attr as empty string in a multi-segment.
                            // But if ResolveAttrSegment signals "suppress box" via returning null
                            // from a `none` fallback, we'd need a sentinel. For v1 in multi-segment,
                            // treat null-from-missing-attr as "".
                        } else {
                            sb.Append(attrVal);
                        }
                        break;
                    }
                    case ContentSegmentKind.Counter: {
                        if (counterCtx != null) {
                            string cval = ResolveCounterSegment(seg.Text, counterCtx);
                            if (cval != null) sb.Append(cval);
                        }
                        break;
                    }
                    case ContentSegmentKind.Counters: {
                        if (counterCtx != null) {
                            string cval = ResolveCountersSegment(seg.Text, counterCtx);
                            if (cval != null) sb.Append(cval);
                        }
                        break;
                    }
                    case ContentSegmentKind.OpenQuote: {
                        if (!quotePairsResolved) { quotePairs = ParseQuotePairs(quotesValue); quotePairsResolved = true; }
                        int depth = counterCtx?.QuoteDepth ?? 0;
                        counterCtx?.IncrementQuoteDepth();
                        if (!quotesNone) sb.Append(GetQuoteString(quotePairs, depth, true));
                        break;
                    }
                    case ContentSegmentKind.CloseQuote: {
                        if (!quotePairsResolved) { quotePairs = ParseQuotePairs(quotesValue); quotePairsResolved = true; }
                        counterCtx?.DecrementQuoteDepth();
                        int depth = counterCtx?.QuoteDepth ?? 0;
                        if (!quotesNone) sb.Append(GetQuoteString(quotePairs, depth, false));
                        break;
                    }
                    case ContentSegmentKind.NoOpenQuote:
                        counterCtx?.IncrementQuoteDepth();
                        break;
                    case ContentSegmentKind.NoCloseQuote:
                        counterCtx?.DecrementQuoteDepth();
                        break;
                    default:
                        // url() in a multi-segment value: unsupported — return null.
                        return null;
                }
            }
            return sb.ToString();
        }

        // ── Quotes property parser ────────────────────────────────────────────

        // Parses the `quotes` property value into an array of (open, close) pairs.
        // Returns null for "none" (caller checks quotesNone flag instead).
        // Returns a 1-element array with English typographic defaults for "auto"/null.
        // CSS Generated Content L3 §3: `quotes: auto | none | [<string> <string>]+`
        //
        // Chrome's `auto` resolution for lang=en: """ """ "'" "'" (outer/inner).
        // v1 simplification: always use English typographic pairs for auto.
        internal static string[][] ParseQuotePairs(string quotesValue) {
            // Normalise.
            if (string.IsNullOrEmpty(quotesValue)) quotesValue = "auto";
            quotesValue = quotesValue.Trim();

            if (quotesValue == "none") return null; // caller uses quotesNone flag
            if (quotesValue == "auto" || quotesValue == "initial" || quotesValue == "inherit" || quotesValue == "unset") {
                // English typographic pairs: outer " " / inner ' '
                return new[] {
                    new[] { "“", "”" }, // " "  (outer)
                    new[] { "‘", "’" }  // ' '  (inner)
                };
            }

            // Parse quoted-string pairs: <string> <string> [<string> <string>]*
            var pairs = new System.Collections.Generic.List<string[]>(2);
            int i = 0;
            int len = quotesValue.Length;
            while (i < len) {
                // Skip whitespace.
                while (i < len && (quotesValue[i] == ' ' || quotesValue[i] == '\t')) i++;
                if (i >= len) break;
                // Parse open string.
                string open = ParseQuoteString(quotesValue, ref i);
                if (open == null) break;
                // Skip whitespace.
                while (i < len && (quotesValue[i] == ' ' || quotesValue[i] == '\t')) i++;
                if (i >= len) break;
                // Parse close string.
                string close = ParseQuoteString(quotesValue, ref i);
                if (close == null) break;
                pairs.Add(new[] { open, close });
            }

            if (pairs.Count == 0) {
                // Malformed — fall back to English typographic pairs.
                return new[] {
                    new[] { "“", "”" },
                    new[] { "‘", "’" }
                };
            }
            return pairs.ToArray();
        }

        // Parses a single quoted CSS string from `s` starting at `i`.
        // Updates `i` past the closing quote. Returns null if not a quoted string.
        static string ParseQuoteString(string s, ref int i) {
            if (i >= s.Length) return null;
            char q = s[i];
            if (q != '"' && q != '\'') return null;
            i++; // past opening quote
            var sb = new System.Text.StringBuilder();
            while (i < s.Length) {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                if (c == q) { i++; break; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Returns the open or close string for the given nesting depth.
        // Depth is clamped to the last pair when it exceeds the pairs array.
        // English typographic defaults used when pairs is null (auto).
        static string GetQuoteString(string[][] pairs, int depth, bool open) {
            if (pairs == null || pairs.Length == 0) {
                // Fallback to English typographic pair.
                if (open) return depth == 0 ? "“" : "‘";
                return depth == 0 ? "”" : "’";
            }
            int idx = depth < pairs.Length ? depth : pairs.Length - 1;
            return open ? pairs[idx][0] : pairs[idx][1];
        }

        // ── Segment tokenizer ────────────────────────────────────────────────

        enum ContentSegmentKind {
            QuotedString,
            Attr,
            Counter,
            Counters,
            Url,
            // CSS Generated Content L3 §3.1 — quote depth keywords.
            OpenQuote,
            CloseQuote,
            NoOpenQuote,
            NoCloseQuote,
            Unknown,
        }

        struct ContentSegment {
            public ContentSegmentKind Kind;
            // For QuotedString: decoded text. For function forms: the raw
            // argument string inside the outer parens. Unused for quote keywords.
            public string Text;
        }

        // Splits a CSS `content` value into segments. Returns null if the
        // value contains syntax we cannot represent as text (e.g. a leading
        // slash-alt-text form, or completely unparseable input).
        static System.Collections.Generic.List<ContentSegment> TokenizeContentSegments(string s) {
            var list = new System.Collections.Generic.List<ContentSegment>(4);
            int i = 0;
            int len = s.Length;
            while (i < len) {
                // Skip inter-segment whitespace.
                while (i < len && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
                if (i >= len) break;

                char c = s[i];

                // ── Quoted string ───────────────────────────────────────────
                if (c == '"' || c == '\'') {
                    char quote = c;
                    int start = i + 1;
                    var sbStr = new System.Text.StringBuilder();
                    i++; // past opening quote
                    bool closed = false;
                    while (i < len) {
                        char ch = s[i];
                        if (ch == '\\' && i + 1 < len) {
                            sbStr.Append(s[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (ch == quote) { i++; closed = true; break; }
                        sbStr.Append(ch);
                        i++;
                    }
                    if (!closed) return null; // malformed
                    list.Add(new ContentSegment { Kind = ContentSegmentKind.QuotedString, Text = sbStr.ToString() });
                    continue;
                }

                // ── Ident or function token ─────────────────────────────────
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-') {
                    int nameStart = i;
                    while (i < len && s[i] != '(' && s[i] != ' ' && s[i] != '\t'
                           && s[i] != '\r' && s[i] != '\n') i++;
                    string token = s.Substring(nameStart, i - nameStart);

                    if (i < len && s[i] == '(') {
                        // ── Function token: attr(, counter(, counters(, url( ─────
                        int parenOpen = i;
                        int parenClose = FindMatchingParenContent(s, parenOpen);
                        if (parenClose < 0) return null;
                        string inside = s.Substring(parenOpen + 1, parenClose - parenOpen - 1);
                        i = parenClose + 1;

                        string fn = token.ToLowerInvariant();
                        ContentSegmentKind fkind;
                        switch (fn) {
                            case "attr":     fkind = ContentSegmentKind.Attr; break;
                            case "counter":  fkind = ContentSegmentKind.Counter; break;
                            case "counters": fkind = ContentSegmentKind.Counters; break;
                            case "url":      fkind = ContentSegmentKind.Url; break;
                            default:         fkind = ContentSegmentKind.Unknown; break;
                        }
                        list.Add(new ContentSegment { Kind = fkind, Text = inside });
                    } else {
                        // ── Bare ident ──────────────────────────────────────────
                        // Check for quote-depth keywords (CSS Generated Content L3 §3.1).
                        ContentSegmentKind ikind;
                        switch (token) {
                            case "open-quote":     ikind = ContentSegmentKind.OpenQuote;    break;
                            case "close-quote":    ikind = ContentSegmentKind.CloseQuote;   break;
                            case "no-open-quote":  ikind = ContentSegmentKind.NoOpenQuote;  break;
                            case "no-close-quote": ikind = ContentSegmentKind.NoCloseQuote; break;
                            default:
                                // `none`, `normal`, or other unknown idents — bail gracefully.
                                return null;
                        }
                        list.Add(new ContentSegment { Kind = ikind });
                    }
                    continue;
                }

                // ── Slash (alt-text separator) ───────────────────────────────
                // `content: url(x) / "alt"` — slash separates image from alt-text.
                // We don't support url() segments, so the whole value is unsupported.
                if (c == '/') return null;

                // Anything else: unknown token — bail.
                return null;
            }
            return list.Count > 0 ? list : null;
        }

        // Finds the index of the closing ')' that matches the '(' at openIdx.
        // Handles nested parens and quoted strings inside them.
        static int FindMatchingParenContent(string s, int openIdx) {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++) {
                char c = s[i];
                if (c == '"' || c == '\'') {
                    char q = c;
                    i++;
                    while (i < s.Length) {
                        if (s[i] == '\\') { i += 2; continue; }
                        if (s[i] == q) break;
                        i++;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        // ── attr() segment resolver ─────────────────────────────────────────

        // Resolves the `inside` of an `attr(...)` call against `host`.
        // Returns the attribute value, decoded fallback, or "" (attr present
        // but missing with no fallback). Returns null ONLY when the fallback
        // is the literal `none` (signals "suppress the pseudo box").
        static string ResolveAttrSegment(string inside, Element host) {
            // Split on first top-level comma.
            SplitFirstArg(inside, out string head, out string rest);
            head = head.Trim();
            if (head.Length == 0) return "";
            // head may be "<name>" or "<name> <type>"; ignore <type> for string content.
            int sp = -1;
            for (int k = 0; k < head.Length; k++) {
                if (head[k] == ' ' || head[k] == '\t') { sp = k; break; }
            }
            string name = sp < 0 ? head : head.Substring(0, sp).Trim();
            if (name.Length == 0) return "";
            string attrValue = host.GetAttribute(name);
            if (attrValue != null) return attrValue;
            // Attribute absent — use fallback.
            if (rest == null) return "";
            string fallback = rest.Trim();
            if (fallback == "none") return null; // suppress pseudo box
            if (fallback.Length >= 2) {
                char q = fallback[0];
                if ((q == '"' || q == '\'') && fallback[fallback.Length - 1] == q) {
                    return DecodeQuotedString(fallback, 0, fallback.Length);
                }
            }
            return "";
        }

        // Kept for backward compatibility (single-segment attr() shortcut).
        static string ResolveAttrContent(string s, Element host) {
            // s is "attr(...)"; extract inside.
            int parenOpen = s.IndexOf('(');
            if (parenOpen < 0) return null;
            int parenClose = FindMatchingParenContent(s, parenOpen);
            if (parenClose < 0) return null;
            string inside = s.Substring(parenOpen + 1, parenClose - parenOpen - 1);
            return ResolveAttrSegment(inside, host);
        }

        // ── counter() segment resolver ──────────────────────────────────────

        // Resolves the `inside` of a `counter(...)` call.
        // CSS Lists L3 §2.1: counter(name) | counter(name, style)
        // Returns the formatted counter value from ctx, or "" if ctx is null
        // or the counter is not defined.
        static string ResolveCounterSegment(string inside, ICounterContext ctx) {
            SplitFirstArg(inside, out string name, out string styleArg);
            name = name.Trim();
            if (name.Length == 0) return "";
            string style = styleArg != null ? styleArg.Trim() : "decimal";
            if (style.Length == 0) style = "decimal";
            int value = ctx.GetCounterValue(name);
            if (value == ICounterContext.NotFound) return "";
            return FormatCounterValue(value, style);
        }

        // ── counters() segment resolver ─────────────────────────────────────

        // Resolves the `inside` of a `counters(...)` call.
        // CSS Lists L3 §2.2: counters(name, sep) | counters(name, sep, style)
        // Returns the joined ancestor chain, or "" when the counter is absent.
        static string ResolveCountersSegment(string inside, ICounterContext ctx) {
            // Split: name, sep, [style]
            SplitFirstArg(inside, out string name, out string rest1);
            name = name.Trim();
            if (name.Length == 0) return "";

            string sep = "";
            string style = "decimal";
            if (rest1 != null) {
                SplitFirstArg(rest1, out string sepRaw, out string rest2);
                sepRaw = sepRaw.Trim();
                // Separator is a quoted string.
                if (sepRaw.Length >= 2) {
                    char q = sepRaw[0];
                    if ((q == '"' || q == '\'') && sepRaw[sepRaw.Length - 1] == q) {
                        sep = DecodeQuotedString(sepRaw, 0, sepRaw.Length);
                    } else {
                        sep = sepRaw; // bare token — use as-is
                    }
                }
                if (rest2 != null) {
                    style = rest2.Trim();
                    if (style.Length == 0) style = "decimal";
                }
            }

            int[] values = ctx.GetCounterValues(name);
            if (values == null || values.Length == 0) return "";

            var sb = new System.Text.StringBuilder();
            for (int vi = 0; vi < values.Length; vi++) {
                if (vi > 0) sb.Append(sep);
                sb.Append(FormatCounterValue(values[vi], style));
            }
            return sb.ToString();
        }

        // ── Counter value formatting ─────────────────────────────────────────

        // Formats a single integer counter value per CSS Counter Styles 3.
        // v1 supports: decimal (default), decimal-leading-zero,
        // upper-roman, lower-roman, upper-alpha / upper-latin,
        // lower-alpha / lower-latin, plus the bullet styles `disc`,
        // `circle`, `square`, and `none` (return their literal symbol /
        // empty string). Unsupported styles fall through to decimal so
        // authored CSS round-trips without breaking the cascade.
        internal static string FormatCounterValue(int value, string style) {
            switch (style) {
                case "decimal-leading-zero":
                    // §6.1: pad to 2 digits when value is in 0..99,
                    // otherwise the decimal representation alone.
                    if (value >= 0 && value <= 99) {
                        return value.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "upper-roman": return ToUpperRoman(value);
                case "lower-roman": return ToLowerRoman(value);
                case "upper-alpha":
                case "upper-latin": return ToAlpha(value, 'A');
                case "lower-alpha":
                case "lower-latin": return ToAlpha(value, 'a');
                // §6.2 simple bullets — non-numeric counter styles. Return
                // the symbol so authors can use them in generated content
                // (rare but spec-valid).
                case "disc":   return "•";
                case "circle": return "◦";
                case "square": return "▪";
                case "none":   return "";
                default:       return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        static string ToUpperRoman(int value) {
            return ToRoman(value).ToUpperInvariant();
        }

        static string ToLowerRoman(int value) {
            return ToRoman(value).ToLowerInvariant();
        }

        static string ToRoman(int n) {
            if (n <= 0) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Standard subtractive notation up to 3999.
            int[] vals  = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] syms = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < vals.Length; i++) {
                while (n >= vals[i]) { sb.Append(syms[i]); n -= vals[i]; }
            }
            return sb.ToString();
        }

        static string ToAlpha(int n, char start) {
            if (n <= 0) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // 1→A, 2→B, …, 26→Z, 27→AA, etc.
            var sb = new System.Text.StringBuilder();
            while (n > 0) {
                n--; // shift to 0-based
                sb.Insert(0, (char)(start + (n % 26)));
                n /= 26;
            }
            return sb.ToString();
        }

        // ── Shared arg parsing helpers ────────────────────────────────────────

        // Splits `s` at the first top-level comma, respecting parens and
        // quoted strings. Returns (head, rest) where rest includes everything
        // after the comma (not trimmed). If no comma, rest is null.
        static void SplitFirstArg(string s, out string head, out string rest) {
            int depth = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '"' || c == '\'') {
                    char q = c;
                    i++;
                    while (i < s.Length) {
                        if (s[i] == '\\') { i += 2; continue; }
                        if (s[i] == q) break;
                        i++;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == ',' && depth == 0) {
                    head = s.Substring(0, i);
                    rest = s.Substring(i + 1);
                    return;
                }
            }
            head = s;
            rest = null;
        }

        // Decodes a CSS quoted string slice (includes the surrounding quotes).
        static string DecodeQuotedString(string s, int start, int end) {
            if (end - start < 2) return "";
            char quote = s[start];
            var sb = new System.Text.StringBuilder(end - start - 2);
            for (int i = start + 1; i < end - 1; i++) {
                char c = s[i];
                if (c == '\\' && i + 1 < end - 1) {
                    sb.Append(s[i + 1]);
                    i++;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        static bool StartsWithCi(string s, string prefix) {
            if (s.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) {
                if (char.ToLowerInvariant(s[i]) != char.ToLowerInvariant(prefix[i])) return false;
            }
            return true;
        }
    }
}
