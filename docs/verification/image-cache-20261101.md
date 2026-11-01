# Decoded-image cache retention — 2026-11-01

The shared core now targets **64 MiB of charged decoded-image storage and
1,024 entries per document**, evicting the least recently used unreferenced
sources. Current box references remain pinned even when their working set
exceeds that target. This closes the decoded-cache budget item from the
[technical debt audit](tech-debt-20261101.md#remaining-debt-in-priority-order).
It follows the separate [async queue and unload fixes](image-queue-20261101.md).

The same engine change reaches Unity and Godot; a locally rebuilt Agentic SDK
also contains it. No host-specific cache policy or C ABI change was added.
Base revision: `f987e1ea`; exact source, binary and evidence hashes are in the
[JSON receipt](image-cache-20261101.json). Local raw evidence is preserved under
`.utmp/image-cache-20261101/`. Older receipts remain unchanged.

## What changed

- Charges include RGBA and sampling-table vector capacity, URL storage and
  estimated entry metadata. Pending URL bookkeeping and failed-source history
  participate in the budget. Moves transfer accounting and leave the source
  reusable; clear releases it.
- Under pressure, a reachable-box sweep pins image sources, backgrounds, masks,
  border images and list markers, including hidden references. Trimming happens
  after layout and raster jobs finish borrowing pixels, including geometry-only
  updates. Published draw textures remain valid. Total update timing includes
  trimming. Under-budget updates avoid the sweep; settled idle updates do not
  repeatedly scan an above-target live set.
- Obsolete **unadmitted** async URLs are removed before later host reads.
  Already admitted jobs retain the existing four-job policy. Live failed URLs
  remain diagnosable without reopening them every frame; inactive failure
  history can be evicted.
- Each published decoded image has a unique resource version. Image,
  background/mask and border raster keys use their actual sources' versions.
  Returning to an evicted URL fetches its current pixels without reusing an old
  raster texture or invalidating unrelated sources. Painting and the reference
  sweep share the border-image URL parser.

## Retention measurement

The original C ABI regression swaps 640 distinct 256×256 image sources through
layout-only updates. A counter tracks requested **live C++ `new` storage**. It
excludes direct `malloc`, allocator spare pages, process RSS/private commit and
host/GPU storage. Both measurements use GCC 13.3 Release and the same original
image churn portion; additional candidate policy checks run afterward.

| Requested live C++ storage | Baseline | Candidate |
|---|---:|---:|
| Warm | 4,581,873 bytes | 4,582,001 bytes |
| After 640 swaps | 172,596,617 bytes | 70,052,377 bytes |
| Added | 168,014,744 bytes | 65,470,376 bytes |

That is **59.41% less total retained requested storage** in this probe, or
61.03% less added storage. It is not a whole-process memory or frame-time claim.
The baseline fails three checks: inactive storage exceeds the target, returning
to an old URL does not reread it, and the old pixels remain. The candidate passes.
Logs: `baseline-failed.log` and `candidate-policy-final.log`.

The final expanded suite adds live and hidden source consumers, stale raster
checks, LRU/move accounting, bounded failure history, deferred URL pruning and
sampling-table release. It passes **1,469 checks** on GCC, Clang ASan/UBSan and
MSVC. Its larger fixture settles at 5,418,901 bytes and retains 70,085,017 bytes
after churn on GCC/Clang. These are separate regression values, not the paired
percentage above. The suite also verifies that **128 settled HUD updates make
zero allocations** and that geometry-only churn preserves the published texture
pointer and draw serial until repaint.

## Verification

- Complete qualified `check.sh --release`: **20 GCC CTest suites**, 809,530 main
  checks; **22 Clang ASan/UBSan suites**, 809,524 main checks. The sanitizer
  configuration retains its six-check small-stack exclusion. Cache, queue and
  handle memory regressions pass 1,469, 150 and 8,352 checks respectively.
- All **341 Chrome layout captures** pass the 1.5 px ceiling. Behavior checks:
  69 pass, one existing known IME exception, zero unexpected failures. Linux
  tooling passes 90 checks. Godot font checks pass 19,833, with Unicode,
  backend, addon install, exports and scene gates green.
- Both Windows native hosts were rebuilt. Unity 6000.4.1f1 full EditMode:
  **259 pass, two environment-gated inconclusive, zero failures**. Editor exit
  2 is expected for this result. Mono development player: **7/7**. Real plugin
  load/decode/unload passes twice. All 16 player images match the earlier
  verified images after RGBA decoding. Metadata hashes confirm all three
  installed native libraries match the final runtime source inputs.
- MSVC standalone handle, queue and cache memory targets all pass. Runtime
  sources were unchanged after the Windows/SDK qualification; subsequent edits
  affected test instrumentation and the manual Godot pressure test.
- The opt-in real Godot pressure render passes **651 checks** after 320 source
  swaps exceeding 64 MiB decoded RGBA. The evicted URL reloads blue; the pinned
  source remains red. The before/after captures were inspected. This uses the
  qualified patched Godot 4.7.2, Vulkan Forward Mobile **llvmpipe**, dummy audio
  and private Xvfb, not a hardware GPU qualification.
- A coherent local optimized Agentic CLI/headless/runtime SDK builds against
  these exact core inputs. Its survival UI smoke passes five modes, selection,
  clicks, use, close, reloads, memory snapshots, cooldown and post-reload
  interaction. The endpoint records 133 requested loads over 20.07 seconds.
  The owned session is stopped and the clean engine-9 slot released. This
  default synchronous fixture is a compatibility smoke; it does not directly
  exercise async loading or cache pressure. Global SDK selection and remote
  native pins were unchanged.

The first full gate failed in the new allocation probe under ASan: the test's
header-based tracker lacked nothrow allocation forms, so `stable_sort` scratch
storage allocated by the sanitizer reached a custom delete expecting a header.
This was a test instrumentation defect. All three header-based memory probes
now share a matching ordinary/array/aligned/nothrow allocator. Focused sanitizer
checks and the complete final gate pass without disabling sanitizers. The first
gate's exit 1, original ASan report and final gate's separate exit 0 are retained.

## Reproduction and limits

The complete local gate uses the qualified patched Linux Godot editor and both
templates, pinned Windows Chrome 152.0.7977.82 through WSL, and private Xvfb.
Exact environment variables are retained in `final-gate/run-gate.sh`.
Run the standalone cache target through CTest:

```sh
ctest --test-dir <build> -R '^weva_image_cache_memory$' --output-on-failure
```

The manual rendered pressure test creates its fixture files and refuses an
existing output directory. Use a new absolute directory each run:

```sh
WEVA_IMAGE_CACHE_OUT=/absolute/new-output-directory \
  xvfb-run -a <qualified-godot> --audio-driver Dummy \
  --path hosts/godot/project --script res://image_cache_budget_tests.gd
```

The 64 MiB and entry limits are retention **targets with a live-working-set
exception**, not hard process limits. Dirty updates above the target may repeat
the reference sweep. Individual encoded inputs, decode peaks, admitted worker
payloads, raster caches and host/GPU allocations remain outside this cache
policy. A single large source can exceed the target while in use. No new FPS,
long-soak, IL2CPP/release-stripping or macOS claim is made. The older Agentic
process growth still has no assigned allocation owner; the prior
[matched lifecycle controls](agentic-memory-20261101.md) retain their own scope.
