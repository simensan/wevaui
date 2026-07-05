namespace Weva.Paint.Filters {
    public abstract class FilterFunction {
        public abstract FilterKind Kind { get; }
        public abstract string ToText();
        public override string ToString() => ToText();
    }
}
