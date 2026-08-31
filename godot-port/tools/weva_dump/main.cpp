// weva_dump — the C++ side of the differential oracle (see docs/ORACLE.md).
//
// Mirrors Tools/BaselineGen/LayoutDump.cs so the two implementations can be
// diffed element-by-element. Usage matches BaselineGen's:
//
//     weva_dump <html> <width> <height> [out] [css]
//
// Phase 0: there is no engine behind this yet, so it emits an empty element
// list. That is the intended Phase 0 exit state — the harness must correctly
// report "N elements expected, 0 produced" before any engine code exists.

#include "weva/arena.h"
#include "weva/intern.h"
#include "weva/status.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace {

struct ElementRect {
    int depth = 0;
    std::string tag, id, cls;
    double x = 0, y = 0, w = 0, h = 0;
};

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
// The explicit round matters. printf("%.2f") rounds half-to-even in glibc,
// while C# Round2 rounds half away from zero, so 0.125 formats as "0.12" here
// and "0.13" there. Every such midpoint would surface as a phantom one-cent
// layout difference in the oracle, and chasing those instead of real bugs is
// exactly how a differential harness loses its credibility. Round explicitly,
// then format a value that is already exact at 2dp.
std::string format_num(double v) {
    double scaled = v * 100.0;
    double rounded = (scaled < 0.0 ? -std::floor(-scaled + 0.5) : std::floor(scaled + 0.5)) / 100.0;
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.2f", rounded);
    std::string s(buf);
    if (s.find('.') != std::string::npos) {
        while (!s.empty() && s.back() == '0') s.pop_back();
        if (!s.empty() && s.back() == '.') s.pop_back();
    }
    if (s == "-0") s = "0";
    return s;
}

void write_json(const std::string& out_path, const std::string& source,
                int width, int height, const std::vector<ElementRect>& boxes) {
    std::string sb;
    sb += "{\n";
    sb += "  \"source\": \"" + json_escape(source) + "\",\n";
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
        sb += "\"x\":" + format_num(b.x) + ",";
        sb += "\"y\":" + format_num(b.y) + ",";
        sb += "\"w\":" + format_num(b.w) + ",";
        sb += "\"h\":" + format_num(b.h);
        sb += "}";
    }
    sb += "\n  ]\n}\n";

    std::FILE* f = std::fopen(out_path.c_str(), "wb");
    if (!f) {
        std::fprintf(stderr, "weva_dump: cannot write %s\n", out_path.c_str());
        std::exit(2);
    }
    std::fwrite(sb.data(), 1, sb.size(), f);
    std::fclose(f);
}

bool read_file(const std::string& path, std::string* out) {
    std::FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) return false;
    std::fseek(f, 0, SEEK_END);
    long n = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    out->resize(static_cast<std::size_t>(n));
    std::size_t got = std::fread(out->data(), 1, static_cast<std::size_t>(n), f);
    out->resize(got);
    std::fclose(f);
    return true;
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 4) {
        std::fprintf(stderr,
            "Usage: weva_dump <htmlPath> <width> <height> [outPath] [cssPath]\n");
        return 1;
    }

    std::string html_path = argv[1];
    int width  = std::atoi(argv[2]);
    int height = std::atoi(argv[3]);
    std::string out_path = argc > 4 ? argv[4] : "dump.json";
    std::string css_path = argc > 5 ? argv[5] : "";

    if (width <= 0 || height <= 0) {
        std::fprintf(stderr, "Viewport must be positive integer width/height.\n");
        return 1;
    }

    // The document source buffer is owned here and outlives every parse slice
    // that will eventually point into it (docs/CONVENTIONS.md).
    std::string html, css;
    if (!read_file(html_path, &html)) {
        std::fprintf(stderr, "HTML not found: %s\n", html_path.c_str());
        return 1;
    }
    if (!css_path.empty() && !read_file(css_path, &css)) {
        std::fprintf(stderr, "CSS not found: %s\n", css_path.c_str());
        return 1;
    }

    weva::Arena arena;
    weva::SymbolTable symbols;
    std::vector<ElementRect> boxes;

    // ---- Phase 1+ : parse -> cascade -> layout fills `boxes` here. ----
    (void)arena; (void)symbols; (void)html; (void)css;

    write_json(out_path, html_path, width, height, boxes);
    std::printf("Wrote %zu elements -> %s\n", boxes.size(), out_path.c_str());
    return 0;
}
