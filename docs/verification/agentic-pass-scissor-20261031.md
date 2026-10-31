# Agentic render-pass scissor ownership — 2026-10-31

Agentic's public `DocumentRenderer::render` could inherit a scissor set by a
previous user of the same render pass. The first batch with no document scissor
did not reset it, so an ordinary canvas background could be mostly missing.
The regression renders a red body background after a 1×1 incoming scissor:
pixel (48,16) was black instead of red on the 64×32 target.

The renderer now starts with scissor state marked as needing reset. The first
unclipped batch establishes full document bounds; a clipped batch still sets
its own rectangle. Subsequent unclipped batches retain the existing avoidance
of redundant resets. This fixes ownership at the adapter's render-pass boundary
without changing core clipping, CSS or the ABI. No performance gain is claimed.

## Reproduction and verification

The first fixture used only a colored div, whose draw already carried a scissor;
its positive-control assertion rejected it. That failure is preserved separately.
Changing the fixture to a normal body/canvas background produced an unclipped
batch and reproduced the actual black-versus-red pixel failure before the fix.
The regression uses real GPU readback and requires full byte equality between
clean-pass and inherited-scissor output after the fix.

Agentic commit `3c84a1819599f53cf49ddbf19dd788f40334ed25` on the existing leased
`codex/weva-render-audit` branch passes the selected full `agentic_weva` library
suite: **84 passed, zero failed, two existing ignored tests**. The ignored cases
require an installed Arabic-capable font and an isolated optimized 8 ms timing
budget; neither is represented as passing here. The suite includes clipping,
alpha/texture ordering, blending, backdrop effects, viewport updates and queued
document ownership. The focused regression failed before and passes after.

Verification uses `WEVA_SOURCE_DIR` pointing at this checkout and
`python tools/verify_engine.py -p agentic_weva --target agentic_weva/agentic_weva
--rendering`; the initial failure uses the named-test filter. A fast optimized
`render_page` build also succeeds. Its 64×32 canvas PNG was inspected; the
inherited-scissor case itself is checked by the GPU regression, not that tool.
This is Windows coverage, not a macOS qualification.

## Host synchronization and delivery scope

Unity's native renderer and Godot's canvas renderer consume geometry already
clipped by the core. Neither implements Agentic's per-batch scissor-state loop,
so this adapter-specific fix is not copied into those hosts. This inspection
does not claim to test every external render-state integration in Unity or Godot.
Their existing shared-core verification remains applicable; no core source or
host binary changed in the main repository.

The Agentic change is committed on the leased audit branch, not pushed,
published as an SDK, or merged into the user's configured runtime. The
[JSON receipt](agentic-pass-scissor-20261031.json) records commits, verification
reports and evidence hashes. Preserved local files are under
`.utmp/render-audit-20261030/agentic-pass-scissor/`.
