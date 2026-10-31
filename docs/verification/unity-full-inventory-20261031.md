# Full inventory selection CPU audit — 2026-10-31

The complete selection workload costs substantially more than the earlier
quantity/highlight-only Unity case. This diagnostic expands coverage; it makes
no optimization claim and changes no production engine behavior.

## Workload and verification

`Tools/performance/run_unity_runtime_audit.py --full-inventory` converts the
prepared `mutations.json` inventory sequence into bindings without dropping any
targets. Twelve selections each assign 20 values: twelve slot classes, six text
fields, the detail icon and the action's disabled state. Null disabled values
become false boolean bindings, preserving attribute removal. Every target is
checked after every iteration, outside measured stages.

Each of three fresh Unity 6000.4.1f1 editor processes ran four scenarios with
128 warmups and 2048 samples each: **24,576 measured iterations**. The final
retained render in each scenario exactly matched a fresh renderer consuming
the same draw list. PNG hashes matched across all three runs. The full inventory
image was inspected: beans selected, matching detail icon, description, count
and action label. This is a cache consistency check, not a Chrome pixel oracle
or a fresh-core comparison, and it captures the final selection only.

An initial three-process run without core stage counters also passed. Its raw
results are retained separately; the table below uses only the subsequent
three-process run with counters. Python compilation and diff checks passed.
No production suites or host builds were rerun for this diagnostic-only change.

## Results

Windows, RTX 5080, 1280x720. Milliseconds, median of three per-process medians
and p95 values. Total is formed per iteration before aggregation; independent
stage percentiles must not be added.

| Workload | Update median / p95 | Sync median / p95 | Record median / p95 | Total median / p95 |
|---|---:|---:|---:|---:|
| HUD | 0.1285 / 0.1656 | 0.0562 / 0.0749 | 0.0332 / 0.0407 | 0.2219 / 0.2738 |
| Quantity/highlights | 0.2401 / 0.2863 | 0.1623 / 0.1938 | 0.0715 / 0.0898 | 0.4708 / 0.5617 |
| Full selection | 0.7142 / 0.8178 | 0.3530 / 0.5874 | 0.1085 / 0.1344 | 1.1795 / 1.4590 |

Within full-selection update, core update is 0.6256 ms median, cascade 0.0760
and paint 0.2865. These overlap the outer update timer. The tiny `layout_ms`
counter (0.0002 median) does **not** establish negligible layout cost: incremental
box/layout work is also charged elsewhere, and this diagnostic does not yet
record `boxes_ms`. Full selection reuses a median 120 of 137 mesh batches and
uploads 1514 triangles, versus 128 of 137 and 1244 for quantity/highlights.

Update includes dictionary assignment, refresh and core processing. Sync
includes host texture/mesh synchronization. Record builds commands without
submitting them to the GPU. This is a warmed, unpaced editor diagnostic, not
player frame time, GPU cost, input latency, busy-scene performance or a matched
Chrome comparison. Earlier optimization receipts used different runs; these
absolute times do not establish a regression against those receipts.

## Evidence and next optimization

Raw logs, inputs, samples, summaries and images:

- `.utmp/render-audit-20261030/unity-full-inventory/`
- `.utmp/render-audit-20261030/unity-full-inventory-stages/`

The adjacent JSON preserves the accepted summaries and hashes of raw files,
fixtures, benchmark source and the Unity native plugin. Both runs exited zero.

Core paint and host mesh synchronization are now measured targets. Before
changing either, profile their internal operations on this full sequence. Shared
core changes must benefit Godot and Unity; Unity buffer-upload changes must be
verified in Unity and assessed against Godot's different canvas upload path.
No new Godot runtime behavior is required by this benchmark addition.
