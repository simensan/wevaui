// Character classes for text that is not English.
//
// Two things need them and neither works without: word-by-word caret movement
// (Ctrl+Arrow, Ctrl+Backspace, double-click) and line breaking in scripts that
// have no spaces to break at.
//
// The classes come from CSS Text L3 section 5.3 and JIS X 4051, and are the
// same sets the C# engine carries in LineBreakClasses -- the port answers the
// same questions with the same answers, which is what parity means here.
//
// Everything takes a codepoint. Byte offsets into UTF-8 are the caller's job,
// and `utf8_at` / `utf8_before` are here for that.
#ifndef WEVA_TEXT_CLASSES_H
#define WEVA_TEXT_CLASSES_H

#include <cstddef>
#include <string_view>

namespace weva {

// ---- UTF-8 walking -------------------------------------------------------
// A byte offset is only a position if it starts a codepoint; these two never
// stop inside one.

// The codepoint starting at `at`, and how many bytes it took. Returns 0 and a
// length of 1 for a malformed byte, so a walk over broken input still ends.
int utf8_at(std::string_view text, std::size_t at, std::size_t* length);

// The codepoint ENDING at `at` -- what is to the left of a caret there -- and
// how many bytes it took.
int utf8_before(std::string_view text, std::size_t at, std::size_t* length);

// ---- CJK -----------------------------------------------------------------

// Han, kana, hangul, and the fullwidth forms: characters that are written
// without spaces between them, so a line may break between any two.
bool is_cjk_ideographic(int codepoint);

// Extension B and beyond, which live above the BMP.
bool is_cjk_supplementary(int codepoint);

// A break is forbidden BEFORE these: they cannot start a line. Closing
// brackets, the ideographic comma and full stop, and the fullwidth closers.
bool is_kinsoku_close(int codepoint);

// The part of that set which `line-break: loose` lifts: small kana, the
// prolonged sound mark, hyphen-likes, iteration marks, and centred
// punctuation.
bool is_loose_only_kinsoku_close(int codepoint);

// A break is forbidden AFTER these: they cannot end a line. Opening brackets
// and opening quotation marks.
bool is_kinsoku_open(int codepoint);

// Everything that takes part in CJK flow -- the ideographs plus the kinsoku
// punctuation that clings to them. This is the test for "is this text the kind
// that breaks between characters".
bool is_cjk_flow_char(int codepoint);

// ---- word boundaries -----------------------------------------------------
//
// The simplified model browsers use for Ctrl+Arrow, not full UAX #29:
//
//   a run of whitespace and punctuation is one separator unit;
//   a run of letters, digits and underscores is one word;
//   a CJK codepoint is a word by itself -- Ctrl+Right through a Japanese run
//   stops at every character, which is what Chrome does.
//
// CJK wins over the other two when a character could be either.

// True for a character that separates words: whitespace, or punctuation that
// is not a word character and not CJK.
bool is_word_separator(int codepoint);

// True for a character that a word is made of.
bool is_word_char(int codepoint);

// Where Ctrl+Left lands: skip the separators immediately to the left, then the
// word before them. Returns 0 at the start.
std::size_t previous_word_boundary(std::string_view text, std::size_t at);

// Where Ctrl+Right lands: skip the separators immediately to the right, then
// the word after them. Returns the length at the end.
std::size_t next_word_boundary(std::string_view text, std::size_t at);

// The unit a double-click selects: the run under the caret, whichever kind it
// is. On the seam between a word and what follows it the word to the LEFT
// wins, which is what clicking a word's trailing edge does in a browser.
void word_range_at(std::string_view text, std::size_t at, std::size_t* start, std::size_t* end);

}   // namespace weva

#endif   // WEVA_TEXT_CLASSES_H
