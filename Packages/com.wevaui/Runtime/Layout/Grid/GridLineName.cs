namespace Weva.Layout.Grid {
    public readonly struct GridLineName {
        public string Name { get; }
        public int? Index { get; }

        public GridLineName(string name, int? index) {
            Name = name;
            Index = index;
        }

        public static GridLineName Of(string name) => new GridLineName(name, null);
    }
}
