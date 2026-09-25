#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS 2.1 §17 tables — the separated-borders model, as the reference has it
// (Layout/Tables/TableLayout.cs + TableTrackResolver.cs).
//
// Ported     column placement with colspan / rowspan (§17.5.1), `table-layout:
//            fixed` from <col>/<colgroup> hints and first-row widths, automatic
//            layout from the reference's cell intrinsics, `border-spacing`
//            (collapse → 0), row groups in header → body → footer order,
//            captions top/bottom, `vertical-align: middle | bottom` on cells,
//            `visibility: collapse` on rows, row groups and columns, an
//            explicit row height as a floor.
//
// NOT ported anonymous table objects (a bare `display: table-cell` gets no
//            synthetic row/table), collapsed-border conflict resolution,
//            `baseline` cell alignment (treated as top), a table's
//            shrink-to-fit width (an auto-width table fills its container,
//            as in the reference), and content-derived intrinsic widths for
//            automatic layout — the reference takes a cell's laid-out width
//            at the table's content width as its max-content and its
//            `min-width` as its min-content, and so does this.

namespace weva {

class BlockLayout;

// Lays out the table's rows, groups, cells and captions inside the table's
// content box and returns the content height (border-spacing included, the
// table's own padding and border excluded). Cells are laid out through `block`
// and re-laid at their resolved column width.
double layout_table(BoxTree* tree, BoxId table, double content_width, const LayoutContext& ctx,
                    BlockLayout* block);

constexpr bool table_is_fully_ported() { return false; }

} // namespace weva
