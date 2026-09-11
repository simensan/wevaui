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
    for (size_t i = open; i < s.size(); ++i) {
        if (s[i] == '(') ++depth;
        else if (s[i] == ')' && --depth == 0) {
            *inner = s.substr(open + 1, i - open - 1);
            return i + 1;
        }
    }
    return std::string_view::npos;
}

bool parse_prelude(std::string_view prelude, ImportSpec* out) {
    std::string_view s = trim(prelude);
    if (s.empty()) return false;
    // The URL: "x", 'x', url(x), url("x").
    if (s.front() == '"' || s.front() == '\'') {
        const size_t close = s.find(s.front(), 1);
        if (close == std::string_view::npos) return false;
        out->url = std::string(s.substr(1, close - 1));
        s = trim(s.substr(close + 1));
    } else if (istarts_with(s, "url(")) {
        std::string_view inner;
        const size_t after = balanced(s, 3, &inner);
        if (after == std::string_view::npos) return false;
        inner = trim(inner);
        if (!inner.empty() && (inner.front() == '"' || inner.front() == '\'')) {
            const size_t close = inner.find(inner.front(), 1);
            if (close == std::string_view::npos) return false;
            inner = inner.substr(1, close - 1);
        }
        out->url = std::string(inner);
        s = trim(s.substr(after));
    } else {
        return false;
    }
    if (out->url.empty()) return false;

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

void expand(Stylesheet* sheet, const StylesheetLoader& load, std::vector<std::string>* missing,
            std::vector<std::string>* loading, int depth, int* resolved) {
    std::vector<RulePtr> out;
    out.reserve(sheet->rules.size());
    // §4: an @import after any other rule (but @charset and a @layer
    // statement) is invalid and ignored, as browsers ignore it.
    bool seen_rule = false;
    static int anonymous_layers = 0;
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
        // A sheet already on the way in is a cycle; it contributes nothing
        // twice.
        if (std::find(loading->begin(), loading->end(), spec.url) != loading->end()) continue;
        if (depth >= kMaxImportDepth) continue;
        std::string css;
        if (!load(spec.url, &css)) {
            if (missing) missing->push_back(spec.url);
            continue;
        }
        auto imported = std::make_unique<Stylesheet>();
        CssParseError err;
        if (!parse_stylesheet(css, false, imported.get(), &err)) continue;
        loading->push_back(spec.url);
        expand(imported.get(), load, missing, loading, depth + 1, resolved);
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
        if (spec.anonymous_layer && spec.layer.empty()) {
            spec.layer = "weva-anonymous-import-" + std::to_string(++anonymous_layers);
        }
        if (!spec.layer.empty()) wrap("layer", spec.layer);
        if (!spec.supports.empty()) wrap("supports", "(" + spec.supports + ")");
        if (!trim(spec.media).empty()) wrap("media", spec.media);
        for (RulePtr& imported_rule : rules) out.push_back(std::move(imported_rule));
    }
    sheet->rules = std::move(out);
}

} // namespace

int expand_imports(Stylesheet* sheet, const StylesheetLoader& load, std::vector<std::string>* missing) {
    if (!sheet || !load) return 0;
    int resolved = 0;
    std::vector<std::string> loading;
    expand(sheet, load, missing, &loading, 0, &resolved);
    return resolved;
}

} // namespace weva
