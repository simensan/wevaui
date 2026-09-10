# Godot text shaping

## Theme fonts and live resource changes

`WevaDocument` resolves the `font` theme item through Godot's Control API.
Parent/project themes, node font overrides and theme type variations select
the document's default font. For example:

```gdscript
ui.add_theme_font_override("font", preload("res://fonts/interface.ttf"))
```

Use ordinary [Control theme font overrides](https://docs.godotengine.org/en/stable/classes/class_control.html#class-control-method-add-theme-font-override)
or assign a Theme with a default font. CSS continues to determine font size
and line height. Register project Font resources for named CSS families:

```gdscript
ui.register_font_family("Camp", preload("res://fonts/interface.ttf"))
```

Then use `font-family: Camp` on the relevant elements. CSS family stacks select
registered faces; matching is case-insensitive. Registering the same name replaces
its resource, and registering `null` removes it. Registrations survive HTML reloads
and observe live Font changes. Native tests cover these operations and fallback
to the document font after removal. CSS `@font-face` loading remains unsupported.

The host retains the selected Font resource and observes its `changed` signal.
Changes refresh shaping, measurement, glyph bitmaps and layout even when CSS
time is paused or the resource keeps the same identity. FontVariation spacing
and explicit resource fallbacks are preserved. Compatibility symbol fonts are
appended only to the global default; appending them to a project font could
preempt its explicit bitmap fallback on Linux. Normal resource properties
notify automatically. Code editing FontFile's low-level glyph/texture caches
must call `emit_changed()` after those edits, as those setters do not notify.

Opening, toggling or destroying documents leaves the Font resource's native
font list unchanged. Compatibility fallbacks are appended to an owned copy.
Previously `Font.get_rids()` returned a shared array and each document appended
to it again, changing the font list seen by other native controls as well.

The C ABI also clears the glyph atlas when any font table is installed again,
including the same table and face ID. The previous texture stays published
until the next update. Switching render backends releases the atlas through
its old owner and uploads its retained CPU pixels to the new owner. Document
destruction releases the last atlas texture. These fixes change no ABI layout.
The collecting backend retains published CPU texture buffers through renderer
replacement until the next paint, including repeated registrations before an
update. Moving their map nodes preserves pixel pointers without copying them.
An ASan control reproduces a heap-use-after-free without this lifetime fix.

`check_theme_fonts.py --godot ... --library ... --render` runs an isolated
project with default and generated bitmap FontFile resources: 75 headless checks and 79
rendered checks pass on Windows/Linux Godot 4.7.2. They cover theme inheritance,
overrides, type variations, live metrics/bitmap changes, FontVariation,
fallback changes, shared resources, stub toggling, paused updates, reparenting
and teardown. Forty checks cover default-font array ownership across repeated
opens, engine-font toggles and destruction while a peer remains live; preview38
fails 20 of them. Live mutated pixels match a fresh document. Preview24 failed
16 of the original 35 headless checks. C ABI regressions cover bitmap refresh, texture
ownership and the lifetime of published pixel views across renderer changes.

## Font size after viewport changes

Viewport-relative font sizes now update when the Control is resized, including
empty elements sized in `em`. For example, an empty box with
`font-size:10vw;width:1em;height:1em` grows from 10x10 to 20x20 when the document
width changes from 100 to 200 pixels. Previously a cached size could survive
that resize even though a freshly loaded document used the correct size.

The font-size memo includes viewport dimensions, root font/line metrics and
DPI alongside its style and parent-size inputs. Reusing a core layout context
after changing these inputs therefore agrees with a fresh resolution, including
`calc()` and `clamp()`. This changes cache validity, not unit interpretation.
Explicit pixel sizes retain a fast path keyed by their own style version.

`check_theme_fonts.py --render` also runs `font_size_tests.tscn`: 208 geometry
checks and 336 checks with rendering, using both the stub and native fonts.
It compares resized documents with fresh controls and checks their painted
rectangle footprint. The matching Chrome oracle is
`tools/oracle/check_font_size_context_chrome.cjs`. Nested relative-font
inheritance still has the C# chain-limit behavior documented in the core;
browser font-size conformance is not complete.

## Synthetic font ownership

For primary fonts with file data, the adapter creates independent synthetic
fonts for bold and italic. Ordinary bold (600–799) and heavier bold (800+)
use the existing 0.6 and 0.9 emboldening strengths. The cache key now includes
that strength: previously 700 and 800 shared whichever variant was requested
first. This corrects order-dependent text without changing the synthesis
policy or claiming full variable-font weight selection.

An eight-entry LRU shares immutable synthetic fonts across documents with
identical file bytes, strength, italic transform and TextServer owner. Live
backends retain references even after an entry is evicted. Font resource
changes still invalidate the document's normal font inputs; byte comparisons
prevent a replacement file from reusing an incompatible synthetic font.
Module shutdown empties the shared pool while its TextServers remain alive.
`WEVA_GODOT_DISABLE_VARIANT_CACHE=1` bypasses sharing for comparisons.

Each immutable synthetic font also retains up to 128 shaped runs and 4,096
allocated glyph slots. Reuse requires the exact source bytes, rounded native
pixel size and ordered font chain. Runs are limited to 512 source bytes and
512 glyphs, with at most 64 fonts in the input chain. Only runs entirely
supplied by the synthetic primary qualify. The host admits a single-font
input or its private, unchanged compatibility fallbacks; arbitrary resource
fallback chains keep normal shaping. Receiving backends remap native glyph
indices into their own opaque handles. Eviction changes reuse, not validity;
normal resource invalidation and font ownership still apply.
`WEVA_GODOT_DISABLE_SHAPE_CACHE=1` bypasses shared runs for comparisons, and
`WEVA_FONT_LOG=1` reports shared hits separately from native shaping calls.

Synthetic primary fonts use regular shaping advances with styled rasterization.
Runs containing automatic fallbacks or positioned marks also obtain styled
native shaping, preserving fallback font selection and accent attachment;
primary advances are matched by source cluster, glyph index and occurrence.
Ordinary primary-only labels still require one native shaping pass. Fallback
and positioned-mark runs can require two passes on a cache miss.

Native font tests now pass 19,682 checks on Windows with caches enabled and
with both variant and shared-run caches disabled,
including both weight-request orders, italic combinations, untouched regular
glyphs, different file data, cache eviction, other-document destruction and
backend replacement. Shared-run checks compare positioned glyphs, byte
clusters, native metrics and bitmap bytes against independently configured
TextServer fonts, combining independent regular primary advances with styled
native offsets, fallback glyphs and raster bytes. They cover mixed Arabic,
Latin and emoji runs, repeated combining marks, distinct receiving-handle maps, fractional size
rounding, combining marks, Arabic, emoji, invisible controls, fallback chains,
font replacement and runs exceeding the reuse bounds. Both sides initialize
raster state before comparing metrics: a native bitmap-font miss can change
provisional metrics on its first raster request. The old boolean variant key
fails eight checks in the earlier regression. An independently
instrumented adapter/test extension now passes 19,816 Linux checks under
ASan/UBSan with caches enabled and again with both caches disabled. Only the
adapter and test objects are instrumented in this native probe; the current
core library, engine and dependencies are not instrumented here. Leak detection
is disabled. The core has its separate complete sanitizer suite.

The harness loads the extension directly through `GDExtensionManager` and
instantiates the test class through `ClassDB`. Its fixture reads raw font bytes,
so editor import is unnecessary. This avoids coupling adapter checks to an
uninstrumented editor import/shutdown crash observed with the sanitizer loader.
An optional `--import-frames` diagnostic retains editor coverage separately.
Every completed process records its command and exit code beside the raw log.
The sanitizer wrapper removes `RTLD_DEEPBIND`, disables the engine crash handler
and places the loader shim before ASan with `verify_asan_link_order=0`.
No loader shim or engine modification is included in the addon.

Evidence: `.utmp/parity68/check-font70-sanitized5.log` and
`/root/weva/font70-sanitized2/{cached5,uncached5}`. The same direct-loading harness
passes 19,682 Windows checks (`.utmp/parity68/adapter-direct`).

## Positioned glyphs and automatic fallbacks

The adapter preserves TextServer's per-shaped-glyph placement offsets in
addition to each bitmap's bearings. Previously those offsets were lost at
the C ABI, moving combining marks away from their native positions. TextServer
source clusters index UTF-32 characters; the adapter now converts them to the
UTF-8 byte offsets required by the core. An opaque glyph registry retains the
exact font chosen by TextServer, including automatic system fallbacks that
were previously read from the wrong primary font.

ABI minor 11 adds `weva_shaped_glyph` and `weva_document_set_font_shaper` as
separate declarations. The original `weva_font_backend` struct is unchanged.
The callback uses its installed table's `user_data`, supports count/fill calls,
and can be removed with null. Replacing the font table also removes it.
Changing the callback clears measured/shaped runs and schedules new layout
and paint; repeating the same registration preserves the warm caches.

Godot's offsets grow downward in y; the core's grow upward from the baseline.
The adapter flips that sign. A one-run cache bridges the sizing/fill calls,
keyed by immutable face ID, actual integer TextServer size and source bytes.
Glyph IDs borrow their font RIDs from the existing font owners and TextServer's
system-font cache; they do not alter the explicit fallback order.

The optional `weva_font_tests` extension compares native glyph positions,
UTF-8 clusters, advances, bitmap metrics and coverage with TextServer, then
checks the document's resulting glyph quads. Fixtures cover combining accents,
Arabic, Devanagari, emoji, multibyte Latin text and invisible controls at two
sizes. The synthetic C ABI regression also checks runtime callback changes,
legacy restoration and re-registration. Tests run in isolated projects and
are not packaged with the addon.

This fixes horizontal glyph placement and fallback identity. Full paragraph
bidi layout, bidi caret navigation, vertical text and project font-family
selection still need separate work. Font availability remains platform-specific.

## Stock Godot 4.7.2 limitation

### Windows patched-engine bundle, 2026-09-08

A clean Windows editor built from `ed1daf0bf001b61586d9930840f2f1394092c079`
with only the script-iterator patch now passes all six isolated text-safety
cases, the original 60-emoji-run autoscroll stress (81 checks), 8,456 host
checks and 19,682 native font-adapter checks. The rendered Frontier comparison
also passes all 1,276 geometry and 33 interaction checks. This build retains
the normal 3D/rendering features, including the Direct3D and accessibility
dependencies; it is not the earlier stripped Linux diagnostic template.

Evidence is under `.utmp/safe-engine71`: `build.json`, `editor-text-safety`,
`autoscroll-stress.json`, `host-checks/verification.json`, `adapter-check` and
`frontier-chrome/verification.json`. The source archive SHA-256 is
`e607e9985e1c201bc9cdc1aec8a120f0c3f53b9603f1f828e2b748534a2471ef`.
The matching debug and release templates also pass all six cases in real exported
project, with ICU data embedded and the source project hidden
(`exported-text-debug-2/result.json` and `exported-text-release-2/result.json`). The fixture enables
`internationalization/locale/include_text_server_data`; it does not use an
external ICU file. The probe's Turkish-case check verifies that support data
actually loaded. Standard templates disable path overrides, so this checks the
packaged game rather than enabling diagnostic command-line behavior.

Full addon verification with the explicit custom templates also passes debug,
release and embedded-pack export, relocation and exact project/export pixel
comparisons (`addon-exports.log`). `check_export.py` accepts `--debug-template`
and `--release-template` together with `--native` and records their hashes.

The local portable bundle is
`.utmp/safe-engine71/weva-godot-4.7.2-scriptfix71-windows-x86_64.zip`, SHA-256
`160e47f1c04888fb84cb05099950acb7ec4f1339014d01304503eb69dbc96978`.
It contains the editor, both templates, runtime DLLs, licenses, patch and build
provenance. All archived file hashes/CRCs pass, and the relocated portable
editor repeats all six text-safety cases. It does not install a global engine.
Use its editor and both custom templates together, with ICU data enabled.
Stock Godot 4.7.2 remains affected; this Windows evidence does not clear other
platforms or the addon's remaining product-readiness requirements.

The reusable `tools/godot-text-shaping-repro/check_exports.py` now repeats this
verification for both actual templates. It passes all 12 cases with the portable
patched bundle. A negative control using that editor with stock templates fails
five of six cases in each build mode, proving editor success cannot mask unsafe
exports. Evidence: `.utmp/safe-engine71/{versioned-export-gate,stock-template-control}`.
`check.sh` runs this gate and supplies the same optional custom-template paths
to the addon export verifier.

Frontier Camp now embeds TextServer data and exercises the long Unicode value
through real state bindings and native editing. Its current verified consumer
project/export passes 92 headless and 98 rendered assertions, including matching
project/export pixels (`.utmp/safe-engine71/frontier-integrated/verification.json`).

Stock Godot 4.7.2 can return corrupt glyph positions or crash when a string
contains more than 32 separate emoji runs within one script run. For example,
`"á😀b".repeat(33)` crosses the limit. This affects Weva's engine-font path
and reproduces with native TextServer calls in an empty project without Weva.
Without a workaround it blocks applications accepting unrestricted Unicode
text. The addon does not contain an engine fix; since the shaping-pieces
candidate its font adapter avoids the defect, as described below.

The cause is in Godot's `modules/text_server_adv/script_iterator.cpp`:
the first growth of its emoji stack allocates a larger buffer without copying
the existing entries. The buffer is then freed inside the loop over scripts,
leaving a dangling pointer for later script runs. The parentheses stack has
the same missing-copy problem when it grows past 128 entries.

A candidate engine patch preserves both stacks on first growth, keeps the
emoji buffer alive across script runs and releases it on the error path.
The standalone project, runner, patch and reproduction instructions are in
`godot-port/tools/godot-text-shaping-repro/` in the source repository.
The patch has not been submitted upstream. The private diagnostic build is
not a distributed or supported replacement Godot editor.

On 2026-09-06, official Windows/Linux Godot 4.7.2 and a matched unpatched Linux
build failed five of six isolated cases, including process crashes. The
matched patched build passed all six cases (18 glyph-count, source-cluster
and advance checks), with ICU support data loaded. It also passed all 81
native Weva autoscroll checks using the original 60-emoji-run stress input.

The 2026-09-07 Windows recheck uses the installed preview56 DLL in a private
project. Ordinary autoscroll passes all 81 assertions; the 60-run stress input
fails five selection/editing assertions. The standalone probe again corrupts
the 33/65/256-run results and crashes both mixed-script processes. Its 32-run
control passes the three text assertions. All of these Windows runs also
report this environment's certificate-store error, retained in the raw logs;
the strict runner treats that error as a failure too.

The main `check.sh` now runs the standalone probe as an engine text-safety
gate. Its `--release` mode additionally rejects skipped gates. The standalone
runner uses a fresh private project, explicit engine logs and a JSON report
with executable/probe hashes and process exits. This prevents ordinary host
success from hiding the known failure; it does not change runtime shaping.

### Adapter workaround: shaping in pieces (2026-09-10)

The font adapter (`hosts/godot/src/godot_font.cpp`) now counts emoji sub-runs
and open brackets with the same ICU character properties and rules as the
engine's script iterator, and shapes a run in separate TextServer buffers
whenever one buffer would need more than 32 emoji sub-runs or 128 open
brackets. Each split lands exactly where the engine starts a new emoji sub-run
or pushes a bracket, so no ligature or kerning pair crosses it; pieces of a
right-to-left run are appended in reverse so the glyphs stay in visual order,
and glyph character indices are rebased to the whole run. Ordinary text keeps
the single-buffer path unchanged. The embedded ICU data now includes
`uemoji.icu` (14,400 bytes), which the emoji properties require.

Evidence on Windows, official stock Godot 4.7.1 (mono) and the patched 4.7.2
editor, adapter library
`8ce46c8432a34b616edef56edcdc19679c5a8b6bf11f82924dbda8d65fa0f74b`
(see [shaping-pieces receipt](verification/stock-engine-shaping.json) for the
exact digest and logs):

- The standalone reproduction still fails five of six cases on the stock
  editor (33/65/256 emoji runs corrupt; both mixed-script cases crash). The
  engine defect is unchanged.
- The native font-adapter suite gains five cases past the limits: 33 and 65
  emoji sub-runs, sub-runs continuing into a second script, 129 open
  brackets and a right-to-left run. Each long text must read exactly as its
  three-unit reference tiled, kerning included. All 19,692 checks pass on
  both the patched and the stock editor.
- The Frontier Camp lifecycle harness with 60-unit mixed-script name churn
  through real bindings aborts on the stock editor with the previous library
  (`FATAL: Index p_index = 242 is out of bounds`), and completes with the new
  one.
- The text autoscroll fixture passes its 81 checks in every combination,
  including stress mode with the previous library, so it does not discriminate
  this defect.

Text that reaches TextServer through other paths (native Godot controls, the
theme font in native overlays) is not shaped by the adapter and still needs
the engine patch. Other platforms and engine versions are unverified.
