# Documentation audit — September 16, 2026

Reviewed navigation, audience, current claims and local references across the
83 tracked Markdown documents present at the start of the audit. Current setup
and support guidance was cross-checked against package manifests, host/core
source and the September 15 verification reports. Historical logs were checked
for clear scope and navigation; their old measurements were not requalified.

## Changes

- Added a [documentation index](../README.md) with separate routes for users,
  engine developers and historical evidence.
- Shortened the root README from 1,091 to 471 words, Unity package README from
  1,895 to 434, and Godot entry guide from 10,070 to 311. Detailed Godot material
  remains in the [host reference](../../hosts/godot/REFERENCE.md).
- Separated current performance/export/release guidance from older build logs.
  The forms guide now opens with a working settings recipe; its long reference
  and checkpoint history can be expanded when needed.
- Corrected retired Unity APIs, controller registration, player/prefab baking,
  hot reload coverage, Sprite-image instructions and backdrop-pass timing.
- Corrected CSS claims: important-layer ordering, container-query settlement,
  partial `@property` validation and native animation interpolation/easing limits.
  These were documentation corrections; no engine behavior was changed.
- Corrected Godot `local()` font loading and removed obsolete “current installed”
  versions from setup guidance. Export guidance identifies matching patched
  editor/templates separately from earlier stock-engine sample passes.
- Replaced C#-oracle instructions in current engineering guidance. Historical
  conformance/port records remain clearly labeled.

## Validation and scope

Repository-local Markdown links, heading anchors and path casing pass, including
the addon README's links resolved against the packager's documented destinations.
Current-guide repository source paths resolve. Moved history sections retain
their original results. `git diff --check` passes.

Existing verification receipts, license texts, runtime code and scenes are
unchanged. Generated addon copies were not edited or regenerated. No engine
build, runtime test or new performance measurement was needed for this
documentation-only change. External websites and packaged-archive link rendering
were not revalidated.
