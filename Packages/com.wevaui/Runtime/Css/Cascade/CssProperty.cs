namespace Weva.Css.Cascade {
    public sealed class CssProperty {
        public string Name { get; }
        public bool IsInherited { get; }
        public string InitialValue { get; }
        // Stable, process-global integer index assigned at registration. -1 for
        // synthesised custom-property entries (`--foo`) which spill to the
        // ComputedStyle.customProps side dictionary instead of the indexed
        // values array. Non-custom properties retain their Id for the lifetime
        // of the process — call sites cache the Id once at startup.
        public int Id { get; }

        public CssProperty(string name, bool isInherited, string initialValue) : this(name, isInherited, initialValue, -1) { }

        internal CssProperty(string name, bool isInherited, string initialValue, int id) {
            Name = name;
            IsInherited = isInherited;
            InitialValue = initialValue ?? "";
            Id = id;
        }
    }
}
