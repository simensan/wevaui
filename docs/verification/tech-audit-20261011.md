# Technical audit and cleanup — October 11, 2026

Audit baseline: `52bb1f68`. Fix commits: `1f778d99` (Unity ownership) and
`1e21fce7` (tooling/build cleanup). Scope: the current shared C++ core, C ABI, Unity and
Godot hosts, build/package tooling, browser reference tools, dependencies and
readiness claims. This is a targeted source review backed by the repository's
full gate, not a claim of exhaustive proof for every source line or device.

## Confirmed findings and fixes

| Priority | Finding | Resolution and regression evidence |
|---|---|---|
| High | Unity binding adapters retained callbacks on an old document after moving. Disposing a replaced adapter disconnected the replacement, and refreshing after document destruction threw. | Document-owned binding registration detaches the old owner before replacement and frees callback roots at teardown. All three baseline regressions failed and now pass. A fourth test verifies failed installation on a disposed document preserves the live binding. |
| Medium | Unity asset-reader assignment rooted callbacks before checking whether the document was disposed. | Validate the native handle before changing managed state or allocating a callback root. Regression checks rejected assignment preserves the previous reader. |
| Medium | Unity renderer cached draw serials and texture IDs without document identity. A renderer reused across documents showed the previous document's pixels. | Scope cached resources to the document; replace them on document changes. A red/blue/red pixel regression fails before the fix and passes afterward. Both final color images were inspected. Same-document idle synchronization remains cached. |
| Medium | The documented corpus harvester had invalid Python string literals and a literal backspace instead of the regex word boundary for void elements. | Repair newline literals and the boundary; write UTF-8 explicitly. A CLI regression checks void-element HTML, non-ASCII content and reused CSS. Replace obsolete migration-era tool description with its actual behavior. |
| High | CI and documentation require `npm ci`, but both root npm manifests were ignored and absent from Git. A fresh checkout could not install the promised locked browser tooling. | Track the private root manifest and lockfile; retain ignores for installed dependencies and scratch manifests. Verify a clean isolated install from those two files. |
| Medium | npm audit reported 11 high-severity vulnerable dependency entries in the browser-tooling tree. | Pin Puppeteer 25.13.0 as a development dependency, regenerate the lockfile, and require Node >=22.12.0. npm audit now reports zero advisories. This does not audit vendored C++ dependencies or certify the fixed historical Chrome reference as secure for general browsing. |
| Medium | Chrome autoscroll correctness check imposed a five-second full-selection deadline that expired while selection was still advancing. | Diagnostic: selection reached offset 404 of 500 and scrolled 3,050px across a 3,956px line. Increase only the bounded eventual-selection wait to 15 seconds; preserve all 35 selection/event assertions. The isolated corrected check passes. This is not a relaxed runtime performance budget. |
| Low | Duplicate `bidi.cpp` CMake source entry and obsolete npm package scaffolding. | Remove the duplicate; both compiler builds reconfigure without rebuilding objects. Mark tooling private, align license with the repository, remove the nonexistent main entry point, and replace the intentionally failing npm test stub with the browser suite. |

## Verification

- GCC: 14/14 suites pass; Clang ASan/UBSan: 16/16 suites pass, including
  positive controls. The CMake cleanup reconfigures both without compiler warnings.
- Unity full EditMode: **243 passed, 0 failed, 2 inconclusive** (245 total).
  The two inconclusive tests require explicit layout/parity manifests and are
  the same environment-gated checks as before this audit.
- Unity PlayMode: **5/5** URP rendering checks pass.
- Tooling: **55/55 on Linux**; Windows 46 pass with 9 Linux-only skips.
- Static syntax check: 66 tracked first-party Python files, 65 CJS and 8 MJS
  scripts parse after the repair; the new Python regression also runs directly.
- The first full gate completed with only the Chrome autoscroll deadline failure.
  Its logs and the isolated failure/diagnostic are preserved. The corrected
  `check.sh --release` exits **0**, ending with **all gates pass**, with no skipped
  gates. Chrome behavior: 66 passed, 0 unexpected failures, 1 existing documented
  IME exception across 67 scripts. All 334 tracked layout captures pass the
  unchanged 1.5px ceiling. Host build/load, generated ABI, Unicode/export,
  backend/interactive renderer and Godot scene gates pass.

[Machine-readable receipt](tech-audit-20261011.json). The qualified configuration
is unchanged from the [September review](review-three-days-20260915.md), except
for the pinned Puppeteer driver; Chrome remains 152.0.7977.82.

Artifacts: `.utmp/audit-20261011/`. Before-fix XML, failed browser logs, diagnostic
selection state, after-fix XML and rendered pixels are preserved. No user scenes
were edited. No golden baselines or browser expectations were regenerated.

## Coverage and remaining product work

Reviewed core invalidation/lifetime and ABI boundaries, managed callback ownership,
render resource identity, Godot lifecycle/input/font integration, asset readers,
archive extraction guards, source fingerprints, generated imports, CI platform
build definitions and documentation against the current architecture. Functional
coverage comes from core/sanitizer tests, tracked Chrome geometry, live browser
behavior, host scenes, font/Unicode probes and rendering/export gates.

This audit does not establish complete CSS/browser parity or production readiness.
Keep the existing adoption limits: real Unity player asset/font loading and
consumer migration, lower-end and additional target hardware, physical IME/touch/
gamepad acceptance, accessibility, advanced shaping/writing modes, and full-game
performance. No new runtime performance claims are made from these correctness
checks. The historical three-object soak observation is not reclassified as a
proven leak or fixed by this audit.
