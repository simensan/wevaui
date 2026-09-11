# Embedded ICU for select typeahead

The core uses ICU 78.3's Unicode collation search at primary strength with the
fixed `en` locale. This provides case/accent-insensitive prefixes, canonical
equivalence and complete collation expansions. It is the same search mechanism
used by [Chromium's string search](https://github.com/chromium/chromium/blob/main/base/i18n/string_search.cc).
The fixed profile is independent of the machine's locale and Godot's ICU.
Per-document language tailoring is not implemented.

CMake downloads the official
[ICU 78.3 source archive](https://github.com/unicode-org/icu/releases/tag/release-78.3),
pinned by SHA-256 `3a2e7a47604ba702f345878308e6fefeca612ee895cf4a5f222e7955fabfe0c0`.
Offline builds can set `FETCHCONTENT_SOURCE_DIR_WEVA_ICU_SOURCE` to the extracted
`icu` directory containing `source/`. Python 3.9 or newer generates the data
translation unit. Source compilation adds time to the first build.

`filter_data.py` verifies the original data hash and repackages 14 unmodified
resources for root/English collation, character boundaries and emoji character
properties: 1,293,232 bytes. The Godot host reads the emoji properties to keep
the stock engine's script iterator within its fixed stacks (see GODOT_TEXT_SHAPING.md).
The libraries and data are linked statically; installed addons need no ICU
DLL or data file. Symbols use the `_weva` library suffix to avoid collisions
with the host. Conversion, formatting, regular expressions, transliteration,
file I/O and dynamic loading are disabled. The embedded data currently requires
a little-endian target, including the shipped Windows/Linux x86_64 builds.

The upstream [license and third-party notices](LICENSE.txt) accompany the
source and packaged addon as `ICU_LICENSE.txt`. Upgrade the source pin, data
hash, resource names and Unicode/browser regressions together.
