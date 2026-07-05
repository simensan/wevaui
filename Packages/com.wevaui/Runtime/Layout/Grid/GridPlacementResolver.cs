using System.Collections.Generic;

namespace Weva.Layout.Grid {
    internal static class GridPlacementResolver {
        // Pre-resolution: turn each item's GridItemPlacement into a partial GridArea
        // where 0 means "auto / unresolved" and positive integers are 1-based grid
        // lines (end exclusive). This pass does NOT do the auto-flow placement
        // (that is GridAutoPlacement); it only resolves named areas, named lines,
        // span/integer specifications, and negative end-of-grid refs (where the
        // explicit grid extents are known).
        public static PartialPlacement Resolve(GridItemPlacement placement,
                                               GridContainerProperties props,
                                               int explicitRowLines,
                                               int explicitColumnLines) {
            int rs = 0, re = 0, cs = 0, ce = 0;
            int rsSpan = 0, reSpan = 0, csSpan = 0, ceSpan = 0;

            // grid-area: <name> resolves to the named area's lines.
            if (placement.AreaName != null && props.Areas != null && props.Areas.Areas.TryGetValue(placement.AreaName, out var area)) {
                rs = area.RowStart;
                re = area.RowEnd;
                cs = area.ColumnStart;
                ce = area.ColumnEnd;
                return new PartialPlacement {
                    RowStart = rs, RowEnd = re,
                    ColumnStart = cs, ColumnEnd = ce
                };
            }

            ResolveAxis(placement.RowStart, placement.RowEnd, props.Rows.LineNames, props.Areas, isRow: true,
                        explicitRowLines, out rs, out re, out rsSpan, out reSpan);
            ResolveAxis(placement.ColumnStart, placement.ColumnEnd, props.Columns.LineNames, props.Areas, isRow: false,
                        explicitColumnLines, out cs, out ce, out csSpan, out ceSpan);

            return new PartialPlacement {
                RowStart = rs, RowEnd = re,
                ColumnStart = cs, ColumnEnd = ce,
                RowStartSpan = rsSpan, RowEndSpan = reSpan,
                ColumnStartSpan = csSpan, ColumnEndSpan = ceSpan
            };
        }

        static void ResolveAxis(GridLineRef start, GridLineRef end,
                                IReadOnlyList<IReadOnlyList<string>> lineNames,
                                GridAreasParser.AreasMap areas,
                                bool isRow,
                                int explicitLineCount,
                                out int startLine, out int endLine,
                                out int startSpan, out int endSpan) {
            startLine = 0; endLine = 0; startSpan = 0; endSpan = 0;

            int? sIdx = ResolveSingle(start, lineNames, areas, isRow, explicitLineCount, isEnd: false);
            int? eIdx = ResolveSingle(end, lineNames, areas, isRow, explicitLineCount, isEnd: true);

            if (sIdx.HasValue) startLine = sIdx.Value;
            else if (start.IsSpan) startSpan = start.Index > 0 ? start.Index : 1;

            if (eIdx.HasValue) endLine = eIdx.Value;
            else if (end.IsSpan) endSpan = end.Index > 0 ? end.Index : 1;
        }

        static int? ResolveSingle(GridLineRef r,
                                  IReadOnlyList<IReadOnlyList<string>> lineNames,
                                  GridAreasParser.AreasMap areas,
                                  bool isRow,
                                  int explicitLineCount,
                                  bool isEnd) {
            if (r.IsAuto) return null;
            if (r.IsSpan) return null;

            if (r.Name != null) {
                // r.Index carries the optional <integer> of `<custom-ident> <integer>`
                // (CSS Grid L1 §8): positive -> Nth match, negative -> Nth-from-end,
                // zero -> bare name (first match).
                int idx = LookupNamedLine(r.Name, lineNames, areas, isRow, isEnd, r.Index);
                if (idx > 0) return idx;
                return null;
            }
            if (r.Index != 0) {
                if (r.Index > 0) return r.Index;
                if (explicitLineCount > 0) {
                    int line = explicitLineCount + r.Index + 1;
                    if (line < 1) line = 1;
                    return line;
                }
                return null;
            }
            return null;
        }

        static int LookupNamedLine(string name,
                                   IReadOnlyList<IReadOnlyList<string>> lineNames,
                                   GridAreasParser.AreasMap areas,
                                   bool isRow,
                                   bool isEnd,
                                   int nth) {
            // nth: 0 or 1 => first match; N>1 => Nth match (1-based);
            // N<0 => Nth from the end (so -1 => last). Matches §8 of Grid L1.
            if (lineNames != null) {
                if (nth < 0) {
                    int target = -nth; // 1-based from the end.
                    int seenFromEnd = 0;
                    for (int i = lineNames.Count - 1; i >= 0; i--) {
                        var slot = lineNames[i];
                        if (slot == null) continue;
                        foreach (var n in slot) {
                            if (n == name) {
                                seenFromEnd++;
                                if (seenFromEnd == target) return i + 1;
                                break;
                            }
                        }
                    }
                } else {
                    int target = nth <= 0 ? 1 : nth;
                    int seen = 0;
                    for (int i = 0; i < lineNames.Count; i++) {
                        var slot = lineNames[i];
                        if (slot == null) continue;
                        foreach (var n in slot) {
                            if (n == name) {
                                seen++;
                                if (seen == target) return i + 1;
                                break;
                            }
                        }
                    }
                }
            }
            if (areas != null) {
                foreach (var kv in areas.Areas) {
                    string baseName = kv.Key;
                    var rect = kv.Value;
                    if (name == baseName + "-start") {
                        return isRow ? rect.RowStart : rect.ColumnStart;
                    }
                    if (name == baseName + "-end") {
                        return isRow ? rect.RowEnd : rect.ColumnEnd;
                    }
                    if (name == baseName) {
                        if (!isEnd) return isRow ? rect.RowStart : rect.ColumnStart;
                        return isRow ? rect.RowEnd : rect.ColumnEnd;
                    }
                }
            }
            return 0;
        }

        public struct PartialPlacement {
            public int RowStart;
            public int RowEnd;
            public int ColumnStart;
            public int ColumnEnd;
            public int RowStartSpan;
            public int RowEndSpan;
            public int ColumnStartSpan;
            public int ColumnEndSpan;
        }
    }
}
