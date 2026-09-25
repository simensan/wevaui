#include "check.h"
#include "weva/grapheme.h"
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace {
void append_utf8(std::string& text, unsigned cp) {
    if (cp < 0x80) text += static_cast<char>(cp);
    else if (cp < 0x800) {
        text += static_cast<char>(0xC0 | (cp >> 6));
        text += static_cast<char>(0x80 | (cp & 63));
    } else if (cp < 0x10000) {
        text += static_cast<char>(0xE0 | (cp >> 12));
        text += static_cast<char>(0x80 | ((cp >> 6) & 63));
        text += static_cast<char>(0x80 | (cp & 63));
    } else {
        text += static_cast<char>(0xF0 | (cp >> 18));
        text += static_cast<char>(0x80 | ((cp >> 12) & 63));
        text += static_cast<char>(0x80 | ((cp >> 6) & 63));
        text += static_cast<char>(0x80 | (cp & 63));
    }
}
}

void test_grapheme_conformance() {
    const auto path = std::filesystem::path(__FILE__).parent_path() / "data/GraphemeBreakTest-17.0.0.txt";
    std::ifstream file(path);
    CHECK(file.good());
    std::string line;
    int cases = 0;
    while (std::getline(file, line)) {
        line = line.substr(0, line.find('#'));
        std::istringstream tokens(line);
        std::string token, text;
        std::vector<size_t> expected;
        while (tokens >> token) {
            if (token == u8"÷") expected.push_back(text.size());
            else if (token != u8"×") append_utf8(text, static_cast<unsigned>(std::stoul(token, nullptr, 16)));
        }
        if (expected.empty()) continue;
        ++cases;
        std::vector<size_t> actual{0};
        weva::Graphemes clusters(text);
        size_t end = 0;
        while (clusters.next(&end)) actual.push_back(end);
        CHECK(actual == expected);
        for (size_t i = 1; i < expected.size(); ++i) {
            CHECK(weva::previous_grapheme(text, expected[i]) == expected[i - 1]);
            CHECK(weva::next_grapheme(text, expected[i - 1]) == expected[i]);
        }
    }
    CHECK(cases == 766);
    CHECK(weva::previous_grapheme("", 9) == 0);
    CHECK(weva::next_grapheme("", 9) == 0);
    const std::string invalid("\xF0\x80\xFFx", 4);
    weva::Graphemes clusters(invalid);
    size_t end = 0;
    for (size_t i = 1; i <= invalid.size(); ++i) {
        CHECK(clusters.next(&end));
        CHECK(end == i);
    }
    CHECK(!clusters.next(&end));
}
