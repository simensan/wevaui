using System.Collections.Generic;
using Weva.Dom;
using Weva.Layout.Containment;

namespace Weva.Css.Cascade {
    // CSS Lists L3 §5 — counter scope implementation.
    //
    // CounterContext is built by a depth-first tree walk (tree order) from the
    // document root to the target element, applying counter-reset /
    // counter-increment / counter-set from each element's ComputedStyle.
    //
    // The counter model uses a per-name scope stack:
    //   counter-reset: name [n=0]  — push a new scope entry on the stack.
    //   counter-increment: name [n=1] — add n to the top-of-stack for name.
    //     If no scope exists yet, implicitly creates one at 0.
    //   counter-set: name [n=0]  — set the top-of-stack for name to n.
    //     If no scope exists yet, implicitly creates one at 0.
    //
    // When exiting an element that reset a counter, the pushed scope level is
    // popped — but only from the live view used by counter(). The counters()
    // function needs all active scope levels at the moment the target element
    // is reached, which are those that have NOT yet been popped (i.e. their
    // owning element is still an ancestor of the target).
    //
    // Tree walk correctness:
    //   - For each element in pre-order: apply counter-reset (push), increment,
    //     set. Then recurse into children. After children, pop any scope levels
    //     created by this element's counter-reset (unless this IS the target —
    //     in that case we stop before descending).
    //
    // The result is consumed by CascadeEngine.ResolveContentString via
    // ICounterContext so counter() and counters() in pseudo-element content
    // resolve to actual values without requiring a separate document-level
    // pass.
    public sealed class CounterContext : ICounterContext {
        // Per counter name: stack of scope values from outermost (bottom)
        // to innermost (top = last). List<int> is used; the list IS the
        // scope stack (index 0 = outermost, index Count-1 = innermost).
        readonly Dictionary<string, List<int>> stacks = new();

        // CSS Generated Content L3 §3.1 — quote nesting depth.
        // Accumulated in document order by scanning open-quote / close-quote /
        // no-open-quote / no-close-quote keywords in ::before / ::after content.
        // 0 = outermost level (initial state before any quotes are opened).
        int quoteDepth;
        public int QuoteDepth => quoteDepth;
        public void IncrementQuoteDepth() { quoteDepth++; }
        public void DecrementQuoteDepth() { if (quoteDepth > 0) quoteDepth--; }

        // Factory: walk the document tree in tree order until `target` is
        // fully processed (counter-reset/increment/set applied), then capture
        // the live counter state. The walk tracks scope push/pop to maintain
        // correctness across siblings and uncles.
        //
        // `styleOf` is the BoxBuilder's own styleOf delegate — no additional
        // allocation required on the hot path.
        //
        // Returns null only when target or styleOf is null, in which case
        // ResolveContentString returns "" for any counter() call.
        public static CounterContext BuildFor(Element target, System.Func<Element, ComputedStyle> styleOf) {
            return BuildFor(target, styleOf, null, null);
        }

        // Full overload: also accepts pseudo-element style resolvers so the
        // tree walk can accumulate quote depth from ::before / ::after content
        // keywords (open-quote, close-quote, no-open-quote, no-close-quote)
        // encountered in document order before the target element.
        //
        // `beforeStyleOf` / `afterStyleOf` may be null — when null the walk
        // skips quote-depth accumulation and quote depth stays 0.
        //
        // `afterPseudo` — when false (default, used for ::before resolution), the
        // tree walk stops before accumulating the target element's own ::before
        // content. The ::before has not "happened yet" at the point of resolution.
        // When true (used for ::after resolution), the walk also accumulates the
        // target's ::before and all descendant pseudo-element content, because
        // those precede ::after in document order.
        public static CounterContext BuildFor(
            Element target,
            System.Func<Element, ComputedStyle> styleOf,
            System.Func<Element, ComputedStyle> beforeStyleOf,
            System.Func<Element, ComputedStyle> afterStyleOf,
            bool afterPseudo = false) {
            if (target == null || styleOf == null) return null;

            // Find the document root. We need the document (or the topmost
            // ancestor) to start the tree walk from. Walk up parent chain to
            // find the owning Document or topmost Element.
            Node root = target;
            while (root.Parent != null) root = root.Parent;

            var ctx = new CounterContext();
            bool found = false;
            WalkTree(root, target, styleOf, beforeStyleOf, afterStyleOf, ctx, ref found, afterPseudo);
            return ctx;
        }

        // Recursive depth-first (pre-order) tree walk. Applies counter
        // operations for each element and tracks scope push/pop so the state
        // is spec-correct when `target` is reached.
        //
        // Also accumulates quote depth from ::before / ::after content keywords
        // (open-quote, close-quote, no-open-quote, no-close-quote) in document
        // order when `beforeStyleOf` / `afterStyleOf` are provided.
        //
        // `afterPseudo` controls how much content around the target is processed:
        //   false — stop BEFORE accumulating the target's ::before (context is for
        //           the target's ::before itself, which hasn't run yet).
        //   true  — also accumulate the target's ::before AND all descendants
        //           (context is for the target's ::after, which runs last).
        //
        // Returns true when the walk is complete (target found + processed).
        static bool WalkTree(Node node, Element target,
            System.Func<Element, ComputedStyle> styleOf,
            System.Func<Element, ComputedStyle> beforeStyleOf,
            System.Func<Element, ComputedStyle> afterStyleOf,
            CounterContext ctx, ref bool found,
            bool afterPseudo = false) {
            if (found) return true;

            if (node is Element e) {
                var style = styleOf(e);

                // Track which counters this element resets so we can pop
                // those scope levels when we exit the element's subtree.
                List<string> resetNames = null;
                if (style != null) {
                    string resetRaw = style.Get("counter-reset");
                    if (!string.IsNullOrEmpty(resetRaw) && resetRaw != "none") {
                        resetNames = new List<string>();
                        foreach (var (name, value) in ParseCounterList(resetRaw, 0)) {
                            ctx.PushScope(name, value);
                            resetNames.Add(name);
                        }
                    }

                    // Increment and set AFTER reset, per spec order.
                    string incrRaw = style.Get("counter-increment");
                    if (!string.IsNullOrEmpty(incrRaw) && incrRaw != "none") {
                        foreach (var (name, value) in ParseCounterList(incrRaw, 1)) {
                            ctx.IncrementTop(name, value);
                        }
                    }

                    string setRaw = style.Get("counter-set");
                    if (!string.IsNullOrEmpty(setRaw) && setRaw != "none") {
                        foreach (var (name, value) in ParseCounterList(setRaw, 0)) {
                            ctx.SetTop(name, value);
                        }
                    }
                }

                // CSS Generated Content L3 §3.1 — quote depth accumulation.
                //
                // Document order for an element E:
                //   E::before → E's children (each with ::before/::after) → E::after
                //
                // For ::before resolution: accumulate depth for ALL preceding
                // elements. Stop before accumulating the TARGET's own ::before,
                // because that is the pseudo being resolved.
                //
                // For ::after resolution: also accumulate the target's ::before
                // and all descendants' pseudo content, because those precede
                // ::after in document order.
                bool isTarget = ReferenceEquals(e, target);

                // Accumulate ::before quote depth for non-target elements only
                // (for ::before mode), OR for all elements including target (for
                // ::after mode where target::before precedes target::after).
                if (beforeStyleOf != null && (!isTarget || afterPseudo)) {
                    var beforeStyle = beforeStyleOf(e);
                    if (beforeStyle != null) {
                        string contentRaw = beforeStyle.Get("content");
                        if (!string.IsNullOrEmpty(contentRaw)) {
                            AccumulateQuoteDepth(contentRaw, ctx);
                        }
                    }
                }

                // If this IS the target element:
                // - For ::before mode (afterPseudo=false): we have already captured
                //   all preceding depth; stop here (counter state already captured
                //   above via reset/increment/set).
                // - For ::after mode (afterPseudo=true): also descend into children
                //   to accumulate their pseudo content depth, then stop.
                if (isTarget) {
                    if (!afterPseudo) {
                        found = true;
                        return true;
                    }
                    // afterPseudo=true: descend into children to collect their depth.
                    foreach (var child in e.Children) {
                        AccumulateSubtreeQuoteDepth(child, beforeStyleOf, afterStyleOf, ctx);
                    }
                    found = true;
                    return true;
                }

                // CSS Containment L2 §3.3 — style containment boundary.
                //
                // When `contain: style` (or `strict` / `content`, both of which
                // include the style bit per §2.3) is set, counter-increment,
                // counter-set, and counter-reset on DESCENDANTS must not affect
                // counters established outside the boundary.  The boundary element
                // itself is unrestricted (its own counter-reset/increment/set were
                // already applied above, correctly).
                //
                // Implementation: snapshot the full counter value state before
                // descending into children.  If the target is NOT found inside
                // this subtree (so the target is a later sibling or ancestor), we
                // restore the snapshot after the recursion, undoing any mutations
                // that child elements produced.  If the target IS inside (found
                // becomes true), we keep the accumulated state — the context is
                // being built FOR an element inside the boundary and the inner
                // increments are correct from its perspective.
                //
                // Quote depth mirrors the same isolation: a style-contained
                // subtree's quote operations don't leak outward (and vice versa).
                Dictionary<string, int[]> styleContainSnapshot = null;
                int quoteDepthSnapshot = ctx.quoteDepth;
                bool hasStyleContain = style != null && ContainmentResolver.HasStyle(style);
                if (hasStyleContain) {
                    styleContainSnapshot = ctx.SnapshotStackTops();
                }

                // Recurse into children.
                foreach (var child in e.Children) {
                    if (WalkTree(child, target, styleOf, beforeStyleOf, afterStyleOf, ctx, ref found, afterPseudo)) break;
                }

                // If we didn't find the target inside this subtree, pop the
                // scopes this element created (exit its subtree), and restore
                // the counter value snapshot if style containment applies.
                if (!found) {
                    // For style-contained elements: restore the counter stack AND
                    // quote depth BEFORE running ::after, because the restore
                    // is meant to undo CHILDREN's mutations. The boundary element's
                    // own ::before and ::after are unrestricted; ::after runs after
                    // the children-isolation restore, not before it.
                    //
                    // Counter stacks: restored here (same as before; counter-reset
                    // scope pops happen next which handle the boundary element's own
                    // scopes separately).
                    if (hasStyleContain) {
                        ctx.RestoreStackTops(styleContainSnapshot);
                        // Restore quote depth to post-::before state (undo children's
                        // mutations). The boundary element's ::after will then run
                        // on top of this, which is the spec-correct order.
                        ctx.quoteDepth = quoteDepthSnapshot;
                    }

                    // ::after is processed after children, in document order.
                    // For style-contained elements, ::after runs AFTER the isolation
                    // restore (it is the boundary element's own unrestricted operation).
                    if (afterStyleOf != null) {
                        var afterStyle = afterStyleOf(e);
                        if (afterStyle != null) {
                            string contentRaw = afterStyle.Get("content");
                            if (!string.IsNullOrEmpty(contentRaw)) {
                                AccumulateQuoteDepth(contentRaw, ctx);
                            }
                        }
                    }

                    if (resetNames != null) {
                        foreach (var name in resetNames) {
                            ctx.PopScope(name);
                        }
                    }
                }
            } else if (node is Document doc) {
                // Walk document children (html, etc.).
                foreach (var child in doc.Children) {
                    if (WalkTree(child, target, styleOf, beforeStyleOf, afterStyleOf, ctx, ref found, afterPseudo)) break;
                }
            }

            return found;
        }

        // Accumulate quote depth for an entire subtree rooted at `node`,
        // visiting pseudo-elements in document order. Used for afterPseudo=true
        // to capture the host element's descendant content depth.
        static void AccumulateSubtreeQuoteDepth(Node node,
            System.Func<Element, ComputedStyle> beforeStyleOf,
            System.Func<Element, ComputedStyle> afterStyleOf,
            CounterContext ctx) {
            if (node is Element e) {
                if (beforeStyleOf != null) {
                    var bs = beforeStyleOf(e);
                    if (bs != null) AccumulateQuoteDepth(bs.Get("content"), ctx);
                }
                foreach (var child in e.Children) {
                    AccumulateSubtreeQuoteDepth(child, beforeStyleOf, afterStyleOf, ctx);
                }
                if (afterStyleOf != null) {
                    var as_ = afterStyleOf(e);
                    if (as_ != null) AccumulateQuoteDepth(as_.Get("content"), ctx);
                }
            }
        }

        // Scan a raw CSS `content` property value for quote-depth keywords
        // and adjust ctx.quoteDepth accordingly. This is used during the tree
        // walk to accumulate depth from preceding pseudo-elements.
        //
        // The content value is parsed at the token level — we look for bare
        // ident tokens `open-quote`, `close-quote`, `no-open-quote`,
        // `no-close-quote` (possibly mixed with quoted strings / function calls).
        static void AccumulateQuoteDepth(string contentRaw, CounterContext ctx) {
            if (string.IsNullOrEmpty(contentRaw)) return;
            if (contentRaw == "normal" || contentRaw == "none") return;
            // Scan for bare ident tokens, skipping quoted strings and parens.
            int i = 0;
            int len = contentRaw.Length;
            while (i < len) {
                char c = contentRaw[i];
                // Skip whitespace.
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                // Skip quoted strings.
                if (c == '"' || c == '\'') {
                    char q = c; i++;
                    while (i < len) {
                        if (contentRaw[i] == '\\' && i + 1 < len) { i += 2; continue; }
                        if (contentRaw[i] == q) { i++; break; }
                        i++;
                    }
                    continue;
                }
                // Skip function calls (attr(), counter(), etc.).
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-') {
                    int nameStart = i;
                    while (i < len && contentRaw[i] != '(' && contentRaw[i] != ' '
                           && contentRaw[i] != '\t' && contentRaw[i] != '\r' && contentRaw[i] != '\n') i++;
                    string token = contentRaw.Substring(nameStart, i - nameStart);
                    if (i < len && contentRaw[i] == '(') {
                        // Function token — skip to matching close paren.
                        int depth = 0;
                        while (i < len) {
                            if (contentRaw[i] == '(') depth++;
                            else if (contentRaw[i] == ')') { depth--; if (depth == 0) { i++; break; } }
                            i++;
                        }
                    } else {
                        // Bare ident token — check for quote keywords.
                        switch (token) {
                            case "open-quote":
                            case "no-open-quote":
                                ctx.quoteDepth++;
                                break;
                            case "close-quote":
                            case "no-close-quote":
                                if (ctx.quoteDepth > 0) ctx.quoteDepth--;
                                break;
                        }
                    }
                    continue;
                }
                // Slash or other: skip.
                i++;
            }
        }

        // ── Style containment snapshot / restore ──────────────────────────────

        // Captures the current top-of-stack value for every counter that has
        // at least one live scope.  Used by the style containment boundary to
        // undo descendant mutations when the target element is not inside the
        // style-contained subtree.
        //
        // Only the top-of-stack value for each counter is captured, because
        // `counter-reset` (which push a new scope) is handled by the existing
        // PopScope mechanism.  What we need to undo is any direct mutation of
        // an already-existing scope entry via IncrementTop / SetTop.
        Dictionary<string, int[]> SnapshotStackTops() {
            var snap = new Dictionary<string, int[]>(stacks.Count);
            foreach (var kv in stacks) {
                if (kv.Value != null && kv.Value.Count > 0) {
                    // Copy the full list so that any scope pushes/pops AND
                    // value mutations inside the boundary are fully rolled back.
                    snap[kv.Key] = kv.Value.ToArray();
                }
            }
            return snap;
        }

        // Restores counter value lists to the snapshot taken before entering a
        // style-contained subtree.  Any counters that were created entirely
        // inside the boundary (not present in the snapshot) are removed.
        void RestoreStackTops(Dictionary<string, int[]> snap) {
            // Remove counters that were created inside the boundary.
            var toRemove = new List<string>();
            foreach (var name in stacks.Keys) {
                if (!snap.ContainsKey(name)) toRemove.Add(name);
            }
            foreach (var name in toRemove) stacks.Remove(name);

            // Restore values for counters that existed before entering.
            foreach (var kv in snap) {
                if (!stacks.TryGetValue(kv.Key, out var list)) {
                    list = new List<int>();
                    stacks[kv.Key] = list;
                }
                list.Clear();
                foreach (var v in kv.Value) list.Add(v);
            }
        }

        // ── Scope-stack mutators (private) ────────────────────────────────────

        void PushScope(string name, int value) {
            if (!stacks.TryGetValue(name, out var list)) {
                list = new List<int>();
                stacks[name] = list;
            }
            list.Add(value);
        }

        void PopScope(string name) {
            if (stacks.TryGetValue(name, out var list) && list.Count > 0) {
                list.RemoveAt(list.Count - 1);
            }
        }

        void IncrementTop(string name, int delta) {
            if (!stacks.TryGetValue(name, out var list) || list.Count == 0) {
                // Implicit scope creation (CSS Lists L3 §5).
                if (list == null) { list = new List<int>(); stacks[name] = list; }
                list.Add(0);
            }
            list[list.Count - 1] += delta;
        }

        void SetTop(string name, int value) {
            if (!stacks.TryGetValue(name, out var list) || list.Count == 0) {
                // Implicit scope creation (CSS Lists L3 §5).
                if (list == null) { list = new List<int>(); stacks[name] = list; }
                list.Add(0);
            }
            list[list.Count - 1] = value;
        }

        // ── Counter list parser ───────────────────────────────────────────────

        // Parses a CSS counter list value: `none | [ <ident> <integer>? ]+`
        // `defaultValue` is used when no explicit integer follows the ident
        // (0 for counter-reset/counter-set, 1 for counter-increment per spec).
        static IEnumerable<(string name, int value)> ParseCounterList(string s, int defaultValue) {
            if (string.IsNullOrEmpty(s) || s == "none") yield break;
            int i = 0;
            int len = s.Length;
            while (i < len) {
                // Skip whitespace.
                while (i < len && IsWs(s[i])) i++;
                if (i >= len) yield break;

                // Read identifier.
                if (!IsIdentStart(s[i])) { i++; continue; } // skip unknown token
                int nameStart = i;
                while (i < len && IsIdentChar(s[i])) i++;
                string name = s.Substring(nameStart, i - nameStart);
                if (name == "none") continue; // shouldn't appear mid-list but be safe

                // Skip whitespace.
                while (i < len && IsWs(s[i])) i++;

                // Optional integer.
                int value = defaultValue;
                if (i < len && (s[i] == '-' || s[i] == '+' || (s[i] >= '0' && s[i] <= '9'))) {
                    int sign = 1;
                    if (s[i] == '-') { sign = -1; i++; }
                    else if (s[i] == '+') { i++; }
                    int numStart = i;
                    while (i < len && s[i] >= '0' && s[i] <= '9') i++;
                    if (i > numStart && int.TryParse(s.Substring(numStart, i - numStart),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int parsed)) {
                        value = sign * parsed;
                    }
                }
                yield return (name, value);
            }
        }

        static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';
        static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-';
        static bool IsIdentChar(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');

        // ── ICounterContext ───────────────────────────────────────────────────

        // Returns the innermost (most recently reset/incremented) value for
        // `name`, or NotFound if no scope exists for the counter.
        public int GetCounterValue(string name) {
            if (name == null || !stacks.TryGetValue(name, out var list) || list.Count == 0)
                return ICounterContext.NotFound;
            return list[list.Count - 1];
        }

        // Returns all scope values for `name` from outermost to innermost,
        // or null when the counter has no scope. Used by counters() to build
        // the ancestor scope chain string.
        public int[] GetCounterValues(string name) {
            if (name == null || !stacks.TryGetValue(name, out var list) || list.Count == 0)
                return null;
            return list.ToArray();
        }
    }
}
