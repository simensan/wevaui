using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Tables {
    // CSS 2.1 §17.2.1 / CSS Tables Module L3 §3.7 — "anonymous table objects".
    //
    // The spec requires that when an element with `display: table-cell` (or
    // `table-row` / `table-row-group` / `table-header-group` / `table-footer-group`
    // / `table-column` / `table-column-group` / `table-caption`) appears in the
    // box tree without the enclosing table parents it expects, the engine
    // SYNTHESIZES the missing wrappers so the cell can participate in table
    // sizing. Concretely:
    //
    //   - `table-cell` without a `table-row` parent  → wrap in anonymous row
    //   - `table-row` without a row-group / table parent → wrap in anonymous tbody
    //   - `table-row-group` / `table-header-group` / `table-footer-group`
    //     without a `table` / `inline-table` parent → wrap in anonymous table
    //   - `table-caption` without a `table` / `inline-table` parent → wrap in
    //     anonymous table
    //   - `table-column` / `table-column-group` without a `table` /
    //     `inline-table` (or column-group, for `table-column`) parent → wrap
    //     in anonymous table
    //
    // CONSECUTIVE same-kind table-internal siblings that need the SAME wrapper
    // type share ONE wrapper rather than getting one each — this matches the
    // spec's grouping rule and gives the natural table layout where two bare
    // `display: table-cell` divs lay out side-by-side in a single synthetic row.
    //
    // Synthetic boxes are marked anonymous (Element = null, Style = null) so
    // they don't participate in selector matching / cascade. Layout code that
    // reads `box.Style` is already null-tolerant (TableLayout.ResolveBorderSpacing
    // explicitly handles `table.Style == null`; cell/row helpers null-check
    // before reading).
    //
    // CSS Tables L3 §3.7 also requires "non-table-cell" content sitting as a
    // direct child of a table-row to be wrapped in an anonymous table-cell so
    // it participates in the column algorithm (tracker I1b). The common shape
    // by the time this pass runs is that BoxFinalize has already swept the
    // inline-flow run that sat between two `<td>` siblings into a single
    // AnonymousBlockBox, so the row sees `[cell, anon-block, cell]`. We wrap
    // each non-cell child (AnonymousBlockBox, InlineBox, non-cell BlockBox,
    // direct TextRun in the all-text row case) in an anonymous TableCellBox.
    // Consecutive non-cell children share one synthetic cell per the spec's
    // "no anonymous table object can be empty" grouping rule. Whitespace-only
    // direct TextRun children are skipped — per spec, whitespace between
    // table-internal boxes does not generate an anonymous cell. (Whitespace
    // between cells reaches the row as either zero children — BoxFinalize's
    // FlushAnonymous drops the whitespace AnonymousBlockBox — or as a
    // standalone TextRun in the all-text row case.)
    //
    // Pass shape: walks the box tree top-down, fixing up each BlockBox parent's
    // Children list before descending. Top-down is safe because each fixup
    // wraps existing children — it never replaces them, so descendants seen
    // later in the walk still exist with intact identity.
    internal static class AnonymousTableInsertionPass {
        // Public entry point. Called by BoxBuilder.BuildDocument / SnapshotBoxBuilder.
        // BuildFromSnapshot after the initial box tree is constructed. `pool`
        // supplies anonymous TableBox / TableRowGroupBox / TableRowBox
        // instances; pooling is preserved across passes.
        public static void Run(Box root, BoxPool pool) {
            if (root == null || pool == null) return;
            Fixup(root, pool);
        }

        // Depth-first walk. Fix up `parent` first (so its children list shape
        // is stable), then recurse into the resulting children.
        //
        // Each `NeededWrapper` call returns the OUTERMOST missing wrapper
        // level only. Wrapping a bare table-cell with a synthetic row leaves
        // the synthetic row itself ill-placed (its parent is still NonTable),
        // so it would need a tbody wrapper on the next pass — and after that
        // tbody needs a table wrapper. We therefore loop until the children
        // list is stable: at most three iterations are needed
        // (cell → row → tbody → table), so the loop is bounded by spec depth.
        static void Fixup(Box parent, BoxPool pool) {
            if (parent == null) return;
            // Bounded loop: each iteration adds at most one wrapper level
            // (cell→row, row→tbody, group→table). A 4-iteration ceiling
            // catches "cell with NonTable parent" (3 wraps to settle) with
            // one extra slot as a safety margin against pathological input.
            for (int iter = 0; iter < 4; iter++) {
                if (!FixupChildrenOf(parent, pool)) break;
            }
            // Iterate via index so the loop sees the post-fixup children list.
            var kids = parent.ChildList;
            for (int i = 0; i < kids.Count; i++) {
                Fixup(kids[i], pool);
            }
        }

        // For a single `parent`, walk its children. Group consecutive children
        // that need the SAME synthetic wrapper into one wrapper, then replace
        // the run with the wrapper. Non-table-internal children pass through
        // untouched. Returns true when at least one wrapper was inserted so
        // the caller can loop until stable.
        static bool FixupChildrenOf(Box parent, BoxPool pool) {
            if (parent == null) return false;
            var kids = parent.ChildList;
            if (kids.Count == 0) return false;

            // The kind of wrapper this `parent` IS — children whose role is
            // already satisfied by `parent` don't need a wrapper.
            ParentKind parentKind = ClassifyParent(parent);

            // Pre-scan: do any children need wrapping? If no, fast-exit.
            bool anyWrap = false;
            for (int s = 0; s < kids.Count; s++) {
                if (NeededWrapper(kids[s], parentKind) != WrapperKind.None) { anyWrap = true; break; }
            }
            if (!anyWrap) return false;

            // Snapshot the original children list, then DETACH them all from
            // `parent` so we can re-attach the rebuilt structure cleanly.
            // We must detach BEFORE building wrappers because the rebuild path
            // re-parents some children into wrappers via AddChild — if we left
            // them in `parent.children` and called parent.ClearChildren() at
            // the end, ClearChildren would walk parent.children (still holding
            // the same instances) and null their Parent pointers AFTER the
            // wrapper had set them correctly, leaving cells with Parent=null.
            var snapshot = new List<Box>(kids.Count);
            for (int s = 0; s < kids.Count; s++) snapshot.Add(kids[s]);
            parent.ClearChildren();

            int i = 0;
            while (i < snapshot.Count) {
                var child = snapshot[i];
                WrapperKind needed = NeededWrapper(child, parentKind);
                if (needed == WrapperKind.None) {
                    parent.AddChild(child);
                    i++;
                    continue;
                }
                // Greedily consume the run of consecutive children that need
                // the SAME wrapper kind. Per the spec each contiguous group
                // shares ONE wrapper.
                int runStart = i;
                int runEnd = i + 1;
                while (runEnd < snapshot.Count) {
                    if (NeededWrapper(snapshot[runEnd], parentKind) != needed) break;
                    runEnd++;
                }
                BlockBox wrapper = AllocateWrapper(needed, pool);
                for (int k = runStart; k < runEnd; k++) {
                    wrapper.AddChild(snapshot[k]);
                }
                parent.AddChild(wrapper);
                i = runEnd;
            }
            return true;
        }

        // Classify the parent's role — i.e. what table-internal child kinds
        // does it implicitly accept without needing a wrapper?
        enum ParentKind {
            // Anything that's not a table-internal: regular block/inline/flex/
            // grid/document root. Cells/rows/row-groups here need full wrapping.
            NonTable,
            Table,         // TableBox (display: table | inline-table)
            RowGroup,      // TableRowGroupBox (thead/tbody/tfoot)
            Row,           // TableRowBox
            ColumnGroup    // BlockBox with display: table-column-group
        }

        // Wrapper kinds the pass may synthesize. None = child needs no wrapper
        // for this parent.
        enum WrapperKind {
            None,
            Row,        // synthesize anonymous TableRowBox
            RowGroup,   // synthesize anonymous TableRowGroupBox (tbody)
            Table,      // synthesize anonymous TableBox
            Cell        // synthesize anonymous TableCellBox (I1b)
        }

        static ParentKind ClassifyParent(Box parent) {
            if (parent is TableBox) return ParentKind.Table;
            if (parent is TableRowGroupBox) return ParentKind.RowGroup;
            if (parent is TableRowBox) return ParentKind.Row;
            // table-column-group is a regular BlockBox in v1 (no dedicated
            // subclass). Detect via display, but only on non-table-typed
            // BlockBoxes — the TableBox/Row/RowGroup subclasses already
            // covered above.
            if (parent is BlockBox bb && !(bb is TableCellBox) && !(bb is TableCaptionBox)
                && bb.Style != null) {
                string disp = StyleResolver.Display(bb.Style);
                if (disp == "table-column-group") return ParentKind.ColumnGroup;
            }
            return ParentKind.NonTable;
        }

        // Decide which (if any) wrapper a given child needs to be wrapped in.
        // Returns the OUTERMOST missing wrapper level — outer passes will
        // recurse into the new wrapper to insert further levels if needed.
        // E.g. a bare table-cell with NonTable parent first gets wrapped in
        // a row; the row then needs a tbody (added on the next recursive
        // FixupChildrenOf call when the cell's new row is itself a child of
        // the bare NonTable parent and we re-evaluate).
        //
        // Actually for one-shot grouping we need to return the full wrapper
        // we'd build. To keep the runs cohesive we return the OUTER wrapper
        // (the one that goes directly inside `parent`); the recursion
        // afterwards inserts the inner wrappers since the synthetic wrapper
        // becomes the parent for its old children.
        static WrapperKind NeededWrapper(Box child, ParentKind parentKind) {
            // I1b — Row-parent non-cell children get wrapped in an anonymous
            // TableCellBox so CSS Tables L3 §3.7's "anonymous table-cell"
            // rule for stray content inside a row is satisfied. This branch
            // runs FIRST so it doesn't fall through to the type-driven
            // table-internal branches below (those would otherwise leave a
            // misparented TableRowBox / TableRowGroupBox child of a row
            // alone, when the spec wants it wrapped in a cell — far edge
            // case but spec-correct). Whitespace-only direct TextRun
            // children are skipped per the §3.7 whitespace exception.
            if (parentKind == ParentKind.Row) {
                if (child is TableCellBox) return WrapperKind.None;
                if (child is TextRun tr && IsWhitespaceOnly(tr.Text)) return WrapperKind.None;
                return WrapperKind.Cell;
            }
            // TableCellBox — needs at minimum a Row parent.
            if (child is TableCellBox) {
                // Cell directly inside a row: no wrapper needed.
                if (parentKind == ParentKind.Row) return WrapperKind.None;
                // Cell anywhere else: outer wrapper is a Row (recursive pass
                // will then ensure the row gets the tbody/table it needs).
                return WrapperKind.Row;
            }
            // TableRowBox — needs at minimum a RowGroup or Table parent.
            if (child is TableRowBox) {
                if (parentKind == ParentKind.RowGroup || parentKind == ParentKind.Table) return WrapperKind.None;
                // Row anywhere else: wrap in a RowGroup (which the recursive
                // pass will then wrap in a Table).
                return WrapperKind.RowGroup;
            }
            // TableRowGroupBox / TableCaptionBox — needs Table parent.
            if (child is TableRowGroupBox || child is TableCaptionBox) {
                if (parentKind == ParentKind.Table) return WrapperKind.None;
                return WrapperKind.Table;
            }
            // table-column / table-column-group — detected via display since
            // they have no dedicated subclass in v1.
            if (child is BlockBox bb && !(bb is TableBox)
                && !(bb is TableRowGroupBox) && !(bb is TableRowBox)
                && !(bb is TableCellBox) && !(bb is TableCaptionBox)
                && bb.Style != null) {
                string disp = StyleResolver.Display(bb.Style);
                if (disp == "table-column") {
                    // table-column lives inside either a Table or a ColumnGroup.
                    if (parentKind == ParentKind.Table || parentKind == ParentKind.ColumnGroup) return WrapperKind.None;
                    return WrapperKind.Table;
                }
                if (disp == "table-column-group") {
                    // table-column-group must live in a Table.
                    if (parentKind == ParentKind.Table) return WrapperKind.None;
                    return WrapperKind.Table;
                }
            }
            return WrapperKind.None;
        }

        static BlockBox AllocateWrapper(WrapperKind kind, BoxPool pool) {
            switch (kind) {
                case WrapperKind.Row: {
                    var b = pool.AllocateTableRowBox();
                    b.Element = null;
                    b.Style = null;
                    return b;
                }
                case WrapperKind.RowGroup: {
                    var b = pool.AllocateTableRowGroupBox();
                    b.Element = null;
                    b.Style = null;
                    b.GroupKind = "body";
                    return b;
                }
                case WrapperKind.Table: {
                    var b = pool.AllocateTableBox();
                    b.Element = null;
                    b.Style = null;
                    return b;
                }
                case WrapperKind.Cell: {
                    // I1b — anonymous cell for non-cell content sitting in a
                    // row. Element/Style left null per the anonymous-wrapper
                    // pattern; ColSpan/RowSpan default to 1.
                    var b = pool.AllocateTableCellBox();
                    b.Element = null;
                    b.Style = null;
                    return b;
                }
            }
            return null;
        }

        // §3.7 whitespace exception — a TextRun consisting only of ASCII
        // whitespace between table-internal boxes does NOT generate an
        // anonymous cell. Matches BoxFinalize.IsWhitespaceOnly so the two
        // passes agree on what "whitespace" means.
        static bool IsWhitespaceOnly(string s) {
            if (string.IsNullOrEmpty(s)) return true;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f') return false;
            }
            return true;
        }
    }
}
