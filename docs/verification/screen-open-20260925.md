# Faster screen opening — September 25, 2026

Opening a screen is almost all rasterizing. On the corpus's slowest pages,
paint was 80–90% of the first update, and most of paint was gradients,
shadows and blurs rasterized on the CPU, one texture after another. Three
commits attack that, each verified on its own:

| Commit | Change | Pixels |
|---|---|---|
| `1998aeb` | A pass's textures are rasterized side by side, at the end of paint | Byte-identical |
| `d7c7c05` | Blurs of sigma ≥ 17 texels are drawn on a coarser grid and resampled | Within 4 levels; 3 on the corpus |
| `1c41876` | Rasterized textures are kept across documents (ABI minor 45) | Byte-identical |

## Results

`weva_bench <page> <css> 5 --cold`, best of two runs, on the 4-core Linux
container the audit used: 1280×720, core only, no host fonts or GPU. The
"before" column is `db8c13c`, built in a separate worktree. **Cold** is a
first opening. **Reopen** is the same page created again in the same
process (`--reopen`): what Unity does when a menu is re-enabled, since
`WevaDocument.OnDisable` destroys the native document.

The twenty slowest pages:

| Page | Before (ms) | Cold (ms) | Speed-up | Reopen (ms) | Speed-up |
|---|---:|---:|---:|---:|---:|
| glass | 86.6 | 42.3 | 2.05x | 11.3 | 7.7x |
| randhtml | 57.1 | 53.5 | 1.07x | 27.2 | 2.1x |
| hud | 54.9 | 42.2 | 1.30x | 8.3 | 6.6x |
| neon | 53.3 | 20.9 | 2.54x | 6.6 | 8.1x |
| match3 | 49.6 | 32.8 | 1.51x | 12.6 | 3.9x |
| episode-stats | 37.3 | 38.9 | 0.96x | 5.5 | 6.8x |
| quests | 34.2 | 33.1 | 1.03x | 9.6 | 3.6x |
| match3-endgame | 31.3 | 22.3 | 1.40x | 7.9 | 4.0x |
| weva-landing | 30.6 | 20.1 | 1.52x | 8.9 | 3.4x |
| stock-dashboard | 29.3 | 28.8 | 1.02x | 10.6 | 2.8x |
| level-select | 27.5 | 18.0 | 1.52x | 6.6 | 4.1x |
| menu | 25.1 | 25.2 | 0.99x | 9.9 | 2.5x |
| particles | 23.8 | 19.5 | 1.22x | 13.4 | 1.8x |
| load-game | 21.3 | 19.6 | 1.09x | 5.9 | 3.6x |
| dialogue | 21.2 | 20.0 | 1.06x | 6.1 | 3.5x |
| form-demo | 19.2 | 15.3 | 1.25x | 5.8 | 3.3x |
| combat-hud | 18.3 | 17.8 | 1.03x | 7.6 | 2.4x |
| layout-stress | 18.2 | 19.7 | 0.93x | 19.1 | 1.0x |
| nook-dialogue | 17.4 | 16.3 | 1.07x | 3.3 | 5.3x |
| inventory | 16.8 | 17.7 | 0.95x | 7.2 | 2.3x |

Across 46 pages, the sum is 849 ms before, 696 ms cold and 301 ms reopened.
Pages with a few large textures and no wide blur, such as episode-stats, menu
and inventory, are unchanged within this machine's run-to-run noise (about
±1.5 ms; repeated runs of vendor and story-bubble overlap in both
directions). layout-stress is layout-bound, and randhtml is roughly half
cascade and layout, so neither gains much from paint work.

## What each change does

**Raster jobs.** Paint resolves a background, shadow or blur on the paint
thread (`prepare_background`), takes the texture's id from the collecting
backend (`reserve_texture`), and queues the pixel work. `paint_tree` runs the
queue before returning. A job larger than a thread's share of the remaining
work runs alone and splits its rows; the rest share the threads. A first
version that let jobs parse CSS crashed six samples intermittently: the CSS
parser keeps process-wide scratch state. Jobs now never parse, and
ThreadSanitizer is clean on the core suite and all 47 samples.

**Coarse blurs.** A Gaussian of sigma s carries nothing finer than about s,
so hud's `filter: blur(60px)` spent 22 of its 50 ms on detail the blur then
removed. Blurs of sigma ≥ 17 texels are rasterized and blurred on a grid
scaled so the blur there is exactly three box passes of radius 8, then
resampled bilinearly, in premultiplied alpha, to the texture's own size.
Hosts see unchanged texture sizes, which matters because the Godot host
samples with nearest filtering. An outer shadow's punch-out stays at full
resolution.

**Shared textures.** Shadows, and backgrounds and filter blurs without images
or font-relative units, are kept process-wide after their document goes, in
a 32 MiB least-recently-used cache. Their keys include everything that
decides the pixels, plus the viewport, root font size and resolution.
`weva_set_raster_cache_limit` sets the bound; 0 disables it.

## Evidence

- **Byte identity (jobs, shared cache).** A C-ABI tool hashed every draw and
  every texture's pixels after each of three updates, for all 47 samples.
  The job queue matched `db8c13c` in five threaded runs and one run with
  `WEVA_RASTER_THREADS=1`. With the cache, each sample opened twice in one
  process, and all 47 opened twice with the cache warm from every page, match
  runs with the cache off. `test_queued_rasters_match_inline` compares queued
  pixels with the inline path a host render backend takes.
  `test_shared_raster_cache` pins reopen identity, the viewport in the key, the
  exclusion of `ch` units and the bound.
- **Coarse blur accuracy.** `test_reduced_blur_matches_full` holds flat
  shadows and gradient filters at sigma 18, 30 and 60 within 4 levels of the
  texel-for-texel blur. At sigma 18 the texel-for-texel reference is itself
  a Gaussian of 18.5. A half-texel misalignment fails the test at 6–10 levels.
  Across the 47 `weva_render` sample renders, the largest change is 3 levels,
  and under 0.001% of channels move more than 2. glass, neon and hud were
  inspected side by side with an 80× difference image: only quantization
  contours in soft falloff, with no displaced edges or halos.
  `visual_rank_soft.py` reports the same distance to Chrome's screenshots, on
  every one of the 37 pages that have one.
- **Gates.** gcc and clang (0 warnings): 507,885 checks, 0 failures. Clang
  and GCC ASan/UBSan: 16 of 16 suites, with 507,879 checks, since the small-stack test
  skips itself there. ThreadSanitizer: 507,885 checks with no reports. ctest
  14 of 14. The Chrome layout oracle passes all 334 captures within 1.5 px,
  with nothing excused. The Godot extension and the Unity plugin build.
  `weva_core_load_test` passes and exports both new entry points.
  `WevaNative.g.cs` is regenerated and passes `--check`.

## Not verified here

Unity and Godot were not run: no editor is installed in this container. The
hosts read the same draw list and textures as before, and texture sizes are
unchanged, so neither host needs code changes. The Unity package's generated
bindings expect minor 45, so a locally built plugin must be rebuilt, which
`check.sh` does. Neither host exposes the cache bound yet. It is a
process-wide ABI call a game can make directly.

## Reproduce

```sh
cmake --build build-rel
cd Tools/oracle/corpus/samples
../../../../build-rel/Tools/weva_bench/weva_bench glass.html glass.css 5 --cold
../../../../build-rel/Tools/weva_bench/weva_bench glass.html glass.css 5 --reopen
WEVA_RASTER_JOB_LOG=1 ../../../../build-rel/Tools/weva_bench/weva_bench hud.html hud.css 1 --cold
python3 ../../visual_rank_soft.py ../../../../build-rel/Tools/weva_render/weva_render
```
