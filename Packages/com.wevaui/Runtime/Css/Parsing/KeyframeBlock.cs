using System.Collections.Generic;

namespace Weva.Css {
    public sealed class KeyframeBlock {
        public string Selector;
        public List<Declaration> Declarations = new();
    }
}
