# Inventory paint investigation — 2026-10-31

No runtime change is retained from this pass. The accepted engine remains
`f5325006`; the no-shadow color-resolution experiment was reverted after its
measurements failed to establish a useful runtime gain. This is not a new
Chrome-parity or speedup claim.

## Attribution

The existing paint diagnostic puts most inventory paint time outside glyph
construction and rasterization. No new shadow or filter textures were built
during the sampled steady updates. Temporary additional timers attribute
approximately 13.7 microseconds/update to replay checks and replay, 20.5 to
the state setup from containing-block checks through clip-path parsing, and
3.6 to child paint ordering. These are means of the final 200 inventory
samples in `diagnostic2.log`, at a requested 1 ms interval. Timer overhead and
workload interference make these diagnostic attribution, not frame timings.

The first instrumentation attempt accidentally included nested glyph-preparation
scopes in the state counter; that counter is invalid. Its source and output
are preserved separately, and the corrected second run is the basis above.
All instrumentation was removed.

## Rejected experiment and comparisons

The candidate deferred resolving `color` for box shadows until after checking
whether `box-shadow` was empty or `none`. It added no cache and changed no
shadow grammar. All 17 GCC suites passed. Initial/final sampled geometry
matches across each batch's six native runs.

Six alternating baseline/candidate/Chrome pairs used the existing real-font
HUD/inventory fixtures, twenty-field inventory mutations, 20 warmups and 200
measured updates at requested 60 Hz. No own builds overlapped timing. Other
work remained allowed to run as requested, and the process manifests captured
unrelated Cargo/Rust compiler activity. All timings are provisional.

The first batch is strongly contaminated: it reports 52.9% less paired paint
CPU, but unchanged style/layout CPU also drops by 54.1%. Those percentages
cannot be attributed to the candidate. Do not aggregate them into a speedup.

The second batch's inventory medians, milliseconds per update, are:

| Stage | Accepted baseline | Rejected candidate | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.168 | 0.159 | 0.148 |
| Paint recording | 0.213 | 0.211 | 0.194 |
| Complete native CPU | 0.944 | 0.952 | Not equivalent |

Its within-pair paint median is only 1.4% lower, with no complete-CPU gain.
HUD paint is flat and paired complete CPU is 4.2% higher. This batch is not
an isolated-machine control either. Chrome raster-worker CPU is 0.082 ms and
is separate from paint recording; native complete CPU has additional host
work. These results do not justify retaining the experiment as an optimization.

## Evidence and next work

The [tracked receipt](paint-investigation-20261031.json) preserves both full
batches, the corrected attribution, diagnostics and source/evidence hashes.
Local artifacts are under `.utmp/paint-20261031/` and timing runs under
`.utmp/inventory-chrome-20261031/comparison-v29/` and `comparison-v30/`.
The rejected patch remains available for inspection.

The final Agentic executable was rebuilt from the restored accepted source,
and the restored GCC build passes all 17 suites. Neither Unity nor Godot host
source or binaries changed. The preceding combined-metrics full gate remains
the qualification for the accepted engine; this pass did not rerun it.

The next investigation should split the remaining state/decoration cost into
property reads, parsing and geometry work before attempting a broader change.
The corrected profile does not show child sorting or fresh rasterization as
the principal target for this workload.
