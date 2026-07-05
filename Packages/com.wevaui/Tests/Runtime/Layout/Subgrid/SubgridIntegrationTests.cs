using NUnit.Framework;
using Weva.Layout.Grid;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Subgrid {
    public class SubgridIntegrationTests {
        [Test]
        public void Subgrid_columns_inherit_parent_column_tracks() {
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 100px 200px 150px;
                    grid-template-rows: 30px;
                    width: 600px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 4;
                    grid-template-columns: subgrid;
                }
                .cell { height: 30px; }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""><div class=""cell""></div><div class=""cell""></div><div class=""cell""></div></div></div>",
                css);
            var child = FindGridByClass(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(3));
            Assert.That(child.ResolvedProperties.Columns.Tracks[0].Value, Is.EqualTo(100));
            Assert.That(child.ResolvedProperties.Columns.Tracks[1].Value, Is.EqualTo(200));
            Assert.That(child.ResolvedProperties.Columns.Tracks[2].Value, Is.EqualTo(150));
        }

        [Test]
        public void Subgrid_columns_only_axis_keeps_explicit_rows() {
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 100px 100px;
                    grid-template-rows: 30px;
                    width: 400px;
                }
                .child {
                    display: grid;
                    grid-column: 1 / 3;
                    grid-template-columns: subgrid;
                    grid-template-rows: 50px 50px;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""></div></div>",
                css);
            var child = FindGridByClass(root, "child");
            Assert.That(child.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(2));
            Assert.That(child.ResolvedProperties.Rows.Tracks.Count, Is.EqualTo(2));
            Assert.That(child.ResolvedProperties.Rows.Tracks[0].Value, Is.EqualTo(50));
        }

        [Test]
        public void Subgrid_rows_inherit_parent_row_tracks() {
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-rows: 40px 80px 30px;
                    grid-template-columns: 100px;
                    width: 200px;
                    height: 300px;
                }
                .child {
                    display: grid;
                    grid-row: 1 / 4;
                    grid-template-rows: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""></div></div>",
                css);
            var child = FindGridByClass(root, "child");
            Assert.That(child.ResolvedProperties.Rows.Tracks.Count, Is.EqualTo(3));
            Assert.That(child.ResolvedProperties.Rows.Tracks[0].Value, Is.EqualTo(40));
            Assert.That(child.ResolvedProperties.Rows.Tracks[1].Value, Is.EqualTo(80));
            Assert.That(child.ResolvedProperties.Rows.Tracks[2].Value, Is.EqualTo(30));
        }

        [Test]
        public void Subgrid_inside_subgrid_walks_up_chain() {
            const string css = @"
                .gp {
                    display: grid;
                    grid-template-columns: 100px 100px 100px;
                    width: 400px;
                }
                .p {
                    display: grid;
                    grid-column: 1 / 4;
                    grid-template-columns: subgrid;
                }
                .c {
                    display: grid;
                    grid-column: 1 / 4;
                    grid-template-columns: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""gp""><div class=""p""><div class=""c""></div></div></div>",
                css);
            var c = FindGridByClass(root, "c");
            Assert.That(c, Is.Not.Null);
            // The intermediate `p` is also subgrid; for v1 we only support direct
            // chaining. The inner `c` slices its parent (p)'s tracks; p's tracks
            // are already resolved to the grandparent slice.
            Assert.That(c.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(3));
        }

        [Test]
        public void Subgrid_falls_back_when_parent_is_not_a_grid() {
            const string css = @"
                .parent { display: block; width: 400px; }
                .child {
                    display: grid;
                    grid-template-columns: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""child""></div></div>",
                css);
            var child = FindGridByClass(root, "child");
            // No parent grid → subgrid degrades to none → empty/auto tracks.
            Assert.That(child.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(0));
        }

        [Test]
        public void Subgrid_partial_span_slices_parent_tracks() {
            const string css = @"
                .parent {
                    display: grid;
                    grid-template-columns: 50px 100px 200px 80px;
                    width: 600px;
                }
                .child {
                    display: grid;
                    grid-column: 2 / 4;
                    grid-template-columns: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""parent""><div class=""filler""></div><div class=""child""></div></div>",
                css);
            var child = FindGridByClass(root, "child");
            Assert.That(child.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(2));
            Assert.That(child.ResolvedProperties.Columns.Tracks[0].Value, Is.EqualTo(100));
            Assert.That(child.ResolvedProperties.Columns.Tracks[1].Value, Is.EqualTo(200));
        }

        [Test]
        public void Subgrid_flag_set_on_container_properties() {
            const string css = @"
                .p { display: grid; grid-template-columns: 100px; width: 200px; }
                .c {
                    display: grid;
                    grid-template-columns: subgrid;
                    grid-template-rows: 40px;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""p""><div class=""c""></div></div>",
                css);
            var c = FindGridByClass(root, "c");
            Assert.That(c.ResolvedProperties.ColumnsSubgrid, Is.True);
            Assert.That(c.ResolvedProperties.RowsSubgrid, Is.False);
        }

        [Test]
        public void Subgrid_resolved_when_both_axes_subgrid() {
            const string css = @"
                .p {
                    display: grid;
                    grid-template-columns: 100px 100px;
                    grid-template-rows: 50px 50px;
                    width: 200px;
                    height: 100px;
                }
                .c {
                    display: grid;
                    grid-column: 1 / 3;
                    grid-row: 1 / 3;
                    grid-template-columns: subgrid;
                    grid-template-rows: subgrid;
                }
            ";
            var (root, _, _) = Build(
                @"<div class=""p""><div class=""c""></div></div>",
                css);
            var c = FindGridByClass(root, "c");
            Assert.That(c.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(2));
            Assert.That(c.ResolvedProperties.Rows.Tracks.Count, Is.EqualTo(2));
        }

        [Test]
        public void Subgrid_does_not_throw_on_track_parser() {
            // The grid track parser must accept "subgrid" as an identifier
            // without raising an error; v0.3 GridTrackParser threw on unknown
            // keywords. v0.4 path: GridContainerProperties intercepts the
            // keyword before invoking the parser.
            const string css = @"
                .p { display: grid; grid-template-columns: 50px; width: 100px; }
                .c { display: grid; grid-template-columns: subgrid; }
            ";
            Assert.DoesNotThrow(() => Build(@"<div class=""p""><div class=""c""></div></div>", css));
        }

        [Test]
        public void Single_axis_subgrid_with_explicit_row_tracks() {
            const string css = @"
                .p {
                    display: grid;
                    grid-template-columns: 75px 75px 75px;
                    grid-template-rows: 30px;
                    width: 300px;
                }
                .c {
                    display: grid;
                    grid-column: 1 / 4;
                    grid-template-columns: subgrid;
                    grid-template-rows: 100px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""p""><div class=""c""></div></div>", css);
            var c = FindGridByClass(root, "c");
            // Columns inherited.
            Assert.That(c.ResolvedProperties.Columns.Tracks.Count, Is.EqualTo(3));
            Assert.That(c.ResolvedProperties.Columns.Tracks[0].Value, Is.EqualTo(75));
            // Rows explicit.
            Assert.That(c.ResolvedProperties.Rows.Tracks.Count, Is.EqualTo(1));
            Assert.That(c.ResolvedProperties.Rows.Tracks[0].Value, Is.EqualTo(100));
        }
    }
}
