# Shared-core selection attribution — 2026-10-31

The next shared-core candidate is glyph-atlas lookup, with computed-style reads
remaining a broader cost. This audit profiles the complete prepared inventory
selection sequence; it changes no production behavior and claims no speedup.

## Diagnostic boundary

A generated diagnostic derives from the current `Tools/weva_bench/main.cpp`.
It resolves all 20 targets once, performs 120 warmup selections, then cycles
the twelve prepared states through C ABI text/attribute setters and zero-delta
updates. Setter return codes are checked on every iteration; the final twenty
values and attribute-presence states are checked, as are missing assets.
All checks passed. Prepared PNG paths are translated to WSL paths without
changing the selection values or content.

The executable links the current GCC Release core after rebuilding its target.
It uses the **fixed diagnostic font backend**, not Unity FontEngine, Godot
TextServer or Agentic's font backend. It is suitable for finding shared
algorithmic work, not predicting host text cost or comparing host frame times.
The source generator, generated C++, executable, symbol table and inputs are
retained under `.utmp/render-audit-20261030/`, principally in
`core-selection-profile/`.

## Evidence

Two 60,000-update sampling runs use the existing SIGPROF sampler, one with a
single frame and one with four frames. Sampling forces serial raster work.
Samples include the setter/update loop; reported benchmark milliseconds time
only update and include profiler overhead. They are not baseline latency data.
The attribution script maps executable addresses to demangled symbols from
`nm -n -C`, then combines instruction sites belonging to a function.

The one-frame run retained **5,733 samples across 2,880 sites**, below the
4,096-site capacity:

| Function | Self samples | Share of retained samples |
|---|---:|---:|
| `ComputedStyle::get(int)` | 543 | 9.47% |
| `GlyphAtlas::get` | 288 | 5.02% |
| `paint_recursive` | 216 | 3.77% |
| `CssPropertyRegistry::id_of` | 179 | 3.12% |
| `update_document` | 130 | 2.27% |
| `BoxTree::create` | 122 | 2.13% |

These are self-time samples across the whole selection loop, not percentages
of paint alone. Unresolved libc work remains separate. The four-frame run hit
the 4,096-site limit; its retained stacks are qualitative caller evidence only,
not a complete percentage distribution. Raw samples remain available.

A separate final-pass allocation trace reports 952 allocations / 264,660
requested bytes. Two leading stacks account for 418 allocations (43.9% of all
952), both growing shaped-glyph vectors from `StubFont::shape`, once during
preparation and once during geometry building. This is strongly backend-sensitive;
do not infer the same allocation count in the production font hosts. Only the
largest twelve allocation stacks are printed, so the attribution JSON's
`represented` subtotal is smaller than the total allocation count.

The coarse `WEVA_PAINT_LOG` run is retained too. It is not used for precise
latency claims: the rounded counters and synchronous diagnostic output perturb
the measurement, including the reported residual time.

## Next action and limits

`GlyphAtlas` currently uses an ordered map keyed by face, glyph and quantized
size. Measure an alternative lookup structure while preserving slot-address
stability, exact size quantization, atlas clear/version semantics and pixel
output. Even eliminating its entire sampled cost would only address about 5%
of this diagnostic loop; a much larger overall gain is not established here.

Do not repeat the prior generic inherited-style/property-ID memoization
experiments without stronger evidence: prior receipts and source comments
record cases where those caches slowed the actual workloads.

Any accepted core change needs GCC/Clang and affected core tests, Chrome checks,
both host rebuilds and rendered validation, followed by host performance runs
with real fonts. This diagnostic alone does not establish those results.

The adjacent receipt hashes accepted artifacts, current core inputs and the
profile generator. No production tests were rerun because this pass modifies
only verification documentation; the existing core benchmark was rebuilt and
the generated diagnostic compiled successfully.
