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

## OPEN: a border radius plus unequal border widths draws a trapezoid

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

**Fixing it** means making the outer-to-inner correspondence robust for unequal
widths -- offsetting each outer vertex inward by the width of the edge it
belongs to, blending the two widths across a corner arc, rather than building a
second rounded rect and zipping. That changes every border in the engine, so it
wants its own pass with the visual corpus checked before and after.

