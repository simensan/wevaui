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
