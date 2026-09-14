// ABI minor 40: the Unicode facts a host's shaper asks the core for.
//
// The Godot host's TextServer works these out itself; the Unity host's
// FontEngine substitutes and positions glyphs but knows nothing about
// direction, mirroring or Arabic joining. It asks here, from the same ICU
// tables bidi.cpp resolves levels with, so both hosts and the core agree.
#include "check.h"
#include "weva_c.h"
#include <cstring>
namespace {
int32_t direction(const char* utf8) { return weva_text_direction(utf8, std::strlen(utf8)); }
constexpr uint32_t tag(const char (&s)[5]) {
    return (static_cast<uint32_t>(static_cast<unsigned char>(s[0])) << 24) |
           (static_cast<uint32_t>(static_cast<unsigned char>(s[1])) << 16) |
           (static_cast<uint32_t>(static_cast<unsigned char>(s[2])) << 8) |
           static_cast<uint32_t>(static_cast<unsigned char>(s[3]));
}
}   // namespace

void test_abi_text_properties() {
    // Direction: the first STRONG character decides; digits, spaces and
    // brackets are weak or neutral and skipped, as an auto-detecting shaper
    // and the Godot host's strong_direction() do.
    CHECK(direction("abc") == 0);
    CHECK(direction("") == 0);
    CHECK(direction("123") == 0);
    CHECK(direction("\xD7\x90\xD7\x91") == 1);                    // אב
    CHECK(direction("\xD8\xB3\xD9\x84\xD8\xA7\xD9\x85") == 1);    // سلام
    CHECK(direction("123 \xD7\x90") == 1);                        // digits, then Hebrew
    CHECK(direction("(\xD7\x90)") == 1);                          // brackets, then Hebrew
    CHECK(direction("abc \xD7\x90") == 0);                        // Latin first
    CHECK(direction("\xFF") == 0);                                // malformed: nothing strong
    CHECK(weva_text_direction(nullptr, 0) == 0);

    // Mirroring: brackets and guillemets have a partner, letters do not.
    CHECK(weva_char_mirror('(') == ')');
    CHECK(weva_char_mirror(')') == '(');
    CHECK(weva_char_mirror('[') == ']');
    CHECK(weva_char_mirror('{') == '}');
    CHECK(weva_char_mirror('<') == '>');
    CHECK(weva_char_mirror(0x00AB) == 0x00BB);   // « »
    CHECK(weva_char_mirror('a') == 'a');
    CHECK(weva_char_mirror(0x05D0) == 0x05D0);
    CHECK(weva_char_mirror(' ') == ' ');

    // Joining types, straight from ArabicShaping.txt.
    CHECK(weva_char_joining_type(0x0628) == 3);   // BEH: dual
    CHECK(weva_char_joining_type(0x0633) == 3);   // SEEN: dual
    CHECK(weva_char_joining_type(0x0627) == 1);   // ALEF: right-joining
    CHECK(weva_char_joining_type(0x062F) == 1);   // DAL: right-joining
    CHECK(weva_char_joining_type(0x0648) == 1);   // WAW: right-joining
    CHECK(weva_char_joining_type(0x200D) == 4);   // ZWJ: join-causing
    CHECK(weva_char_joining_type(0x0640) == 4);   // tatweel: join-causing
    CHECK(weva_char_joining_type(0x064E) == 5);   // FATHA: transparent
    CHECK(weva_char_joining_type(0x0651) == 5);   // SHADDA: transparent
    CHECK(weva_char_joining_type(0x200C) == 0);   // ZWNJ: non-joining
    CHECK(weva_char_joining_type('a') == 0);
    CHECK(weva_char_joining_type(' ') == 0);
    CHECK(weva_char_joining_type(0x05D0) == 0);   // Hebrew does not join

    // Scripts as ISO 15924, packed.
    CHECK(weva_char_script('a') == tag("Latn"));
    CHECK(weva_char_script(0x05D0) == tag("Hebr"));
    CHECK(weva_char_script(0x0628) == tag("Arab"));
    CHECK(weva_char_script(0x0416) == tag("Cyrl"));
    CHECK(weva_char_script('1') == tag("Zyyy"));
    CHECK(weva_char_script(' ') == tag("Zyyy"));
    CHECK(weva_char_script(0x0300) == tag("Zinh"));
    CHECK(weva_char_script(0x064E) == tag("Zinh"));   // FATHA is inherited, not Arab
    CHECK(weva_char_script(0x10FFFF) == tag("Zzzz"));

    // Indic categories (ABI minor 42), from IndicSyllabicCategory.txt and
    // IndicPositionalCategory.txt: what a syllable is made of and where a
    // matra sits.
    int32_t position = -1;
    CHECK(weva_char_indic_category(0x0915, &position) == 1 && position == 0);   // KA: consonant
    CHECK(weva_char_indic_category(0x0930, nullptr) == 1);                      // RA
    CHECK(weva_char_indic_category(0x0905, &position) == 2);                    // A: vowel independent
    CHECK(weva_char_indic_category(0x093F, &position) == 3 && position == 1);   // I matra: dependent, LEFT
    CHECK(weva_char_indic_category(0x093E, &position) == 3 && position == 2);   // AA matra: right
    CHECK(weva_char_indic_category(0x0940, &position) == 3 && position == 2);   // II matra: right
    CHECK(weva_char_indic_category(0x0941, &position) == 3 && position == 4);   // U matra: bottom
    CHECK(weva_char_indic_category(0x0947, &position) == 3 && position == 3);   // E matra: top
    CHECK(weva_char_indic_category(0x094B, &position) == 3 && position != 1);   // O matra: dependent, not left
    CHECK(weva_char_indic_category(0x094D, &position) == 5 && position == 4);   // virama: bottom
    CHECK(weva_char_indic_category(0x093C, &position) == 4 && position == 4);   // nukta
    CHECK(weva_char_indic_category(0x0902, &position) == 6 && position == 3);   // anusvara: bindu, top
    CHECK(weva_char_indic_category(0x0903, &position) == 7 && position == 2);   // visarga: right
    CHECK(weva_char_indic_category(0x200D, nullptr) == 12);                     // ZWJ: joiner
    CHECK(weva_char_indic_category(0x200C, nullptr) == 13);                     // ZWNJ: non-joiner
    CHECK(weva_char_indic_category(0x09BF, &position) == 3 && position == 1);   // Bengali I matra: left
    CHECK(weva_char_indic_category(0x0BC6, &position) == 3 && position == 1);   // Tamil E matra: left
    CHECK(weva_char_indic_category('a', &position) == 0 && position == 0);
    CHECK(weva_char_indic_category(0x0628, nullptr) == 0);                      // Arabic BEH: not Indic

    // Canonical decomposition, one level.
    uint32_t parts[4] = {};
    CHECK(weva_char_decompose(0x0BCA, parts, 4) == 2 && parts[0] == 0x0BC6 && parts[1] == 0x0BBE);   // Tamil O = E + AA
    CHECK(weva_char_decompose(0x09CB, parts, 4) == 2 && parts[0] == 0x09C7 && parts[1] == 0x09BE);   // Bengali O
    CHECK(weva_char_decompose(0x0D4C, parts, 4) == 2 && parts[0] == 0x0D46 && parts[1] == 0x0D57);   // Malayalam AU
    CHECK(weva_char_decompose(0x00E9, parts, 4) == 2 && parts[0] == 'e' && parts[1] == 0x0301);      // é
    CHECK(weva_char_decompose(0x1E09, parts, 4) == 2 && parts[0] == 0x00E7 && parts[1] == 0x0301);   // one level: ḉ = ç + acute
    CHECK(weva_char_decompose('a', parts, 4) == 0);
    CHECK(weva_char_decompose(0x0915, parts, 4) == 0);                                              // KA
    CHECK(weva_char_decompose(0x00BD, parts, 4) == 0);                                              // ½: compatibility only
    CHECK(weva_char_decompose(0x0BCA, nullptr, 0) == 2);                                            // sizing call
}
