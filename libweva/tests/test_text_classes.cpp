// Word boundaries, which decide where Ctrl+Arrow lands, what Ctrl+Backspace
// eats, and what a double click selects.
//
// The cases below are the ones a browser gets right and a naive
// "letters or not" test gets wrong: punctuation runs, CJK, and the seam where
// a word ends.
#include "weva/text_classes.h"

#include <string>

#include "check.h"

using namespace weva;

namespace {

// Named for readability: these read as positions in the string, and a bare
// integer in an expectation says nothing about which one it is.
std::size_t word_left(const std::string& s, std::size_t at) {
    return previous_word_boundary(s, at);
}
std::size_t word_right(const std::string& s, std::size_t at) {
    return next_word_boundary(s, at);
}
std::string word_at(const std::string& s, std::size_t at) {
    std::size_t from = 0, to = 0;
    word_range_at(s, at, &from, &to);
    return s.substr(from, to - from);
}

}   // namespace

void test_word_motion_over_ascii() {
    const std::string s = "the quick brown fox";

    // Right from the start passes the first word and stops at its end -- not
    // at the start of the next, which is where a "skip to the next letter"
    // implementation would put it.
    CHECK(word_right(s, 0) == 3);
    CHECK(word_right(s, 3) == 9);    // through the space, to the end of "quick"
    CHECK(word_right(s, 19) == 19);  // already at the end

    // Left is the mirror: to the START of the word behind.
    CHECK(word_left(s, 19) == 16);   // "fox"
    CHECK(word_left(s, 16) == 10);   // over the space, to "brown"
    CHECK(word_left(s, 0) == 0);

    // From inside a word, not from its edge.
    CHECK(word_right(s, 5) == 9);
    CHECK(word_left(s, 5) == 4);
}

void test_word_motion_over_punctuation() {
    // A run of punctuation is ONE unit, not one stop per mark: Ctrl+Right
    // through "..." crosses it whole.
    const std::string s = "a... b";
    CHECK(word_right(s, 1) == 6);   // over "... " and through "b"
    CHECK(word_left(s, 6) == 5);
    CHECK(word_left(s, 5) == 0);    // back over the punctuation to "a"

    // An underscore is part of a word, which is what makes identifiers move
    // as one: a programmer's Ctrl+Right over `max_length` crosses it whole.
    const std::string ident = "max_length here";
    CHECK(word_right(ident, 0) == 10);

    // A hyphen is not.
    const std::string hyphen = "well-known";
    CHECK(word_right(hyphen, 0) == 4);
}

void test_word_motion_over_cjk() {
    // Japanese has no spaces, and the browser rule is one stop per character.
    // The old byte test made the whole run one word, so Ctrl+Right jumped the
    // entire sentence.
    const std::string s = "日本語テスト";   // six characters, three bytes each
    CHECK(word_right(s, 0) == 3);
    CHECK(word_right(s, 3) == 6);
    CHECK(word_left(s, 6) == 3);
    CHECK(word_left(s, 18) == 15);

    // And the boundary between scripts is a boundary.
    const std::string mixed = "hello日本";
    CHECK(word_right(mixed, 0) == 5);
    CHECK(word_right(mixed, 5) == 8);
}

void test_word_selection_picks_one_unit() {
    const std::string s = "the quick brown fox";
    CHECK(word_at(s, 5) == "quick");
    CHECK(word_at(s, 0) == "the");

    // On the seam after a word, the word wins -- clicking a word's trailing
    // edge selects the word, not the space after it.
    CHECK(word_at(s, 3) == "the");
    CHECK(word_at(s, 19) == "fox");

    // Inside a run of spaces, the spaces are the unit.
    const std::string spaced = "a   b";
    CHECK(word_at(spaced, 2) == "   ");

    // One CJK character, not the run.
    const std::string cjk = "日本語";
    CHECK(word_at(cjk, 3) == "本");

    CHECK(word_at("", 0).empty());
}

void test_utf8_walking_never_splits_a_character() {
    const std::string s = "aé日";   // 1 + 2 + 3 bytes
    std::size_t n = 0;
    CHECK(utf8_at(s, 0, &n) == 'a');
    CHECK(n == 1);
    CHECK(utf8_at(s, 1, &n) == 0xE9);
    CHECK(n == 2);
    CHECK(utf8_at(s, 3, &n) == 0x65E5);
    CHECK(n == 3);
    CHECK(utf8_at(s, 6, &n) == 0);   // past the end

    CHECK(utf8_before(s, 6, &n) == 0x65E5);
    CHECK(n == 3);
    CHECK(utf8_before(s, 3, &n) == 0xE9);
    CHECK(n == 2);
    CHECK(utf8_before(s, 0, &n) == 0);

    // A walk over bytes that are not valid UTF-8 still terminates, which is
    // what keeps a malformed attribute from hanging the caret.
    // Split, because "\xFEb" is one hex escape with too many digits and not
    // the two bytes it looks like.
    const std::string broken = "a\xFF\xFE" "b";
    std::size_t i = 0;
    int steps = 0;
    while (i < broken.size() && steps < 10) {
        utf8_at(broken, i, &n);
        i += n;
        ++steps;
    }
    CHECK(i == broken.size());
}

void test_cjk_classification() {
    CHECK(is_cjk_ideographic(0x65E5));    // 日
    CHECK(is_cjk_ideographic(0x3042));    // あ
    CHECK(is_cjk_ideographic(0xAC00));    // 가
    CHECK(!is_cjk_ideographic('a'));

    // The punctuation that clings to a CJK run counts as flow, so a break
    // decision either side of it sees CJK on both sides.
    CHECK(is_kinsoku_close(0x3002));      // 。cannot start a line
    CHECK(is_kinsoku_open(0x300C));       // 「cannot end one
    CHECK(is_cjk_flow_char(0x3002));
    CHECK(is_cjk_flow_char(0x300C));

    // `line-break: loose` lifts the small kana and the sound mark, so they are
    // in the loose-only set rather than the universal one.
    CHECK(is_loose_only_kinsoku_close(0x30FC));   // ー
    CHECK(is_loose_only_kinsoku_close(0x3063));   // っ
    CHECK(!is_kinsoku_close(0x30FC));

    // Latin punctuation is not CJK flow, whatever else it is.
    CHECK(!is_cjk_flow_char('.'));
    CHECK(!is_cjk_flow_char(' '));
}

void test_accented_latin_is_a_word_character() {
    // Above ASCII but not CJK: a French or Norwegian word is one word, and an
    // implementation that only knows [a-z] cuts it in half.
    const std::string s = "café blårev";
    CHECK(word_right(s, 0) == 5);    // "café" is 5 bytes
    CHECK(word_at(s, 0) == "café");
    CHECK(word_at(s, 6) == "blårev");

    // A non-breaking space still separates.
    const std::string nbsp = "a\xC2\xA0";
    CHECK(is_word_separator(0x00A0));
    CHECK(word_at(nbsp, 0) == "a");
}
