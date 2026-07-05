using Weva.Layout.Boxes;

namespace Weva.Layout.Flex {
    public sealed class FlexBox : BlockBox {
        public bool IsInline { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            IsInline = false;
        }
    }
}
