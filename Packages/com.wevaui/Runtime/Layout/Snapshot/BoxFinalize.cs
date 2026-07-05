using System.Collections.Generic;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;

namespace Weva.Layout {
    // Shared post-build pass: takes a freshly assembled BlockBox whose Children list
    // is a mix of block-level and inline-level boxes and either
    //   - leaves it as-is (all-block or all-inline),
    //   - or wraps each run of consecutive inline children in an AnonymousBlockBox so
    //     the parent has a clean alternating block-level layout.
    //
    // Hoisted out of BoxBuilder so SnapshotBoxBuilder can call the same logic without
    // duplicating it. Both buffers come from LayoutScratch and are cleared on entry +
    // exit so a sibling invocation later in the same pass sees them empty.
    internal static class BoxFinalize {
        internal static void FinalizeBlockChildren(BlockBox parent, BoxPool pool, LayoutScratch scratch) {
            var kids = parent.Children;
            if (kids.Count == 0) {
                parent.ContainsInlines = false;
                return;
            }

            bool anyBlock = false;
            bool anyInline = false;
            for (int i = 0; i < kids.Count; i++) {
                var k = kids[i];
                if (IsBlockLevel(k)) anyBlock = true;
                else if (k is InlineBox || k is TextRun) anyInline = true;
                else if (k is BlockBox bb2 && bb2.IsInlineBlock) anyInline = true;
            }

            if (!anyBlock) {
                // CSS Flexbox §4 / Grid §6: text directly contained in a flex/grid
                // container is wrapped in an anonymous flex/grid item. Without this
                // wrap, FlexLayout / GridLayout (which collect only BlockBox
                // children) see zero items and FinalizeContainerCrossSize collapses
                // the container's cross axis to its padding/border frame.
                // Canonical regression: `<div class="obj" style="display:flex">
                // Recover the Sun-Sigil</div>` — h collapses from ~16 (line-height
                // of the text) to 2 (just padding). Element children are already
                // blockified by SnapshotBoxBuilder.AppendNodeAsBlockChild's
                // `blockifyInlines` path; this finalizer is the symmetric handle
                // for raw text children that bypass that branch.
                if ((parent is FlexBox || parent is GridBox) && anyInline) {
                    var allInlines = scratch.AnonymousFlushBuffer;
                    allInlines.Clear();
                    for (int i = 0; i < kids.Count; i++) allInlines.Add(kids[i]);
                    parent.ClearChildren();
                    FlushAnonymous(parent, allInlines, pool);
                    allInlines.Clear();
                    parent.ContainsInlines = false;
                    return;
                }
                parent.ContainsInlines = true;
                return;
            }
            if (!anyInline) {
                parent.ContainsInlines = false;
                return;
            }

            var existing = scratch.AnonymousFlushExisting;
            existing.Clear();
            for (int i = 0; i < kids.Count; i++) existing.Add(kids[i]);
            parent.ClearChildren();
            var currentInlines = scratch.AnonymousFlushBuffer;
            currentInlines.Clear();
            for (int i = 0; i < existing.Count; i++) {
                var k = existing[i];
                if (IsBlockLevel(k)) {
                    if (currentInlines.Count > 0) {
                        FlushAnonymous(parent, currentInlines, pool);
                        currentInlines.Clear();
                    }
                    parent.AddChild(k);
                } else {
                    currentInlines.Add(k);
                }
            }
            if (currentInlines.Count > 0) {
                FlushAnonymous(parent, currentInlines, pool);
                currentInlines.Clear();
            }
            existing.Clear();
            parent.ContainsInlines = false;
        }

        static bool IsBlockLevel(Box b) {
            if (!(b is BlockBox bb)) return false;
            if (b is AnonymousBlockBox) return false;
            if (bb.IsInlineBlock) return false;
            return true;
        }

        static void FlushAnonymous(BlockBox parent, List<Box> inlines, BoxPool pool) {
            if (AreAllWhitespaceTextRuns(inlines)) return;
            var anon = pool.AllocateAnonymousBlockBox();
            anon.Style = null;
            anon.Element = null;
            for (int i = 0; i < inlines.Count; i++) anon.AddChild(inlines[i]);
            parent.AddChild(anon);
        }

        static bool AreAllWhitespaceTextRuns(List<Box> boxes) {
            for (int i = 0; i < boxes.Count; i++) {
                var b = boxes[i];
                if (b is TextRun r) {
                    if (!IsWhitespaceOnly(r.Text)) return false;
                } else {
                    return false;
                }
            }
            return true;
        }

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
