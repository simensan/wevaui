# Three-day review audit — September 15, 2026

The source review and its major fixes are finished. Full-gate verification
remains blocked by the three failures below; this is not a clean-gate or release
sign-off. There are 31 review commits through `e274350e`, followed by this audit.
Nothing was pushed, and no user Unity scene was edited.

The original window is September 11–14, ending at `3e6f145d`: 135 commits.
The comparison base is `e6903caa`. Its ancestry range contains 136 commits because
the merge also brought in September 1 setup commit `4a679790`; those resulting
changes were included. Directory flattening was normalized before reviewing
the 281 surviving source/tool paths. Deleted C# code, tests and captures were
assessed as a migration, alongside the replacement native implementation,
documentation, fixtures and build metadata.

The [machine-readable record](review-three-days-20260915.json) lists the source
inventory, review commits, coverage basis, verification counts and evidence paths.
The [earlier full-gate receipt](review-full-gate-20260915.json) is historical:
its pending scope findings were subsequently fixed and verified.

## Requirement audit

| Original review area | Result and supporting evidence |
|---|---|
| Repository flattening, C# removal and release tooling | Reviewed. Repaired checkout fingerprints, static plugin contents, iOS imports and surviving tool paths. Both host builds/load checks and tooling regressions pass. |
| Core CSS, cascade, components, imports, colors and painting | Reviewed. Fixed stylesheet ordering/origins, import parsing, color grammar, scope containment/proximity/invalidation and nested selectors/declaration order. New Chrome regressions and full core/layout gates pass. |
| Core layout, sizing, sticky, snap, multicolumn and writing modes | Reviewed. Fixed direct inline and inline-block multicolumn content and clipping versus scroll-container behavior. Both compiler suites and 334 Chrome layout captures pass. |
| ABI lifetime, diagnostics, inspector and reload | Reviewed. Fixed keyed sibling order during reload; ABI additions are documented and regenerated at 0.44. Both hosts build, and affected core/host tests pass. |
| Unity lifecycle, API, bindings, stylesheets and editor | Reviewed. Fixed typed collection write-back, reentrant event dispatch, asset dependency reload, nested-import baking and inspector reconnection. Full EditMode passes 237 with 2 environment-gated inconclusive. |
| Unity/Godot input parity and events | Reviewed. Fixed astral text handling and stale input frames; core-owned rules remain shared. Input regressions, native suites and affected Godot scenes pass. |
| Unity/Godot fonts and Unity shaping | Reviewed. Fixed installed-font path/identity handling, shared font buffers and complete kerning-table caching. Font tests, Godot's 19,833 adapter checks and inspected text renders support the changes. |
| Unity URP/offscreen and Godot rendering | Reviewed. Five PlayMode pixel checks pass; 47 backend samples and 12 interactive states pass the structural gate. Generated 47 Unity/Chrome visual pairs and inspected changed renders. These pairs are not a pixel-conformance claim. |
| Samples, oracle coverage and documentation | Reviewed. Replaced the obsolete Sprite demo with CSS border-image, preserved collector assets, repaired Unity oracle evidence handling and routed legacy commands through Chrome. All 47 sample assets load; 50 release/oracle tooling tests pass on Linux. |
| Final full gate and host verification | **Blocked.** All mandatory steps ran, but the full gate exits 1 on the three failures below. Later affected suites and the final canonical Windows browser run pass as detailed here. |

## Major improvements

- **Shared engine:** corrected CSS import origins and sheet boundaries, `@scope`
  matching/cache behavior, CSS nesting, color parsing, multicolumn layout and
  reload ordering. Unity and Godot consume the same fixes through the C ABI.
- **Unity host:** repaired binding/event lifetime failures and editor asset
  refresh; nested imported CSS survives scene/prefab baking. Font caching removes
  the large repeated kerning allocation observed during changing text.
- **Development workflow:** restored reliable Chrome and Unity oracle commands,
  rejected stale/failed test evidence, made missing sample assets and benchmark
  failures fail visibly, and preserved failed-run logs.

## Verification

| Check | Latest result |
|---|---|
| Core | 507,328 checks, 0 failures; GCC 14/14 suites, sanitizer 16/16; 0 compiler warnings |
| Chrome layout | 334 captures below the 1.5px ceiling; 0 excuses |
| Canonical Windows Chrome 152 behavior | 67 scripts: 66 pass, 0 unexpected failures, 1 existing documented IME failure |
| Unity full EditMode | 239 total: 237 pass, 0 failures, 2 environment-gated inconclusive |
| Unity Native EditMode | 236 pass, 0 failures, 2 environment-gated inconclusive |
| Unity PlayMode rendering | 5 pixel checks pass |
| Unity/standalone-core layout | 47 agree, 0 differ, 0 missing, using identical synthetic metrics |
| Godot | 19,833 font-adapter checks; 47 backend samples and 12 interactive states within the structural gate; host scenes and addon exports pass |
| Release/oracle tooling | Linux: 50 pass. Windows: 41 pass, 9 Linux-only skips |
| Other tooling | 9 text-safety runner checks and 32 Frontier runner checks pass |
| ABI | 0.44; generated binding current at 142 imports, 15 structs and 10 enums |

The full-gate run predates the final scope/nesting and tooling fixes. Each later
chunk ran its affected suites; the JSON record identifies those follow-ups.
No full-gate success is inferred by combining partial runs. Local logs, XML,
images and failed evidence remain under `.utmp/review-20260915`.

## Unresolved verification requirements

1. **Stock Godot 4.7.2 native-control Unicode safety:** 5 of 6 isolated probes
   fail independently of the Weva addon. A runtime that passes these probes is
   needed before this gate can close.
2. **Stock Godot exported Unicode safety:** 5 of 6 probes fail in both debug and
   release exports. The export runtime/templates must also pass independently.
3. **Linux Chrome numeric editing:** decimal-comma input differs from the pinned
   Windows Chrome reference. The canonical Windows run passes. The Linux
   discrepancy remains reported without weakening engine behavior or waiving it.

These failures persisted through the review; the source changes do not resolve
the external runtime/reference conditions. The overall goal is not marked
complete while its full-gate requirement remains unmet.

Documented web-subset and shaping limits, minor soft-hyphen/snap details,
investigation-only decoder/probe cleanup and an inactive legacy renderer warning
are deferred under the requested preference for major usable improvements.
Consumer-project migration, additional player platforms and physical input/IME
testing remain product-readiness follow-ups, not claims of this review.
