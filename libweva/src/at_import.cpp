#include "weva/at_import.h"

#include "weva/css_rule.h"

#include <algorithm>
#include <cctype>
#include <memory>
#include <string>
#include <vector>

namespace weva {

namespace {

constexpr int kMaxImportDepth = 8;
// Depth and the ancestor check stop cycles, not fan-out: nine sheets that each
// import the next twenty times are 20^8 loads, every one parsed and copied.
// A document's whole import graph gets this many loads.
constexpr int kMaxImports = 256;

std::string_view trim(std::string_view s) {
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) s.remove_prefix(1);
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.remove_suffix(1);
    return s;
}

bool istarts_with(std::string_view s, std::string_view prefix) {
    if (s.size() < prefix.size()) return false;
    for (size_t i = 0; i < prefix.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(s[i])) != std::tolower(static_cast<unsigned char>(prefix[i]))) return false;
    }
    return true;
}

// What one @import prelude says: where, and under which conditions.
struct ImportSpec {
    std::string url;
    std::string layer;      // `layer(name)`; "" for none, and the anonymous `layer` keeps ""
    bool anonymous_layer = false;
    std::string supports;   // the text inside supports(...)
    std::string media;      // the media query list, possibly empty
};

// The text inside the parentheses that open at `open`, and the index past
// the closing one; npos when unbalanced.
size_t balanced(std::string_view s, size_t open, std::string_view* inner) {
    int depth = 0;
    char quote = 0;
    for (size_t i = open; i < s.size(); ++i) {
        if (s[i] == '\\') { if (i + 1 < s.size()) ++i; continue; }
        if (quote) { if (s[i] == quote) quote = 0; continue; }
        if (s[i] == '\'' || s[i] == '"') { quote = s[i]; continue; }
        if (s[i] == '(') ++depth;
        else if (s[i] == ')' && --depth == 0) {
            *inner = s.substr(open + 1, i - open - 1);
            return i + 1;
        }
    }
    return std::string_view::npos;
}

bool parse_prelude(std::string_view prelude, ImportSpec* out) {
    // Let the CSS tokenizer decode strings/escapes. A ')' inside a quoted
    // filename is content, not the end of url(), and an escaped quote is not
    // the end of a bare string URL.
    std::vector<CssToken> tokens;
    CssParseError error;
    if (!CssTokenizer(prelude, true).tokenize(&tokens, &error)) return false;
    size_t i = 0;
    const auto skip_space = [&]() {
        while (i < tokens.size() && tokens[i].kind == CssTokenKind::Whitespace) ++i;
    };
    skip_space();
    if (i >= tokens.size()) return false;
    if (tokens[i].kind == CssTokenKind::String || tokens[i].kind == CssTokenKind::Url) {
        out->url = tokens[i++].text;
    } else if (tokens[i].kind == CssTokenKind::Function &&
               tokens[i].text.size() == 3 && istarts_with(tokens[i].text, "url")) {
        ++i;
        skip_space();
        if (i >= tokens.size() || tokens[i].kind != CssTokenKind::String) return false;
        out->url = tokens[i++].text;
        skip_space();
        if (i >= tokens.size() || tokens[i++].kind != CssTokenKind::RParen) return false;
    } else {
        return false;
    }
    if (out->url.empty()) return false;
    std::string tail;
    for (; i < tokens.size(); ++i) tail += css_token_source(tokens[i]);
    std::string_view s = trim(tail);

    // Then, in order: layer | layer(name), supports(...), media queries.
    if (istarts_with(s, "layer(")) {
        std::string_view inner;
        const size_t after = balanced(s, 5, &inner);
        if (after == std::string_view::npos) return false;
        out->layer = std::string(trim(inner));
        s = trim(s.substr(after));
    } else if (istarts_with(s, "layer") && (s.size() == 5 || std::isspace(static_cast<unsigned char>(s[5])))) {
        out->anonymous_layer = true;
        s = trim(s.substr(5));
    }
    if (istarts_with(s, "supports(")) {
        std::string_view inner;
        const size_t after = balanced(s, 8, &inner);
        if (after == std::string_view::npos) return false;
        out->supports = std::string(trim(inner));
        s = trim(s.substr(after));
    }
    out->media = std::string(s);
    return true;
}

// Import identifiers stay relative to the document reader, but each nested
// reference first resolves against the importing file. Normalize dot segments
// so aliases also share the cycle guard. Preserve host schemes such as res://.
std::string import_url(std::string_view parent, std::string_view url) {
    const auto has_scheme = [](std::string_view path) {
        const size_t colon = path.find(':');
        return colon != std::string_view::npos && colon < path.find_first_of("/\\?#");
    };
    std::string joined;
    if (!url.empty() && (url.front() == '#' || url.front() == '?')) {
        const size_t end = url.front() == '?' ? parent.find_first_of("?#") : parent.find('#');
        return std::string(parent.substr(0, end)) + std::string(url);
    }
    if (!url.empty() && url.front() == '/' &&
        (istarts_with(parent, "http://") || istarts_with(parent, "https://"))) {
        const size_t scheme = parent.find(':');
        if (url.size() > 1 && url[1] == '/') joined.assign(parent.substr(0, scheme + 1));
        else joined.assign(parent.substr(0, parent.find('/', scheme + 3)));
    }
    if (!url.empty() && url.front() != '/' && url.front() != '\\' && !has_scheme(url)) {
        const std::string_view file = parent.substr(0, parent.find_first_of("?#"));
        const size_t slash = file.find_last_of('/');
        if (slash != std::string_view::npos) joined.assign(file.substr(0, slash + 1));
    }
    joined.append(url);
    std::string_view path(joined);
    std::string prefix;
    const size_t scheme = path.find("://");
    if (scheme != std::string_view::npos) {
        size_t start = scheme + 3;
        if (!istarts_with(path, "res://") && !istarts_with(path, "user://")) {
            // URL authorities are not directories that '..' can climb above.
            start = path.find('/', start);
            if (start == std::string_view::npos) return joined;
        }
        prefix.assign(path.substr(0, start));
        path.remove_prefix(start);
    } else if (has_scheme(path)) {
        // Drive-letter paths and opaque host URLs are already absolute.
        return joined;
    }
    const size_t suffix_at = path.find_first_of("?#");
    const std::string suffix(suffix_at == std::string_view::npos ? std::string_view{} : path.substr(suffix_at));
    path = path.substr(0, suffix_at);
    const bool rooted = !prefix.empty() || (!path.empty() && path.front() == '/');
    const bool leading_slash = !path.empty() && path.front() == '/';
    std::vector<std::string_view> parts;
    while (!path.empty()) {
        const size_t slash = path.find('/');
        const std::string_view part = path.substr(0, slash);
        if (part == "..") {
            if (!parts.empty() && parts.back() != "..") parts.pop_back();
            else if (!rooted) parts.push_back(part);
        } else if (!part.empty() && part != ".") {
            parts.push_back(part);
        }
        if (slash == std::string_view::npos) break;
        path.remove_prefix(slash + 1);
    }
    std::string result = prefix;
    if (leading_slash) result.push_back('/');
    for (size_t i = 0; i < parts.size(); ++i) {
        if (i) result.push_back('/');
        result.append(parts[i]);
    }
    return result + suffix;
}

void expand(Stylesheet* sheet, const StylesheetLoader& load, std::vector<std::string>* missing,
            std::vector<std::string>* loading, int depth, int* resolved, std::string_view parent) {
    std::vector<RulePtr> out;
    out.reserve(sheet->rules.size());
    // §4: an @import after any other rule (but @charset and a @layer
    // statement) is invalid and ignored, as browsers ignore it.
    bool seen_rule = false;
    for (RulePtr& r : sheet->rules) {
        if (!r) continue;
        if (r->kind() != RuleKind::At) { seen_rule = true; out.push_back(std::move(r)); continue; }
        auto* ar = static_cast<GenericAtRule*>(r.get());
        if (ar->name != "import" || ar->has_block) {
            if (ar->name != "charset" && !(ar->name == "layer" && !ar->has_block)) seen_rule = true;
            out.push_back(std::move(r));
            continue;
        }
        if (seen_rule) continue;
        ImportSpec spec;
        if (!parse_prelude(ar->prelude, &spec)) continue;   // malformed: dropped, as a browser does
        spec.url = import_url(parent, spec.url);
        // A sheet already on the way in is a cycle; it contributes nothing
        // twice.
        if (std::find(loading->begin(), loading->end(), spec.url) != loading->end()) continue;
        if (depth >= kMaxImportDepth) continue;
        if (*resolved >= kMaxImports) continue;
        std::string css;
        if (!load(spec.url, &css)) {
            if (missing) missing->push_back(spec.url);
            continue;
        }
        auto imported = std::make_unique<Stylesheet>();
        CssParseError err;
        if (!parse_stylesheet(css, false, imported.get(), &err)) continue;
        set_stylesheet_source(imported.get(), spec.url);
        loading->push_back(spec.url);
        expand(imported.get(), load, missing, loading, depth + 1, resolved, spec.url);
        loading->pop_back();
        ++*resolved;

        // Wrap in the conditions the import stated, innermost first: layer,
        // then supports, then media -- so a false media query hides the layer
        // assignment along with the rules.
        std::vector<RulePtr> rules = std::move(imported->rules);
        const auto wrap = [&](const char* name, const std::string& prelude) {
            auto w = std::make_unique<GenericAtRule>();
            w->name = name;
            w->prelude = prelude;
            w->has_block = true;
            w->nested_rules = std::move(rules);
            rules.clear();
            rules.push_back(std::move(w));
        };
        // The cascade already owns anonymous layer identities. Inventing a
        // global author-visible name here both races and lets CSS reopen it.
        if (spec.anonymous_layer || !spec.layer.empty()) wrap("layer", spec.layer);
        if (!spec.supports.empty()) wrap("supports", "(" + spec.supports + ")");
        if (!trim(spec.media).empty()) wrap("media", spec.media);
        for (RulePtr& imported_rule : rules) out.push_back(std::move(imported_rule));
    }
    sheet->rules = std::move(out);
}

} // namespace

void set_stylesheet_source(Stylesheet* sheet, std::string_view source_url) {
    if (!sheet) return;
    sheet->source_url = std::string(source_url);
    const std::function<void(std::vector<RulePtr>&)> assign = [&](std::vector<RulePtr>& rules) {
        for (auto& rule : rules) {
            rule->source_url = sheet->source_url;
            if (rule->kind() == RuleKind::Style) assign(static_cast<StyleRule&>(*rule).nested_rules);
            else assign(static_cast<GenericAtRule&>(*rule).nested_rules);
        }
    };
    assign(sheet->rules);
}

void resolve_stylesheet_value_urls(std::string* value, std::string_view source_url) {
    if (!value || source_url.empty() || value->find('(') == std::string::npos) return;
    std::vector<CssToken> tokens;
    CssParseError error;
    if (!CssTokenizer(*value, false).tokenize(&tokens, &error)) return;
    bool changed = false;
    const auto resolve = [&](CssToken& token) {
        if (token.text.empty()) return;
        std::string resolved = import_url(source_url, token.text);
        if (resolved != token.text) { token.text = std::move(resolved); changed = true; }
    };
    for (size_t i = 0; i < tokens.size(); ++i) {
        if (tokens[i].kind == CssTokenKind::Url) resolve(tokens[i]);
        else if (tokens[i].kind == CssTokenKind::Function && tokens[i].text.size() == 3 &&
                 istarts_with(tokens[i].text, "url")) {
            size_t content = i + 1;
            while (content < tokens.size() && tokens[content].kind == CssTokenKind::Whitespace) ++content;
            if (content < tokens.size() && tokens[content].kind == CssTokenKind::String) resolve(tokens[content]);
        }
    }
    if (!changed) return;
    value->clear();
    for (const auto& token : tokens) *value += css_token_source(token);
}

int expand_imports(Stylesheet* sheet, const StylesheetLoader& load, std::vector<std::string>* missing) {
    if (!sheet || !load) return 0;
    int resolved = 0;
    std::vector<std::string> loading;
    if (!sheet->source_url.empty()) loading.push_back(import_url({}, sheet->source_url));
    expand(sheet, load, missing, &loading, 0, &resolved, sheet->source_url);
    return resolved;
}

} // namespace weva
