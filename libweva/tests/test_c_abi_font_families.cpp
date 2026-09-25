// ABI minor 41: the family names a document asks for, so a host can reach
// installed fonts by name the way a browser does.
#include "check.h"
#include "weva_c.h"
#include <cstring>
#include <string>
#include <vector>
namespace {
std::vector<std::string> names_of(const char* css, const char* html) {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_set_css(d, css, std::strlen(css));
    weva_document_update(d, 0);
    const size_t n = weva_document_font_family_names(d, nullptr, 0);
    std::string text(n + 1, '\0');
    weva_document_font_family_names(d, text.data(), text.size());
    text.resize(n);
    weva_document_destroy(d);
    std::vector<std::string> out;
    size_t start = 0;
    while (start <= text.size()) {
        const size_t nl = text.find('\n', start);
        const std::string line = text.substr(start, nl == std::string::npos ? std::string::npos : nl - start);
        if (!line.empty()) out.push_back(line);
        if (nl == std::string::npos) break;
        start = nl + 1;
    }
    return out;
}
std::string joined(const std::vector<std::string>& v) {
    std::string s;
    for (const std::string& x : v) { if (!s.empty()) s += '|'; s += x; }
    return s;
}
}   // namespace

void test_abi_font_family_names() {
    // Quotes stripped, order kept, generics and keywords dropped, a name once
    // whatever its case, the `font` shorthand's family included.
    CHECK_EQ(joined(names_of(
                 "body { font-family: \"Segoe UI\", Inter, sans-serif }"
                 "h1 { font-family: 'Playfair Display', serif }"
                 "code { font-family: monospace }"
                 "p { font-family: inter, system-ui }"
                 ".x { font: bold 14px Georgia, serif }"
                 ".y { font-family: inherit }"
                 ".z { font-family: var(--brand), Tahoma }",
                 "<body><p>hi</p></body>")),
             "Segoe UI|Inter|Playfair Display|Georgia|Tahoma");

    // Inline styles count; a rule nested in a media query counts; the
    // user-agent sheet's `sans-serif` does not.
    CHECK_EQ(joined(names_of(
                 "@media (min-width: 10px) { .m { font-family: Verdana } }",
                 "<body><p style=\"color: red; font-family: 'Comic Sans MS', cursive\">hi</p>"
                 "<span style=\"font-family: Verdana\">x</span></body>")),
             "Verdana|Comic Sans MS");

    // No names at all.
    CHECK(names_of("body { font-family: sans-serif }", "<body>hi</body>").empty());
    CHECK(weva_document_font_family_names(nullptr, nullptr, 0) == 0);
}
