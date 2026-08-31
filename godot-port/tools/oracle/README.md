Differential test harness. Build this in Phase 0, before any engine code —
see ../../docs/ORACLE.md.

  corpus/     HTML + CSS + viewport, partitioned by feature
  reference/  C# BaselineGen dumps (regenerate on corpus change)
  candidate/  C++ weva_dump output
  diff        comparator; zero tolerance; non-zero exit on mismatch

Prerequisite: BaselineGen must build and run headlessly. Its csproj still
excludes a stale Runtime/UIDocument.cs path — verify with dotnet build.
