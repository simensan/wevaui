# Paint phase probes — October 31, 2026

Temporary timers narrow the previous unclassified paint cost. They do not
support a production speedup. All probe code and the subsequent experiment
were removed, and the standalone diagnostic rebuilt against accepted source.

## Attribution

The prepared 720p survival inventory has 20 warmup mutations followed by 200
measured mutations. Each probe's last 200 paint reports cover these mutations.
The first two probes use 60 Hz pacing; later probes are unpaced to locate work
quickly. Timers and synchronous stderr output perturb execution, so these
figures are attribution only and must not be combined across probes.

First probe, mean ms: replay checks 0.0239, state setup 0.0462, own-box work
0.1787, child ordering 0.0076. Own-box work contains text, effects, background,
decoration and overflow setup; it excludes recursive child painting.

Second probe splits own-box work: effects 0.0164, image/background preparation
0.0330, decorations 0.0415 and text/control/overflow work 0.0917 ms. A further
unpaced probe separates overflow setup at 0.0041 ms from text/control work at
0.0664 ms. The final text probe reports 0.0161 ms selecting style/face/spacing,
0.0036 ms shadow preparation and no form-control drawing in this workload.
Existing text geometry measurements nest inside the text interval. These
categories identify costs; they do not establish one dominant algorithm.

Replay validation and child ordering are smaller than own-box work. The large
previous remainder was not evidence that either was the principal bottleneck.
The next investigation should address repeated changed-subtree paint work or
the independently measured render-worker preparation, rather than another
small literal-property lookup experiment.

## Rejected font-family experiment

`LayoutContext::font_for` normalizes a family token into an owned string on
every lookup. The candidate instead trimmed/unquoted a view and compared it
against registered lowercase names using the existing ASCII comparison.
Registration kept its normalization. This removed a repeated copy/allocation
without adding a cache or lifetime rules, but the measured complete path did
not improve consistently.

Four alternating process pairs, 20 warmups, 200 changing/200 retained frames,
60 Hz pacing, no concurrent builds. Median of per-run CPU means, milliseconds:

| Work | HUD before → candidate | Inventory before → candidate |
| --- | ---: | ---: |
| Paint | 0.0570 → 0.0560 | 0.2900 → 0.3075 |
| Core update | 0.1430 → 0.1410 | 0.7095 → 0.7330 |
| Complete CPU path | 0.4030 → 0.3905 | 1.3065 → 1.3775 |

Inventory complete CPU was 5.4% higher in this batch, so the candidate was
reverted. This is not proof that copying a family name is intrinsically faster;
it is insufficient evidence to adopt this candidate as a performance fix.
All eight initial/final mutation-target geometry sequences agree. No broad
correctness gate was run for rejected code; no host received it.

[All paired runs, binary hashes and probe means](paint-phases-20261031.json).
Raw logs and patches remain under `.utmp/render-audit-20261030/`:
`paint-split*`, `paint-own*`, `paint-overflow*`, `paint-text*`,
`paint-phase-probe.patch`, `font-family-paired/`, `font-family-experiment.patch`,
and `paint-phases-restored-build.log`. Godot, Unity and published host-engine SDKs
remain on accepted shared-core behavior. This chunk records audit evidence,
not a new full gate or production speedup.
