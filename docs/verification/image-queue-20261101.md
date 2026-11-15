# Async image ownership and native unload — 2026-11-01

Three confirmed findings are fixed in the shared core:

1. Cancelling a document generation left its encoded image buffers owned by
   the global decode queue until workers reached them. The generation now owns
   queued buffers; the scheduler holds weak tickets. Cancellation releases the
   buffers while unrelated decodes are still blocked.
2. The per-update limit of two reads did not bound a slow generation's total
   outstanding work. A generation now admits at most **four** queued, decoding
   or completed-but-unpublished images. The owner checks capacity before calling
   the host reader; publication releases admission slots.
3. A process-global worker-pool owner kept Windows worker threads and their
   native module loaded after the host destroyed every document and called
   `FreeLibrary`. Live image stores now lease a shared pool, with a weak global
   registry. Destroying the final lease retires the threads before code unload.

These are core-owned fixes. Unity and Godot use the same implementation; an
explicitly rebuilt local host-engine SDK also contains it. No C ABI layout, function
or minor version changes. Default synchronous image loading is unchanged.

Base revision: `e1ddbd27` (the full revision is in the
[machine-readable receipt](image-queue-20261101.json)). Raw evidence is retained
under `.utmp/image-queue-20261101/`.

## Regressions and measured retention

The scheduler has a private header so its actual worker implementation can be
tested with a controlled decoder. The baseline preserves the original ownership
while allowing both workers to be held at a barrier. Four further jobs each own
4 MiB of encoded bytes. A live C++ `new` counter measures retained requested
storage before workers are released; it is not RSS, allocator capacity or a
production texture-memory measurement. The paired GCC probe reports:

| Immediately after cancelling the queued generation | Baseline | Candidate |
|---|---:|---:|
| Live C++ storage above the settled worker baseline | 16,777,857 bytes | 224 bytes |
| Queued encoded buffers still retained | 16 MiB | 0 |

The initial regression fails three checks on the baseline: capacity, refusal
when full, and immediate buffer release. The final suite passes **150 checks**,
including 128 cancelled generations while both workers remain blocked, no decode
of cancelled tickets, no strong generation pin during a running decode, capacity
held by unpublished results, and resumed admission after publication.
The same 150 checks also pass under Clang ASan/UBSan and Windows MSVC; MSVC
retains 216 bytes of bookkeeping in its cancellation measurement.

A separate Windows probe first reproduced the native-module issue using the
preserved pre-lifetime-fix player plugin: async loading starts two workers,
document destruction returns, and `GetModuleHandleW` still finds the module
after `FreeLibrary`. The candidate returns no module handle. The committed
host-free Unity plugin test now loads and releases the real plugin twice,
decodes a valid PNG through its asset-reader ABI, checks settled state, destroys
the document and verifies actual Windows module removal. Linux runs the same
decode and close/reopen lifecycle; its check verifies successful `dlclose`.
The static-consumer preprocessor branch also passes a GCC syntax check with
warnings treated as errors.

## Lifetime tradeoff and limits

Clear, reader replacement and base-path replacement cancel the generation
without joining a decoder. Already-running work owns its input until decoding
ends and cannot publish to a replacement generation. The final image-store
destruction joins at most two active decodes so executable code cannot outlive
its module. That destruction can wait for those decodes; no frame-time or
constant-time destruction claim is made. Other stores keep their pool lease.

Four outstanding jobs is a **count bound**, not a byte budget for an individual
encoded file, all live documents, published images or failed URL entries. The
[image-cache budget debt](tech-debt-20261101.md#remaining-debt-in-priority-order)
remains. These fixes do not attribute the historical host-engine process growth;
the [matched memory controls](host-engine-memory-20261101.md) have separate workload
and build identities.

## Final verification

- Qualified full `check.sh --release`: GCC **19** CTest suites, **809,530** main
  checks; Clang ASan/UBSan **21** suites, **809,524** main checks. Both include
  the new queue regression; exact runtime identities are in the JSON receipt.
- All **341** tracked Chrome layout captures remain within the 1.5 px ceiling;
  behavior checks retain their existing known IME exception and have no new
  unexpected failures.
- Unity Windows full EditMode: **259 pass, two environment-gated inconclusive**.
  Mono development player: **7/7**. The new plugin load/decode/unload test passes.
- Godot extension, font, Unicode, backend, install, export and scene gates pass
  with the qualified patched Linux editor/templates and private Xvfb display.
  An inspected scoped forms capture has mean channel difference 0.02/255,
  maximum 1/255 and zero structural mismatches against the software backend.
  All 16 Unity player images match the prior verified images after RGBA decoding.
- A local optimized host-engine CLI/headless/runtime SDK builds with explicit current
  Weva sources and passes the survival UI lifecycle smoke: modes, selection,
  use, close, reloads, ownership snapshots, cooldown and post-soak interaction.
  The owned session stops successfully. No global SDK selection or remote pin
  changes, publication, new long memory soak or performance comparison is claimed.

The first gate was deliberately interrupted after discovering the additional
DLL-lifetime defect. Its partial logs and interruption record remain in `gate/`;
the complete final run is separate under `final-gate/`. Earlier failing unload
and allocation evidence is preserved. No historical verification receipt was
rewritten.
