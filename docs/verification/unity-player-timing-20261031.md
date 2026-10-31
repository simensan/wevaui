# Unity presented-frame timing audit — October 31, 2026

This opt-in diagnostic measures Unity's reported whole-player frame timings
in a Windows x64 Mono development player. It uses the production URP path
with a black camera background and the survival UI at 1280×720. It does not
measure an isolated UI GPU interval or change engine rendering behavior.

## Results

Unity 6000.4.1f1, Direct3D 12, RTX 5080, GPU timer frequency 1 GHz.
The completed run contains 9,000 unique frames, all focused, with zero missing
or duplicate frame timestamps. GPU timings are available for 8,997/9,000 frames.
The three unavailable values are in idle-HUD round 2 (597/600); every other
phase has 600/600. Raw unavailable rows remain in the evidence.

Median of three per-round medians / p95 values, milliseconds:

| Case | CPU main | CPU render thread | Reported GPU frame |
| --- | ---: | ---: | ---: |
| Background | 0.361 / 0.604 | 0.143 / 0.228 | 0.0202 / 0.0210 |
| Idle HUD | 0.411 / 0.663 | 0.213 / 0.329 | 0.0205 / 0.0212 |
| Changing HUD | 0.613 / 0.933 | 0.299 / 0.546 | 0.0271 / 0.0274 |
| Idle inventory | 0.476 / 0.768 | 0.295 / 0.443 | 0.0302 / 0.0312 |
| Changing inventory | 0.836 / 1.240 | 0.400 / 0.716 | 0.0384 / 0.0389 |

GPU statistics are conditional on available readings; idle-HUD coverage is
incomplete and must accompany those values. CPU frame medians are about
16.667 ms in every case because of the 60 Hz target. This does not establish
uncapped FPS. CPU work dominates the measured UI cases on this lightly loaded
GPU; the next useful checks are a busy scene and the complete inventory
controller mutation sequence, rather than assuming shader optimization is
the priority. No before/after speedup or Chrome GPU parity is claimed here.

The opt-in player test passed (exit zero), including ongoing render serials,
binding values and visible/control captures. Final HUD and inventory captures
were inspected. Python compilation and diff checks passed. Production code
is unchanged, so existing correctness suites were not rerun. This is not a
full release gate, lower-end hardware test or IL2CPP/release player result.

## Method and boundaries

Three rounds run background-only, idle HUD, changing HUD, idle inventory and
changing inventory. The second round reverses case order. Each phase warms
120 frames and collects 600 unique completed-frame timestamps. GPU availability
is stored per sample; zeros are unavailable, not free rendering. The summary
records coverage separately and computes GPU statistics only from available
values. An all-unavailable phase fails. Missing or repeated timestamps are
counted explicitly, with a finite polling limit.

VSync is disabled and the target frame rate is 60. CPU frame duration includes
pacing/waits; main-thread, render-thread and present-wait fields are separate.
Do not add their medians/p95 values or subtract p95 values to infer UI cost.
The background control is context for the same whole-player boundary, not
an exact per-pass subtraction. GPU clocks are not locked. Development/test
framework overhead and other engine work remain in the measurements.

Unity documents a four-frame reporting delay and warns that GPU data can be
unavailable. The long phase warmup separates transitions from measured frames.
See [FrameTimingManager](https://docs.unity3d.com/6000.0/Documentation/Manual/frame-timing-manager.html)
and the [FrameTiming fields](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/FrameTiming.html).

The HUD changes bound stamina text and meter width. Inventory changes bound
quantity and two slot highlights; this is not the complete selection/controller
flow. Automatic component updates and normal camera rendering run throughout.
Before any manual capture, the test checks that renderer synchronization has
reached the core draw serial from the preceding frame that had an opportunity
to render. Current CPU paint may already be one frame ahead when the coroutine
resumes. This assertion checks ongoing rendering rather than proving it only
through the later screenshot.

Raw logs, XML, fifteen captures, raw frame arrays and per-phase summaries are
under `.utmp/render-audit-20261030/unity-player-timing-v5`. The adjacent JSON
records summaries and evidence/build hashes.

After timing, camera captures verify visible UI versus the empty control;
bound values are checked too. Capture/readback work is outside timing and is
followed by the next phase's warmup. No user scene is loaded or saved. Temporary
test-player settings and runtime timing/pacing settings are restored.

## Reproduction

```powershell
python Tools/performance/run_unity_player_timing.py --unity "C:/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor/Unity.exe" --fixtures .utmp/render-audit-20261030/survival-fixtures --output .utmp/render-audit-20261030/unity-player-timing-v5 --fps 60
```

The output directory must be new. The runner builds only Unity's generated
test bootstrap, invokes the opt-in standalone test, preserves XML/build logs,
captures and every raw timing, then validates/summarizes all fifteen phases.
The test has an explicit six-minute timeout; the build/run wrapper has ten
minutes. Ordinary editor test runs skip this opt-in player diagnostic.

## Preserved diagnostic failures

All earlier attempts remain under `.utmp/render-audit-20261030`:

- `unity-player-timing`: missing namespace import caught at compilation; fixed.
- `unity-player-timing-v2`: uncapped background collected 496 valid GPU timings
  out of 600. The first harness incorrectly required every GPU value to be
  available; availability is now recorded explicitly without discarding rows.
- `unity-player-timing-v3`: fourteen 60 Hz phases completed before the test
  framework's default three-minute timeout. It is excluded from final results.
- `unity-player-timing-v4`: an overly strict current-paint equality assertion
  saw CPU serial 721 versus rendered serial 720. This was coroutine scheduling,
  not stale rendering; the assertion now checks the preceding eligible serial.

These are harness/measurement limitations, not production renderer fixes.
