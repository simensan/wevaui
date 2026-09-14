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
}
