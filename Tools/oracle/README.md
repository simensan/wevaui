Differential test harness — the C# engine guards the C++ one. Design and
rationale: [../../docs/ORACLE.md](../../docs/ORACLE.md).

    harvest.py <tests-root> <corpus-out>    extract HTML/CSS from the C# tests
    harvest_corpus.py <roots> --out <dir>   the flat corpus run_oracle.py reads
    run_oracle.py <corpus> [--reuse-reference] [--only name]
    diff.py <reference> <candidate>         compare two dumps; zero tolerance
    run.sh [bucket]                         full run, optionally one feature

The working recipe (from the wevaui repo root, engines built). Chrome measures
with the engines' synthetic faces (`--metrics=mono`, see make_mono_font.py) so
that a text-dependent disagreement is decidable; the faces must cover every
character the corpora use, so regenerate them after adding cases:

    python Tools/oracle/make_mono_font.py \
        --scan Tools/oracle/corpus/samples Tools/oracle/corpus/harvest \
               Tools/oracle/corpus/hand          # Windows python has fontTools

    python3 Tools/oracle/harvest_corpus.py Packages/com.wevaui/Tests/Runtime \
        --out Tools/oracle/corpus/harvest --min-elements 2
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../Tools/oracle/corpus/harvest 800 600 --metrics=mono)   # Chrome, for arbitration
    python3 Tools/oracle/run_oracle.py Tools/oracle/corpus/harvest \
        --weva-dump <build>/tools/weva_dump/weva_dump --quiet --reuse-reference

The hand-built cases live in the Unity package (Tests/Runtime/Goldens/Snippets)
next to Inter-metric Chrome captures that LayoutDiffTests.cs consumes. NEVER
capture with --metrics=mono in there; copy the cases out first:

    mkdir -p Tools/oracle/corpus/hand
    cp Packages/com.wevaui/Tests/Runtime/Goldens/Snippets/*.html \
       Packages/com.wevaui/Tests/Runtime/Goldens/Snippets/*.css Tools/oracle/corpus/hand/
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../Tools/oracle/corpus/hand 800 600 --metrics=mono)
    python3 Tools/oracle/run_oracle.py Tools/oracle/corpus/hand \
        --weva-dump <build>/tools/weva_dump/weva_dump --quiet --reuse-reference

The sample pages — the ones a Godot host must render — are collected the same
way and run at the game viewport:

    python3 Tools/oracle/collect_samples.py . \
        --out Tools/oracle/corpus/samples
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../Tools/oracle/corpus/samples 1280 720 --metrics=mono)
    python3 Tools/oracle/run_oracle.py Tools/oracle/corpus/samples \
        --width 1280 --height 720 --weva-dump <build>/tools/weva_dump/weva_dump \
        --quiet --reuse-reference

`--reuse-reference` keeps a reference dump that is newer than its case; the
.NET start-up per case is otherwise most of a run. Drop the flag after a C#
change. The Chrome capture beside each case is what lets a disagreement be
blamed on the reference; without it every difference counts against the port.
Elements are paired by identity (tag, id, class) rather than position, so a
fragment the engines list elsewhere, or a `display: none` element Chrome
omits, costs only its own verdict; an element that merely sits elsewhere in
the walk is not a difference. A run of same-identity siblings is paired
against Chrome by whichever of positional / nearest-by-reference /
nearest-by-candidate gives Chrome the most agreements with either side.

Verdicts: `REF!` — every difference is one Chrome sides with the port on
(within 1/64px plus 2.5e-4 relative). `REF~` — the same, except that on some
Chrome only LEANS to the port: within 1.5px of it while the reference is
whole pixels off and at least 4x farther (Blink's own residue — an inline
rect rounded to the pixel, a text width off by a hundredth of an em — on top
of a real disagreement). Both count as arbitrated in the summary and are
printed apart. `FAIL` lines break the rest down into sides-with-reference,
leans-to-reference, neither, and the port's own counts.

What Chrome still cannot arbitrate, even with the synthetic faces: the y and
height of an inline element's own rect (Blink rounds a run's ascent and
descent to whole pixels; the line box is exact), anything an animation moves
(each side samples its own time), and letter-spacing — Blink adds spacing
after every character, both engines after every character but the last, so a
letter-spaced run reads one spacing narrower here than in Chrome by design.

  corpus/     harvested snippets + .meta.json (viewport, origin, tolerance)
  reference/  C# BaselineGen dumps
  candidate/  C++ weva_dump output

All three are generated and gitignored.

Current harvest: 3,879 entries — block 1563, cascade 967, flex 450, grid 269,
scrolling 169, positioning 159, inline 121, text 113, multicol 46, tables 22.
The harvester is deliberately conservative and its output is not yet reviewed;
snippets that need a test's surrounding setup will not lay out identically
standalone and should be culled as they surface.

**`run.sh` requires the .NET SDK.** BaselineGen is the oracle, so without it
the script exits 2 rather than reporting a vacuous pass over zero entries.


## What the Chrome survey can and cannot tell you

`chrome_survey.py` compares the software render against the corpus's Chrome
captures over the cells that are flat in both. Its numbers look like a
visual-quality score and are not one, and the difference is worth stating
because chasing them costs a day and finds nothing.

The captures were made under a synthetic metrics font whose glyphs are SOLID
BOXES. The engine renders its own 5x7 stub. So:

  * A block of text is a run of solid rectangles in Chrome and real glyphs
    here. A solid rectangle IS flat, and a dense patch of stub glyphs can be
    flat too, so text leaks past the flatness filter and reports deltas in the
    200s. Every large disagreement in the survey traces to this.
  * Text also drives LAYOUT. grid-playground's `1fr` columns size to their
    content, so a different glyph width moves the column edges -- the survey
    reports a colour difference where the truth is that a bar is 45px wider.
  * A sample with `@keyframes` is at a different INSTANT in the two images:
    Chrome captured whatever moment it captured, and the engine renders the
    first frame. match3-endgame reads 31.8% for this reason alone.

So the survey is useful for finding a shape or a colour that is plainly wrong,
and useless as a score. The measurement that IS meaningful is the backend
comparison (`hosts/godot/compare_render.py`): both sides consume the identical
draw list from the identical build, so a difference there is a difference
between the two rasterisers and nothing else.

Audited 2026-09-02, and this is what the backend numbers mean:

  * a broad difference of <= 8 across a gradient-filled panel, in diagonal
    bands -- the two samplers filtering the same texture slightly differently;
  * up to 32 along a rounded border -- edge antialiasing;
  * over 32 only on SINGLE ROWS at the edge of thin elements. quests' worst
    pixel is one such: at y=447 the software renderer is mid-ramp on a
    progress bar's top edge and Godot has already resolved it to full
    coverage, and by y=448 they agree exactly.

None of that is a defect. Two rasterisers resolving a half-pixel differently
is what the structural tolerance of 64 exists to permit, and the 0.34% figure
for quests is a count of those edge rows.

## What the form-control metrics actually say

`corpus/samples/form-metrics.html` exists to pin the UA's form-control box
model against Chrome, since the ordinary samples style their controls and hide
the defaults. Captured at 1280x720 with `--metrics=mono`, Chrome and this
engine agree exactly on:

  * `input[type=text]` 218x34, `textarea` 218x90, both unstyled
  * `select` 218 wide whatever its option's length -- and 218 EVEN WITH an
    author `width: 90px`, which Chrome refuses to shrink below its intrinsic
    minimum while honouring the same declaration on an input, a textarea and a
    button. The UA's `min-width: 218px` on select is not a divergence from
    Chrome; it is Chrome.
  * checkbox and radio at 16x16

One control does NOT agree, and it is worth writing down rather than half
fixing. An unstyled `<button>`:

    weva    46 x 20     (after adding a 1px border: 46 x 22)
    chrome  46 x 23.2

The width is a missing 1px border on each side. The remaining ~1.2px of height
is the content box: Chrome resolves `line-height: normal` inside a button to
17.2px where this engine gets 16, and the difference shows again in
`sample-menu`, where a styled button is 32.002 in the C# reference, 34.002 with
the border added here, and 36 in Chrome.

Adding the border alone therefore makes things WORSE by the oracle's rules --
it moves the port off the reference without landing on Chrome, which the
harness counts as a real difference rather than a reference bug. The fix has to
be the button's whole intrinsic box, in the C# engine and the port together,
since the reference is what the port is measured against. Left undone
deliberately, with the numbers here so it can be picked up.

## Comparing renderer pixels with browser screenshots

Chrome PNG pixels are sRGB bytes. `SoftwareRenderer::pixel()` returns linear
RGB for numeric rendering assertions; multiplying its RGB channels by 255 is
not a valid PNG comparison. Use `SoftwareRenderer::to_srgb_rgba()` and compare
the resulting straight-alpha byte channels with the browser PNG. Fully saturated
primary colors can hide this mistake; nearly opaque colors exposed it in the
table-opacity probe.

`check_table_opacity_chrome.cjs <output.json>` captures equivalent opaque CSS
forms and a partially transparent control, including overlap hit targets. It
uses the same Chrome installation and Puppeteer dependency as the other focused
Windows oracles. [Qualification](../../docs/verification/opacity193.json).
