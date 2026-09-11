# Decimal arithmetic

Adapted from Chromium Blink WTF Decimal at revision
`9596b537e435c5c23d247cb7d9925d81e8f7707e`.

- [Upstream source](https://chromium.googlesource.com/chromium/src/+/9596b537e435c5c23d247cb7d9925d81e8f7707e/third_party/blink/renderer/platform/wtf/decimal.cc)
- [License](LICENSE.txt)

Local adaptations: namespace isolation; standard C++ string_view, assertions and
abort; removed Blink allocator/export annotations and unused double/output
conversions. `ToString` uses standard strings with Blink's decimal rounding and
notation rules. Decimal parsing and arithmetic retain upstream behavior. This code
is used for HTML number-input constraint validation and keyboard stepping; it is
not on idle UI paths.
