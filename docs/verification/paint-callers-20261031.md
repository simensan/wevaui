# Paint caller audit after flex optimization — 2026-10-31

The profiler printed only four frames even when it captured six. This hid
paint callers and made deep-stack attribution incomplete. `weva_bench` now
prints every captured frame on Linux and Windows; sampling, engine behavior,
timing boundaries and allocation counting are unchanged.

## Current-core attribution

A generated diagnostic runs all twenty prepared inventory mutations, cycling
twelve selection states for 60,000 updates after 120 warmups. It links the
current GCC Release core containing `a0943b78`, with the fixed diagnostic font.
All setter statuses, twenty final values/attribute-presence states and missing
assets pass. Sampling forces serial rasterization. It is algorithmic
attribution, not real-font host latency or an optimization result.

The diagnostic increases stack capacity to 48 frames and the site table to
32,768 entries, then prints all sites. The accepted run has **4,201 samples
at 3,391 sites**, with maximum observed depth **26**: neither cap is reached.
The preceding four-output-frame and sixteen-captured-frame runs are preserved
as incomplete evidence and excluded from this table.

**1,948 samples (46.37%)** have `paint_tree` in their stack. Within that group,
1,739 descend through recursive paint, 194 through glyph preparation and 15
through other paint-tree work. Selected inclusive paths are below; inclusive
rows can overlap and must not be summed.

| Inclusive path | Samples | Share of paint samples |
| --- | ---: | ---: |
| Resolved decorations | 278 | 14.27% |
| Text geometry construction | 241 | 12.37% |
| Glyph preparation | 194 | 9.96% |
| Retained draw replay | 182 | 9.34% |
| Border tessellation | 157 | 8.06% |

Selected self samples inside paint: glyph-atlas lookup 176, computed-style
integer lookup 139, property-name lookup 101, and retained-range relocation 80.
Unresolved libc samples remain separate. Compiler inlining and sampling limit
precision; the fixed-font shaping path differs from production hosts.

The next concrete candidate is avoiding repeated decoration/geometry work for
unchanged descendants during selection changes, with inherited color, opacity,
transforms, clipping and texture lifetimes still checked. Inspect invalidation
ownership before adding a cache. Existing glyph-map and generic style-cache
experiments remain rejected; these samples do not overturn their paired results.
Any proposed runtime change must preserve exact renders and demonstrate a
real-font host benefit before retention.

Follow-up: the [pass-local shaping-buffer experiment](shaping-scratch-20261031.md)
reduced allocations but slowed five of six fixed-font pairs, so it was removed
before host rollout. Do not treat fewer allocations as a measured speedup.

## Verification and scope

The benchmark target builds without warnings on GCC and Clang. A real GCC
`--sample-depth=6` run now emits six frames (previously at most four). A Clang
ASan/UBSan benchmark smoke completes without diagnostics. No host code or
runtime core changed, so Unity/Godot rebuilds and the full rendering gate are
not repeated for this output-only tooling correction. Their prior verified
optimized binaries remain applicable. No new speedup is claimed.

The [JSON receipt](paint-callers-20261031.json) contains the complete aggregated
attribution, representative stacks, build/sampling artifact hashes and scope.
Raw evidence is `.utmp/render-audit-20261030/optimized-paint-profile/`, including
the generated source, `selection_profile`, symbols, all sample sites, parser,
build logs and benchmark smokes. Local diagnostic sampling time includes
profiler overhead and must not be compared with host timing reports.
