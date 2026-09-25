# Ada URL parser

Unmodified release amalgamation from [Ada v4.0.0](https://github.com/ada-url/ada/releases/tag/v4.0.0),
revision `b12a893a45809da8103bb4f1e2f6f5ee13f9100b`.
Dual licensed under [MIT](LICENSE-MIT) or [Apache 2.0](LICENSE-APACHE).

The core uses `ada::can_parse` without a base URL for HTML URL-input validity.
URLPattern support is disabled (`ADA_INCLUDE_URL_PATTERN=0`). No networking is
performed. URL validation runs during form validation, not on idle UI frames.

Ada and the private URL wrapper compile as C++20; public core consumers remain
C++17. Sanitizer and platform flags apply to the parser object target too.
