# Architecture

## Positioned content in collapsed tables

The table paint pass retains ordinary content, cell backgrounds and shared borders
in their existing order, then emits nonnegative positioned layers. Positioned
table-cell backgrounds stay in the ordinary table layer; their content carries
the resolved ancestor paint state into the later pass. Pointer routing uses the
same positioned-layer predicate and reverses the layer order.

An ancestor's captured range cannot be replayed if it excludes a descendant
deferred beyond that range. A pass-local adapter suppresses capture/replay for
those incomplete ancestor ranges, while preserving unaffected cell ranges and
the outer table's complete range. It reads the current box/style inputs and
does not create persistent dirty flags or globally invalidate paint caches.
The ordinary no-change document path still skips painting and allocates nothing.
The clipping correction is installed; remaining table stacking defects are
recorded in [the current reproduction](verification/table-stacking190.json).

Overflow clips retain their owning box, rectangle and scroll offset in an
immutable ancestry chain. Absolute and fixed descendants remove only clips
between themselves and their actual containing block, restoring skipped scroll
offsets. Clip-path polygons remain applicable; rounded overflow polygons carry
an owner so the same filtering rule applies. Pointer routing follows the same
containing-block rule and tests rounded corners analytically.

Paint replay inputs include the active absolute/fixed containing blocks and
clip-chain values. This prevents replay when positioning changes clip applicability
without moving the ancestor. An outgoing paint scope restores the incoming
scissor, including cached and deferred paths. Culling preserves a subtree when
positioned descendants can escape its overflow clip.

## Table stacking source correction

The current source separates the own drawing of an auto-z wrapper from its
positioned descendants. Explicit stacking contexts retain their atomic ordering.
For tables with negative layers, a traversal collects those layers and paints
them before ordinary table content; the ordinary traversal skips the collected
layers. Collection carries the same transforms, overflow ancestry and scroll
offsets as painting. Unchanged tables still use their complete cached range.
Split ancestor ranges are excluded from inner replay, and collection/own-only
passes do not overwrite a complete box capture with partial output.

Pointer routing reverses these layers and considers negative layers only after
ordinary content and the table itself. Internal row/group/column boxes contribute
table backgrounds but are not direct pointer targets; cell events still bubble
through their DOM ancestors. This source correction is awaiting native runtime
qualification. [Evidence](verification/table-stacking191.json).

## Godot binding path reads

Each dictionary segment uses a checked `Variant::get` lookup, avoiding a separate
presence lookup and avoiding insertion of missing keys. Object segments use
`Variant::get_named` and its validity result instead of allocating and scanning
the object's complete property list on every refresh. Existing null values and
missing paths still resolve to empty binding output; control writes retain their
existing type conversion and missing-branch behavior.

This changes lookup work, not freshness: values are read again on each refresh,
so mutations of shared dictionaries and object properties remain visible. No
dictionary-identity shortcut or persistent value cache is introduced.

## Retained host shaping runs

The core host-font adapter retains at most 4,096 shaped runs, keyed by face,
text and quantized size, with full input equality checked on a hash hit. Hits
promote a run to the most-recently-used end; an insertion at capacity removes
only the least recently used run. Stable HUD labels therefore survive a stream
of changing counters or names. Hash collisions replace the conflicting entry
after shaping succeeds, without ever returning a mismatched result.

Intrusive links point into unordered-map values, whose addresses survive rehash.
Lookup promotion and eviction require no list-node allocation or cache scan.
Changing the shaper still clears all runs and both list endpoints because the
inputs have changed. A separate 4 MiB payload budget bounds retained entry storage,
text capacity and glyph capacity per document. Oversized runs still shape but are
not cached. Map buckets and allocator overhead are outside that payload count;
the entry limit bounds their scale. Incremental eviction keeps the cache warm.
`WEVA_FONT_CACHE_LOG` provides opt-in capacity diagnostics, sampled
every 256 insertions (including sampled before/after single-run evictions).

## Godot Control integration

Native font adoption duplicates the Array returned by `Font.get_rids()` before
appending compatibility symbol fonts. Godot shares that array with the source
Font; copying a `TypedArray` handle alone aliases it. Mutating the borrowed
array grew the global default's font chain on every document open or engine-font
toggle. The duplicate owns only the list; font resources and native RID owners
retain their existing lifetimes and resource-change invalidation.

`WevaDocument` derives from `Control`. Native size/anchors/Containers provide
the document viewport, and `_gui_input` owns typing and pointer activation.
`_has_point` consults the core hit region, including select-list rows, so CSS
`pointer-events` participates in Godot's native stacking. Focus notifications
synchronize HTML focus with the viewport; non-wrapping ABI focus steps hand
Tab navigation back to Godot at the document edge. Pointer entry defers HTML
focus to the clicked element, avoiding an unrelated first-field scroll.

An `_input` observer only schedules light dismissal for primary presses.
The GUI handler cancels dismissal when it receives that press. Otherwise the
observer dismisses existing auto popovers/dropdowns after native routing.
The core's transient input version guards deferred work against a native
handler opening a new popup. Manual popovers and dialogs are unaffected.
Scene removal and visibility loss cancel pointer state. Explicit input methods
remain available for games that set `interactive = false`.

Keyboard activation shares `activate_element` with pointer clicks. Space
retains a DOM target until release; focus loss, removal and document reload
cancel it. Radio focus memory is scoped by live form ownership and cleared
with the DOM lifetime. Range reads, painting and pointer/keyboard edits use
one normalization helper. Event-driven value changes propagate through the
existing tracker and binding path; these states add no idle-frame traversal.

IME preedit is an explicit UTF-8 range in the focused control value. The core
retains the original edit snapshot until composition ends and adds one undo
checkpoint only when the final value differs. External value changes end the
stale range. Selection and composition ranges participate in caret repaint
comparison. The host converts Godot character offsets, batches native result
keys before replacing preedit, and transforms measured caret bounds into
window coordinates. Queued events own their full text separately from the
unchanged ABI event struct; hosts retrieve it before polling the next event.
See [IME.md](IME.md) for platform evidence and outstanding compatibility work.

## Retained subtree updates

The optional Godot `WevaView` script loads files and connects a game's change
signal to a deferred binding refresh. Bursts refresh once, with no extra process
callback or idle dictionary traversal. The native document still owns layout,
paint and GUI input.

Templated HTML boolean attributes bind presence. Their source remains in the
owned `BindingTemplates` map while the live attribute is absent, so false → true
can restore it. Literal attributes retain HTML semantics; other attributes stay
strings. This uses the existing binding invalidation and lifetime cleanup.

Modal box changes have a guarded retained-layout transaction. A single dialog
box input can rebuild the modal and its ancestor scaffold while retaining
unchanged absolute/fixed panels under ordinary block ancestors. Input versions
exclude panels touched by any style/paint change. The scratch pass uses the
normal builder, layout and positioning code, then proves equal containing-chain
geometry, fonts, borders and padding before preserving any subtree identity.
Shared layout changes, counters/generated content, floats, sticky/anchor
constraints and other unsupported cases fall back to full layout. Positioned
paint boundaries also validate incoming paint state; box-ID recycling cannot
silently retain the previous ancestor's effects or clipping. Registration uses
per-box flags to avoid quadratic work and an ever-growing candidate list.
The modal transaction also preserves the verified subtrees' layout indexes.
It unindexes discarded ancestors and removes their paint/grid candidates before
box IDs are recycled, then indexes the new scaffold while skipping retained
roots. Later HUD mutations still invalidate through their original input
versions and element/style mappings. `WEVA_MODAL_TRACE=1` reports aggregate
phase timing and accepted/attempted transactions at thread teardown; it is a
diagnostic, not a timing-budget result.

Binding refreshes observe actual DOM mutations during substitution. Changed
text and image sources enter the content-input queue; attributes restyle their
selector scope. Reordering the same unique explicit keys moves existing rows
and queues the containing element's content/order input. Subtree geometry and
intrinsic exports must still pass the normal reuse proof. Membership changes
keep reconstruction. DOM queries use an order cache keyed by the document's
structural input version, independently of stable element handles. The reverse
handle map is updated on registration, removal and reload; removed elements
lose their map entries before their addresses can be reused.
Removed rows lose pointer-keyed state before their addresses can be
reused. Dead computed styles remain owned until the previous box/paint pass is
replaced, then are released instead of accumulating across list refreshes.

Caret, selection and composition changes invalidate the affected controls'
paint inputs, including the old control on a focus move. Dialog box changes
retain their cascade origin. Opening a dialog and focusing a field can therefore
share the final layout; deferred focus reveal uses the new geometry. Retained
grid proxies must not be classified as empty by margin collapsing: their
in-flow children remain in the retained arena until the splice.

On Windows, the host activates IME on target/window changes and moves the
candidate anchor only when its position changes. Focus/window teardown still
deactivates IME. Reassociating an unchanged Windows input context on every
painted caret incurred unnecessary OS work. Other platforms retain the painted
caret refresh needed by the X11 focus handoff.

The Godot host caches parsed binding paths with a bounded lifetime, while
reading shared Dictionary values on every refresh. Small forms resolve their
model elements in one scan; larger forms keep the dynamic fallback.

Godot's ordinary style, text, value and class writes query the live DOM without
flushing layout. A sequence of HUD writes is consumed by the next frame,
explicit update, geometry/style read or geometry-dependent interaction. Those
read and input paths retain their synchronous update behavior. No extra batch
API, retained selector handle or cache key is introduced. After an update that
retains the published draw serial, the host also retains its texture map;
texture views are published only with a new draw list in the core.

Repeated direct-text writes compare direct text children and their original
binding sources before replacing nodes. Element children and their order stay
intact; a manual write of a currently rendered binding value still replaces the
template source. Repeated inline-style writes compare the complete normalized
declaration string before notifying the tracker. Neither no-op clears other
pending changes. Changed direct text queues its owner's layout input for the
next pass, even when computed declarations are unchanged. The existing subtree
proof builds fresh text/line boxes and compares geometry, baselines, intrinsic
exports and parent inputs before splicing them into the retained tree. If the
proof fails, layout falls back to a full rebuild. Removal and document reload
discard queued owners before their DOM lifetime ends; queue capacity is reused.

Image `src` writes enter the same content-input queue, since a new source may
change intrinsic dimensions without changing computed declarations. Equal
source assignments remain no-ops for layout and paint.

Text writes also restyle the selector scope for `:empty`. Sibling-dependent
sheets include the containing element's family, because filtered
`nth-last-child(... of :empty)` can affect preceding siblings. Filtered sibling
ranks bypass the per-element match cache: a sibling's filter match can change
without changing the target's index/count. `:has()` retains the existing full
cascade fallback. Stable HUD labels therefore reuse unrelated layout and paint
without assuming that every text replacement has identical sizing or styles.

Computed styles keep a property-id slot table and allocate raw strings in
stable pages of 16 values as declarations are written. Presence, importance
and parsed-value metadata remain indexed by property id; lazy inheritance,
initial-value lookup and style versions retain their existing contracts.
Growing the slot table or page list cannot move another property's string.
`clear()` resets presence, mappings, parsed values, custom properties and the
inheritance parent while retaining page and string capacity for reuse. Move
and swap transfer page ownership, and clearing a moved-from style is valid.
This trades an extra indirection on reads for fewer string constructions on
cold sparse styles. Retained capacity follows each style's high-water usage.
Declaration enumeration walks the existing occupancy words in ascending id
order and reserves the known declaration count once. It does not scan unused
property slots or change presence/version semantics.
`WEVA_CASCADE_LOG=1` with `WEVA_STAGE_LOG=1` reports cascade subscopes and
metadata/page allocation time; clocks remain disabled by default.

For elapsed update-stage diagnostics without per-update output, set
`WEVA_STAGE_TRACE=1` before starting the process. Each participating thread
allocates one bounded buffer for its last 4,096 updates (224 KiB of records).
It writes `WEVA_STAGE_TRACE_BEGIN`, rows, and `WEVA_STAGE_TRACE_END` to stderr
on thread teardown. Row columns are update sequence, cascade, animation, box
build, layout, paint, and total milliseconds. Early exits retain zero for
unvisited stages; total also includes work after the final stage marker.
Leave `WEVA_STAGE_LOG` unset to avoid its verbose output. The buffer is absent
when tracing is disabled. These are elapsed timers, including scheduling
pauses, and must not be treated as exclusive CPU execution time.
On Windows, each row is followed by `WEVA_STAGE_CYCLES sequence cycles`, the
executing thread's raw cycle delta across that update. Zero means unavailable
(including unsupported platforms). Queries run only with tracing enabled.
Cycle counts include user and kernel execution; compare them as raw counts,
not milliseconds. The processor's cycle accounting can vary with its timer
implementation, so they are diagnostic evidence rather than a conversion to
CPU time. See [Microsoft's API contract](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-querythreadcycletime).
 Abrupt process
termination may lose the trace; the buffer is diagnostic, not persistent logging.

Line-height resolution finds the declaring style through the DOM inheritance
chain. Lengths and percentages use that style's computed font size; numbers
and `normal` use the consuming element's font. Numeric calc/min/max/clamp
expressions retain multiplier semantics. Materialized inherit/unset/rollback
and pseudo values carry a source flag, so identical raw strings with different
bases invalidate the line-height property. Set, unset and clear reset that
flag; move/swap transfer it. No used line-height is cached: parent font and
viewport/root/DPI changes remain live inputs. The source is found before
parsing, so changing an inherited declaration's type cannot reuse the copied
child syntax. Pseudo line-height rollback preserves a lower layer's own value.

The font-size memo also keys on exact viewport width/height, root font size,
root line height and DPI. A fixed pixel-sized parent does not change when
those inputs change, so style version plus resolved parent size alone could
reuse stale vw/rem/rlh/pt results. Context changes now invalidate that memo
without globally clearing styles or their parsed-value caches. The extra
five doubles belong to the style and follow its normal move/swap lifetime;
`clear()` already bumps the style version before any memo can be reused.
An explicitly owned px/number font size depends only on its own declaration:
its memo can return before resolving the parent or comparing context values.
Setting, unsetting or clearing the declaration changes its version and
recomputes that classification. Undeclared and explicitly inherited sizes
forward to the DOM parent's computed size, without a redundant memo at every
inherited link. Authored relative declarations compound along the full style
chain, independently of box ancestry (`display:contents`, anonymous boxes).

Materialized font-size inheritance retains a source flag beside the raw value.
An inherited `2em` and an authored `2em` have different bases despite identical
strings, so changing that flag bumps the style version and is reported as a
font-size change by the cascade diff. This preserves layout invalidation and
`!important` without expanding the cached value into every descendant.
Normal `set`, `unset` and `clear` reset the flag; generated content points to
the originating element's style and uses the same computed-inheritance path.

Border-width resolution reads an owned declaration's existing parsed-value cache.
The cache stores syntax, so font-relative, viewport and physical units still
resolve against current numeric inputs on each call. Setting, unsetting or
clearing a width invalidates its parse through the normal style mutation path.
Inherited and registry-provided values use raw resolution: re-registering an
initial value can change it without advancing this style's version.
`none` and `hidden` border styles skip width resolution. No geometry cache or
additional input-version scheme is introduced. The raw-string resolver remains
available for callers without a style and uses locale-independent number parsing.

Within one `BlockLayout` pass, plain-text containers retain up to three
inline-layout results of at most eight lines each. Intrinsic probes commonly
revisit nonconsecutive widths. Exact width, padding/border origin, style
identity and style input versions select a result; text/font inputs already
belong to that pass's collected items. Active floats, atoms, inline fragments
and larger results keep the normal layout path. Detached lines stay in the
box arena. Reattachment restores their original local positions before button
centering or table-cell alignment runs again. Nothing survives the layout
pass, and no cross-frame invalidation or C ABI changes are introduced.
`WEVA_DISABLE_INLINE_REUSE=1` restores ordinary line construction.
`WEVA_LAYOUT_LOG=1` separates inline collection, atom sizing and line building
as inclusive subscopes of inline layout.

The ABI style walk prepares `::backdrop` only for hosts accepted by the box
builder's shared top-layer predicate. Ordinary elements no longer allocate a
style for the universal UA backdrop rule. A closed host retains any previous
backdrop style without recomputing it; its DOM attribute versions schedule
recomputation and box construction when it reopens. This preserves the style's
address and consumes declaration changes made while the backdrop was absent.
The general pseudo-element cascade API keeps its existing behavior.

Layout now reports the grid roots whose identities and children survive an
ancestor replacement. Paint distinguishes those replacements from actual
style/scroll input changes: the former stop invalidation at retained roots;
the latter continue to propagate through descendants normally.

Each eligible boundary records its incoming absolute position, accumulated
opacity/transform/filter, scissor, geometric clip chain and canvas owner.
Changed values bump the subtree's paint input version before replay is tried.
Clip and filter snapshots compare exact values, not shared-pointer identities
or approximate bounds. Their immutable shared storage survives until the next
boundary snapshot or reset. Full layout/backend changes reset these snapshots
with the ordinary paint ranges. A grid that survives layout can therefore keep
its draw buffers only while its paint inputs also remain equal. Descendant
ranges relocate when earlier siblings add or remove draws; referenced textures
remain retained by the existing texture-cache pass.

ABI minor 12 exposes a parallel `weva_document_draw_versions` array without
changing the `weva_draw` struct or its stride. Each newly collected command
gets a document-lifetime unique, nonzero version. Replayed commands retain
their versions when they move within the draw list. Equal versions identify
identical published command inputs, including geometry, texture ID and effects.
The version array has the same lifetime as the published draw views.

Godot retains converted vertex/color/UV/index arrays for each consecutive draw
batch. An exact match of its ordered command versions reuses those arrays;
changed batch boundaries or commands repack the slot. PackedArray copy-on-write
preserves any earlier submission while the slot changes. Unused slots are
released after drawing. Canvas items, materials, painter order and texture
lookup keep their existing behavior. Every redraw still submits the geometry
to Godot; this cache avoids CPU packing and color conversion, not GPU uploads.
`WEVA_GODOT_DISABLE_PACK_CACHE=1` bypasses reuse for pixel/performance comparisons.

The glyph prepass uses the same subtree input versions before walking text.
A previously prepared subtree can skip shaping and atlas lookups while its
inputs and the atlas's slot version stay equal. Clearing glyph slots bumps
that version; adding glyphs and replacing the GPU texture preserve it. Moving
or clipping a retained grid can therefore require new paint without requiring
another glyph walk. Text/font mutations and full layout/provider resets still
prepare the affected subtree. Popup labels retain their separate prepass.
The `PaintReuse` hooks have defaults for existing implementations, and the C
ABI is unchanged. `WEVA_DISABLE_GLYPH_REUSE=1` restores the full glyph walk.

Font-provider changes invalidate glyph slots as well as measured/shaped runs.
The atlas key's face/glyph IDs belong to the installed provider and may be
reused by its replacement. The previous uploaded texture survives until the
next paint publishes a new atlas. Renderer changes release that handle through
its old owner and retain CPU glyphs for upload to the new renderer; destruction
releases the last handle. Godot theme/resource notifications feed the existing
external-font input path and schedule new boxes. Clean frames do not resolve
theme fonts or rebuild font tables.
During renderer replacement, the collecting backend moves its published
texture map nodes into temporary storage before releasing handles. Pixel views
remain valid across multiple replacements until the next paint starts; no
pixel copy or work on a clean frame is added.

Host font shaping uses the original callback table plus an optional positioned
callback registered separately in ABI minor 11. Changing it clears shaped-run
and measurement caches and schedules new boxes; replacing the table removes
the override. Godot preserves native placement offsets and maps glyph IDs to
their exact font RID, including automatic fallbacks. Its count/fill memo is
keyed by immutable face, actual TextServer size and source bytes. The original
table's binary layout remains unchanged. See [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).

Godot's synthetic primary fonts share an eight-entry LRU across backends.
Exact file bytes, emboldening strength, italic transform and the owning
TextServer identify each immutable independent font. The key never borrows a
source RID or relies on a mutable FontFile's identity. Each backend retains
its own shared reference, so eviction or another document's destruction cannot
free a font still in use. Cache entries keep their TextServer alive and release
RIDs through that owner. Scene-module shutdown clears the pool before engine
teardown. Fallback RIDs remain borrowed from each document's current resources.
The local variant key distinguishes the existing 0.6 and 0.9 synthesis
strengths; a boolean bold key previously made weights 700/800 depend on request
order. `WEVA_GODOT_DISABLE_VARIANT_CACHE=1` bypasses cross-document sharing.

Each immutable synthetic font retains a bounded LRU of shaped runs: 128
entries and 4,096 allocated glyph slots, with at most 512 source bytes and
512 glyphs per run. Entries compare exact source bytes, rounded TextServer
size and the ordered font RID list (at most 64 fonts). The host only marks
its private compatibility fallback chain immutable; resource fallback chains
are excluded. A run using any fallback glyph is also excluded. Native glyph
indices are remapped to each receiving backend's opaque handles. Shared runs
live with their owning synthetic font, so eviction cannot free a live font.
`WEVA_GODOT_DISABLE_SHAPE_CACHE=1` bypasses this reuse independently.

Form controls own lazy `FormControlState` storage on their DOM element. Live
values, checkedness and selectedness have separate dirty flags from markup
defaults. DOM mutation hooks maintain clean defaults and group selection;
`FormStateChanged` bubbles to the document and marks the affected elements.
`StyleMap` compares each control's `form_version` and adds its existing style
to `changes`: textarea requires layout, other control state requires paint.
Option changes also bump their select's input version for the closed caption.
Selector caches already include live pseudo-state in their keys.

Form visuals and selector inputs are consumed separately. The DOM observer
queues each control's versioned visual inputs, but marks a cascade scope only
when its state differs from the state last styled. An option's selectedness
therefore restyles that option, while the containing select only consumes its
caption/row version. Value events do not create another select-subtree scope.
Queued controls are discarded on removal and reload. The normal attribute,
sibling-selector and `:has()` propagation paths remain responsible for their
broader dependencies.

Before a scoped cascade, overlapping dirty roots are coalesced using their
actual ancestor/sibling reach. Paint requests such as scrolling can coexist
with a scoped cascade; they do not require a full style walk. The stage log
reports visited elements and match-cache hits/misses, and `WEVA_STYLE_LOG`
reports the changed property IDs/values for diagnosis.

Text painting already reads `color` from the retained style. The unused box
color snapshot has been removed, making color a paint-only property. Parent
color changes invalidate descendant paint inputs through the existing tracker,
including text decoration and `currentColor`, without reflow. Full-render
comparisons cover inherited color, selection, sibling rules, `:has()`, reset,
removal and reload. Reload also clears the focus chain before any old DOM
pointer can become a dirty root.

Select display mode is another versioned form input: changing `size` or
`multiple` can require new child boxes while leaving computed CSS and current
selectedness unchanged. Dropdown child suppression belongs to box building;
option computed display remains ordinary CSS. Listbox keyboard-row changes
have independent input versions consumed by the style/paint change tracker,
so Ctrl+arrows repaint the select without giving an option DOM focus. Gesture
anchors and selectedness snapshots belong to the document and are discarded
on reset, load or removal. Idle frames do not scan this state.

Held listbox and text-selection autoscroll share a separate input clock. The
original C update advances both clocks; Godot uses monotonic elapsed time for
input and simulation delta for CSS, so paused/scaled game time does not stall
selection. Layout frames restore ancestor
scroll offsets before evaluating the pointer against the new list viewport.
A changed scroll offset feeds the list box into existing paint input-version
propagation, invalidating descendants and ancestors while retaining unrelated
branches. Reaching a boundary publishes no new draws or scroll events. The
host requests a redraw only when the core draw serial changes during a tick.

Text gestures preserve the source anchor and extend the endpoint as scrolling
exposes characters. Single-line fields use their internal text offset;
textareas use their box scroll offsets. Neither path edits form state or undo
history. After pointer release, range selection retains its viewport until an
explicit caret-follow action. Capture ownership is discarded on focus loss,
hide, disable, reset, detach or reload. The text-field box enters the same
paint input-version propagation as a scrolling list, with retained/full-frame
comparisons covering nested scrolling, wrapping and live geometry changes.

Option text/label and optgroup label changes increment `form_label_version`.
The style tracker consumes it separately from selection versions, so a label
rebuilds affected text layout while a choice only repaints. Options own their
display text in the box arena; they preserve DOM text and submitted values.
Popup rows have no boxes, so `PaintContext` optionally supplies their computed
styles. Popup glyph preparation uses the same face/size as drawing, and the
panel clips its rows after drawing its border.

Each document owns a select typeahead session with a steady-clock timeout.
Input invokes the embedded ICU collation search; idle updates do not visit it.
Focus/removal/load clear its DOM target. The ICU wrapper owns every UTF-16
buffer retained by its search handle, including short labels. The pinned
English data profile and isolated symbols are described in
[third_party/icu](../third_party/icu/README.md).

Textarea runs view the live value buffer and carry `source_control` through
inline splitting and subtree imports. Equal assignments preserve that buffer;
changed values bump the version before reflow. Pool resets clear the source
pointer with the rest of the box. These paths add no idle-frame traversal.
The host batches all reset model writes before refreshing bindings. See
[FORM_STATE.md](FORM_STATE.md) for the public live/default contract.

The C ABI lifecycle consumes `StyleMap::changes`, the actual cascade and
animation output differences. `IncrementalLayout` keeps principal box IDs and
the intrinsic contributions exported by the last layout. It builds and lays
out a fresh subtree against the retained containing-block chain, accepting it
only when its exports stay equal. Every candidate is checked before any
splice. External constraints and dependencies that cannot be isolated use the
full baseline. Index updates stay within replaced subtrees; full layout over
unchanged DOM reuses index storage, while structure resets identities.

Flex/grid boxes also retain `ParentLayoutInput`, the natural dimensions and
intrinsic widths measured before their parent allocated the final size. A
probe first compares those original inputs, then compares the output at the
old allocated width. Used widths alone are insufficient: shrinking can hide
changed natural widths and otherwise leave neighbouring items stale. The
record owns no pointers and follows the box's pool reset/copy contract.

Parent input capture measures min-content and max-content in one recursive
walk, sharing child classification and constraint resolution. Single-size
queries specialize away the unused result. The calculation reads current
geometry every time; it adds no cache, retained key or pool-reset fields, and
does not weaken the comparisons required before an incremental splice.

`BoxTree` itself is move-only: its text views can reference its own deque and
imported-string map. A shallow tree copy would duplicate that storage without
rebinding the views. This matters when a vector of prepared replacements
grows; its reallocation must move the trees and retain their text owners.

Unchanged positioned ancestors and siblings do not prevent a static subtree
from being replaced. Positioned descendants still do; floats, sticky boxes
and named anchors retain a document-wide fallback because their dependencies
can cross subtree boundaries. `WEVA_LAYOUT_LOG=1` identifies rejected probes
and accepted roots.

A clean grid can remain in the retained tree while a probe reflows its
surrounding flex allocation. Grid roots carry the input version of their last
layout; changed descendants and ancestors invalidate that version. The box
builder leaves eligible grid children deferred. Layout reuses their height,
intrinsic widths and visual overflow only when the newly resolved width and
box edges match. A changed allocation materializes the children before normal
layout continues. This path requires an auto-height grid under an ordinary
block, with no positioned descendants, subgrid, or percentage-height dependency
on an auto-height parent. External inputs still force full layout.

The transaction detaches retained grid roots before releasing their old
ancestors, then reconnects them under the replacement wrappers. Their child
IDs and owned text remain intact; indexing skips these retained descendants.
Probe size limits count the boxes actually rebuilt, allowing a small flex
column around a large clean grid to qualify. The enclosing root must still
pass the same outer-geometry, baseline and intrinsic-export checks. Repaint
remains necessary when that reflow moves the grid or changes its clip.

Corner-radius painting uses each longhand's existing `ComputedStyle::parsed`
entry, including the two components of elliptical radii. Writes, unset and
clear invalidate that entry through the existing style contract. Only syntax
is retained: each use resolves percentages, font units and calculations from
the current border-box dimensions and length context. Zero radii avoid
populating parse entries. The shared CSS value parser also handles whitespace
and comments between components, replacing the former literal-space split.

Replaced images (`<img>`) reuse their raster textures through `TextureCache`.
The key owns the source URL, resolved object-fit/position layer inputs, exact
content and texel dimensions, length-resolution context and accumulated color
filter. ImageStore content versions distinguish reader/base-path resets and
replacement stores without retaining decoded-image pointers. Versions change
only when resource inputs reset; looking up or loading an image does not bump
them. Identical images share a texture, while position, opacity and clipping
remain per-draw work. End-of-pass retention and eviction use the existing
texture ownership contract.

The public base-path and asset-reader setters schedule layout on changed
resource inputs, preserving published views until the next update. Background,
blurred-background and border-image texture keys include the same resource
version, so replacing asset bytes cannot reuse old raster pixels.

Painting and hit testing share `ChildPaintOrder`. It checks the current
sibling sequence and traverses the existing links forwards or backwards when
they already follow paint order. A reordered container uses one entry vector;
sorting includes the original sequence as the final tie-breaker. The former
four bucket vectors, stable-sort scratch and output vector are unnecessary.
The view lasts only for the walk, with no retained tree pointers or new cache
keys. Separate ordering/allocation checks cover mixed stacking groups,
flex/grid items, sibling reordering and reverse hit testing.

Geometry builders reserve known append sizes before emitting vertices and
indices. `Mesh::reserve_append` retains geometric growth when many shapes are
combined. Text uses bounded 64-glyph stack batches of atlas slots to count only
quads with ink; slots are owned by stable atlas map nodes and never survive the
call. These reservations add no cache or retained lifetime, and preserve
geometry values, order and clipping. A separate allocation-budget executable
covers rounded geometry, repeated appends and visible/empty glyph runs.

Clip preparation compacts its owned polygon copy in place when dropping
consecutive duplicates, then reserves the maximum triangle-piece count before
ear clipping. The caller's polygon stays unchanged for containment tests.
Rounded clip outlines reserve their exact point count from the clamped radii
and segment count. Preparation remains lazy at the existing `ClipNode`
boundary; triangulation order and vertex interpolation are unchanged. The
allocation guard emits a digest of prepared metadata and clipped geometry for
comparison with a frozen library, covering both windings, concave/degenerate
polygons and duplicate points.

Paint submission consumes temporary meshes through an rvalue-reference
parameter. Colour filters, transforms and opacity mutate those owned vertices
before the vectors move into `RenderInterface::render_mesh`. Clipping retains
its existing screen-space operation order and replaces the owned mesh at each
cut; it needs no initial copy. The backdrop region also consumes its temporary
shape. Callers finish geometry measurements before submission, including the
input text width used by decorations. The backend API and retained cache keys
are unchanged. Ownership checks keep an earlier backend's draws alive through
later paints and compare combined effects against a frozen library.

The collecting backend owns preceding command buffers and per-box command
ranges. Its key is a monotonically increasing input version, propagated from
changed styles/replaced boxes to descendants and ancestors. Glyph-atlas
identity is another input. Cache hits transfer buffers and retain textures by
handle so sweeping cannot release a visible cached background. External paint
inputs invalidate the root version; clean frames still return before pipeline
work. Custom backend callbacks use the existing full path.

The incremental corpus gate forces full recomputation in a control document
by round-tripping viewport size before updating, without resetting its DOM,
animation clock or glyph history. See PERFORMANCE.md for timings and fallback
cases.

Geometric clipping preserves a triangle's original vertices whenever all
three lie inside every inward half-plane of a convex clip. This applies to
textured, colored and antialiased triangles: a clip with no geometric effect
must not introduce new attribute interpolation. Shared vertices are classified
lazily for each mesh/clip invocation. Boundary crossings and concave clips
retain the triangulated clipping path. Direct-render pixel checks enforce
that an irrelevant clip leaves the original triangle's appearance unchanged.
Passing triangles also retain their input vertex sharing. A map local to the
clip call records the appended output index for each source index; coincident
vertices with different attributes stay distinct. This reduces copied and
uploaded vertices without changing triangle order, interpolation or cache keys.

Three layers, with the seam deliberately placed so a second host costs a shim
rather than a second engine.

```
┌──────────────────────────────────────────────────────────┐
│ libweva  (C++17, host-agnostic, -fno-exceptions)         │
│   parse → cascade → layout → paint display list          │
│   version-keyed invalidation; per-pass arenas            │
└───────────────┬──────────────────────────────────────────┘
                │ include/weva_c.h   (C linkage, POD, versioned)
        ┌───────┴────────┐
        │                │
┌───────▼──────┐  ┌──────▼──────────────┐
│ hosts/godot  │  │ hosts/unity         │
│ GDExtension  │  │ P/Invoke shim       │
│ (first)      │  │ (later)             │
└──────────────┘  └─────────────────────┘
```

`libweva` must not reference Godot or Unity types anywhere. That is enforceable
— the C# core proved the discipline works (47 of 682 files touched UnityEngine,
and the whole surface fitted in a 104-line stub) — but here it is structural:
the core links against neither engine.

## Size container queries

`CascadeEngine` retains conditional rules with chains of compiled size queries.
`ContainerQueryState` owns each element's complete query-result signature; that
signature joins the existing cascade match key. A stylesheet generation separates
old query indices from newly compiled rules. Equal result sets can reuse cached
matches when a panel crosses back over a breakpoint.

After layout, the C ABI refreshes these inputs from eligible ancestor content
boxes, then restyles only changed roots through `StyleMap::walk`. Style differences
use the existing layout/paint invalidation path. Nested containers settle before
the update publishes its frame. Clean idle frames perform no query refresh, and
warm refreshes retain their maps/vectors. Removed nodes are pruned. The standalone
dump runs the same input evaluation and settlement with full rebuilding.

Containment suppresses intrinsic contributions as well as shrink-to-fit sizes;
otherwise flex/grid parents would recover the very content width that an inline
size container must hide. Font-relative query values use the container's resolved
font and the document root's font; viewport lengths keep the viewport basis.

The size-query subset includes named/unnamed selection, physical/logical size
features, orientation/ratio, ranges, boolean expressions and supported length math.
Style queries, scroll-state queries and container-relative units remain outside
this implementation. See `examples/frontier_camp/CHROME_PARITY.md` for current
browser findings and `test_container_query_allocations.cpp` for the focused
allocation/performance guard.

## What carries over unchanged

These are the parts of the C# design worth preserving verbatim:

* **Version-keyed invalidation.** Cache keys are input versions, not heuristic
  dirty bits; clean subtrees skip every stage. This is why a `:hover` flip costs
  0.08 ms against 8.3 ms for a full cascade. Port the contract as written in
  `AGENTS.md` §"Layout / paint cache invariants" — it is the performance
  architecture.
* **The paint display list as a decoupling layer.** Layout emits commands; a
  backend consumes them. Keep this. Only its *altitude* changes (below).
* **The engine-neutral core boundary** itself — the set of files
  `Tools/BaselineGen` already compiles is the port's scope definition.

## What gets re-cut on the way over

### 1. The render backend drops to geometry + effects

Weva's `IRenderBackend` has **12 required methods at a semantic altitude** —
`FillRect`, `StrokeBorder`, `DrawText`, `DrawShadow`, `PushFilter`… so every
backend reimplements rounded-rect SDF coverage, gradient evaluation, shadow blur
and per-edge border styles. The cost is visible: the URP backend is 9,082 LOC
plus 3,904 shader lines, and `SoftwareRasterizer` is 1,592 LOC and still renders
glyphs as blocks and gradients as flat fills.

RmlUi decomposes everything to **indexed triangles** plus optional layer/filter/
shader ops, requires 8 methods, and ships 6 renderers × 6 platforms off it. That
is the altitude to target.

```cpp
struct RenderInterface {
    // Required
    virtual GeometryHandle compile_geometry(std::span<const Vertex>,
                                            std::span<const uint32_t>) = 0;
    virtual void render_geometry(GeometryHandle, Vec2 translation, TextureHandle) = 0;
    virtual void release_geometry(GeometryHandle) = 0;
    virtual TextureHandle load_texture(std::string_view path, Vec2i* out_size) = 0;
    virtual TextureHandle generate_texture(std::span<const uint8_t>, Vec2i size) = 0;
    virtual void release_texture(TextureHandle) = 0;
    virtual void set_scissor(const Recti*) = 0;   // nullptr = disable

    // Optional — default no-op, feature degrades rather than breaks
    virtual void set_transform(const Mat2x3*) {}
    virtual LayerHandle push_layer() { return {}; }
    virtual void composite_layers(LayerHandle src, LayerHandle dst, BlendMode,
                                  std::span<const FilterHandle>) {}
    virtual void pop_layer() {}
    virtual FilterHandle compile_filter(FilterKind, const FilterParams&) { return {}; }
    virtual void release_filter(FilterHandle) {}
};
```

The core takes on the tessellation and effect decomposition it currently pushes
onto backends: rounded-rect and border geometry, gradient meshing, shadow
expansion, clip-path shapes. That work moves once, into `libweva/src/paint/`,
instead of being written again per backend.

**Keep the batched über-shader as one backend, not as the interface shape.** The
57-float4 instance record and per-instance clip rects are a genuinely strong
design for a single fast target — they just shouldn't dictate what every backend
must implement. A batching backend can coalesce compiled geometry itself.

Exit test for this decision: the software backend should be a few hundred lines,
not 1,592, and should render gradients and real glyphs.

### 2. Fonts become a real pluggable interface

The C# rasterizer binds `FontEngine.TryRenderGlyphsToTexture` **by reflection
into undocumented internals** of `UnityEngine.TextCoreTextEngineModule.dll`,
with a TextMeshPro fallback ladder. The interfaces above it (`IFontMetrics`,
`IGlyphMetrics`, `FontLoader.IFaceLoader`) are the right shape; the
implementation underneath is not portable and never was.

```cpp
struct FontInterface {
    virtual FaceHandle load_face(std::span<const uint8_t> ttf, int index) = 0;
    virtual bool face_metrics(FaceHandle, double px, FaceMetrics* out) = 0;
    virtual bool glyph_index(FaceHandle, uint32_t codepoint, uint32_t* out) = 0;
    virtual bool glyph_metrics(FaceHandle, uint32_t glyph, double px, GlyphMetrics* out) = 0;
    virtual bool rasterize(FaceHandle, uint32_t glyph, double px, RenderMode,
                           Bitmap* out) = 0;
    virtual void shape(FaceHandle, std::string_view utf8, double px,
                       std::vector<ShapedGlyph>* out) = 0;
};
```

Two implementations to choose between (decide in Phase 5, not now):

* **FreeType + HarfBuzz directly** — `libweva` keeps owning its atlas, so
  `GlyphAtlasPacker` / `SdfGlyphAtlasAdapter` port with little change. Adds
  dependencies; gives full control and works identically on every host.
* **Godot `TextServer`** — HarfBuzz + ICU + FreeType already in the engine, MSDF
  support built in, no new dependencies. But Godot owns the atlas, so
  `glyph_rect` lookups source from its cache and the Unity host would need a
  different implementation — which weakens the single-core argument.

The interface above is deliberately compatible with either.

### 3. Data binding: the reflection does not port, the template layer did

The original decision here was that binding would not be ported at all: the C#
reflection in `Runtime/Binding/` reads C# object graphs, does not translate, and
a GDScript world would use Godot's own introspection instead.

Half of that held and half was reversed, and the split is the useful part.

**Not ported, as planned:** the reflection. Reading a C# object graph, or a
GDScript one, stays on the host side. Neither the core nor the ABI knows what an
object is.

**Ported after all:** the template layer above it — `{{ path }}` interpolation,
`data-each` / `data-key` row repetition, `data-model`, and handler dispatch. That
is markup semantics, not language reflection, and leaving it to each host meant
writing it twice and having it drift. It lives in `libweva/src/binding.cpp`
(~500 lines).

The seam is `weva_binding_source` (ABI, `weva_c.h`): a callback table the host
fills with "read this path", "count this collection". The core walks the
document, asks for the paths it finds, and writes the results into the DOM;
`weva_document_refresh_bindings` returns how many nodes changed. The host keeps
the reflection and answers path lookups against whatever it has — Godot's
`Object::get` over `Variant` in `weva_view.gd`, a C# dictionary graph in
`Runtime/Native/NativeBindings.cs`.

So `libweva` does have a binding layer; what it does not have is any idea where
the values come from. Verified by `binding_tests.gd` (186 checks) and
`NativeBindingTests`.

## Stylesheets in the markup

The cascade sees, in this order: the UA sheet, the host's sheets
(`weva_document_set_css` / `add_css`, in the order given), the document's own
`<style>` blocks in document order (a `media` attribute is evaluated against
the viewport and colour scheme, so they are read again on a resize or scheme
change), then one scoped sheet per component. A component's sheet is the
`<style>` inside its `<template id>`: `component_scoping.cpp` rewrites its
selectors so the rightmost compound demands `[data-uui-scope="<id>"]` and
`:host` / `:host(...)` become `[data-uui-host="<id>"]`, the expander stamps a
clone's elements with the scope before slot projection (slotted light-dom
keeps its own attributes) and the host with the host marker, and the sheet
itself is not cloned into instances. A reload or an appended fragment that
carries a template replaces that component's sheet. The attribute names and
the rewritten selector text are byte-identical to the C# `ScopeMarkers` /
`SelectorScoper`, so a scoped sheet reads the same on either side.

## The C ABI

Narrow by construction. Sketch, not final:

```c
uint32_t          weva_abi_version(void);
weva_document_t   weva_document_create(const weva_config*);
void              weva_document_destroy(weva_document_t);
weva_status       weva_document_load_html(weva_document_t, const char*, size_t);
weva_status       weva_document_add_css(weva_document_t, const char*, size_t);
void              weva_document_set_viewport(weva_document_t, int w, int h);
weva_status       weva_document_update(weva_document_t, double dt_seconds);
const weva_paint* weva_document_paint(weva_document_t, size_t* out_count);
weva_element_t    weva_document_query(weva_document_t, const char* selector);
```

Hosts register their `RenderInterface` and `FontInterface` through function-
pointer tables mirroring the C++ vtables. Everything crossing the boundary is
POD; nothing crossing it allocates without a paired release.

Do not design the Unity side of this yet — but do not design it *out*. The rule
for Phase 1–6 is simply that no core API takes or returns a Godot type.

ABI minor 13 adds `weva_element_set_selection_without_focus` using the existing
byte-based anchor/caret convention. Existing focusing selection calls remain
available; the Godot host exposes both methods.


ABI minor 14 adds `weva_document_register_font_family(doc, family, face)`.
Register a family after installing the font backend; CSS `font-family` stacks
then select that face for measurement, glyph painting and caret geometry.
The core copies the family name and owns its metrics adapter; the host owns the
font resource and must keep it alive. A zero face removes the registration.
Repeating an unchanged mapping does not invalidate layout. Registrations survive
HTML reload and shaper replacement, which rebuilds all family measurement caches.
Installing a font backend clears registrations because face IDs belong to that
backend. Hosts must register their families again after such a replacement.
This is a native-face registration API, not an implementation of CSS `@font-face`.

ABI minor 25 adds `weva_document_font_faces(doc, buffer, capacity)`: the
compiled stylesheets' `@font-face` rules as `family<TAB>source<TAB>weight<TAB>style`
lines, the source being the first `url()` entry resolved against the base path
like an image. The core still loads no fonts; a host loads each source and
calls `weva_document_register_font_family`. `@font-face` no longer appears in
the unsupported-rule diagnostics.

ABI minor 25 also adds `weva_element_tag_name(doc, element, buffer, capacity)`,
the lowercase tag of a handle with the attribute buffer convention. The Godot
host reads it for gamepad navigation: an accept on a field is Enter, on a
control Space, and vertical pad movement stays inside a `<select>`,
`<textarea>` or number field.

ABI minor 26 adds `weva_document_layout_dump(doc, source, buffer, capacity)`:
the layout dump the differential oracle compares (`docs/ORACLE.md`), produced
from the document's own box tree by the walk the `weva_dump` tool now shares
(`weva/layout_dump.h`), so a host's dump is the tool's dump by construction
and any remaining difference is the host's fonts or setup. With it,
`weva_document_set_font_leading_rounding(doc, rounds)`: a host font backend's
half-leading rounds down to whole pixels by default, as real font layout does;
a synthetic face that exists to be compared with the oracle's arithmetic (the
Unity host's `SyntheticFontBackend`) passes 0 to keep it exact. The built-in
stub is unaffected.

ABI minor 27 adds the inspector surface an Elements panel needs beyond a
selector query: `weva_element_parent` and `weva_element_children` (element
children in document order, two-call), `weva_element_box_model` (margin, border
and padding edges plus the content box behind `weva_element_bounds`' border
box), `weva_element_matched_rules` (every declaration that applies, sheet rules
and the style attribute, in cascade order with the winner per property marked,
shorthands expanded as the cascade applies them) and
`weva_element_computed_style_all` (every registered property resolved, then the
custom properties in scope). Tab-separated lines, the `css_diagnostics` buffer
convention.

ABI minor 28 adds `weva_document_set_color_scheme(doc, dark)`: the host's
colour-scheme preference, which `@media (prefers-color-scheme)` and
`light-dark()` follow (an element's own inherited `color-scheme` wins over
it). A change recompiles the conditional rules and restyles, like a resize.
The Godot source exposes `register_font_family(name, Font)` and watches resource changes. Installed Windows preview105 includes minor 14; its package and native exports pass the recorded verification.

ABI minor 29 adds `weva_document_cursor(doc, buffer, capacity)` and
`weva_document_cursor_at(doc, x, y, ...)`: the CSS `cursor` under the pointer as
the keyword it settles to (`auto` resolved to `text`, `pointer` or `default`; a
`url()` list reduced to its fallback), so a host can show the matching shape.
The Godot node answers Godot's cursor-shape query with it (`follow_css_cursor`,
on by default); the Unity wrapper exposes `NativeDocument.Cursor` for the host
to map.

ABI minor 30 adds `weva_document_stats(doc, &stats)` -- the last update's stage
timings, element/box/draw/texture counts, the paint pass's texture-cache hits
and misses and the cascade's running totals, for a host's stats window (Godot
`get_stats()`, Unity `NativeDocument.Stats()`) -- and
`weva_document_element_at_devtools(doc, x, y)`, the hit test an inspector wants:
`pointer-events: none`, `visibility: hidden` and modal inertness hide nothing
from it (`HitTestOptions` on `element_at_point`).

ABI minor 31 adds `weva_document_boxes(doc, out, capacity)`: the whole box tree
in tree order, anonymous, line and text boxes included, each with its parent
index, kind, owning element (a text run names the element whose text it is),
border box in document coordinates (scroll not applied), margin/border/padding
edges, own scroll offset and a text box's run
-- what a devtools outline overlay draws from (Godot `get_box_tree()`, Unity
`NativeDocument.Boxes()`).

ABI minor 32 adds `weva_document_reload_html(doc, html, length)`: the markup is
diffed onto the live document (HotReload/DomDiffer.cs ported: keyed by `id` /
`data-key`, else positional by tag), so matched elements keep their handles,
focus, scroll, form values and transitions while attributes, text and the
unmatched subtrees change. The hot-reload path in either host should use it
(Godot `reload_html()`, Unity `NativeDocument.ReloadHtml`).

ABI minor 33 adds `blend_mode` to `weva_draw` (a `weva_blend_mode`, the sixteen CSS
Compositing modes): `mix-blend-mode` rides on every draw of the element's
subtree, told to the render interface through `set_blend_mode`. Hosts render
the modes a blend state can express -- Godot maps multiply to MUL and the
brightening modes to ADD on a child canvas item with a CanvasItemMaterial; the
Unity mesh shader takes multiply, screen, darken and lighten as blend states --
and draw the rest normally.

ABI minor 34 adds `weva_document_html_diagnostics(doc, buffer, capacity)`: the parse
errors the last load recovered from (stray and mismatched end tags, end tags on
void elements, elements open at the end of input), one `line:column: message`
per line, from a diagnostics sink on `ParseOptions` (Godot
`get_html_diagnostics()`, Unity `NativeDocument.HtmlDiagnostics`).

ABI minor 35 adds change notification: `weva_document_changed_elements` lists the
elements the last update restyled with how far each change reached (paint,
layout, boxes -- the cascade's own invalidation, mapped to handles), and
`weva_document_structure_version` moves whenever elements come or go (Godot
`get_changed_elements()` / `get_structure_version()`, Unity
`NativeDocument.ChangedElements()` / `StructureVersion`).

ABI minor 36 adds `weva_document_set_safe_area_insets(doc, top, right, bottom,
left)`: the values `env(safe-area-inset-*)` resolve to, per document (the env()
table moved from a process singleton into the cascade engine). Godot
`set_safe_area_insets()` / `follow_display_safe_area`, Unity
`NativeDocument.SetSafeAreaInsets` / `WevaNativeDocument.FollowScreenSafeArea`.

Bidirectional text (no ABI change): `bidi.cpp` runs ICU's ubidi over a
paragraph's inline items -- `direction` as the paragraph level, `unicode-bidi`
on inline boxes as the matching control characters -- splits text runs where
the embedding level changes and places each line's fragments in visual order
(UAX #9 L2) after justification, before alignment. A left-to-right paragraph
with no right-to-left text is untouched. Each run the host shapes is one
direction; TextServer shapes a right-to-left run on its own, TextCore does not.

ABI minor 37 extends `weva_document_font_faces` with a fifth field: the whole
`src` list in the author's order, `url:<resolved path>` and `local:<name>`
entries separated by `|`, and lists a rule with only `local()` sources (its
source field empty). Both hosts try the entries first to last, a `local()`
name being an installed font (Godot `OS.get_system_font_path`, Unity
`Font.GetOSInstalledFontNames` + `CreateDynamicFontFromOSFont`).
