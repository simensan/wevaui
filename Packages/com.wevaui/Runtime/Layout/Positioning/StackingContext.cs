using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Positioning {
    public sealed class StackingContext {
        public Box Root { get; internal set; }
        public int ZIndex { get; internal set; }
        internal int SortKey;

        public List<Box> NonPositionedDescendants { get; } = new();
        public List<Box> PositionedDescendantsZNegative { get; } = new();
        public List<Box> PositionedDescendantsZAuto { get; } = new();
        public List<Box> PositionedDescendantsZPositive { get; } = new();
        public List<StackingContext> ChildContexts { get; } = new();
    }
}
