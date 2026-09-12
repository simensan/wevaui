// See weva/layout_dump.h. Moved here from tools/weva_dump/main.cpp so the C
// ABI can serve the same dump; the comments are the tool's.
#include "weva/layout_dump.h"

#include "weva/dom.h"
#include "weva/computed_style.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace weva {
namespace {

std::string json_escape(std::string_view v) {
    std::string out;
    out.reserve(v.size());
    for (char c : v) {
        switch (c) {
            case '\\': out += "\\\\"; break;
            case '"':  out += "\\\""; break;
            case '\r': out += "\\r";  break;
            case '\n': out += "\\n";  break;
            default:   out += c;      break;
        }
    }
    return out;
}


// Matches C#'s Math.Round(v, 2, MidpointRounding.AwayFromZero) followed by
// value.ToString("0.##", InvariantCulture).
//
// The explicit round matters. printf("%.4f") rounds half-to-even in glibc,
// while C# Math.Round(.., AwayFromZero) rounds half away from zero, so a
// midpoint formats differently on the two sides. Every such midpoint would
// surface as a phantom layout difference in the oracle, and chasing those
// instead of real bugs is exactly how a differential harness loses its
// credibility. Round explicitly, then format a value already exact at 4dp.
//
// FOUR decimals, not two. The two engines are separate implementations, so
// they accumulate the same arithmetic in slightly different orders and land
// fractions of a ulp apart. At 2dp such a pair straddles a rounding boundary
// every so often and prints as a 0.01 difference — which the oracle compares
// EXACTLY and reports, and which then cascades to every box below it. On
// weva-landing that produced 80 differences from one 0.01 step at `.stats`,
// burying a real 56px difference further down the page. At 4dp the pair
// agrees and the real difference is what surfaces. Measured across the
// corpora the change is strictly better: hand and harvest identical, samples
// 15 -> 16 agreeing (one page was only ever a rounding artifact).
std::string format_num_impl(double v) {
    double scaled = v * 10000.0;
    double rounded = (scaled < 0.0 ? -std::floor(-scaled + 0.5) : std::floor(scaled + 0.5)) / 10000.0;
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.4f", rounded);
    std::string s(buf);
    if (s.find('.') != std::string::npos) {
        while (!s.empty() && s.back() == '0') s.pop_back();
        if (!s.empty() && s.back() == '.') s.pop_back();
    }
    if (s == "-0") s = "0";
    return s;
}


std::string_view trim_ws(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\n')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\n')) s.remove_suffix(1);
    return s;
}


double length_percent_px(std::string_view token, double basis) {
    token = trim_ws(token);
    if (token.empty()) return 0;
    std::string s(token);
    if (s.back() == '%') {
        s.pop_back();
        return std::strtod(s.c_str(), nullptr) * basis * 0.01;
    }
    if (s.size() > 2 && (s.compare(s.size() - 2, 2, "px") == 0 || s.compare(s.size() - 2, 2, "PX") == 0)) {
        s.resize(s.size() - 2);
    }
    char* end = nullptr;
    const double v = std::strtod(s.c_str(), &end);
    return (end && *end == '\0') ? v : 0;
}


void split_first_two(std::string_view args, std::string_view* first, std::string_view* second) {
    *first = args;
    *second = std::string_view();
    int depth = 0;
    for (std::size_t i = 0; i < args.size(); ++i) {
        const char c = args[i];
        if (c == '(') ++depth;
        else if (c == ')') --depth;
        else if ((c == ',' || c == ' ' || c == '\t') && depth == 0) {
            *first = trim_ws(args.substr(0, i));
            std::size_t j = i + 1;
            while (j < args.size() && (args[j] == ',' || args[j] == ' ' || args[j] == '\t')) ++j;
            *second = j < args.size() ? trim_ws(args.substr(j)) : std::string_view();
            return;
        }
    }
    *first = trim_ws(args);
}


void transform_translation(const Box& b, double* tx, double* ty) {
    *tx = 0;
    *ty = 0;
    if (!b.style) return;
    // The individual `translate` property applies after the list.
    const std::string_view own = trim_ws(b.style->get("translate"));
    if (!own.empty() && own != "none") {
        std::string_view a, c2;
        split_first_two(own, &a, &c2);
        *tx += length_percent_px(a, b.width);
        if (!c2.empty()) *ty += length_percent_px(c2, b.height);
    }
    const std::string_view raw = trim_ws(b.style->get("transform"));
    if (raw.empty() || raw == "none") return;
    std::size_t cursor = 0;
    while (cursor < raw.size()) {
        const std::size_t open = raw.find('(', cursor);
        if (open == std::string_view::npos) break;
        int depth = 0;
        std::size_t close = std::string_view::npos;
        for (std::size_t i = open; i < raw.size(); ++i) {
            if (raw[i] == '(') ++depth;
            else if (raw[i] == ')' && --depth == 0) { close = i; break; }
        }
        if (close == std::string_view::npos) break;
        std::string name(trim_ws(raw.substr(cursor, open - cursor)));
        for (char& c : name) c = static_cast<char>((c >= 'A' && c <= 'Z') ? c - 'A' + 'a' : c);
        const std::string_view args = raw.substr(open + 1, close - open - 1);
        const auto ends_with = [&](const char* suffix) {
            const std::size_t n = std::strlen(suffix);
            return name.size() >= n && name.compare(name.size() - n, n, suffix) == 0;
        };
        std::string_view a, c2;
        split_first_two(args, &a, &c2);
        if (ends_with("translatex")) {
            *tx += length_percent_px(a, b.width);
        } else if (ends_with("translatey")) {
            *ty += length_percent_px(a, b.height);
        } else if (ends_with("translate")) {
            *tx += length_percent_px(a, b.width);
            *ty += length_percent_px(c2, b.height);
        }
        cursor = close + 1;
    }
}


}  // namespace

std::string layout_dump_number(double v) { return format_num_impl(v); }

void collect_layout_dump(const BoxTree& tree, BoxId id, double parent_x, double parent_y,
                         int depth, std::vector<ElementRect>* out,
                         std::set<const Element*>* seen) {
    if (id == kNoBox) return;
    const Box& b = tree[id];
    double tx = 0, ty = 0;
    transform_translation(b, &tx, &ty);
    const double x = parent_x + b.x + tx;
    const double y = parent_y + b.y + ty;

    // `html` and `body` do not appear in the dump but do not consume a level
    // either, so a top-level div is depth 1 on both sides.
    const bool is_wrapper =
        b.element && (b.element->tag_name() == "html" || b.element->tag_name() == "body");
    const bool is_principal = b.element && !is_wrapper && b.kind != BoxKind::Line &&
                              b.kind != BoxKind::AnonymousBlock &&
                              b.kind != BoxKind::AnonymousInline &&
                              b.kind != BoxKind::Text && seen->insert(b.element).second;
    if (is_principal) {
        ElementRect r;
        r.depth = depth;
        r.tag = b.element->tag_name();
        r.id = std::string(b.element->id());
        r.cls = std::string(b.element->class_name());
        r.x = x;
        r.y = y;
        r.w = b.width;
        r.h = b.height;
        out->push_back(r);
    }

    for (BoxId c : tree.children(id)) {
        collect_layout_dump(tree, c, x, y, is_wrapper ? depth : depth + 1, out, seen);
    }
}


std::string layout_dump_json(std::string_view source, int width, int height,
                             const std::vector<ElementRect>& boxes) {
    std::string sb;
    sb += "{\n";
    sb += "  \"source\": \"" + json_escape(std::string(source)) + "\",\n";
    sb += "  \"width\": " + std::to_string(width) + ",\n";
    sb += "  \"height\": " + std::to_string(height) + ",\n";
    sb += "  \"count\": " + std::to_string(boxes.size()) + ",\n";
    sb += "  \"elements\": [";
    for (std::size_t i = 0; i < boxes.size(); ++i) {
        const ElementRect& b = boxes[i];
        if (i > 0) sb += ",";
        sb += "\n    {";
        sb += "\"i\":" + std::to_string(i) + ",";
        sb += "\"depth\":" + std::to_string(b.depth) + ",";
        sb += "\"tag\":\"" + json_escape(b.tag) + "\",";
        sb += "\"id\":\"" + json_escape(b.id) + "\",";
        sb += "\"cls\":\"" + json_escape(b.cls) + "\",";
        sb += "\"path\":\"" + json_escape(b.path) + "\",";
        sb += "\"x\":" + format_num_impl(b.x) + ",";
        sb += "\"y\":" + format_num_impl(b.y) + ",";
        sb += "\"w\":" + format_num_impl(b.w) + ",";
        sb += "\"h\":" + format_num_impl(b.h);
        sb += "}";
    }
    sb += "\n  ]\n}\n";

    return sb;
}

}  // namespace weva
