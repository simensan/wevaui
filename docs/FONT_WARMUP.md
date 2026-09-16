# Incremental font warmup

Call the static method from a loading screen on Godot's main thread, before
constructing a WevaDocument:

```gdscript
while not WevaDocument.warmup_fonts_step():
    await get_tree().process_frame
```

Each call loads at most one installed compatibility font, including its lazy
TextServer face setup. It returns `true` when all configured fallbacks are ready.
Further calls return `true`. Fonts are shared across documents and released at
module shutdown. An ordinary document still finishes any pending warmup
synchronously, so partial warmup does not change fallback order or coverage.

This moves font initialization into loading; it does not eliminate its total
cost, prepare document layout, or guarantee a per-frame time budget. A first
Windows headless diagnostic recorded eight steps, with the largest at 19.059 ms.
Prepare and retain the actual UI during loading as well to avoid construction
cost on interactive frames.

The Frontier sample's boot scene uses this API while displaying a loading label.
It checks API availability so the same scene still starts with older addons.
The lifecycle benchmark deliberately bypasses that warmup to retain a true cold
measurement. A focused five-paragraph multilingual test measured construction
at 64–67 ms without warmup and about 22 ms after complete warmup, with font work
performed earlier. These single-run diagnostics are not Frontier HUD timings.

`WEVA_STAGE_LOG=1` now also reports theme, compatibility-font, backend and family
setup times. Existing update-stage timing starts after lazy font setup.

## Recorded verification

This API was verified in preview211. The following counts and timings describe
that checkpoint. All 44 host entries
(35,468 checks), sample and export
checks pass. Cold, partial and full warmup produce byte-identical multilingual
text captures to preview210 on OpenGL and Vulkan. All 72 desktop, 276 automatic
1080p and 276 automatic 4K timing checks pass, along with 20 installed smoke
suites. Lower-end and longer lifecycle qualification remain open.
[Verification](verification/font-warmup.json).
