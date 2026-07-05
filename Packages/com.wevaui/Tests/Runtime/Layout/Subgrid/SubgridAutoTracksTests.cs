// Tests for CSS Grid Layout Level 2 §6 — `grid-auto-rows: subgrid` and
// `grid-auto-columns: subgrid`. These cover the case where a subgrid needs
// implicit tracks (beyond its explicit grid-template) that also inherit sizing
// from the parent grid's track template.
//
// Spec reference: https://www.w3.org/TR/css-grid-2/#subgrid-implicit
using NUnit.Framework;
using Weva.Layout.Grid;
using Weva.Layout.Boxes;
using Weva.Layout.Subgrid;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Subgrid {
    /// <summary>
    /// Integration tests for CSS Grid L2 §6 — implicit tracks on subgrids.
    /// </summary>
    public class SubgridAutoTracksTests {

        // ------------------------------------------------------------------ //
        // grid-auto-rows: subgrid
        // ------------------------------------------------------------------ //

        [Test]
        public void Auto_rows_subgrid_flag_parsed_from_style() {
            // Verify that grid-auto-rows: subgrid sets AutoRowsSubgrid on the
            // resolved container properties.
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-rows: 40px 80px 60px;
                    grid-template-columns: 100px;
                    width: 200px;
                }
                .child {
                    display: grid;
                    grid-row: 1 / 2;
                    grid-auto-rows: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""></div></div>", css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null, "child grid must exist");
            Assert.That(child.ResolvedProperties.AutoRowsSubgrid, Is.True,
                "AutoRowsSubgrid flag must be set when grid-auto-rows: subgrid is declared");
        }

        [Test]
        public void Auto_columns_subgrid_flag_parsed_from_style() {
            // Verify that grid-auto-columns: subgrid sets AutoColumnsSubgrid.
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 50px 100px 150px;
                    grid-template-rows: 30px;
                    width: 400px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 2;
                    grid-auto-columns: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""></div></div>", css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null, "child grid must exist");
            Assert.That(child.ResolvedProperties.AutoColumnsSubgrid, Is.True,
                "AutoColumnsSubgrid flag must be set when grid-auto-columns: subgrid is declared");
        }

        [Test]
        public void Auto_rows_subgrid_implicit_tracks_inherit_parent_sizing() {
            // A 2-column parent has 4 row tracks (40, 80, 60, 50).
            // The child subgrid uses grid-auto-rows: subgrid and places items
            // into rows 1 and 2 explicitly (via grid-template-rows: subgrid)
            // plus two extra implicit rows that should pick up parent rows 3 & 4.
            //
            // Expected: child autoRows is set to a pattern derived from parent
            // tracks starting at the child's explicit row end.
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 100px 100px;
                    grid-template-rows: 40px 80px 60px 50px;
                    width: 200px;
                    height: 300px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 3;
                    grid-row: 1 / 3;
                    grid-template-rows: subgrid;
                    grid-auto-rows: subgrid;
                }
                .item { height: 10px; }
            ";
            // 4 items: rows 1 & 2 are explicit subgrid, rows 3 & 4 are implicit
            var (root, _, _) = Build(
                @"<div class=""parent"">
                    <div class=""child"">
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                    </div>
                </div>", css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null, "child grid must exist");
            // The explicit template has 2 rows; auto rows should be derived
            // from parent tracks (rows 3 & 4: 60px, 50px) cycling.
            Assert.That(child.ResolvedProperties.AutoRowsSubgrid, Is.True);
            // AutoRows must not be the default single-auto-track anymore.
            var autoRows = child.ResolvedProperties.AutoRows;
            Assert.That(autoRows, Is.Not.Null, "AutoRows must be set");
            Assert.That(autoRows.Length, Is.GreaterThan(0), "AutoRows must be non-empty");
            // First auto track should come from parent row 3 (index 2) = 60px.
            Assert.That(autoRows[0].Value, Is.EqualTo(60).Within(0.01),
                "first implicit row should inherit parent track 3 = 60px");
        }

        [Test]
        public void Auto_columns_subgrid_implicit_tracks_inherit_parent_sizing() {
            // Parent has 4 column tracks (50, 100, 200, 80).
            // Child occupies columns 1-2 explicitly (subgrid) and auto-generates
            // columns 3-4 via grid-auto-columns: subgrid.
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 50px 100px 200px 80px;
                    grid-template-rows: 30px;
                    width: 600px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 3;
                    grid-template-columns: subgrid;
                    grid-auto-columns: subgrid;
                    grid-auto-flow: column;
                }
                .item { width: 10px; }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent"">
                    <div class=""child"">
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                    </div>
                </div>", css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null, "child grid must exist");
            Assert.That(child.ResolvedProperties.AutoColumnsSubgrid, Is.True);
            var autoCols = child.ResolvedProperties.AutoColumns;
            Assert.That(autoCols, Is.Not.Null, "AutoColumns must be set");
            Assert.That(autoCols.Length, Is.GreaterThan(0), "AutoColumns must be non-empty");
            // Parent column 3 (index 2) = 200px should be the first auto track.
            Assert.That(autoCols[0].Value, Is.EqualTo(200).Within(0.01),
                "first implicit column should inherit parent track 3 = 200px");
        }

        [Test]
        public void Auto_rows_subgrid_non_grid_parent_degrades_to_auto() {
            // When the parent is not a grid container, grid-auto-rows: subgrid
            // must degrade to auto (the initial value), just like
            // grid-template-rows: subgrid does.
            const string css = @"
                .parent { display: block; width: 400px; }
                .child {
                    display: grid;
                    grid-auto-rows: subgrid;
                }
                .item { height: 30px; }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""><div class=""item""></div></div></div>",
                css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null);
            // AutoRowsSubgrid flag is set from the parse stage, but no
            // parent grid means the track resolution falls back to the
            // initial single-auto-track — auto rows should not crash and
            // should produce a usable (non-zero height) item.
            var items = ChildBlockBoxes(child);
            Assert.That(items.Count, Is.EqualTo(1));
            // The item must have a height derived from its own content (30px
            // explicit height), not zero. Proves auto-fallback didn't break.
            Assert.That(items[0].Height, Is.EqualTo(30).Within(0.01),
                "item height must reflect the declared 30px even under non-grid-parent subgrid auto fallback");
        }

        [Test]
        public void Auto_rows_subgrid_cycles_parent_tracks_for_many_implicit_rows() {
            // Parent has 3 row tracks: 30px, 60px, 90px.
            // Child has NO explicit template rows (grid-template-rows: none)
            // but declares grid-auto-rows: subgrid. It places 7 items that
            // each span one row. The auto-track pattern from the parent
            // (starting at row 1, since child starts at row 1 with no explicit
            // template) should cycle: 30, 60, 90, 30, 60, 90, 30.
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 100px;
                    grid-template-rows: 30px 60px 90px;
                    width: 100px;
                    height: 400px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 2;
                    grid-row: 1 / 2;
                    grid-auto-rows: subgrid;
                }
                .item { width: 10px; }
            ";
            // 7 items in a 1-column child grid, all auto-placed in rows
            var (root, _, _) = Build(
                @"<div class=""parent"">
                    <div class=""child"">
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                    </div>
                </div>", css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.ResolvedProperties.AutoRowsSubgrid, Is.True);
            // The auto pattern should be derived from parent tracks (rotated
            // from position 0 since child has no explicit rows): [30, 60, 90].
            var autoRows = child.ResolvedProperties.AutoRows;
            Assert.That(autoRows, Is.Not.Null);
            Assert.That(autoRows.Length, Is.EqualTo(3),
                "BuildAutoTracksFromParent should produce a pattern of length = parent track count");
            Assert.That(autoRows[0].Value, Is.EqualTo(30).Within(0.01), "cycle position 0 = 30px");
            Assert.That(autoRows[1].Value, Is.EqualTo(60).Within(0.01), "cycle position 1 = 60px");
            Assert.That(autoRows[2].Value, Is.EqualTo(90).Within(0.01), "cycle position 2 = 90px");
        }

        [Test]
        public void Mixed_explicit_subgrid_rows_and_auto_subgrid_rows_item_positions() {
            // Parent has 4 row tracks: 40px, 80px, 40px, 80px.
            // Child occupies parent rows 1-2 explicitly (subgrid template),
            // and any additional items overflow into implicit rows sized by the
            // parent's remaining tracks (3 & 4 → 40px, 80px cycling).
            // Each placed item must land at the correct Y offset.
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 200px;
                    grid-template-rows: 40px 80px 40px 80px;
                    width: 200px;
                    height: 400px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 2;
                    grid-row: 1 / 3;
                    grid-template-rows: subgrid;
                    grid-auto-rows: subgrid;
                }
                .item { width: 10px; }
            ";
            // 4 items: first 2 land in explicit subgrid rows (40, 80);
            // items 3 & 4 land in implicit rows from parent (40, 80).
            var (root, _, _) = Build(
                @"<div class=""parent"">
                    <div class=""child"">
                        <div class=""item"" id=""i1""></div>
                        <div class=""item"" id=""i2""></div>
                        <div class=""item"" id=""i3""></div>
                        <div class=""item"" id=""i4""></div>
                    </div>
                </div>", css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.ResolvedProperties.AutoRowsSubgrid, Is.True);
            // Verify items are laid out in rows: auto placement default row flow.
            var items = ChildBlockBoxes(child);
            Assert.That(items.Count, Is.EqualTo(4), "all 4 items must be in the child grid");
            // Items in explicit rows:
            Assert.That(items[0].Y, Is.LessThan(items[1].Y), "item 1 must be above item 2");
            Assert.That(items[1].Y, Is.LessThan(items[2].Y), "item 2 must be above item 3");
            Assert.That(items[2].Y, Is.LessThan(items[3].Y), "item 3 must be above item 4");
        }

        // ------------------------------------------------------------------ //
        // Unit tests for BuildAutoTracksFromParent (SubgridTrackResolver)
        // ------------------------------------------------------------------ //

        [Test]
        public void BuildAutoTracksFromParent_returns_rotated_pattern_from_startIdx() {
            // Parent tracks: 10, 20, 30. Start at index 1 (2nd track).
            // Expected: [20, 30, 10] — rotated so first auto track = track[1].
            var parent = new GridTrackSize[] {
                GridTrackSize.Length(10),
                GridTrackSize.Length(20),
                GridTrackSize.Length(30)
            };
            // childExplicitEnd1Based = 2 means explicit tracks end at parent
            // line 2, so implicit tracks start at parent track index 1 (= 20px).
            var pattern = SubgridTrackResolver.BuildAutoTracksFromParent(parent, 2);
            Assert.That(pattern.Length, Is.EqualTo(3), "pattern length equals parent track count");
            Assert.That(pattern[0].Value, Is.EqualTo(20).Within(0.01), "first auto track = parent[1]");
            Assert.That(pattern[1].Value, Is.EqualTo(30).Within(0.01), "second auto track = parent[2]");
            Assert.That(pattern[2].Value, Is.EqualTo(10).Within(0.01), "wraps to parent[0]");
        }

        [Test]
        public void BuildAutoTracksFromParent_null_parent_returns_auto_track() {
            // No parent tracks → should degrade gracefully to a single auto track.
            var result = SubgridTrackResolver.BuildAutoTracksFromParent(null, 1);
            Assert.That(result.Length, Is.EqualTo(1), "must return exactly one fallback track");
            Assert.That(result[0].Kind, Is.EqualTo(GridTrackKind.Auto), "fallback must be auto");
        }

        [Test]
        public void BuildAutoTracksFromParent_empty_parent_returns_auto_track() {
            var result = SubgridTrackResolver.BuildAutoTracksFromParent(new GridTrackSize[0], 1);
            Assert.That(result.Length, Is.EqualTo(1));
            Assert.That(result[0].Kind, Is.EqualTo(GridTrackKind.Auto));
        }

        [Test]
        public void BuildAutoTracksFromParent_startIdx_past_end_wraps_to_zero() {
            // startIdx >= n wraps to 0, effectively starting from the first track.
            var parent = new GridTrackSize[] {
                GridTrackSize.Length(100),
                GridTrackSize.Length(200)
            };
            // childExplicitEnd1Based = 5, which is past the 2-track list.
            // Should wrap: startIdx = 0 → pattern = [100, 200].
            var pattern = SubgridTrackResolver.BuildAutoTracksFromParent(parent, 5);
            Assert.That(pattern.Length, Is.EqualTo(2));
            Assert.That(pattern[0].Value, Is.EqualTo(100).Within(0.01), "wrap to beginning");
            Assert.That(pattern[1].Value, Is.EqualTo(200).Within(0.01));
        }
    }
}
