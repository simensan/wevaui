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
