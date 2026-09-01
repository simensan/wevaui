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

    python godot-port/tools/oracle/make_mono_font.py \
        --scan godot-port/tools/oracle/corpus/samples godot-port/tools/oracle/corpus/harvest \
               godot-port/tools/oracle/corpus/hand          # Windows python has fontTools

    python3 godot-port/tools/oracle/harvest_corpus.py Packages/com.wevaui/Tests/Runtime \
        --out godot-port/tools/oracle/corpus/harvest --min-elements 2
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../godot-port/tools/oracle/corpus/harvest 800 600 --metrics=mono)   # Chrome, for arbitration
    python3 godot-port/tools/oracle/run_oracle.py godot-port/tools/oracle/corpus/harvest \
        --weva-dump <build>/tools/weva_dump/weva_dump --quiet --reuse-reference

The hand-built cases live in the Unity package (Tests/Runtime/Goldens/Snippets)
next to Inter-metric Chrome captures that LayoutDiffTests.cs consumes. NEVER
capture with --metrics=mono in there; copy the cases out first:

    mkdir -p godot-port/tools/oracle/corpus/hand
    cp Packages/com.wevaui/Tests/Runtime/Goldens/Snippets/*.html \
       Packages/com.wevaui/Tests/Runtime/Goldens/Snippets/*.css godot-port/tools/oracle/corpus/hand/
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../godot-port/tools/oracle/corpus/hand 800 600 --metrics=mono)
    python3 godot-port/tools/oracle/run_oracle.py godot-port/tools/oracle/corpus/hand \
        --weva-dump <build>/tools/weva_dump/weva_dump --quiet --reuse-reference

The sample pages — the ones a Godot host must render — are collected the same
way and run at the game viewport:

    python3 godot-port/tools/oracle/collect_samples.py . \
        --out godot-port/tools/oracle/corpus/samples
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../godot-port/tools/oracle/corpus/samples 1280 720 --metrics=mono)
    python3 godot-port/tools/oracle/run_oracle.py godot-port/tools/oracle/corpus/samples \
        --width 1280 --height 720 --weva-dump <build>/tools/weva_dump/weva_dump \
        --quiet --reuse-reference

`--reuse-reference` keeps a reference dump that is newer than its case; the
.NET start-up per case is otherwise most of a run. Drop the flag after a C#
change. The Chrome capture beside each case is what lets a disagreement be
blamed on the reference; without it every difference counts against the port.
Elements are paired by identity (tag, id, class) rather than position, so a
fragment the engines list elsewhere, or a `display: none` element Chrome
omits, costs only its own verdict.

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
