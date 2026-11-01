# Parser symbol lifetime — 2026-11-01

Markup replacement no longer retains obsolete HTML parser symbols for the
document's lifetime. Full load, reconciled reload and fragment insertion now
use a parse-local symbol table. The returned DOM already owns its tag names,
attribute names and values; it does not need the tokenizer's numeric identifiers.
The explicit caller-owned symbol-table overload remains available to C++ tools.
No C ABI or host-specific policy changes.

Base revision: `791ac12a`. The [JSON receipt](parser-memory-20261101.json) records
exact source and binary identities. Raw evidence is retained locally under
`.utmp/parser-memory-20261101/`. The previous
[decoded-image budget](image-cache-20261101.md) and
[async image ownership](image-queue-20261101.md) receipts remain unchanged.

## Reproduction and fix

The new standalone `weva_parser_memory` test replaces markup 512 times through
each of three real C ABI paths. Each replacement has 32 distinct long attribute
names and one custom tag name. The live DOM stays small while source names
continually change. Measurements compare live requested C++ `new` storage after
64 warm replacements with storage after all 512 replacements.

| Replacement path | Baseline added storage | Candidate added storage |
|---|---:|---:|
| Full load | 2,328,773 bytes | -15,991 bytes |
| Reconciled reload | 2,328,773 bytes | -15,991 bytes |
| Fragment replacement | 2,357,445 bytes | 12,681 bytes |

These are paired GCC 13.3 Release runs of the same fixture. Negative values mean
storage finishes below the warmed measurement. The test log prints zero for
negative growth; the signed differences above are computed from its endpoints.
This was reachable but obsolete cache retention until document destruction,
not a demonstrated unreachable allocation leak. Normal reuse of a fixed set of
tag and attribute names did not trigger this growth.

All **9,740 checks** now pass, including the three memory-budget checks that fail
on the baseline. Every replacement verifies that custom tags and long attribute
names/values survive parse completion. Reconciled keyed handles remain stable;
replaced child handles are not recycled, and fragment owners remain valid.
The same target passes Clang ASan/UBSan and MSVC. The small fragment endpoint
change remains within the 64 KiB allowance for other retained document storage.

The ownership fix is in the shared core: a scoped parser overload owns the
table, and the ABI document no longer contains a lifetime-wide symbol table.
This applies equally to Unity, Godot and the explicitly rebuilt local Agentic
SDK. It adds neither an arbitrary identifier limit nor eviction of live names.

## Verification

- Qualified full `check.sh --release`: **21 GCC CTest suites**, 809,530 main
  checks; **23 Clang ASan/UBSan suites**, 809,524 main checks. The sanitizer
  configuration retains its six-check small-stack exclusion. The new parser
  regression passes 9,740 checks in each build. Existing memory and allocation
  regressions remain green.
- **341 Chrome layout captures** pass the 1.5 px ceiling. Behavior checks:
  69 pass, one existing known IME exception, zero unexpected failures. Linux
  tooling passes 90 tests. Godot font checks pass 19,833; Unicode, backend,
  installation, export and affected scene gates pass.
- Both Windows native hosts were rebuilt. Unity 6000.4.1f1 full EditMode:
  **259 pass, two environment-gated inconclusive, zero failures**; exit 2 is
  expected for this result. Mono development player: **7/7**. Native plugin
  load/decode/unload passes. All 16 player images match the earlier verified
  RGBA pixels. Installed library metadata matches the final native source.
- A coherent local Agentic CLI/headless/runtime SDK passes the survival UI
  smoke: five modes, selection, clicks, use, close, reload lifecycle, ownership
  snapshots, cooldown and interaction afterward. The owned session stops and
  the engine-9 lease is released. The smoke uses fixed tag/attribute names;
  unique-name churn is covered by the standalone core regression.

Reproduce the memory regression with:

```sh
cmake --build <build> --target weva_parser_memory
ctest --test-dir <build> -R '^weva_parser_memory$' --output-on-failure
```

The full-gate environment and exact local commands are retained in
`gate/run-gate.sh`, `windows/verify-windows.ps1` and `smoke.py` beside the logs.
The qualified gate uses patched Linux Godot editor/templates, pinned Windows
Chrome 152.0.7977.82 through WSL, and private Xvfb/software rendering.

## Limits

The counter measures requested live C++ `new` storage, excluding direct
`malloc`, allocator spare pages, process RSS/private commit and host/GPU memory.
Current input size still determines parsing peaks; caller-owned C++ symbol
tables retain the caller's chosen lifetime. Temporary tables may allocate again
on later parses, so this is a retention fix with no parsing-speed or FPS claim.
The prior image-cache live-working-set exception still applies. Long hardware
soaks, historical Agentic memory attribution, IL2CPP/release stripping and
unqualified platforms remain open; see [readiness](../PRODUCT_READINESS.md).
