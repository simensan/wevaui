# Visual quality

How close the render is to a browser, and — more useful — how to tell a real
difference from an artefact of the comparison. Every number here came from

    python3 tools/oracle/visual_rank_soft.py <weva_render>

against the 35 Chrome screenshots in `tools/oracle/corpus/samples`. Re-run it
rather than trusting the table; it is a snapshot.

## What this is not

It is not a gate. The three gates are the layout oracle, the backend
comparison and the interactive comparison, and all three are exact. This is a
ranking: it says which page to LOOK at first, and the looking is the part that
finds things.

That distinction is load-bearing, because the ranking's top entry has twice now
been an artefact rather than a defect.

## The two artefacts, both of which fooled me first

**The clock.** A page with an entrance animation renders at the START of it at
`t = 0`, and a browser's screenshot is taken once it has settled. Comparing
those measures the clock. `match3-endgame` sets its cards to `opacity: 0` and
animates them in, and it read as the worst page in the corpus at **33.9%
differing — three times the next entry** — until `weva_render` grew an
`--advance=SECONDS` flag and ran the clock first. The same page then read
3.6%, mid-pack. Nothing about the renderer changed.

A page with an `infinite` animation cannot be fixed this way, because there is
no settled phase to advance to. Those are marked `animated` in the output and
their numbers mean nothing.

**The face.** The corpus screenshots were taken with text rendered as SOLID
BLOCKS, and the software renderer draws a 5x7 bitmap face. Those two disagree
on every pixel of every word, which is most of the interesting pixels on most
pages. The tool masks text out — rendering each page twice, once with
`color: transparent`, and excluding what changed — but Chrome's blocks are so
much heavier than our strokes that they spill past any mask derived from our
own render. So the `non-text` column is an upper bound, not a measurement.

## What a full look at the worst non-animated page found

`quests`, the top non-animated entry at 11.6% non-text, was examined pixel by
pixel. Three candidate defects, all three of them not:

  * A **light strip inside each card's right edge** that Chrome does not draw.
    It is our overlay scrollbar. `.quests` is `overflow-y: auto`, our heavier
    line boxes are a few pixels taller than Chrome's, and that is enough to
    push the list into overflow where Chrome's content still fits. Re-rendering
    with `overflow-y: visible` puts the card background back against the border
    exactly where Chrome has it.
  * The footer **button reading as a gradient** in Chrome and flat in ours.
    Both render `(159, 114, 251)`. Chrome's white text blocks are heavy enough
    to shift the button's apparent colour.
  * **Cards a few pixels taller.** Line height, downstream of the face again.

Layout — card positions, progress-bar extents, badge geometry, border radii,
the footer — lines up within a pixel or two throughout.

## The reading

No page in the corpus is missing an effect. Across 35 samples the residual is
the text face and the second-order consequences of the text face. That is a
statement about this corpus and this comparison, not a proof; the way to
falsify it is to add a sample that uses something untested and look at it.

## Ranking, non-animated pages only

| page | all | non-text |
|---|---|---|
| quests | 14.0% | 11.6% |
| weva-landing | 13.6% | 10.6% |
| episode-stats | 8.9% | 5.6% |
| leaderboard | 7.9% | 5.2% |
| grid-playground | 7.1% | 5.1% |
| stock-dashboard | 7.2% | 4.3% |
| ... | | |
| map | 1.5% | 0.4% |
| card-component | 0.4% | **0.0%** |

`card-component` is the control: little text, and it agrees with Chrome
exactly. The ordering above tracks text density almost perfectly, which is
the same conclusion as the paragraph before it, arrived at from the other end.

## FIXED: a border radius plus unequal border widths drew a trapezoid

Reproductions: `tools/oracle/corpus/visual/border-radius-uneven.{html,css}` and
`inset-shadow-blur.{html,css}`. Render either with `weva_render` and look.

`border-bottom: 20px solid` on a square box draws the full-width band it
should. Add `border-radius: 11px` and the box becomes a **trapezoid** -- the
left and right edges slope inward over the whole height, not just at the
corners. Two adjacent borders with no radius mitre correctly, so the trigger is
specifically *a radius together with unequal side widths*.

The same geometry is what inset box-shadows are built from, so every inset
shadow on a rounded box is wrong too, and a blurred one (`inset 0 0 40px`)
comes out as a hard-edged rectangle instead of a soft falloff.

**Why.** `tessellate_border` builds the ring by zipping the outer rounded
outline against the inner one, vertex k to vertex k. That correspondence only
holds when the two outlines are similar, which needs uniform widths. With
`bottom: 20` and the other three at zero, the outer left edge runs from
`y+11` to `y+h-11` while the inner left edge runs from `y+11` to `y+h-20`; the
two are zipped end to end, and the length difference becomes a wedge that
widens down the edge. With no radius each corner is a single point, the zip is
four-to-four, and the mitre is right -- which is why this only appears with a
radius.

**Scope.** 14 of the 35 gallery samples have a rule that hits it, 26 rules in
all: `episode-stats`, `glass`, `load-game` (4 each), `hud`, `level-select`,
`stock-dashboard` (2 each), and eight more with one. It is visible in
`episode-stats` as a hard rectangle inside each card's artwork where Chrome
has a smooth fade.

**Not caught by any gate**, and could not be: the layout oracle compares
geometry, not pixels; the backend gate compares the two rasterisers on the
IDENTICAL draw list, so a wrong draw list passes it twice over. Only
`visual_rank_soft.py` against the Chrome screenshots ranks it, and that tool
is not wired into `check.sh` because two rasterisers never agree pixel for
pixel. `episode-stats` is the top-ranked page in it, at 5.6 per cent non-text
difference, which is what led here.

**The fix**, and it was smaller than the diagnosis suggested. The zip is fine;
what was wrong was one corner of the inner outline. `inset_radii` reduces each
radius per axis, so `border-bottom: 20px; border-radius: 11px` gave the bottom
corners a radius of (11, 0) -- the y-axis eaten by the 20px bottom, the x-axis
untouched by the 0px side. That is neither a curve nor a corner:
`rounded_outline` hands it to `arc_points`, which has no arc to sweep and emits
the arc's CENTRE, a point 11px in from where the corner belongs. The inner
right edge then ran diagonally from (300, 11) to (289, 70) and the ring
swallowed a wedge of the content that widened down the whole side.

A corner that has collapsed on either axis is square, so both axes now go to
zero together. The mesh dump goes from `inner (289.00, 70.00)` to
`inner (300.00, 70.00)` and the trapezoid is gone.

**A second bug underneath it.** With the geometry right, blurred inset shadows
were still hard-edged rectangles. `paint_inset_shadows` computed each layer's
target coverage as `blurred_coverage(-e, sigma)` where the outset loop directly
above it uses `blurred_coverage(e, sigma)`. Negated, the THICKEST frame came
out fully dense on the first iteration, every later frame failed the
`target <= accumulated` test, and the blur collapsed to one hard frame a
blur-radius thick -- contradicting the comment two lines below, which already
said the frames run thickest-faint to thinnest-dense.

**Measured.** `visual_rank_soft.py` over the 35 samples, before and after:
`episode-stats` 5.6% -> **0.5%** non-text difference from Chrome, `load-game`
2.3% -> 1.2%, `menu` 3.6% -> 2.7%, and **nothing worse**. episode-stats was the
worst non-animated page in the corpus and is now the best.

## OPEN: an inline replaced element with a percentage width blows out its grid track

Reproduction: `tools/oracle/corpus/visual/inline-percent-in-grid.{html,css}`.

Three cells in `grid-template-columns: repeat(3, 1fr)`, each holding an `<img>`
that cannot load. The two whose image is `display: block` get even thirds. The
one whose image is left INLINE takes the whole grid:

    div.c   x=0     w=40         <- padding only, image contributes 0
    div.c   x=64    w=40
    div.c   x=128   w=1280       <- the entire grid, overflowing it
    img.inl x=148   w=1240

The two rules differ in one declaration, `display: block`. Chrome gives all
three 389.33.

**Why.** CSS Sizing §5.2.1: while computing an intrinsic contribution, a
percentage size behaves as `auto` -- for a replaced element with no intrinsic
width, zero. Block children go through block layout, which does this, which is
why the block images contribute nothing. An inline atom does not: `size_atoms`
sizes it with `shrink_to_fit(atom, available_width, ...)`, and
`available_width` is a concrete number, so `width: 100%` resolves against it
and the item reports a max-content contribution of the whole containing block.
A `1fr` track is `minmax(auto, 1fr)`, so that automatic minimum wins over the
fr share and the track swallows the grid.

**Scope.** `9slice-demo` is the case that surfaced it: its first grid has one
`.frame-img-large`, which is the only image on the page without `display:
block`, and that one cell takes the row. Its second grid, whose cells hold
`<div>`s, is correct on the same page.

**Shared with the reference**, which agrees with us -- so the layout oracle
reads `ok` and only the comparison against Chrome shows it. It was found by
sorting `chrome_sweep.py` output by the largest disagreement: 827px, on a page
whose next-largest difference is 2px of line-height.

**Fixing it** means telling the atom-sizing path that it is inside an intrinsic
measurement, so percentages resolve as `auto` rather than against a provisional
width. That is the same distinction block layout already makes; the plumbing is
what is missing, and it wants its own pass.

