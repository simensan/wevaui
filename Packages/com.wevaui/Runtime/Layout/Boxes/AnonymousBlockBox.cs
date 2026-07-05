namespace Weva.Layout.Boxes {
    public sealed class AnonymousBlockBox : BlockBox {
        public AnonymousBlockBox() {
            ContainsInlines = true;
        }

        internal override void ResetForPool() {
            base.ResetForPool();
            // Restore the construction-time invariant that anonymous wrappers always
            // contain inlines so a recycled instance behaves identically to a freshly
            // constructed one.
            ContainsInlines = true;
        }
    }
}
