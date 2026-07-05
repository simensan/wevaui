using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout.Grid {
    // Direct unit coverage of GridAutoPlacement (CSS Grid L1 §8.5 sparse/dense
    // placement) and GridPlacementResolver (named-line / span / negative-index
    // resolution). These tests bypass the HTML/CSS parse pipeline that the
    // sibling integration suites use and exercise the static APIs directly so
    // each algorithmic branch can be pinned in isolation.
    //
    // Tracker references: CODE_AUDIT_FINDINGS.md TG12 (GridAutoPlacement) and
    // TG13 (GridPlacementResolver). Both were flagged as 0-hit direct test
    // coverage; this file establishes the baseline.
    public class GridAutoPlacementUnitTests {
        // ----- Helpers -------------------------------------------------------

        static GridContainerProperties Props(GridAutoFlow flow = GridAutoFlow.Row,
                                             GridAreasParser.AreasMap areas = null,
                                             GridTemplate columns = null,
                                             GridTemplate rows = null) {
            return new GridContainerProperties {
                Columns = columns ?? GridTemplate.Empty,
                Rows = rows ?? GridTemplate.Empty,
                AutoColumns = new[] { GridTrackSize.Auto },
                AutoRows = new[] { GridTrackSize.Auto },
                AutoFlow = flow,
                Areas = areas ?? GridAreasParser.AreasMap.Empty
            };
        }

        static GridPlacementResolver.PartialPlacement Auto() => new GridPlacementResolver.PartialPlacement();

        static GridPlacementResolver.PartialPlacement Explicit(int rs, int re, int cs, int ce) {
            return new GridPlacementResolver.PartialPlacement {
                RowStart = rs, RowEnd = re, ColumnStart = cs, ColumnEnd = ce
            };
        }

        static GridPlacementResolver.PartialPlacement SpanCols(int n) {
            return new GridPlacementResolver.PartialPlacement {
                ColumnStartSpan = n
            };
        }

        static GridPlacementResolver.PartialPlacement SpanBoth(int rowSpan, int colSpan) {
            return new GridPlacementResolver.PartialPlacement {
                RowStartSpan = rowSpan, ColumnStartSpan = colSpan
            };
        }

        // ----- GridAutoPlacement: sparse row-major ---------------------------

        [Test]
        public void Sparse_row_major_fills_three_items_left_to_right_top_to_bottom() {
            // 2x2 explicit grid, three auto items.
            // Expected fill order (sparse default): (1,1) (1,2) (2,1).
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                Auto(), Auto(), Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 2, explicitColumns: 2);

            Assert.That(result.ItemAreas[0].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[0].ColumnStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[2].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[2].ColumnStart, Is.EqualTo(1));
        }

        [Test]
        public void Sparse_row_major_wraps_to_next_row_when_column_exhausted() {
            // 3 columns, 4 items -> rows 1/1/1/2; cols 1/2/3/1.
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                Auto(), Auto(), Auto(), Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 2, explicitColumns: 3);

            Assert.That(result.ItemAreas[3].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[3].ColumnStart, Is.EqualTo(1));
        }

        [Test]
        public void Sparse_row_major_skips_explicitly_occupied_cell() {
            // Item 0 placed at row 2, col 3. Items 1 / 2 / 3 / 4 auto-flow row-major:
            // (1,1), (1,2), (1,3), (2,1). Item 5 would be (2,2), avoiding (2,3).
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                Explicit(2, 3, 3, 4), Auto(), Auto(), Auto(), Auto(), Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 2, explicitColumns: 3);

            Assert.That(result.ItemAreas[0].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[0].ColumnStart, Is.EqualTo(3));
            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[4].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[4].ColumnStart, Is.EqualTo(1));
            // Item 5 (sparse cursor at row=2 col=2): must skip the occupied (2,3).
            Assert.That(result.ItemAreas[5].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[5].ColumnStart, Is.EqualTo(2));
        }

        // ----- GridAutoPlacement: dense --------------------------------------

        [Test]
        public void Dense_row_backfills_earlier_hole_left_by_wide_item() {
            // 3 cols. Item 0 = col-fixed (cols 2..4) row-auto; in sparse mode
            // that advances the auto cursor to row 1 col 4 and pushes the
            // subsequent narrow auto item to row 2. With dense, the cursor
            // is ignored and the small auto item backfills (1,1).
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                new GridPlacementResolver.PartialPlacement {
                    ColumnStart = 2, ColumnEnd = 4
                },
                Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(GridAutoFlow.RowDense),
                                                  explicitRows: 2, explicitColumns: 3);

            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(1));
        }

        [Test]
        public void Sparse_row_does_not_backfill_earlier_hole_when_cursor_advanced() {
            // .a has grid-column: 2 / span 2 (col-fixed, row-auto). The
            // col-fixed/row-auto branch in sparse mode advances the auto
            // cursor's minor index to col=4. .b (fully auto) then can't fit
            // in the remaining sliver of row 1 and wraps to row 2 col 1 —
            // it does NOT backfill the (1,1) hole. Mirrors the integration
            // assertion in GridPlacementTests.Sparse_vs_dense_differs_when_holes_exist
            // but at the algorithm level.
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                new GridPlacementResolver.PartialPlacement {
                    ColumnStart = 2, ColumnEnd = 4
                },
                Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(GridAutoFlow.Row),
                                                  explicitRows: 2, explicitColumns: 3);

            // .a sits on row 1, cols 2..4.
            Assert.That(result.ItemAreas[0].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[0].ColumnStart, Is.EqualTo(2));
            // .b sparse-flows to row 2 col 1 (NOT row 1 col 1).
            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(1));
        }

        // ----- GridAutoPlacement: column flow --------------------------------

        [Test]
        public void Column_flow_fills_top_to_bottom_then_next_column() {
            // 2x2 grid, 3 auto items, grid-auto-flow: column.
            // Expected: (1,1), (2,1), (1,2).
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                Auto(), Auto(), Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(GridAutoFlow.Column),
                                                  explicitRows: 2, explicitColumns: 2);

            Assert.That(result.ItemAreas[0].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[0].ColumnStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[2].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[2].ColumnStart, Is.EqualTo(2));
        }

        [Test]
        public void Column_dense_backfills_earlier_column_hole() {
            // Item 0 spans rows 2..3 in col 1; column-dense should put item 1
            // back at (1,1) — sparse column flow would otherwise leave that hole.
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                new GridPlacementResolver.PartialPlacement {
                    RowStart = 2, RowEnd = 4, ColumnStart = 1, ColumnEnd = 2
                },
                Auto()
            };
            var result = GridAutoPlacement.Place(partials, Props(GridAutoFlow.ColumnDense),
                                                  explicitRows: 3, explicitColumns: 2);

            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(1));
        }

        // ----- GridAutoPlacement: spans --------------------------------------

        [Test]
        public void Auto_item_with_span_two_columns_finds_two_wide_slot() {
            // 3 cols. Item 0 = span 2 cols -> (1, cols 1..3).
            // Item 1 (span 1) -> (1, col 3). Item 2 (span 2) wraps to row 2.
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                SpanCols(2), Auto(), SpanCols(2)
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 2, explicitColumns: 3);

            Assert.That(result.ItemAreas[0].ColumnStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[0].ColumnEnd, Is.EqualTo(3));
            Assert.That(result.ItemAreas[0].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].RowStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[1].ColumnStart, Is.EqualTo(3));
            Assert.That(result.ItemAreas[2].RowStart, Is.EqualTo(2));
            Assert.That(result.ItemAreas[2].ColumnStart, Is.EqualTo(1));
        }

        [Test]
        public void Auto_item_with_two_axis_spans_places_into_both_axes() {
            // grid-row: span 2; grid-column: span 2 -> 2x2 block at top-left.
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                SpanBoth(2, 2)
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 3, explicitColumns: 3);

            var area = result.ItemAreas[0];
            Assert.That(area.RowStart, Is.EqualTo(1));
            Assert.That(area.RowEnd, Is.EqualTo(3));
            Assert.That(area.ColumnStart, Is.EqualTo(1));
            Assert.That(area.ColumnEnd, Is.EqualTo(3));
        }

        [Test]
        public void Explicit_grid_column_1_to_3_with_row_span_2_pins_both_axes() {
            // Item declares: grid-row: 1 / span 2; grid-column: 1 / 3.
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                new GridPlacementResolver.PartialPlacement {
                    RowStart = 1, RowEndSpan = 2,
                    ColumnStart = 1, ColumnEnd = 3
                }
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 2, explicitColumns: 3);

            var area = result.ItemAreas[0];
            Assert.That(area.RowStart, Is.EqualTo(1));
            Assert.That(area.RowEnd, Is.EqualTo(3));
            Assert.That(area.ColumnStart, Is.EqualTo(1));
            Assert.That(area.ColumnEnd, Is.EqualTo(3));
        }

        [Test]
        public void Item_spanning_more_columns_than_grid_extends_implicit_grid() {
            // 2-column explicit grid; auto item with column span 3 forces the
            // implicit grid to widen to 3 columns (per §8.5 grid expansion).
            var partials = new List<GridPlacementResolver.PartialPlacement> {
                SpanCols(3)
            };
            var result = GridAutoPlacement.Place(partials, Props(), explicitRows: 1, explicitColumns: 2);

            Assert.That(result.Columns, Is.GreaterThanOrEqualTo(3));
            Assert.That(result.ItemAreas[0].ColumnStart, Is.EqualTo(1));
            Assert.That(result.ItemAreas[0].ColumnEnd, Is.EqualTo(4));
        }

        // ----- GridPlacementResolver: named lines ----------------------------

        [Test]
        public void Named_lines_resolve_start_and_end_to_correct_positions() {
            // grid-template-columns: [start] 1fr [middle] 1fr [end]
            // -> 3 line slots: 0 -> "start", 1 -> "middle", 2 -> "end".
            var lineNames = new List<IReadOnlyList<string>> {
                new[] { "start" }, new[] { "middle" }, new[] { "end" }
            };
            var cols = new GridTemplate(
                new[] { GridTrackSize.Fr(1), GridTrackSize.Fr(1) },
                lineNames);
            var props = Props(columns: cols);

            var placement = new GridItemPlacement(
                GridLineRef.Auto, GridLineRef.Auto,
                GridLineRef.NameValue("start"), GridLineRef.NameValue("end"),
                areaName: null);

            var resolved = GridPlacementResolver.Resolve(placement, props,
                explicitRowLines: 2, explicitColumnLines: 3);

            Assert.That(resolved.ColumnStart, Is.EqualTo(1));
            Assert.That(resolved.ColumnEnd, Is.EqualTo(3));
        }

        [Test]
        public void Negative_line_index_resolves_against_explicit_line_count() {
            // 4-column grid -> 5 lines. grid-column: 1 / -1 spans all four
            // tracks: start=1, end = 5 + (-1) + 1 = 5.
            var props = Props();
            var placement = new GridItemPlacement(
                GridLineRef.Auto, GridLineRef.Auto,
                GridLineRef.IndexValue(1), GridLineRef.IndexValue(-1),
                areaName: null);

            var resolved = GridPlacementResolver.Resolve(placement, props,
                explicitRowLines: 2, explicitColumnLines: 5);

            Assert.That(resolved.ColumnStart, Is.EqualTo(1));
            Assert.That(resolved.ColumnEnd, Is.EqualTo(5));
        }

        [Test]
        public void Span_keyword_resolves_to_span_count_only_when_start_is_set() {
            // grid-column: 1 / span 2 -> start=1, endSpan=2.
            var props = Props();
            var placement = new GridItemPlacement(
                GridLineRef.Auto, GridLineRef.Auto,
                GridLineRef.IndexValue(1), GridLineRef.Span(2),
                areaName: null);

            var resolved = GridPlacementResolver.Resolve(placement, props,
                explicitRowLines: 2, explicitColumnLines: 4);

            Assert.That(resolved.ColumnStart, Is.EqualTo(1));
            Assert.That(resolved.ColumnEnd, Is.EqualTo(0));
            Assert.That(resolved.ColumnEndSpan, Is.EqualTo(2));
        }

        [Test]
        public void Auto_on_both_axes_leaves_zero_for_GridAutoPlacement_to_resolve() {
            // grid-row: auto; grid-column: auto -> all-zero PartialPlacement,
            // signalling that GridAutoPlacement should fully auto-place this item.
            var props = Props();
            var resolved = GridPlacementResolver.Resolve(GridItemPlacement.AllAuto, props,
                explicitRowLines: 3, explicitColumnLines: 3);

            Assert.That(resolved.RowStart, Is.EqualTo(0));
            Assert.That(resolved.RowEnd, Is.EqualTo(0));
            Assert.That(resolved.ColumnStart, Is.EqualTo(0));
            Assert.That(resolved.ColumnEnd, Is.EqualTo(0));
            Assert.That(resolved.RowStartSpan, Is.EqualTo(0));
            Assert.That(resolved.ColumnStartSpan, Is.EqualTo(0));

            // Confirm the all-zero shape is what GridAutoPlacement treats as
            // fully-auto (i.e. participates in the cursor walk).
            var place = GridAutoPlacement.Place(
                new List<GridPlacementResolver.PartialPlacement> { resolved },
                props, explicitRows: 2, explicitColumns: 2);
            Assert.That(place.ItemAreas[0].RowStart, Is.EqualTo(1));
            Assert.That(place.ItemAreas[0].ColumnStart, Is.EqualTo(1));
        }

        [Test]
        public void Custom_ident_with_negative_integer_picks_nth_from_end() {
            // Three lines named "col" (slots 0,1,2). `col -1` -> last (slot 2 -> line 3).
            var lineNames = new List<IReadOnlyList<string>> {
                new[] { "col" }, new[] { "col" }, new[] { "col" }
            };
            var cols = new GridTemplate(
                new[] { GridTrackSize.Length(50), GridTrackSize.Length(50) },
                lineNames);
            var props = Props(columns: cols);

            var placement = new GridItemPlacement(
                GridLineRef.Auto, GridLineRef.Auto,
                GridLineRef.NameValue("col", 1), GridLineRef.NameValue("col", -1),
                areaName: null);

            var resolved = GridPlacementResolver.Resolve(placement, props,
                explicitRowLines: 2, explicitColumnLines: 3);

            Assert.That(resolved.ColumnStart, Is.EqualTo(1));
            Assert.That(resolved.ColumnEnd, Is.EqualTo(3));
        }

        [Test]
        public void Named_area_grid_area_resolves_to_areas_rectangle() {
            // Build a 2x2 areas map with `a` covering the top row, `b` at (2,1),
            // `c` at (2,2). grid-area: a -> rows 1..2 cols 1..3.
            var areasDict = new Dictionary<string, GridArea> {
                ["a"] = new GridArea(1, 2, 1, 3),
                ["b"] = new GridArea(2, 3, 1, 2),
                ["c"] = new GridArea(2, 3, 2, 3)
            };
            var areas = new GridAreasParser.AreasMap(rows: 2, columns: 2, areas: areasDict);
            var props = Props(areas: areas);

            var placement = new GridItemPlacement(
                GridLineRef.Auto, GridLineRef.Auto,
                GridLineRef.Auto, GridLineRef.Auto,
                areaName: "a");

            var resolved = GridPlacementResolver.Resolve(placement, props,
                explicitRowLines: 3, explicitColumnLines: 3);

            Assert.That(resolved.RowStart, Is.EqualTo(1));
            Assert.That(resolved.RowEnd, Is.EqualTo(2));
            Assert.That(resolved.ColumnStart, Is.EqualTo(1));
            Assert.That(resolved.ColumnEnd, Is.EqualTo(3));
        }
    }
}
