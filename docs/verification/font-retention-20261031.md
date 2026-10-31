# Unity font-retention fixes — 2026-10-31

Both confirmed findings from the [memory audit](leak-audit-20261031.md) are fixed.
Rejected font bytes are held only by their pending source and enter the shared
canonical cache after FontEngine accepts the load. Activation checks for an
already accepted buffer first, preserving native font identity across documents
and editor asset/byte sources. This retains the prior protection against native
FontEngine table growth while excluding rejected payloads from the domain cache.

The CSS loader now remembers accepted content-to-face identities for each URL.
Returning to a previously accepted version reuses its face and metrics/glyph maps.
Backend disposal clears that index. Existing references remain valid; faces used
by fallbacks and variants are not removed prematurely.

| Original audit probe | Before | After |
| --- | ---: | ---: |
| Cache payload after 24 rejected 256 KiB fonts and disposal | 6,291,456 bytes | 0 bytes |
| Faces after alternating two valid files at one URL 200 times | 200 | 2 |
| Faces after disposal | 0 | 0 |

These are the original reflection-based audit probes rerun on the fix. All 24
rejected-load backends were disposed and collected in both runs. Committed tests
also cover rejected cache insertion and the public CSS synchronization path.
That synchronization test installs a default font first, so it expects three
records: the default and the two CSS versions.

Unity full EditMode passes **257 tests plus two environment-gated inconclusive**.
This includes the native-memory lifetime tests for asset, file and byte font
sources. The Windows x64 Mono development player passes **7/7**. All 16 player
render captures match the accepted lazy-atlas baseline exactly; a representative
render was visually inspected. No core, ABI, native plugin or Godot code changed;
the previous core/Godot qualification remains applicable to those unchanged files.

Initial compile and regression-setup failures are preserved under
`.utmp/font-retention-20261031/`; the final full suite and player are green.
The first setup used the CSS family registration path without installing its
backend, producing invalid-argument failure. Correct installation resolved it.
The adjacent JSON records results, source hashes and evidence hashes.

Accepted distinct fonts still retain canonical buffers for the Unity domain's
lifetime because FontEngine can retain native tables by buffer identity. This
fix prevents rejected-buffer retention and duplicate faces for recurring content;
it does not impose a budget on every distinct successfully loaded font. The old
Agentic 13.7 MiB process-private growth remains unattributed.
