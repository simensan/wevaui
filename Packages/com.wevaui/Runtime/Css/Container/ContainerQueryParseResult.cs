namespace Weva.Css.Container {
    public readonly struct ContainerQueryParseResult {
        public string Name { get; }
        public ContainerQueryList Condition { get; }

        public ContainerQueryParseResult(string name, ContainerQueryList condition) {
            Name = name;
            Condition = condition;
        }
    }
}
