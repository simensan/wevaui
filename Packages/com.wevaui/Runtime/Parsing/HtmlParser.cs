using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Parsing {
    public static class HtmlParser {
        // HTML5 §13.2.6.2 head-content elements. When the source omits an
        // explicit `<html>` wrapper, tokens for these tags go into the
        // synthetic `<head>`; everything else triggers an implicit close of
        // `<head>` and falls into the synthetic `<body>`.
        //
        // Note: `<template>` is permitted in both head and body per HTML5,
        // but Weva's binding layer treats `<template>` as body content
        // (data-each / data-if blocks), so we keep it OUT of this set —
        // fragments that start with `<template>` open the synthetic body.
        static readonly HashSet<string> headContentElements = new() {
            "base", "basefont", "bgsound", "link", "meta", "noscript",
            "script", "style", "title"
        };

        // Head-content elements whose TEXT is the element's own (RAWTEXT/RCDATA)
        // content, not document body content. While one of these is open inside
        // <head>, a Text token is its data and must NOT trigger the head→body
        // transition — otherwise EnsureBody() closes <head> mid-element and the
        // synthetic <body> nests inside the still-open <style>/<title>, orphaning
        // the real content (the cause of a blank document whose source starts
        // with `<style>…</style><div>…` and no explicit <body>).
        static readonly HashSet<string> headTextContentElements = new() {
            "script", "style", "title"
        };

        // Inline formatting elements per HTML5 §13.2.4.2 "the list of active
        // formatting elements". When a block-level start tag would close an
        // open <p> while one of these is nested inside, we re-create them
        // after the block, matching the Chrome / Firefox DOM shape for
        // malformed-but-common HTML such as
        // `<p>Click <a><div>here</div></a> to start</p>`. See AAA notes in
        // BuildTree.
        //
        // Note: <span> is intentionally NOT in this set. The HTML5 spec
        // classifies it as an "ordinary" element, not a formatting element —
        // when a <span> contains a <div> inside a <p>, the spec also closes
        // the <p>, but the <span> is discarded structurally rather than
        // re-opened around the block. We deliberately keep the lenient
        // legacy behavior for <span> (block nested inside) so that author
        // CSS that relies on `<span style="display:inline-block">` wrapping
        // block descendants still works.
        static readonly HashSet<string> formattingElements = new() {
            "a", "b", "big", "code", "em", "font", "i", "nobr", "s",
            "small", "strike", "strong", "tt", "u"
        };

        // Block-level start tags that close an open <p> per the
        // HTML Living Standard "Optional tags" section. Mirrors
        // HtmlElements.ShouldImplicitlyClose's <p> handler — kept as a
        // separate predicate so AAA can branch on "would this close p?"
        // without duplicating the closesOpenP set.
        static bool ClosesOpenP(string startTag) {
            return HtmlElements.ShouldImplicitlyClose("p", startTag);
        }

        public static Document Parse(string source, ParseOptions options = null) {
            options ??= new ParseOptions();
            var tokens = new HtmlTokenizer(source).Tokenize();
            tokens = NormalizeFragment(tokens);
            return BuildTree(tokens, options);
        }

        // Fragment normalization — HTML5 §13.2.5 "tree construction" approximated
        // at the token level. Browsers always produce a `Document > <html> >
        // <head> + <body>` shape regardless of whether the source omits any of
        // those wrappers. Weva authors commonly hand the parser raw fragments
        // like `<main class="hud">...</main>` and expect them to behave the same
        // as the browser-rendered preview, where the preview HTML shell includes
        // explicit `<html><body>` tags.
        //
        // Without this pass, top-level Elements appear as direct children of
        // Document. That breaks two things:
        //   1. `:root` (defined as the first Element child of Document) matches
        //      whatever element happened to come first in the fragment — often a
        //      `<link>` or `<style>` rather than the real content root. Custom
        //      properties declared on `:root` then live on `<link>` (display:
        //      none) and never inherit down to the content tree.
        //   2. The `html`/`body` background-propagation pass in BoxBuilder is a
        //      no-op because neither element exists.
        //
        // The fix synthesizes `<html>`, `<head>`, `<body>` start/end tokens
        // around the existing token stream when any of them is missing. The
        // resulting token list parses through BuildTree unchanged and produces
        // the expected `Document > <html> > <head> + <body>` shape. If the
        // source already includes an explicit `<html>` (full document), no
        // synthesis happens.
        //
        // Whitespace-only / empty / DOCTYPE-only inputs deliberately produce
        // an empty Document (no wrappers) to preserve the existing
        // "empty string → 0 children" contract.
        static List<HtmlToken> NormalizeFragment(List<HtmlToken> tokens) {
            // Decide whether the source has an explicit <html> start tag.
            // We trust the author's structure in that case and emit tokens
            // verbatim. Otherwise we splice in synthetic wrappers.
            bool hasExplicitHtml = false;
            bool hasContent = false;
            foreach (var t in tokens) {
                if (t.Kind == HtmlTokenKind.StartTag) {
                    if (t.Name == "html") hasExplicitHtml = true;
                    hasContent = true;
                } else if (t.Kind == HtmlTokenKind.Text) {
                    if (!string.IsNullOrEmpty(t.Text) && !IsWhitespaceOnly(t.Text)) hasContent = true;
                }
            }

            if (hasExplicitHtml) return tokens;
            // Empty / whitespace-only / comment-only / doctype-only fragments
            // keep the existing zero-element-document contract.
            if (!hasContent) return tokens;

            var result = new List<HtmlToken>(tokens.Count + 6);
            // Emit any leading DocType / Comment / whitespace tokens unchanged
            // — they belong to the Document, not to <html>.
            int i = 0;
            while (i < tokens.Count) {
                var t = tokens[i];
                if (t.Kind == HtmlTokenKind.DocType
                    || t.Kind == HtmlTokenKind.Comment
                    || (t.Kind == HtmlTokenKind.Text && IsWhitespaceOnly(t.Text))) {
                    result.Add(t);
                    i++;
                    continue;
                }
                break;
            }

            // Synthetic <html>.
            result.Add(StartTag("html"));

            // Determine head/body routing. Walk the remaining tokens and
            // split them into "head section" (head-content elements that
            // appear before any body content) and "body section".
            //
            // The split point is the first start tag that is NOT a head-
            // content element AND not <head> itself (or the first non-
            // whitespace text token). After the split, head is closed and
            // everything else goes into body. If <head> is already explicit,
            // its closing tag triggers the same transition.
            bool emittedHead = false;
            bool inHead = false;
            bool inBody = false;
            bool emittedBody = false;
            bool headOpenedExplicitly = false;
            bool bodyOpenedExplicitly = false;
            // Name of the currently-open head text-content element (style/script/
            // title), or null. While set, Text tokens are that element's content
            // and stay in head instead of triggering the body transition.
            string openHeadTextElement = null;

            void EnsureHead() {
                if (emittedHead) return;
                if (!headOpenedExplicitly) result.Add(StartTag("head"));
                emittedHead = true;
                inHead = true;
            }
            void CloseHead() {
                if (!inHead) return;
                if (!headOpenedExplicitly) result.Add(EndTag("head"));
                // If head was opened explicitly we let the author's </head>
                // end tag close it (or the implicit-close logic in BuildTree
                // will pop it once body content arrives).
                inHead = false;
            }
            void EnsureBody() {
                if (emittedBody) return;
                CloseHead();
                if (!bodyOpenedExplicitly) result.Add(StartTag("body"));
                emittedBody = true;
                inBody = true;
            }

            for (; i < tokens.Count; i++) {
                var t = tokens[i];
                if (t.Kind == HtmlTokenKind.Eof) break;

                if (t.Kind == HtmlTokenKind.StartTag) {
                    if (t.Name == "html") {
                        // Stray `<html>` inside a no-html document — drop
                        // it; the synthetic wrapper already covers the role.
                        continue;
                    }
                    if (t.Name == "head") {
                        // Author opened <head> explicitly. Mark it so we
                        // don't double-emit start/end tokens. Body has not
                        // started yet.
                        if (!emittedHead) {
                            headOpenedExplicitly = true;
                            emittedHead = true;
                            inHead = true;
                            result.Add(t);
                        }
                        // A second <head> is malformed — drop it.
                        continue;
                    }
                    if (t.Name == "body") {
                        // Author opened <body> explicitly.
                        if (!emittedBody) {
                            CloseHead();
                            bodyOpenedExplicitly = true;
                            emittedBody = true;
                            inBody = true;
                            result.Add(t);
                            continue;
                        }
                        // Second <body> is malformed — drop it.
                        continue;
                    }
                    if (!emittedBody && headContentElements.Contains(t.Name)) {
                        EnsureHead();
                        result.Add(t);
                        // Track open style/script/title so their text content
                        // doesn't get mis-routed to body (closing head mid-element).
                        if (headTextContentElements.Contains(t.Name)) openHeadTextElement = t.Name;
                        continue;
                    }
                    // Body content.
                    EnsureBody();
                    result.Add(t);
                    continue;
                }

                if (t.Kind == HtmlTokenKind.EndTag) {
                    if (t.Name == "html" || t.Name == "head" || t.Name == "body") {
                        if (t.Name == "head") {
                            // Author closed an explicit <head>. Stop
                            // accumulating head tokens; later content goes
                            // to body.
                            if (headOpenedExplicitly && inHead) {
                                result.Add(t);
                                inHead = false;
                                continue;
                            }
                            continue;
                        }
                        if (t.Name == "body") {
                            if (bodyOpenedExplicitly && inBody) {
                                result.Add(t);
                                inBody = false;
                                continue;
                            }
                            continue;
                        }
                        // Stray </html>.
                        continue;
                    }
                    // Closing the open head text-content element (</style> etc.)
                    // — clear the flag so following content routes normally.
                    if (openHeadTextElement != null && t.Name == openHeadTextElement) {
                        openHeadTextElement = null;
                    }
                    // Generic end tag — route to whichever section is
                    // currently open (head or body).
                    if (inHead && !emittedBody) {
                        result.Add(t);
                    } else {
                        EnsureBody();
                        result.Add(t);
                    }
                    continue;
                }

                if (t.Kind == HtmlTokenKind.Text) {
                    if (string.IsNullOrEmpty(t.Text)) continue;
                    // Text inside an open <style>/<script>/<title> is that
                    // element's own content — keep it in head; do NOT trigger
                    // the body transition (which would close head mid-element).
                    if (openHeadTextElement != null) { result.Add(t); continue; }
                    if (IsWhitespaceOnly(t.Text)) {
                        // Whitespace between elements is forwarded into
                        // whichever section is open. Before any content has
                        // been routed, drop it — it doesn't belong anywhere
                        // yet.
                        if (inHead || emittedBody) result.Add(t);
                        continue;
                    }
                    EnsureBody();
                    result.Add(t);
                    continue;
                }

                // Comments / DocTypes inside the body — leave with whatever
                // section is open. Pre-content ones were already drained
                // above.
                if (inHead || emittedBody) {
                    result.Add(t);
                }
            }

            // Close any sections we opened (synthetic only — explicit ones
            // were closed by the author's tokens, or BuildTree's
            // unclosed-element handling will tolerate the omission).
            if (inHead && !headOpenedExplicitly) result.Add(EndTag("head"));
            if (inBody && !bodyOpenedExplicitly) result.Add(EndTag("body"));
            // If we never opened a body (only head content), still emit an
            // empty <body> so the tree shape is uniform.
            if (!emittedBody) {
                if (inHead) {
                    if (!headOpenedExplicitly) result.Add(EndTag("head"));
                }
                if (!bodyOpenedExplicitly) {
                    result.Add(StartTag("body"));
                    result.Add(EndTag("body"));
                }
            }
            result.Add(EndTag("html"));

            // Preserve the original EOF token (BuildTree relies on it).
            var eof = tokens[tokens.Count - 1];
            if (eof.Kind != HtmlTokenKind.Eof) {
                eof = new HtmlToken { Kind = HtmlTokenKind.Eof };
            }
            result.Add(eof);
            return result;
        }

        static bool IsWhitespaceOnly(string s) {
            if (string.IsNullOrEmpty(s)) return true;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f') return false;
            }
            return true;
        }

        static HtmlToken StartTag(string name) {
            return new HtmlToken { Kind = HtmlTokenKind.StartTag, Name = name };
        }

        static HtmlToken EndTag(string name) {
            return new HtmlToken { Kind = HtmlTokenKind.EndTag, Name = name };
        }

        static Document BuildTree(List<HtmlToken> tokens, ParseOptions options) {
            var doc = new Document();
            var stack = new Stack<Node>();
            stack.Push(doc);

            // HTML5 §13.2.4.3 active formatting elements list. We keep a
            // simplified version: a flat list of formatting Elements whose
            // start tag was emitted but whose end tag hasn't been seen and
            // whose enclosing block was implicitly closed by AAA. When the
            // next text or inline start tag arrives we "reconstruct" them so
            // that subsequent inline content is wrapped in a fresh copy of
            // each — this is what gives Chrome the duplicate <a> on each
            // side of a block in `<p>...<a><div/></a>...</p>`.
            var activeFormatting = new List<Element>();

            foreach (var t in tokens) {
                switch (t.Kind) {
                    case HtmlTokenKind.DocType:
                    case HtmlTokenKind.Comment:
                        break;

                    case HtmlTokenKind.Text:
                        if (string.IsNullOrEmpty(t.Text)) break;
                        ReconstructActiveFormatting(stack, activeFormatting);
                        stack.Peek().AppendChild(new TextNode(t.Text));
                        break;

                    case HtmlTokenKind.StartTag: {
                        // HTML5 §13.2.6.4.7 ("in body" insertion mode):
                        // a block-level start tag while an inline
                        // formatting element is open inside <p> triggers
                        // the "adoption agency"-style fixup. We detect
                        // the pattern, pop the formatting + p elements,
                        // and record them on the active-formatting list
                        // so they get re-opened around later inline
                        // content.
                        if (ClosesOpenP(t.Name) && TryAdoptionAgencyForBlock(stack, activeFormatting)) {
                            // stack top is now <p>'s former parent (or
                            // its grandparent if multiple inlines were
                            // popped). Fall through to the normal
                            // start-tag insertion below.
                        } else {
                            // HTML Living Standard "Optional tags":
                            // implicitly close an open element when a
                            // start tag would otherwise nest inside
                            // something it shouldn't (e.g. `<p>One<p>Two`
                            // produces two siblings, not nested <p>s).
                            while (stack.Count > 1 && stack.Peek() is Element openTop
                                   && HtmlElements.ShouldImplicitlyClose(openTop.TagName, t.Name)) {
                                stack.Pop();
                            }
                        }

                        // If this is an inline formatting element, reconstruct
                        // any pending active formatting elements first so they
                        // wrap the new inline (matches browser behavior of
                        // re-opening <a>/<b>/... around a fresh nested inline).
                        bool isFormatting = formattingElements.Contains(t.Name);
                        if (isFormatting) {
                            ReconstructActiveFormatting(stack, activeFormatting);
                        }

                        var elem = new Element(t.Name);
                        if (t.Attributes != null) {
                            foreach (var a in t.Attributes) elem.SetAttribute(a.Name, a.Value);
                        }
                        stack.Peek().AppendChild(elem);
                        bool isVoid = HtmlElements.IsVoid(t.Name);
                        if (!isVoid && !t.SelfClosing) {
                            stack.Push(elem);
                            if (isFormatting) {
                                activeFormatting.Add(elem);
                            }
                        }
                        break;
                    }

                    case HtmlTokenKind.EndTag: {
                        if (HtmlElements.IsVoid(t.Name)) {
                            if (options.ThrowOnError)
                                throw new HtmlParseException($"Unexpected end tag for void element '{t.Name}'", t.Line, t.Column);
                            break;
                        }
                        // HTML5 §13.2.6 AAA-lite: if the end tag matches a
                        // formatting element on the active-formatting list
                        // but not on the stack (i.e. it was already popped
                        // implicitly by a block insertion), this is a no-op
                        // on the tree — just drop it from the AFL so we
                        // don't reopen it around subsequent inline content.
                        if (formattingElements.Contains(t.Name)
                            && !IsOnStack(stack, t.Name)
                            && RemoveFromActiveFormatting(activeFormatting, t.Name)) {
                            break;
                        }
                        if (stack.Count <= 1) {
                            // HTML5 §13.2.6.4.7: stray `</p>` even at root
                            // inserts an empty <p>. Required so the
                            // trailing </p> in `<p>...<div>...</p>`
                            // produces the sibling Chrome emits.
                            if (t.Name == "p") {
                                stack.Peek().AppendChild(new Element("p"));
                                break;
                            }
                            if (options.ThrowOnError && !HtmlElements.IsOptionalClose(t.Name))
                                throw new HtmlParseException($"Mismatched end tag '{t.Name}' (expected close of '<root>')", t.Line, t.Column);
                            break;
                        }
                        // Per the HTML Living Standard "in body" end-tag handling:
                        // when the current open element doesn't match the closing
                        // tag, look for a matching ancestor on the stack. If one is
                        // found, implicitly close intermediate optional-close
                        // elements (li, p, dd, dt, td, tr, ...). If none is found,
                        // the end tag is a stray and is silently ignored — browsers
                        // do the same rather than reject the document.
                        // For optional-close end tags (</li>, </p>, ...) the HTML
                        // Living Standard restricts the search to the element's
                        // "list item scope" / "button scope" / etc. — i.e. it
                        // does NOT cross intervening block boundaries like <ul>,
                        // <table>, <div>. If a non-optional-close ancestor sits
                        // between the stack top and a deeper match, the end tag
                        // is stray (its real target was already implicitly
                        // closed earlier in the run) and must be silently
                        // ignored rather than tearing down the surrounding
                        // block. Without this restriction, randomly nested DOMs
                        // like `<li><ul><span></span></li></ul>` mis-attribute
                        // the </li> to the outer <li> and trip a fatal
                        // "Mismatched end tag" error inside the implicit-pop
                        // loop below.
                        bool optionalCloseTarget = HtmlElements.IsOptionalClose(t.Name);
                        int matchDepth = -1;
                        int idx = 0;
                        foreach (var node in stack) {
                            if (node is Element el) {
                                if (el.TagName == t.Name) { matchDepth = idx; break; }
                                if (optionalCloseTarget && !HtmlElements.IsOptionalClose(el.TagName)) {
                                    // Scope boundary — stop searching. The
                                    // end tag is stray and will be ignored.
                                    break;
                                }
                            }
                            idx++;
                        }
                        if (matchDepth < 0) {
                            // No matching ancestor — stray end tag.
                            // HTML5 §13.2.6.4.7 "in body" — a `</p>` with
                            // no <p> in button scope is a parse error AND
                            // inserts an empty `<p>` (then immediately
                            // closes it). This mirrors Chrome's DOM after
                            // the AAA fixup runs on `<p>...<div>...</p>`:
                            // the </p> at the end produces a trailing
                            // empty <p> sibling. We replicate that here so
                            // LayoutDiff against Chrome lines up exactly.
                            if (t.Name == "p" && stack.Peek() is Node host) {
                                var emptyP = new Element("p");
                                host.AppendChild(emptyP);
                                break;
                            }
                            // Other optional-close end tags are silently
                            // ignored; non-optional in strict mode throw.
                            if (options.ThrowOnError && !optionalCloseTarget) {
                                var topName = stack.Peek() is Element e ? e.TagName : "<root>";
                                throw new HtmlParseException($"Mismatched end tag '{t.Name}' (expected close of '{topName}')", t.Line, t.Column);
                            }
                            break;
                        }
                        // Pop intermediate elements (auto-closing optional ones)
                        // and then the matched element itself.
                        for (int i = 0; i < matchDepth; i++) {
                            var intermediate = stack.Peek() as Element;
                            if (intermediate != null
                                && !HtmlElements.IsOptionalClose(intermediate.TagName)
                                && !formattingElements.Contains(intermediate.TagName)
                                && options.ThrowOnError) {
                                throw new HtmlParseException($"Mismatched end tag '{t.Name}' (expected close of '{intermediate.TagName}')", t.Line, t.Column);
                            }
                            // Note: intermediate formatting elements are
                            // popped off the stack but stay on the AFL.
                            // HTML5 §13.2.6 AAA reconstructs them around
                            // upcoming inline content so e.g. closing a
                            // <div> mid-<a> still wraps later text in <a>.
                            stack.Pop();
                        }
                        // Pop the matched element itself. If it's a
                        // formatting element, drop it from the AFL too —
                        // its end tag was explicit so don't reopen it.
                        if (stack.Peek() is Element matched
                            && formattingElements.Contains(matched.TagName)) {
                            RemoveFromActiveFormatting(activeFormatting, matched.TagName);
                        }
                        stack.Pop();
                        break;
                    }

                    case HtmlTokenKind.Eof:
                        if (stack.Count > 1 && options.ThrowOnError) {
                            var top = stack.Peek() as Element;
                            throw new HtmlParseException($"Unclosed element '{top?.TagName}'", t.Line, t.Column);
                        }
                        break;
                }
            }
            return doc;
        }

        // HTML5 §13.2.6 "adoption agency" lite: triggered when a block-level
        // start tag is about to be inserted and the open-elements stack has
        // `<p>` somewhere with one or more inline formatting elements above
        // it. Pop the formatting elements off the stack (recording them on
        // the active-formatting list so they get re-opened around later
        // inline content), then pop the <p> itself. Returns true if it did
        // the rewrite; false if the open stack didn't match the pattern
        // (caller falls back to the normal implicit-close path).
        //
        // The targeted shape:
        //   stack: ... p, a, b, ...
        //   new start tag: <div>  (any block-level)
        // becomes:
        //   stack: ...
        //   activeFormatting: [a, b, ...]
        // and the new <div> is inserted as a sibling of <p>.
        static bool TryAdoptionAgencyForBlock(Stack<Node> stack, List<Element> activeFormatting) {
            // Walk the stack top-down looking for the pattern
            // [formatting elements...]+ <p>. Anything other than a
            // formatting element above <p> aborts: the rewrite is only
            // safe for inline ancestors of <p>.
            int formattingCount = 0;
            bool foundP = false;
            foreach (var node in stack) {
                if (node is Element el) {
                    if (el.TagName == "p") { foundP = true; break; }
                    if (formattingElements.Contains(el.TagName)) {
                        formattingCount++;
                        continue;
                    }
                    // Non-formatting, non-p ancestor between stack top and
                    // <p> — bail. Could be <div>, <li>, etc.
                    return false;
                }
            }
            if (!foundP) return false;
            if (formattingCount == 0) {
                // Plain <p> with no formatting inside — the normal
                // implicit-close path handles this fine.
                return false;
            }

            // Record formatting elements (top of stack first), then pop
            // them and the <p>. Insert into activeFormatting so they get
            // reconstructed around upcoming inline content.
            var popped = new List<Element>();
            for (int i = 0; i < formattingCount; i++) {
                popped.Add((Element)stack.Pop());
            }
            stack.Pop(); // pop the <p>

            // The popped list is in reverse stack order (innermost first).
            // Reverse so activeFormatting reads outermost->innermost, which
            // matches the order ReconstructActiveFormatting re-inserts them.
            popped.Reverse();
            foreach (var f in popped) {
                if (!activeFormatting.Contains(f)) activeFormatting.Add(f);
            }
            return true;
        }

        // HTML5 §13.2.4.3 "reconstruct the active formatting elements".
        // For each formatting element on the AFL that isn't currently in the
        // open-elements stack, create a fresh copy (cloning attributes) as a
        // child of the current insertion target and push it onto the stack.
        // Replaces the original entry in the AFL with the new clone so a
        // later </a> matches the live element.
        static void ReconstructActiveFormatting(Stack<Node> stack, List<Element> activeFormatting) {
            for (int i = 0; i < activeFormatting.Count; i++) {
                var fe = activeFormatting[i];
                if (IsOnStack(stack, fe)) continue;
                var clone = new Element(fe.TagName);
                foreach (var attr in fe.Attributes) {
                    clone.SetAttribute(attr.Key, attr.Value);
                }
                stack.Peek().AppendChild(clone);
                stack.Push(clone);
                activeFormatting[i] = clone;
            }
        }

        static bool IsOnStack(Stack<Node> stack, Element target) {
            foreach (var n in stack) if (n == target) return true;
            return false;
        }

        static bool IsOnStack(Stack<Node> stack, string tagName) {
            foreach (var n in stack) {
                if (n is Element el && el.TagName == tagName) return true;
            }
            return false;
        }

        static bool RemoveFromActiveFormatting(List<Element> activeFormatting, string tagName) {
            for (int i = activeFormatting.Count - 1; i >= 0; i--) {
                if (activeFormatting[i].TagName == tagName) {
                    activeFormatting.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }
    }
}
