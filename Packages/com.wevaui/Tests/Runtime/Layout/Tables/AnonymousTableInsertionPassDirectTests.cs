using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;

namespace Weva.Tests.Layout.Tables {
    // Direct unit coverage for AnonymousTableInsertionPass — the spec
    // (CSS 2.1 §17.2.1, CSS Tables L3 §3.7) "synthesise missing table
    // wrappers" pass. The audit (CODE_AUDIT_FINDINGS.md TG9) flagged that
    // sibling AnonymousTableInsertionTests covers outcomes through full
    // LayoutEngine integration, but the pass itself is never invoked
    // directly. These tests hand-construct minimal box trees, call
    // AnonymousTableInsertionPass.Run(root, pool), and verify the post-
    // pass tree shape — without any cascade / parsing / BlockLayout in
    // the loop.
    //
    // The contract under test:
    //   - bare TableCellBox under non-table parent  → wraps in
    //     anon TableRowBox → anon TableRowGroupBox → anon TableBox
    //   - bare TableRowBox under non-table parent   → wraps in
    //     anon TableRowGroupBox → anon TableBox
    //   - bare TableRowGroupBox under non-table parent → wraps in
    //     anon TableBox
    //   - bare TableBox needs nothing above it (already a table)
    //   - non-cell children of a TableRowBox get wrapped in an
    //     anonymous TableCellBox (CSS Tables L3 §3.7 / tracker I1b)
    //   - properly-nested tree (table → row group → row → cell) is
    //     left alone — no extra synthetic wrappers added
    //   - synthetic wrappers carry Element = null and Style = null
    public class AnonymousTableInsertionPassDirectTests {
        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in Walk(c)) yield return d;
            }
        }

        static List<T> FindAll<T>(Box root) where T : Box {
            var list = new List<T>();
            foreach (var b in Walk(root)) if (b is T t) list.Add(t);
            return list;
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        // The pass requires a BoxPool to allocate synthetic wrappers.
        // We new one up per test to keep state isolated.
        static BoxPool NewPool() => new BoxPool();

        // Bare <table> with a non-row child (a <div> cell — `display:
        // table-cell`-equivalent represented directly as a TableCellBox).
        // Spec: the table needs the cell to live inside a table-row, so the
        // pass must synthesise an anonymous TableRowBox between the table
        // and the cell.
        [Test]
        public void Bare_table_with_direct_cell_child_synthesizes_anonymous_row() {
            var pool = NewPool();
            var root = new BlockBox { Element = new Element("html") };
            var table = new TableBox { Element = new Element("table") };
            var cell = new TableCellBox { Element = new Element("td") };
            table.AddChild(cell);
            root.AddChild(table);

            AnonymousTableInsertionPass.Run(root, pool);

            // The cell must now have a TableRowBox parent, not the table.
            Assert.That(cell.Parent, Is.InstanceOf<TableRowBox>(),
                "Cell directly under a table must be wrapped in a synthetic row.");
            var row = (TableRowBox)cell.Parent;
            Assert.That(row.Element, Is.Null,
                "Synthetic row must be anonymous (Element = null).");
            Assert.That(row.Style, Is.Null,
                "Synthetic row must have null Style (anonymous, not cascaded).");
            Assert.That(row.Parent, Is.SameAs(table),
                "Synthetic row must be parented by the original table (no extra wrappers).");

            // Exactly one row was added.
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1));
        }

        // Bare TableRowBox under a non-table parent (no enclosing table /
        // row-group). Spec: must synthesise a tbody AND a table around the
        // row. The row's original cell child stays parented to the row.
        [Test]
        public void Bare_row_outside_any_table_synthesizes_table_and_tbody_ancestors() {
            var pool = NewPool();
            var root = new BlockBox { Element = new Element("div") };
            var row = new TableRowBox { Element = new Element("tr") };
            var cell = new TableCellBox { Element = new Element("td") };
            row.AddChild(cell);
            root.AddChild(row);

            AnonymousTableInsertionPass.Run(root, pool);

            // Find the synthesised wrappers.
            var tables = FindAll<TableBox>(root);
            Assert.That(tables.Count, Is.EqualTo(1),
                "Bare row must produce exactly one synthetic table wrapper.");
            Assert.That(tables[0].Element, Is.Null,
                "Synthetic table is anonymous.");
            Assert.That(tables[0].Style, Is.Null);

            var groups = FindAll<TableRowGroupBox>(root);
            Assert.That(groups.Count, Is.EqualTo(1),
                "Bare row must produce exactly one synthetic tbody wrapper.");
            Assert.That(groups[0].Element, Is.Null,
                "Synthetic tbody is anonymous.");
            Assert.That(groups[0].Style, Is.Null);

            // Ancestor chain: root → table → tbody → row → cell.
            Assert.That(row.Parent, Is.SameAs(groups[0]),
                "Author row must end up parented by the synthetic tbody.");
            Assert.That(groups[0].Parent, Is.SameAs(tables[0]),
                "Synthetic tbody must be parented by the synthetic table.");
            Assert.That(tables[0].Parent, Is.SameAs(root),
                "Synthetic table sits at the original root.");
            Assert.That(cell.Parent, Is.SameAs(row),
                "Author cell stays attached to the author row through the wrap.");
        }

        // Bare TableCellBox outside any row / row-group / table. Spec must
        // synthesise all three wrappers above the cell — row, tbody, table.
        [Test]
        public void Bare_cell_outside_any_row_synthesizes_full_row_tbody_table_chain() {
            var pool = NewPool();
            var root = new BlockBox { Element = new Element("div") };
            var cell = new TableCellBox { Element = new Element("td") };
            root.AddChild(cell);

            AnonymousTableInsertionPass.Run(root, pool);

            // Three synthetic wrappers above the cell.
            var tables = FindAll<TableBox>(root);
            Assert.That(tables.Count, Is.EqualTo(1),
                "Bare cell must produce exactly one synthetic table.");
            Assert.That(tables[0].Element, Is.Null);

            var groups = FindAll<TableRowGroupBox>(root);
            Assert.That(groups.Count, Is.EqualTo(1),
                "Bare cell must produce exactly one synthetic tbody.");
            Assert.That(groups[0].Element, Is.Null);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "Bare cell must produce exactly one synthetic row.");
            Assert.That(rows[0].Element, Is.Null);

            // Ancestor chain: root → table → tbody → row → cell.
            Assert.That(cell.Parent, Is.SameAs(rows[0]),
                "Cell parented by synthetic row.");
            Assert.That(rows[0].Parent, Is.SameAs(groups[0]),
                "Synthetic row parented by synthetic tbody.");
            Assert.That(groups[0].Parent, Is.SameAs(tables[0]),
                "Synthetic tbody parented by synthetic table.");
            Assert.That(tables[0].Parent, Is.SameAs(root),
                "Synthetic table sits at the original root.");
        }

        // Two consecutive bare cells under one non-table parent must SHARE
        // a single anonymous-wrapper chain (one row, one tbody, one table)
        // per the spec's "consecutive same-kind children share one
        // wrapper" rule. Verifies the grouping behaviour rather than
        // generating one chain per cell.
        [Test]
        public void Two_consecutive_bare_cells_share_one_anonymous_wrapper_chain() {
            var pool = NewPool();
            var root = new BlockBox { Element = new Element("div") };
            var cellA = new TableCellBox { Element = new Element("td") };
            var cellB = new TableCellBox { Element = new Element("td") };
            root.AddChild(cellA);
            root.AddChild(cellB);

            AnonymousTableInsertionPass.Run(root, pool);

            // Exactly one of each wrapper level.
            Assert.That(FindAll<TableBox>(root).Count, Is.EqualTo(1),
                "Both cells must share one synthetic table — not get one each.");
            Assert.That(FindAll<TableRowGroupBox>(root).Count, Is.EqualTo(1),
                "Both cells must share one synthetic tbody.");
            Assert.That(FindAll<TableRowBox>(root).Count, Is.EqualTo(1),
                "Both cells must share one synthetic row (the grouping rule).");

            // Both cells live inside the same synthetic row.
            var row = FindFirst<TableRowBox>(root);
            Assert.That(cellA.Parent, Is.SameAs(row));
            Assert.That(cellB.Parent, Is.SameAs(row));
            // And the row has exactly two cell children, in source order.
            int cellsInRow = 0;
            foreach (var ch in row.Children) if (ch is TableCellBox) cellsInRow++;
            Assert.That(cellsInRow, Is.EqualTo(2),
                "Synthetic row must contain both cells in source order.");
        }

        // I1b — non-cell children of a TableRowBox get wrapped in an
        // anonymous TableCellBox. Build a row containing [cell, BlockBox,
        // cell] and verify the middle BlockBox ends up wrapped in a
        // synthetic cell while the author cells stay untouched.
        [Test]
        public void Non_cell_child_of_row_gets_wrapped_in_anonymous_cell_I1b() {
            var pool = NewPool();
            // Need a fully-wrapped row (so the pass treats the parent as
            // a Row, not a NonTable). Build table → tbody → row.
            var root = new BlockBox { Element = new Element("div") };
            var table = new TableBox { Element = new Element("table") };
            var tbody = new TableRowGroupBox { Element = new Element("tbody") };
            var row = new TableRowBox { Element = new Element("tr") };
            var cellA = new TableCellBox { Element = new Element("td") };
            var stray = new BlockBox { Element = new Element("div") }; // non-cell sibling
            var cellC = new TableCellBox { Element = new Element("td") };
            row.AddChild(cellA);
            row.AddChild(stray);
            row.AddChild(cellC);
            tbody.AddChild(row);
            table.AddChild(tbody);
            root.AddChild(table);

            AnonymousTableInsertionPass.Run(root, pool);

            // The row must contain exactly THREE cells now (A, anon-around-
            // stray, C). The stray div is no longer a direct child.
            int directCells = 0;
            foreach (var ch in row.Children) {
                Assert.That(ch, Is.InstanceOf<TableCellBox>(),
                    "Every direct child of the row must be a TableCellBox after I1b; got " + ch.GetType().Name);
                directCells++;
            }
            Assert.That(directCells, Is.EqualTo(3),
                "Row must have three cells: author A, anon wrap of stray div, author C.");

            // The synthetic middle cell is anonymous; its child is the
            // original stray BlockBox.
            var middle = (TableCellBox)row.Children[1];
            Assert.That(middle.Element, Is.Null,
                "Anonymous wrapping cell carries no Element.");
            Assert.That(middle.Style, Is.Null,
                "Anonymous wrapping cell carries no Style.");
            Assert.That(middle.Children.Count, Is.EqualTo(1),
                "Anonymous cell wraps exactly the one stray child.");
            Assert.That(middle.Children[0], Is.SameAs(stray),
                "Anonymous cell's child must be the original stray BlockBox (identity preserved).");
            Assert.That(stray.Parent, Is.SameAs(middle),
                "Stray's Parent pointer must reflect the new anonymous cell.");

            // Author cells must be untouched (still parented to the row,
            // still carrying their Element).
            Assert.That(cellA.Parent, Is.SameAs(row));
            Assert.That(cellA.Element, Is.Not.Null);
            Assert.That(cellC.Parent, Is.SameAs(row));
            Assert.That(cellC.Element, Is.Not.Null);
        }

        // Properly-nested table tree must be left ALONE by the pass — no
        // extra synthetic wrappers, no Parent re-pointing. Regression pin
        // against a "wrap-everything" misfire that would force valid trees
        // through an unnecessary wrap-detach-rewrap cycle.
        [Test]
        public void Properly_nested_table_tree_is_left_untouched() {
            var pool = NewPool();
            var root = new BlockBox { Element = new Element("div") };
            var table = new TableBox { Element = new Element("table") };
            var tbody = new TableRowGroupBox { Element = new Element("tbody") };
            var row = new TableRowBox { Element = new Element("tr") };
            var cell = new TableCellBox { Element = new Element("td") };
            row.AddChild(cell);
            tbody.AddChild(row);
            table.AddChild(tbody);
            root.AddChild(table);

            AnonymousTableInsertionPass.Run(root, pool);

            // No extra anything.
            Assert.That(FindAll<TableBox>(root).Count, Is.EqualTo(1),
                "Properly-nested table must not gain a second TableBox.");
            Assert.That(FindAll<TableRowGroupBox>(root).Count, Is.EqualTo(1),
                "Properly-nested table must not gain a second tbody.");
            Assert.That(FindAll<TableRowBox>(root).Count, Is.EqualTo(1),
                "Properly-nested table must not gain a second row.");
            Assert.That(FindAll<TableCellBox>(root).Count, Is.EqualTo(1),
                "Properly-nested table must not gain a second cell.");

            // No Parent pointers were nullified — the original chain
            // survives unbroken.
            Assert.That(cell.Parent, Is.SameAs(row));
            Assert.That(row.Parent, Is.SameAs(tbody));
            Assert.That(tbody.Parent, Is.SameAs(table));
            Assert.That(table.Parent, Is.SameAs(root));
        }

        // Bare TableRowGroupBox (e.g. a stray `display: table-row-group`
        // div) outside any table. Spec: wrap in a synthetic table only —
        // not in a second tbody.
        [Test]
        public void Bare_row_group_outside_table_synthesizes_table_wrapper_only() {
            var pool = NewPool();
            var root = new BlockBox { Element = new Element("div") };
            var tbody = new TableRowGroupBox { Element = new Element("tbody") };
            var row = new TableRowBox { Element = new Element("tr") };
            var cell = new TableCellBox { Element = new Element("td") };
            row.AddChild(cell);
            tbody.AddChild(row);
            root.AddChild(tbody);

            AnonymousTableInsertionPass.Run(root, pool);

            // Exactly one synthetic table wrapping the existing tbody.
            var tables = FindAll<TableBox>(root);
            Assert.That(tables.Count, Is.EqualTo(1),
                "Bare row-group must produce exactly one synthetic table wrapper.");
            Assert.That(tables[0].Element, Is.Null,
                "Synthetic table is anonymous.");

            // The author tbody / row / cell must be the only ones (no
            // extra synthetic tbody / row inserted).
            var groups = FindAll<TableRowGroupBox>(root);
            Assert.That(groups.Count, Is.EqualTo(1),
                "Author tbody must survive — no second synthetic tbody added.");
            Assert.That(groups[0], Is.SameAs(tbody),
                "The surviving row-group must be the author's, not a fresh anonymous one.");
            Assert.That(FindAll<TableRowBox>(root).Count, Is.EqualTo(1));
            Assert.That(FindAll<TableCellBox>(root).Count, Is.EqualTo(1));

            // Parent chain: root → synthetic table → author tbody → row → cell.
            Assert.That(tbody.Parent, Is.SameAs(tables[0]));
            Assert.That(tables[0].Parent, Is.SameAs(root));
        }

        // Null root / null pool early-out: the pass must be defensive
        // against null inputs (called from BoxBuilder and SnapshotBoxBuilder
        // where the root could theoretically be unbuilt). Don't crash.
        [Test]
        public void Null_root_or_pool_returns_without_crashing() {
            Assert.DoesNotThrow(() => AnonymousTableInsertionPass.Run(null, new BoxPool()));
            var dummy = new BlockBox { Element = new Element("div") };
            Assert.DoesNotThrow(() => AnonymousTableInsertionPass.Run(dummy, null));
            // Both null: still safe.
            Assert.DoesNotThrow(() => AnonymousTableInsertionPass.Run(null, null));
        }
    }
}
