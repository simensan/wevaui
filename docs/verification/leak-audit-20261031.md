# Memory-leak audit — 2026-10-31

Audited baseline: `d3198a0b`. This pass records two confirmed Unity retention
findings. It makes no runtime changes and does not claim all hosts are leak-free.

## Findings

### P2: rejected font payloads remain in a domain-lifetime cache

`UnityFontBackend.SharedFontBytes` clones and inserts data into `s_fontBytes`
(lines 55–67), and `Adopt(byte[])` calls it before the font is validated.
`AdoptCssUrl` removes a rejected face from `_faces`, but never removes its
buffer from the static cache. Backend disposal also leaves that cache intact.
Repeated distinct invalid font content therefore consumes memory for the Unity
domain's lifetime despite successful disposal of all associated backends.

Measured in Unity 6000.4.1f1: 24 distinct 256 KiB invalid buffers, 24 rejected
metric loads, 24 disposed and garbage-collected backends, yet **24 cache entries
and 6,291,456 retained payload bytes**. Reflection counted the actual cache
payload; this is not an inference from process RSS. The probe exercised byte
adoption and validation directly. The CSS failure path reaches the same cache.

Next fix: only publish successfully validated font content to the canonical
buffer cache. Preserve stable buffer identity for accepted fonts: that existing
policy avoids repeated native FontEngine table allocations. Accepted unique
fonts also have no cache budget; changing that requires checking native ownership
rather than simply evicting buffers and recreating the earlier native growth.

### P2: changing CSS font content accumulates historical face records

`AdoptCssUrl` only compares against the URL's latest face. If bytes differ, it
calls `Adopt` (line 599), which registers a new `_faces` entry (line 858), then
overwrites the URL mapping (line 605). Earlier records are not reused or retired.
Alternating the same two valid bundled font files at `changing.ttf` through
this helper **200 times leaves 200 `_faces` entries and one URL mapping**.
`Dispose` reduces the face count to zero. This is unbounded document-lifetime
retention for repeated content changes, not a post-disposal leak. The probe
called the real CSS URL loader directly; it did not run 200 full rendered scenes.

Next fix: reuse faces by accepted content identity across URL changes and audit
retirement of no-longer-referenced face/variant records. Do not delete faces
still referenced by active core font metrics, fallbacks or variants.

## Checks that passed

- All **20 Clang sanitizer suites** pass with explicit
  `ASAN_OPTIONS=detect_leaks=1:halt_on_error=1` and `LSAN_OPTIONS=exitcode=23`.
- An independent intentional 12,345-byte allocation leak produces the expected
  LeakSanitizer report and exit 23. Detection is active. The first shell wrapper
  mishandled exit-code capture; its report is preserved, and a Python-driven
  repeat verifies the expected exit explicitly.
- A core-only lifecycle probe performs one warm lifetime plus **1,000 additional
  lifetimes**. Each creates a document, loads styled text with rounded backgrounds
  and shadows, replaces a subtree, reconciles markup, resets the renderer and
  font backend, updates, and destroys the document. Tracked live C++ storage
  after destruction is **71,808 bytes at every 100-cycle sample**, with 450 live
  allocations. The shared raster cache is explicitly disabled to isolate owned
  document allocations. RSS settles at 7,816 KiB from cycle 100 through 1,000;
  RSS is supplementary, not the leak assertion.
- Unity diagnostic invocations exit successfully; their temporary editor source
  and metadata are removed afterward. No runtime source or user scene changes.

## Ownership review and limits

The core destroys cached textures, atlas textures and transient textures before
deleting the document. Unity's ordinary component release disposes input,
bindings, renderer, font backend and document. Its asset-reader GC handle is
weak and freed on document disposal/finalization. Font-backend callback handles
require explicit disposal; the owning component supplies it. Renderer disposal
releases meshes, materials and textures (Unity schedules destruction in play mode).

Godot document destruction disconnects font signals, destroys the core and frees
render layers/batches/shaders. Its font backend frees owned TextServer RIDs and
clears per-backend maps; the shared variant cache has eight entries and releases
at extension termination. These are ownership-review findings, not a fresh
long-running Godot leak measurement.

The inspected Agentic engine-9 host drops its native document and replaces its
GPU texture map with the published live set. This was source review only;
that checkout is separate from the audited core commit and is not the immutable
SDK used by the old soak. No changes were made to it.

LeakSanitizer detects unreachable native allocations, not reachable unbounded
caches. The lifecycle counter covers C++ `new`, not every host/direct-`malloc`
allocation or GPU resource. This audit does **not** attribute or close the
previous **13.7 MiB Agentic process-private growth**. A matched no-reload control
and allocation traces on the same SDK remain required. Fixed-content core
plateaus cannot substitute for those host measurements.

## Evidence

The adjacent JSON receipt contains measured results, audited source hashes and
hashes of the preserved probes/logs under `.utmp/leak-audit-20261031/`.
`WevaLeakAudit20261031.cs` reproduces the Unity probes via reflection;
`run-unity.py` installs it temporarily and removes it in `finally`.
`lifecycle.cpp` and `lsan-positive.cpp` reproduce the native probes.
The previous full release qualification remains the lazy-atlas receipt; this
report is an additional audit, not a new full host-release qualification.

Follow-up: both confirmed Unity findings are addressed by the
[font-retention fix](font-retention-20261031.md), with the original probes rerun.
