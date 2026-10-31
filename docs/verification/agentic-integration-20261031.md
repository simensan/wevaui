# Agentic main integration — 2026-10-31

The rendering audit branch is integrated into **local Agentic main** at
`c2288c71cd360ea558457763f036f201b9e4a15f`. Current main was merged into the
leased audit worktree without conflicts, validated, then the clean main
checkout was fast-forwarded. Nothing was pushed or globally retargeted.

Verification on the combined source:

- Eleven verifier targets pass across `agentic_weva`, `agentic_game`,
  `agentic_game_support`, `agentic_script_host` and `agentic_scripting`, including
  rendering, AOT, script integration/native tests and SDK contract tests.
  The production game target also compiles and doctests pass.
- The editor Weva queue's three tests pass with the optional `weva` feature.
  The ordinary verifier omits that feature, so its filter matched no tests;
  this failed discovery attempt is preserved. The explicit Cargo feature run
  supplies the missing coverage. Existing API-feedback key:
  `verifier-optional-feature-selection`.
- An optimized immutable CLI/headless/runtime SDK builds successfully:
  `29682ea83412ab5fc9b170f4c590280d153e192657beec184872acd0f64482c8`.
- That SDK passes five runtime UI mode checks, item selection, use, close,
  **112 reloads** and post-reload selection in the 240-cube survival fixture.
  No timing profiles are collected during concurrent builds/lifecycle work.
- The selected-inventory capture was inspected. PNG SHA-256:
  `df5d0ec8b0509c51588985f324465baef9302c3dd4bddc98626bbc2cf242c17e`.
  The owned smoke session reports successful shutdown.

The local SDK was built with explicit `WEVA_SOURCE_DIR` pointing at this Weva
checkout, whose shared core still contains the accepted flex optimization.
Agentic's **default prebuilt native pin remains `2c27fe25`**. Updating that
published cross-platform SDK is a separate release operation: the local SDK
above contains the current core, but a default Agentic build does not thereby
acquire the newest core. No macOS qualification or remote publication is claimed.

Evidence: `.utmp/render-audit-20261030/agentic-merge-tests.log`,
`agentic-merge-editor-weva.log`, `merged-sdk.log`, `merged-runtime-smoke/`, and
Agentic `builds/verify/20261031-080507/report.json`. The initial unknown-package
plan and default-feature/no-matching-test attempt remain preserved separately.
