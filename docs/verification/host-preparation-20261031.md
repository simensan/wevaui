# Host preparation attribution — October 31, 2026

Follow-up: the proposed [bounded texture cache](bounded-textures-20261031.md)
was implemented experimentally, measured and rejected. The temporary retention
probe below is not an outstanding production change or an accepted cache policy.

The inventory host cost is not primarily queue submission. The host-engine
diagnostic now separates snapshot capture, resource/batch preparation,
encoding (including finish), and queue submission. These are CPU intervals;
GPU execution and presentation remain outside them.

Three initial runs split prepare/encode/submit. A further capture split
measured inventory at 0.031ms capture, 0.256ms resource/batch preparation,
0.096ms encoding and 0.086ms submission. These exploratory numbers vary in
subsequent processes; they locate work, not a stable latency guarantee.

## Confirmed texture churn

The selected detail icon receives a new 95x95 texture ID on every selection.
For example, frames 31–33 use IDs 66, 67 and 68, while the glyph atlas and
other images keep their IDs. TextureCache::end_pass releases unused entries;
returning to a previous selection therefore creates another host texture.
The shared pixel cache saves rerasterization but not GPU allocation/upload.
The host engine's renderer correctly uploads every new ID. This lifecycle also reaches
Godot and Unity through the shared core; their costs were not measured here.

## Fixed-workload retention probe

A temporary core experiment kept unused entries while there were at most 64
entries total. This is deliberately not a production memory policy. Three
alternating before/after process pairs used the prepared 720p survival cases,
20 warmups, 200 changing and 200 retained frames, pacing at 60 Hz. Median
per-run means in ms:

| Inventory CPU work | Before | Retention probe |
| --- | ---: | ---: |
| Snapshot capture | 0.038 | 0.041 |
| Resource/batch preparation | 0.092 | 0.063 |
| Encoding | 0.094 | 0.101 |
| Queue submission | 0.081 | 0.078 |
| Complete controller/update/prepare/submit | 1.234 | 1.196 |

The 3.1% complete-path difference is modest and based on three pairs with
variable driver/resource timings. It does not justify an unbounded or
entry-count-only cache. Inventory retains twelve detail textures instead of
one in this probe, adding 397,100 RGBA bytes plus host/driver overhead.
No production optimization is retained or claimed from this experiment.

The next candidate is a byte-bounded inactive-texture cache in the shared
core, verified for eviction, backend replacement, document destruction,
texture identity and repeated selection/reopening. Measure the memory/time
tradeoff before adoption; do not work around it with a game-specific icon pool.

## Evidence and current state

The host diagnostic builds and runs against restored core `4c8bb0af`.
Phase sums were checked across 14 workload results within printed rounding
error. Initial compile mismatch evidence is retained; the final build passes.
The temporary texture-ID logging and core retention edit were both removed.
Unity and Godot never received the experimental core; their accepted binaries
and prior verification remain unchanged. No production renderer code changed.

Raw logs: `.utmp/render-audit-20261030/host-phases-*`, `host-capture-*`,
`texture-probe.log`, and `texture-retention-probe-paired/`. The temporary core
diff is preserved as `texture-retention-probe.patch`.
[Every paired result and phase interval](host-preparation-20261031.json)
includes executable hashes. This is attribution evidence, not a new full gate.
