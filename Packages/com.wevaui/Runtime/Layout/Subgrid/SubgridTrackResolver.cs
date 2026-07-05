using System.Collections.Generic;
using Weva.Css.Values;
using Weva.Layout.Grid;

namespace Weva.Layout.Subgrid {
    // SubgridTrackResolver — implements `grid-template-rows: subgrid` /
    // `grid-template-columns: subgrid` per CSS Grid Layout Module Level 2.
    //
    // Behaviour: when a child grid declares `subgrid` for a track axis, instead
    // of computing tracks locally it adopts the parent grid's resolved track
    // sizes for that axis, intersected with the child's `grid-row` /
    // `grid-column` span. The intersection ensures that the subgrid's local
    // line indices line up 1:1 with the parent grid's tracks within its area.
    //
    // Integration: GridLayout.Layout calls TryResolve at materialize time. If
    // the parent is itself a subgrid for the same axis, the chain walks up to
    // the nearest non-subgrid ancestor whose tracks have been resolved.
    //
    // v1 simplifications (per PLAN §4 and brief):
    //   - direct-child subgridding only (parent must be a GridBox and the
    //     child must be one of its grid items);
    //   - `grid-auto-rows/columns: subgrid` implemented (B9 closed);
    //   - no explicit added tracks alongside the `subgrid` keyword.
    public static class SubgridTrackResolver {
        public static bool IsSubgridKeyword(string raw) {
            return CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "subgrid");
        }

        // Slice a parent track template to the [start, end) indices the subgrid
        // child occupies. Returns a new GridTemplate over only those tracks. If
        // the slice runs past the parent track count the slice is clamped.
        public static GridTemplate SliceParentTracks(IReadOnlyList<GridTrackSize> parentTracks,
                                                     int sliceStart, int sliceEnd) {
            if (parentTracks == null || parentTracks.Count == 0) return GridTemplate.Empty;
            int n = parentTracks.Count;
            if (sliceStart < 0) sliceStart = 0;
            if (sliceEnd > n) sliceEnd = n;
            if (sliceStart >= sliceEnd) {
                return new GridTemplate(new GridTrackSize[0],
                                        new IReadOnlyList<string>[] { new string[0] });
            }
            int len = sliceEnd - sliceStart;
            var sliced = new GridTrackSize[len];
            for (int i = 0; i < len; i++) {
                sliced[i] = parentTracks[sliceStart + i];
            }
            var lineNames = new IReadOnlyList<string>[len + 1];
            for (int i = 0; i <= len; i++) lineNames[i] = new string[0];
            return new GridTemplate(sliced, lineNames);
        }

        // Build a subgrid's track template by slicing the parent tracks against
        // the child's placement on that axis. parentTrackCount is the parent's
        // explicit track count for the axis. Auto placements (start == 0)
        // default to spanning the parent's full extent.
        public static GridTemplate ResolveAxis(IReadOnlyList<GridTrackSize> parentTracks,
                                               int childAxisStart1Based,
                                               int childAxisEnd1Based) {
            int n = parentTracks?.Count ?? 0;
            int s = childAxisStart1Based - 1;
            int e = childAxisEnd1Based - 1;
            if (childAxisStart1Based <= 0) s = 0;
            if (childAxisEnd1Based <= 0) e = n;
            if (s < 0) s = 0;
            if (e > n) e = n;
            if (e < s) e = s;
            return SliceParentTracks(parentTracks, s, e);
        }

        // CSS Grid L2 §6 — build the auto-track pattern for `grid-auto-rows/
        // columns: subgrid`. Implicit tracks beyond the subgrid's explicit
        // template adopt sizing from the parent grid's track list, starting at
        // the parent track immediately after the explicitly-covered range and
        // cycling through the full parent track list.
        //
        // Parameters:
        //   parentTracks      — the parent grid's full track list for the axis.
        //   childExplicitEnd1 — 1-based index of the first parent line AFTER the
        //                       child's explicit (grid-template) span. This is
        //                       where the first implicit track maps to.
        //
        // Returns an array suitable for use as the `autoTracks` argument to
        // GridTrackSizing.Resolve (cycled modulo its length). Returns a single
        // `auto` track when parentTracks is null/empty (non-grid-parent
        // fallback per spec: `subgrid` degrades to `auto`).
        public static GridTrackSize[] BuildAutoTracksFromParent(
            IReadOnlyList<GridTrackSize> parentTracks,
            int childExplicitEnd1Based) {
            int n = parentTracks?.Count ?? 0;
            if (n == 0) return new[] { GridTrackSize.Auto };

            // Determine the 0-based starting index into the parent track list
            // for the first implicit track. Clamp to [0, n-1].
            int startIdx = childExplicitEnd1Based - 1;
            if (startIdx < 0) startIdx = 0;
            if (startIdx >= n) startIdx = 0; // wrap: cycle from beginning

            // Build a pattern that, when cycled, picks up from startIdx and
            // wraps around the full parent list. We produce the full parent
            // list rotated so index 0 = startIdx, length = n. The caller
            // cycles this modulo n, which is equivalent to cycling the parent
            // list from startIdx onward.
            var pattern = new GridTrackSize[n];
            for (int i = 0; i < n; i++) {
                pattern[i] = parentTracks[(startIdx + i) % n];
            }
            return pattern;
        }
    }
}
