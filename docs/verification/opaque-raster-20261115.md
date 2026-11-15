# Opaque gradient raster cost

A single opaque, normally blended gradient covering the painting area now
samples directly into RGBA texels. It does not composite against a backdrop
that it replaces completely. Transparency, masks, image layers, tiled boundaries
and supersampled hard edges retain the general path. Interpolation also avoids
dividing by an alpha of exactly one. Neither texture resolution nor CSS behavior
changes.

The new regression compares direct rasters with the same gradients beneath a
transparent top layer, which forces general multilayer compositing. All 27
image comparisons are byte-identical, including diagonal, axial, radial,
hinted, repeated and hard-stop gradients at three resolutions. Existing blend,
mask, shared-cache and parallel-raster tests also pass.

Windows MSVC Release, WSL GCC Release and WSL Clang Release pass the core suite
(600,590 checks on MSVC) and CTest. All three final builds report zero warnings.
This also removes an unused hit-test string lookup overload and simplifies the
click diagnostic's redundant enum/integer conditional. No ABI change.

Seven alternating before/after pairs, each in a fresh Windows process, measured
the Agentic CPU document profiler with the host game's upgrade picker/controller
fixture at 1920x1080. No samples were discarded. Median cold load decreased from
18.750 to 16.845 ms; first paint from 11.755 to 10.364 ms. This excludes GPU work
and does not establish a hitch-free game frame. Compilation and tests had
finished before measurement. Raw inputs, binary hashes and all samples are
retained at `<workspace>/Cache/hitches-gradient-comparison.json`.

The full Unity/Godot host suites and macOS were not exercised for this change.

## Transparent subtree painting

The paint traversal now stops at an accumulated opacity of zero before generating
gradients, shadows or descendant draws. Up-front glyph preparation, layout and hit testing remain
active. Retained painting records the empty subtree and uses its normal style
and ancestor-input invalidation when opacity changes.

The ABI regression covers cold transparency without texture-cache misses,
an opaque child and fixed descendant beneath a transparent parent, pointer hit
testing, and repeated zero/partial/full opacity changes. Every revealed frame
matches a freshly built control document. Existing animated-transform and
incremental-paint cases pass. GCC, Clang and MSVC core builds have zero warnings
and pass CTest. No ABI change; the host suites remain outside this check.

## Focus invalidation scope

Focus changes now mark only ancestors that a `:focus-within` selector can
observe, while the old and new controls keep their own focus/caret updates.
Clearing focus after a control becomes hidden uses the same scoped cascade as
ordinary state changes instead of an unconditional second full-page walk.
Sibling, relational, nested and root-reaching selectors retain their reach.

The regression compares focused/hidden-control frames with freshly built
controls for local focus, ancestor, sibling, root, `:has()` and nested `:not()`
rules. A page with 250 unrelated rows verifies local changes visit fewer than
32 elements. The full GCC, Clang and MSVC core suites pass with zero build
warnings. No C ABI change.

## Large image sampling

Single normally blended image layers covering a transparent painting area now
resolve their box/bilinear filter footprints once per axis. Large artwork no
longer repeats the same coordinate division, clipping and weights for every
texel. Source taps, premultiplied filtering, summation order and the compositor's
float round trip are preserved. The bounded five-tap axis tables cover enlargement
and footprints up to four source pixels; wider minification, masks, blends, tile
boundaries and layered backgrounds retain the existing general sampler.

The regression compares 108 random-alpha rasters against the scalar multilayer
path, including fractional cover/contain/stretch, positioning, repeats, unequal
axis scales and minification on both sides of the optimization boundary. All
are byte-identical. Full GCC, Clang and MSVC core suites pass with zero compiler
warnings. No API, sampling-quality or texture-resolution change.

An explicit four-channel accumulator loop is needed for MSVC to vectorize this
path; the scalar-variable spelling improved GCC but regressed MSVC. Eight
alternating MSVC samples of a 1672x941 random-alpha image covering 1920x1080,
rasterized at 1024x1024, took 4.0–5.2 ms through prepared axes and 11.6–16.7 ms
through the scalar multilayer reference. This is an isolated raster comparison,
not a whole-frame result. Logs are `Cache/hitches-image-channel-bench.log` in
the host game's checkout. The final three compiler suites still pass, with every
equivalence comparison exact. `WEVA_GRADIENT_LOG` identifies this path.

## Showing unchanged panels

A local display change refreshes the descendants' hidden-state cache without
recomputing their unchanged styles. Inherited inputs, explicit inheritance of
non-inherited properties, selector reach and active animations keep their full
style propagation. Folding overlapping invalidation roots also preserves an
independently changed descendant; a parent's local class change cannot hide it.

The regression repeatedly hides/shows 250 unchanged rows, checks fewer than
eight style visits and identical frames against fresh documents, then combines
parent/child mutations and verifies a grandchild's transition still starts after
revealing the panel. The existing animation, focus, selector-reach and incremental
suites also pass. GCC, Clang and MSVC builds have zero compiler warnings and
pass their complete core suites. No C ABI change.

## Cold selector matching

Compiled rules are indexed by one required ID, class or type selector on their
rightmost compound. Rules without a required literal stay in a universal bucket.
Each element gathers those candidate lists, deduplicates repeated class tokens,
restores source order and uses the existing full selector matcher and cascade
ordering. Pseudo alternatives, relational selectors, scopes and containers retain
their normal matching semantics; no inferred literal from an alternative or an
ancestor is used to exclude a rule.

The regression compares indexed selectors with specificity-preserving `:is()`
controls that use the universal bucket. It covers IDs/classes/types, escaped
identifiers, duplicates, attributes, combinators, relational/alternative/negative
selectors, mutations, newly added sheets, clearing and recompilation alongside
400 irrelevant rules. The full GCC, Clang and MSVC suites pass with zero compiler
warnings (601,322 checks on MSVC). No C ABI change.
