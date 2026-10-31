# Explicit hidden preparation — October 31, 2026

Agentic's embedding `Overlay` now exposes `prepare_snapshot()`. It applies
pending bindings and runs the existing document update at zero elapsed time,
then captures immutable CPU paint data. Unlike ordinary `snapshot` it is an
explicit request to prepare even when hidden. It does not toggle visibility,
take input, allocate a local renderer or submit any drawing.

This lets an embedding host move first layout, glyph and paint preparation
into its loading phase without flashing the screen. The returned frame can
be uploaded by the existing owning renderer. GPU pipeline creation, upload,
backdrop resources and eventual presentation remain that host's responsibility.
This is synchronous scheduling of existing work, not faster rasterization.

The operation reuses the same update/binding/capture implementation and cache
as normal snapshots. Ordinary hidden updates continue to defer work. Later
bindings, viewport changes, fonts and reloaded markup remain invalidations;
this is not a promise that every future opening will be free.

## Verification and scope

All 44 Overlay tests pass in Agentic
`builds/verify/20261031-014631/report.json`. The new regression prepares bound
geometry and text while hidden, verifies glyph textures exist and no local
renderer or pointer ownership is introduced, and confirms repeated preparation
and the unchanged first visible snapshot reuse the identical `Arc<PaintFrame>`.
It then verifies deferred hidden binding changes and markup reload produce new
frames while staying hidden. Existing tests retain direct/snapshot hidden-work,
input, binding, form and lifecycle coverage.

The shared core/ABI and font output are unchanged. Godot already provides an
explicit `update_document(dt)` path that calls `ensure_updated` independently
of its automatic hidden-update policy. No new Godot or Unity performance result
is implied; their accepted shared-core improvements remain unchanged.

This chunk provides the embedding primitive only. A script-facing preparation
request, runtime/editor integration, real hidden-preparation/opening benchmark
and appropriate Unity workflow remain follow-up work. The previous 0.56 ms
retained reopening measurement must not be presented as a measurement of this
new API. No SDK switch, upstream merge, release gate or new speedup is claimed.
