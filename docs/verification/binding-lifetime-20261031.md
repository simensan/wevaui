# Binding callback lifetime audit and fix — 2026-10-31

A Unity callback ownership cycle prevented NativeDocument's finalizer from
running for an abandoned document with bindings. NativeBindings installed a
strong GCHandle to itself; its `_doc` field kept the document alive. Neither
object could be collected, so the native document and the bound model graph
survived for the domain lifetime when explicit disposal was missed.

The adapter now installs a weak callback handle. The live document's
`BindingOwner` already supplies managed ownership of the adapter. An unreachable
managed document/adapter cycle can therefore be collected. NativeDocument's
finalizer now calls the same Dispose path as explicit disposal, detaching the
binding source and releasing its callback handle before native destruction.
Binding detachment uses the C ABI and managed state, with no Unity rendering or
asset calls on the finalizer thread. Ordinary explicit disposal remains the
normal deterministic cleanup path.

## Measured reproduction

In a fresh Unity editor invocation, create 24 native documents, install bindings
with 256 KiB payloads, and retain only weak references to the document, adapter
and payload. Force collection, wait for finalizers, and collect again.
The identical probe is rerun on the fixed source.

| Objects still alive | Baseline `62dae03c` | Fixed |
| --- | ---: | ---: |
| Documents | 24 | 0 |
| Binding adapters | 24 | 0 |
| Model payloads | 24 | 0 |
| Payload bytes alone | 6,291,456 | 0 |

The payload figure excludes document/native/adapter overhead and is based on
object liveness, not process RSS. This is a confirmed managed-root leak under
missed disposal, rather than a cache or allocator high-water measurement.

## Verification

Two committed regressions cover abandoned documents with models, and live
callback ownership through GC. The live resolver itself triggers collection;
text and repeated rows must still resolve. Explicit disposal must detach the
adapter. The abandoned-object test uses a non-inlined creation helper to avoid
JIT stack temporaries keeping references alive during collection.

Full Unity EditMode passes **259 tests with the same two environment-gated
inconclusive results**. The Windows x64 Mono development player passes **7/7**.
All 16 Unity player captures match the preceding font-retention build exactly;
a representative render was visually inspected. The initial live-callback test
also asked for immediate collection in the same method that inspected the
adapter, and Mono retained a temporary reference. Its redundant collection
assertion was replaced by deterministic detachment; the separate abandoned
collection test already passed. The failed receipt is preserved.

This pass changes only Unity wrapper lifetime management; shared C++, ABI,
native binaries and Godot sources are unchanged. The existing core/sanitizer
and Godot qualification applies to those unchanged sources. This is not a
rerun of the full cross-host release gate or attribution of the earlier Agentic
process-memory growth.

## Additional audit scope

Reviewed renderer texture/material replacement and disposal, input subscription
cleanup, Unity font caches, and core image decode ownership. Renderer replacement
drops unpublished resources; mesh pools retain their high-water working set.
The core image store intentionally caches decoded URLs and failures for the
document lifetime; asynchronous decode jobs own bytes and hold their publication
generation weakly. These were source reviews, not a new live image/GPU soak,
and do not establish bounds for all changing-resource workloads. No speculative
cache eviction was added to paths that expose borrowed decoded-image pointers.

Raw baseline/fixed probes, editor/player logs and the initial failed regression
are under `.utmp/binding-lifetime-20261031/`; the adjacent JSON hashes the evidence.
The diagnostic runner removes its temporary editor source and metadata in cleanup.
