# The oracle: how the C# engine guards the C++ one

**Build this before writing engine C++.** It is the single highest-leverage
thing in the plan.

## The problem

`Packages/com.wevaui/Tests/` is **183,194 LOC across ~10,500 NUnit tests** —
60k LOC on layout alone, 52k on CSS, 23k on paint. Only ~143 files are data
fixtures (47 HTML, 46 CSS, 50 JSON); the rest are hand-written C# asserts
against Weva's own APIs. They do not translate cheaply, and a layout engine
without them is not trustworthy.

Losing that net is the largest risk in the whole port — larger than any
individual subsystem.

## The solution

Don't translate the tests. Translate the *behaviour they pin*, by keeping the
C# implementation as a reference oracle and diffing against it.

`Tools/BaselineGen/LayoutDump.cs` already emits exactly the right artifact: a
stable JSON dump of every box for a given HTML + CSS at a given viewport.

```json
{ "source": "...", "width": 1280, "height": 720, "count": 412,
  "elements": [ {"i":0,"depth":0,"tag":"div","id":"root","cls":"page",
                 "x":0,"y":0,"w":1280,"h":2140}, ... ] }
```

Per-element `x/y/w/h` at a fixed viewport is the entire contract of a layout
engine. If both implementations agree on that across a large corpus, they agree.

## Pipeline

```
corpus/*.html + *.css
        │
        ├─── C# BaselineGen ──────► reference/*.json      (generated once per corpus change)
        │
        └─── C++ weva_dump ───────► candidate/*.json
                                          │
                                    tools/oracle/diff ──► pass / per-element deltas
```

Three pieces to build:

1. **`tools/oracle/corpus/`** — HTML+CSS snippets with a viewport size.
   Sources, in priority order:
   * Harvest every inline HTML/CSS string from the C# test suite. 291 test files
     across Layout and Css contain them; extraction is a scripted pass, not
     manual work.
   * The 47 existing HTML / 46 CSS fixtures.
   * `Assets/UI/randhtml.html` + `.css` — the dev demo, already Chrome-diffed.
   * Generated combinatorial cases per property group once the harness runs.

2. **`weva_dump`** — a C++ CLI mirroring `BaselineGen`'s output byte-for-byte.
   Build it in Phase 1 with a stub layout that emits nothing; it grows with the
   engine.

3. **`tools/oracle/diff`** — compares two dumps, reports per-element deltas,
   exits non-zero on any mismatch. Tolerance is **zero** (see CONVENTIONS.md on
   floating point). A tolerance knob is a way to hide bugs.

## Gates

Each phase in PORT_PLAN.md is done when its slice of the corpus diffs clean.
The corpus is partitioned by feature so a phase can gate on its own subset:

| Phase | Corpus subset gated |
|---|---|
| 2 CSS parse + values | property round-trip dumps, no layout |
| 3 cascade + selectors | computed-style dumps |
| 4 block + inline | `corpus/block/`, `corpus/inline/` |
| 5 text | `corpus/text/` (needs font parity — see below) |
| 6 flex | `corpus/flex/` |
| 7 grid | `corpus/grid/` |
| 8 positioning, floats, tables | remainder |

Never advance a phase on a partially-green subset. The whole value of the oracle
is that it refuses to let divergence accumulate.

## Paint has a second oracle

`Runtime/Testing/Goldens/SoftwareRasterizer.cs` (1,592 LOC, zero Unity refs)
plus **38 baseline PNGs** in `Tests/Runtime/Goldens/Baselines/`. Port the
software rasterizer early (Phase 4) and diff images directly against those
baselines. `PngReader`/`PngWriter` in the same directory are also Unity-free and
port with it.

## Text is the one place exact parity is not the goal

Glyph rasterization will differ between Unity's `FontEngine` and whatever
`FontInterface` implementation is chosen. Do not chase bit-identical text.

Gate text on **layout-level** properties instead: line-break positions, line
count, line box heights, and advance-width sums within a tolerance stated in the
corpus metadata. Box geometry for everything *around* the text stays zero-
tolerance.

## Prerequisite: fix the stale csproj excludes first

`Tools/BaselineGen/BaselineGen.csproj` and `Tools/PerfBench/PerfBench.csproj`
still exclude `Runtime/UIDocument.cs`, which no longer exists — the file is now
`WevaDocument.cs`, and only `TestVerifyAll.csproj` excludes it. Those two
headless builds are therefore likely pulling in a `MonoBehaviour`.

This was noted as a minor cleanup in the feasibility analysis. It stops being
minor here: **BaselineGen is the oracle**, so it must build cleanly and
deterministically outside Unity before anything else in this plan is worth
starting. Verify with an actual `dotnet build` — this has not been confirmed on
a machine with the SDK.

## All three corpora are gated now

`check.sh` runs the oracle over `samples` (47, at 1280x720), `hand` (47) and
`harvest` (210, both at 800x600). It used to run only `samples`, and two real
bugs lived comfortably underneath it -- a subgrid growing implicit rows it
should have clamped, and a list marker taking inline space so every inline
child of every `<li>` sat a marker-width too far right.

The marker one could not have been caught by `samples` at any size: a list item
holding only text has no element after the marker for the dump to compare, and
every item in every sample holds only text. It took an `<input>` inside an
`<li>`, in a case lifted from the reference's own suite, to make it visible.

`hand` and `harvest` gate at 0 and 3 differ respectively. The three are waiting
on the font and form-control metrics decision in `known-gaps/README.md`; on
those, Chrome agrees with neither engine. The number is written into check.sh
so a fourth breaks the build.

The oracle step now takes about four minutes rather than one. That is the price
of the two bugs above, and it is worth paying.

## The harvest pool, and chrome_sweep.py

`corpus/samples` is the gate: 47 cases, three-way, run by `check.sh`.
`corpus/harvest` holds 210 more, each with a Chrome capture already beside it,
and nothing ran them -- the three-way needs BaselineGen for every case, which
is slow enough that the gate deliberately does not.

`chrome_sweep.py` does the cheap two-way instead: lay each case out and ask
whether Chrome agrees. No reference, so it cannot tell a port bug from a place
where the reference and Chrome differ by design. Its output is a list of
**leads**, not a verdict.

    python3 tools/oracle/chrome_sweep.py tools/oracle/corpus/harvest         --weva-dump <build>/tools/weva_dump/weva_dump --width 800 --height 600

The width matters: the harvest captures were taken at 800x600 and the samples
at 1280x720. Running harvest at 1280 reports 138 of 210 differing, all of them
`ours 1280, chrome 800` on some full-width block. At the right width it is 34,
and most of those are the font-metrics divergence -- the harvest captures were
not taken with `--metrics=mono`, so any line height reads 18.288 against
Chrome's 19. Read past those.

What it found on its first run was real: a subgrid with more items than
subgridded tracks grew implicit rows instead of clamping into the last one.
Confirmed by promoting the lead to the three-way, which returned REF! on four
cases -- 0 differ, chrome sides with us on every value.

### What the harvest pool found

Running the full three-way over all 210 harvest cases takes a few minutes and
is worth doing when the gate has been green for a while. The first run read
**181 agree, 14 differ, 15 reference bugs**. The 14:

* Nine are CSS anchor positioning (`anchor-name`, `position-anchor`,
  `position-try-fallbacks`), which `positioning.cpp` does not implement at all.
  Not a bug -- a missing feature, and the largest one left.
* Two were list markers taking inline space, fixed.
* One is a subgrid case, fixed.
* The rest (`QuestGridAutoRow-02`, `SnapshotMatcher-00`) are cases where Chrome
  agrees with NEITHER engine: both differ from the browser on text-driven
  heights, and from each other by a few pixels on top. Those need the font
  decision in known-gaps/README.md before they mean anything.

The 15 reference bugs are the C# reference being wrong with Chrome siding with
us, which is the verdict working as designed and needs nothing.

