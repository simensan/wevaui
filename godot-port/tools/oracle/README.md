Differential test harness — the C# engine guards the C++ one. Design and
rationale: [../../docs/ORACLE.md](../../docs/ORACLE.md).

    harvest.py <tests-root> <corpus-out>    extract HTML/CSS from the C# tests
    harvest_corpus.py <roots> --out <dir>   the flat corpus run_oracle.py reads
    run_oracle.py <corpus> [--reuse-reference] [--only name]
    diff.py <reference> <candidate>         compare two dumps; zero tolerance
    run.sh [bucket]                         full run, optionally one feature

The working recipe (from the wevaui repo root, engines built):

    python3 godot-port/tools/oracle/harvest_corpus.py Packages/com.wevaui/Tests/Runtime \
        --out godot-port/tools/oracle/corpus/harvest --min-elements 2
    (cd Tools/Layout && node capture-all-chrome-layouts.mjs \
        ../../godot-port/tools/oracle/corpus/harvest 800 600)     # Chrome, for arbitration
    python3 godot-port/tools/oracle/run_oracle.py godot-port/tools/oracle/corpus/harvest \
        --weva-dump <build>/tools/weva_dump/weva_dump --quiet --reuse-reference

`--reuse-reference` keeps a reference dump that is newer than its case; the
.NET start-up per case is otherwise most of a run. Drop the flag after a C#
change. The Chrome capture beside each case is what lets a disagreement be
blamed on the reference; without it every difference counts against the port.

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
