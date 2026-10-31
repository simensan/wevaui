# Rejected ordinary-draw bounds experiment — 2026-10-31

Agentic computes vertex bounds for each new draw batch, although only backdrop
filters consume them. An experiment skipped that scan for ordinary draws.
**It was removed: a freshly rebuilt baseline did not establish a runtime gain.**
There is no source change or new performance improvement in this receipt.

The first four alternating pairs used the existing accepted diagnostic binary
as baseline and reported roughly 9% lower complete CPU time. Unchanged layout,
paint and encoding also became faster, undermining attribution. Both versions
were then rebuilt with the same command and the comparison repeated. The
second batch supersedes the first for the retention decision; both are kept.

| Fresh-baseline comparison | HUD | Inventory |
| --- | ---: | ---: |
| Preparation median of per-run means, before → candidate | 0.0855 → 0.0855 ms | 0.1825 → 0.1800 ms |
| Preparation median paired change | -1.74% | -0.14% |
| Preparation faster pairs | 3/4 | 2/4 |
| Complete CPU median paired change | +1.04% | +6.46% |
| Complete CPU faster pairs | 1/4 | 1/4 |

Positive percentages mean slower. These observations do not prove that
skipping the scan causes a regression; unchanged stages also varied. They
provide insufficient evidence to retain this optimization. No cumulative
gain, FPS gain, pixel parity or new rendering-test pass is claimed.

Each batch has eight fresh processes, each measuring 200 changing and 200
retained updates for both real-font survival cases after 20 warmups, paced
at 60 Hz. That is 6,400 measured updates per batch, 12,800 total. Initial and
final queried geometry matched across all eight runs within each batch.
Windows RTX 5080/Vulkan NVIDIA 617.42, 1280×720. No builds or tests overlapped
timing. Existing core and SDK sources were otherwise unchanged.

Godot already limits its equivalent vertex-bounds scan to backdrop filters.
Unity sets mesh culling bounds without an equivalent ordinary-draw scan.
Neither host needs a corresponding change.

Source `render.rs` was restored byte-for-byte to Agentic `6ab7386e3` and the
optimized `weva_cost` and `render_page` binaries rebuilt successfully with
`cargo build --profile fast -p agentic_weva --features diagnostics --bin
weva_cost --bin render_page`. The installed SDK was never changed. Because the
candidate was rejected before correctness qualification, its planned rendering
suite was not run; prior tests apply to the restored unchanged source.

Raw evidence lives under `.utmp/render-audit-20261030/backdrop-bounds/` and
`backdrop-bounds-v2/`: executable hashes, all timing rows, cadence, geometry,
source snapshots and build logs. Initial target-discovery errors are retained.
Runners are `run_bounds_pairs.py` and `run_bounds_pairs_v2.py` in the parent.
The companion JSON preserves both complete receipts.

Further work should target a larger measured cost or a more representative
production workload, rather than continuing small renderer-loop edits whose
benefit is below this experiment's variability.
