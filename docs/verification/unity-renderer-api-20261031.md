# Unity explicit renderer preparation — October 31, 2026

The unreleased 2.0 API now includes `WevaDocument.PrepareRenderer()`.
It applies current bindings and CPU paint through `Prepare()`, then uses the
existing renderer synchronization to upload textures and build meshes. The
component stays hidden if hidden, registers no input, and retains resources
for showing. There is no second rendering implementation or cache.

```csharp
ui.Visible = false;
ui.SetController(menuController);
ui.PrepareRenderer();
// Later:
ui.Visible = true;
```

This is synchronous loading work and requires a graphics device. It does not
draw, create all materials, warm shaders, or wait for first GPU execution.
Prepare at the intended viewport with current content. Later viewport,
font or content changes may require more work. Parameterless `Prepare()`
remains CPU-only. Both calls do nothing without an initialized document;
component disable still releases resources.

The [preceding rotated experiment](unity-renderer-preparation-20261031.md)
measured 14.49 ms versus 0.22 ms opening CPU by moving resource setup into
loading. That result used internal Sync directly; the diagnostic now calls
the supported API. It is not a player timing claim. Godot already uploads
new textures during explicit hidden updates; no Godot/core change is needed.

## Verification

- Full Unity EditMode: 248 passed, zero failures, two existing environment-gated
  inconclusive. The new lifecycle test checks binding changes, retained paint,
  reload, disable/no-op, re-enable, textures and meshes.
- URP PlayMode: 7/7. The hidden preparation test now calls PrepareRenderer
  before showing and after a hidden content change. It verifies Sync returns
  false, and captures black/red/black/blue. All four captures were inspected.
- Windows x64 Mono development player: 7/7, exit zero. All four hidden/show
  captures exactly match the inspected editor captures. This verifies player
  behavior, not player timing or IL2CPP/release stripping.
- Public API survival smoke: three fresh-editor modes completed with identical
  output matching the previously inspected inventory. Renderer-prepared opening
  was 0.291 ms, CPU-prepared 11.691 ms, cold 210.277 ms. This single smoke round
  verifies the supported path and does not replace the earlier repeated data.

Evidence directory: `.utmp/render-audit-20261030`. EditMode XML/log:
`renderer-api-edit.*`; PlayMode XML/log: `renderer-api-play.*`; captures:
`renderer-api-play-renders`. Adjacent JSON records hashes and test counts.
Player evidence: `renderer-api-player/receipt.json`, `results.xml`, `build.log`
and `renders/`. Reproduce with `Tools/run_unity_player_tests.py`.
Survival evidence: `renderer-api-survival`; use the preparation runner with
`--rounds 1 --renderer-prepared` and the existing survival fixtures.
No ABI change, native rebuild, Chrome behavior change or full integration
gate is claimed by this host-only change.
