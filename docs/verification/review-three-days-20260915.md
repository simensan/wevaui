# Three-day review audit — September 15, 2026

The source review and full-gate verification are finished. `check.sh --release`
exits 0 with no skipped gates using the qualified Godot builds, canonical Windows
Chrome reference and private display described below. This closes the review's
verification requirements; consumer and platform release qualification remains
separate. The 31 original review commits through `e274350e` are followed by the
audit and two verification fixes (`11c6d2fc`, `c635df2b`). Nothing was pushed,
and no user Unity scene was edited.

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
its pending scope findings were subsequently fixed and verified. The
[verification-fix receipt](review-gate-fixes-20260915.json) records the complete
passing run, runtime and patch hashes, regression controls and configuration.

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
| Samples, oracle coverage and documentation | Reviewed. Replaced the obsolete Sprite demo with CSS border-image, preserved collector assets, repaired Unity oracle evidence handling and routed legacy commands through Chrome. All 47 sample assets load; 54 release/oracle tooling tests pass on Linux. |
| Final full gate and host verification | **Passed with the qualified configuration.** The complete full gate exits 0 with no skipped steps. Native/exported Unicode, canonical Windows Chrome, renderer comparisons and host scenes pass in that run. |

## Major improvements

- **Shared engine:** corrected CSS import origins and sheet boundaries, `@scope`
  matching/cache behavior, CSS nesting, color parsing, multicolumn layout and
  reload ordering. Unity and Godot consume the same fixes through the C ABI.
- **Unity host:** repaired binding/event lifetime failures and editor asset
  refresh; nested imported CSS survives scene/prefab baking. Font caching removes
  the large repeated kerning allocation observed during changing text.
- **Development workflow:** restored reliable Chrome and Unity oracle commands,
  rejected stale/failed test evidence, made missing sample assets and benchmark
  failures fail visibly, and preserved failed-run logs. The WSL gate now runs
  the canonical Windows browser checks directly. A Godot editor shutdown patch
  prevents queued help callbacks from accessing destroyed documentation state;
  four stale-cache import aborts become 12 passing imports plus the cache seed.

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
| Godot | 6/6 native Unicode cases; 6/6 in each debug/release export; 19,833 font checks; 47 backend samples, 12 interactive states and host scenes pass. Three addon export modes match project pixels |
| Release/oracle tooling | Linux: 54 pass. Windows: 45 pass, 9 Linux-only skips |
| Other tooling | 9 text-safety runner checks and 32 Frontier runner checks pass |
| ABI | 0.44; generated binding current at 142 imports, 15 structs and 10 enums |

The latest full-gate run includes the final scope, nesting and tooling fixes.
Its log ends with `all gates pass`; the archived exit code is 0. Unity's editor
and PlayMode results above remain applicable to the unchanged Unity/core source;
they are separate from the shell gate. Original evidence remains under
`.utmp/review-20260915`; follow-up builds, failed controls, renders and the passing
run are under `.utmp/review-fixes-20260915/full-gate-isolated` and its parent.

## Qualified configuration and resolved requirements

1. **Native-control Unicode:** a normal Linux x64 Godot editor built from
   `ed1daf0bf001b61586d9930840f2f1394092c079` with the script-iterator and
   editor-help shutdown patches passes all six isolated cases. A native Label
   render with 65 alternating emoji runs was inspected. Default-font coverage
   is not qualified; Devanagari fallback boxes remain visible.
2. **Exported Unicode:** matching debug and release templates pass all twelve
   relocated cases with embedded ICU data. Normal 3D/rendering features and
   template path restrictions are retained. Addon debug, release and embedded
   release exports also pass startup/relocation and match the editor's pixels.
3. **Numeric editing:** the new WSL bridge runs the same pinned Windows Chrome
   152.0.7977.82 reference as CI. All 580 numeric-editing cases and the complete
   67-script suite run unchanged; child process failures propagate. The existing
   documented IME failure remains the sole known browser failure.

Use the qualified editor and both templates together. Stock Godot still fails
five of six Unicode cases, and Linux Chrome still handles decimal commas
differently. Neither is silently accepted as the qualified configuration.
The [build recipe](../../Tools/godot-text-shaping-repro/README.md) applies both
checked-in patches; the receipt records exact source, patch and binary hashes.
Initial upstream Godot builds emitted one GDScript `-Wdangling-pointer` warning;
this is separate from the zero-warning Weva builds.

The first follow-up full run on the shared desktop failed one pressed-button
capture (1.06% color difference). Its image lost pressed/hover appearance;
desktop focus interference is the inferred cause. The unchanged private-display
run passes all 12 interactive states at 0.00% over tolerance. Both runs and the
inspected images are preserved; no threshold or input behavior was changed.

For this checkout, the passing run is reproduced from WSL with the installed
Node/Puppeteer dependencies and these paths:

```sh
export PATH="/root/weva/review-tools/node-v22.23.2-linux-x64/bin:$PATH"
export GODOT_BIN=/root/weva/godot-scriptfix-review/bin/godot.linuxbsd.editor.x86_64
export WEVA_GODOT_DEBUG_TEMPLATE=/root/weva/godot-scriptfix-review/bin/godot.linuxbsd.template_debug.x86_64
export WEVA_GODOT_RELEASE_TEMPLATE=/root/weva/godot-scriptfix-review/bin/godot.linuxbsd.template_release.x86_64
export WEVA_BUILD_UNITY=/root/weva/build-unity-review
export WEVA_CHROME=/root/weva/review-tools/puppeteer/chrome/linux-152.0.7977.82/chrome-linux64/chrome
export WEVA_CHROME_NO_SANDBOX=1
export WEVA_CHROME_WINDOWS_PYTHON=/mnt/c/Users/simen/miniconda3/python.exe
export WEVA_CHROME_WINDOWS=C:/Users/simen/Documents/GitHub/unityui/.utmp/review-20260914/ci-chrome/pinned-browser/chrome/win64-152.0.7977.82/chrome-win64/chrome.exe
export WEVA_CHROME_CHECKS_OUT="$PWD/.utmp/chrome-checks"
export TMPDIR=/tmp
WEVA_EXPORT_RENDER=1 LIBGL_ALWAYS_SOFTWARE=1 \
  xvfb-run -a -s '-screen 0 1920x1080x24' bash check.sh --release
```

Chrome's temporary profiles stay on the native Linux filesystem; Windows
reference receipts stay on the shared Windows drive. Xvfb gives render and
interactive checks a private display while retaining the real Godot rasterizer.

Documented web-subset and shaping limits, minor soft-hyphen/snap details,
investigation-only decoder/probe cleanup and an inactive legacy renderer warning
are deferred under the requested preference for major usable improvements.
Consumer-project migration, additional player platforms and physical input/IME
testing remain product-readiness follow-ups, not claims of this review.
