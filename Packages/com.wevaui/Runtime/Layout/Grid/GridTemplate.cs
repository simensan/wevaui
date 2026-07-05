using System.Collections.Generic;

namespace Weva.Layout.Grid {
    public sealed class GridTemplate {
        public IReadOnlyList<GridTrackSize> Tracks { get; }

        // Line names for each line index. Lines are between/around tracks; for
        // N tracks there are N+1 line slots (index 0 is before track 0, index N
        // is after the last track). Each entry is the (possibly empty) list of
        // names declared at that position.
        public IReadOnlyList<IReadOnlyList<string>> LineNames { get; }

        public bool IsAutoFill { get; }
        public bool IsAutoFit { get; }
        public IReadOnlyList<GridTrackSize> AutoRepeatPattern { get; }
        public IReadOnlyList<IReadOnlyList<string>> AutoRepeatLineNames { get; }

        public GridTemplate(IReadOnlyList<GridTrackSize> tracks, IReadOnlyList<IReadOnlyList<string>> lineNames) {
            Tracks = tracks;
            LineNames = lineNames;
            IsAutoFill = false;
            IsAutoFit = false;
            AutoRepeatPattern = null;
            AutoRepeatLineNames = null;
        }

        public GridTemplate(IReadOnlyList<GridTrackSize> tracks,
                            IReadOnlyList<IReadOnlyList<string>> lineNames,
                            bool isAutoFill, bool isAutoFit,
                            IReadOnlyList<GridTrackSize> autoRepeatPattern,
                            IReadOnlyList<IReadOnlyList<string>> autoRepeatLineNames) {
            Tracks = tracks;
            LineNames = lineNames;
            IsAutoFill = isAutoFill;
            IsAutoFit = isAutoFit;
            AutoRepeatPattern = autoRepeatPattern;
            AutoRepeatLineNames = autoRepeatLineNames;
        }

        public static readonly GridTemplate Empty =
            new GridTemplate(new GridTrackSize[0], new[] { new string[0] });
    }
}
