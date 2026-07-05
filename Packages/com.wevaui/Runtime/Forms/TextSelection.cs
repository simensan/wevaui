namespace Weva.Forms {
    public enum SelectionDirection {
        None,
        Forward,
        Backward
    }

    public readonly struct TextSelection {
        public readonly int Start;
        public readonly int End;
        public readonly SelectionDirection Direction;

        public TextSelection(int start, int end, SelectionDirection direction) {
            Start = start;
            End = end;
            Direction = direction;
        }

        public static TextSelection Caret(int position) =>
            new TextSelection(position, position, SelectionDirection.None);

        public bool IsCollapsed => Start == End;
        public int Length => End - Start;

        public int Anchor => Direction == SelectionDirection.Backward ? End : Start;
        public int Focus => Direction == SelectionDirection.Backward ? Start : End;

        public override string ToString() {
            return IsCollapsed ? $"caret@{Start}" : $"[{Start},{End}] {Direction}";
        }
    }
}
