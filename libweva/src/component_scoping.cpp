#include "weva/component_scoping.h"

#include <cstring>

namespace weva {

namespace {

bool is_ws(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f'; }

std::string_view trim(std::string_view s) {
    while (!s.empty() && is_ws(s.front())) s.remove_prefix(1);
    while (!s.empty() && is_ws(s.back())) s.remove_suffix(1);
    return s;
}

size_t skip_string(std::string_view s, size_t start) {
    const char q = s[start];
    size_t i = start + 1;
    while (i < s.size()) {
        const char c = s[i];
        if (c == '\\' && i + 1 < s.size()) { i += 2; continue; }
        if (c == q) return i + 1;
        ++i;
    }
    return s.size();
}

size_t skip_balanced(std::string_view s, size_t start) {
    const char open = s[start];
    const char close = open == '(' ? ')' : (open == '[' ? ']' : '}');
    int depth = 0;
    size_t i = start;
    while (i < s.size()) {
        const char c = s[i];
        if (c == '"' || c == '\'') { i = skip_string(s, i); continue; }
        if (c == open) {
            ++depth;
        } else if (c == close) {
            if (--depth == 0) return i + 1;
        }
        ++i;
    }
    return s.size();
}

std::vector<std::string_view> split_top_level_commas(std::string_view s) {
    std::vector<std::string_view> parts;
    size_t start = 0, i = 0;
    while (i < s.size()) {
        const char c = s[i];
        if (c == '(' || c == '[') { i = skip_balanced(s, i); continue; }
        if (c == '"' || c == '\'') { i = skip_string(s, i); continue; }
        if (c == ',') {
            parts.push_back(s.substr(start, i - start));
            ++i;
            start = i;
            continue;
        }
        ++i;
    }
    parts.push_back(s.substr(start));
    return parts;
}

bool contains_top_level_comma(std::string_view s) {
    return split_top_level_commas(s).size() > 1;
}

bool starts_with_host(std::string_view compound) {
    if (compound.size() < 5) return false;
    static const char kHost[] = ":host";
    for (size_t i = 0; i < 5; ++i) {
        char c = compound[i];
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        if (c != kHost[i]) return false;
    }
    return true;
}

struct Compound {
    std::string text;
    std::string leading;   // the whitespace + combinator before it, "" for the first
};

// Splits a complex selector into compounds. The leading text of each
// compound after the first is " ", " > ", " + " or " ~ ", so re-emitting
// normalises the spacing the way the reference does.
std::vector<Compound> split_compounds(std::string_view selector) {
    std::vector<Compound> out;
    size_t i = 0;
    const size_t n = selector.size();
    size_t compound_start = std::string::npos;
    std::string pending;
    while (i < n) {
        const char c = selector[i];
        if (compound_start == std::string::npos) {
            if (is_ws(c) || c == '>' || c == '+' || c == '~') { ++i; continue; }
            compound_start = i;
            if (c == '[' || c == '(') i = skip_balanced(selector, i);
            else if (c == '"' || c == '\'') i = skip_string(selector, i);
            else ++i;
            continue;
        }
        if (c == '(' || c == '[') { i = skip_balanced(selector, i); continue; }
        if (c == '"' || c == '\'') { i = skip_string(selector, i); continue; }
        if (is_ws(c) || c == '>' || c == '+' || c == '~') {
            out.push_back({std::string(selector.substr(compound_start, i - compound_start)), pending});
            compound_start = std::string::npos;
            bool has_explicit = false;
            char explicit_char = '\0';
            while (i < n) {
                const char cc = selector[i];
                if (is_ws(cc)) { ++i; continue; }
                if (!has_explicit && (cc == '>' || cc == '+' || cc == '~')) {
                    has_explicit = true;
                    explicit_char = cc;
                    ++i;
                    continue;
                }
                break;
            }
            pending = has_explicit ? std::string(" ") + explicit_char + " " : std::string(" ");
            continue;
        }
        ++i;
    }
    if (compound_start != std::string::npos) {
        out.push_back({std::string(selector.substr(compound_start)), pending});
    }
    return out;
}

// `:host(.a, .b)` with more than one alternative -> `:host(.a)`, `:host(.b)`
// (any text after the parenthesis kept on each). Empty when not that shape.
std::vector<std::string> host_alternatives(std::string_view compound) {
    std::vector<std::string> out;
    if (!starts_with_host(compound)) return out;
    const size_t after = 5;
    if (after >= compound.size() || compound[after] != '(') return out;
    const size_t close = skip_balanced(compound, after);
    const std::string_view inner = compound.substr(after + 1, close - after - 2);
    if (!contains_top_level_comma(inner)) return out;
    const std::string_view suffix = compound.substr(close);
    for (const std::string_view alt : split_top_level_commas(inner)) {
        const std::string_view a = trim(alt);
        if (a.empty()) continue;
        out.push_back(":host(" + std::string(a) + ")" + std::string(suffix));
    }
    return out;
}

bool rewrite_host(std::string_view compound, std::string_view scope_id, std::string* out) {
    if (!starts_with_host(compound)) return false;
    const size_t after = 5;
    std::string_view suffix, inner;
    if (after < compound.size() && compound[after] == '(') {
        const size_t close = skip_balanced(compound, after);
        inner = trim(compound.substr(after + 1, close - after - 2));
        suffix = compound.substr(close);
        if (contains_top_level_comma(inner)) inner = trim(split_top_level_commas(inner)[0]);
    } else {
        suffix = compound.substr(after);
    }
    *out = std::string("[") + kComponentHostAttribute + "=\"" + std::string(scope_id) + "\"]" +
           std::string(inner) + std::string(suffix);
    return true;
}

// The first top-level ':' of a compound, or npos.
size_t pseudo_start(std::string_view compound) {
    size_t i = 0;
    while (i < compound.size()) {
        const char c = compound[i];
        if (c == '(' || c == '[') { i = skip_balanced(compound, i); continue; }
        if (c == '"' || c == '\'') { i = skip_string(compound, i); continue; }
        if (c == ':') return i;
        ++i;
    }
    return std::string::npos;
}

std::string with_scope_attribute(std::string_view compound, std::string_view scope_id) {
    const std::string attr =
        std::string("[") + kComponentScopeAttribute + "=\"" + std::string(scope_id) + "\"]";
    const size_t at = pseudo_start(compound);
    // A compound that IS a pseudo (`:not(...)`) takes the attribute at its end.
    if (at == std::string::npos || at == 0) return std::string(compound) + attr;
    return std::string(compound.substr(0, at)) + attr + std::string(compound.substr(at));
}

bool has_direct_nesting(std::string_view compound) {
    for (size_t i = 0; i < compound.size();) {
        const char c = compound[i];
        if (c == '\\' && i + 1 < compound.size()) { i += 2; continue; }
        if (c == '(' || c == '[') { i = skip_balanced(compound, i); continue; }
        if (c == '&') return true;
        ++i;
    }
    return false;
}

std::string emit(const std::vector<Compound>& compounds, std::string_view scope_id, bool nested) {
    std::string out;
    const size_t last = compounds.size() - 1;
    for (size_t i = 0; i < compounds.size(); ++i) {
        if (i > 0) out += compounds[i].leading;
        std::string host;
        if (rewrite_host(compounds[i].text, scope_id, &host)) out += host;
        // A direct & already requires the scoped parent. Adding an internal
        // scope marker would make :host { &.active {...} } unable to match
        // its host, which carries the host marker instead.
        else if (nested && has_direct_nesting(compounds[i].text)) out += compounds[i].text;
        else if (i == last) out += with_scope_attribute(compounds[i].text, scope_id);
        else out += compounds[i].text;
    }
    return out;
}

void scope_single(std::string_view selector, std::string_view scope_id, std::vector<std::string>* out,
                  bool nested) {
    std::vector<std::vector<Compound>> variants{split_compounds(selector)};
    const size_t count = variants[0].size();
    for (size_t i = 0; i < count; ++i) {
        const std::vector<std::string> alts = host_alternatives(variants[0][i].text);
        if (alts.empty()) continue;
        std::vector<std::vector<Compound>> expanded;
        for (const std::vector<Compound>& v : variants) {
            for (const std::string& alt : alts) {
                std::vector<Compound> copy = v;
                copy[i].text = alt;
                expanded.push_back(std::move(copy));
            }
        }
        variants = std::move(expanded);
    }
    for (const std::vector<Compound>& v : variants) out->push_back(emit(v, scope_id, nested));
}

std::vector<std::string> scope_selectors(std::string_view selector_list, std::string_view scope_id, bool nested) {
    std::vector<std::string> out;
    for (const std::string_view single : split_top_level_commas(selector_list)) {
        const std::string_view s = trim(single);
        if (!s.empty()) scope_single(s, scope_id, &out, nested);
    }
    return out;
}

void scope_rules(std::vector<RulePtr>& rules, std::string_view scope_id, bool nested = false,
                 bool scope_declarations = false) {
    for (RulePtr& r : rules) {
        if (r->kind() == RuleKind::Style) {
            auto* sr = static_cast<StyleRule*>(r.get());
            if (sr->nested_declarations && scope_declarations) {
                // @scope's own declarations target its root, which can be an
                // internal element or this component's host. Keep that root
                // inside the component even when its prelude matches outside.
                sr->nested_declarations = false;
                sr->selectors = {":where(:scope):is([" + std::string(kComponentScopeAttribute) +
                    "=\"" + std::string(scope_id) + "\"],[" + kComponentHostAttribute +
                    "=\"" + std::string(scope_id) + "\"])"};
                continue;
            }
            std::vector<std::string> scoped;
            for (const std::string& sel : sr->selectors) {
                for (std::string& s : scope_selectors(sel, scope_id, nested)) scoped.push_back(std::move(s));
            }
            sr->selectors = std::move(scoped);
            scope_rules(sr->nested_rules, scope_id, true);
            continue;
        }
        auto* at = static_cast<GenericAtRule*>(r.get());
        if (at->name == "media" || at->name == "supports" || at->name == "layer" ||
            at->name == "container" || at->name == "scope") {
            if (at->name == "scope" && !at->declarations.empty()) {
                auto run = std::make_unique<StyleRule>();
                run->nested_declarations = true;
                run->source_url = at->source_url;
                run->declarations = std::move(at->declarations);
                at->nested_rules.insert(at->nested_rules.begin(), std::move(run));
            }
            scope_rules(at->nested_rules, scope_id, at->name == "scope" ? false : nested,
                        at->name == "scope");
        }
    }
}

} // namespace

std::vector<std::string> scope_selector_list(std::string_view selector_list, std::string_view scope_id) {
    return scope_selectors(selector_list, scope_id, false);
}

void scope_stylesheet(Stylesheet* sheet, std::string_view scope_id) {
    if (!sheet || scope_id.empty()) return;
    scope_rules(sheet->rules, scope_id);
}

} // namespace weva
