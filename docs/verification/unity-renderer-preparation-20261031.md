# Hidden Unity renderer preparation experiment — October 31, 2026

The internal benchmark now has a third mode: synchronize textures and meshes
while hidden, after CPU preparation. No public component behavior changes.
Nine fresh Unity editor processes rotate cold, CPU-prepared and
renderer-prepared ordering across three rounds. All exited zero and produced
identical bound inventory PNGs; the renderer-prepared capture was inspected.
Every renderer-prepared opening reused the prepared draw serial and Sync
returned false (no resource rebuild).

| Median milliseconds (3 per mode) | Cold | CPU-prepared | Renderer-prepared |
| --- | ---: | ---: | ---: |
| Opening CPU, including resource sync | 213.823 | 14.485 | 0.216 |
| Renderer sync during loading | 0 | 0 | 10.792 |
| First offscreen render and readback | 14.884 | 14.733 | 12.921 |

Renderer-prepared opening ranges from 0.203 to 0.223 ms. This proves that
first texture/mesh synchronization can be moved before opening, not that the
work disappears or that a player frame now costs 0.216 ms. Shader/material
creation still occurs during drawing. First GPU execution and asynchronous
URP frame behavior are not isolated by this synchronous readback diagnostic.
Loading totals and all individual measurements are in the adjacent JSON.

Godot already creates ImageTextures inside ensure_updated after new paint is
published, including explicit hidden update_document calls. Unity's public
Prepare currently stops at CPU paint. A supported Unity renderer-preparation
option would close this loading-workflow gap; it still needs lifecycle,
hidden URP and player validation before release. No core or Godot behavior
was changed by this experiment.

## Reproduce

Use the [previous diagnostic command](unity-preparation-cost-20261031.md), add
`--renderer-prepared` and choose a new output directory. Evidence for this run
is `.utmp/render-audit-20261030/unity-renderer-preparation`.
Environment and timing boundaries remain the same: Unity 6000.4.1f1, RTX 5080,
1280×720 after initial 640×480 activation, file reads outside timing,
fresh-editor managed/font first-use costs included. This is not player FPS
or a Chrome comparison. Only three samples per mode were collected.

The benchmark compiled and completed nine invocations. Python compilation
and diff checks passed. Logs contain the existing licensing access-token
refresh message, but each process reports successful batch exit. A comment-only
benchmark edit during the run triggered one additional editor compilation;
compilation precedes the measured method. Evidence retains all logs and hashes.
