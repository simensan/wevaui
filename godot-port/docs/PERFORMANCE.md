# Performance

Measured numbers, and the reasoning behind which ones matter. Re-measure with
the two tools in `tools/` rather than trusting this file — it is a snapshot,
and the point of writing it down is that a later measurement can disagree with
it visibly.

    tools/weva_bench      one layout pass, boxes and allocations
    tools/framebench      idle, hover and animating FRAMES on a real page
    tools/scrollbench     what a scrolled list costs per frame

All figures below: release build, 1280x720, the corpus in
`tools/oracle/corpus/samples`.

## What a frame costs

The number a game feels is the per-frame one, not the load. Four cases, in
rising order of how much work they are:

| case | cost | why |
|---|---|---|
| idle | **0.000 ms** | nothing moved, and the update discovers that before doing anything |
| hover | **0.3–2 ms** | the elements whose `:hover` flipped restyle, and nothing else |
| scroll | **0.5 ms** at 200 rows, **4.2** at 2,000 | offset plus a repaint of what is visible |
| animating | **~1 ms** typical (hud 0.88, combat-hud 1.0) | only what is moving |

`layout-stress` is the exception at **8.3 ms** a frame, and deliberately: it
animates `width`, `font-size` and `padding` on purpose, so the relayout path
runs every frame on 6,926 boxes. It is the worst case the corpus can build,
and it is still inside a 16 ms budget.

## What a load costs

First update, which is parse-free (the HTML is already a tree) but does
everything else for the first time:

| page | boxes | first update | dominated by |
|---|---|---|---|
| sample-menu | 123 | 0.03 ms | — |
| menu | 1,313 | 0.4 ms | — |
| layout-stress | 6,926 | 26 ms | cascade 15, layout 8 |
| vendor | 8,162 | 22 ms | cascade 6, layout 6 |
| hud | 1,932 | **127 ms** | **paint 128** |
| glass | 2,299 | **156 ms** | **paint 156** |

hud and glass are not slow because they are big. They are slow because they
rasterise gradient backgrounds and blur them, and the first paint is where
that happens: five million gradient-stop evaluations and 52 blurs on hud
alone. It is a ONE-TIME cost -- the results go into the texture cache, and the
same page's next paint is 1.7 ms -- so it shows up as a hitch when a menu
opens and never again.

Worth knowing before optimising it: every candidate change (rasterising smooth
gradients at reduced resolution, a colour lookup table instead of walking the
stop list) moves pixels, and the render gates compare against the Godot
backend at 0.00%. A faster gradient has to prove it draws the same picture.

## What was slow and is not

Kept because the reasoning is what stops it coming back, not the number.

**A scroll used to restyle the document.** `only_time` treated any pending
invalidation as a reason to re-run the cascade, so scrolling a 2,000-row list
spent 80 ms a frame in the cascade discovering nothing had changed. Scrolling,
a blinking caret and a moving tooltip all ask for `Invalidation::Paint` and
none of them can change a computed style. 100.6 ms -> 4.2 ms.

**Paint walked every box.** The clip already threw the meshes away, but
building them cost ~2 us a box, so a long list did 46,000 boxes of work to
produce the twenty draws you can see. Boxes carry a visual-overflow rectangle
now and paint returns immediately when a subtree cannot reach the clip.

**Moving the mouse restyled the page.** A hover chain is the element and every
ancestor up to `<body>`, so marking both chains whole put `<body>` in the
changed set on every pointer move -- and a touched-subtree walk from `<body>`
is the whole document. Only the elements that actually flipped are marked now.
66.6 ms -> 11.3 ms on layout-stress, 10.5 -> 0.46 on stats.

**The occupancy flags were a bitset pretending to be a byte array.**
`ComputedStyle::occupied_` was a `std::vector<bool>`, so every presence check
-- and `get` does one for every property read in a layout pass -- paid a shift
and a mask to extract one bit. The C# this ports keeps a `bool[]` beside its
`ulong[]` bitset explicitly "for single-load hot readers", and the header here
said so; `std::vector<bool>` simply is not that. As `uint8_t` it is one load,
and the bitset is still there for the word-at-a-time walks that want it. Two to
nine per cent, measured interleaved: vendor -9.4%, stock-dashboard -6.6%,
randhtml -5.8%, glass -5.6%. It costs a byte per property per style rather
than a bit, about a megabyte on the largest page in the corpus.

**Every box resolved four zeroes it did not have.** `resolve_box_sides_px`
substitutes "0" for an absent side, so a box declaring no margin and no padding
-- which is most of them -- put four zeroes through keyword matching, a
parsed-value lookup and a length resolution to arrive back at zero, twice per
box per pass. Recognising them costs four string compares and was worth 5 to
16 per cent on its own, the largest single change in this sequence.

**font_size_px re-derived a number that had not changed.** It runs several
times for every box and again for the parent, and a `calc()` font-size was
evaluated afresh on each -- CssCalc::evaluate was the second-largest cost in a
randhtml pass, behind only grid layout. The style now remembers the answer with
the parent size it was resolved against, keyed on the style's VERSION so any
write invalidates it automatically. The first attempt invalidated by hand at
each mutation and missed the main `set()`; tying it to the version cannot miss.

**A layout pass hashed its property names over and over.** `get(name)` and
`parsed(name)` each resolve the name themselves, so `resolve_length` and
`font_size_px` hashed the same property twice per call -- and font_size_px runs
several times for every box, recursively for the parent as well. Sampling put
`ComputedStyle::get` and `CssPropertyRegistry::id_of` together at 28% of a pass
on randhtml, ahead of any layout algorithm. The ids are resolved once now,
which is safe because the registry keeps an id stable across re-registration
for exactly this reason.

**An anonymous box parsed four zeroes per pass.** `resolve_box_sides_px`
substitutes "0" for every absent side; "0" is not a keyword, and with a null
style there is no memo to hold the parsed result, so a box with no style
re-parsed the same four zeroes on every layout. 3,192 allocations on randhtml,
more than a fifth of the pass. It answers zero directly now.

**A shorthand's sides were re-parsed per side per pass.** `margin: 10px`'s
parts have no longhand slot to memoise against, so each side parsed its
substring again every layout. They read components of the shorthand's own
memoised parse now.

**size_tracks copied its contributions.** It sorts them, so it cannot take
them by reference -- but taking them by value allocated a fresh vector on
every call, seven per grid. It copies into a pooled buffer now, the same
pattern flex and inline layout already use.

Together, measured at 40 passes so the numbers are outside the +-0.5% noise:

| page | before | after |
|---|---|---|
| match3 | 1.507 ms | **1.349** |
| flex-playground | 1.828 | **1.658** |
| layout-stress | 4.721 | **4.336** |
| randhtml | 3.395 | **3.143** |
| glass | 1.648 | **1.586** |
| vendor | 3.265 | **3.191** |

randhtml's allocations fell 16,663 -> 12,195 with it.

With the id conversion and the font-size memo on top, against the same
baseline:

| page | before | after |
|---|---|---|
| match3 | 1.507 ms | **1.057** |
| randhtml | 3.395 | **2.600** |
| stock-dashboard | 1.205 | **0.934** |
| flex-playground | 1.828 | **1.425** |
| layout-stress | 4.721 | **3.738** |
| glass | 1.648 | **1.310** |
| stats | 1.807 | **1.484** |
| vendor | 3.265 | **2.758** |

About a fifth to a third of a layout pass, depending on the page.

And with the zero fast path on top, against that same baseline:

| page | before | after |
|---|---|---|
| match3 | 1.507 ms | **0.893** |
| stock-dashboard | 1.205 | **0.793** |
| layout-stress | 4.721 | **3.249** |
| flex-playground | 1.828 | **1.268** |
| randhtml | 3.395 | **2.413** |
| vendor | 3.265 | **2.463** |
| glass | 1.648 | **1.242** |
| stats | 1.807 | **1.391** |

A quarter to two fifths of a layout pass.

Worth recording because it cost an hour: the first three of these were chosen
from a 258-sample profile, which cannot resolve a 5% effect, and measured at
20 passes, where the run-to-run spread is +-3% and hid every one of them. The
same measurements at 2,340 samples and 40 passes are unambiguous. Sample first,
and check the noise floor before believing a null result.

**A border image re-sliced itself every frame.** The nine pieces were
rasterized into a fresh texture on every paint, where a layered background has
always gone through the texture cache. Twelve 9-sliced panels on a page with
an animation running cost **5.014 ms a frame**; an ordinary border on the same
page cost 0.072. Cached on the resolved slice, width, repeat and destination
size -- the RESOLVED values, because `border-image-width` is a multiple of the
used border width, so two boxes with identical declarations and different
borders are different pictures. 5.014 ms -> 0.072, which is the plain border's
number. The first paint halved too, since same-sized panels now share one
texture.

**A ComputedStyle sized itself 334 times.** `ensure_capacity` grew six vectors
by one slot per property set, three of them `vector<bool>`. It takes the
registry's full size at once now.

## Measuring it

    tools/layoutbench.sh              # median of 3 sweeps, worst page first
    tools/layoutbench.sh --ab A B     # two binaries, interleaved

Use `--ab` for anything under about ten per cent, and do not compare two
separate runs. The same binary measured twenty minutes apart read 3.052 ms and
3.262 on layout-stress -- seven per cent of drift with no code between them,
larger than most single optimisations. A median defends against variance
within a sweep and not at all against drift between invocations; `--ab`
alternates the two binaries sample by sample, in both orders, so they see the
same machine.

That distinction has already changed two conclusions. Making box sides look
themselves up by id read 1 to 8 per cent SLOWER on its first sweep and was
nearly reverted; it is a small win. Pooling the grid's occupancy vector looked
like a small win by the same loose method and is a consistent 1 to 6 per cent
LOSS under `--ab` -- clearing the borrowed rows costs more than the allocations
it saves, so the grid still builds them fresh.

## The registry's side tables, flattened

`ComputedStyle::get(id)` answers from the box's own values when the property is
set there, and most reads are not: they fall through to `is_inherited(id)`,
then a walk of the ancestors, then `initial_value(id)`. Those two registry
calls are therefore the tail of nearly every property read on nearly every box.

Sampling layout-stress put `is_inherited` at 194 samples on one line -- more
than any other single line in the pass, ahead of flex and inline layout. It was
an out-of-line call doing a bounds check and a `std::vector<bool>` bit extract.
`initial_value` was worse per call: a bounds check, a pointer chase into a
`CssProperty`, and a `std::string`-to-`string_view` conversion.

Both are now inline, off flat arrays: `inherited_` holds bytes rather than
bits, and `initial_views_` holds the views themselves so the fallback return is
one load. Between 4 and 6 per cent on the large pages and up to 9 on the small
ones, with nothing slower anywhere in the corpus:

    layout-stress  -3.8%     glass       -5.8%     particles    -7.8%
    stats          -4.1%     match3      -6.4%     sample-menu  -8.3%
    settings       -5.2%     dialogue    -7.1%     card-component -9.1%

`std::vector<bool>` has now cost measurable time twice in this engine, on
`ComputedStyle::occupied_` and here. It is worth treating as a red flag in any
table an inner loop indexes.

## The last of the by-name property reads

The bulk conversion of `get(style, "name")` to a cached id missed every call
whose property is chosen at runtime -- `get(is, column ? "margin-top" :
"margin-left")` -- because the rewrite matched a literal, and a ternary is not
one. It also missed `positioning.cpp` entirely, which had no id-taking helper
at all and read `will-change`, `contain`, `overflow-x`/`-y`, `z-index`,
`outline-width`, `filter` and `margin` by name on every box in the tree.

Those are now ids as well, the ternaries picking between two constants instead
of two strings. Nothing in the corpus got slower and the large pages moved
again:

    stats       -7.4%     glass         -6.2%     quests    -8.4%
    flex-play   -4.9%     grid-play     -4.7%     dialogue  -7.6%
    vendor      -4.6%     layout-stress -3.7%     randhtml  -3.0%

Together with the registry flattening above, a layout pass of layout-stress is
down from 3.16 ms to 2.67 ms.

## Caller attribution, and the two things it found

Line-level attribution names the callee. `id_of` was still visible after every
by-name read had supposedly been converted, and `id_of` is not where the fix
goes. The sampler now aggregates the innermost address WITH its caller
(`WEVA_PAIRS` in the scratch `timesites.cpp`), which answered it in one run:

`resolve_border_edges` takes its property names as lambda PARAMETERS --
`edge("border-top-style", "border-top-width")` -- so the textual rewrite, which
matched `get(style, "literal")`, could not see them. Eight name hashes per box
per pass, and the largest single contributor left to `id_of`.

`is_border_box` was a cross-translation-unit call from block, flex, grid,
inline, table and positioning, several times per box, to read one byte's worth
of answer. Now inline in the header, with the property id as a namespace-scope
inline variable so there is no function-local static guard to check either.

    layout-stress -6.2%   glass  -5.1%   match3    -7.4%
    vendor        -4.2%   quests -4.0%   particles -7.0%
    stats         -3.4%   randhtml -3.3%

flex-playground read +1.9% on the five-sweep A/B and -2.4% on an eleven-sweep
one restricted to it. Five sweeps is not always enough to call a two per cent
move, even interleaved.

## Box is 528 bytes, and that is not the problem

A layout pass of layout-stress walks 6,926 boxes, 3.6 MB of them, which does
not fit in L2 -- and sampling put `BoxTree::operator[]` at about a tenth of the
pass. The obvious read is that the struct is too fat: 38 of those bytes are
padding around interleaved bools, and another 72 are `std::optional` offsets
that almost every box leaves empty. Splitting it hot/cold would be days of
work across every layout file.

Before starting, the cheap experiment: add 64 bytes of dead padding to `Box`
and measure. A twelve per cent size increase cost **nothing** -- layout-stress
-0.3%, vendor +0.3%, randhtml +1.4%, and the two samples that moved 2.5% are
small ones inside the noise. So the samples on `operator[]` are the index and
the loads, not misses that a smaller struct would avoid, and the whole refactor
is off the table for a fraction of a per cent.

Worth doing this way round whenever the fix is expensive and the diagnosis is
an inference: make the problem WORSE first, cheaply, and see if the metric
notices.

## The font-size memo was checked one step too late

`font_size_px` memoises its result on the style, keyed on the style's version
and the parent's resolved size. The check sat AFTER the declaration was read --
and `font-size` is inherited, so that read walks the ancestor chain whenever
the box does not set a size of its own, which is most boxes. Every memo hit
was still paying for an O(depth) walk, twice per box, for a value the memo
already had.

Moving the check above the read is the whole change. Layout writes no style,
so after the first call per style the walk is pure waste.

    stock-dashboard -6.1%   stats  -4.7%   vendor        -3.9%
    glass           -4.6%   flex-playground -2.1%   layout-stress -2.1%

The key stays sound across inheritance even though it is the style's OWN
version: what an ancestor's font-size change moves is the parent's resolved
size, and that is the other half of the key. This is the distinction the
reverted inherit memo got wrong -- it cached an ancestor's identity under a
key that an ancestor could not move.

layout-stress and grid-playground both read as small REGRESSIONS on the
five-sweep A/B (+0.9% and +1.6%) and as -2.1% and -1.0% on an eleven-sweep run
restricted to them. That is the second time a five-sweep reading has inverted;
confirm anything under about three per cent before believing it either way.

## There is no redundant relayout to remove

The standing assumption through several rounds of this work was that flex and
grid re-lay their children more than they need to, and that the win waiting to
be had was structural rather than another few per cent off a property read.
Counting says no.

Instrumenting the entry points for one pass (a temporary probe, not kept):

    sample            boxes  layout_block  relayout  relayout at the SAME width
    layout-stress      6926          3704       988         193, all with a height imposed
    vendor             8162          1907      1418          80, all with a height imposed
    flex-playground    4890          1704      1341          54, all with a height imposed
    grid-playground    3008           886      1065         202, all with a height imposed

Relayout is between a quarter and more than all of the initial layout count --
grid-playground relayouts MORE often than it lays out -- which is what made the
hypothesis look good. But of the relayouts that arrive at a width the box
already has, **every single one has had a cross size imposed on it**, so it is
re-running with a genuinely new constraint rather than repeating itself. Not
one relayout in the corpus is provably redundant.

The two-pass shape is the flex and grid algorithms doing their job: lay the
item out at the container's width to learn its natural size, then again at the
size that falls out of resolving the line. Skipping the second pass is not
available, and a guard on "same width" would fire on nothing.

## `text-indent: 0` was half the allocations in a layout pass

The biggest single win in this file, from four words of code.

`resolve_length` has two overloads. One takes a style and a property id and
reads the style's memoised parse. The other takes the value as CHARACTERS, has
no memo to read, and calls `parse_css_value` -- which allocates a CssValue --
every single time it runs.

`text-indent` computes to `0`, it is inherited so every element has it, and
`text_indent_px` reads it through the character overload once per inline
container per inline layout. Allocation-site profiling on vendor.html:

    3168 of 6022 allocations in one steady-state pass
         parse_css_value  <- resolve_length(string_view)  <- layout_inline_items

Fifty-three per cent of a layout pass's allocations, to parse the string "0"
into the number nought, over and over, on a page where no element indents
anything.

The fix is a fast path in the character overload for `0` and `0px`, which is
the same trick `resolve_box_sides_px` already uses for its all-zero case:

    vendor        -18.6%     stats         -16.4%     randhtml      -11.7%
    inventory     -18.0%     layout-stress -11.2%     quests        -14.1%

    allocations per pass:  vendor 10163 -> 2915, inventory 3140 -> 716,
                           layout-stress 7670 -> 2348

NOT the empty string, which falls through to `auto` -- a different answer from
zero in every caller that tells them apart -- and not `0%`, which is a Percent
to its callers rather than a Length, and that difference decides how it
resolves against its basis.

Worth remembering as a shape: a hot path reading a value as text rather than
through the parsed-value memo. The memo exists precisely so this does not
happen, and the character overload is the door around it.

## Anchor positioning costs 2 to 4 per cent, and I cannot say why

Landing CSS anchor positioning moved about half the corpus by 2 to 4 per cent
and the other half not at all. It is recorded here unresolved rather than
quietly absorbed, because the obvious explanations are all ruled out:

* **Not measurement noise.** The harness A/B'd against a copy of the same
  binary reads within 1 per cent, and `bench_head` against a freshly built HEAD
  reads within 1.2. Both controls were run alongside the numbers below.
* **Not the out-of-flow path.** `layout-stress` declares no `position:
  absolute` at all and still moves 1.9 per cent.
* **Not the anchor walk.** It is lazy: nothing collects a registry until a
  declaration actually reads `anchor(`. Two earlier shapes -- collecting per
  pass, and noting anchor names per element at build time -- were worse (6 to
  12 and 1 to 2 per cent), and both were replaced.
* **Not the extra translation unit.** Building `anchor.cpp` into the library
  with the positioning side reverted costs nothing measurable.
* **Not the size of the diff in positioning.cpp.** Moving the pass state and
  both resolvers out into `anchor.cpp`, leaving 27 added lines behind, changed
  nothing.

**It was the call sites, not the calls.** Disabling all six with `if (false)`
while leaving every line in place kept most of the regression -- so their mere
presence was changing how `apply_absolute` compiled. That same run also
cleared `layout-stress` and `randhtml` to 0.0 and 0.2 per cent, which means
their earlier readings were variance and the real cost only ever landed on
samples that HAVE out-of-flow content.

Collapsing the six into one cold call -- `apply_anchor_overrides`, which writes
all four insets and both extents in `anchor.cpp` and is marked
`[[gnu::cold]]` -- halves what is left:

    sample     six call sites   one cold call
    glass               +2.4%           +1.4%
    inventory           +2.5%           +1.1%
    layout-stress       +1.9%            0.0%
    randhtml            +0.2%           +0.2%

What remains is about 1 to 2 per cent on the four samples with the most
out-of-flow boxes, which is the honest price of the feature rather than an
accident of codegen. Kept: it closes nine real oracle failures and implements
a CSS module the engine did not have.

## An animated page reshaped all its text every frame

`hud` ran at 12 to 15 ms a frame in the gallery and spiked on hover. That was
the engine, and text was nearly all of it.

An animated page invalidates every frame, so it runs a full pass: cascade,
layout, paint. Timed per stage, paint was 5.0 ms of it and layout 2.1. Timed
inside paint (`WEVA_PAINT_LOG`), 3.0 of paint's 4.0 ms was text -- 1.5 shaping
and 1.5 building the quads -- against 0.16 for backgrounds and 0.10 for
shadows.

The text had not changed. It was being shaped twice per frame, once by layout
to measure it and once by paint to place it, and every one of those calls
crossed the host boundary TWICE (once to size the result, once to fill it),
with the Godot host building a TextServer buffer and reading a Dictionary per
glyph each time.

Shaping is now memoised at the ABI adapter, keyed on (face, text, size), which
covers both callers. Measured through the host, engine work per frame:

    page          before   after     hover before   hover after
    hud            4.33    0.96          5.40          2.04
    combat-hud     7.01    1.06          6.92          1.09
    match3         9.03    2.30          9.48          2.70
    glass          0.03    0.03          2.45          0.57

`hud`'s worst frame went 8.64 ms to 3.04, and its worst while hovering 12.91 to
5.49.

### Where hud's frame goes now

    paint 0.82 ms total:  rest 0.57   backgrounds 0.13   shadows 0.08
                          glyphs 0.03  text 0.03

Text was 3.0 of the old 4.0 ms and is now 0.06. What is left is `rest` -- the
part of a paint pass none of the named buckets claim: the tree walk, clips,
borders, images, the layer machinery. It is the largest remaining bucket on
every animated page (0.57 ms on hud, 1.3 ms on match3, where backgrounds are
another 0.6), and attributing it further needs finer buckets in the profile
before anything can be done about it.

There are now two caches on the same seam: this one, and the scalar width cache
in `FontInterfaceMetrics::measure`. They are kept apart deliberately -- measure
wants a double and would otherwise copy a glyph vector to get one. Both are
pure functions of (face, text, size) and the face is immutable, so neither has
an invalidation surface.

## Measuring text was 98 per cent repeat work

The largest win in this file, and it was invisible to every benchmark in it.

`FontInterfaceMetrics::measure` shapes the text and sums the advances. It did
that on every call, and layout calls it over and over: every relayout probe and
every intrinsic-width pass re-measures runs that have not changed. Building
`stats.html` through the Godot host made **5,384 measure() calls for 107
distinct (text, size) pairs** -- 98 per cent repeats, each one a full shaping
call across the host boundary into Godot's TextServer.

Memoised on a hash of (text, size), so a lookup allocates nothing and the
stored text is compared on a hit -- a collision costs a re-shape, never a wrong
width. Bounded at 4096 entries and cleared wholesale when it fills, because the
access pattern is a layout pass rather than a working set.

    build, engine font    266 ms -> 61.6 ms
    of which layout       241 ms -> (the rest is parse, cascade, paint, bridge)

Invisible to `layoutbench` because `MonoFontMetrics` has its own `measure` and
never touches this path -- which is exactly the point of the section below.

## The layout benchmarks measure a stub font

Everything above was measured with `tools/layoutbench.sh`, which drives
`weva_bench`, which registers `MonoFontMetrics` -- a stub face whose advance is
a constant. The Godot host registers the real engine font. Measured through the
host, on `stats.html` at 1280x720, with a Release extension
(`hosts/godot/project/statprobe.tscn`, and `WEVA_STAGE_LOG=1` for the split):

    build, engine font    284 ms      cascade 6, layout 241, paint 23
    build, stub font       49 ms
    settled update          0 ms      the core's early-out, working

`layoutbench` puts the same page at about **0.9 ms**.

So measuring text is the overwhelming majority of what layout costs in the host,
and the numbers in the rest of this file are a fraction of a per cent of what a
real page pays. They are not wrong -- the box-model work they measure is real,
and `text-indent` at -18% was a genuine allocation removed from a hot path --
but they are not the whole picture, and nothing here has yet been measured
against the face that ships.

The gallery's stats window (`S`) shows the build time, and `F` switches faces
while it is open, so the difference is one keypress away.

## Tried, measured, and not kept

Both of these looked obviously worth doing and are slower. Recorded so the
next person -- or the next tick -- does not spend the afternoon rediscovering
them.

**Pooling the grid's auto-placement occupancy.** One heap allocation per row
per grid, replaced by a borrowed vector whose rows are cleared instead. 722
fewer allocations a pass on randhtml and a consistent 1 to 6 per cent LOSS
under `--ab`: clearing the borrowed rows costs more than the allocations save.

**Memoising the inherit chain.** `ComputedStyle::get` walks ancestors to find
who sets an inherited property, which is O(depth) and runs for every read of
colour, font, line-height and the rest on every box. Caching the ancestor that
answered, guarded by a global version counter, measured 4 to 6 per cent SLOWER
on the larger pages -- stats +6.1%, map +6.2%, vendor +4.2%. Real chains are
one or two links, so the guard costs more than the walk.

**Reserving the grid's track-list vectors.** `split_tracks` and
`parse_track_list` grow by `push_back`, so a track list takes three
allocations to reach eight. `reserve(8)` in both moved allocations by 1 per
cent -- most real track lists are two or three tracks, where the growth walk
allocated once anyway -- and cost 2 to 3 per cent in time for the
over-reservation.

**Replacing the grid's `std::stable_sort` with `std::sort`.** Sorting spanning
items by span allocates a temporary buffer inside `stable_sort`: 1276 of the
7210 allocations in a randhtml pass, the largest single site in the engine
after `text-indent`. Ordering by (span, arrival index) instead is provably
identical -- arrival order is exactly what stability preserved -- and removed
17 per cent of the pass's allocations. It is 0.4 to 4.5 per cent SLOWER. The
lists are short, and the second comparison in the comparator costs more than
the buffer it avoids.

All four were measured the wrong way first and looked like wins. All four are
losses under an interleaved A/B.

### Allocation count is not a proxy for time in this engine

Four times now, and worth stating as a rule rather than a coincidence. The one
allocation fix that DID pay -- `text-indent`, worth 18 per cent -- did not pay
because it removed allocations. It paid because it removed a full CSS value
PARSE from a per-container path: the allocation was a symptom of parsing text
that a memo already held, and the parse was the cost.

So the question to ask of an allocation site is not "how many?" but "what work
is it a symptom of?". A vector that grows is not doing avoidable work. A
`parse_css_value` on a string that has not changed since the last pass is.

## What is still slow, and why it has not been fixed

**Rasterising gradients on the CPU, in the first paint.** This is the whole of
the remaining problem and it is not close:

| page | cascade | layout | paint |
|---|---|---|---|
| hud | 6.6 ms | 2.0 ms | **134.6 ms** |
| glass | 6.2 ms | 2.7 ms | **150.0 ms** |

Five million gradient-stop evaluations and 52 blurs on hud alone. Making the
CASCADE faster -- style sharing, a cheaper property write -- addresses four
percent of that, which is why the obvious-sounding fix is the wrong one. It
was worth measuring before building: an earlier reading of the same problem
blamed the cascade on the strength of a 10,000-row synthetic page, where the
proportions are reversed.

What would actually help, and what it costs:

  * A LUT for the stop search. `sample_stops` walks the stop list per pixel.
    A table from quantised `t` to SEGMENT INDEX skips the search while leaving
    the interpolation exactly as it is, so the output stays bit-identical.
    Worth perhaps a tenth of the paint.
  * Rasterising smooth gradients at reduced resolution and upsampling. This is
    where the real factor is -- a full-screen layer is 590,000 pixels at the
    current 1024-px cap -- and it MOVES PIXELS. The render gates compare
    against the Godot backend at 0.00% on thirteen states, so this needs a
    decision about tolerance before it can be a change.
  * Gradients as GPU work rather than a baked texture. A different
    architecture, not an optimisation.

**A first cascade is ~30 us an element on a rule-heavy page.** Each element
performs ~178 property writes to materialise a ComputedStyle. Style sharing --
elements with the same shape, the same parent and no inline style pointing at
one computed style -- is the fix, and it is worth doing on a page like
layout-stress where the cascade IS the cost (16.7 ms against 7.3 for paint).
It is not worth doing for hud or glass.

Measured and worth recording: the cascade's shape cache does not earn its keep
on a uniform list. 10,000 rows with a UNIQUE class each cascade FASTER than
10,000 identical ones, which is the opposite of what a shape cache is for.
