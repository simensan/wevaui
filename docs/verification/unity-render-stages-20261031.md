# Unity first-render stages — October 31, 2026

Nine fresh-editor runs split the offscreen render diagnostic into target
creation, command recording, ExecuteCommandBuffer, readback and cleanup.
Each process renders twice using the production Draw method. Both draws use
a fresh target and command buffer; the second retains document, textures,
meshes and materials. PNG encoding occurs between draws, outside stage timers.

Median milliseconds for the three renderer-prepared runs:

| CPU call duration | First draw | Retained draw |
| --- | ---: | ---: |
| Target/command-buffer creation | 1.413 | 0.191 |
| Command recording | 3.047 | 0.109 |
| ExecuteCommandBuffer | 3.380 | 0.791 |
| Texture allocation, ReadPixels and Apply | 3.680 | 4.039 |
| Cleanup | 0.353 | 0.028 |

First command recording across all nine runs is materially more expensive
than retained recording. Source inspection identifies lazy shader lookup and
material creation there, alongside managed first-use costs. This measurement
does not isolate those contributors. The next targeted experiment is explicit
material preparation through the existing resource-loading path.

Readback is not faster in every retained run. CPU submission and readback can
wait on graphics work, and are not GPU timestamp measurements. These numbers
must not be labeled GPU rendering time, player frame time or a Chrome speed
comparison. There is no production optimization claim in this diagnostic pass.
The original combined total also includes first use of the measurement helper;
stage sums need not equal that total. Each table entry is an independent median.

The full results preserve a 38.40 ms CPU-prepared opening outlier. No sample
was discarded. Only three samples per mode were collected on this machine.

## Verification and reproduction

Unity 6000.4.1f1, RTX 5080, survival inventory at 1280×720 after initial
640×480 activation. Three rounds rotate cold, CPU-prepared and renderer-prepared
order. All nine Unity processes exited zero. All eighteen first/retained
captures are identical and match the previously inspected bound inventory.
The diagnostic compiled in Unity, Python compilation and diff checks passed.
Production renderer code is unchanged; no host correctness suites were rerun.

Run the existing preparation runner with `--rounds 3 --renderer-prepared`
using the [documented fixture and editor](unity-preparation-cost-20261031.md).
Raw logs, individual measurements and images remain under
`.utmp/render-audit-20261030/unity-render-stages`. The adjacent JSON contains
the results and evidence hashes. Shader/material preparation is still separate
from completed GPU execution in Unity and Godot; no cross-host speedup is inferred.
