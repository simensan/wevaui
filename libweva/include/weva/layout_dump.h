#pragma once
// The layout dump the differential oracle compares (docs/ORACLE.md): every
// element's principal box, in box-tree order, with its depth and border box
// including transform translation, as the JSON Tools/BaselineGen/LayoutDump.cs
// writes. Shared by the weva_dump tool and the C ABI
// (weva_document_layout_dump), so a host's dump is the tool's dump by
// construction and any remaining difference is the host's fonts or setup.
#include "weva/box.h"

#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace weva {

struct ElementRect {
    int depth = 0;
    std::string tag, id, cls, path;
    double x = 0, y = 0, w = 0, h = 0;
};

// Walks the box tree from `id` (the document root), appending one rect per
// element: `html` and `body` are skipped as wrappers and do not consume a
// level, anonymous and line boxes are skipped but do, only an element's
// FIRST box is emitted, and translate() transforms move a box and what is
// under it.
void collect_layout_dump(const BoxTree& tree, BoxId id, double parent_x, double parent_y,
                         int depth, std::vector<ElementRect>* out,
                         std::set<const Element*>* seen);

// Matches C#'s Math.Round(v, 4, MidpointRounding.AwayFromZero) then "0.####".
std::string layout_dump_number(double v);

// The dump as JSON: source, width, height, count, elements[].
std::string layout_dump_json(std::string_view source, int width, int height,
                             const std::vector<ElementRect>& boxes);

}  // namespace weva
