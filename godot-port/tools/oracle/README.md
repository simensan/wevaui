Differential test harness — the C# engine guards the C++ one. Design and
rationale: [../../docs/ORACLE.md](../../docs/ORACLE.md).

    harvest.py <tests-root> <corpus-out>    extract HTML/CSS from the C# tests
    diff.py <reference> <candidate>         compare two dumps; zero tolerance
    run.sh [bucket]                         full run, optionally one feature

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
