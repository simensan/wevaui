namespace Weva.Css {
    public sealed class Declaration {
        public string Property;
        public string ValueText;
        public bool Important;
        // Lazily-cached CssProperties.GetId(Property) result. CssProperties.GetId
        // is a Dictionary<string,int> lookup; the cascade hits it once per
        // declaration per element per cascade pass (~150 declarations × 1500
        // elements per cold pass = 225K hashings per pass). Storing the id
        // here amortizes it across passes. Sentinel UNCOMPUTED marks "haven't
        // looked yet"; -1 is the legit return for custom (--*) and unknown
        // properties. Mutating Property invalidates the cache via the setter
        // path — but Property is a public field; callers that swap it must
        // reset cachedId themselves. In practice declarations are constructed
        // once at parse time and never reassigned.
        const int UNCOMPUTED = int.MinValue;
        int cachedId = UNCOMPUTED;

        public int PropertyId {
            get {
                if (cachedId != UNCOMPUTED) return cachedId;
                cachedId = Cascade.CssProperties.GetId(Property);
                return cachedId;
            }
        }

        // Force re-resolution on next read. Used by the small number of
        // callers (parser shorthand expansion, tests) that overwrite Property
        // after construction.
        public void InvalidatePropertyIdCache() {
            cachedId = UNCOMPUTED;
        }

        public Declaration() { }

        public Declaration(string property, string valueText, bool important) {
            Property = property;
            ValueText = valueText;
            Important = important;
        }
    }
}
