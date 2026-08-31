#include "weva/dom.h"
#include "weva/html.h"

#include <algorithm>
#include <unordered_set>

// Ports Runtime/Parsing/HtmlParser.cs.
//
// Two structural notes on the translation:
//
//  * C#'s Stack<T> enumerates top-first. Here the open-element stack is a
//    vector and every "foreach (var node in stack)" becomes a reverse
//    iteration. Getting this backwards silently inverts the scope searches
//    and the adoption-agency pattern match, so it is spelled out at each site.
//  * The stack holds raw pointers. Every element is appended to its parent
//    before being pushed, so the parent's Ref keeps it alive for as long as
//    the stack can reach it.

namespace weva {

namespace {

// HTML5 §13.2.6.2 head-content elements. With no explicit <html> wrapper,
// these route into the synthetic <head>; anything else opens <body>.
//
// <template> is deliberately absent: HTML5 allows it in both, but Weva's
// binding layer treats it as body content (data-each / data-if blocks), so a
// fragment starting with <template> must open the body.
const std::unordered_set<std::string_view>& head_content() {
    static const std::unordered_set<std::string_view> s = {
        "base", "basefont", "bgsound", "link", "meta", "noscript",
        "script", "style", "title"};
    return s;
}

// Head elements whose text is their own RAWTEXT/RCDATA content. While one is
// open, a Text token must not trigger the head->body transition — otherwise
// the synthetic <body> nests inside the still-open <style> and orphans the
// real content. That was the cause of a blank document for sources beginning
// `<style>…</style><div>…` with no explicit <body>.
const std::unordered_set<std::string_view>& head_text_content() {
    static const std::unordered_set<std::string_view> s = {"script", "style", "title"};
    return s;
}

// HTML5 §13.2.4.2 "list of active formatting elements".
//
// <span> is deliberately excluded. The spec classifies it as ordinary, and
// when a <span> holds a <div> inside a <p> the spec closes the <p> and drops
// the <span> structurally. Weva keeps the lenient legacy behaviour so author
// CSS relying on `<span style="display:inline-block">` wrapping block
// descendants still works.
const std::unordered_set<std::string_view>& formatting_elements() {
    static const std::unordered_set<std::string_view> s = {
        "a", "b", "big", "code", "em", "font", "i", "nobr", "s",
        "small", "strike", "strong", "tt", "u"};
    return s;
}

bool closes_open_p(std::string_view start) {
    return html_elements::should_implicitly_close("p", start);
}

// C# HtmlParser.IsWhitespaceOnly is ASCII-only — deliberately NARROWER than
// the tokenizer's char.IsWhiteSpace. Do not unify the two.
bool is_whitespace_only(std::string_view s) {
    for (char c : s) {
        if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f') return false;
    }
    return true;
}

HtmlToken synth(HtmlTokenKind kind, Symbol name) {
    HtmlToken t;
    t.kind = kind;
    t.name = name;
    return t;
}

struct Names {
    Symbol html, head, body;
    explicit Names(SymbolTable* s)
        : html(s->intern("html")), head(s->intern("head")),
          body(s->intern("body")) {}
};

// ------------------------------------------------------------------ fragment

// HTML5 §13.2.5 tree construction, approximated at the token level. Browsers
// always yield Document > <html> > <head> + <body>; Weva authors routinely
// hand the parser a bare fragment. Without this pass, top-level elements are
// direct children of Document, which breaks two things: `:root` (first Element
// child of Document) matches whatever came first — often a <link> or <style>,
// so custom properties declared on :root land on a display:none element and
// never inherit — and the html/body background-propagation pass has no
// elements to work with.
//
// Whitespace-only / empty / DOCTYPE-only input deliberately produces an empty
// Document, preserving the "empty string -> 0 children" contract.
std::vector<HtmlToken> normalize_fragment(const std::vector<HtmlToken>& tokens,
                                          SymbolTable* symbols) {
    const Names n(symbols);

    bool has_explicit_html = false, has_content = false;
    for (const auto& t : tokens) {
        if (t.kind == HtmlTokenKind::StartTag) {
            if (t.name == n.html) has_explicit_html = true;
            has_content = true;
        } else if (t.kind == HtmlTokenKind::Text) {
            if (!t.text.empty() && !is_whitespace_only(t.text)) has_content = true;
        }
    }
    if (has_explicit_html || !has_content) return tokens;

    std::vector<HtmlToken> out;
    out.reserve(tokens.size() + 6);

    // Leading DOCTYPE / comments / whitespace belong to the Document, not <html>.
    std::size_t i = 0;
    for (; i < tokens.size(); ++i) {
        const auto& t = tokens[i];
        if (t.kind == HtmlTokenKind::DocType || t.kind == HtmlTokenKind::Comment ||
            (t.kind == HtmlTokenKind::Text && is_whitespace_only(t.text))) {
            out.push_back(t);
            continue;
        }
        break;
    }

    out.push_back(synth(HtmlTokenKind::StartTag, n.html));

    bool emitted_head = false, in_head = false;
    bool emitted_body = false, in_body = false;
    bool head_explicit = false, body_explicit = false;
    Symbol open_head_text = kInvalidSymbol;

    auto ensure_head = [&] {
        if (emitted_head) return;
        if (!head_explicit) out.push_back(synth(HtmlTokenKind::StartTag, n.head));
        emitted_head = true;
        in_head = true;
    };
    auto close_head = [&] {
        if (!in_head) return;
        // An explicitly-opened <head> is closed by the author's own end tag,
        // or by build_tree's implicit-close once body content arrives.
        if (!head_explicit) out.push_back(synth(HtmlTokenKind::EndTag, n.head));
        in_head = false;
    };
    auto ensure_body = [&] {
        if (emitted_body) return;
        close_head();
        if (!body_explicit) out.push_back(synth(HtmlTokenKind::StartTag, n.body));
        emitted_body = true;
        in_body = true;
    };

    for (; i < tokens.size(); ++i) {
        const auto& t = tokens[i];
        if (t.kind == HtmlTokenKind::Eof) break;

        if (t.kind == HtmlTokenKind::StartTag) {
            if (t.name == n.html) continue;             // stray; wrapper covers it
            if (t.name == n.head) {
                if (!emitted_head) {
                    head_explicit = emitted_head = in_head = true;
                    out.push_back(t);
                }
                continue;                                // a second <head> is malformed
            }
            if (t.name == n.body) {
                if (!emitted_body) {
                    close_head();
                    body_explicit = emitted_body = in_body = true;
                    out.push_back(t);
                }
                continue;                                // a second <body> is malformed
            }
            if (!emitted_body && head_content().count(symbols->text(t.name))) {
                ensure_head();
                out.push_back(t);
                if (head_text_content().count(symbols->text(t.name))) open_head_text = t.name;
                continue;
            }
            ensure_body();
            out.push_back(t);
            continue;
        }

        if (t.kind == HtmlTokenKind::EndTag) {
            if (t.name == n.head) {
                if (head_explicit && in_head) { out.push_back(t); in_head = false; }
                continue;
            }
            if (t.name == n.body) {
                if (body_explicit && in_body) { out.push_back(t); in_body = false; }
                continue;
            }
            if (t.name == n.html) continue;              // stray </html>
            if (open_head_text != kInvalidSymbol && t.name == open_head_text) {
                open_head_text = kInvalidSymbol;
            }
            if (in_head && !emitted_body) out.push_back(t);
            else { ensure_body(); out.push_back(t); }
            continue;
        }

        if (t.kind == HtmlTokenKind::Text) {
            if (t.text.empty()) continue;
            // Text inside an open <style>/<script>/<title> is that element's
            // own content: keep it in head, do not transition.
            if (open_head_text != kInvalidSymbol) { out.push_back(t); continue; }
            if (is_whitespace_only(t.text)) {
                // Before anything has been routed, inter-element whitespace
                // belongs nowhere yet — drop it.
                if (in_head || emitted_body) out.push_back(t);
                continue;
            }
            ensure_body();
            out.push_back(t);
            continue;
        }

        if (in_head || emitted_body) out.push_back(t);   // comments / doctypes mid-document
    }

    if (in_head && !head_explicit) out.push_back(synth(HtmlTokenKind::EndTag, n.head));
    if (in_body && !body_explicit) out.push_back(synth(HtmlTokenKind::EndTag, n.body));
    if (!emitted_body) {
        if (in_head && !head_explicit) out.push_back(synth(HtmlTokenKind::EndTag, n.head));
        if (!body_explicit) {
            out.push_back(synth(HtmlTokenKind::StartTag, n.body));
            out.push_back(synth(HtmlTokenKind::EndTag, n.body));
        }
    }
    out.push_back(synth(HtmlTokenKind::EndTag, n.html));

    HtmlToken eof;
    eof.kind = HtmlTokenKind::Eof;
    if (!tokens.empty() && tokens.back().kind == HtmlTokenKind::Eof) eof = tokens.back();
    out.push_back(std::move(eof));
    return out;
}

// ----------------------------------------------------------------- tree build

using Stack = std::vector<Node*>;

bool is_on_stack(const Stack& stack, const Element* target) {
    for (auto it = stack.rbegin(); it != stack.rend(); ++it) {
        if (*it == target) return true;
    }
    return false;
}

bool is_on_stack_named(const Stack& stack, std::string_view tag) {
    for (auto it = stack.rbegin(); it != stack.rend(); ++it) {
        if ((*it)->is_element() && static_cast<Element*>(*it)->tag_name() == tag) return true;
    }
    return false;
}

bool remove_from_afl(std::vector<Element*>* afl, std::string_view tag) {
    for (int i = static_cast<int>(afl->size()) - 1; i >= 0; --i) {
        if ((*afl)[static_cast<std::size_t>(i)]->tag_name() == tag) {
            afl->erase(afl->begin() + i);
            return true;
        }
    }
    return false;
}

// HTML5 §13.2.4.3 "reconstruct the active formatting elements". For each AFL
// entry not currently on the stack, clone it (with attributes) into the
// insertion point and replace the AFL entry with the clone, so a later end tag
// matches the live element.
void reconstruct_afl(Stack* stack, std::vector<Element*>* afl) {
    for (std::size_t i = 0; i < afl->size(); ++i) {
        Element* fe = (*afl)[i];
        if (is_on_stack(*stack, fe)) continue;
        Ref<Element> clone = make_ref<Element>(fe->tag_name());
        for (std::size_t a = 0; a < fe->attributes().size(); ++a) {
            clone->set_attribute(fe->attributes().name_at(a), fe->attributes().value_at(a));
        }
        stack->back()->append_child(clone.get());
        stack->push_back(clone.get());
        (*afl)[i] = clone.get();
    }
}

// HTML5 §13.2.6 "adoption agency", lite. Triggered when a block-level start
// tag is about to be inserted and the stack holds <p> with one or more inline
// formatting elements above it:
//
//     stack: ... p, a, b        + <div>
//  -> stack: ...                  afl: [a, b]
//
// The formatting elements are recorded so they reopen around later inline
// content — this is what gives Chrome the duplicate <a> on each side of a
// block in `<p>...<a><div/></a>...</p>`.
bool try_adoption_agency(Stack* stack, std::vector<Element*>* afl) {
    int formatting_count = 0;
    bool found_p = false;
    for (auto it = stack->rbegin(); it != stack->rend(); ++it) {   // top-first, as C#
        if (!(*it)->is_element()) continue;
        auto* el = static_cast<Element*>(*it);
        if (el->tag_name() == "p") { found_p = true; break; }
        if (formatting_elements().count(el->tag_name())) {
            ++formatting_count;
            continue;
        }
        return false;   // a <div>/<li>/... between top and <p>: not safe to rewrite
    }
    if (!found_p || formatting_count == 0) return false;

    std::vector<Element*> popped;
    for (int i = 0; i < formatting_count; ++i) {
        popped.push_back(static_cast<Element*>(stack->back()));
        stack->pop_back();
    }
    stack->pop_back();   // the <p>

    // popped is innermost-first; reverse so the AFL reads outermost->innermost,
    // which is the order reconstruct_afl re-inserts them in.
    std::reverse(popped.begin(), popped.end());
    for (Element* f : popped) {
        if (std::find(afl->begin(), afl->end(), f) == afl->end()) afl->push_back(f);
    }
    return true;
}

} // namespace

Ref<Document> parse_html(std::string_view source, SymbolTable* symbols,
                         const ParseOptions& options, HtmlParseError* error) {
    std::vector<HtmlToken> raw;
    HtmlTokenizer tokenizer(source, symbols);
    if (!tokenizer.tokenize(&raw, error)) return Ref<Document>();

    std::vector<HtmlToken> tokens = normalize_fragment(raw, symbols);

    auto doc = make_ref<Document>();
    Stack stack;
    stack.push_back(doc.get());
    std::vector<Element*> afl;

    auto fail = [&](const std::string& msg, const HtmlToken& t) {
        if (error) *error = HtmlParseError{msg, t.line, t.column};
        return Ref<Document>();
    };

    for (const auto& t : tokens) {
        std::string_view name = symbols->text(t.name);
        switch (t.kind) {
            case HtmlTokenKind::DocType:
            case HtmlTokenKind::Comment:
                break;

            case HtmlTokenKind::Text: {
                if (t.text.empty()) break;
                reconstruct_afl(&stack, &afl);
                auto text = make_ref<TextNode>(t.text);
                stack.back()->append_child(text.get());
                break;
            }

            case HtmlTokenKind::StartTag: {
                if (closes_open_p(name) && try_adoption_agency(&stack, &afl)) {
                    // Stack top is now <p>'s former parent; fall through to
                    // normal insertion.
                } else {
                    // "Optional tags": `<p>One<p>Two` yields siblings, not nesting.
                    while (stack.size() > 1 && stack.back()->is_element() &&
                           html_elements::should_implicitly_close(
                               static_cast<Element*>(stack.back())->tag_name(), name)) {
                        stack.pop_back();
                    }
                }

                bool is_formatting = formatting_elements().count(name) != 0;
                if (is_formatting) reconstruct_afl(&stack, &afl);

                auto elem = make_ref<Element>(name);
                for (const auto& a : t.attributes) {
                    elem->set_attribute(symbols->text(a.name), a.value);
                }
                stack.back()->append_child(elem.get());

                if (!html_elements::is_void(name) && !t.self_closing) {
                    stack.push_back(elem.get());
                    if (is_formatting) afl.push_back(elem.get());
                }
                break;
            }

            case HtmlTokenKind::EndTag: {
                if (html_elements::is_void(name)) {
                    if (options.strict) {
                        return fail("Unexpected end tag for void element '" +
                                    std::string(name) + "'", t);
                    }
                    break;
                }
                // AAA-lite: an end tag matching an AFL entry that is no longer
                // on the stack (a block insertion already popped it) is a tree
                // no-op — just drop it so it isn't reopened later.
                if (formatting_elements().count(name) && !is_on_stack_named(stack, name) &&
                    remove_from_afl(&afl, name)) {
                    break;
                }
                if (stack.size() <= 1) {
                    // HTML5 §13.2.6.4.7: a stray </p> even at root inserts an
                    // empty <p>. Needed so the trailing </p> in
                    // `<p>...<div>...</p>` produces the sibling Chrome emits.
                    if (name == "p") {
                        stack.back()->append_child(make_ref<Element>(name).get());
                        break;
                    }
                    if (options.strict && !html_elements::is_optional_close(name)) {
                        return fail("Mismatched end tag '" + std::string(name) +
                                    "' (expected close of '<root>')", t);
                    }
                    break;
                }

                // For optional-close end tags the spec restricts the search to
                // the element's list-item / button scope — it must not cross a
                // block boundary like <ul>, <table>, <div>. Without that,
                // `<li><ul><span></span></li></ul>` mis-attributes the </li> to
                // the outer <li> and trips a fatal error in the pop loop below.
                bool optional_target = html_elements::is_optional_close(name);
                int match_depth = -1;
                int idx = 0;
                for (auto it = stack.rbegin(); it != stack.rend(); ++it, ++idx) {
                    if (!(*it)->is_element()) continue;
                    auto* el = static_cast<Element*>(*it);
                    if (el->tag_name() == name) { match_depth = idx; break; }
                    if (optional_target &&
                        !html_elements::is_optional_close(el->tag_name())) {
                        break;   // scope boundary: the end tag is stray
                    }
                }

                if (match_depth < 0) {
                    // §13.2.6.4.7: a </p> with no <p> in button scope is a parse
                    // error that still inserts (and closes) an empty <p>. This
                    // mirrors Chrome's DOM after the AAA fixup on
                    // `<p>...<div>...</p>`, so LayoutDiff lines up exactly.
                    if (name == "p") {
                        stack.back()->append_child(make_ref<Element>(name).get());
                        break;
                    }
                    if (options.strict && !optional_target) {
                        std::string top = stack.back()->is_element()
                            ? static_cast<Element*>(stack.back())->tag_name()
                            : "<root>";
                        return fail("Mismatched end tag '" + std::string(name) +
                                    "' (expected close of '" + top + "')", t);
                    }
                    break;
                }

                for (int i = 0; i < match_depth; ++i) {
                    if (stack.back()->is_element()) {
                        auto* inter = static_cast<Element*>(stack.back());
                        std::string_view iname = inter->tag_name();
                        if (!html_elements::is_optional_close(iname) &&
                            !formatting_elements().count(iname) && options.strict) {
                            return fail("Mismatched end tag '" + std::string(name) +
                                        "' (expected close of '" + std::string(iname) + "')", t);
                        }
                    }
                    // Intermediate formatting elements leave the stack but stay
                    // on the AFL, so closing a <div> mid-<a> still wraps later
                    // text in a fresh <a>.
                    stack.pop_back();
                }
                if (stack.back()->is_element()) {
                    auto* matched = static_cast<Element*>(stack.back());
                    if (formatting_elements().count(matched->tag_name())) {
                        remove_from_afl(&afl, matched->tag_name());
                    }
                }
                stack.pop_back();
                break;
            }

            case HtmlTokenKind::Eof:
                if (stack.size() > 1 && options.strict) {
                    std::string top = stack.back()->is_element()
                        ? static_cast<Element*>(stack.back())->tag_name()
                        : "";
                    return fail("Unclosed element '" + top + "'", t);
                }
                break;
        }
    }
    return doc;
}

} // namespace weva
